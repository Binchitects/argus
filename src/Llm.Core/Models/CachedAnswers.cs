namespace Llm.Core.Models;

/// <summary>
/// An API answer kept by the answer cache (Gateway:AnswerCache): the same request again
/// with the same key, before it expires, gets this back without asking the model.
/// </summary>
public sealed class CachedAnswer
{
    public long Id { get; set; }
    /// <summary>SHA-256 (hex) of the key's hash and the request (without stream and user): the exact match.</summary>
    public required string Hash { get; set; }
    /// <summary>The key's hash as the gateway keeps it (SHA-256 of the key), so a person's answers go when they turn the cache off.</summary>
    public required string KeyHash { get; set; }
    public required string Model { get; set; }
    /// <summary>The answer as a chat.completion (JSON), whether it was asked for streamed or whole.</summary>
    public required string Response { get; set; }
    /// <summary>The tokens the answer took (prompt and completion): what each hit saves.</summary>
    public int Tokens { get; set; }
    public int Hits { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}
