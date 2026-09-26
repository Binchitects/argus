using System.Collections.Immutable;
using System.Security.Claims;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Llm.Api.Oidc;

public static class OidcEndpoints
{
    public static void MapOidc(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeAsync);
        app.MapPost("/connect/token", TokenAsync).RequireRateLimiting("token");
        app.MapMethods("/connect/userinfo", ["GET", "POST"], UserInfoAsync);
        app.MapMethods("/connect/logout", ["GET", "POST"], LogoutAsync);
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext ctx, UserManager<AppUser> users, PersonClaims claims,
        IOpenIddictApplicationManager apps, IOpenIddictAuthorizationManager authorizations, IOpenIddictScopeManager scopeManager,
        IOptions<AuthOptions> auth)
    {
        var request = ctx.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OIDC request.");
        var session = await ctx.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = session.Succeeded ? await users.GetUserAsync(session.Principal) : null;

        var tooOld = request.MaxAge is { } maxAge && session.Succeeded &&
            AppClaimsFactory.SignedInAt(session.Principal) is { } at && DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(maxAge);
        if (user is null || user.IsDisabled || request.HasPromptValue(PromptValues.Login) || tooOld)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "Sign-in is required.");
            }
            // Back to this same authorize request once signed in (without prompt=login, or it would loop).
            var parameters = ctx.Request.HasFormContentType ? ctx.Request.Form.ToList() : ctx.Request.Query.ToList();
            parameters.RemoveAll(p => p.Key == Parameters.Prompt);
            var back = ctx.Request.PathBase + ctx.Request.Path + QueryString.Create(parameters);
            if (request.HasPromptValue(PromptValues.Login) || tooOld)
            {
                await ctx.SignOutAsync(IdentityConstants.ApplicationScheme);
            }
            return Results.Redirect("/login?rd=" + Uri.EscapeDataString(back));
        }

        var app = await apps.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException("Unknown client.");
        var identity = await claims.IdentityAsync(user, request.GetScopes());
        identity.SetResources(await scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());

        // A permanent authorization per person and app, so refresh tokens can be revoked when they are disabled.
        var subject = user.Id.ToString();
        var appId = await apps.GetIdAsync(app);
        var existing = await authorizations.FindAsync(subject, appId, Statuses.Valid, AuthorizationTypes.Permanent, identity.GetScopes()).FirstOrDefaultAsync();
        existing ??= await authorizations.CreateAsync(identity, subject, appId!, AuthorizationTypes.Permanent, identity.GetScopes());
        identity.SetAuthorizationId(await authorizations.GetIdAsync(existing));

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> TokenAsync(
        HttpContext ctx, UserManager<AppUser> users, PersonClaims claims, IOpenIddictApplicationManager apps, IOpenIddictScopeManager scopeManager)
    {
        var request = ctx.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OIDC request.");

        if (request.IsClientCredentialsGrantType())
        {
            var app = await apps.FindByClientIdAsync(request.ClientId!) ?? throw new InvalidOperationException("Unknown client.");
            var id = new ClaimsIdentity("oidc", Claims.Name, Claims.Role);
            id.SetClaim(Claims.Subject, request.ClientId).SetClaim(Claims.Name, await apps.GetDisplayNameAsync(app));
            id.SetScopes(request.GetScopes());
            id.SetResources(await scopeManager.ListResourcesAsync(id.GetScopes()).ToListAsync());
            id.SetDestinations(_ => [Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(id), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var user = result.Principal?.GetClaim(Claims.Subject) is { } sub ? await users.FindByIdAsync(sub) : null;
            if (user is null || user.IsDisabled)
            {
                return Forbid(Errors.InvalidGrant, "The person no longer has access.");
            }
            // Claims are rebuilt, not copied, so a role change reaches the app at its next refresh.
            var identity = await claims.IdentityAsync(user, result.Principal!.GetScopes());
            identity.SetResources(result.Principal!.GetResources());
            identity.SetAuthorizationId(result.Principal!.GetAuthorizationId());
            return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Forbid(Errors.UnsupportedGrantType, "That grant type is not supported.");
    }

    private static async Task<IResult> UserInfoAsync(HttpContext ctx, UserManager<AppUser> users, PersonClaims claims)
    {
        var result = await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var user = result.Principal?.GetClaim(Claims.Subject) is { } sub ? await users.FindByIdAsync(sub) : null;
        if (user is null || user.IsDisabled)
        {
            return Results.Challenge(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }
        var scopes = result.Principal!.GetScopes();
        var info = new Dictionary<string, object> { [Claims.Subject] = user.Id.ToString() };
        if (scopes.Contains(Scopes.Profile))
        {
            info[Claims.Name] = user.DisplayName;
            info[Claims.PreferredUsername] = user.UserName!;
        }
        if (scopes.Contains(Scopes.Email))
        {
            info[Claims.Email] = user.Email!;
            info[Claims.EmailVerified] = true;
        }
        if (scopes.Contains(OidcScopes.Groups))
        {
            info[OidcScopes.Groups] = await claims.GroupsAsync(user);
        }
        return Results.Ok(info);
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx, SignInService signIn)
    {
        await signIn.SignOutAsync();
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static IResult Forbid(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
