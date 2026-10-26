using Llm.Api.Access;
using Llm.Api.ArenaMcp;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Plans;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plans;

/// <summary>A step of Code Arena's plan as it sends it: numbered by Code Arena, with what done means, the files and its check's result.</summary>
public sealed record CodePlanStep(int Number, string Title, string? Status = null, string? Check = null, string[]? Files = null, int[]? DependsOn = null,
    string? Notes = null, string? CheckResult = null, string? CheckOutput = null, DateTimeOffset? CheckedAt = null);

/// <summary>Code Arena's plan, whole: it alone changes it, and sends it each time it does.</summary>
public sealed record CodePlan(string? Title, string? Goal, string? Place, IReadOnlyList<CodePlanStep> Steps);

/// <summary>
/// Code Arena's working plan, kept with the chat its session syncs to: on the Plans board beside the chat's own plans, and
/// read back when a session picks the work up again. Code Arena owns it: the board shows it, and only Code Arena changes it.
/// </summary>
public static class CodeArenaPlans
{
    public static void MapCodeArenaPlans(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena/chats/{id:guid}/plan").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        g.MapGet("", GetAsync);
        g.MapPut("", PutAsync);
        g.MapDelete("", DeleteAsync);
    }

    private static Task<Plan?> PlanOfAsync(AppDbContext db, Guid chat, CancellationToken ct) =>
        db.Plans.Include(p => p.Steps).Where(p => p.ConversationId == chat && p.Source == Plan.FromCode).OrderByDescending(p => p.UpdatedAt).FirstOrDefaultAsync(ct);

    private static async Task<(Guid? Chat, IResult? Refusal)> ChatAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return (null, refusal);
        }
        return await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted)
            ? (id, null)
            : (null, AuthEndpoints.Problem(404, "gone", "That chat is not there."));
    }

    private static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db, TimeProvider clock)
    {
        var (chat, refusal) = await ChatAsync(id, http, people, db);
        if (chat is null)
        {
            return refusal!;
        }
        return await PlanOfAsync(db, id, http.RequestAborted) is { } plan ? Results.Ok(PlanBook.Shape(plan, Today(clock))) : Results.NoContent();
    }

    private static async Task<IResult> PutAsync(Guid id, CodePlan body, HttpContext http, McpPeople people, AppDbContext db, TimeProvider clock)
    {
        var (chat, refusal) = await ChatAsync(id, http, people, db);
        if (chat is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var userId = await db.Conversations.Where(c => c.Id == id).Select(c => c.UserId).SingleAsync(ct);
        var plan = await PlanOfAsync(db, id, ct);
        if (plan is null)
        {
            plan = new Plan { UserId = userId, ConversationId = id, Source = Plan.FromCode };
            db.Plans.Add(plan);
        }

        plan.Title = Cut(body.Title?.Trim(), 200) is { Length: > 0 } t ? t : "Plan";
        plan.Goal = Cut(body.Goal?.Trim(), 4000) is { Length: > 0 } g ? g : null;
        plan.Place = Cut(body.Place?.Trim(), 200) is { Length: > 0 } where ? where : null;
        try
        {
            if (body.Steps.GroupBy(s => s.Number).FirstOrDefault(n => n.Count() > 1 || n.Key < 1) is { } twice)
            {
                throw new PlanException($"Step {twice.Key} is there {(twice.Key < 1 ? "but steps count from 1" : "twice")}.");
            }
            // Each step kept by its number (changed in place: a number is never there twice), those gone taken out.
            var sent = body.Steps.Select(s => s.Number).ToHashSet();
            foreach (var gone in plan.Steps.Where(s => !sent.Contains(s.Number)).ToList())
            {
                plan.Steps.Remove(gone);
                db.PlanSteps.Remove(gone);
            }
            foreach (var s in body.Steps.OrderBy(s => s.Number))
            {
                if (string.IsNullOrWhiteSpace(s.Title))
                {
                    throw new PlanException($"Step {s.Number} needs a title.");
                }
                if (plan.Steps.FirstOrDefault(x => x.Number == s.Number) is not { } step)
                {
                    step = new PlanStep { PlanId = plan.Id, Number = s.Number };
                    plan.Steps.Add(step);
                }
                step.CheckResult = s.CheckResult is "passed" or "failed" ? s.CheckResult : null;
                step.CheckOutput = step.CheckResult is null ? null : Cut(s.CheckOutput, 4000);
                step.CheckedAt = step.CheckResult is null ? null : s.CheckedAt;
            }
            // Every step as Code Arena has it, then checked whole: what each waits for, and no circle.
            PlanBook.Apply(plan, body.Steps.Select(s => new StepChange(Number: s.Number, Title: s.Title, Notes: s.Notes ?? "", Status: s.Status ?? PlanStep.Todo,
                DependsOn: s.DependsOn ?? [], Check: s.Check ?? "", Files: s.Files ?? [])));
        }
        catch (PlanException ex)
        {
            return AuthEndpoints.Problem(400, "plan", ex.Message);
        }
        await db.SaveChangesAsync(ct);
        var shape = System.Text.Json.JsonSerializer.SerializeToNode(PlanBook.Shape(plan, Today(clock)), System.Text.Json.JsonSerializerOptions.Web)!.AsObject();
        shape["link"] = $"/plans/{plan.Id}";
        return Results.Ok(shape);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (chat, refusal) = await ChatAsync(id, http, people, db);
        if (chat is null)
        {
            return refusal!;
        }
        await db.Plans.Where(p => p.ConversationId == id && p.Source == Plan.FromCode).ExecuteDeleteAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
