using Llm.Core.Access;

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
    /// <summary>The functions this chat loaded with load_tools, when its tools go on demand: they stay loaded.</summary>
    public List<string> LoadedTools { get; set; } = [];
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
    /// <summary>The scheduled task whose run this chat is, if any.</summary>
    public Guid? ScheduledTaskId { get; set; }
    /// <summary>The assistant this chat is with, if any: its instructions and files go with every answer.</summary>
    public Guid? AssistantId { get; set; }
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
    /// <summary>What a tool's call shows the person beyond what the model read (sub-agents' work: their thinking, tool calls and words), as JSON.</summary>
    public string? DetailsJson { get; set; }
    /// <summary>Assistant: what filled the request it answered, in characters by kind (system, tools, files, your messages...), as JSON; scaled to its prompt tokens for the context gauge.</summary>
    public string? ContextJson { get; set; }
    /// <summary>Assistant: its thinking was cut short ("Answer now"), and it answered without more.</summary>
    public bool CutShort { get; set; }
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CachedTokens { get; set; }
    public int? CompletionTokens { get; set; }
    /// <summary>Assistant: how long the model thought before answering. Tool: how long the tool took.</summary>
    public int? ThinkingMs { get; set; }
    public int? DurationMs { get; set; }
    public MessageStatus Status { get; set; }
    public string? Error { get; set; }
    /// <summary>
    /// Set when the chat was compacted here: a summary of its branch from the first
    /// message down to this one. The model reads it instead of those messages (the
    /// person still sees them); a branch that does not pass here is not affected.
    /// </summary>
    public string? Summary { get; set; }
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
    /// <summary>
    /// "text" (Text holds it), "image" (Data holds it), "audio" (Data is its MP3; Text, once made, its transcript)
    /// or "video" (Data is the video; Sound its sound track, its frames are pages; Text, once made, the transcript).
    /// </summary>
    public string Kind { get; set; } = "text";
    public byte[]? Data { get; set; }
    /// <summary>A video's sound track, as an MP3 (mono, 16 kHz).</summary>
    public byte[]? Sound { get; set; }
    /// <summary>A sound's or video's length.</summary>
    public double? Seconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A page of a document attachment (PDF, Word, PowerPoint...), drawn as a JPEG for its preview; made once, on first look.</summary>
public sealed class AttachmentPage
{
    public Guid AttachmentId { get; set; }

    /// <summary>From 1.</summary>
    public int Number { get; set; }

    /// <summary>How many pages the document has (only the first ones are drawn).</summary>
    public int Total { get; set; }

    public required byte[] Data { get; set; }
}

/// <summary>
/// An assistant (as custom GPTs, Gems and Claude's projects): instructions and files
/// (its knowledge) that every answer of its chats reads, the model, thinking and tools a
/// new chat with it starts with, and a few conversation starters. Its owner's alone until
/// shared: with groups who use it, or company-wide (admins); chosen people and groups
/// may edit it. Projects grew into assistants, and their rows are kept where they were.
/// </summary>
public sealed class Assistant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Its owner: they share it and remove it.</summary>
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>Sent with every answer of its chats, after the app's system prompt and before a chat's own.</summary>
    public string? Instructions { get; set; }
    /// <summary>The model a new chat with it uses (when the person may); null = the deployment's default.</summary>
    public string? Model { get; set; }
    /// <summary>The thinking level a new chat with it starts at; null = the deployment's default.</summary>
    public string? Thinking { get; set; }
    /// <summary>The tools a new chat with it has on (of those the person may use); null = the tools on in new chats.</summary>
    public List<string>? Tools { get; set; }
    /// <summary>Short prompts shown on a new chat with it, to start from.</summary>
    public List<string> Starters { get; set; } = [];
    /// <summary>Its icon and colour in lists (names the page knows: "bot", "code"…; "blue", "green"…).</summary>
    public string Icon { get; set; } = "bot";
    public string Color { get; set; } = "blue";
    /// <summary>Who may use it besides its owner and editors.</summary>
    public Reach Reach { get; set; }
    /// <summary>The groups whose members may use it, when <see cref="Reach"/> is Groups.</summary>
    public List<Guid> Groups { get; set; } = [];
    /// <summary>People who may edit it (not share or remove it).</summary>
    public List<Guid> EditorPeople { get; set; } = [];
    /// <summary>Groups whose members may edit it.</summary>
    public List<Guid> EditorGroups { get; set; } = [];
    /// <summary>How many chats were started with it, by anyone (deleted ones too).</summary>
    public int ChatsStarted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A file of an assistant (an uploaded attachment): every chat with it has it.</summary>
public sealed class AssistantFile
{
    public Guid AssistantId { get; set; }
    public Guid AttachmentId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A read-only link to a chat for people in the company or in chosen groups, never for
/// anyone signed out: the whole chat as it is now, or one branch of it as it was when
/// shared. Removing it (revoking) stops the link at once.
/// </summary>
public sealed class ChatShare
{
    /// <summary>The link's id: random, so it says nothing about when or what.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    /// <summary>The chat's owner, who shared it.</summary>
    public Guid UserId { get; set; }
    /// <summary>Null: the whole chat, with its branches, as it grows. Set: the branch that ends at this message.</summary>
    public Guid? LeafId { get; set; }
    /// <summary>Company (everyone who signs in) or Groups.</summary>
    public Reach Reach { get; set; } = Reach.Company;
    public List<Guid> Groups { get; set; } = [];
    /// <summary>How many times others opened it.</summary>
    public int Opens { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Someone who opened a shared chat (its owner does not count), so the owner sees how many people did.</summary>
public sealed class ChatShareView
{
    public Guid ShareId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset FirstAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Something a safeguard counts per person (a message refused, a picture drawn, a deep research), and when.</summary>
public sealed class SafeguardMark
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>blocked, image, research.</summary>
    public required string Kind { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
