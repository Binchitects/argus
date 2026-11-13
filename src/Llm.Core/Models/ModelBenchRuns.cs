namespace Llm.Core.Models;

/// <summary>
/// A run of the model test bench (Admin → Models → Test): what a model can do (its capability, a set of tests each checked
/// by a rule) or how much at once (its capacity, answers at once raised until more do not make more), and how it went.
/// </summary>
public sealed class ModelBenchRun
{
    public const string Capability = "capability", Capacity = "capacity";
    public const string Running = "running", Done = "done", Failed = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>The model, by its name at the gateway.</summary>
    public required string Model { get; set; }
    public required string Kind { get; set; }
    public string Status { get; set; } = Running;
    /// <summary>What it found so far, as JSON: the tests and their results, or each level of answers at once.</summary>
    public string Results { get; set; } = "{}";
    public string? Error { get; set; }
    public Guid? ById { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
