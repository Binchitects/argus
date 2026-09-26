using System.Globalization;
using System.Security.Claims;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>Adds the person's display name. The sign-in time is stamped by AppSignInManager, only at a real sign-in.</summary>
public sealed class AppClaimsFactory(UserManager<AppUser> users, RoleManager<AppRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(users, roles, options)
{
    public const string AuthTime = "auth_time";
    public const string DisplayName = "name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim(DisplayName, user.DisplayName));
        return id;
    }

    public static DateTimeOffset? SignedInAt(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(AuthTime), CultureInfo.InvariantCulture, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
}
