using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Schedules;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Studio;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Studio;

/// <param name="Reach">Who may run it by hand or by its form: private, groups (<paramref name="GroupIds"/>) or company.</param>
public sealed record WorkflowRequest(string? Name = null, string? Description = null, Flow? Flow = null, bool? Enabled = null, string? Reach = null, List<Guid>? GroupIds = null);

/// <summary>A run started by hand or by its form: its words, and its fields' values.</summary>
public sealed record RunRequest(string? Text = null, Dictionary<string, string>? Fields = null);

public sealed record Approval(bool Approve);

/// <summary>
/// Agent Studio: workflows (a trigger, its steps) run as their owner; by hand, a form, a schedule, a webhook, GitLab or a
/// room's message; each run with its steps as they went, to read and replay; an approval answered; templates.
/// </summary>
public static class StudioEndpoints
{
    public const int MostWorkflows = 100;

    public static void MapStudio(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/studio").PersonOnly().RequirePermission(Core.Access.Permissions.Studio);
        g.MapGet("", ListAsync);
        g.MapGet("/templates", () => Results.Ok(StudioTemplates.All));
        g.MapGet("/tools", ToolsAsync);
        g.MapGet("/tools/{tool}/functions", FunctionsAsync);
        g.MapPost("", CreateAsync);
        g.MapPost("/from-task/{taskId:guid}", FromTaskAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/secret", SecretAsync);
        g.MapPost("/{id:guid}/run", RunAsync);
        g.MapGet("/{id:guid}/runs", RunsAsync);
        g.MapGet("/runs/{runId:guid}", RunDetailAsync);
        g.MapPost("/runs/{runId:guid}/approve", ApproveAsync);
        g.MapPost("/runs/{runId:guid}/stop", StopAsync);
        app.MapPost("/api/studio/hooks/{id:guid}", HookAsync).AllowAnonymous("A workflow's webhook: its secret is checked.").DisableAntiforgery();
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static async Task<bool> MayRunAsync(Workflow w, AppUser me, AccessService access, CancellationToken ct) =>
        w.OwnerId == me.Id || w.Reach == Workflow.Company || (w.Reach == Workflow.Groups && (await access.MembershipAsync(me, ct)).Groups.Any(w.GroupIds.Contains));

    private static object Brief(Workflow w, Guid me, string? owner) => new
    {
        w.Id, w.Name, w.Description, w.Enabled, w.Reach, w.GroupIds, w.NextRunAt, w.LastRunAt, w.UpdatedAt, mine = w.OwnerId == me, owner,
        trigger = FlowModel.Read(w.Flow).Trigger.Kind,
    };

    private static object Full(Workflow w, HttpRequest request) => new
    {
        w.Id, w.Name, w.Description, w.Enabled, w.Reach, w.GroupIds, w.NextRunAt, w.LastRunAt, w.CreatedAt, w.UpdatedAt, mine = true,
        flow = JsonNode.Parse(w.Flow), hook = $"{request.Scheme}://{request.Host}/api/studio/hooks/{w.Id}", secretSet = w.SecretHash is not null,
    };

    /// <summary>One's own workflows, and those whose form or run one may start (shared).</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var groups = (await access.MembershipAsync(me, ct)).Groups.ToList();
        var list = await db.Workflows.AsNoTracking()
            .Where(w => w.OwnerId == me.Id || (w.Enabled && (w.Reach == Workflow.Company || (w.Reach == Workflow.Groups && w.GroupIds.Any(g => groups.Contains(g))))))
            .OrderByDescending(w => w.UpdatedAt).Take(300).ToListAsync(ct);
        var owners = list.Select(w => w.OwnerId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName == "" ? u.UserName! : u.DisplayName, ct);
        return Results.Ok(list.Select(w => Brief(w, me.Id, names.GetValueOrDefault(w.OwnerId))));
    }

    /// <summary>The tools a tool step may use: those its owner may use.</summary>
    private static async Task<IResult> ToolsAsync(ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var mine = await registry.ForAsync(await access.MembershipAsync(me, ct), ct);
        return Results.Ok(mine.Select(t => new { t.Tool.Id, t.Tool.Title, t.Tool.Description, t.Tool.Icon }));
    }

    /// <summary>A tool's functions, with what each takes.</summary>
    private static async Task<IResult> FunctionsAsync(string tool, ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if ((await registry.ForAsync(await access.MembershipAsync(me, ct), ct)).FirstOrDefault(t => t.Tool.Id == tool) is not { } choice)
        {
            return Results.NotFound();
        }
        try
        {
            var run = await choice.Tool.StartAsync(new ToolContext(me, me.Email ?? "", new Llm.Core.Chat.Conversation { UserId = me.Id }), ct);
            return Results.Ok(run.Functions.Select(f => f?["function"]).OfType<JsonObject>().Select(f => new
            {
                name = f["name"]?.GetValue<string>(), description = f["description"]?.GetValue<string>(),
                parameters = (f["parameters"]?["properties"] as JsonObject)?.Select(x => new { name = x.Key, description = x.Value?["description"]?.GetValue<string>() }),
            }));
        }
        catch (McpException ex)
        {
            return AuthEndpoints.Problem(502, "tool", ex.Message);
        }
    }

    /// <summary>The request applied: its name, its flow (checked), who may run it; its next scheduled run worked out.</summary>
    private static async Task<IResult?> ApplyAsync(Workflow w, WorkflowRequest body, AppUser me, AppDbContext db, AccessService access, TimeProvider clock, CancellationToken ct)
    {
        if (body.Name is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 100)
            {
                return AuthEndpoints.Problem(400, "name", "A workflow needs a name of up to 100 characters.");
            }
            w.Name = name;
        }
        if (body.Description is { } description)
        {
            w.Description = description.Trim() is var d && d.Length > 500 ? d[..500] : d;
        }
        if (body.Flow is { } flow)
        {
            if (FlowModel.Check(flow) is { } problem)
            {
                return AuthEndpoints.Problem(400, "flow", problem);
            }
            // A room trigger or step: a room its owner writes in.
            foreach (var room in FlowModel.All(flow.Steps).Where(s => s.Kind == "room").Select(s => s.RoomId!.Value).Append(flow.Trigger.Kind == "room" ? flow.Trigger.RoomId!.Value : Guid.Empty).Where(r => r != Guid.Empty).Distinct())
            {
                var c = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == room && x.Kind == Core.Chat.RoomMember.Room, ct);
                if (c is null || (c.UserId != me.Id && !await db.RoomMembers.AnyAsync(m => m.ConversationId == room && m.UserId == me.Id && m.Role != Core.Chat.RoomMember.Viewer, ct)))
                {
                    return AuthEndpoints.Problem(400, "room", "A room the workflow uses must be one you write in.");
                }
            }
            w.Flow = FlowModel.Write(flow);
            w.NextRunAt = StudioRuns.NextRun(flow.Trigger, clock.GetUtcNow());
        }
        if (body.Enabled is { } enabled)
        {
            w.Enabled = enabled;
        }
        if (body.Reach is { } reach)
        {
            if (reach is not (Workflow.Private or Workflow.Groups or Workflow.Company))
            {
                return AuthEndpoints.Problem(400, "reach", "Who may run it: private, groups or company.");
            }
            w.Reach = reach;
        }
        if (body.GroupIds is { } groupIds)
        {
            var known = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            if (known.Count != groupIds.Distinct().Count())
            {
                return AuthEndpoints.Problem(400, "groups", "A group of those is not here.");
            }
            var mine = (await access.MembershipAsync(me, ct)).Groups;
            if (!await access.MayAsync(me, Core.Access.Permissions.AssistantsShare, ct) && known.Any(g => !mine.Contains(g)))
            {
                return AuthEndpoints.Problem(403, "groups", "Share it with groups you are in.");
            }
            w.GroupIds = known;
        }
        if (w.Reach == Workflow.Groups && w.GroupIds.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Name the groups that may run it.");
        }
        return null;
    }

    private static string NewSecret(Workflow w)
    {
        var secret = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        w.SecretHash = Hash(secret);
        return secret;
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    private static bool Hooked(Workflow w) => FlowModel.Read(w.Flow).Trigger.Kind is "webhook" or "gitlab";

    private static async Task<IResult> CreateAsync(WorkflowRequest body, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        TimeProvider clock, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (body.Name is null || body.Flow is null)
        {
            return AuthEndpoints.Problem(400, "workflow", "A workflow needs a name and its steps.");
        }
        if (await db.Workflows.CountAsync(w => w.OwnerId == me.Id, ct) >= MostWorkflows)
        {
            return AuthEndpoints.Problem(409, "workflows", $"You have {MostWorkflows} workflows, the most: remove one first.");
        }
        var w = new Workflow { OwnerId = me.Id, Name = "", Flow = "{}" };
        if (await ApplyAsync(w, body, me, db, access, clock, ct) is { } problem)
        {
            return problem;
        }
        var secret = Hooked(w) ? NewSecret(w) : null;
        db.Workflows.Add(w);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workflow.create", w.Name, detail: FlowModel.Read(w.Flow).Trigger.Kind);
        return Results.Created($"/api/studio/{w.Id}", new { workflow = Full(w, request), secret });
    }

    /// <summary>A scheduled task as a workflow: its trigger, its prompt and tools as an agent step, its email and GitLab answer as steps.</summary>
    private static async Task<IResult> FromTaskAsync(Guid taskId, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        TimeProvider clock, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId && t.UserId == me.Id, ct) is not { } task)
        {
            return Results.NotFound();
        }
        var flow = new Flow
        {
            Trigger = task.Trigger switch
            {
                Triggers.GitLab => new FlowTrigger { Kind = "gitlab", Events = task.Events },
                Triggers.Webhook => new FlowTrigger { Kind = "webhook" },
                _ => new FlowTrigger { Kind = "schedule", Cron = task.Cron, TimeZone = task.TimeZone },
            },
            Steps = [new FlowStep { Id = "answer", Kind = "agent", Name = "Answer", Prompt = task.Trigger == Triggers.Schedule ? task.Prompt : task.Prompt + "\n\n{{input.text}}", Tools = task.Tools, Model = task.Model }],
        };
        if (task.Email)
        {
            flow.Steps.Add(new FlowStep { Id = "mail", Kind = "email", Name = "Email it", Subject = task.Name, Text = "{{answer.text}}" });
        }
        if (task.ReplyInGitLab)
        {
            flow.Steps.Add(new FlowStep { Id = "reply", Kind = "comment", Name = "Answer in GitLab", Text = "{{answer.text}}" });
        }
        return await CreateAsync(new WorkflowRequest(task.Name, $"Made from the scheduled task {task.Name}.", flow, false), request, p, users, db, access, clock, audit, ct);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct) is not { } w || !await MayRunAsync(w, me, access, ct))
        {
            return Results.NotFound();
        }
        if (w.OwnerId == me.Id)
        {
            return Results.Ok(Full(w, request));
        }
        // Someone it is shared with: what it is, and its form's fields; not its steps.
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == w.OwnerId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        var trigger = FlowModel.Read(w.Flow).Trigger;
        return Results.Ok(new { w.Id, w.Name, w.Description, w.Enabled, mine = false, owner, trigger = trigger.Kind, fields = trigger.Fields ?? [] });
    }

    private static async Task<IResult> UpdateAsync(Guid id, WorkflowRequest body, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        AccessService access, TimeProvider clock, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Workflows.SingleOrDefaultAsync(w => w.Id == id && w.OwnerId == me.Id, ct) is not { } w)
        {
            return Results.NotFound();
        }
        if (await ApplyAsync(w, body, me, db, access, clock, ct) is { } problem)
        {
            return problem;
        }
        // A trigger that takes events now needs its secret.
        var secret = Hooked(w) && w.SecretHash is null ? NewSecret(w) : null;
        w.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workflow.update", w.Name);
        return Results.Ok(new { workflow = Full(w, request), secret });
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Workflows.SingleOrDefaultAsync(w => w.Id == id && w.OwnerId == me.Id, ct) is not { } w)
        {
            return Results.NotFound();
        }
        db.Workflows.Remove(w);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workflow.delete", w.Name);
        return Results.NoContent();
    }

    /// <summary>A new secret for its webhook, shown once.</summary>
    private static async Task<IResult> SecretAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Workflows.SingleOrDefaultAsync(w => w.Id == id && w.OwnerId == me.Id, ct) is not { } w)
        {
            return Results.NotFound();
        }
        var secret = NewSecret(w);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { secret });
    }

    /// <summary>Run now, by hand or by its form (by those it is shared with too): its words and its fields; it runs as its owner.</summary>
    private static async Task<IResult> RunAsync(Guid id, RunRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, StudioRuns runs,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct) is not { } w || !await MayRunAsync(w, me, access, ct))
        {
            return Results.NotFound();
        }
        if (!w.Enabled && w.OwnerId != me.Id)
        {
            return AuthEndpoints.Problem(409, "off", "It is off.");
        }
        var trigger = FlowModel.Read(w.Flow).Trigger;
        var input = new JsonObject { ["text"] = (body.Text ?? "").Trim() is var t && t.Length > 20_000 ? t[..20_000] : t, ["by"] = me.DisplayName == "" ? me.UserName : me.DisplayName };
        foreach (var f in trigger.Fields ?? [])
        {
            var value = body.Fields?.GetValueOrDefault(f.Name)?.Trim() ?? "";
            if (f.Required && value.Length == 0)
            {
                return AuthEndpoints.Problem(400, f.Name, $"{(f.Label.Length > 0 ? f.Label : f.Name)} is needed.");
            }
            if (f.Kind == "choice" && value.Length > 0 && f.Choices?.Contains(value) != true)
            {
                return AuthEndpoints.Problem(400, f.Name, $"{(f.Label.Length > 0 ? f.Label : f.Name)}: one of {string.Join(", ", f.Choices ?? [])}.");
            }
            input[f.Name] = value.Length > 10_000 ? value[..10_000] : value;
        }
        var run = await runs.StartAsync(w, trigger.Kind == "form" ? "form" : "manual", input, me.Id, null, ct);
        return Results.Accepted($"/api/studio/runs/{run}", new { id = run });
    }

    private static async Task<IResult> RunsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Workflows.AnyAsync(w => w.Id == id && w.OwnerId == me.Id, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await db.WorkflowRuns.AsNoTracking().Where(r => r.WorkflowId == id).OrderByDescending(r => r.StartedAt).Take(100)
            .Select(r => new { r.Id, r.Trigger, r.Status, r.Error, r.StartedAt, r.FinishedAt, steps = db.WorkflowRunSteps.Count(s => s.RunId == r.Id) }).ToListAsync(ct));
    }

    /// <summary>A run with its steps in the order they went: for its workflow's owner, and for whoever started it.</summary>
    private static async Task<IResult> RunDetailAsync(Guid runId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct) is not { } run
            || await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == run.WorkflowId, ct) is not { } w
            || (w.OwnerId != me.Id && run.ById != me.Id))
        {
            return Results.NotFound();
        }
        var steps = await db.WorkflowRunSteps.AsNoTracking().Where(s => s.RunId == runId).OrderBy(s => s.Order).ToListAsync(ct);
        var mine = w.OwnerId == me.Id;
        return Results.Ok(new
        {
            run.Id, run.WorkflowId, workflow = w.Name, run.Trigger, run.Status, run.Error, run.StartedAt, run.FinishedAt,
            input = JsonNode.Parse(run.Input), flow = mine ? JsonNode.Parse(run.Flow) : null,
            // Someone it is shared with sees how it went, not what each step was given or gave.
            steps = steps.Select(s => new
            {
                s.Path, s.StepId, s.Kind, s.Name, s.Status, input = mine ? s.Input : null, output = mine || s.Kind is "text" ? s.Output : null,
                s.Link, s.Error, s.StartedAt, s.FinishedAt,
            }),
        });
    }

    /// <summary>An approval answered by the workflow's owner: the run goes on (or ends, turned down).</summary>
    private static async Task<IResult> ApproveAsync(Guid runId, Approval body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StudioRuns runs, TimeProvider clock,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == runId, ct) is not { } run
            || !await db.Workflows.AnyAsync(w => w.Id == run.WorkflowId && w.OwnerId == me.Id, ct))
        {
            return Results.NotFound();
        }
        if (run.Status != WorkflowRun.Waiting
            || await db.WorkflowRunSteps.SingleOrDefaultAsync(s => s.RunId == runId && s.Status == "waiting", ct) is not { } step)
        {
            return AuthEndpoints.Problem(409, "approval", "It is not waiting for an answer.");
        }
        step.Status = "done";
        step.Output = body.Approve ? "Approved" : "Turned down";
        step.OutputJson = new JsonObject { ["approved"] = body.Approve }.ToJsonString();
        step.FinishedAt = clock.GetUtcNow();
        run.Status = WorkflowRun.Running;
        await db.SaveChangesAsync(ct);
        runs.Go(runId);
        return Results.Accepted($"/api/studio/runs/{runId}", new { id = runId });
    }

    private static async Task<IResult> StopAsync(Guid runId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StudioRuns runs, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == runId, ct) is not { } run
            || !await db.Workflows.AnyAsync(w => w.Id == run.WorkflowId && w.OwnerId == me.Id, ct))
        {
            return Results.NotFound();
        }
        if (!runs.Stop(runId) && run.Status is WorkflowRun.Running or WorkflowRun.Waiting)
        {
            // Waiting, or running on another replica that is gone: ended here.
            run.Status = WorkflowRun.Stopped;
            run.Error = "Stopped.";
            run.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    /// <summary>An event for a workflow: GitLab's (its secret in X-Gitlab-Token) or any system's (X-Hook-Secret). One it does not take is acknowledged.</summary>
    private static async Task<IResult> HookAsync(Guid id, HttpRequest request, AppDbContext db, StudioRuns runs, GitLabBot bot, CancellationToken ct)
    {
        var w = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        var supplied = request.Headers["X-Gitlab-Token"].FirstOrDefault() ?? request.Headers["X-Hook-Secret"].FirstOrDefault() ?? "";
        if (w?.SecretHash is not { } expected || supplied.Length == 0
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Hash(supplied)), System.Text.Encoding.ASCII.GetBytes(expected)))
        {
            return Results.Json(new { error = "unknown workflow or wrong secret" }, statusCode: 401);
        }
        var trigger = FlowModel.Read(w.Flow).Trigger;
        if (!w.Enabled || trigger.Kind is not ("webhook" or "gitlab"))
        {
            return Results.Ok(new { status = "ignored", reason = "the workflow is off, or no event starts it" });
        }
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (body.Length > 1_000_000)
        {
            return Results.Json(new { error = "the event is larger than 1 MB" }, statusCode: 413);
        }
        TriggerEvent? e;
        JsonNode? payload = null;
        try
        {
            payload = body.Length == 0 ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
        }
        if (trigger.Kind == "gitlab")
        {
            if (payload is not JsonObject o)
            {
                return Results.BadRequest(new { error = "the event is not a JSON object" });
            }
            e = await Triggers.FromGitLabAsync(o, trigger.Events ?? [], bot, ct);
        }
        else
        {
            e = Triggers.FromJson(body);
        }
        if (e is null)
        {
            return Results.Ok(new { status = "ignored", reason = "not an event this workflow takes" });
        }
        var input = new JsonObject { ["text"] = e.Text };
        if (payload is not null && body.Length <= 200_000)
        {
            input["event"] = payload;
        }
        var run = await runs.StartAsync(w, trigger.Kind, input, null, e.Reply, ct);
        return Results.Accepted(value: new { status = "started", run });
    }
}
