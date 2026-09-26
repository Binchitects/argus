using System.Globalization;
using System.Security.Claims;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>
/// Stamps the time of the real sign-in into the session, and carries it over
/// when the session is refreshed (password change, 2FA changes). Otherwise any
/// refresh would restart the 12-hour cap, and a stolen session could be kept
/// alive forever by repeating a refreshing action.
/// </summary>
public sealed class AppSignInManager(
    UserManager<AppUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<AppUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<AppUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<AppUser> confirmation)
    : SignInManager<AppUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    private string? _carriedAuthTime;

    public override async Task RefreshSignInAsync(AppUser user)
    {
        _carriedAuthTime = Context.User.FindFirstValue(AppClaimsFactory.AuthTime);
        try
        {
            await base.RefreshSignInAsync(user);
        }
        finally
        {
            _carriedAuthTime = null;
        }
    }

    public override Task SignInWithClaimsAsync(AppUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        var at = _carriedAuthTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims.Append(new Claim(AppClaimsFactory.AuthTime, at)));
    }
}
