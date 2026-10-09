using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>
/// A search of company knowledge as one person: only documents they may read take part. Everyone's,
/// their groups', admins' when they are one, the GitLab projects they are a member of (by their
/// username; a members list not read for a day no longer counts, as it cannot be checked), the
/// Confluence spaces and restricted pages they may view (by their email or username; read at each
/// sync, and no longer counted a day after the sync was due), what was shared with them or their
/// directory groups in SharePoint, and what a source's admin chose for them where its permissions
/// cannot tell.
/// </summary>
public sealed class KnowledgeSearch(AppDbContext db, AccessService access, Embedder embedder, KnowledgeStore store, TimeProvider clock,
    IOptionsMonitor<KnowledgeOptions> options)
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
        var name = user.UserName?.Trim().ToLowerInvariant() ?? "";
        var email = user.Email?.Trim().ToLowerInvariant() ?? "";
        if (name.Length > 0 || email.Length > 0)
        {
            // GitLab's members are read every 15 minutes, by username; Confluence's people at each sync of their source, by email or username.
            var now = clock.GetUtcNow();
            var members = now - MembersGrace;
            var synced = now - MembersGrace - options.CurrentValue.SyncEvery;
            readers.AddRange(await db.KnowledgeReaders.AsNoTracking()
                .Where(r => r.Key.StartsWith("gitlab:")
                    ? name.Length > 0 && r.People.Contains(name) && r.FetchedAt > members
                    : ((name.Length > 0 && r.People.Contains(name)) || (email.Length > 0 && r.People.Contains(email))) && r.FetchedAt > synced)
                .Select(r => r.Key).Distinct().ToListAsync(ct));
            readers.AddRange(new[] { name, email }.Where(n => n.Contains('@', StringComparison.Ordinal)).Select(Readers.Person));
        }
        // Their directory groups, as SharePoint names Microsoft Entra groups: by the group's name or ID, as the company sign-in's groups claim
        // does; and the app's groups that stand for one (linked to a directory group, or provisioned by SCIM under the group's name).
        var directory = user.DirectoryGroups.SelectMany(Ldap.LdapDirectory.Names).ToList();
        if (member.Groups.Count > 0)
        {
            directory.AddRange(await db.Groups.AsNoTracking().Where(g => member.Groups.Contains(g.Id) && (g.Directory != null || g.Scim))
                .Select(g => g.Directory ?? g.Name).ToListAsync(ct));
        }
        readers.AddRange(directory.Where(g => g.Trim().Length > 0).Select(Readers.Directory));
        // Where Confluence's or SharePoint's permissions cannot tell, whom each source's admin chose.
        var chosen = await db.KnowledgeSources.AsNoTracking().Where(s => s.Kind == "confluence" || s.Kind == "sharepoint")
            .Select(s => new { s.Id, s.Audience, s.Groups }).ToListAsync(ct);
        readers.AddRange(chosen.Where(s => Readers.For(s.Audience, s.Groups).Intersect(readers).Any()).Select(s => Readers.Chosen(s.Id)));
        return [.. readers.Distinct()];
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
/// wikis and issues, Confluence, SharePoint, shared folders, websites) that the person may read, each with its title and link to cite.
/// There when an admin has added a source and the embedder runs.
/// </summary>
public sealed class KnowledgeTool(AppDbContext db, Embedder embedder, KnowledgeSearch search) : IChatTool
{
    public const string Function = "search_knowledge";
    private const int Most = 12;

    public string Id => "knowledge";
    public string Title => "Company knowledge";
    public string Description => "Searches the company's documents you may read (GitLab wikis and issues, Confluence, SharePoint, shared folders, websites), with links to cite.";
    public string Icon => "library";

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        !await db.KnowledgeSources.AnyAsync(ct) ? "No knowledge sources yet: an admin adds them under Admin → Knowledge."
        : !await embedder.ReadyAsync(ct) ? "The embedder (the embed module) is not running: company knowledge needs it to search."
        : null;

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function(Function, "Searches the company's own documents that the person may read: GitLab wikis and issues, Confluence pages, SharePoint files and pages, shared folders and websites. " +
            "Returns the passages that best match, each with its document's title, link and section.",
            new JsonObject
            {
                ["query"] = Schema.Text("What to find, as a question or the words the document would use"),
                ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = $"How many passages (default 6, at most {Most})" },
            }, "query")],
        "search_knowledge searches the company's own documents (wikis, issues, Confluence, SharePoint, shared folders, websites) that the person may read. " +
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
