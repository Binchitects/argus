using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Plans;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plans;

/// <summary>A plan's refusal, with words for the model or the page.</summary>
public sealed class PlanException(string message) : Exception(message);

/// <summary>A step as the tool and the page give it: unset fields are kept as they are.</summary>
public sealed record StepChange(int? Number = null, string? Title = null, string? Notes = null, string? Owner = null, string? Status = null,
    string? Start = null, string? Due = null, int[]? DependsOn = null, bool Remove = false);

/// <summary>Plans: made and kept by the chat's Plans tool, read and moved along on the page; a room's are its people's.</summary>
public static class PlanBook
{
    public const int MostSteps = 200;

    /// <summary>The plan, when <paramref name="me"/> may see it (<paramref name="need"/> Write: change it): its maker, or whoever may open its chat that way.</summary>
    public static async Task<Plan?> FindAsync(AppDbContext db, Guid id, AppUser me, Need need, CancellationToken ct)
    {
        var plan = await db.Plans.Include(p => p.Steps).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null)
        {
            return null;
        }
        if (plan.UserId == me.Id)
        {
            return plan;
        }
        return plan.ConversationId is { } chat && await RoomAccess.FindAsync(db, chat, me, need, ct) is not null ? plan : null;
    }

    private static DateOnly? Date(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new PlanException($"{what} is a date as 2026-11-30, not \"{text}\".");
    }

    /// <summary>Applies changes to a plan's steps: a change with a number changes (or removes) that step, without adds one; then checks it whole.</summary>
    public static void Apply(Plan plan, IEnumerable<StepChange> changes)
    {
        foreach (var c in changes)
        {
            PlanStep step;
            if (c.Number is { } n)
            {
                step = plan.Steps.FirstOrDefault(s => s.Number == n) ?? throw new PlanException($"The plan has no step {n}.");
                if (c.Remove)
                {
                    plan.Steps.Remove(step);
                    foreach (var other in plan.Steps)
                    {
                        other.DependsOn.Remove(n);
                    }
                    continue;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(c.Title))
                {
                    throw new PlanException("A new step needs a title.");
                }
                step = new PlanStep { PlanId = plan.Id, Number = plan.Steps.Count == 0 ? 1 : plan.Steps.Max(s => s.Number) + 1 };
                plan.Steps.Add(step);
            }
            if (c.Title is { } title)
            {
                step.Title = Cut(title.Trim(), 300);
            }
            if (c.Notes is { } notes)
            {
                step.Notes = notes.Trim().Length == 0 ? null : Cut(notes.Trim(), 4000);
            }
            if (c.Owner is { } owner)
            {
                step.Owner = owner.Trim().Length == 0 ? null : Cut(owner.Trim(), 120);
            }
            if (c.Status is { } status)
            {
                step.Status = PlanStep.States.Contains(status) ? status : throw new PlanException($"A step's status is one of {string.Join(", ", PlanStep.States)}.");
            }
            if (c.Start is not null)
            {
                step.Start = Date(c.Start, $"Step {step.Number}'s start");
            }
            if (c.Due is not null)
            {
                step.Due = Date(c.Due, $"Step {step.Number}'s due date");
            }
            if (c.DependsOn is { } deps)
            {
                step.DependsOn = [.. deps.Distinct().Order()];
            }
        }
        if (plan.Steps.Count > MostSteps)
        {
            throw new PlanException($"A plan holds at most {MostSteps} steps.");
        }
        var numbers = plan.Steps.Select(s => s.Number).ToHashSet();
        foreach (var s in plan.Steps)
        {
            if (s.DependsOn.FirstOrDefault(d => !numbers.Contains(d) || d == s.Number) is var bad && bad != 0)
            {
                throw new PlanException($"Step {s.Number} waits for step {bad}, which {(bad == s.Number ? "is itself" : "the plan does not have")}.");
            }
            if (s.Start is { } start && s.Due is { } due && due < start)
            {
                throw new PlanException($"Step {s.Number} is due before it starts.");
            }
        }
        if (Cycle(plan) is { } cycle)
        {
            throw new PlanException($"Steps {string.Join(" → ", cycle)} wait for one another in a circle.");
        }
        plan.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Steps that wait for one another in a circle, or null.</summary>
    private static List<int>? Cycle(Plan plan)
    {
        var deps = plan.Steps.ToDictionary(s => s.Number, s => s.DependsOn);
        var state = new Dictionary<int, int>();
        var path = new List<int>();
        List<int>? Visit(int n)
        {
            state[n] = 1;
            path.Add(n);
            foreach (var d in deps[n])
            {
                if (state.GetValueOrDefault(d) == 1)
                {
                    return [.. path.SkipWhile(x => x != d), d];
                }
                if (state.GetValueOrDefault(d) == 0 && Visit(d) is { } found)
                {
                    return found;
                }
            }
            state[n] = 2;
            path.RemoveAt(path.Count - 1);
            return null;
        }
        foreach (var n in deps.Keys.Order())
        {
            if (state.GetValueOrDefault(n) == 0 && Visit(n) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>What does not fit: a step that starts before what it waits for is due, one overdue and not done.</summary>
    public static List<string> Warnings(Plan plan, DateOnly today)
    {
        var byNumber = plan.Steps.ToDictionary(s => s.Number);
        var warnings = new List<string>();
        foreach (var s in plan.Steps.OrderBy(s => s.Number))
        {
            foreach (var d in s.DependsOn.Where(byNumber.ContainsKey).Select(d => byNumber[d]))
            {
                if (s.Start is { } start && d.Due is { } due && start < due)
                {
                    warnings.Add($"Step {s.Number} starts {start:yyyy-MM-dd}, before step {d.Number} it waits for is due ({due:yyyy-MM-dd}).");
                }
            }
            if (s.Due is { } late && late < today && s.Status != PlanStep.Done)
            {
                warnings.Add($"Step {s.Number} was due {late:yyyy-MM-dd} and is not done.");
            }
        }
        return warnings;
    }

    public static object Shape(Plan p, DateOnly today) => new
    {
        p.Id, p.Title, p.Goal, p.ConversationId, p.CreatedAt, p.UpdatedAt,
        steps = p.Steps.OrderBy(s => s.Number).Select(s => new
        {
            s.Number, s.Title, s.Notes, s.Owner, s.Status,
            start = s.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), due = s.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.DependsOn,
        }),
        warnings = Warnings(p, today),
        done = p.Steps.Count(s => s.Status == PlanStep.Done),
    };

    public static List<StepChange> Changes(JsonArray? steps) =>
        [.. (steps ?? []).OfType<JsonObject>().Select(s => new StepChange(
            Number: s["number"] is JsonValue n && n.TryGetValue<int>(out var number) ? number : null,
            Title: Str(s, "title"), Notes: Str(s, "notes"), Owner: Str(s, "owner"), Status: Str(s, "status"), Start: Str(s, "start"), Due: Str(s, "due"),
            DependsOn: s["depends_on"] is JsonArray deps ? [.. deps.Select(d => d is JsonValue v && v.TryGetValue<int>(out var x) ? x : 0)] : null,
            Remove: s["remove"] is JsonValue r && r.TryGetValue<bool>(out var remove) && remove))];

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : null;

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// The chat's Plans tool: the model makes a plan (steps, owners, dates, what each waits for), keeps it up to date as the
/// conversation goes, and reads it back. The plan belongs to the chat: in a room, its people see and move it along too.
/// </summary>
public sealed class PlansTool(AppDbContext db, TimeProvider clock) : IChatTool
{
    public const string ToolId = "plans";

    public string Id => ToolId;
    public string Title => "Plans";
    public string Description => "Makes and keeps plans: steps with owners, dates and what each waits for, shown as a board and a timeline.";
    public string Icon => "list-checks";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    private static readonly JsonObject StepSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["number"] = new JsonObject { ["type"] = "integer", ["description"] = "plan_change: the step to change; leave out to add a step" },
            ["title"] = Schema.Text("What is done in it, short"),
            ["owner"] = Schema.Text("Who does it (a person or a team)"),
            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("todo", "doing", "blocked", "done") },
            ["start"] = Schema.Text("YYYY-MM-DD"),
            ["due"] = Schema.Text("YYYY-MM-DD"),
            ["depends_on"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "Numbers of the steps it waits for" },
            ["notes"] = Schema.Text("Details, links, acceptance"),
            ["remove"] = new JsonObject { ["type"] = "boolean", ["description"] = "plan_change: true takes the step out" },
        },
    };

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function("plan_make", "Makes a plan in this chat: a title, its goal, and its steps (numbered 1, 2, ... in the order given).",
                new JsonObject { ["title"] = Schema.Text("The plan's name"), ["goal"] = Schema.Text("What it is for; what done looks like"), ["steps"] = new JsonObject { ["type"] = "array", ["items"] = StepSchema.DeepClone() } },
                "title", "steps"),
            Schema.Function("plan_change", "Changes a plan: its title or goal, and steps (with number: change or remove that step; without: add one). Keep plans up to date as things are decided or done.",
                new JsonObject { ["plan_id"] = Schema.Text("The plan; leave out for this chat's latest"), ["title"] = Schema.Text("New name"), ["goal"] = Schema.Text("New goal"), ["steps"] = new JsonObject { ["type"] = "array", ["items"] = StepSchema.DeepClone() } }),
            Schema.Function("plan_show", "The plan as it is now (people may have moved steps along on its board): steps, statuses, dates, and what does not fit.",
                new JsonObject { ["plan_id"] = Schema.Text("The plan; leave out for this chat's latest") }),
        ],
        "Use the plan tools when the conversation is about doing something in steps (a project, a launch, a migration, a trip): " +
        "plan_make with concrete steps, owners and dates (ask for what you do not know), then plan_change as things change; plan_show before " +
        "answering about where a plan stands. Plans show to the person as a board and a timeline, so do not repeat the whole plan in your answer: say what changed.",
        async (name, args, token) =>
        {
            try
            {
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                Plan plan;
                if (name == "plan_make")
                {
                    plan = new Plan
                    {
                        UserId = context.User.Id, ConversationId = context.Conversation.Id,
                        Title = (Schema.Str(args, "title") ?? "Plan").Trim() is { Length: > 0 } t ? t[..Math.Min(200, t.Length)] : "Plan",
                        Goal = Schema.Str(args, "goal")?.Trim() is { Length: > 0 } g ? g[..Math.Min(4000, g.Length)] : null,
                    };
                    PlanBook.Apply(plan, PlanBook.Changes(args["steps"] as JsonArray).Select(c => c with { Number = null }));
                    db.Plans.Add(plan);
                }
                else
                {
                    plan = await FindAsync(args, context, token) ?? throw new PlanException("This chat has no plan yet: make one with plan_make.");
                    if (name == "plan_change")
                    {
                        if (Schema.Str(args, "title")?.Trim() is { Length: > 0 } t)
                        {
                            plan.Title = t[..Math.Min(200, t.Length)];
                        }
                        if (Schema.Str(args, "goal") is { } g)
                        {
                            plan.Goal = g.Trim().Length == 0 ? null : g.Trim()[..Math.Min(4000, g.Trim().Length)];
                        }
                        PlanBook.Apply(plan, PlanBook.Changes(args["steps"] as JsonArray));
                    }
                }
                await db.SaveChangesAsync(token);
                var shape = System.Text.Json.JsonSerializer.SerializeToNode(PlanBook.Shape(plan, today), System.Text.Json.JsonSerializerOptions.Web)!.AsObject();
                shape["link"] = $"/plans/{plan.Id}";
                return new ToolResult(shape.ToJsonString(Mcp.Plain)) { Details = new JsonObject { ["plan"] = plan.Id.ToString() } };
            }
            catch (PlanException ex)
            {
                return new ToolResult(ex.Message, IsError: true);
            }
        }));

    /// <summary>The plan named, if this chat's (or one its person made); else the chat's latest.</summary>
    private async Task<Plan?> FindAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var chat = context.Conversation.Id;
        if (Schema.Str(args, "plan_id") is { } raw && Guid.TryParse(raw, out var id))
        {
            return await db.Plans.Include(p => p.Steps).SingleOrDefaultAsync(p => p.Id == id && (p.ConversationId == chat || p.UserId == context.User.Id), ct);
        }
        return await db.Plans.Include(p => p.Steps).Where(p => p.ConversationId == chat).OrderByDescending(p => p.UpdatedAt).FirstOrDefaultAsync(ct);
    }
}

