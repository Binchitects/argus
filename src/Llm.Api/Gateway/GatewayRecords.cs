namespace Llm.Api.Gateway;

/// <summary>
/// A model the gateway serves, with what it can do (<see cref="GatewayRoutes"/>). Unknown capabilities are taken as: tools
/// and thinking yes (the engine's template decides), vision no (a model needs a projector to see, and says so when it has
/// one). Prices are per million tokens. Mode is "chat", or "image_generation" for a picture model, and so on.
/// </summary>
public sealed record GatewayModel(string Name, int? Context, int? MaxOutput, bool Vision, bool Tools, bool Thinking, decimal? InputPerMtok, decimal? CachedInputPerMtok, decimal? OutputPerMtok, string Mode = "chat", bool Audio = false);

/// <summary>A key as its pages show it (<see cref="ApiKeys.KeysAsync(Llm.Core.Identity.AppUser, CancellationToken)"/>): the hashed token (never the key itself), alias, spend and state.</summary>
/// <remarks>Models: the models the key may call; empty means every model. Rate: its requests and tokens a minute.</remarks>
public sealed record GatewayKey(string Token, string Alias, string? Preview, decimal Spend, bool Blocked, DateTimeOffset? CreatedAt, IReadOnlyList<string>? Models = null, int? MaxParallel = null,
    string? TeamId = null, KeyRate? Rate = null, decimal? MaxBudget = null);

