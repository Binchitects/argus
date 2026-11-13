using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.ArenaMcp;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>How Code Arena checks its goal: a command or a condition, and whether the person or the agent wrote it down.</summary>
public sealed record CodeGoalCheck(string? Kind, string? Text, string? By);

/// <summary>What the goal may spend: steps (requests to the model), minutes of work, tokens.</summary>
public sealed record CodeGoalBudget(int Steps, int Minutes, long Tokens);

/// <summary>Code Arena's goal, whole, as it sends it each time it changes (GoalRun's shape).</summary>
public sealed record CodeGoal(string? Text, CodeGoalCheck? Check, CodeGoalBudget? Budget, string? Status, int Turns = 0, int Steps = 0, long Tokens = 0,
    long Ms = 0, bool? Passed = null, string? Said = null, DateTimeOffset? CheckedAt = null, string? Outcome = null, DateTimeOffset? Started = null);

/// <summary>
/// Code Arena's goal (goal mode) kept with the chat its session syncs to: Code Arena alone changes it, sending it whole; the
/// web chat shows it above the conversation (what it works toward, how it is checked, what it spent, how it ended). And the
/// person's assistants for Code Arena: a session or a sub-agent works as one.
/// </summary>
public static class CodeArenaGoals
{
    public static void MapCodeArenaGoals(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        g.MapGet("/chats/{id:guid}/goal", ArenaGetAsync);
        g.MapPut("/chats/{id:guid}/goal", PutAsync);
        g.MapDelete("/chats/{id:guid}/goal", DeleteAsync);
        g.MapGet("/assistants", AssistantsAsync);
        app.MapGroup("/api/chat/conversations/{id:guid}/goal").PersonOnly().MapGet("", WebGetAsync);
    }

    private static async Task<(AppUser? Me, IResult? Refusal)> ChatAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return (null, refusal);
        }
        return await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted)
            ? (me, null)
            : (null, AuthEndpoints.Problem(404, "gone", "That chat is not there."));
    }

    /// <summary>The goal as the web chat and Code Arena read it.</summary>
    public static object Shape(ChatGoal g) => new
    {
        text = g.Text,
        check = g.CheckKind is null ? null : new { kind = g.CheckKind, text = g.CheckText, by = g.CheckBy },
        budget = new { steps = g.BudgetSteps, minutes = g.BudgetMinutes, tokens = g.BudgetTokens },
        status = g.Status,
        turns = g.Turns,
        steps = g.Steps,
        tokens = g.Tokens,
        ms = g.WorkedMs,
        passed = g.Passed,
        said = g.Said,
        checkedAt = g.CheckedAt,
        outcome = g.Outcome,
        startedAt = g.StartedAt,
        updatedAt = g.UpdatedAt,
    };

    private static async Task<IResult> ArenaGetAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await ChatAsync(id, http, people, db);
        if (me is null)
        {
            return refusal!;
        }
        return await db.ChatGoals.AsNoTracking().SingleOrDefaultAsync(g => g.ConversationId == id, http.RequestAborted) is { } goal ? Results.Ok(Shape(goal)) : Results.NoContent();
    }

    /// <summary>The web chat's: the goal of one of the person's own chats (none: 204).</summary>
    private static async Task<IResult> WebGetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        return await db.ChatGoals.AsNoTracking().SingleOrDefaultAsync(g => g.ConversationId == id, ct) is { } goal ? Results.Ok(Shape(goal)) : Results.NoContent();
    }

    private static async Task<IResult> PutAsync(Guid id, CodeGoal body, HttpContext http, McpPeople people, AppDbContext db, AccessService access, TimeProvider clock)
    {
        var (me, refusal) = await ChatAsync(id, http, people, db);
        if (me is null)
        {
            return refusal!;
        }
        if (body.Text?.Trim() is not { Length: > 0 and <= ChatGoal.MaxText } text)
        {
            return AuthEndpoints.Problem(400, "text", $"A goal says what it is in 1 to {ChatGoal.MaxText:N0} characters.");
        }
        if (body.Status is not { } status || !ChatGoal.Statuses.Contains(status))
        {
            return AuthEndpoints.Problem(400, "status", $"A goal's status is one of {string.Join(", ", ChatGoal.Statuses)}.");
        }
        if (body.Check is { } check && (check.Kind is not ("command" or "condition") || check.Text?.Trim() is not { Length: > 0 and <= ChatGoal.MaxCheck }))
        {
            return AuthEndpoints.Problem(400, "check", $"A check is a command or a condition of 1 to {ChatGoal.MaxCheck:N0} characters.");
        }
        if (body.Budget is { } b && (b.Steps < 1 || b.Minutes < 1 || b.Tokens < 1))
        {
            return AuthEndpoints.Problem(400, "budget", "A budget's steps, minutes and tokens are more than none.");
        }
        var ct = http.RequestAborted;
        var goal = await db.ChatGoals.SingleOrDefaultAsync(g => g.ConversationId == id, ct);
        if (goal is null)
        {
            goal = new ChatGoal { ConversationId = id, UserId = me.Id, Text = text };
            db.ChatGoals.Add(goal);
        }
        goal.Text = text;
        goal.CheckKind = body.Check?.Kind;
        goal.CheckText = body.Check?.Text?.Trim();
        goal.CheckBy = body.Check?.By is "person" or "agent" ? body.Check.By : null;
        goal.BudgetSteps = body.Budget?.Steps ?? 0;
        goal.BudgetMinutes = body.Budget?.Minutes ?? 0;
        goal.BudgetTokens = body.Budget?.Tokens ?? 0;
        goal.Status = status;
        goal.Turns = Math.Max(0, body.Turns);
        goal.Steps = Math.Max(0, body.Steps);
        goal.Tokens = Math.Max(0, body.Tokens);
        goal.WorkedMs = Math.Max(0, body.Ms);
        goal.Passed = body.Passed;
        goal.Said = Cut(body.Said, ChatGoal.MaxText);
        goal.CheckedAt = body.CheckedAt;
        goal.Outcome = Cut(body.Outcome, ChatGoal.MaxCheck);
        goal.StartedAt = body.Started ?? goal.StartedAt;
        goal.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        // A session in a project: its goal is one of the project's too, its state following the session's.
        await Projects.ProjectWork.MirrorAsync(db, access, me, goal, ct);
        return Results.Ok(new { saved = true });
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await ChatAsync(id, http, people, db);
        if (me is null)
        {
            return refusal!;
        }
        await db.ChatGoals.Where(g => g.ConversationId == id).ExecuteDeleteAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static string? Cut(string? text, int most) => text is null ? null : text.Length <= most ? text : text[..most];

    /// <summary>
    /// The assistants the key's person may use (theirs, shared with them, the company's), whole: Code Arena works as one with its
    /// instructions, model, thinking, the Arena tools it has on and the skills it uses. Its files stay in Arena (the web chat's).
    /// </summary>
    private static async Task<IResult> AssistantsAsync(HttpContext http, McpPeople people, AccessService access, AppDbContext db)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var m = await access.MembershipAsync(me, ct);
        var list = await Assistants.Usable(db.Assistants.AsNoTracking(), me.Id, m).OrderBy(a => a.Name).Take(AssistantEndpoints.MaxAssistants).ToListAsync(ct);
        return Results.Ok(list.Select(a => new
        {
            id = a.Id, name = a.Name, description = a.Description, instructions = a.Instructions, model = a.Model, thinking = a.Thinking, tools = a.Tools,
            skills = a.Skills, icon = a.Icon, color = a.Color, mine = a.UserId == me.Id,
        }));
    }
}
