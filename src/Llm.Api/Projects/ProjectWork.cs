using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Projects;

public sealed class ProjectWorkException(string message) : Exception(message);

/// <summary>A chat's project, as the chat's person may change it: the project, and whether they may (a contributor or more, the project active).</summary>
public sealed record ProjectPlace(Project Project, bool MayChange);

/// <summary>
/// A project's goals and tasks as its chats and Code Arena sessions keep them: the plan read, a goal or a task added or
/// changed (by the same rules as the project's page), each marked with the chat that set it; and a Code Arena session's own
/// goal kept as one of the project's. What the project tool, Code Arena's project tools and goal sync share.
/// </summary>
public static class ProjectWork
{
    public const string PlanFunction = "project_plan", GoalFunction = "project_goal", TaskFunction = "project_task";

    /// <summary>The most of the plan the model reads in the tool's instructions; the rest it reads with project_plan.</summary>
    public const int MostInstructionChars = 6000;

    /// <summary>The project of the person's chat, when it is in one they may see; null: none.</summary>
    public static async Task<ProjectPlace?> PlaceAsync(AppDbContext db, AccessService access, Core.Identity.AppUser person, Guid chat, CancellationToken ct)
    {
        var projectId = await db.Conversations.AsNoTracking().Where(c => c.Id == chat).Select(c => c.WorkProjectId).SingleOrDefaultAsync(ct);
        if (projectId is not { } id)
        {
            return null;
        }
        var (project, role) = await ProjectAccess.FindAsync(db, id, person.Id, await access.MembershipAsync(person, ct), ct);
        return project is null ? null : new ProjectPlace(project, project.Status == Project.Active && ProjectAccess.Holds(role, ProjectMember.Contributor));
    }

