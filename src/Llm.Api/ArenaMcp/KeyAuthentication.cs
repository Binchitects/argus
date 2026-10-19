using System.Text.Encodings.Web;
using Llm.Api.Gateway;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.ArenaMcp;

/// <summary>
/// The person signed in by their own API key (Authorization: Bearer sk-…), as Arena MCP and /api/code-arena sign them
/// in (<see cref="McpPeople"/>), on the few routes of the chat that Code Arena's IDE relays for its page: their files
/// (uploaded, read, their pages). Only the routes mapped with <see cref="Policy"/> take it; every other route of the app
/// takes the sign-in cookie alone. The key reaches nothing new so: it reads every chat of the person's already, through
/// /api/code-arena.
/// </summary>
public sealed class KeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, McpPeople people, IUserClaimsPrincipalFactory<AppUser> principals)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Name = "ApiKey";

    /// <summary>The sign-in cookie, or the person's key.</summary>
    public const string Policy = "PersonOrKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        McpCaller caller;
        try
        {
            caller = await people.FromHeaderAsync(header, Context.RequestAborted);
        }
        catch (GatewayException)
        {
            return AuthenticateResult.Fail("The gateway cannot say whose that key is now: try again in a moment.");
        }
        if (caller.User is not { } user)
        {
            return AuthenticateResult.Fail(caller.Refusal ?? McpPeople.NoKey);
        }
        return AuthenticateResult.Success(new AuthenticationTicket(await principals.CreateAsync(user), Name));
    }
}
