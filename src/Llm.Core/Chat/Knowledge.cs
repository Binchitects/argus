using Llm.Core.Access;

namespace Llm.Core.Chat;

/// <summary>
/// Where company knowledge comes from: a GitLab project or group (its wikis and issues), a folder
/// the app can read, or a website. Synced into documents and their passages, each document with
/// who may read it, so a search returns only what the asker may read.
/// </summary>
public sealed class KnowledgeSource
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    /// <summary>"gitlab", "folder" or "website".</summary>
    public required string Kind { get; set; }
    /// <summary>A GitLab project's or group's path (group/app), a folder (/knowledge/handbook), or a website's first page.</summary>
    public required string Location { get; set; }
    /// <summary>GitLab: its projects' wikis are read.</summary>
    public bool Wiki { get; set; } = true;
    /// <summary>GitLab: its projects' issues are read (never confidential ones).</summary>
    public bool Issues { get; set; } = true;
    /// <summary>A website: the hosts its pages may be on, comma separated; empty: the first page's host.</summary>
    public string? Hosts { get; set; }
    /// <summary>A website: the most pages read.</summary>
    public int MaxPages { get; set; } = 200;
    /// <summary>A folder or a website: who may read it. A GitLab project's readers are its members, in GitLab.</summary>
    public Audience Audience { get; set; }
    public List<Guid> Groups { get; set; } = [];
    /// <summary>new, syncing, synced or failed.</summary>
    public string State { get; set; } = "new";
    /// <summary>Why the last sync failed, or what it could not read.</summary>
    public string? Error { get; set; }
    public DateTimeOffset? SyncStartedAt { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A document a source holds (a wiki page, an issue, a file, a web page), or a chat's file that answers
/// read by its passages; with who may read it. Its passages are <see cref="KnowledgeChunk"/>s.
/// </summary>
public sealed class KnowledgeDocument
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? SourceId { get; set; }
    /// <summary>A chat attachment or project file, read by its passages instead of inlined.</summary>
    public Guid? AttachmentId { get; set; }
    /// <summary>What it is within its source: "7/wiki/deploy", "7/issues/12", a file's path, a page's address.</summary>
    public required string Key { get; set; }
    public required string Title { get; set; }
    /// <summary>Where people open it (a wiki page, an issue, a web page); null for a file in a folder.</summary>
    public string? Url { get; set; }
    /// <summary>What it was when read (an update time, a hash of its text): unchanged, it is not read again.</summary>
    public string? Version { get; set; }
    /// <summary>The embedding model its passages were embedded with: another model's vectors do not compare.</summary>
    public string? Model { get; set; }
    /// <summary>Who may read it: "everyone", "admins", "group:{id}", or "gitlab:{project id}" (that project's members).</summary>
    public List<string> Readers { get; set; } = [];
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A passage of a document, about 1,000 characters cut at headings and paragraphs, with its embedding.</summary>
public sealed class KnowledgeChunk
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    /// <summary>Its first and last line in the document, from 1.</summary>
    public int Line { get; set; }
    public int EndLine { get; set; }
    /// <summary>The heading it is under, if any.</summary>
    public string? Heading { get; set; }
    public required string Text { get; set; }
    /// <summary>Of unit length; compared as pgvector's vector type in the search.</summary>
    public required float[] Embedding { get; set; }
}

/// <summary>Who may read a GitLab project's documents: its members' usernames, read from GitLab and kept fresh.</summary>
public sealed class KnowledgeReaders
{
    public Guid SourceId { get; set; }
    /// <summary>"gitlab:{project id}", as its documents name their readers.</summary>
    public required string Key { get; set; }
    /// <summary>The project's path, for the admin page.</summary>
    public string? Name { get; set; }
    /// <summary>The GitLab usernames (lower case) of its active members, inherited ones too.</summary>
    public List<string> People { get; set; } = [];
    /// <summary>The project's last activity when its wiki and issues were last read: unchanged, they are not read again (for a day).</summary>
    public string? Activity { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    /// <summary>When its members were last read from GitLab.</summary>
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}
