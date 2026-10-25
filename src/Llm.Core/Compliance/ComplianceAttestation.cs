namespace Llm.Core.Compliance;

/// <summary>
/// Someone with compliance.manage says the company's part of a control is done (a policy written, a review held), with a note
/// of what and where. Kept for good, the person's name with it, so the record outlives their account; the newest one counts until
/// it is a year old.
/// </summary>
public sealed class ComplianceAttestation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>The control's id in controls.yaml (IAM-01).</summary>
    public required string ControlId { get; set; }
    public Guid? ActorId { get; set; }
    public required string Actor { get; set; }
    /// <summary>What was done and where the proof is ("Access policy v3, signed by the CISO, in the ISMS folder").</summary>
    public required string Note { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An access review: who holds privileged access, each listed for a reviewer to keep or revoke, and accounts nobody used for long.
/// Kept for good with its decisions (who, when, what came of it), as the frameworks' auditors ask for.
/// </summary>
public sealed class AccessReview
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Who started it, or "the schedule".</summary>
    public required string CreatedBy { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
}

/// <summary>One thing an access review asks about, and what the reviewer decided.</summary>
public sealed class AccessReviewItem
{
    public const string Role = "role", Member = "member", Unused = "unused";
    public const string Keep = "keep", Revoke = "revoke";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ReviewId { get; set; }
    /// <summary>role: a privileged role given to a person or a group; member: a member of a group that holds one; unused: an account not signed in to for long.</summary>
    public required string Kind { get; set; }
    public Guid? AssignmentId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Who: the person or the group, by name as it was then.</summary>
    public required string Subject { get; set; }
    /// <summary>What they hold, in words as it was then ("Owner, given directly", "member of Ops, which holds People admin", "last signed in 2026-03-01").</summary>
    public required string What { get; set; }
    /// <summary>keep or revoke; null until decided.</summary>
    public string? Decision { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
    /// <summary>What revoking did ("the role was taken away"), or why it could not be done here ("remove them in the directory").</summary>
    public string? Outcome { get; set; }
}
