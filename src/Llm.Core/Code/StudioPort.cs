namespace Llm.Core.Code;

/// <summary>
/// A port of a Code Studio workspace as its owner set it in the Ports view: one they forwarded by hand (a dev server not
/// started yet), or one found listening that they named, shared or stopped. A port found listening with no row is
/// forwarded to its owner alone, as VS Code forwards what it detects.
/// </summary>
public sealed class StudioPort
{
    /// <summary>Its owner alone reaches it.</summary>
    public const string Private = "private";
    /// <summary>Everyone signed in to Arena who may use Code Studio reaches it (its address, given to them).</summary>
    public const string SignedIn = "signed-in";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public int Port { get; set; }
    /// <summary>What the person calls it (Web app, API).</summary>
    public string? Label { get; set; }
    public string Visibility { get; set; } = Private;
    /// <summary>Forwarded by hand, before or without anything listening; else found listening.</summary>
    public bool Added { get; set; }
    /// <summary>Forwarding stopped by the person: not reached until they forward it again.</summary>
    public bool Stopped { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
