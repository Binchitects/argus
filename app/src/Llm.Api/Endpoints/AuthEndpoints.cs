using System.Security.Claims;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting("sign-in");
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/login/2fa", TwoFactorAsync);
        auth.MapPost("/logout", async (SignInService s) => { await s.SignOutAsync(); return Results.NoContent(); });
        app.MapGet("/api/auth/me", MeAsync);
    }

    private static async Task<IResult> LoginAsync(LoginRequest body, SignInService signIn, IOptions<AuthOptions> auth) =>
        Answer(await signIn.PasswordAsync(body.UserName ?? "", body.Password ?? "", body.Remember), body.Redirect, auth.Value);

    private static async Task<IResult> TwoFactorAsync(TwoFactorRequest body, SignInService signIn, IOptions<AuthOptions> auth) =>
        Answer(await signIn.TwoFactorAsync(body.Code ?? "", body.Recovery, body.Remember), body.Redirect, auth.Value);

    private static IResult Answer(SignInOutcome outcome, string? redirect, AuthOptions auth) => outcome switch
    {
        SignInOutcome.Success => Results.Ok(new { status = "ok", redirect = Redirects.Safe(redirect, auth.Domain) }),
        SignInOutcome.TwoFactorRequired => Results.Ok(new { status = "2fa" }),
        SignInOutcome.Invalid => Problem(401, "invalid", "Wrong username or password."),
        SignInOutcome.LockedOut => Problem(423, "locked", "Too many wrong attempts. Try again in 15 minutes, or ask an admin to reset your password."),
        SignInOutcome.Disabled => Problem(403, "disabled", "This account is disabled. Ask an admin."),
        SignInOutcome.NotAllowed => Problem(403, "not_allowed", "Your directory account is not allowed to sign in here. Ask an admin."),
        SignInOutcome.Banned => Problem(429, "banned", "Too many failed sign-ins from your address. Try again later."),
        SignInOutcome.Unavailable => Problem(503, "unavailable", "The company directory cannot be reached right now. Try again shortly."),
        _ => Problem(500, "error", "Sign-in failed."),
    };

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = principal.Identity?.IsAuthenticated == true ? await users.GetUserAsync(principal) : null;
        if (user is null || user.IsDisabled)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(new
        {
            id = user.Id,
            userName = user.UserName,
            displayName = user.DisplayName,
            email = user.Email,
            isAdmin = await users.IsInRoleAsync(user, Roles.Admin),
            source = user.Source == UserSource.Ldap ? "ldap" : "local",
            twoFactorEnabled = user.TwoFactorEnabled,
            signedInAt = AppClaimsFactory.SignedInAt(principal)?.ToUnixTimeSeconds(),
        });
    }

    public static IResult Problem(int status, string code, string message) =>
        Results.Json(new { status = code, error = message }, statusCode: status);
}
