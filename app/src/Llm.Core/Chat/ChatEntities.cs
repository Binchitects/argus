namespace Llm.Core.Chat;

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "New chat";
    /// <summary>Thinking level for this chat ("low", "xhigh", "off"...); null = the deployment's default.</summary>
    public string? Thinking { get; set; }
    /// <summary>
    /// The tools this chat may call (ids, see ToolSetting); null = the tools that
    /// are on in new chats. What the person may use still applies.
    /// </summary>
    public List<string>? Tools { get; set; }
    /// <summary>The model this chat talks to; null = the deployment's default.</summary>
    public string? Model { get; set; }
    /// <summary>The person's own instructions for this chat, sent after the app's system prompt.</summary>
    public string? SystemPrompt { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    /// <summary>
    /// The last message of the branch on screen. Messages form a tree (an edited
    /// question or a regenerated answer is a sibling); the conversation shown and
    /// sent to the model is the path from the root to this leaf.
    /// </summary>
    public Guid? CurrentLeafId { get; set; }
    /// <summary>Set when the person archives the chat: it leaves the list, and comes back when they write in it.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
    /// <summary>The chat this one was forked from, if any (it may since have been deleted).</summary>
    public Guid? ForkedFromId { get; set; }
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
    /// <summary>A tool call the person did not allow ("ask before running").</summary>
    Declined = 3,
}

/// <summary>
/// One turn. Assistant turns that called tools keep the calls (ToolCallsJson);
/// each tool's answer is its own message with Role "tool", as the model sees them.
/// </summary>
public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    /// <summary>The message before this one on its branch; null for a first question.</summary>
    public Guid? ParentId { get; set; }
    /// <summary>Order of creation in the conversation, across branches.</summary>
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
    /// <summary>Assistant: how long the model thought before answering. Tool: how long the tool took.</summary>
    public int? ThinkingMs { get; set; }
    public int? DurationMs { get; set; }
    public MessageStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A file someone attached. For text, code and PDFs only the extracted text is
/// kept, never the file; an image is kept as it is (the model looks at it).
/// </summary>
public sealed class ChatAttachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Text { get; set; }
    public bool Truncated { get; set; }
    /// <summary>"text" (Text holds it) or "image" (Data holds it).</summary>
    public string Kind { get; set; } = "text";
    public byte[]? Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
