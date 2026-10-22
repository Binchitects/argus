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
    /// <summary>The part of an earlier answer it quotes, if any (as ChatMessage keeps it).</summary>
    public string? Quote { get; set; }
    /// <summary>The skill it uses, as ChatMessage keeps it (its parameters filled in when it was queued).</summary>
    public string? Skill { get; set; }
    /// <summary>The attachments' ids, as a JSON array (as ChatMessage keeps them).</summary>
    public string? AttachmentsJson { get; set; }
    /// <summary>Deep research for this one.</summary>
    public bool Research { get; set; }
    /// <summary>Who sent it: the answer starts as theirs (in a room, not the owner's).</summary>
    public Guid UserId { get; set; }
    /// <summary>A room's message for the people only: saved, not answered.</summary>
    public bool ForPeople { get; set; }
    public List<Guid> Mentions { get; set; } = [];
    /// <summary>The message it replies to (a room's).</summary>
    public Guid? ReplyToId { get; set; }
    /// <summary>The model a safeguard rule sent it to instead of the chat's; null: the chat's.</summary>
    public string? Model { get; set; }
    /// <summary>Its place in line: the lowest goes next (Send now puts one first).</summary>
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
