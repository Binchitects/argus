namespace Llm.Core.Code;

/// <summary>
/// Fix it: an agent's change to a project's code, for a review finding, an issue, a failed pipeline or what the person asked:
/// it read the branch with the person's own GitLab token, changed the code in a workspace, ran the tests it could, and shows
/// the diff; the merge request is opened as the person, with their token, when they say so.
/// </summary>
public sealed class CodeFix
{
    public const string Working = "working";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Opened = "opened";
    public const string Stopped = "stopped";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>The project's path (group/project).</summary>
    public required string Project { get; set; }
    /// <summary>The branch it starts from (and the merge request goes into).</summary>
    public required string Branch { get; set; }
    /// <summary>The commit it read.</summary>
    public string? BaseSha { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What it was asked to do, in full (the finding, the issue, the failed job's log, the person's words).</summary>
    public string Ask { get; set; } = "";
    /// <summary>finding, issue, pipeline or ask.</summary>
    public string Source { get; set; } = "ask";
    public string? SourceId { get; set; }
    public string Status { get; set; } = Working;
    /// <summary>What it is at now, for the page while it works.</summary>
    public string? Step { get; set; }
    /// <summary>The agent's account of what it changed and why, and what the tests said.</summary>
    public string? Summary { get; set; }
    /// <summary>What it did, step by step, JSON: <c>[{ tool, about, result }]</c>.</summary>
    public string Log { get; set; } = "[]";
    /// <summary>The files it changed, JSON: <c>{ path: content, … }</c> (null content: deleted).</summary>
    public string Changes { get; set; } = "{}";
    /// <summary>The change as a unified diff.</summary>
    public string Diff { get; set; } = "";
    /// <summary>The tests' last run: its command, whether it passed, its output.</summary>
    public string? Tests { get; set; }
    public string? Model { get; set; }
    public string? MergeRequestUrl { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
