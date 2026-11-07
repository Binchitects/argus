using System.Globalization;
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

    /// <summary>The code matches a page holds.</summary>
    public const int CodePerPage = 20;

    /// <summary>How near in meaning a symbol must be to be shown at all (1 is the same).</summary>
    public const double MeaningFloor = 0.5;

    public static void MapWiki(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/wiki").PersonOnly().RequirePermission(Core.Access.Permissions.Wiki);
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
    /// meaning; code: Argus's ranked search (the symbol a name means, the file, then the files that hold the words), a page
    /// at a time and narrowed by repo, lang, path, kind (symbol, code, doc) and branch, with what is near in meaning beside
    /// it only when the words find less than a page; data: the tables and columns whose names or comments hold the words.
    /// Each part says why it could not answer rather than failing the rest.
    /// </summary>
    private static async Task<IResult> SearchAsync(string? q, string? @in, string? repo, string? lang, string? path, string? kind, string? branch, int? page,
        ClaimsPrincipal p, UserManager<AppUser> users, KnowledgeSearch search, KnowledgeStore store,
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
        var meaning = new List<object>();
        JsonObject? codePage = null;
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
                var (found, problem) = await argus.SearchAsync(me.Email ?? "", new Dictionary<string, string?>
                {
                    ["q"] = words, ["repo"] = repo, ["lang"] = lang, ["path"] = path, ["kind"] = kind, ["branch"] = branch,
                    ["page"] = Math.Max(1, page ?? 1).ToString(CultureInfo.InvariantCulture), ["per_page"] = CodePerPage.ToString(CultureInfo.InvariantCulture),
                }, ct);
                if (found is null)
                {
                    problems.Add($"The code could not be searched: {problem}");
                }
                else
                {
                    code.AddRange((found["rows"] as JsonArray ?? []).OfType<JsonObject>().Select(Code));
                    codePage = new JsonObject
                    {
                        ["total"] = found["total"]?.DeepClone(), ["page"] = found["page"]?.DeepClone(), ["perPage"] = found["per_page"]?.DeepClone(),
                        ["reach"] = found["reach"]?.DeepClone(), ["facets"] = found["facets"]?.DeepClone(), ["branches"] = found["branches"]?.DeepClone(),
                        ["branch"] = found["branch"]?.DeepClone(),
                    };
                    // Near in meaning only beside a short answer, never ahead of what holds the words: nearest is not always near.
                    if ((page ?? 1) <= 1 && (found["total"]?.GetValue<long>() ?? 0) < CodePerPage && kind is null or "" or "symbol"
                        && repo is null or "" && lang is null or "" && path is null or "")
                    {
                        try
                        {
                            var session = await argus.ConnectAsync(me.Id, me.Email ?? "", ct);
                            meaning.AddRange(Rows(await session.CallAsync("semantic_search", new JsonObject { ["query"] = words, ["branch"] = branch, ["limit"] = 10 }, null, ct))
                                .Where(r => r["score"]?.GetValue<double>() is not { } score || score >= MeaningFloor).Take(10).Select(Meaning));
                        }
                        catch (McpException ex)
                        {
                            problems.Add($"Search by meaning is not running: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is McpException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
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
        return Results.Ok(new { query = words, knowledge, code, codePage, meaning, data = tables.Take(Most), problems });
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

    /// <summary>One of Argus's ranked matches: a symbol, a file of code or a document, and how it matched.</summary>
    private static object Code(JsonObject r) => new
    {
        kind = r["kind"]?.ToString(),
        how = r["how"]?.ToString(),
        repoId = r["repo_id"]?.GetValue<long>(),
        repo = r["repo"]?.ToString(),
        branch = r["branch"]?.ToString(),
        path = r["path"]?.ToString(),
        lang = r["lang"]?.ToString(),
        line = r["line"]?.GetValue<long>(),
        name = r["name"]?.ToString(),
        symbolKind = r["symbol_kind"]?.ToString(),
        scope = r["scope"]?.ToString(),
        doc = r["doc"]?.ToString(),
        snippet = r["snippet"]?.ToString(),
    };

    /// <summary>A symbol near the words in meaning (Argus's semantic_search), with how near.</summary>
    private static object Meaning(JsonObject r) => new
    {
        repoId = r["repo_id"]?.GetValue<long>(),
        repo = r["path_with_namespace"]?.ToString() ?? r["repo"]?.ToString(),
        path = r["path"]?.ToString() ?? r["file"]?.ToString(),
        line = r["line"]?.GetValue<long>(),
        name = r["name"]?.ToString(),
        symbolKind = r["kind"]?.ToString(),
        doc = r["doc"]?.ToString(),
        snippet = (r["signature"] ?? r["snippet"])?.ToString(),
        score = r["score"]?.GetValue<double>() is { } score ? Math.Round(score, 3) : (double?)null,
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
            // index_status has a row per repository and branch: a repository is one row, its default branch first.
            return Results.Ok(Rows(await session.CallAsync("index_status", [], null, ct))
                .GroupBy(r => r["path_with_namespace"]?.ToString() ?? "")
                .Select(g =>
                {
                    var rows = g.OrderByDescending(r => r["branch"]?.ToString() == r["default_branch"]?.ToString()).ThenBy(r => r["branch"]?.ToString(), StringComparer.Ordinal).ToList();
                    var main = rows[0];
                    return new
                    {
                        repoId = main["repo_id"]?.GetValue<long>(), repo = g.Key, files = main["files"]?.GetValue<long>(), symbols = main["symbols"]?.GetValue<long>(),
                        indexedAt = main["last_indexed_at"]?.ToString() ?? main["indexed_at"]?.ToString(),
                        branches = rows.Select(r => new { repoId = r["repo_id"]?.GetValue<long>(), branch = r["branch"]?.ToString(), files = r["files"]?.GetValue<long>() }),
                    };
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
                repo, path, language = file["lang"]?.ToString(), content = file["content"]?.ToString() ?? "", truncated = file["truncated"]?.GetValue<bool>() ?? false,
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
