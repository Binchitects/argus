namespace Llm.Core.Panels;

/// <summary>
/// A panel: several assistants (or roles) discuss a person's question in turns, each from their side (the expert, the
/// critic, the one who says no), and a moderator writes the verdict with who said what; the whole goes into the chat or room.
/// </summary>
public sealed class Panel
{
    public const string Working = "working";
    public const string Done = "done";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>The chat or room it was asked in, where it goes when done.</summary>
    public Guid ConversationId { get; set; }
    public required string Question { get; set; }
    /// <summary>Who sits on it, JSON: <c>[{ name, role, model, assistantId }]</c>.</summary>
    public required string Panelists { get; set; }
    public int Rounds { get; set; } = 2;
    /// <summary>What was said, JSON: <c>[{ round, speaker, text }]</c>.</summary>
    public string Turns { get; set; } = "[]";
    public string? Verdict { get; set; }
    public string Status { get; set; } = Working;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
