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
