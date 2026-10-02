namespace Llm.Core.Models;

/// <summary>
/// Working hours for the models: during this window (days and hours, in the app's
/// ModelHours:TimeZone) the engine keeps these models loaded instead of the ones
/// admins pinned, and new chats start on its default. A small fast model in busy
/// hours, the big one at night.
/// </summary>
public sealed class ModelWindow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>The days it starts on, ISO: Monday 1 … Sunday 7.</summary>
    public List<int> Days { get; set; } = [];
    public TimeOnly Start { get; set; }
    /// <summary>Until (not including). Before the start: it runs past midnight; equal: all day.</summary>
    public TimeOnly End { get; set; }
    /// <summary>The models kept loaded meanwhile (at most the engine's places).</summary>
    public List<string> Keep { get; set; } = [];
    /// <summary>The model a new chat uses meanwhile; null: the usual (the first loaded).</summary>
    public string? DefaultModel { get; set; }
    /// <summary>When windows overlap, the lowest applies.</summary>
    public int Order { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
