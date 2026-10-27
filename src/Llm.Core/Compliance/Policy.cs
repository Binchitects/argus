using Llm.Core.Access;

namespace Llm.Core.Compliance;

/// <summary>
/// One of the company's policies (ISO 27001 5.2 and 7.5, ISO 42001's AI policy, SOC 2 CC5.3): its owner, who must read
/// it, how often it is reviewed, and its versions; the one approved last is in force.
/// </summary>
public sealed class Policy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>A short name for links and the evidence (information-security).</summary>
    public required string Key { get; set; }
    public required string Title { get; set; }
    public string? Owner { get; set; }
    /// <summary>Who must read and acknowledge it: everyone, the admins, or the members of <see cref="Groups"/>.</summary>
    public Audience Audience { get; set; } = Audience.Everyone;
    public Guid[] Groups { get; set; } = [];
    /// <summary>Reviewed again this many months after its approval.</summary>
    public int ReviewMonths { get; set; } = 12;
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A version of a policy: a draft until approved, then in force until the next is approved.</summary>
public sealed class PolicyVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PolicyId { get; set; }
    public int Version { get; set; }
    /// <summary>Its text, in Markdown.</summary>
    public required string Text { get; set; }
    /// <summary>What changed from the version before.</summary>
    public string? Changes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    /// <summary>When it is to be reviewed (set at approval, and again at each review that keeps it).</summary>
    public DateTimeOffset? ReviewDue { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}

/// <summary>A person read a policy's version and said so.</summary>
public sealed class PolicyAcknowledgement
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PolicyVersionId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
