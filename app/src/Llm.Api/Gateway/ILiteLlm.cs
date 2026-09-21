namespace Llm.Api.Gateway;

/// <summary>A person's standing at the gateway. Spend and budget are in the gateway's currency units.</summary>
public sealed record GatewayUser(string UserId, decimal Spend, decimal? Budget);

/// <summary>A key as the gateway lists it: the hashed token (never the key itself), alias, spend and state.</summary>
public sealed record GatewayKey(string Token, string Alias, string? Preview, decimal Spend, bool Blocked, DateTimeOffset? CreatedAt);

/// <summary>
/// LiteLLM's admin API. People are known to it by email, which is what ties
/// their spend (API keys and the chat path) to them.
/// </summary>
public interface ILiteLlm
{
    /// <summary>Creates the gateway user if missing; an existing one is left as it is.</summary>
    Task EnsureUserAsync(string email, CancellationToken ct = default);

    /// <summary>Sets the ceiling on both the user (API keys) and the end-user record (the shared chat key).</summary>
    Task SetBudgetAsync(string email, decimal? budget, CancellationToken ct = default);

    Task<string> GenerateKeyAsync(string email, string keyAlias, CancellationToken ct = default);
    Task<IReadOnlyList<GatewayKey>> KeysAsync(string email, CancellationToken ct = default);
    Task DeleteKeysAsync(IEnumerable<string> tokens, CancellationToken ct = default);
    Task SetBlockedAsync(IEnumerable<string> tokens, bool blocked, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, GatewayUser>> UsersAsync(CancellationToken ct = default);
    Task DeleteUserAsync(string email, CancellationToken ct = default);
}

public sealed class GatewayException(string message, int? status = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Status { get; } = status;
}
