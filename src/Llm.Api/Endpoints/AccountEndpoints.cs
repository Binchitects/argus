using Llm.Core.Access;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Endpoints;

/// <summary>What a signed-in person can do for themselves.</summary>
public static class AccountEndpoints
{
    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/account").RequireAuthorization();
        me.MapPost("/password", ChangePasswordAsync);
        me.MapGet("/2fa", async (ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var u = await users.GetUserAsync(p);
            return Results.Ok(new { enabled = u!.TwoFactorEnabled, recoveryCodesLeft = await users.CountRecoveryCodesAsync(u) });
        });
        me.MapPost("/2fa/setup", SetupTwoFactorAsync);
        me.MapPost("/2fa/enable", EnableTwoFactorAsync);
        me.MapPost("/2fa/disable", DisableTwoFactorAsync);
        me.MapGet("/preferences", async (ClaimsPrincipal p, UserManager<AppUser> users) =>
            Results.Ok(new { answerLength = (await users.GetUserAsync(p))!.AnswerLength ?? Chat.AnswerLengths.Normal }));
        me.MapPut("/preferences", async (Preferences body, ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            if (body.AnswerLength is { } length && !Chat.AnswerLengths.All.Contains(length))
            {
                return AuthEndpoints.Problem(400, "answer_length", "The answer length is short, normal or thorough.");
            }
            var user = (await users.GetUserAsync(p))!;
            if (body.AnswerLength is not null)
            {
                user.AnswerLength = body.AnswerLength == Chat.AnswerLengths.Normal ? null : body.AnswerLength;
                await users.UpdateAsync(user);
            }
            return Results.Ok(new { answerLength = user.AnswerLength ?? Chat.AnswerLengths.Normal });
        });
        me.MapGet("/keys", KeysAsync);
        me.MapGet("/keys/limits", LimitsAsync);
        me.MapPost("/keys/rotate", RotateAsync);
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest body, ClaimsPrincipal p, UserManager<AppUser> users,
        SignInManager<AppUser> signIn, Audit audit)
    {
        var user = (await users.GetUserAsync(p))!;
        if (user.Source != UserSource.Local)
        {
            return AuthEndpoints.Problem(400, UserSources.Name(user.Source), user.Source == UserSource.Ldap
                ? "Your password is managed by the company directory. Change it there."
                : "You sign in with your company account: its password is changed there.");
        }
        var result = await users.ChangePasswordAsync(user, body.Current ?? "", body.Next ?? "");
        if (!result.Succeeded)
        {
            var wrong = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch));
            await audit.WriteAsync("account.change_password", user.UserName, success: false, detail: wrong ? "wrong current password" : "rejected");
            return AuthEndpoints.Problem(400, wrong ? "incorrect" : "weak",
                wrong ? "The current password is incorrect." : string.Join(" ", result.Errors.Select(e => e.Description)));
        }
        // The new security stamp ends every other session; keep this one.
        await signIn.RefreshSignInAsync(user);
        await audit.WriteAsync("account.change_password", user.UserName);
        return Results.NoContent();
    }

    private static async Task<IResult> SetupTwoFactorAsync(ClaimsPrincipal p, UserManager<AppUser> users, IUserStore<AppUser> store,
        Microsoft.Extensions.Options.IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var user = (await users.GetUserAsync(p))!;
        if (user.Source == UserSource.Oidc)
        {
            return AuthEndpoints.Problem(400, "oidc", "You sign in with your company account: its two-factor sign-in is set up there.");
        }
        if (user.TwoFactorEnabled)
        {
            return AuthEndpoints.Problem(409, "enabled", "Two-factor sign-in is already on. Turn it off first to set up a new device.");
        }
        // A key for the device being set up. It protects nothing until two-factor is
        // turned on, so it leaves the security stamp alone: opening setup and
        // cancelling must not sign the person out elsewhere. Turning it on rotates
        // the stamp (SetTwoFactorEnabledAsync), and other sessions end then.
        // (ResetAuthenticatorKeyAsync would rotate it now.)
        await ((IUserAuthenticatorKeyStore<AppUser>)store).SetAuthenticatorKeyAsync(user, users.GenerateNewAuthenticatorKey(), ct);
        await users.UpdateAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        var issuer = Uri.EscapeDataString(auth.Value.Domain);
        var uri = string.Create(CultureInfo.InvariantCulture,
            $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.UserName!)}?secret={key}&issuer={issuer}&digits=6");
        return Results.Ok(new { sharedKey = Group(key), uri });
    }

    private static async Task<IResult> EnableTwoFactorAsync(CodeRequest body, ClaimsPrincipal p, UserManager<AppUser> users, SignInManager<AppUser> signIn, Audit audit)
    {
        var user = (await users.GetUserAsync(p))!;
        var code = (body.Code ?? "").Replace(" ", "", StringComparison.Ordinal);
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            return AuthEndpoints.Problem(400, "invalid", "That code is not right. Check the time on your phone and try the next code.");
        }
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await signIn.RefreshSignInAsync(user);
        await audit.WriteAsync("account.enable_2fa", user.UserName);
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> DisableTwoFactorAsync(CodeRequest body, ClaimsPrincipal p, UserManager<AppUser> users, SignInManager<AppUser> signIn, Audit audit)
    {
        var user = (await users.GetUserAsync(p))!;
        var code = (body.Code ?? "").Replace(" ", "", StringComparison.Ordinal);
        // Proof of the device (or a recovery code), so a borrowed session cannot switch 2FA off.
        var ok = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code) ||
                 (await users.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded;
        if (!ok)
        {
            return AuthEndpoints.Problem(400, "invalid", "That code is not right.");
        }
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await signIn.RefreshSignInAsync(user);
        await audit.WriteAsync("account.disable_2fa", user.UserName);
        return Results.NoContent();
    }

    /// <summary>
    /// A new key for the person, the old one revoked. A few an hour: each new key starts its rate
    /// limits' minute at zero at the gateway, so making keys must not be a way round them.
    /// </summary>
    private static async Task<IResult> RotateAsync(ClaimsPrincipal p, UserManager<AppUser> users, PeopleService people, Audit audit, TimeProvider clock, HttpContext http)
    {
        var user = (await users.GetUserAsync(p))!;
        if (user.ApiOff)
        {
            return AuthEndpoints.Problem(403, "api_off", "Your API access is off: ask an admin to turn it on.");
        }
        if (await people.OwnKeyWaitAsync(user, clock.GetUtcNow()) is { Ticks: > 0 } wait)
        {
            var minutes = (int)Math.Ceiling(wait.TotalMinutes);
            await audit.WriteAsync("person.rotate_key", user.UserName, success: false, detail: $"refused: {PeopleService.OwnKeysPerHour} new keys in the last hour");
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return AuthEndpoints.Problem(429, "too_many_keys",
                $"You made {PeopleService.OwnKeysPerHour} new keys in the last hour, the most there may be. Make the next in {minutes} minute{(minutes == 1 ? "" : "s")}, or ask an admin, who can make one for you now.");
        }
        return Results.Ok(await people.RotateKeyAsync(user));
    }

    /// <summary>
    /// The person's keys (none while their API access is off), what they spent this month, and where they stand on each kind
    /// of credit (the home page shows these too).
    /// </summary>
    private static async Task<IResult> KeysAsync(ClaimsPrincipal p, UserManager<AppUser> users, ILiteLlm gateway, Ledger ledger, Credit credit)
    {
        var user = (await users.GetUserAsync(p))!;
        var keys = user.ApiOff ? [] : await gateway.KeysAsync(user.Email!);
        var spending = await ledger.ReadAsync();
        var standing = await credit.StandingAsync(user);
        var api = standing?.FirstOrDefault(s => s.Kind == CreditKind.Api);
        return Results.Ok(new
        {
            apiOff = user.ApiOff,
            keys = keys.Select(k => new { alias = k.Alias, preview = k.Preview, spend = k.Spend, blocked = k.Blocked, createdAt = k.CreatedAt }),
            // This month, every kind together; the API keys' own credit and spend.
            spend = spending.Of(user.Email),
            apiSpend = api?.Spent ?? 0,
            apiCredit = api?.Credit,
            standing = standing?.Select(AdminEndpoints.Standing),
        });
    }

    /// <summary>
    /// The person's keys' rate limits, with what the keys used in the last minute and what was
    /// refused in the last day: only the key's card asks, as it reads the gateway's request log.
    /// </summary>
    private static async Task<IResult> LimitsAsync(ClaimsPrincipal p, UserManager<AppUser> users, ILiteLlm gateway, RateLimits rateLimits, Models.KeyAccess keyAccess,
        CancellationToken ct)
    {
        var user = (await users.GetUserAsync(p))!;
        return Results.Ok(await rateLimits.ViewAsync(user, await gateway.KeysAsync(user.Email!, ct), keyAccess.MaxParallel, ct));
    }

    private static string Group(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }
        return sb.ToString().TrimEnd().ToLowerInvariant();
    }
}

/// <summary>A person's own choices for their answers. Only what is sent changes.</summary>
public sealed record Preferences(string? AnswerLength = null);