/// <summary>The plans' pages: the person's plans and their rooms', one plan, and its steps moved along.</summary>
public static class PlanEndpoints
{
    public static void MapPlans(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/plans").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}/steps/{number:int}", StepAsync);
        g.MapPost("/{id:guid}/steps", AddAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var rooms = await RoomAccess.RoomsOfAsync(db, me, ct);
        var plans = await db.Plans.AsNoTracking().Where(x => x.UserId == me.Id || (x.ConversationId != null && rooms.Contains(x.ConversationId.Value)))
            .OrderByDescending(x => x.UpdatedAt).Take(200)
            .Select(x => new { x.Id, x.Title, x.ConversationId, x.UpdatedAt, steps = x.Steps.Count, done = x.Steps.Count(s => s.Status == PlanStep.Done) }).ToListAsync(ct);
        return Results.Ok(plans);
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await PlanBook.FindAsync(db, id, me, Need.Read, ct) is { } plan ? Results.Ok(PlanBook.Shape(plan, Today(clock))) : Results.NotFound();
    }

    private static async Task<IResult> ChangeAsync(Guid id, StepChange change, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await PlanBook.FindAsync(db, id, me, Need.Write, ct) is not { } plan)
        {
            return Results.NotFound();
        }
        try
        {
            PlanBook.Apply(plan, [change]);
        }
        catch (PlanException ex)
        {
            return AuthEndpoints.Problem(400, "plan", ex.Message);
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(PlanBook.Shape(plan, Today(clock)));
    }

    private static Task<IResult> StepAsync(Guid id, int number, StepChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        ChangeAsync(id, body with { Number = number }, p, users, db, clock, ct);

    private static Task<IResult> AddAsync(Guid id, StepChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        ChangeAsync(id, body with { Number = null, Remove = false }, p, users, db, clock, ct);

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.Plans.Where(x => x.Id == id && x.UserId == me.Id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
    }
}
