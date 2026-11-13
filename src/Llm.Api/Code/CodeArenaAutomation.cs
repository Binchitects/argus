using Llm.Api.Access;
using Llm.Api.ArenaMcp;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Operations;
using Llm.Api.Schedules;
using Llm.Api.Studio;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Studio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Code;

/// <summary>
/// The person's agents (Agent Studio's workflows) and scheduled tasks, for Code Arena with their key, as the pages have
/// them: listed, run, and each run followed. The same rules as the pages: an agent is the person's or shared with them, a
/// task the person's own, Agent Studio and scheduled tasks as their role allows.
/// </summary>
public static class CodeArenaAutomation
{
    public static void MapCodeArenaAutomation(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        g.MapGet("/agents", AgentsAsync);
        g.MapPost("/agents/{id:guid}/run", RunAgentAsync);
        g.MapGet("/agents/runs/{runId:guid}", AgentRunAsync);
        g.MapGet("/scheduled", ScheduledAsync);
        g.MapPost("/scheduled/{id:guid}/run", RunScheduledAsync);
        g.MapGet("/scheduled/{id:guid}/runs", ScheduledRunsAsync);
    }

    private static async Task<(AppUser? Me, IResult? Refusal)> AgentsCallerAsync(HttpContext http, McpPeople people, AccessService access, Features features)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return (null, refusal);
        }
        if (!features.IsOn("agents"))
        {
            return (null, AuthEndpoints.Problem(404, "feature-off", "Agent Studio is not installed in this Arena."));
        }
        return (await access.MembershipAsync(me, http.RequestAborted)).Has(Core.Access.Permissions.Studio)
            ? (me, null)
            : (null, AuthEndpoints.Problem(403, "studio", "Your role does not include Agent Studio."));
    }

    private static async Task<(AppUser? Me, IResult? Refusal)> TasksCallerAsync(HttpContext http, McpPeople people, AccessService access, bool run)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return (null, refusal);
        }
        return !run || (await access.MembershipAsync(me, http.RequestAborted)).Has(Core.Access.Permissions.Tasks)
            ? (me, null)
            : (null, AuthEndpoints.Problem(403, "tasks", "Your role does not include scheduled tasks."));
    }

    /// <summary>The agents the person may run: theirs, and those shared with their groups or the company (on).</summary>
    private static async Task<IResult> AgentsAsync(HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var groups = (await access.MembershipAsync(me, ct)).Groups.ToList();
        var list = await db.Workflows.AsNoTracking()
            .Where(w => w.OwnerId == me.Id || (w.Enabled && (w.Reach == Workflow.Company || (w.Reach == Workflow.Groups && w.GroupIds.Any(g => groups.Contains(g))))))
            .OrderByDescending(w => w.UpdatedAt).Take(300).ToListAsync(ct);
        var owners = list.Select(w => w.OwnerId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName == "" ? u.UserName! : u.DisplayName, ct);
        return Results.Ok(list.Select(w => StudioEndpoints.Brief(w, me.Id, names.GetValueOrDefault(w.OwnerId))));
    }

    private static async Task<IResult> RunAgentAsync(Guid id, RunRequest body, HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db, StudioRuns runs)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        var (run, problem) = await StudioEndpoints.StartRunAsync(id, body, me, db, access, runs, http.RequestAborted);
        return problem ?? Results.Accepted($"/api/code-arena/agents/runs/{run}", new { id = run });
    }

    private static async Task<IResult> AgentRunAsync(Guid runId, HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        return await StudioEndpoints.DetailAsync(runId, me, db, http.RequestAborted) is { } detail ? Results.Ok(detail) : Results.NotFound();
    }

    /// <summary>The person's scheduled tasks, each with its next runs and how its last went.</summary>
    private static async Task<IResult> ScheduledAsync(HttpContext http, McpPeople people, AccessService access, AppDbContext db, TimeProvider clock, IOptions<AuthOptions> auth)
    {
        var (me, refusal) = await TasksCallerAsync(http, people, access, run: false);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var tasks = await db.ScheduledTasks.AsNoTracking().Where(t => t.UserId == me.Id).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var ids = tasks.Select(t => t.Id).ToList();
        var lastRuns = (await db.ScheduledRuns.AsNoTracking().Where(r => ids.Contains(r.TaskId)).ToListAsync(ct))
            .GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.StartedAt));
        var now = clock.GetUtcNow();
        return Results.Ok(tasks.Select(t => TaskEndpoints.View(t, lastRuns.GetValueOrDefault(t.Id), Scheduler.IsRunning(t, now), now, auth.Value.Origin)));
    }

    private static async Task<IResult> RunScheduledAsync(Guid id, HttpContext http, McpPeople people, AccessService access, AppDbContext db, Scheduler scheduler)
    {
        var (me, refusal) = await TasksCallerAsync(http, people, access, run: true);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.UserId == me.Id, ct) is not { } task)
        {
            return Results.NotFound();
        }
        return await scheduler.StartAsync(task.Id, manual: true, ct: ct) ? Results.Accepted() : AuthEndpoints.Problem(409, "running", "It is running now; wait for this run to end.");
    }

    private static async Task<IResult> ScheduledRunsAsync(Guid id, HttpContext http, McpPeople people, AccessService access, AppDbContext db)
    {
        var (me, refusal) = await TasksCallerAsync(http, people, access, run: false);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (!await db.ScheduledTasks.AnyAsync(t => t.Id == id && t.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        var runs = await db.ScheduledRuns.AsNoTracking().Where(r => r.TaskId == id).OrderByDescending(r => r.StartedAt).Take(50).ToListAsync(ct);
        return Results.Ok(runs.Select(TaskEndpoints.RunView));
    }
}
