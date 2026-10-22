namespace Llm.Core.Code;

/// <summary>
/// What one project's software does for the business, as Argus's business map has it: its name, its domain and a
/// summary, and its profile (capabilities, entities, rules, processes, integrations and a glossary, each with the
/// sources it was read from), mapped by a model with Argus's read-only tools. Shown to whoever may read the project.
/// </summary>
public sealed class BusinessSystem
{
    public const string Building = "building";
    public const string Ready = "ready";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>The project's path in GitLab (group/project), one map each.</summary>
    public required string Project { get; set; }
    public string Name { get; set; } = "";
    public string? Domain { get; set; }
    public string? Summary { get; set; }
    /// <summary>The profile as JSON (BusinessProfile): what the page and the chat read.</summary>
    public string Profile { get; set; } = "{}";
    public string Status { get; set; } = Building;
    /// <summary>What it is doing while it maps (for the page).</summary>
    public string? Step { get; set; }
    public string? Error { get; set; }
    /// <summary>Who mapped it last: the map was read with their access, and spent on their account.</summary>
    public Guid? BuiltBy { get; set; }
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public DateTimeOffset? BuiltAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
