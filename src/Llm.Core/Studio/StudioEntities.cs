namespace Llm.Core.Studio;

/// <summary>
/// An agent workflow (Agent Studio): a trigger and the steps it runs, as JSON, run as its owner (their model, tools, credit
/// and safeguards). Its form, when it has one, may be shared.
/// </summary>
public sealed class Workflow
{
    public const string Private = "private";
    public const string Groups = "groups";
    public const string Company = "company";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    /// <summary>The trigger and the steps, JSON: <c>{ trigger: { kind, … }, steps: [{ id, kind, … }] }</c>.</summary>
    public required string Flow { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Who may run it by hand or fill its form (it runs as its owner): its owner, the groups named, or everyone.</summary>
    public string Reach { get; set; } = Private;
    public List<Guid> GroupIds { get; set; } = [];
    /// <summary>SHA-256 of its webhook's secret (shown once), for a webhook or GitLab trigger.</summary>
    public string? SecretHash { get; set; }
    /// <summary>When its schedule runs it next; null when no schedule runs it.</summary>
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One run of a workflow: what started it, its input, and how it went; its steps are <see cref="WorkflowRunStep"/>s.</summary>
public sealed class WorkflowRun
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkflowId { get; set; }
    /// <summary>The workflow as it was when the run started, JSON: a run replays what it ran.</summary>
    public required string Flow { get; set; }
    /// <summary>manual, schedule, webhook, gitlab, room or form.</summary>
    public string Trigger { get; set; } = "manual";
    /// <summary>Who started it by hand or by its form; null for a schedule or an event.</summary>
    public Guid? ById { get; set; }
    /// <summary>The run's input, JSON: <c>{ text, …fields }</c>.</summary>
    public string Input { get; set; } = "{}";
    /// <summary>Where in GitLab its event came from, JSON, for a comment step.</summary>
    public string? Reply { get; set; }
    public string Status { get; set; } = Running;
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>A step of a run, as it went: its place (a step in a loop's third turn is "s3[2]/s4"), what it was given and what it gave.</summary>
public sealed class WorkflowRunStep
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RunId { get; set; }
    public int Order { get; set; }
    /// <summary>The step's place in the flow, unique in the run.</summary>
    public required string Path { get; set; }
    public required string StepId { get; set; }
    public required string Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>running, waiting (an approval), done, failed or skipped.</summary>
    public string Status { get; set; } = "running";
    /// <summary>What it was given, its templates filled in: a prompt, a tool's arguments, a message.</summary>
    public string Input { get; set; } = "";
    /// <summary>What it gave, as text.</summary>
    public string Output { get; set; } = "";
    /// <summary>What it gave as JSON, when it did (a model asked for JSON, a tool's result, a condition's answer).</summary>
    public string? OutputJson { get; set; }
    /// <summary>A link to what it made: its chat, its file, the room.</summary>
    public string? Link { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
