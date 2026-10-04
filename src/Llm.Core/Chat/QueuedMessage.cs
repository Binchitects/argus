namespace Llm.Core.Chat;

/// <summary>
/// A message the person sent while the chat was answering: it waits on the server
/// (a reload or another tab still shows it) and becomes the chat's next question
/// when the answer before it ends, the first in line first.
/// </summary>
public sealed class QueuedMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    public required string Content { get; set; }
    /// <summary>The attachments' ids, as a JSON array (as ChatMessage keeps them).</summary>
    public string? AttachmentsJson { get; set; }
    /// <summary>Deep research for this one.</summary>
    public bool Research { get; set; }
    /// <summary>Its place in line: the lowest goes next (Send now puts one first).</summary>
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
