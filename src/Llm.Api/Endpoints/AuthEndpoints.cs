using Llm.Api.Access;
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
        var auth = app.MapGroup("/api/auth").RequireRateLimiting("sign-in").AllowAnonymous("Signing in and out: the password and the second factor are checked here.");
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/login/2fa", TwoFactorAsync);
        auth.MapPost("/logout", async (SignInService s) => { await s.SignOutAsync(); return Results.NoContent(); });
        app.MapGet("/api/auth/me", MeAsync).AllowAnonymous("Says who is signed in, or that nobody is.");
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

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, UserManager<AppUser> users, Access.AccessService access, Compliance.Notice notice)
    {
        var user = principal.Identity?.IsAuthenticated == true ? await users.GetUserAsync(principal) : null;
        if (user is null || user.IsDisabled)
        {
            return Results.Unauthorized();
        }
        var member = await access.MembershipAsync(user);
        return Results.Ok(new
        {
            id = user.Id,
            userName = user.UserName,
            displayName = user.DisplayName,
            email = user.Email,
            isAdmin = member.IsOwner,
            // The administrative permissions they hold (a role of theirs): which admin pages they see.
            permissions = Core.Access.Permissions.AdminKeys.Where(member.Has),
            source = UserSources.Name(user.Source),
            twoFactorEnabled = user.TwoFactorEnabled,
            // Signed in to set up two-factor sign-in, which the company requires, before anything else.
            mustSetUpTwoFactor = principal.HasClaim(c => c.Type == TwoFactorPolicy.EnrolClaim),
            // Signed in, and to accept the sign-in notice's current version before anything else.
            mustAcceptNotice = await notice.MustAcceptAsync(user.Id),
            signedInAt = AppClaimsFactory.SignedInAt(principal)?.ToUnixTimeSeconds(),
        });
    }

    public static IResult Problem(int status, string code, string message) =>
        Results.Json(new { status = code, error = message }, statusCode: status);
}
