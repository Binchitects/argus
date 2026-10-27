namespace Llm.Core.Compliance;

/// <summary>
/// A risk in the register (ISO 27001 6.1.2, 6.1.3 and 8.2, 8.3; ISO 42001's AI risks; SOC 2 CC3): what could
/// happen to which assets, how likely and how bad on a 1 to 5 scale before and after its treatment, who owns it,
/// how it is treated and with which of Arena's controls, and when it is looked at again.
/// </summary>
public sealed class Risk
{
    public const string Reduce = "reduce", Accept = "accept", Transfer = "transfer", Avoid = "avoid";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }
    public string? Description { get; set; }
    /// <summary>security, privacy, ai, continuity, supplier, legal or other.</summary>
    public string Category { get; set; } = "security";
    /// <summary>What it threatens: models, providers, data stores, connectors, repositories, by name.</summary>
    public string[] Assets { get; set; } = [];
    public string? Threat { get; set; }
    /// <summary>Before treatment, 1 (rare) to 5 (almost certain).</summary>
    public int Likelihood { get; set; }
    /// <summary>Before treatment, 1 (negligible) to 5 (severe).</summary>
    public int Impact { get; set; }
    public string? Owner { get; set; }
    /// <summary>reduce, accept, transfer or avoid.</summary>
    public string Treatment { get; set; } = Reduce;
    /// <summary>Arena's controls that treat it (controls.yaml ids).</summary>
    public string[] Controls { get; set; } = [];
    public string? Plan { get; set; }
    /// <summary>After treatment; null until assessed.</summary>
    public int? ResidualLikelihood { get; set; }
    public int? ResidualImpact { get; set; }
    /// <summary>Accepted as it is (or as it is left): by whom, why, and until when; then it is due again.</summary>
    public string? AcceptedBy { get; set; }
    public string? AcceptedWhy { get; set; }
    public DateTimeOffset? AcceptedUntil { get; set; }
    public DateTimeOffset ReviewDue { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A step of a risk's treatment: what, who, by when, and when it was done.</summary>
public sealed class RiskAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RiskId { get; set; }
    public required string Text { get; set; }
    public string? Owner { get; set; }
    public DateTimeOffset? Due { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public string? DoneBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A risk's history: each change, review, acceptance and action, by whom.</summary>
public sealed class RiskEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RiskId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public required string By { get; set; }
    /// <summary>created, changed, reviewed, accepted, action, action_done, closed, reopened.</summary>
    public required string Kind { get; set; }
    public string? Text { get; set; }
}

/// <summary>
/// A Statement of Applicability's line (ISO 27001 6.1.3 d, ISO 42001 6.1.3): for one of a framework's Annex A controls,
/// whether it applies, why, and how far it is in place.
/// </summary>
public sealed class SoaEntry
{
    public const string Implemented = "implemented", Partial = "partial", Planned = "planned", NotImplemented = "not_implemented";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>iso27001 or iso42001.</summary>
    public required string Framework { get; set; }
    public required string Clause { get; set; }
    public bool Applicable { get; set; } = true;
    /// <summary>implemented, partial, planned or not_implemented.</summary>
    public string Status { get; set; } = Implemented;
    public string? Justification { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A Statement of Applicability approved as it stood: its version, who approved it and when.</summary>
public sealed class SoaApproval
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Framework { get; set; }
    public int Version { get; set; }
    public required string ApprovedBy { get; set; }
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Note { get; set; }
    /// <summary>Its lines as they were approved (CSV), so a version can be read again.</summary>
    public required string Snapshot { get; set; }
}
