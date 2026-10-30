namespace Llm.Core.Gateway;

/// <summary>
/// One request to a model, whatever became of it (done, failed, stopped, refused, answered from the cache): who asked, by
/// which way, which model and where it was answered, its tokens and cost, how long it took. The app's own ledger, in place of
/// LiteLLM's spend logs; pictures, videos, sound and music the app makes itself are booked here too.
/// </summary>
public sealed class GatewayRequest
{
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
    public const string Refused = "refused";
    public const string Cached = "cached";

    /// <summary>The same as the prompt log's line when the request is logged there.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>When its first word came: the time to first token.</summary>
    public DateTimeOffset? FirstTokenAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public int DurationMs { get; set; }
    /// <summary>chat (a conversation's turn), app (the app's side requests), api (people's keys), studio (a workspace's key), media (made by the app).</summary>
    public required string Surface { get; set; }
    /// <summary>chat, completion, embedding, image, image_edit, transcription, translation, speech, video, music, rerank.</summary>
    public required string Call { get; set; }
    /// <summary>openai, anthropic, responses, completions or internal.</summary>
    public string Protocol { get; set; } = "openai";
    /// <summary>The credit it counts against: chat, api, pictures, video or speech.</summary>
    public required string CreditKind { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>The person's email, lower case, as it was when they asked; null: no one; "erased" after their data was erased.</summary>
    public string? Person { get; set; }
    /// <summary>The API key's SHA-256 in hex; null for the app's own requests.</summary>
    public string? KeyHash { get; set; }
    public string? KeyAlias { get; set; }
    /// <summary>What part of the app asked (title, safeguards, deck, meetings…), for its side requests.</summary>
    public string? Tag { get; set; }
    public Guid? ConversationId { get; set; }
    /// <summary>The model's name as callers ask for it.</summary>
    public required string Model { get; set; }
    /// <summary>Where it was answered: llamacpp, vllm:flash@1, remote:gpu2, imagegen…</summary>
    public string? Deployment { get; set; }
    /// <summary>The model asked for, when its fallback answered.</summary>
    public string? FallbackFrom { get; set; }
    public short Attempts { get; set; } = 1;
    public int PromptTokens { get; set; }
    public int CachedTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    /// <summary>Prompt and completion tokens together (stored by the database).</summary>
    public int TotalTokens { get; private set; }
    /// <summary>The tokens were estimated (a stream stopped before its usage came).</summary>
    public bool UsageEstimated { get; set; }
    public decimal Cost { get; set; }
    /// <summary>The length of sound, video or music made or transcribed.</summary>
    public double? Seconds { get; set; }
    public int? Pictures { get; set; }
    public int? Characters { get; set; }
    /// <summary>done, failed, stopped, refused or cached.</summary>
    public required string Status { get; set; }
    public short HttpStatus { get; set; }
    /// <summary>Why it was refused: rpm, tpm, parallel, budget, credit, blocked, expired, disabled, api_off, model, safeguards, busy, too_long, room.</summary>
    public string? Refusal { get; set; }
    public string? Error { get; set; }
    /// <summary>LiteLLM's request id for a row brought over from its spend logs.</summary>
    public string? SourceId { get; set; }
}

/// <summary>
/// An API key, kept by the app (only its SHA-256, as LiteLLM kept it, so keys made before still work): a person's main key,
/// one of their own (named, with its own limits), or a Code Studio workspace's.
/// </summary>
public sealed class ApiKey
{
    public const string Main = "main";
    public const string Own = "own";
    public const string Studio = "studio";

    /// <summary>The key's SHA-256, in hex.</summary>
    public required string Hash { get; set; }
    public Guid UserId { get; set; }
    /// <summary>main, own or studio.</summary>
    public required string Kind { get; set; }
    /// <summary>app-{username}, app-{username}-own-xxxxxxxx, studio:{id}.</summary>
    public required string Alias { get; set; }
    /// <summary>An own key's name.</summary>
    public string? Name { get; set; }
    /// <summary>What may be shown of it: sk-…abcd.</summary>
    public required string Preview { get; set; }
    public Guid? WorkspaceId { get; set; }
    /// <summary>An own key's limits: requests and tokens a minute, at once.</summary>
    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }
    public int? Parallel { get; set; }
    /// <summary>An own key's budget over 30 days.</summary>
    public decimal? MonthlyBudget { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it was last used (written at most once a minute).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>What it has cost in all.</summary>
    public decimal Spend { get; set; }
}
