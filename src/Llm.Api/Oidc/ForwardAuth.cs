using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Llm.Api.Oidc;

public enum HostPolicy
{
    Deny,
    /// <summary>Any signed-in person.</summary>
    Person,
    /// <summary>Signed-in admins only.</summary>
    Admin,
    /// <summary>A machine token with the "api" scope, or a signed-in person (a browser on /docs).</summary>
    ApiOrPerson,
}

/// <summary>
/// Traefik's forwardAuth target: decides for services that have no sign-in of
/// their own, and tells the admin panel who is calling (Remote-* headers,
/// which Traefik overwrites so a browser cannot forge them).
/// </summary>
public static class ForwardAuth
{
    public static HostPolicy PolicyFor(string host, string domain)
    {
        if (!host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
        {
            return HostPolicy.Deny;
        }
        return host[..^(domain.Length + 1)].ToLowerInvariant() switch
        {
            "metrics" or "alerts" or "logs" or "cadvisor" or "node" or "gpu" or "s3" or "api2" => HostPolicy.Admin,
            "admin" => HostPolicy.Person,
            "api" => HostPolicy.ApiOrPerson,
            _ => HostPolicy.Deny,
        };
    }

    public static void MapForwardAuth(this IEndpointRouteBuilder app) =>
        app.MapMethods("/api/authz/forward-auth", ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], CheckAsync);

    private static async Task<IResult> CheckAsync(HttpContext ctx, UserManager<AppUser> users, PersonClaims claims, IOptions<AuthOptions> auth)
    {
        var h = ctx.Request.Headers;
        // UseForwardedHeaders has already moved Traefik's X-Forwarded-Host/Proto into the request.
        var host = ctx.Request.Host.Host;
        var policy = PolicyFor(host, auth.Value.Domain);
        if (policy == HostPolicy.Deny)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (h.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = await ctx.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            if (!token.Succeeded)
            {
                return Results.Unauthorized();
            }
            var isMachine = token.Principal.HasScope(OidcScopes.Api);
            if (policy != HostPolicy.ApiOrPerson || !isMachine)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            ctx.Response.Headers["Remote-User"] = token.Principal.GetClaim(OpenIddictConstants.Claims.Subject);
            ctx.Response.Headers["Remote-Groups"] = "";
            return Results.Ok();
        }

        var session = await ctx.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = session.Succeeded ? await users.GetUserAsync(session.Principal) : null;
        if (user is null || user.IsDisabled)
        {
            var method = h["X-Forwarded-Method"].ToString();
            var browser = (method is "" or "GET" or "HEAD") && h.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
            if (!browser)
            {
                return Results.Unauthorized();
            }
            var original = $"{ctx.Request.Scheme}://{ctx.Request.Host}{h["X-Forwarded-Uri"]}";
            return Results.Redirect($"{auth.Value.Origin}/login?rd={Uri.EscapeDataString(original)}");
        }
        var groups = await claims.GroupsAsync(user);
        if (policy == HostPolicy.Admin && !await users.IsInRoleAsync(user, Roles.Admin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        ctx.Response.Headers["Remote-User"] = user.UserName;
        ctx.Response.Headers["Remote-Groups"] = string.Join(',', groups);
        ctx.Response.Headers["Remote-Email"] = user.Email;
        ctx.Response.Headers["Remote-Name"] = user.DisplayName;
        return Results.Ok();
    }
}
