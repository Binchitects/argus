namespace Llm.Core.Plans;

/// <summary>
/// A plan: steps with owners, dates and what each waits for, that a chat makes and keeps up to date (the Plans tool),
/// shown as a board and a timeline. A plan of a room is its people's to see and change. Code Arena's agent keeps its
/// working plan here too (<see cref="Source"/> code): each step with what done means, the files it touches, and how its
/// check went.
/// </summary>
public sealed class Plan
{
    public const string FromChat = "chat";
    public const string FromCode = "code";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Who made it.</summary>
    public Guid UserId { get; set; }
    /// <summary>The chat it belongs to: whoever may open the chat (a room's people) may open the plan.</summary>
    public Guid? ConversationId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What it is for, when done looks like what.</summary>
    public string? Goal { get; set; }
    /// <summary>Who keeps it: the chat's Plans tool (<see cref="FromChat"/>), or Code Arena's agent (<see cref="FromCode"/>), which alone changes it.</summary>
    public string Source { get; set; } = FromChat;
    /// <summary>Code Arena's: where it works (the project's folder name).</summary>
    public string? Place { get; set; }
    /// <summary>Code Arena's, made in plan mode: proposed (the person reviews it there) or approved; null otherwise.</summary>
    public string? Review { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PlanStep> Steps { get; set; } = [];
}

public sealed class PlanStep
{
    public const string Todo = "todo";
    public const string Doing = "doing";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public static readonly string[] States = [Todo, Doing, Blocked, Done];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PlanId { get; set; }
    /// <summary>Its place in the plan, from 1.</summary>
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    /// <summary>Who does it: a name, as the plan says it (a person, a team).</summary>
    public string? Owner { get; set; }
    public string Status { get; set; } = Todo;
    public DateOnly? Start { get; set; }
    public DateOnly? Due { get; set; }
    /// <summary>The steps (by number) it waits for.</summary>
    public List<int> DependsOn { get; set; } = [];
    /// <summary>What done means: how it is checked (a command whose success says so, or what to look at).</summary>
    public string? Check { get; set; }
    /// <summary>The files it changes (Code Arena's), relative to its project.</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>How its check went the last time it ran: passed or failed (null: not run).</summary>
    public string? CheckResult { get; set; }
    /// <summary>What the check said, its end, when it ran.</summary>
    public string? CheckOutput { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
}
