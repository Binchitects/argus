using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>
/// What the connectors of other services share: one HTTP client (ServiceConnectors.Client), the source's secret, its
/// readers (whom the admin chose), JSON asked with the service's own authorization, and a failure said in the service's
/// name. Each service is a connector of its own below: GitHub and Gitea, Jira, Notion, S3, feeds, WebDAV.
/// </summary>
public abstract class ServiceConnector(IHttpClientFactory http, IOptions<AuthOptions> auth) : IKnowledgeConnector, IConnectionTest
{
    public const string Client = "knowledge-services";
    public const int MaxDocuments = 20_000;

    public abstract string Kind { get; }

    /// <summary>The service's name in messages.</summary>
    protected abstract string Name { get; }

    /// <summary>What its secret is, for the form; null when it takes none.</summary>
    protected abstract string? SecretIs { get; }

    /// <summary>Whether a source may go without a secret (public repositories, a public feed).</summary>
    protected virtual bool SecretOptional => false;

    public virtual string? Check(KnowledgeSource source) =>
        SecretIs is null || (SecretOptional && string.IsNullOrEmpty(source.SecretEncrypted)) ? null : SourceSecret.Problem(source, auth.Value.DataKey, SecretIs);

    public abstract IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, CancellationToken ct);

    public abstract Task<string> TestAsync(KnowledgeSource source, CancellationToken ct);

    protected HttpClient Http => http.CreateClient(Client);

    protected string? Secret(KnowledgeSource source) => string.IsNullOrEmpty(source.SecretEncrypted) ? null : SourceSecret.Read(source, auth.Value.DataKey);

    protected static List<string> Lines(string text) =>
        [.. text.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    protected static List<string> ReadersOf(KnowledgeSource source) => [Readers.Chosen(source.Id)];

    /// <summary>A request, sent; a failure (refused, missing, unreachable) is a <see cref="KnowledgeException"/> in the service's words.</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await Http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new KnowledgeException($"{Name} could not be reached at {request.RequestUri?.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
        }
        if (res.IsSuccessStatusCode)
        {
            return res;
        }
        var status = (int)res.StatusCode;
        res.Dispose();
        throw new KnowledgeException(status switch
        {
            401 => $"{Name} refused the {SecretIs ?? "request"}: check it.",
            403 => $"{Name} says the account may not read {request.RequestUri?.AbsolutePath}.",
            404 => $"{Name} has nothing at {request.RequestUri?.AbsolutePath}.",
            429 => $"{Name} says too many requests: the next sync tries again.",
            _ => $"{Name} answered {status} for {request.RequestUri?.AbsolutePath}.",
        });
    }

    protected async Task<JsonNode?> JsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var res = await SendAsync(request, ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    protected static string Str(JsonNode? node) => node?.GetValueKind() == System.Text.Json.JsonValueKind.String ? node.GetValue<string>() : node?.ToString() ?? "";
}

/// <summary>
/// GitHub or Gitea repositories: their documentation (Markdown, reStructuredText, AsciiDoc and text files anywhere, and every
/// file under docs/ or doc/ that <see cref="DocumentReader"/> reads) on the default branch, and their issues with their
/// comments (not pull requests). Location: "owner/repo" a line, or "owner" for all of an organisation's or a person's.
/// Account: the API's address (empty: GitHub's; Gitea's is https://gitea.example.com/api/v1). Secret: a token (read only),
/// optional for public repositories.
/// </summary>
public sealed class GitHubConnector(IHttpClientFactory http, IOptions<AuthOptions> auth, DocumentReader reader) : ServiceConnector(http, auth)
{
    public override string Kind => "github";
    protected override string Name => "The code host";
    protected override string? SecretIs => "token";
    protected override bool SecretOptional => true;

    private static readonly string[] DocText = [".md", ".mdx", ".markdown", ".rst", ".adoc", ".asciidoc", ".txt"];

    public override string? Check(KnowledgeSource source) =>
        Lines(source.Location).FirstOrDefault(r => !System.Text.RegularExpressions.Regex.IsMatch(r, @"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)?$")) is { } bad
            ? $"{bad} is not owner/repo or owner."
            : source.Account is { Length: > 0 } a && !Uri.TryCreate(a, UriKind.Absolute, out _) ? "The API's address is a URL (https://gitea.example.com/api/v1), or empty for GitHub."
            : base.Check(source);

