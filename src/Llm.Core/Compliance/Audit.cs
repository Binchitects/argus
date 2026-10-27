namespace Llm.Core.Compliance;

/// <summary>
/// An audit (ISO 27001 9.2, ISO 42001 9.2, SOC 2 CC4): internal, or an external body's (a certification's stage 1 or 2, a
/// surveillance, a recertification, a SOC 2 period), with its scope, auditor and dates; its findings are kept with it.
/// </summary>
public sealed class ComplianceAudit
{
    public static readonly string[] Kinds = ["internal", "stage1", "stage2", "surveillance", "recertification", "soc2", "assessment", "other"];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }
    /// <summary>internal, stage1, stage2, surveillance, recertification, soc2, assessment or other.</summary>
    public string Kind { get; set; } = "internal";
    /// <summary>The framework audited against (iso27001...), if one.</summary>
    public string? Framework { get; set; }
    public string? Scope { get; set; }
    public string? Auditor { get; set; }
    public DateTimeOffset Starts { get; set; }
    public DateTimeOffset? Ends { get; set; }
    /// <summary>What the auditor concluded, once reported.</summary>
    public string? Conclusion { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A finding (ISO 27001 10.2): a nonconformity (major or minor), an observation or an opportunity, from an audit or found
/// otherwise; with its correction, root cause, corrective action, owner and date, and the check that the action worked.
/// </summary>
public sealed class Finding
{
    public static readonly string[] Kinds = ["major", "minor", "observation", "opportunity"];
    public const string Open = "open", Done = "done", Verified = "verified";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? AuditId { get; set; }
    public required string Title { get; set; }
    public string Kind { get; set; } = "minor";
    public string? Description { get; set; }
    /// <summary>The clause it is against (framework and id: "iso27001 A.8.5"), and Arena's control, if any.</summary>
    public string? Clause { get; set; }
    public string? Control { get; set; }
    public string? Owner { get; set; }
    public DateTimeOffset? Due { get; set; }
    public string? Correction { get; set; }
    public string? RootCause { get; set; }
    public string? CorrectiveAction { get; set; }
    /// <summary>open, done (the action taken) or verified (checked that it worked; closed).</summary>
    public string Status { get; set; } = Open;
    public DateTimeOffset? DoneAt { get; set; }
    public string? DoneBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
    public string? Verification { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A certificate or attestation the company holds (an ISO certificate, a SOC 2 report), with its scope and when it ends.</summary>
public sealed class Certificate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Framework { get; set; }
    public required string Body { get; set; }
    public string? Number { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset Issued { get; set; }
    public DateTimeOffset Expires { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A waiver (an exception): a control not met for a time, approved by a person, with why, what makes up for it, and until when.
/// </summary>
public sealed class ControlWaiver
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Arena's control (IAM-03) or a framework's clause ("iso27001 A.8.5").</summary>
    public required string Control { get; set; }
    public required string Reason { get; set; }
    public string? Compensating { get; set; }
    public required string ApprovedBy { get; set; }
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Until { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
}
