using Llm.Api.Gateway;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.ArenaMcp;

/// <summary>Who a request to Arena MCP comes from: the person whose API key it carries, or why nobody.</summary>
public sealed record McpCaller(AppUser? User, string? Refusal);

/// <summary>
/// Signs a request in by the person's own API key (Your account → API key): whose it is, as the app keeps its keys
/// (<see cref="KeyBook"/>: believed for ten seconds, forgotten at once when a key is revoked). Whether the person is
/// disabled, or has no API access, is read on every request.
/// </summary>
public sealed class McpPeople(KeyBook book, UserManager<AppUser> users, TimeProvider clock, Access.AccessService access)
{
    public const string NoKey = "Send your API key as the header Authorization: Bearer sk-… (Your account → API key).";

    /// <summary>The person behind an Authorization header.</summary>
    public async Task<McpCaller> FromHeaderAsync(string? authorization, CancellationToken ct)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new McpCaller(null, NoKey);
        }
        var key = authorization[7..].Trim();
        // The people's keys only: anything else (a hash of one among them) is never looked up.
        if (!key.StartsWith("sk-", StringComparison.Ordinal))
        {
            return new McpCaller(null, "Arena MCP takes your API key (sk-…), from Your account → API key.");
        }
        var holder = await book.ForAsync(key, ct);
        if (holder is null || holder.ExpiredAt(clock.GetUtcNow()))
        {
            return new McpCaller(null, "That API key is not known here: it may have been replaced or blocked. Make a new one under Your account → API key.");
        }
        var user = await users.FindByIdAsync(holder.UserId.ToString());
        if (user is null)
        {
            return new McpCaller(null, "That API key belongs to no one with an account here.");
        }
        return user.IsDisabled ? new McpCaller(null, "Your account is disabled. Ask an admin.")
            : await access.ApiOffAsync(user, ct) ? new McpCaller(null, "Your API access is off. Ask an admin to turn it on.")
            : new McpCaller(user, null);
    }
}