    private static string Api(KnowledgeSource s) => (string.IsNullOrWhiteSpace(s.Account) ? "https://api.github.com" : s.Account.Trim()).TrimEnd('/');

    private HttpRequestMessage Get(KnowledgeSource s, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api(s) + path);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("argus-arena-knowledge");
        if (Secret(s) is { } token)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    public override async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct)
    {
        var repos = await ReposAsync(source, ct);
        return $"It reaches {repos.Count} {(repos.Count == 1 ? "repository" : "repositories")}: {string.Join(", ", repos.Take(5).Select(r => r.Full))}{(repos.Count > 5 ? ", ..." : "")}.";
    }

    private sealed record Repo(string Full, string Branch, string? Html);

    private async Task<List<Repo>> ReposAsync(KnowledgeSource s, CancellationToken ct)
    {
        var repos = new List<Repo>();
        foreach (var line in Lines(s.Location))
        {
            if (line.Contains('/'))
            {
                var r = await JsonAsync(Get(s, $"/repos/{line}"), ct);
                repos.Add(new Repo(Str(r?["full_name"]), Str(r?["default_branch"]), Str(r?["html_url"])));
                continue;
            }
            // An organisation's, else a person's, a page of 100 at a time.
            for (var page = 1; page <= 20; page++)
            {
                JsonNode? list;
                try
                {
                    list = await JsonAsync(Get(s, $"/orgs/{line}/repos?per_page=100&page={page}&type=all"), ct);
                }
                catch (KnowledgeException) when (page == 1)
                {
                    list = await JsonAsync(Get(s, $"/users/{line}/repos?per_page=100&page={page}"), ct);
                }
                var rows = (list as JsonArray ?? []).OfType<JsonObject>().Where(r => r["archived"]?.GetValue<bool>() != true).ToList();
                repos.AddRange(rows.Select(r => new Repo(Str(r["full_name"]), Str(r["default_branch"]), Str(r["html_url"]))));
                if (rows.Count < 100)
                {
                    break;
                }
            }
        }
        return repos;
    }

    public override async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var readers = ReadersOf(source);
        var count = 0;
        foreach (var repo in await ReposAsync(source, ct))
        {
            // The default branch's files, all at once.
            var tree = await JsonAsync(Get(source, $"/repos/{repo.Full}/git/trees/{Uri.EscapeDataString(repo.Branch)}?recursive=1"), ct);
            if (tree?["truncated"]?.GetValue<bool>() == true)
            {
                pass.Problems.Add($"{repo.Full} has more files than the host lists at once: some are left out.");
            }
            foreach (var item in (tree?["tree"] as JsonArray ?? []).OfType<JsonObject>().Where(i => Str(i["type"]) == "blob"))
            {
                var path = Str(item["path"]);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var underDocs = path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("doc/", StringComparison.OrdinalIgnoreCase);
                if (!DocText.Contains(ext) && !underDocs || item["size"]?.GetValue<long>() > 20 * 1024 * 1024)
                {
                    continue;
                }
                if (++count > MaxDocuments)
                {
                    pass.Incomplete = true;
                    yield break;
                }
                var key = $"{repo.Full}/{path}";
                var link = repo.Html is { Length: > 0 } h ? $"{h}/blob/{repo.Branch}/{path}" : null;
                yield return new FoundDocument(key, key, link, Str(item["sha"]), readers, async token =>
                {
                    var file = await JsonAsync(Get(source, $"/repos/{repo.Full}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}?ref={Uri.EscapeDataString(repo.Branch)}"), token);
                    var bytes = Convert.FromBase64String(Str(file?["content"]).Replace("\n", "", StringComparison.Ordinal));
                    var read = await reader.ReadAsync(path, bytes, token);
                    return read.Problem is not null && read.Text.Length == 0 ? null : read.Text;
                });
            }
            if (!source.Issues)
            {
                continue;
            }
            for (var page = 1; page <= 50; page++)
            {
                var issues = (await JsonAsync(Get(source, $"/repos/{repo.Full}/issues?state=all&per_page=100&page={page}"), ct) as JsonArray ?? []).OfType<JsonObject>().ToList();
                foreach (var issue in issues.Where(i => i["pull_request"] is null))
                {
                    var number = issue["number"]!.GetValue<long>();
                    var key = $"{repo.Full}/issues/{number}";
                    var title = $"{repo.Full}#{number}: {Str(issue["title"])}";
                    var comments = issue["comments"]?.GetValue<long>() ?? 0;
                    yield return new FoundDocument(key, title, Str(issue["html_url"]), Str(issue["updated_at"]), readers, async token =>
                    {
                        var sb = new StringBuilder($"# {Str(issue["title"])}\n\n{Str(issue["state"])}, by {Str(issue["user"]?["login"])}\n\n{Str(issue["body"])}\n");
                        if (comments > 0)
                        {
                            foreach (var c in (await JsonAsync(Get(source, $"/repos/{repo.Full}/issues/{number}/comments?per_page=100"), token) as JsonArray ?? []).OfType<JsonObject>())
                            {
                                sb.Append("\n## ").Append(Str(c["user"]?["login"])).Append(", ").Append(Str(c["created_at"])).Append("\n\n").Append(Str(c["body"])).Append('\n');
                            }
                        }
                        return sb.ToString();
                    });
                }
                if (issues.Count < 100)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Jira's issues, with their descriptions and comments. Location: the site (https://your-site.atlassian.net, or Data Center's).
/// Account: the email (Cloud, with an API token), or empty (Data Center, a personal access token). Spaces: the project
/// keys to read, comma separated (empty: every project the account may browse).
/// </summary>
public sealed class JiraConnector(IHttpClientFactory http, IOptions<AuthOptions> auth) : ServiceConnector(http, auth)
{
    public override string Kind => "jira";
    protected override string Name => "Jira";
    protected override string? SecretIs => "API token (Cloud) or personal access token (Data Center)";

    public override string? Check(KnowledgeSource source) =>
        !Uri.TryCreate(source.Location.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")
            ? "Give the Jira site's address: https://your-site.atlassian.net, or your Data Center's."
            : base.Check(source);

    private HttpRequestMessage Get(KnowledgeSource s, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, s.Location.Trim().TrimEnd('/') + path);
        req.Headers.Accept.ParseAdd("application/json");
        var token = Secret(s) ?? "";
        req.Headers.Authorization = s.Account is { Length: > 0 } email
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email.Trim()}:{token}")))
            : new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string Jql(KnowledgeSource s) => Lines(s.Spaces ?? "") is { Count: > 0 } keys ? $"project in ({string.Join(",", keys)}) ORDER BY updated DESC" : "ORDER BY updated DESC";

    public override async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct)
    {
        var me = await JsonAsync(Get(source, "/rest/api/2/myself"), ct);
        var found = await JsonAsync(Get(source, $"/rest/api/2/search?jql={Uri.EscapeDataString(Jql(source))}&maxResults=0"), ct);
        return $"Signed in as {Str(me?["displayName"])}: {found?["total"]} issues to read.";
    }

    public override async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var readers = ReadersOf(source);
        var site = source.Location.Trim().TrimEnd('/');
        for (var at = 0; at < MaxDocuments; at += 100)
        {
            var page = await JsonAsync(Get(source, $"/rest/api/2/search?jql={Uri.EscapeDataString(Jql(source))}&startAt={at}&maxResults=100&fields=summary,description,comment,status,updated,issuetype,reporter,assignee"), ct);
            var issues = (page?["issues"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            foreach (var issue in issues)
            {
                var key = Str(issue["key"]);
                var f = issue["fields"];
                var text = new StringBuilder($"# {key}: {Str(f?["summary"])}\n\n{Str(f?["issuetype"]?["name"])}, {Str(f?["status"]?["name"])}; reported by {Str(f?["reporter"]?["displayName"])}")
                    .Append(f?["assignee"] is JsonObject a ? $", assigned to {Str(a["displayName"])}" : "").Append("\n\n").Append(Str(f?["description"])).Append('\n');
                foreach (var c in (f?["comment"]?["comments"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    text.Append("\n## ").Append(Str(c["author"]?["displayName"])).Append(", ").Append(Str(c["created"])).Append("\n\n").Append(Str(c["body"])).Append('\n');
                }
                var body = text.ToString();
                yield return new FoundDocument(key, $"{key}: {Str(f?["summary"])}", $"{site}/browse/{key}", Str(f?["updated"]), readers, _ => Task.FromResult<string?>(body));
            }
            if (issues.Count < 100)
            {
                yield break;
            }
        }
        pass.Incomplete = true;
    }
}

/// <summary>
/// Notion's pages shared with an integration (Settings → Connections → the integration), each with its blocks' text, nested
/// blocks two levels deep. Secret: the integration's secret.
/// </summary>
public sealed class NotionConnector(IHttpClientFactory http, IOptions<AuthOptions> auth) : ServiceConnector(http, auth)
{
    public const string Api = "https://api.notion.com/v1";

    public override string Kind => "notion";
    protected override string Name => "Notion";
    protected override string? SecretIs => "integration secret";

    private HttpRequestMessage Request(KnowledgeSource s, HttpMethod method, string path, JsonObject? body = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret(s) ?? "");
        req.Headers.Add("Notion-Version", "2022-06-28");
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        return req;
    }

    public override async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct)
    {
        var found = await JsonAsync(Request(source, HttpMethod.Post, "/search", new JsonObject { ["page_size"] = 1 }), ct);
        return found?["results"] is JsonArray { Count: > 0 } ? "The integration reaches pages." : "The integration reaches no page yet: share pages with it (… → Connections).";
    }

    public override async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var readers = ReadersOf(source);
        string? cursor = null;
        var count = 0;
        do
        {
            var body = new JsonObject { ["page_size"] = 100, ["filter"] = new JsonObject { ["property"] = "object", ["value"] = "page" } };
            if (cursor is not null)
            {
                body["start_cursor"] = cursor;
            }
            var page = await JsonAsync(Request(source, HttpMethod.Post, "/search", body), ct);
            foreach (var p in (page?["results"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (++count > MaxDocuments)
                {
                    pass.Incomplete = true;
                    yield break;
                }
                var id = Str(p["id"]);
                var title = TitleOf(p) is { Length: > 0 } t ? t : "Untitled";
                yield return new FoundDocument(id, title, Str(p["url"]), Str(p["last_edited_time"]), readers, async token =>
                {
                    var sb = new StringBuilder($"# {title}\n\n");
                    await BlocksAsync(source, id, sb, 0, token);
                    return sb.ToString();
                });
            }
            cursor = page?["has_more"]?.GetValue<bool>() == true ? Str(page["next_cursor"]) : null;
        }
        while (cursor is not null);
    }

    private static string TitleOf(JsonObject page) =>
        (page["properties"] as JsonObject ?? []).Select(p => p.Value).OfType<JsonObject>().FirstOrDefault(v => Str(v["type"]) == "title")?["title"] is JsonArray parts
            ? string.Concat(parts.OfType<JsonObject>().Select(x => Str(x["plain_text"])))
            : "";

    private async Task BlocksAsync(KnowledgeSource s, string id, StringBuilder sb, int depth, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var page = await JsonAsync(Request(s, HttpMethod.Get, $"/blocks/{id}/children?page_size=100{(cursor is null ? "" : "&start_cursor=" + cursor)}"), ct);
            foreach (var b in (page?["results"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var type = Str(b["type"]);
                var text = b[type]?["rich_text"] is JsonArray rich ? string.Concat(rich.OfType<JsonObject>().Select(x => Str(x["plain_text"]))) : "";
                var line = type switch
                {
                    "heading_1" => "# " + text, "heading_2" => "## " + text, "heading_3" => "### " + text,
                    "bulleted_list_item" or "to_do" => "- " + text, "numbered_list_item" => "1. " + text,
                    "code" => $"```\n{text}\n```", "quote" or "callout" => "> " + text, _ => text,
                };
                if (line.Length > 0)
                {
                    sb.Append(new string(' ', depth * 2)).Append(line).Append("\n\n");
                }
                if (b["has_children"]?.GetValue<bool>() == true && depth < 2 && type != "child_page")
                {
                    await BlocksAsync(s, Str(b["id"]), sb, depth + 1, ct);
                }
            }
            cursor = page?["has_more"]?.GetValue<bool>() == true ? Str(page["next_cursor"]) : null;
        }
        while (cursor is not null);
    }
}

/// <summary>
/// RSS and Atom feeds: each item a document (its title, date, link and text). Location: the feeds' addresses, one a line.
/// </summary>
public sealed class FeedConnector(IHttpClientFactory http, IOptions<AuthOptions> auth) : ServiceConnector(http, auth)
{
    public override string Kind => "feed";
    protected override string Name => "The feed";
    protected override string? SecretIs => null;

    public override string? Check(KnowledgeSource source) =>
        Lines(source.Location) is { Count: > 0 } feeds && feeds.All(f => Uri.TryCreate(f, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            ? null : "Give the feeds' addresses, one a line (https://…/feed.xml).";

    public override async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct)
    {
        var n = 0;
        foreach (var feed in Lines(source.Location))
        {
            n += (await ItemsAsync(feed, ct)).Count;
        }
        return $"{n} items in {Lines(source.Location).Count} feed(s).";
    }

    private sealed record Item(string Id, string Title, string? Link, string Updated, string Text);

    private async Task<List<Item>> ItemsAsync(string feed, CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, feed), ct);
        XDocument xml;
        try
        {
            xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (System.Xml.XmlException)
        {
            throw new KnowledgeException($"{feed} is not an RSS or Atom feed.");
        }
        XNamespace atom = "http://www.w3.org/2005/Atom";
        string Plain(string? html) => html is null ? "" : Html.Read($"<html><body>{html}</body></html>").Text;
        var rss = xml.Descendants("item").Select(i => new Item(
            (string?)i.Element("guid") ?? (string?)i.Element("link") ?? (string?)i.Element("title") ?? "", (string?)i.Element("title") ?? "", (string?)i.Element("link"),
            (string?)i.Element("pubDate") ?? "", Plain((string?)i.Element("{http://purl.org/rss/1.0/modules/content/}encoded") ?? (string?)i.Element("description"))));
        var entries = xml.Descendants(atom + "entry").Select(e => new Item(
            (string?)e.Element(atom + "id") ?? "", (string?)e.Element(atom + "title") ?? "", (string?)e.Elements(atom + "link").FirstOrDefault()?.Attribute("href"),
            (string?)e.Element(atom + "updated") ?? "", Plain((string?)e.Element(atom + "content") ?? (string?)e.Element(atom + "summary"))));
        return [.. rss.Concat(entries).Where(i => i.Id.Length > 0)];
    }

    public override async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var readers = ReadersOf(source);
        foreach (var feed in Lines(source.Location))
        {
            List<Item> items;
            try
            {
                items = await ItemsAsync(feed, ct);
            }
            catch (KnowledgeException ex)
            {
                // One feed down: its items are kept as they were.
                pass.Problems.Add(ex.Message);
                pass.Incomplete = true;
                continue;
            }
            foreach (var i in items)
            {
                var text = $"# {i.Title}\n\n{i.Updated}\n\n{i.Text}";
                yield return new FoundDocument($"{feed}#{i.Id}", i.Title, i.Link, $"{i.Updated}:{text.Length}", readers, _ => Task.FromResult<string?>(text));
            }
        }
    }
}

/// <summary>
/// A WebDAV folder (Nextcloud, ownCloud, SharePoint's WebDAV, a NAS): its files and subfolders', read as uploaded files are.
/// Location: the folder's address (Nextcloud: https://cloud.example.com/remote.php/dav/files/USER/Folder). Account: the
/// user; secret: the password or an app password.
/// </summary>
public sealed class WebDavConnector(IHttpClientFactory http, IOptions<AuthOptions> auth, DocumentReader reader) : ServiceConnector(http, auth)
{
    public override string Kind => "webdav";
    protected override string Name => "The WebDAV server";
    protected override string? SecretIs => "password or app password";

    public override string? Check(KnowledgeSource source) =>
        !Uri.TryCreate(source.Location.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") ? "Give the folder's WebDAV address (https://…)."
        : string.IsNullOrWhiteSpace(source.Account) ? "Give the user it signs in as."
        : base.Check(source);

    private HttpRequestMessage Request(KnowledgeSource s, HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Account!.Trim()}:{Secret(s)}")));
        return req;
    }

    private sealed record Entry(string Href, bool Folder, string Version, long Size);

    private async Task<List<Entry>> ListAsync(KnowledgeSource s, string url, CancellationToken ct)
    {
        var req = Request(s, new HttpMethod("PROPFIND"), url);
        req.Headers.Add("Depth", "1");
        req.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getetag/><d:getlastmodified/><d:getcontentlength/></d:prop></d:propfind>",
            Encoding.UTF8, "application/xml");
        using var res = await SendAsync(req, ct);
        XNamespace d = "DAV:";
        var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var self = new Uri(url).AbsolutePath.TrimEnd('/');
        return [.. xml.Descendants(d + "response").Select(r => new Entry(
                (string?)r.Element(d + "href") ?? "",
                r.Descendants(d + "collection").Any(),
                (string?)r.Descendants(d + "getetag").FirstOrDefault() ?? (string?)r.Descendants(d + "getlastmodified").FirstOrDefault() ?? "",
                long.TryParse((string?)r.Descendants(d + "getcontentlength").FirstOrDefault(), CultureInfo.InvariantCulture, out var n) ? n : 0))
            .Where(e => Uri.UnescapeDataString(new Uri(new Uri(url), e.Href).AbsolutePath).TrimEnd('/') != Uri.UnescapeDataString(self))];
    }

    public override async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct) =>
        $"The folder holds {(await ListAsync(source, source.Location.Trim(), ct)).Count} entries.";

    public override async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var readers = ReadersOf(source);
        var root = new Uri(source.Location.Trim().TrimEnd('/') + "/");
        var folders = new Queue<Uri>([root]);
        var count = 0;
        while (folders.Count > 0)
        {
            var folder = folders.Dequeue();
            foreach (var e in await ListAsync(source, folder.ToString(), ct))
            {
                var at = new Uri(folder, e.Href);
                var name = Uri.UnescapeDataString(at.AbsolutePath[root.AbsolutePath.Length..]).TrimEnd('/');
                if (name.Length == 0 || name.Split('/').Any(p => p.StartsWith('.')))
                {
                    continue;
                }
                if (e.Folder)
                {
                    folders.Enqueue(new Uri(at.ToString().TrimEnd('/') + "/"));
                    continue;
                }
                if (e.Size > KnowledgeFile.MaxBytes)
                {
                    continue;
                }
                if (++count > MaxDocuments)
                {
                    pass.Incomplete = true;
                    yield break;
                }
                yield return new FoundDocument(name, name, null, e.Version, readers, async token =>
                {
                    using var res = await SendAsync(Request(source, HttpMethod.Get, at.ToString()), token);
                    var read = await reader.ReadAsync(name, await res.Content.ReadAsByteArrayAsync(token), token);
                    return read.Problem is not null && read.Text.Length == 0 ? null : read.Text;
                });
            }
        }
    }
}

