namespace Llm.Core.Projects;

/// <summary>
/// A piece of work several people do together, in the web chat and in Code Arena: its people and groups (each with a role),
/// its repositories, the chats, rooms and Code Arena sessions put in it, the assistants, skills and apps it uses, its goals
/// and tasks, and what it used (the usage log's requests tagged with it, gateway.requests.project_id). A global project is
/// everyone's to see and add to. The tables are work_projects*: "projects" is the assistants' (they were called projects).
/// </summary>
public sealed class Project
{
    public const string Active = "active";
    public const string Archived = "archived";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Who made it: an owner of it always.</summary>
    public Guid OwnerId { get; set; }
    /// <summary>Everyone who can sign in sees it and may add to it (a contributor); its owners still run it.</summary>
    public bool Global { get; set; }
    /// <summary>active or archived (read only: nothing is added, its chats answer as before).</summary>
    public string Status { get; set; } = Active;
    /// <summary>When the work starts and should be done, for its progress; null: not said.</summary>
    public DateOnly? StartsOn { get; set; }
    public DateOnly? DueOn { get; set; }
    /// <summary>What it may spend in a calendar month (all its requests together); null: no budget.</summary>
    public decimal? MonthlyBudget { get; set; }
    /// <summary>Its owners are told when this share of the budget is spent (and again when all of it is).</summary>
    public int AlertPercent { get; set; } = 80;
    /// <summary>Past the budget, its chats' answers are refused until the month ends or the budget is raised.</summary>
    public bool StopAtBudget { get; set; }
    /// <summary>Its tags (review, security, …: Tags:Known); accounting groups by them.</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>What it is about in a few sentences, from its chats (Summarize and tag); null until summarized.</summary>
    public string? Brief { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A person or a group in a project (one of the two), with a role.</summary>
public sealed class ProjectMember
{
    /// <summary>Changes the project, its people and its budget; sees each person's share.</summary>
    public const string Owner = "owner";
    /// <summary>Puts their chats, sessions and things in it, and keeps its goals and tasks.</summary>
    public const string Contributor = "contributor";
    /// <summary>Sees it and its numbers.</summary>
    public const string Viewer = "viewer";
    public static readonly string[] Roles = [Owner, Contributor, Viewer];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? GroupId { get; set; }
    public string Role { get; set; } = Contributor;
    public Guid? AddedById { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How much a role may do: viewer 1, contributor 2, owner 3; 0 for none.</summary>
    public static int Rank(string? role) => role switch { Owner => 3, Contributor => 2, Viewer => 1, _ => 0 };
}

/// <summary>
/// A repository of the project, by its path on the code host ("payments/api") and, when known, the host ("gitlab.corp"):
/// Code Arena's sessions in a clone of it go in the project, and its merged changes count to the project's progress.
/// </summary>
public sealed class ProjectRepository
{
    public Guid ProjectId { get; set; }
    public required string Path { get; set; }
    public string? Host { get; set; }
    public Guid? AddedById { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An assistant, a skill or an app the project uses, by its id. Being in a project does not share it: whoever may not
/// use it still may not. Chats, rooms and Code Arena sessions are in a project by their own column (Conversation.WorkProjectId).
/// </summary>
public sealed class ProjectItem
{
    public const string Assistant = "assistant";
    public const string Skill = "skill";
    public const string App = "app";
    public static readonly string[] Kinds = [Assistant, Skill, App];

    public Guid ProjectId { get; set; }
    public required string Kind { get; set; }
    public Guid ItemId { get; set; }
    public Guid? AddedById { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A goal or milestone of the project, with its state and when it is due.</summary>
public sealed class ProjectGoal
{
    public const string Planned = "planned";
    public const string Doing = "doing";
    public const string Done = "done";
    public const string Dropped = "dropped";
    public static readonly string[] States = [Planned, Doing, Done, Dropped];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = Planned;
    public DateOnly? DueOn { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    /// <summary>The hours a person would take for it by hand, without the models (counted when none of its tasks has its own); null: not estimated.</summary>
    public decimal? HumanHours { get; set; }
    /// <summary>Its place in the list.</summary>
    public int Position { get; set; }
    /// <summary>The chat or Code Arena session that set it (and keeps it up to date); null: set on the project's page.</summary>
    public Guid? ChatId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A task of the project, of a goal or not, done or not, and who does it.</summary>
public sealed class ProjectTask
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Guid? GoalId { get; set; }
    public required string Title { get; set; }
    public Guid? AssigneeId { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    /// <summary>The hours a person would take for it by hand, without the models; null: not estimated.</summary>
    public decimal? HumanHours { get; set; }
    public Guid? CreatedById { get; set; }
    /// <summary>The chat or Code Arena session that set it (and ticks it); null: set on the project's page.</summary>
    public Guid? ChatId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
