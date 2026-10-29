namespace Llm.Core.Code;

/// <summary>
/// A Code Arena version a person used against this Arena (its User-Agent, code-arena/X.Y.Z), when first and last seen:
/// Admin → Overview lists the people on one older than the Arena serves, and each is told in their bell.
/// </summary>
public sealed class CodeArenaClient
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Version { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
