using Llm.Api.Access;
using Llm.Api.ArenaMcp;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A memory Code Arena keeps: its words, whose (the person's, or the project's), its kind; pinned or not.</summary>
public sealed record ArenaMemoryRequest(string? Text, string? Scope = null, string? Kind = null, string? Project = null, bool? Pinned = null);

/// <summary>What Code Arena asks memory about: its question, its project, and the memories it gave already (not again).</summary>
public sealed record ArenaRecallRequest(string? Query, string? Project = null, List<Guid>? Besides = null, int? Budget = null);

/// <summary>Memories Code Arena kept on the machine, brought into Arena once.</summary>
public sealed record ArenaMemoryImport(List<ArenaMemoryRequest>? Memories);

/// <summary>
/// Code Arena's memory is Arena's (/api/code-arena/memories, with the person's key): the same memories as the chat's, the
/// project's kept by the repository's key (its remote: host/group/name). Its session reads the block (pinned, the
/// company's, the newest) at its start and the memories that matter to each message; remember and recall go here.
/// </summary>
public static class CodeArenaMemory
{
    /// <summary>The most memories one import brings.</summary>
    public const int MostImported = 500;

    public static void MapCodeArenaMemory(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena/memories").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        g.MapGet("", ListAsync);
        g.MapPost("", KeepAsync);
        g.MapDelete("/{id:guid}", ForgetAsync);
        g.MapPost("/recall", RecallAsync);
        g.MapPost("/import", ImportAsync);
    }

    private static object View(Memory m) => new { m.Id, m.Text, m.Scope, m.Kind, project = m.ProjectKey, m.Pinned, m.Uses, m.UpdatedAt, mine = m.Scope != Memory.Company };

    private static string? Project(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    /// <summary>What a session starts with: the prompt's block for the project, and every memory it may read (for /memory).</summary>
    private static async Task<IResult> ListAsync(HttpContext http, McpPeople people, Memories memories, string? project = null)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var key = Project(project);
        var (note, ids) = await memories.NoteAsync(me, ct, key);
        var all = await memories.Readable(me.Id, key).AsNoTracking().OrderByDescending(m => m.Pinned).ThenByDescending(m => m.UpdatedAt).ToListAsync(ct);
        return Results.Ok(new { on = memories.On(me), note, inNote = ids, memories = all.Select(View) });
    }

    private static MemoryFacts Facts(ArenaMemoryRequest body) =>
        new(body.Scope is Memory.Project ? Memory.Project : Memory.Person, body.Kind ?? Memory.Fact, Project(body.Project), Memory.FromCodeArena, null, body.Pinned ?? false);

    private static async Task<IResult> KeepAsync(ArenaMemoryRequest body, HttpContext http, McpPeople people, Memories memories)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (!memories.On(me))
        {
            return AuthEndpoints.Problem(409, "off", "Memory is off for this person (or for everyone here).");
        }
        var (text, problem) = Memories.Check(body.Text);
        var facts = Facts(body);
        if ((problem ??= Memories.Problem(facts)) is { } why)
        {
            return AuthEndpoints.Problem(400, "text", why);
        }
        return await memories.KeepAsync(me.Id, text, facts, http.RequestAborted) is { } kept
            ? Results.Ok(View(kept))
            : AuthEndpoints.Problem(409, "full", $"You keep {Memories.PerPerson} memories already: forget some first.");
    }

    private static async Task<IResult> ForgetAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        return await db.Memories.Where(m => m.Id == id && m.UserId == me.Id && m.Scope != Memory.Company).ExecuteDeleteAsync(http.RequestAborted) > 0
            ? Results.NoContent()
            : Results.NotFound();
    }

    /// <summary>The memories that matter to a question (those not given already), and the note that says them; counted as read.</summary>
    private static async Task<IResult> RecallAsync(ArenaRecallRequest body, HttpContext http, McpPeople people, Memories memories)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var found = await memories.RecallAsync(me, body.Query ?? "", ct, Project(body.Project), body.Besides,
            Math.Clamp(body.Budget ?? Memories.RecallChars, 200, 8_000), most: 25);
        await memories.UsedAsync([.. found.Select(r => r.Memory.Id)], ct);
        return Results.Ok(new
        {
            note = found.Count > 0 ? Memories.QuestionNote(found) : null,
            memories = found.Select(r => new { r.Memory.Id, r.Memory.Text, r.Memory.Kind, r.Memory.Scope, score = Math.Round(r.Score, 3) }),
        });
    }

    /// <summary>Code Arena's memory files brought in: each kept (one said already is that one); how many were new.</summary>
    private static async Task<IResult> ImportAsync(ArenaMemoryImport body, HttpContext http, McpPeople people, Memories memories)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var (kept, skipped) = (0, 0);
        foreach (var item in (body.Memories ?? []).Take(MostImported))
        {
            var (text, problem) = Memories.Check(item.Text);
            var facts = Facts(item) with { Source = Memory.Imported };
            if ((problem ?? Memories.Problem(facts)) is not null || await memories.KeepAsync(me.Id, text, facts, http.RequestAborted) is null)
            {
                skipped++;
                continue;
            }
            kept++;
        }
        return Results.Ok(new { kept, skipped });
    }
}