    /// <summary>The plan as the model reads it: each goal with its state, its id and its tasks, then the tasks of no goal.</summary>
    public static async Task<string> TextAsync(AppDbContext db, Project project, Guid? chat, CancellationToken ct)
    {
        var goals = await db.ProjectGoals.AsNoTracking().Where(g => g.ProjectId == project.Id).OrderBy(g => g.Position).ThenBy(g => g.CreatedAt).ToListAsync(ct);
        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == project.Id).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Project \"{project.Name}\": {goals.Count} goals ({goals.Count(g => g.Status == ProjectGoal.Done)} done), {tasks.Count} tasks ({tasks.Count(t => t.Done)} done).\n");
        string Mine(Guid? of) => of is not null && of == chat ? " (this chat's)" : "";
        void Tasks(IEnumerable<ProjectTask> some, string indent)
        {
            foreach (var t in some)
            {
                text.Append(CultureInfo.InvariantCulture, $"{indent}- [{(t.Done ? "x" : " ")}] {t.Title} (task {t.Id}){Mine(t.ChatId)}\n");
            }
        }
        foreach (var g in goals)
        {
            text.Append(CultureInfo.InvariantCulture, $"Goal: {g.Title} [{g.Status}]{(g.DueOn is { } due ? $", due {due:yyyy-MM-dd}" : "")} (goal {g.Id}){Mine(g.ChatId)}\n");
            if (g.Description is { Length: > 0 } d)
            {
                text.Append("  ").Append(d.Length > 300 ? d[..300] + "…" : d).Append('\n');
            }
            Tasks(tasks.Where(t => t.GoalId == g.Id), "  ");
        }
        var loose = tasks.Where(t => t.GoalId is null || goals.All(g => g.Id != t.GoalId)).ToList();
        if (loose.Count > 0)
        {
            text.Append("Tasks of no goal:\n");
            Tasks(loose, "");
        }
        return text.ToString();
    }

    private static Guid? Id(JsonObject args, string name) =>
        Schema.Str(args, name) is { Length: > 0 } s ? Guid.TryParse(s.Trim(), out var id) ? id : throw new ProjectWorkException($"{name} is an id from the plan, as project_plan shows it.") : null;

    /// <summary>Adds a goal (no id) or changes one (its title, state, description, due date), set by this chat when it adds it.</summary>
    public static async Task<ProjectGoal> SetGoalAsync(AppDbContext db, ProjectPlace place, Guid chat, JsonObject args, CancellationToken ct)
    {
        Changeable(place);
        var p = place.Project;
        var body = new ProjectGoalRequest(Schema.Str(args, "title"), Schema.Str(args, "description"), Schema.Str(args, "status"), Schema.Str(args, "due"));
        if (ProjectEndpoints.GoalFault(body) is { } fault)
        {
            throw new ProjectWorkException(fault.Message);
        }
        ProjectGoal goal;
        if (Id(args, "id") is { } id)
        {
            goal = await db.ProjectGoals.SingleOrDefaultAsync(g => g.Id == id && g.ProjectId == p.Id, ct) ?? throw new ProjectWorkException("There is no such goal in this project.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(body.Title))
            {
                throw new ProjectWorkException("A new goal needs a title.");
            }
            if (await db.ProjectGoals.CountAsync(g => g.ProjectId == p.Id, ct) >= ProjectEndpoints.MostGoals)
            {
                throw new ProjectWorkException($"A project has at most {ProjectEndpoints.MostGoals} goals.");
            }
            goal = new ProjectGoal
            {
                ProjectId = p.Id, Title = body.Title.Trim(), ChatId = chat,
                Position = await db.ProjectGoals.Where(g => g.ProjectId == p.Id).MaxAsync(g => (int?)g.Position, ct) + 1 ?? 0,
            };
            db.ProjectGoals.Add(goal);
        }
        ProjectEndpoints.Apply(goal, body);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return goal;
    }

    /// <summary>Adds a task (no id), of a goal or not, or changes one (its title, goal, done or not), set by this chat when it adds it.</summary>
    public static async Task<ProjectTask> SetTaskAsync(AppDbContext db, ProjectPlace place, Guid chat, Guid person, JsonObject args, CancellationToken ct)
    {
        Changeable(place);
        var p = place.Project;
        var done = args["done"] is JsonValue v && v.TryGetValue<bool>(out var d) ? d : (bool?)null;
        var body = new ProjectTaskRequest(Schema.Str(args, "title"), Id(args, "goal"), Done: done);
        if (await ProjectEndpoints.TaskFaultAsync(db, p.Id, body, ct) is { } fault)
        {
            throw new ProjectWorkException(fault.Message);
        }
        ProjectTask task;
        if (Id(args, "id") is { } id)
        {
            task = await db.ProjectTasks.SingleOrDefaultAsync(t => t.Id == id && t.ProjectId == p.Id, ct) ?? throw new ProjectWorkException("There is no such task in this project.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(body.Title))
            {
                throw new ProjectWorkException("A new task needs a title.");
            }
            if (await db.ProjectTasks.CountAsync(t => t.ProjectId == p.Id, ct) >= ProjectEndpoints.MostTasks)
            {
                throw new ProjectWorkException($"A project has at most {ProjectEndpoints.MostTasks} tasks.");
            }
            task = new ProjectTask { ProjectId = p.Id, Title = body.Title.Trim(), CreatedById = person, ChatId = chat };
            db.ProjectTasks.Add(task);
        }
        ProjectEndpoints.Apply(task, body);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return task;
    }

    private static void Changeable(ProjectPlace place)
    {
        if (!place.MayChange)
        {
            throw new ProjectWorkException(place.Project.Status == Project.Active
                ? "You may read this project's plan, not change it: a contributor or its owner may."
                : "This project is archived: its plan is as it was left.");
        }
    }

    /// <summary>
    /// A Code Arena session's goal (goal mode) in a project: kept as one of the project's goals, the session's, its state
    /// following the session's (working: doing; met: done; cancelled: dropped). Its title is the goal's first line.
    /// </summary>
    public static async Task MirrorAsync(AppDbContext db, AccessService access, Core.Identity.AppUser person, ChatGoal goal, CancellationToken ct)
    {
        if (await PlaceAsync(db, access, person, goal.ConversationId, ct) is not { MayChange: true } place)
        {
            return;
        }
        var title = goal.Text.Split('\n', 2)[0].Trim();
        title = title.Length > 200 ? title[..199] + "…" : title;
        if (title.Length == 0)
        {
            return;
        }
        var status = goal.Status switch { "met" => ProjectGoal.Done, "cancelled" => ProjectGoal.Dropped, _ => ProjectGoal.Doing };
        var mine = await db.ProjectGoals.SingleOrDefaultAsync(g => g.ProjectId == place.Project.Id && g.ChatId == goal.ConversationId && g.Title == title, ct);
        if (mine is null)
        {
            if (await db.ProjectGoals.CountAsync(g => g.ProjectId == place.Project.Id, ct) >= ProjectEndpoints.MostGoals)
            {
                return;
            }
            mine = new ProjectGoal
            {
                ProjectId = place.Project.Id, Title = title, ChatId = goal.ConversationId,
                Description = goal.CheckText is { Length: > 0 } check ? (check.Length > 2000 ? check[..2000] : $"Checked by: {check}") : null,
                Position = await db.ProjectGoals.Where(g => g.ProjectId == place.Project.Id).MaxAsync(g => (int?)g.Position, ct) + 1 ?? 0,
            };
            db.ProjectGoals.Add(mine);
        }
        ProjectEndpoints.Apply(mine, new ProjectGoalRequest(Status: status));
        place.Project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The functions the model is given, the same in a chat and in Code Arena.</summary>
    public static JsonArray Functions(bool mayChange)
    {
        var functions = new JsonArray(Schema.Function(PlanFunction, "The project's plan as it is now: its goals with their states and tasks, by id.", new JsonObject()));
        if (mayChange)
        {
            functions.Add(Schema.Function(GoalFunction, "Adds a goal of the project (no id), or changes one (its id): its title, state, description, due date. Keep each goal to an aim the person set out.",
                new JsonObject
                {
                    ["id"] = Schema.Text("The goal's id, to change it; leave out to add one."),
                    ["title"] = Schema.Text("What it is, in a line (1 to 200 characters)."),
                    ["status"] = Schema.Choice("Its state.", ProjectGoal.States),
                    ["description"] = Schema.Text("More on it: what done means, how it is checked."),
                    ["due"] = Schema.Text("When it is due, 2026-12-31."),
                }));
            functions.Add(Schema.Function(TaskFunction, "Adds a task of the project (no id), under a goal or not, or changes one (its id): its title, its goal, done or not.",
                new JsonObject
                {
                    ["id"] = Schema.Text("The task's id, to change it; leave out to add one."),
                    ["title"] = Schema.Text("What to do, in a line (1 to 300 characters)."),
                    ["goal"] = Schema.Text("The id of the goal it is part of."),
                    ["done"] = new JsonObject { ["type"] = "boolean", ["description"] = "true when it is done; false when it is not after all." },
                }));
        }
        return functions;
    }

    /// <summary>What the model is told: keep the plan true to the work, and the plan as it is now (cut to <see cref="MostInstructionChars"/>).</summary>
    public static string Instructions(ProjectPlace place, string plan) =>
        $"This chat is in the project \"{place.Project.Name}\". Its goals and tasks are the team's plan, and this chat keeps them true to its work"
        + (place.MayChange
            ? ": when the person sets out an aim, add it as a goal (or use the one there); as you plan the work, add its steps as tasks under it; "
              + "tick a task done (project_task with done) when it is done, and set a goal doing as work on it starts and done when it is met. "
              + "Do not add goals or tasks for questions or small talk, and do not change what others set unless the work changed it.\n"
            : ". You may read it (project_plan), not change it.\n")
        + (plan.Length > MostInstructionChars ? plan[..MostInstructionChars] + "\n… (project_plan reads all of it)" : plan);

    /// <summary>A call of one of the functions, by the person in their chat.</summary>
    public static async Task<ToolResult> CallAsync(AppDbContext db, ProjectPlace place, Guid chat, Guid person, string function, JsonObject args, CancellationToken ct)
    {
        try
        {
            switch (function)
            {
                case PlanFunction:
                    return new ToolResult(await TextAsync(db, place.Project, chat, ct));
                case GoalFunction:
                    var goal = await SetGoalAsync(db, place, chat, args, ct);
                    return new ToolResult($"Goal \"{goal.Title}\" [{goal.Status}] (goal {goal.Id}) is saved.\n" + await TextAsync(db, place.Project, chat, ct));
                case TaskFunction:
                    var task = await SetTaskAsync(db, place, chat, person, args, ct);
                    return new ToolResult($"Task \"{task.Title}\" [{(task.Done ? "done" : "to do")}] (task {task.Id}) is saved.\n" + await TextAsync(db, place.Project, chat, ct));
                default:
                    return new ToolResult($"There is no function {function}.", IsError: true);
            }
        }
        catch (ProjectWorkException ex)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
    }
}

/// <summary>The project's plan for Code Arena, with the person's key: what its session's model is told, and its calls.</summary>
public static class ProjectWorkEndpoints
{
    public static void MapProjectWork(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena/chats/{id:guid}/project-work").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        g.MapGet("", ReadAsync);
        g.MapPost("/{function}", CallAsync);
    }

    private static async Task<(Core.Identity.AppUser? Me, IResult? Refusal)> ChatAsync(Guid id, HttpContext http, ArenaMcp.McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await Chat.CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return (null, refusal);
        }
        return await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted)
            ? (me, null)
            : (null, Endpoints.AuthEndpoints.Problem(404, "gone", "That chat is not there."));
    }

    /// <summary>The session's project and its plan as the model is told it, with the functions it may call; 204: the session is in no project.</summary>
    private static async Task<IResult> ReadAsync(Guid id, HttpContext http, ArenaMcp.McpPeople people, AppDbContext db, AccessService access)
    {
        var (me, refusal) = await ChatAsync(id, http, people, db);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (await ProjectWork.PlaceAsync(db, access, me, id, ct) is not { } place)
        {
            return Results.NoContent();
        }
        var plan = await ProjectWork.TextAsync(db, place.Project, id, ct);
        return Results.Ok(new
        {
            project = new { id = place.Project.Id, name = place.Project.Name },
            mayChange = place.MayChange,
            instructions = ProjectWork.Instructions(place, plan),
            functions = ProjectWork.Functions(place.MayChange).Select(f => f!["function"]!["name"]!.GetValue<string>()),
        });
    }

    /// <summary>A call of project_plan, project_goal or project_task by the session's model: its words, and whether it failed.</summary>
    private static async Task<IResult> CallAsync(Guid id, string function, JsonObject? body, HttpContext http, ArenaMcp.McpPeople people, AppDbContext db, AccessService access)
    {
        var (me, refusal) = await ChatAsync(id, http, people, db);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (await ProjectWork.PlaceAsync(db, access, me, id, ct) is not { } place)
        {
            return Results.Ok(new { text = "This session is in no project: /project puts it in one.", error = true });
        }
        var result = await ProjectWork.CallAsync(db, place, id, me.Id, function, body ?? [], ct);
        return Results.Ok(new { text = result.Text, error = result.IsError });
    }
}

