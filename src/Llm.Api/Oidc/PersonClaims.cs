using System.Security.Claims;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Llm.Api.Oidc;

/// <summary>What other services learn about a person. One place, so every app sees the same identity.</summary>
public sealed class PersonClaims(UserManager<AppUser> users, IOptions<AuthOptions> auth)
{
    /// <summary>"users" for everyone, plus the admin group (Open WebUI maps it to its admin role).</summary>
    public async Task<string[]> GroupsAsync(AppUser user) =>
        await users.IsInRoleAsync(user, Roles.Admin) ? [auth.Value.AdminGroup, "users"] : ["users"];

    public async Task<ClaimsIdentity> IdentityAsync(AppUser user, IEnumerable<string> scopes)
    {
        var id = new ClaimsIdentity(
            authenticationType: "oidc",
            nameType: Claims.Name,
            roleType: Claims.Role);
        id.SetClaim(Claims.Subject, user.Id.ToString())
          .SetClaim(Claims.Name, user.DisplayName)
          .SetClaim(Claims.PreferredUsername, user.UserName)
          .SetClaim(Claims.Email, user.Email)
          .SetClaim(Claims.EmailVerified, true)
          .SetClaims(OidcScopes.Groups, [.. await GroupsAsync(user)]);
        id.SetScopes(scopes);
        id.SetDestinations(c => Destinations(c, id));
        return id;
    }

    private static string[] Destinations(Claim claim, ClaimsIdentity id)
    {
        var scopes = id.GetScopes();
        return claim.Type switch
        {
            Claims.Subject => [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
            Claims.Name or Claims.PreferredUsername when scopes.Contains(Scopes.Profile) => Both,
            Claims.Email or Claims.EmailVerified when scopes.Contains(Scopes.Email) => Both,
            OidcScopes.Groups when scopes.Contains(OidcScopes.Groups) => Both,
            _ => [OpenIddictConstants.Destinations.AccessToken],
        };
    }

    private static readonly string[] Both = [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken];
}
