namespace Llm.Core.Chat;

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "New chat";
    /// <summary>Thinking level for this chat ("low", "xhigh", "off"...); null = the deployment's default.</summary>
    public string? Thinking { get; set; }
    public bool UseArgus { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; set; } = [];
}

public enum MessageStatus
{
    Complete = 0,
    /// <summary>The person pressed stop; what had arrived is kept.</summary>
    Stopped = 1,
    Failed = 2,
}

/// <summary>
/// One turn. Assistant turns that called tools keep the calls (ToolCallsJson);
/// each tool's answer is its own message with Role "tool", as the model sees them.
/// </summary>
public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    public int Sequence { get; set; }
    public required string Role { get; set; }
    public string Content { get; set; } = "";
    public string? Reasoning { get; set; }
    public string? ToolCallsJson { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    /// <summary>Attachment ids (JSON array) whose text went with this user turn.</summary>
    public string? AttachmentsJson { get; set; }
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CachedTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public MessageStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A file someone attached: only its extracted text is kept, never the file.</summary>
public sealed class ChatAttachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Text { get; set; }
    public bool Truncated { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
