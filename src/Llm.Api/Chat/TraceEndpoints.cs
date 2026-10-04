using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A tool used in an answer (its sub-agents' calls too): how many times, and their time together.</summary>
public sealed record TraceToolUse(string Name, int Count, int Ms);

/// <summary>An answer in Admin → Traces: who asked, when, its time and where most of it went; no words.</summary>
public sealed record TraceSummary(Guid Id, DateTimeOffset At, TracePerson? Person, string? Model, string Status, int Ms, int? QueueMs, int Rounds, int ToolCalls, int Agents,
    IReadOnlyList<TraceToolUse> Tools, TraceTokens Tokens, TraceTokens AgentTokens, TraceSlowest? Slowest);

/// <summary>
/// Admin → Traces: the slowest answers of a time range across people, and one answer's trace
/// (also from the chat, for an admin). Times, tokens, sizes and tool names: never what was said.
/// </summary>
public static class TraceEndpoints
{
    public static void MapTraces(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/traces").RequireAuthorization(Endpoints.AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapGet("/{id:guid}", GetAsync);
    }

    /// <summary>The slowest answers that ended in the range (default: the last day), slowest first.</summary>
    private static async Task<IResult> ListAsync(DateTimeOffset? from, DateTimeOffset? to, int? limit, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-1);
        var slowest = await db.ChatMessages.AsNoTracking()
            .Where(m => m.AnswerMs != null && m.CreatedAt >= start && m.CreatedAt < end)
            .OrderByDescending(m => m.AnswerMs).Take(Math.Clamp(limit ?? 50, 1, 200))
            .Select(m => new { m.Id, m.ConversationId })
            .ToListAsync(ct);
        var chats = slowest.Select(s => s.ConversationId).Distinct().ToList();
        var rows = await RowsAsync(db, chats, ct);
        var people = await PeopleAsync(db, chats, ct);
        var answers = slowest.Select(s => AnswerTraces.Assemble(rows[s.ConversationId], s.Id)).OfType<AnswerTrace>()
            .Select(t => Summary(t, people.GetValueOrDefault(t.ConversationId)));
        return Results.Ok(new { from = start, to = end, answers });
    }

    /// <summary>The answer a message belongs to, step by step.</summary>
    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var chat = await db.ChatMessages.AsNoTracking().Where(m => m.Id == id).Select(m => (Guid?)m.ConversationId).SingleOrDefaultAsync(ct);
        if (chat is not { } c || AnswerTraces.Assemble((await RowsAsync(db, [c], ct))[c], id) is not { } trace)
        {
            return Endpoints.AuthEndpoints.Problem(404, "not_found", "There is no answer with that id.");
        }
        trace.Person = (await PeopleAsync(db, [c], ct)).GetValueOrDefault(c);
        return Results.Ok(trace);
    }

    /// <summary>The chats' messages as a trace reads them: their sizes, never their words.</summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<TraceRow>>> RowsAsync(AppDbContext db, List<Guid> chats, CancellationToken ct)
    {
        var rows = await db.ChatMessages.AsNoTracking()
            .Where(m => chats.Contains(m.ConversationId))
            .Select(m => new TraceRow(m.Id, m.ConversationId, m.ParentId, m.Sequence, m.Role, m.ToolName, m.Content.Length, m.AttachmentsJson,
                m.ToolName == "delegate" ? m.DetailsJson : null, m.Role == "assistant" ? m.ContextJson : null, m.Model, m.PromptTokens, m.CachedTokens,
                m.CompletionTokens, m.ThinkingMs, m.DurationMs, m.Status, m.TraceJson, m.AnswerMs, m.CreatedAt))
            .ToListAsync(ct);
        return chats.ToDictionary(c => c, c => (IReadOnlyList<TraceRow>)[.. rows.Where(r => r.ConversationId == c)]);
    }

    private static async Task<Dictionary<Guid, TracePerson>> PeopleAsync(AppDbContext db, List<Guid> chats, CancellationToken ct) =>
        await (from c in db.Conversations.AsNoTracking()
               where chats.Contains(c.Id)
               join u in db.Users.AsNoTracking() on c.UserId equals u.Id
               select new { c.Id, u.UserName, u.DisplayName, UserId = u.Id })
            .ToDictionaryAsync(x => x.Id, x => new TracePerson(x.UserId, x.UserName, x.DisplayName), ct);

    private static TraceSummary Summary(AnswerTrace t, TracePerson? person)
    {
        var calls = t.Steps.Where(s => s.Kind is "tool" or "agents").Select(s => (Name: s.Name ?? s.Label, s.Ms))
            .Concat(t.Steps.SelectMany(s => s.Agents ?? []).SelectMany(a => a.Steps).Select(s => (s.Name, Ms: s.Ms ?? 0)));
        var tools = calls.GroupBy(c => c.Name).Select(g => new TraceToolUse(g.Key, g.Count(), g.Sum(c => c.Ms))).OrderByDescending(u => u.Ms).ToList();
        return new TraceSummary(t.Id, t.At, person, t.Model, t.Status, t.Ms, t.QueueMs, t.Rounds, t.ToolCalls, t.Agents, tools, t.Tokens, t.AgentTokens, t.Slowest);
    }
}
