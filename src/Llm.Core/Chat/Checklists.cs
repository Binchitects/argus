namespace Llm.Core.Chat;

/// <summary>
/// A checklist a person keeps (Workspace → Checklists, and Code Arena's /checklist): its items in order. Turned on in a chat
/// or a Code Arena session, the model goes through it and ticks each item as it does or checks it.
/// </summary>
public sealed class Checklist
{
    public const int MostItems = 50, MostItemChars = 500, MostPerPerson = 100;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<string> Items { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A checklist turned on in a chat (one at a time): its items as they were then, each ticked or not, with what was said of it.
/// The chat's answers (and its Code Arena session's) see it and tick it; the person can tick it too.
/// </summary>
public sealed class ChecklistRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    /// <summary>The checklist it was taken from (null once that is deleted: the run keeps its copy).</summary>
    public Guid? ChecklistId { get; set; }
    public required string Name { get; set; }
    public List<ChecklistRunItem> Items { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChecklistRunItem
{
    public required string Text { get; set; }
    public bool Done { get; set; }
    /// <summary>How it was done or checked (the model's words, or the person's), or why it does not apply.</summary>
    public string? Note { get; set; }
    /// <summary>model or person: who ticked it.</summary>
    public string? By { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
}
