namespace Llm.Core.Code;

/// <summary>
/// A Code Studio workspace: a project and branch a person works on in the browser's IDE — on the server (a clone the
/// workspaces service keeps, worked on by Code Arena's own engine) or on their own machine (their Code Arena, connected).
/// Its engine's address and key are kept while it runs, so any of the app's replicas reaches it.
/// </summary>
public sealed class StudioWorkspace
{
    public const string Server = "server";
    public const string Machine = "machine";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>server or machine.</summary>
    public string Kind { get; set; } = Server;
    public string Name { get; set; } = "";
    /// <summary>The project's path (group/project), and the branch it was opened on.</summary>
    public string? Project { get; set; }
    public string? Branch { get; set; }
    /// <summary>Its agent's own key at the gateway (encrypted), and the key's alias there, to revoke it with the workspace.</summary>
    public string? KeyCipher { get; set; }
    public string? KeyAlias { get; set; }
    /// <summary>While its engine runs: its port at the workspaces service, and its key (encrypted); null when it sleeps.</summary>
    public int? EnginePort { get; set; }
    public string? EngineKeyCipher { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>Why it last failed to start (the clone, the engine), for the page.</summary>
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When the IDE last reached it: a workspace unused a while sleeps (its engine stopped, its files kept).</summary>
    public DateTimeOffset UsedAt { get; set; } = DateTimeOffset.UtcNow;
}
