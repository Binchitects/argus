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
        me.MapGet("/keys", KeysAsync);
        me.MapPost("/keys/rotate", async (ClaimsPrincipal p, UserManager<AppUser> users, PeopleService people) =>
            Results.Ok(await people.RotateKeyAsync((await users.GetUserAsync(p))!)));
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest body, ClaimsPrincipal p, UserManager<AppUser> users,
        SignInManager<AppUser> signIn, Audit audit)
    {
        var user = (await users.GetUserAsync(p))!;
        if (user.Source != UserSource.Local)
        {
            return AuthEndpoints.Problem(400, "ldap", "Your password is managed by the company directory. Change it there.");
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

    private static async Task<IResult> KeysAsync(ClaimsPrincipal p, UserManager<AppUser> users, ILiteLlm gateway)
    {
        var user = (await users.GetUserAsync(p))!;
        var keys = await gateway.KeysAsync(user.Email!);
        var all = await gateway.UsersAsync();
        all.TryGetValue(user.Email!, out var standing);
        return Results.Ok(new
        {
            keys = keys.Select(k => new { alias = k.Alias, preview = k.Preview, spend = k.Spend, blocked = k.Blocked, createdAt = k.CreatedAt }),
            spend = standing?.Spend ?? 0,
            budget = standing?.Budget,
        });
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
