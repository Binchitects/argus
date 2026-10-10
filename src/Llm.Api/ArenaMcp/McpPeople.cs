using System.Security.Cryptography;
using System.Text;
using Llm.Api.Gateway;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Llm.Api.ArenaMcp;

/// <summary>Who a request to Arena MCP comes from: the person whose API key it carries, or why nobody.</summary>
public sealed record McpCaller(AppUser? User, string? Refusal);

/// <summary>
/// Signs a request in by the person's own gateway API key (Your account → API key). The gateway
/// says whose a key is; the answer is kept for half a minute by the key's hash, so a busy agent
/// does not ask it on every call, and a key replaced, deleted or blocked there stops working
/// within that time. Whether the person is disabled is read on every request.
/// </summary>
public sealed class McpPeople(ILiteLlm gateway, IMemoryCache cache, UserManager<AppUser> users, TimeProvider clock)
{
    public static readonly TimeSpan Remember = TimeSpan.FromSeconds(30);

    public const string NoKey = "Send your API key as the header Authorization: Bearer sk-… (Your account → API key).";

    /// <summary>The person behind an Authorization header. Throws <see cref="GatewayException"/> when the gateway cannot say.</summary>
    public async Task<McpCaller> FromHeaderAsync(string? authorization, CancellationToken ct)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new McpCaller(null, NoKey);
        }
        var key = authorization[7..].Trim();
        // The gateway's keys only: anything else (a hash of one among them) is never passed on.
        if (!key.StartsWith("sk-", StringComparison.Ordinal))
        {
            return new McpCaller(null, "Arena MCP takes your API key (sk-…), from Your account → API key.");
        }
        var hash = "mcp-key:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (!cache.TryGetValue(hash, out string? email))
        {
            var owner = await gateway.KeyInfoAsync(key, ct);
            email = owner is { UserId: { Length: > 0 } e, Blocked: false } && (owner.Expires is null || owner.Expires > clock.GetUtcNow()) ? e : "";
            cache.Set(hash, email, Remember);
        }
        if (string.IsNullOrEmpty(email))
        {
            return new McpCaller(null, "That API key is not known here: it may have been replaced or blocked. Make a new one under Your account → API key.");
        }
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return new McpCaller(null, "That API key belongs to no one with an account here.");
        }
        return user.IsDisabled ? new McpCaller(null, "Your account is disabled. Ask an admin.")
            : user.ApiOff ? new McpCaller(null, "Your API access is off. Ask an admin to turn it on.")
            : new McpCaller(user, null);
    }
}