/// <summary>
/// A chat in a project, for its answers: the project's plan (goals and tasks), and project_goal and project_task to keep it
/// up to date as the work goes. Started for every answer of a chat in a project; never chosen by hand.
/// </summary>
public sealed class ProjectWorkTool(AppDbContext db, AccessService access) : IChatTool
{
    public const string ToolId = "project-work";

    public string Id => ToolId;
    public string Title => "The project's plan";
    public string Description => "The goals and tasks of the project this chat is in: read them, and keep them up to date as the work goes.";
    public string Icon => "list-checks";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var chat = context.Conversation.Id;
        var place = await ProjectWork.PlaceAsync(db, access, context.User, chat, ct);
        if (place is null)
        {
            return new LocalRun([], null, (_, _, _) => Task.FromResult(new ToolResult("This chat is in no project.", IsError: true)));
        }
        return new LocalRun(ProjectWork.Functions(place.MayChange), ProjectWork.Instructions(place, await ProjectWork.TextAsync(db, place.Project, chat, ct)),
            async (function, args, token) => await ProjectWork.PlaceAsync(db, access, context.User, chat, token) is { } now
                ? await ProjectWork.CallAsync(db, now, chat, context.User.Id, function, args, token)
                : new ToolResult("This chat is in no project any more.", IsError: true));
    }
}
