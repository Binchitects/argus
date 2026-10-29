using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Insights;
using Llm.Api.Knowledge;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Wiki;

/// <summary>
/// The wiki: what the company knows, found and read by people themselves, with no model in between. One search over the
/// knowledge sources' documents (by their words, and by meaning when the embedder runs), the code (Argus's full-text,
/// symbol and meaning searches, as the person, so only what they may read in GitLab), and the data (the tables and
/// columns of the databases they may ask); and to browse: the sources and their documents, a document whole, the
/// repositories and a file of one. Every answer is what this person may read, nothing more.
/// </summary>
public static partial class WikiEndpoints
{
    public const int Most = 30;

    public static void MapWiki(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/wiki").PersonOnly();
        g.MapGet("", HomeAsync);
        g.MapGet("/search", SearchAsync);
        g.MapGet("/documents", DocumentsAsync);
        g.MapGet("/documents/{id:guid}", DocumentAsync);
        g.MapGet("/code/repos", ReposAsync);
        g.MapGet("/code/file", FileAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>What there is: the knowledge sources with the documents this person may read in each, and whether code and data can be searched.</summary>
    private static async Task<IResult> HomeAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, KnowledgeSearch search, KnowledgeStore store,
        ArgusMcp argus, Embedder embedder, AccessService access, Operations.Features features, CancellationToken ct)
    {
        var me = await Me(p, users);
        var counts = await store.CountsAsync(await search.ReadersAsync(me, ct), ct);
        var sources = await db.KnowledgeSources.AsNoTracking().Where(s => counts.Keys.Contains(s.Id)).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Kind, s.SyncedAt }).ToListAsync(ct);
        return Results.Ok(new
        {
            sources = sources.Select(s => new { s.Id, s.Name, s.Kind, s.SyncedAt, documents = counts[s.Id] }),
            meaning = await embedder.ReadyAsync(ct),
            code = argus.Enabled,
            data = features.Runs("data") && (await access.MembershipAsync(me, ct)).Has(Core.Access.Permissions.Data),
        });
    }

    /// <summary>
    /// One search: in=all (default), knowledge, code or data. Knowledge: the passages that hold the words, then those nearest in
    /// meaning; code: Argus's symbols, then its full-text and meaning searches; data: the tables and columns whose names or
    /// comments hold the words. Each part says why it could not answer rather than failing the rest.
    /// </summary>
    private static async Task<IResult> SearchAsync(string? q, string? @in, ClaimsPrincipal p, UserManager<AppUser> users, KnowledgeSearch search, KnowledgeStore store,
        Embedder embedder, ArgusMcp argus, DataQuestions data, AccessService access, Operations.Features features, CancellationToken ct)
    {
        var words = q?.Trim() ?? "";
        if (words.Length is 0 or > 300)
        {
            return AuthEndpoints.Problem(400, "q", "Search for something, up to 300 characters.");
        }
        var where = @in is "knowledge" or "code" or "data" ? @in : "all";
        var me = await Me(p, users);
        var knowledge = new List<object>();
        var code = new List<object>();
        var tables = new List<object>();
        var problems = new List<string>();

        if (where is "all" or "knowledge")
        {
            var readers = await search.ReadersAsync(me, ct);
            var found = await store.KeywordAsync(words, readers, Most, ct);
            if (await embedder.ReadyAsync(ct))
            {
                try
                {
                    var near = await store.SearchAsync(await embedder.QueryAsync(words, ct), readers, Most, ct);
                    found.AddRange(near.Where(n => n.Score > 0.3));
                }
                catch (EmbedderException ex)
                {
                    problems.Add($"Search by meaning is not running: {ex.Message}");
                }
            }
            knowledge.AddRange(found.GroupBy(f => (f.DocumentId, f.Line)).Select(g => g.OrderByDescending(f => f.Score).First())
                .GroupBy(f => f.DocumentId).SelectMany(g => g.OrderByDescending(f => f.Score).Take(2)).OrderByDescending(f => f.Score).Take(Most)
                .Select(f => new { f.DocumentId, f.Title, f.Url, f.Source, f.Heading, f.Line, snippet = Snippet(f.Text, words), score = Math.Round(f.Score, 3) }));
        }

        if (where is "all" or "code" && argus.Enabled)
        {
            try
            {
                var session = await argus.ConnectAsync(me.Id, me.Email ?? "", ct);
                if (Identifier().IsMatch(words))
                {
                    code.AddRange(Rows(await session.CallAsync("find_symbol", new JsonObject { ["name"] = words }, null, ct)).Take(10).Select(r => Code("symbol", r)));
                }
                code.AddRange(Rows(await session.CallAsync("search_code", new JsonObject { ["query"] = words }, null, ct)).Take(15).Select(r => Code("text", r)));
                code.AddRange(Rows(await session.CallAsync("semantic_search", new JsonObject { ["query"] = words, ["limit"] = 10 }, null, ct)).Take(10).Select(r => Code("meaning", r)));
            }
            catch (Exception ex) when (ex is McpException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                problems.Add($"The code could not be searched: {ex.Message}");
            }
        }

        if (where is "all" or "data" && features.Runs("data") && (await access.MembershipAsync(me, ct)).Has(Core.Access.Permissions.Data))
        {
            var terms = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Holds(string? s) => s is not null && terms.All(t => s.Contains(t, StringComparison.OrdinalIgnoreCase));
            foreach (var source in await data.SourcesForAsync(me, ct))
            {
                foreach (var t in DataQuestions.Open(source))
                {
                    var columns = t.Columns.Where(c => Holds(c.Name) || Holds(c.Comment)).Select(c => c.Name).ToList();
                    if (Holds(t.Name) || Holds(t.Comment) || columns.Count > 0 || Holds(source.Name))
                    {
                        tables.Add(new { sourceId = source.Id, source = source.Name, table = t.Name, t.Kind, t.Comment, columns = t.Columns.Select(c => new { c.Name, c.Type, c.Comment }), matched = columns });
                    }
                }
            }
        }
        return Results.Ok(new { query = words, knowledge, code = code.Take(Most), data = tables.Take(Most), problems });
    }

    /// <summary>Argus's answer to a tool call: its result rows.</summary>
    private static IEnumerable<JsonObject> Rows((string Text, bool IsError) answer)
    {
        if (answer.IsError)
        {
            throw new McpException(answer.Text.Length > 300 ? answer.Text[..300] : answer.Text);
        }
        try
        {
            return JsonNode.Parse(answer.Text) switch
            {
                JsonObject o when o["result"] is JsonArray a => a.OfType<JsonObject>(),
                JsonArray a => a.OfType<JsonObject>(),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object Code(string how, JsonObject r) => new
    {
        how,
        repoId = r["repo_id"]?.GetValue<long>(),
        repo = r["path_with_namespace"]?.ToString() ?? r["repo"]?.ToString(),
        path = r["path"]?.ToString() ?? r["file"]?.ToString(),
        line = r["line"]?.GetValue<int>() ?? r["start_line"]?.GetValue<int>(),
        name = r["name"]?.ToString(),
        kind = r["kind"]?.ToString(),
        snippet = (r["snippet"] ?? r["signature"] ?? r["text"])?.ToString(),
    };

    /// <summary>About 300 characters of a passage around the first of the words, whole words at its ends.</summary>
    public static string Snippet(string text, string words)
    {
        var flat = Spaces().Replace(text, " ").Trim();
        var first = words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => flat.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, first - 100);
        var end = Math.Min(flat.Length, start + 300);
        while (start > 0 && start < flat.Length && flat[start - 1] != ' ')
        {
            start--;
        }
        while (end < flat.Length && flat[end] != ' ')
        {
            end++;
        }
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }

    /// <summary>The documents this person may read, of one source or all, by title (their words when given), 50 at a time.</summary>
    private static async Task<IResult> DocumentsAsync(Guid? source, string? q, int? skip, ClaimsPrincipal p, UserManager<AppUser> users, KnowledgeSearch search,
        KnowledgeStore store, CancellationToken ct)
    {
        var me = await Me(p, users);
        var from = Math.Max(0, skip ?? 0);
        var docs = await store.DocumentsAsync(source, await search.ReadersAsync(me, ct), string.IsNullOrWhiteSpace(q) ? null : q.Trim(), from, 51, ct);
        return Results.Ok(new { documents = docs.Take(50), next = docs.Count > 50 ? from + 50 : (int?)null });
    }

    /// <summary>A document whole, as its passages hold it, in order; not found when this person may not read it.</summary>
    private static async Task<IResult> DocumentAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, KnowledgeSearch search, KnowledgeStore store, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await store.ReadAsync(id, await search.ReadersAsync(me, ct), ct) is not { } read)
        {
            return Results.NotFound();
        }
        return Results.Ok(new
        {
            read.Document.Id, read.Document.Title, read.Document.Url, read.Document.SourceId, read.Document.Source, read.Document.SyncedAt,
            passages = read.Passages.Select(c => new { c.Ordinal, c.Heading, c.Line, c.EndLine, c.Text }),
        });
    }

    /// <summary>The repositories this person may read, as Argus indexed them.</summary>
    private static async Task<IResult> ReposAsync(ClaimsPrincipal p, UserManager<AppUser> users, ArgusMcp argus, CancellationToken ct)
    {
        if (!argus.Enabled)
        {
            return Results.Ok(Array.Empty<object>());
        }
        var me = await Me(p, users);
        try
        {
            var session = await argus.ConnectAsync(me.Id, me.Email ?? "", ct);
            return Results.Ok(Rows(await session.CallAsync("index_status", [], null, ct)).Select(r => new
            {
                repoId = r["repo_id"]?.GetValue<long>(), repo = r["path_with_namespace"]?.ToString(), files = r["files"]?.GetValue<long>(), symbols = r["symbols"]?.GetValue<long>(),
                indexedAt = r["last_indexed_at"]?.ToString() ?? r["indexed_at"]?.ToString(),
            }));
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return AuthEndpoints.Problem(502, "argus", $"The code could not be read: {ex.Message}");
        }
    }

    /// <summary>A file of a repository this person may read (Argus's get_file: 64 KB at most).</summary>
    private static async Task<IResult> FileAsync(long repo, string path, ClaimsPrincipal p, UserManager<AppUser> users, ArgusMcp argus, CancellationToken ct)
    {
        if (!argus.Enabled)
        {
            return Results.NotFound();
        }
        var me = await Me(p, users);
        try
        {
            var session = await argus.ConnectAsync(me.Id, me.Email ?? "", ct);
            var (text, error) = await session.CallAsync("get_file", new JsonObject { ["repo_id"] = repo, ["path"] = path }, null, ct);
            if (error || JsonNode.Parse(text) is not JsonObject file)
            {
                return Results.NotFound();
            }
            return Results.Ok(new
            {
                repo, path, language = file["language"]?.ToString(), content = file["content"]?.ToString() ?? "", truncated = file["truncated"]?.GetValue<bool>() ?? false,
            });
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return AuthEndpoints.Problem(502, "argus", $"The code could not be read: {ex.Message}");
        }
        catch (JsonException)
        {
            return Results.NotFound();
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.:]{2,}$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
