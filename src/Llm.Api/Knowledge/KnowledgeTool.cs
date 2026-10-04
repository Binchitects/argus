using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Knowledge;

/// <summary>
/// A search of company knowledge as one person: only documents they may read take part. Everyone's,
/// their groups', admins' when they are one, and the GitLab projects they are a member of (by their
/// username; a members list not read for a day no longer counts, as it cannot be checked).
/// </summary>
public sealed class KnowledgeSearch(AppDbContext db, AccessService access, Embedder embedder, KnowledgeStore store, TimeProvider clock)
{
    private static readonly TimeSpan MembersGrace = TimeSpan.FromDays(1);

    /// <summary>The readers a person is, as documents name them.</summary>
    public async Task<List<string>> ReadersAsync(AppUser user, CancellationToken ct)
    {
        var member = await access.MembershipAsync(user, ct);
        var readers = new List<string> { Readers.Everyone };
        if (member.IsAdmin)
        {
            readers.Add(Readers.Admins);
        }
        readers.AddRange(member.Groups.Select(g => "group:" + g));
        if (user.UserName?.Trim().ToLowerInvariant() is { Length: > 0 } name)
        {
            var fresh = clock.GetUtcNow() - MembersGrace;
            readers.AddRange(await db.KnowledgeReaders.AsNoTracking().Where(r => r.People.Contains(name) && r.FetchedAt > fresh).Select(r => r.Key).Distinct().ToListAsync(ct));
        }
        return readers;
    }

    /// <summary>The passages nearest the question that the person may read, best first; at most two of one document.</summary>
    public async Task<List<Passage>> SearchAsync(AppUser user, string query, int limit, CancellationToken ct)
    {
        var readers = await ReadersAsync(user, ct);
        var found = await store.SearchAsync(await embedder.QueryAsync(query, ct), readers, limit * 4, ct);
        return [.. found.GroupBy(p => p.DocumentId).SelectMany(g => g.Take(2)).OrderByDescending(p => p.Score).Take(limit)];
    }
}

/// <summary>
/// Company knowledge, for the chat: search_knowledge finds the passages of the company's documents (GitLab
/// wikis and issues, shared folders, websites) that the person may read, each with its title and link to cite.
/// There when an admin has added a source and the embedder runs.
/// </summary>
public sealed class KnowledgeTool(AppDbContext db, Embedder embedder, KnowledgeSearch search) : IChatTool
{
    public const string Function = "search_knowledge";
    private const int Most = 12;

    public string Id => "knowledge";
    public string Title => "Company knowledge";
    public string Description => "Searches the company's documents you may read (GitLab wikis and issues, shared folders, websites), with links to cite.";
    public string Icon => "library";

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        !await db.KnowledgeSources.AnyAsync(ct) ? "No knowledge sources yet: an admin adds them under Admin → Knowledge."
        : !await embedder.ReadyAsync(ct) ? "The embedder (the embed module) is not running: company knowledge needs it to search."
        : null;

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function(Function, "Searches the company's own documents that the person may read: GitLab wikis and issues, shared folders and websites. " +
            "Returns the passages that best match, each with its document's title, link and section.",
            new JsonObject
            {
                ["query"] = Schema.Text("What to find, as a question or the words the document would use"),
                ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = $"How many passages (default 6, at most {Most})" },
            }, "query")],
        "search_knowledge searches the company's own documents (wikis, issues, shared folders, websites) that the person may read. " +
        "Use it for questions about the company's projects, processes, decisions and documents. Answer from the passages, and cite each one you use " +
        "as [its title](its link), or by its title when it has no link. When it finds nothing, say so: do not guess.",
        async (_, args, token) =>
        {
            var query = (Schema.Str(args, "query") ?? "").Trim();
            if (query.Length == 0)
            {
                return new ToolResult("Say what to find in 'query'.", IsError: true);
            }
            var limit = Math.Clamp(args["limit"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 6, 1, Most);
            List<Passage> found;
            try
            {
                found = await search.SearchAsync(context.User, query, limit, token);
            }
            catch (EmbedderException ex)
            {
                return new ToolResult($"Company knowledge cannot be searched right now: {ex.Message}.", IsError: true);
            }
            return new ToolResult(new JsonObject
            {
                ["query"] = query,
                ["results"] = new JsonArray([.. found.Select(p => (JsonNode)new JsonObject
                {
                    ["title"] = p.Title, ["link"] = p.Url, ["source"] = p.Source, ["section"] = p.Heading, ["passage"] = p.Text, ["score"] = Math.Round(p.Score, 2),
                })]),
                ["note"] = found.Count == 0
                    ? "Nothing the person may read matches. Say so; do not guess."
                    : "Cite each passage you use as [title](link), or by its title when it has no link.",
            }.ToJsonString(Mcp.Plain));
        }));
}
