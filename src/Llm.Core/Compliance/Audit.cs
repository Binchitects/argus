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

/// <summary>
/// A supplier (ISO 27001 A.5.19–A.5.23, SOC 2 CC9.2, the GDPR's processors): a model provider, a cloud, a connector or
/// another service the company relies on, with the data it receives, its certifications, the agreements, its risk and
/// its next review.
/// </summary>
public sealed class Supplier
{
    public static readonly string[] Kinds = ["model", "cloud", "connector", "software", "service", "other"];
    public static readonly string[] DataKinds = ["personal", "health", "confidential", "code", "payment"];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string Kind { get; set; } = "service";
    /// <summary>What it provides.</summary>
    public string? Service { get; set; }
    /// <summary>The kinds of data it receives: personal, health, confidential, code, payment.</summary>
    public string[] Data { get; set; } = [];
    /// <summary>Where it processes the data (a country, a region).</summary>
    public string? Location { get; set; }
    /// <summary>Its certifications and reports, with their dates (SOC 2 Type II to 2026-03; ISO 27001 until 2027-05).</summary>
    public string? Certifications { get; set; }
    /// <summary>The agreements signed (a DPA, a HIPAA BAA, standard contractual clauses).</summary>
    public string? Agreements { get; set; }
    /// <summary>low, medium or high: it sets how often it is reviewed.</summary>
    public string Risk { get; set; } = "medium";
    public string? Owner { get; set; }
    public string? Notes { get; set; }
    /// <summary>What Arena knows it as (remote-server:ID, mcp:ID, knowledge:ID, gitlab, huggingface), when it came from there.</summary>
    public string? Source { get; set; }
    public DateTimeOffset ReviewDue { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An AI system the company runs (ISO 42001's inventory, the EU AI Act, NIST's AI RMF): an assistant, an agent, an app or a
/// model, with its purpose and intended use, whom it affects, its class under the AI Act and the company's role, its impact
/// assessment, human oversight and evaluation; in use only once those its class needs are said.
/// </summary>
public sealed class AiSystem
{
    public static readonly string[] Kinds = ["assistant", "agent", "app", "model", "other"];
    /// <summary>Under the AI Act: not yet assessed, minimal, limited (transparency duties), high-risk, or a prohibited practice.</summary>
    public static readonly string[] Classes = ["unassessed", "minimal", "limited", "high", "prohibited"];
    public static readonly string[] Roles = ["deployer", "provider", "both"];
    /// <summary>The AI Act's Annex III areas, where a system is high-risk.</summary>
    public static readonly string[] HighRiskAreas =
    [
        "biometrics", "critical infrastructure", "education and vocational training", "employment and workers management",
        "essential private and public services", "law enforcement", "migration, asylum and border control", "justice and democratic processes",
    ];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string Kind { get; set; } = "assistant";
    /// <summary>What Arena knows it as (assistant:ID, workflow:ID, app:ID, model:NAME), when it came from there.</summary>
    public string? Source { get; set; }
    public string? Purpose { get; set; }
    public string? IntendedUse { get; set; }
    /// <summary>Whom it affects: the people who use it, and those its outputs are about.</summary>
    public string? Affected { get; set; }
    public string Class { get; set; } = "unassessed";
    public string? ClassWhy { get; set; }
    /// <summary>For a high-risk system: its Annex III area.</summary>
    public string? HighRiskArea { get; set; }
    public string Role { get; set; } = "deployer";
    public string? Owner { get; set; }
    /// <summary>How people oversee it: who can stop or overrule it, and how.</summary>
    public string? Oversight { get; set; }
    /// <summary>How it was evaluated for its task, and where the results are.</summary>
    public string? Evaluation { get; set; }
    public string? ImpactAssessment { get; set; }
    public DateTimeOffset? ImpactAssessedAt { get; set; }
    public string? ImpactAssessedBy { get; set; }
    public DateTimeOffset? InUseSince { get; set; }
    public DateTimeOffset ReviewDue { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A compliance check's result as it was on a day: the history an audit over a period reads (SOC 2 Type II), kept six years.
/// </summary>
public sealed class ComplianceSnapshot
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateOnly Day { get; set; }
    public required string Check { get; set; }
    public required string State { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Who owns one of Arena's controls in the company: the person who runs its part and answers for it.</summary>
public sealed class ControlOwner
{
    public required string ControlId { get; set; }
    public required string Owner { get; set; }
    public required string SetBy { get; set; }
    public DateTimeOffset SetAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A management review (ISO 27001, 42001 and 22301 9.3): its inputs as the system stood that day (drawn up by Arena), who
/// took part, the minutes and the decisions.
/// </summary>
public sealed class ManagementReview
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset HeldAt { get; set; }
    public string? Attendees { get; set; }
    /// <summary>The inputs the clause names, as Arena drew them up (Markdown).</summary>
    public required string Inputs { get; set; }
    public string? Minutes { get; set; }
    public string? Decisions { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A security objective (ISO 27001 6.2): what is to hold, measured by one of the compliance checks, its target and owner.</summary>
public sealed class SecurityObjective
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }
    /// <summary>The compliance check that measures it.</summary>
    public required string Check { get; set; }
    /// <summary>The share of days, out of 100, the check is to hold (ok).</summary>
    public int Target { get; set; } = 95;
    public string? Owner { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An auditor's request for evidence, and its answer.</summary>
public sealed class EvidenceRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Text { get; set; }
    public string? Clause { get; set; }
    public required string RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Answer { get; set; }
    public string? AnsweredBy { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
}
