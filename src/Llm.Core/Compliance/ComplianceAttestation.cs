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

/// <summary>
/// A person's request about their data (GDPR Articles 15–22): taken in from them or by an admin for them, answered within its
/// month. Kept with its outcome after they are gone (an erasure's certificate with it), naming them as they were.
/// </summary>
public sealed class DataRequest
{
    public const string Access = "access", Portability = "portability", Rectification = "rectification", Restriction = "restriction",
        Objection = "objection", Erasure = "erasure";

    public static readonly IReadOnlyList<string> Kinds = [Access, Portability, Rectification, Restriction, Objection, Erasure];

    public const string Open = "open", Done = "done", Refused = "refused";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>The person; null once they are erased.</summary>
    public Guid? UserId { get; set; }
    public required string Person { get; set; }
    public string? Email { get; set; }
    public required string Kind { get; set; }
    /// <summary>What they asked, in their words.</summary>
    public string? Details { get; set; }
    public string Status { get; set; } = Open;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>A month after it came in (Article 12(3)).</summary>
    public DateTimeOffset DueAt { get; set; }
    /// <summary>"themselves", or the admin who took it in for them.</summary>
    public required string ReceivedBy { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public string? DoneBy { get; set; }
    /// <summary>What was done, or why it was refused.</summary>
    public string? Outcome { get; set; }
    /// <summary>An erasure's certificate (JSON): what was erased, what is kept and why, and when backups let go of the rest.</summary>
    public string? Certificate { get; set; }
}
