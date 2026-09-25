namespace Llm.Api.Gateway;

/// <summary>A person's standing at the gateway. Spend and budget are in the gateway's currency units.</summary>
public sealed record GatewayUser(string UserId, decimal Spend, decimal? Budget);

/// <summary>
/// A model the gateway serves, with what it can do. Unknown capabilities (LiteLLM's
/// null) are taken as: tools and thinking yes (the engine's template decides),
/// vision no (a model needs a projector to see, and says so when it has one).
/// Prices are per million tokens.
/// </summary>
/// <summary>A model the gateway serves. Mode is "chat", or "image_generation" for a picture model.</summary>
public sealed record GatewayModel(string Name, int? Context, int? MaxOutput, bool Vision, bool Tools, bool Thinking, decimal? InputPerMtok, decimal? CachedInputPerMtok, decimal? OutputPerMtok, string Mode = "chat");

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

    /// <summary>A key that belongs to no person (the chat's): spend is attributed by the request's `user`.</summary>
    Task<string> GenerateServiceKeyAsync(string keyAlias, CancellationToken ct = default);
    Task<IReadOnlyList<GatewayKey>> KeysAsync(string email, CancellationToken ct = default);
    Task DeleteKeysAsync(IEnumerable<string> tokens, CancellationToken ct = default);
    Task SetBlockedAsync(IEnumerable<string> tokens, bool blocked, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, GatewayUser>> UsersAsync(CancellationToken ct = default);
    Task DeleteUserAsync(string email, CancellationToken ct = default);

    /// <summary>The models the gateway serves (/model/info).</summary>
    Task<IReadOnlyList<GatewayModel>> ModelsAsync(CancellationToken ct = default);
}

public sealed class GatewayException(string message, int? status = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Status { get; } = status;
}
