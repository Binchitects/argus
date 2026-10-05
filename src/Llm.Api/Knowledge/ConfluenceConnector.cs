using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>
/// Confluence spaces, Cloud or Data Center (and Server), read with an account that only reads: their pages (and blog
/// posts, when chosen) as text with their headings, each with its title, link and version, so only what changed is
/// read again. Who may read them mirrors Confluence: who may view the space (its users, and its groups' members) and,
/// for a restricted page, every read restriction on it and its ancestors; people are matched by their email (Cloud,
/// when their profile shows it) or username (Data Center). Where Confluence does not tell (a space whose permissions
/// the account cannot see, someone whose email is hidden, a group whose members cannot be listed), whom the admin
/// chose reads instead, and the admin page says so. Everything is read again at each sync.
/// </summary>
public sealed partial class ConfluenceConnector(IHttpClientFactory http, IOptions<AuthOptions> auth, AppDbContext db, TimeProvider clock)
    : IKnowledgeConnector, IConnectionTest
{
    public const string Client = "confluence";
    private const int PerPage = 50;
    /// <summary>Pages of one space's listing at most: 10,000 pages.</summary>
    private const int MaxListing = 200;
    /// <summary>Pages of a group's members at most: 20,000 people.</summary>
    private const int MaxMembers = 100;
    private const string Secret = "API token (Cloud) or personal access token (Data Center)";

    public string Kind => "confluence";

    public string? Check(KnowledgeSource source) =>
        !Uri.TryCreate(source.Location.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")
            ? "Give the Confluence site's address: https://your-site.atlassian.net, or your Data Center's, https://confluence.example.com."
        : source.Account is { Length: > 0 } email && !email.Contains('@', StringComparison.Ordinal) ? "The account's email is an email address (Cloud), or empty (Data Center)."
        : KeysOf(source).FirstOrDefault(k => !SpaceKey().IsMatch(k)) is { } bad ? $"{bad} is not a space key: they are letters and digits, like ENG."
        : SourceSecret.Problem(source, auth.Value.DataKey, Secret);

    /// <summary>The space keys an admin listed; none: every space the account may read.</summary>
    public static List<string> KeysOf(KnowledgeSource source) =>
        [.. (source.Spaces ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>Where the API is and how to sign in: Cloud with an email and API token (Basic), Data Center with a personal access token (Bearer).</summary>
    private sealed record Site(string Root, string Origin, string Path, bool Cloud, AuthenticationHeaderValue Auth);

    private Site SiteOf(KnowledgeSource source)
    {
        var token = SourceSecret.Read(source, auth.Value.DataKey) ?? throw new KnowledgeException(SourceSecret.Problem(source, auth.Value.DataKey, Secret)!);
        var root = source.Location.Trim().TrimEnd('/');
        var cloud = source.Account is { Length: > 0 };
        // Cloud's Confluence is under /wiki; Data Center's address may have its own path (/confluence).
        if (cloud && !root.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase))
        {
            root += "/wiki";
        }
        var u = new Uri(root);
        return new Site(root, u.GetLeftPart(UriPartial.Authority), u.AbsolutePath.TrimEnd('/'), cloud, cloud
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.Account!.Trim()}:{token}")))
            : new AuthenticationHeaderValue("Bearer", token));
    }

    /// <summary>People resolved from Confluence's users and groups: who they are here, everyone (anonymous access), and what could not be matched.</summary>
    private sealed class Resolved
    {
        public SortedSet<string> People { get; } = new(StringComparer.Ordinal);
        /// <summary>Each person once (by their first name in <see cref="People"/>), to count them.</summary>
        public HashSet<string> Persons { get; } = new(StringComparer.Ordinal);
        public bool Everyone { get; set; }
        /// <summary>People Confluence names without an email or username to match (Cloud hides emails unless a profile shows it).</summary>
        public int Hidden { get; set; }
        /// <summary>Groups whose members could not be listed.</summary>
        public SortedSet<string> Groups { get; } = new(StringComparer.Ordinal);

        public bool Whole => Hidden == 0 && Groups.Count == 0;
    }

    /// <summary>What one sync learnt of people, kept for every space it reads: a group's members are listed once.</summary>
    private sealed class People
    {
        public Dictionary<string, (List<List<string>> Who, int Hidden)?> Groups { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject?> Restrictions { get; } = new(StringComparer.Ordinal);
    }

    public async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var site = SiteOf(source);
        var spaces = await SpacesAsync(site, source, pass, ct);
        var rows = await db.KnowledgeReaders.Where(r => r.SourceId == source.Id).ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, ct);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var people = new People();
        var chosen = Readers.Chosen(source.Id);
        var types = source.BlogPosts ? new[] { "page", "blogpost" } : ["page"];
        // A space that could not be read keeps its documents and its readers as they were.
        var kept = KeysOf(source).Where(k => spaces.All(s => s.Key != k)).ToList();
        kept.ForEach(k => pass.Unchanged.Add(k + "/"));
        foreach (var space in spaces)
        {
            var viewers = await ViewersAsync(site, space.Key, people, ct);
            string[] readers;
            if (viewers is null)
            {
                readers = [chosen];
                pass.Mirror.Add($"{space.Key}: Confluence does not show the account who may view this space, so the people you chose read it.");
            }
            else
            {
                var key = Row(rows, used, source.Id, Space(space.Key), $"{space.Key} · {space.Name}", viewers.People);
                readers = [key, .. viewers.Everyone ? new[] { Readers.Everyone } : [], .. viewers.Whole ? [] : new[] { chosen }];
                pass.Mirror.Add(viewers.Whole
                    ? $"{space.Key}: mirrored from Confluence, {Count(viewers.Persons.Count, "person", "people")}{(viewers.Everyone ? " and anyone it lets in anonymously" : "")}."
                    : $"{space.Key}: mirrored from Confluence, {Count(viewers.Persons.Count, "person", "people")}; {Unmatched(viewers)}, so the people you chose read it too.");
            }
            await db.SaveChangesAsync(ct);
            var items = new List<JsonObject>();
            var whole = true;
            foreach (var type in types)
            {
                var cql = Uri.EscapeDataString($"space=\"{space.Key}\" and type={type}");
                var (found, status, capped) = await ListAsync(site,
                    $"content/search?cql={cql}&expand=version,ancestors,restrictions.read.restrictions.user,restrictions.read.restrictions.group&limit={PerPage}", MaxListing, ct);
                if (found is null || capped)
                {
                    whole = false;
                    pass.Problems.Add(capped ? $"{space.Key}: only its first {MaxListing * PerPage:N0} {type}s are read" : $"{space.Key}: its {type}s could not be listed ({Said(status)})");
                }
                items.AddRange(found ?? []);
            }
            if (!whole)
            {
                // What was listed is read; what was not stays as it was.
                kept.Add(space.Key);
                pass.Unchanged.Add(space.Key + "/");
            }
            var byId = items.GroupBy(i => Str(i, "id")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var restricted = new Resolved();
            var unreadable = 0;
            foreach (var item in items)
            {
                var id = Str(item, "id");
                var type = Str(item, "type");
                // Every read restriction on the page and its ancestors applies: a reader must pass each.
                var requires = new List<string>();
                var known = true;
                foreach (var holder in (item["ancestors"] as JsonArray ?? []).OfType<JsonObject>().Select(a => Str(a, "id")).Append(id))
                {
                    // Listed with the page (an ancestor that is not a page, like a folder, is asked for itself).
                    var who = (byId.TryGetValue(holder, out var listed) ? ReadRestriction(listed) : null) ?? await RestrictionAsync(site, holder, people, ct);
                    if (who is null)
                    {
                        known = false;
                        break;
                    }
                    if (Any(who))
                    {
                        var key = RowKey(space.Key, holder);
                        if (!used.Contains(key))
                        {
                            var resolved = new Resolved();
                            await AddAsync(site, who, resolved, people, ct);
                            Row(rows, used, source.Id, key, $"{Title(byId.GetValueOrDefault(holder)) ?? holder} ({space.Key}, restricted)", resolved.People);
                            restricted.Hidden += resolved.Hidden;
                            restricted.Groups.UnionWith(resolved.Groups);
                        }
                        requires.Add(key);
                    }
                }
                if (!known)
                {
                    // Its restrictions cannot be told: it is not read, so nobody reads more than Confluence lets them.
                    unreadable++;
                    continue;
                }
                var title = Title(item) ?? id;
                var url = Link(site, item);
                var version = item["version"]?["number"]?.ToString() ?? "";
                yield return new FoundDocument($"{space.Key}/{type}/{id}", type == "blogpost" ? $"{title} ({space.Name} blog)" : $"{title} ({space.Name})", url, version, readers,
                    token => TextAsync(site, id, title, token), requires);
            }
            await db.SaveChangesAsync(ct);
            if (unreadable > 0)
            {
                pass.Problems.Add($"{space.Key}: {Count(unreadable, "page is", "pages are")} left out, as the restrictions above them could not be read");
            }
            if (!restricted.Whole)
            {
                pass.Mirror.Add($"{space.Key}: in page restrictions, {Unmatched(restricted)}: they do not find those pages.");
            }
        }
        // Spaces no longer read, and restrictions lifted: their readers go (their documents go, as they were not seen).
        db.KnowledgeReaders.RemoveRange(rows.Values.Where(r => !used.Contains(r.Key) && !kept.Any(k => r.Key == Space(k) || r.Key.StartsWith(Space(k) + "/", StringComparison.Ordinal))));
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct)
    {
        var site = SiteOf(source);
        var (status, me) = await GetAsync(site, "user/current", ct);
        if (status != 200 || me is not JsonObject account)
        {
            throw new KnowledgeException(Refused(status, "say who the account is"));
        }
        if (Str(account, "type") == "anonymous")
        {
            throw new KnowledgeException(site.Cloud
                ? "Confluence took the account as anonymous: check the email and API token (id.atlassian.com → Security → API tokens)."
                : "Confluence took the account as anonymous: check the personal access token.");
        }
        var name = Str(account, "displayName") is { Length: > 0 } n ? n : Str(account, "username");
        var pass = new SyncPass();
        var spaces = await SpacesAsync(site, source, pass, ct);
        if (spaces.Count == 0)
        {
            throw new KnowledgeException(pass.Problems.Count > 0 ? string.Join("; ", pass.Problems) : $"Signed in as {name}, but the account may read no space.");
        }
        var first = spaces[0];
        var viewers = await ViewersAsync(site, first.Key, new People(), ct);
        var text = new StringBuilder($"Signed in to Confluence {(site.Cloud ? "Cloud" : "Data Center")} as {name}. ")
            .Append(CultureInfo.InvariantCulture, $"It may read {Count(spaces.Count, "space", "spaces")}: {string.Join(", ", spaces.Take(10).Select(s => s.Key))}{(spaces.Count > 10 ? "…" : "")}. ")
            .Append(viewers is null
                ? $"Confluence does not show it who may view {first.Key}: the people you choose read that."
                : $"Who may view {first.Key} is mirrored: {Count(viewers.Persons.Count, "person", "people")}{(viewers.Whole ? "" : $" ({Unmatched(viewers)})")}.");
        if (pass.Problems.Count > 0)
        {
            text.Append(' ').Append(string.Join("; ", pass.Problems)).Append('.');
        }
        return text.ToString();
    }

    private sealed record SpaceRef(string Key, string Name);

    /// <summary>The spaces listed (each checked), or every global space the account may read. Throws when Confluence refuses the account.</summary>
    private async Task<List<SpaceRef>> SpacesAsync(Site site, KnowledgeSource source, SyncPass pass, CancellationToken ct)
    {
        var keys = KeysOf(source);
        if (keys.Count == 0)
        {
            var (all, status, capped) = await ListAsync(site, "space?type=global&limit=100", 50, ct);
            if (all is null)
            {
                throw new KnowledgeException(Refused(status, "list its spaces"));
            }
            if (capped)
            {
                pass.Incomplete = true;
                pass.Problems.Add("Only the first 5,000 spaces are read");
            }
            return [.. all.Where(s => Str(s, "status") is "" or "current").Select(s => new SpaceRef(Str(s, "key"), Str(s, "name"))).Where(s => s.Key.Length > 0)];
        }
        var spaces = new List<SpaceRef>();
        foreach (var key in keys)
        {
            var (status, body) = await GetAsync(site, $"space/{Uri.EscapeDataString(key)}", ct);
            if (status == 401)
            {
                throw new KnowledgeException(Refused(status, "read its spaces"));
            }
            if (status == 200 && body is JsonObject space)
            {
                spaces.Add(new SpaceRef(key, Str(space, "name") is { Length: > 0 } name ? name : key));
                continue;
            }
            pass.Problems.Add(status is 403 or 404 ? $"{key}: there is no such space, or the account may not view it" : $"{key}: it could not be read ({Said(status)})");
        }
        if (spaces.Count == 0 && keys.Count > 0 && pass.Problems.Count == keys.Count)
        {
            throw new KnowledgeException(string.Join("; ", pass.Problems) + ".");
        }
        return spaces;
    }

    /// <summary>Who may view the space, from its permissions; null when Confluence does not show them to the account.</summary>
    private async Task<Resolved?> ViewersAsync(Site site, string key, People people, CancellationToken ct)
    {
        var (status, body) = await GetAsync(site, $"space/{Uri.EscapeDataString(key)}?expand=permissions", ct);
        if (status != 200 || body?["permissions"] is not JsonArray permissions)
        {
            return null;
        }
        var viewers = new Resolved();
        foreach (var p in permissions.OfType<JsonObject>().Where(p => p["operation"] is JsonObject op && Str(op, "operation") == "read" && Str(op, "targetType") == "space"))
        {
            if (Flag(p, "anonymousAccess") || Flag(p, "unlicensedAccess"))
            {
                viewers.Everyone = true;
            }
            if (p["subjects"] is JsonObject who)
            {
                await AddAsync(site, who, viewers, people, ct);
            }
        }
        // Nobody at all (not even the account itself) is not how a space is: its permissions were not shown.
        return viewers.People.Count == 0 && !viewers.Everyone && viewers.Whole ? null : viewers;
    }

    /// <summary>The users and groups of a page's read restriction as it was listed with it; null when the listing left them out.</summary>
    private static JsonObject? ReadRestriction(JsonObject item) => item["restrictions"]?["read"]?["restrictions"] as JsonObject;

    /// <summary>The users and groups of a page's read restriction, asked for by itself; null when it cannot be read.</summary>
    private async Task<JsonObject?> RestrictionAsync(Site site, string id, People people, CancellationToken ct)
    {
        if (!people.Restrictions.TryGetValue(id, out var restriction))
        {
            var (status, body) = await GetAsync(site, $"content/{Uri.EscapeDataString(id)}/restriction/byOperation/read", ct);
            people.Restrictions[id] = restriction = status == 200 ? body?["restrictions"] as JsonObject : null;
        }
        return restriction;
    }

    /// <summary>The users and groups of a permission or restriction, as people: each user, and every member of each group.</summary>
    private async Task AddAsync(Site site, JsonObject subjects, Resolved into, People people, CancellationToken ct)
    {
        foreach (var user in Results(subjects["user"]))
        {
            Add(into, Who(user, site.Cloud));
        }
        foreach (var group in Results(subjects["group"]))
        {
            var name = Str(group, "name");
            var id = Str(group, "id");
            var key = id.Length > 0 ? id : name;
            if (key.Length == 0)
            {
                continue;
            }
            if (!people.Groups.TryGetValue(key, out var members))
            {
                var path = site.Cloud && id.Length > 0 ? $"group/{Uri.EscapeDataString(id)}/membersByGroupId?limit=200"
                    : site.Cloud ? $"group/member?name={Uri.EscapeDataString(name)}&limit=200"
                    : $"group/{Uri.EscapeDataString(name)}/member?limit=200";
                var (found, _, capped) = await ListAsync(site, path, MaxMembers, ct);
                people.Groups[key] = members = found is null || capped ? null
                    : ([.. found.Select(u => Who(u, site.Cloud)).Where(w => w.Count > 0)], found.Count(u => Who(u, site.Cloud).Count == 0));
            }
            if (members is not { } m)
            {
                into.Groups.Add(name.Length > 0 ? name : key);
                continue;
            }
            m.Who.ForEach(w => Add(into, w));
            into.Hidden += m.Hidden;
        }
    }

    private static void Add(Resolved into, List<string> who)
    {
        if (who.Count == 0)
        {
            into.Hidden++;
            return;
        }
        into.People.UnionWith(who);
        into.Persons.Add(who[0]);
    }

    /// <summary>A Confluence person as people here are matched: by their email (Cloud shows it when their profile lets it), and their username (Data Center).</summary>
    private static List<string> Who(JsonObject user, bool cloud)
    {
        if (Str(user, "type") is "anonymous" or "app")
        {
            return [];
        }
        var who = new List<string>();
        if (Str(user, "email").Trim() is { Length: > 0 } email && email.Contains('@', StringComparison.Ordinal))
        {
            who.Add(email.ToLowerInvariant());
        }
        if (!cloud && Str(user, "username").Trim() is { Length: > 0 } name)
        {
            who.Add(name.ToLowerInvariant());
        }
        return who;
    }

    /// <summary>The page's text: its storage format read as text, under its title.</summary>
    private async Task<string?> TextAsync(Site site, string id, string title, CancellationToken ct)
    {
        var (status, body) = await GetAsync(site, $"content/{Uri.EscapeDataString(id)}?expand=body.storage", ct);
        return status == 200 && body?["body"]?["storage"]?["value"] is JsonValue v && v.TryGetValue<string>(out var storage)
            ? $"# {title}\n\n{ConfluenceStorage.Text(storage)}"
            : null;
    }

    /// <summary>A readers row of this source, made or brought up to date with these people now; its key.</summary>
    private string Row(Dictionary<string, KnowledgeReaders> rows, HashSet<string> used, Guid source, string key, string name, IEnumerable<string> people)
    {
        if (!rows.TryGetValue(key, out var row))
        {
            row = new KnowledgeReaders { SourceId = source, Key = key };
            db.KnowledgeReaders.Add(row);
            rows[key] = row;
        }
        row.Name = name.Length > 500 ? name[..500] : name;
        row.People = [.. people];
        row.FetchedAt = clock.GetUtcNow();
        used.Add(key);
        return key;
    }

    /// <summary>Who may view a space, as its documents name it.</summary>
    private static string Space(string key) => "confluence:" + key;

    /// <summary>A restricted page's readers, as the pages under it name them (a hash when the key would be too long to keep).</summary>
    private static string RowKey(string space, string page) =>
        $"{Space(space)}/{page}" is { Length: <= 100 } key ? key : $"{Space(space)[..Math.Min(Space(space).Length, 60)]}/{Readers.Hash(space + "/" + page)[..32]}";

    private static bool Any(JsonObject who) => Results(who["user"]).Any() || Results(who["group"]).Any();

    private static IEnumerable<JsonObject> Results(JsonNode? list) => (list?["results"] as JsonArray ?? []).OfType<JsonObject>();

    private static string? Title(JsonObject? item) => item is not null && Str(item, "title") is { Length: > 0 } t ? t : null;

    /// <summary>Where people open it: the site and the page's web link.</summary>
    private static string? Link(Site site, JsonObject item) =>
        item["_links"]?["webui"] is JsonValue v && v.TryGetValue<string>(out var web) && web.StartsWith('/') ? site.Root + web : null;

    private static string Unmatched(Resolved r) => string.Join(" and ", new[]
    {
        r.Hidden > 0 ? $"{Count(r.Hidden, "person", "people")} could not be matched (no email{(r.Hidden == 1 ? " or username" : "s or usernames")} shown)" : null,
        r.Groups.Count > 0 ? $"the members of {string.Join(", ", r.Groups.Take(5))}{(r.Groups.Count > 5 ? "…" : "")} could not be listed" : null,
    }.Where(x => x is not null));

    /// <summary>Every item of a listing, following Confluence's next links; null (with its status) when a page of it could not be read; capped when there were more.</summary>
    private async Task<(List<JsonObject>? Items, int Status, bool Capped)> ListAsync(Site site, string path, int most, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        for (var (next, n) = ((string?)path, 0); next is not null; n++)
        {
            if (n == most)
            {
                return (items, 200, true);
            }
            var (status, body) = await GetAsync(site, next, ct);
            if (status != 200 || body?["results"] is not JsonArray results)
            {
                return (null, status, false);
            }
            items.AddRange(results.OfType<JsonObject>());
            next = body["_links"]?["next"] is JsonValue v && v.TryGetValue<string>(out var link) && link.Length > 0 ? link : null;
        }
        return (items, 200, false);
    }

    /// <summary>A GET of the REST API as JSON, with Confluence's status: 0 when it cannot be reached (or a link points elsewhere: the token stays on the site).</summary>
    private async Task<(int Status, JsonNode? Body)> GetAsync(Site site, string path, CancellationToken ct)
    {
        var url = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? path
            // A next link is from the site's root (Cloud: /rest/api/… under /wiki; or /wiki/rest/api/…).
            : path.StartsWith('/') ? (site.Path.Length > 0 && path.StartsWith(site.Path + "/", StringComparison.OrdinalIgnoreCase) ? site.Origin + path : site.Root + path)
            : $"{site.Root}/rest/api/{path}";
        if (!url.StartsWith(site.Origin + "/", StringComparison.OrdinalIgnoreCase))
        {
            return (0, null);
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = site.Auth;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.CreateClient(Client).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return ((int)response.StatusCode, response.IsSuccessStatusCode ? Parse(text) : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (0, null);
        }
    }

    private static string Refused(int status, string what) => status switch
    {
        0 => "Confluence could not be reached: check the site's address.",
        401 => "Confluence refused the account (401): check the email and API token (Cloud), or the personal access token (Data Center).",
        403 => $"Confluence did not let the account {what} (403).",
        404 => "There is no Confluence at that address (404): Cloud's is https://your-site.atlassian.net.",
        _ => $"Confluence answered {status} when asked to {what}.",
    };

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Flag(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";

    private static string Said(int status) => status == 0 ? "it could not be reached" : $"Confluence answered {status.ToString(CultureInfo.InvariantCulture)}";

    [GeneratedRegex(@"^~?[A-Za-z0-9_\-:.]{1,255}$")]
    private static partial Regex SpaceKey();
}

/// <summary>
/// Confluence's storage format (XHTML with its own ac: and ri: elements) as text: headings as Markdown's, so passages
/// keep their section; lists and table rows; code blocks fenced; macros' settings and pictures left out.
/// </summary>
public static partial class ConfluenceStorage
{
    public static string Text(string storage)
    {
        var html = CodeMacro().Replace(storage, m => "<pre>\n```\n" + WebUtility.HtmlEncode(m.Groups[1].Value) + "\n```\n</pre>");
        html = Cdata().Replace(html, m => WebUtility.HtmlEncode(m.Groups[1].Value));
        html = Parameter().Replace(html, " ");
        // A link to another page with no text of its own shows the page's title.
        html = PageLink().Replace(html, m => " " + m.Groups[1].Value + " ");
        return Html.Read("<html><body>" + html + "</body></html>").Text;
    }

    [GeneratedRegex(@"<ac:structured-macro\b[^>]*\bac:name=""code""[^>]*>.*?<ac:plain-text-body>\s*<!\[CDATA\[(.*?)\]\]>\s*</ac:plain-text-body>.*?</ac:structured-macro>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CodeMacro();

    [GeneratedRegex(@"<!\[CDATA\[(.*?)\]\]>", RegexOptions.Singleline)]
    private static partial Regex Cdata();

    [GeneratedRegex(@"<ac:parameter\b[^>]*>.*?</ac:parameter>|<ac:parameter\b[^>]*/>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Parameter();

    [GeneratedRegex(@"<ac:link\b[^>]*>\s*<ri:page\b[^>]*\bri:content-title=""([^""]*)""[^>]*/>\s*</ac:link>", RegexOptions.IgnoreCase)]
    private static partial Regex PageLink();
}
