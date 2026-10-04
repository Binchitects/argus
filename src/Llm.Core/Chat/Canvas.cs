namespace Llm.Core.Chat;

/// <summary>
/// A document (Markdown) or code beside a chat, edited by the person and by the model
/// (by changes, not rewrites). It holds the latest text; every change is a
/// <see cref="CanvasVersion"/>, and any version can be restored.
/// </summary>
public sealed class Canvas
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    public required string Title { get; set; }
    /// <summary>"document" (Markdown) or "code".</summary>
    public string Kind { get; set; } = "document";
    /// <summary>A code canvas's language ("python", "typescript"...); null for a document.</summary>
    public string? Language { get; set; }
    public string Content { get; set; } = "";
    /// <summary>The number of its latest version, from 1. A change saved over an older one is refused (a concurrency token).</summary>
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A canvas as one change left it: who made it (the person or the model), when, and a short summary.</summary>
public sealed class CanvasVersion
{
    public Guid CanvasId { get; set; }
    /// <summary>From 1, in the order the changes were made.</summary>
    public int Number { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    /// <summary>"person" or "model".</summary>
    public required string Author { get; set; }
    public required string Summary { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