/// <summary>
/// An S3 bucket (Amazon S3, MinIO, Ceph, Wasabi, R2): its objects under a prefix, read as uploaded files are. Location: the
/// endpoint, bucket and prefix, path-style (https://s3.eu-west-1.amazonaws.com/bucket/docs/, https://minio.example.com:9000/bucket/).
/// Account: the access key ID; secret: the secret access key; Tenant: the region (us-east-1 when empty).
/// </summary>
public sealed class S3Connector(IHttpClientFactory http, IOptions<AuthOptions> auth, DocumentReader reader, TimeProvider clock) : ServiceConnector(http, auth)
{
    public override string Kind => "s3";
    protected override string Name => "The bucket";
    protected override string? SecretIs => "secret access key";

    public override string? Check(KnowledgeSource source) =>
        !Uri.TryCreate(source.Location.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.AbsolutePath.Trim('/').Length == 0
            ? "Give the endpoint and the bucket, path-style: https://s3.eu-west-1.amazonaws.com/bucket/prefix/."
            : string.IsNullOrWhiteSpace(source.Account) ? "Give the access key ID." : base.Check(source);

    private sealed record Place(Uri Endpoint, string Bucket, string Prefix);

    private static Place PlaceOf(KnowledgeSource s)
    {
        var u = new Uri(s.Location.Trim());
        var parts = u.AbsolutePath.TrimStart('/').Split('/', 2);
        return new Place(new Uri(u.GetLeftPart(UriPartial.Authority)), parts[0], parts.Length > 1 ? parts[1] : "");
    }

    /// <summary>A request signed with AWS Signature Version 4 (an empty body: GETs only).</summary>
    private HttpRequestMessage Signed(KnowledgeSource s, string path, IReadOnlyDictionary<string, string> query)
    {
        var place = PlaceOf(s);
        var region = string.IsNullOrWhiteSpace(s.Tenant) ? "us-east-1" : s.Tenant.Trim();
        var now = clock.GetUtcNow();
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var canonicalQuery = string.Join('&', query.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        var canonicalPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var host = place.Endpoint.IsDefaultPort ? place.Endpoint.Host : $"{place.Endpoint.Host}:{place.Endpoint.Port}";
        var canonical = $"GET\n{canonicalPath}\n{canonicalQuery}\nhost:{host}\nx-amz-content-sha256:{Empty}\nx-amz-date:{amzDate}\n\nhost;x-amz-content-sha256;x-amz-date\n{Empty}";
        var scope = $"{day}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + Secret(s)), day), region), "s3"), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, toSign));
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(place.Endpoint, canonicalPath + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : "")));
        req.Headers.Add("x-amz-date", amzDate);
        req.Headers.Add("x-amz-content-sha256", Empty);
        req.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={s.Account!.Trim()}/{scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={signature}");
        return req;
    }

    private sealed record S3Object(string Key, string ETag, long Size);

    private async Task<(List<S3Object> Objects, string? Next)> PageAsync(KnowledgeSource s, string? token, CancellationToken ct)
    {
        var place = PlaceOf(s);
        var query = new Dictionary<string, string> { ["list-type"] = "2", ["prefix"] = place.Prefix, ["max-keys"] = "1000" };
        if (token is not null)
        {
            query["continuation-token"] = token;
        }
        using var res = await SendAsync(Signed(s, $"/{place.Bucket}", query), ct);
        var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
        var objects = xml.Descendants(ns + "Contents").Select(c => new S3Object((string?)c.Element(ns + "Key") ?? "", ((string?)c.Element(ns + "ETag") ?? "").Trim('"'),
            (long?)c.Element(ns + "Size") ?? 0)).ToList();
        return (objects, (string?)xml.Root?.Element(ns + "IsTruncated") == "true" ? (string?)xml.Root?.Element(ns + "NextContinuationToken") : null);
    }

    public override async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct)
    {
        var (objects, next) = await PageAsync(source, null, ct);
        return $"The bucket lists {objects.Count}{(next is not null ? "+" : "")} objects under its prefix.";
    }

    public override async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var readers = ReadersOf(source);
        var place = PlaceOf(source);
        string? token = null;
        var count = 0;
        do
        {
            var (objects, next) = await PageAsync(source, token, ct);
            foreach (var o in objects.Where(o => !o.Key.EndsWith('/') && o.Size is > 0 and <= KnowledgeFile.MaxBytes))
            {
                var name = o.Key[place.Prefix.Length..].TrimStart('/');
                if (name.Split('/').Any(p => p.StartsWith('.')))
                {
                    continue;
                }
                if (++count > MaxDocuments)
                {
                    pass.Incomplete = true;
                    yield break;
                }
                yield return new FoundDocument(o.Key, name, null, o.ETag, readers, async t =>
                {
                    using var res = await SendAsync(Signed(source, $"/{place.Bucket}/{o.Key}", new Dictionary<string, string>()), t);
                    var read = await reader.ReadAsync(name, await res.Content.ReadAsByteArrayAsync(t), t);
                    return read.Problem is not null && read.Text.Length == 0 ? null : read.Text;
                });
            }
            token = next;
        }
        while (token is not null);
    }
}
