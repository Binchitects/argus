namespace Llm.Core.Chat;

/// <summary>
/// Something the models remember, for the chat and Code Arena alike: about a person (for all their chats and projects), a
/// project (a repository, for the person's sessions in it), or the company (kept by admins, read by everyone). The prompt
/// takes the pinned ones and those that matter to the question; the person lists, edits and forgets them (Memory).
/// </summary>
public sealed class Memory
{
    /// <summary>About the person: every chat and project of theirs.</summary>
    public const string Person = "person";
    /// <summary>About a project (<see cref="ProjectKey"/>): the person's work in it.</summary>
    public const string Project = "project";
    /// <summary>About the company: kept by admins, read by everyone.</summary>
    public const string Company = "company";
    public static readonly string[] Scopes = [Person, Project, Company];

    /// <summary>How the person likes things done.</summary>
    public const string Preference = "preference";
    /// <summary>What is so (their team, the stack, a name).</summary>
    public const string Fact = "fact";
    /// <summary>What was decided, and why.</summary>
    public const string Decision = "decision";
    /// <summary>How to do something here (build, test, deploy).</summary>
    public const string HowTo = "howto";
    /// <summary>What went wrong once, or what the person corrected: not again.</summary>
    public const string Lesson = "lesson";
    public static readonly string[] Kinds = [Preference, Fact, Decision, HowTo, Lesson];

    /// <summary>Typed by the person (here, or with /memory).</summary>
    public const string Typed = "typed";
    /// <summary>Said in a chat, or offered there and kept.</summary>
    public const string FromChat = "chat";
    /// <summary>Kept by Code Arena in a session.</summary>
    public const string FromCodeArena = "code-arena";
    /// <summary>Brought in (Markdown, Code Arena's memory files).</summary>
    public const string Imported = "import";
    /// <summary>Proposed from what a chat said, as it went quiet (the source is its chat).</summary>
    public const string Reflected = "reflected";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Whose it is (a person's, a project's); who wrote it (the company's).</summary>
    public Guid UserId { get; set; }
    /// <summary>One or two short sentences.</summary>
    public required string Text { get; set; }
    public string Scope { get; set; } = Person;
    public string Kind { get; set; } = Fact;
    /// <summary>A project's: its repository's key (host/group/name, as its remote says), or one folder's on one machine.</summary>
    public string? ProjectKey { get; set; }
    public string Source { get; set; } = Typed;
    /// <summary>The chat or Code Arena session it came from.</summary>
    public Guid? SourceId { get; set; }
    /// <summary>Always given to the model, whatever the question.</summary>
    public bool Pinned { get; set; }
    /// <summary>When an answer last read it, and how many have.</summary>
    public DateTimeOffset? UsedAt { get; set; }
    public int Uses { get; set; }
    /// <summary>Its meaning as the knowledge embedder reads it (unit length); null until embedded.</summary>
    public float[]? Embedding { get; set; }
    /// <summary>A newer memory said otherwise: this one is kept (to undo), and no longer given to the model.</summary>
    public Guid? ReplacedBy { get; set; }
    /// <summary>Proposed from a chat that went quiet, waiting for the person: not given to the model until they keep it.</summary>
    public bool Proposed { get; set; }
    /// <summary>A proposal that says otherwise than this memory of theirs: kept, it replaces that one.</summary>
    public Guid? Replaces { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Written, edited or said again.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
