namespace Llm.Core.Code;

/// <summary>
/// A review of a merge request (or of what a branch has over another) by the model, run by a person with their own
/// read access to the code (Argus, read-only): findings on lines, each checked by a second pass that tried to refute
/// it, and a summary with a verdict. Nothing is posted back to the code host.
/// </summary>
public sealed class CodeReview
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Who ran it: it read the code as them, and its cost is theirs.</summary>
    public Guid UserId { get; set; }
    /// <summary>The project's path (group/project).</summary>
    public required string Project { get; set; }
    /// <summary>The merge request's number; null for a comparison of two refs.</summary>
    public int? Iid { get; set; }
    /// <summary>A comparison's refs (from, to), or the merge request's branches.</summary>
    public string? FromRef { get; set; }
    public string? ToRef { get; set; }
    public string Title { get; set; } = "";
    public string? WebUrl { get; set; }
    /// <summary>The commit it read: a review again reads only what came after it.</summary>
    public string? HeadSha { get; set; }
    /// <summary>The review it follows (a review again after new commits).</summary>
    public Guid? PreviousId { get; set; }
    public string Status { get; set; } = Queued;
    /// <summary>What it is at now, for the page while it runs ("reading the changes", "reviewing 3 of 7"...).</summary>
    public string? Step { get; set; }
    /// <summary>looks_good, comments or changes.</summary>
    public string? Verdict { get; set; }
    /// <summary>low, medium or high.</summary>
    public string? Risk { get; set; }
    /// <summary>The walkthrough and what to look at, in Markdown.</summary>
    public string? Summary { get; set; }
    /// <summary>The pipeline's state when it ran, and a failed job's explanation.</summary>
    public string? Ci { get; set; }
    public string? Model { get; set; }
    public int Files { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public string? Error { get; set; }
    /// <summary>The room it is discussed in, once someone opened one.</summary>
    public Guid? RoomId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public List<ReviewFinding> Findings { get; set; } = [];
}

/// <summary>One thing a review found, on a line of a file the change touched.</summary>
public sealed class ReviewFinding
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ReviewId { get; set; }
    public required string Path { get; set; }
    /// <summary>Its line in the new version of the file; null: the file as a whole.</summary>
    public int? Line { get; set; }
    /// <summary>critical, major, minor or nit.</summary>
    public string Severity { get; set; } = "minor";
    /// <summary>bug, security, performance, design, tests, style...</summary>
    public string? Category { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>The code it suggests instead, if any.</summary>
    public string? Suggestion { get; set; }
    /// <summary>The second pass's verdict: true when it could not refute it; false when it did (shown apart, with why).</summary>
    public bool? Verified { get; set; }
    public string? VerifyNote { get; set; }
    public string State { get; set; } = Open;
    public Guid? DecidedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
