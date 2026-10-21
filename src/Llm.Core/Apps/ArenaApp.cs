namespace Llm.Core.Apps;

/// <summary>
/// An app: a page or a React component the chat built, published at its own link for the company (or some groups, or
/// only its owner). It runs in the preview's sandbox (no network); through window.arena it asks the model as the person
/// using it and keeps its data here. Every version of its code is kept.
/// </summary>
public sealed class ArenaApp
{
    public const string Private = "private";
    public const string Groups = "groups";
    public const string Company = "company";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    /// <summary>"html" (a page) or "react" (a component, its default export).</summary>
    public string Kind { get; set; } = "html";
    public required string Code { get; set; }
    public int Version { get; set; } = 1;
    /// <summary>Who may open it: its owner only, the groups named, or everyone.</summary>
    public string Reach { get; set; } = Private;
    public List<Guid> GroupIds { get; set; } = [];
    /// <summary>The app it was remixed from, if any.</summary>
    public Guid? RemixOfId { get; set; }
    /// <summary>The chat it was built in, if any.</summary>
    public Guid? ConversationId { get; set; }
    public int Opens { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One version of an app's code, kept to go back to.</summary>
public sealed class ArenaAppVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AppId { get; set; }
    public int Version { get; set; }
    public required string Code { get; set; }
    public string? Note { get; set; }
    public Guid ById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A value an app keeps (arena.store): everyone's (<see cref="UserId"/> empty) or one person's own.</summary>
public sealed class ArenaAppData
{
    public Guid AppId { get; set; }
    /// <summary>Whose: Guid.Empty for the data everyone using the app shares.</summary>
    public Guid UserId { get; set; }
    public required string Key { get; set; }
    /// <summary>JSON.</summary>
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A question an app asked the model as one person: counted against the asks a day.</summary>
public sealed class ArenaAppAsk
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? AppId { get; set; }
    public Guid UserId { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
