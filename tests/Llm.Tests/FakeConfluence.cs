using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace Llm.Tests;

/// <summary>
/// Confluence's REST API for company knowledge: Data Center at https://confluence.test (a personal access token,
/// Bearer) and Cloud at https://acme.atlassian.net/wiki (an email and API token, Basic). Spaces with who may view
/// them, pages and blog posts with versions, ancestors and read restrictions, groups with members. Listings come
/// two at a time, so their next links are followed.
/// </summary>
public sealed partial class FakeConfluence : HttpMessageHandler
{
    public const string Token = "confluence-pat-for-tests";
    public const string CloudEmail = "bot@acme.test";
    public const string CloudToken = "confluence-api-token-for-tests";

    public sealed record Person(string Username, string Email, bool EmailHidden = false)
    {
        public string AccountId => "acc-" + Username;
    }

    public sealed class Space
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public List<string> Users { get; } = [];
        public List<string> Groups { get; } = [];
        /// <summary>False: the space's permissions are not in its answer (as when the account may not see them).</summary>
        public bool PermissionsShown { get; set; } = true;
    }

    public sealed class Content
    {
        public required string Id { get; init; }
        public required string Space { get; init; }
        public required string Title { get; set; }
        public required string Body { get; set; }
        public string Type { get; init; } = "page";
        public int Version { get; set; } = 1;
        public string? Parent { get; init; }
        public List<string> RestrictUsers { get; } = [];
        public List<string> RestrictGroups { get; } = [];
    }

    public List<Person> People { get; } = [];
    public Dictionary<string, List<string>> Groups { get; } = new(StringComparer.Ordinal);
    public List<Space> Spaces { get; } = [];
    public List<Content> Contents { get; } = [];
    public List<(string Method, string PathAndQuery, string? Authorization)> Calls { get; } = [];
    /// <summary>Set: the content listing answers 500 from its second page on (a sync that fails half way).</summary>
    public bool FailSecondPage { get; set; }

    /// <summary>
    /// ENG (Engineering): alice directly, and the group eng-team (dave, carol is not in it). Its pages: Deploy (how to roll back),
    /// Onboarding, a Leads page restricted to the group leads (alice) with a child under it, and a blog post. HR: only bob; Leave policy.
    /// </summary>
    public FakeConfluence()
    {
        People.AddRange([new("alice", "alice@example.test"), new("dave", "dave@example.test"), new("bob", "bob@example.test"), new("carol", "carol@example.test"),
            new("hidden", "hidden@example.test", EmailHidden: true), new("svc-bot", "bot@acme.test")]);
        Groups["eng-team"] = ["dave"];
        Groups["leads"] = ["alice"];
        var eng = new Space { Key = "ENG", Name = "Engineering" };
        eng.Users.AddRange(["alice", "svc-bot"]);
        eng.Groups.Add("eng-team");
        var hr = new Space { Key = "HR", Name = "People team" };
        hr.Users.AddRange(["bob", "svc-bot"]);
        Spaces.AddRange([eng, hr]);
        Contents.Add(new Content
        {
            Id = "101", Space = "ENG", Title = "Deploy",
            Body = "<h2>Rollback</h2><p>To roll back a deploy, run <code>make rollback</code> and pick the release tag.</p>" +
                "<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">bash</ac:parameter><ac:plain-text-body><![CDATA[make rollback TAG=v1 && echo <done>]]></ac:plain-text-body></ac:structured-macro>" +
                "<h2>Releases</h2><p>Releases go out on Tuesdays after the change review.</p>",
        });
        Contents.Add(new Content { Id = "102", Space = "ENG", Title = "Onboarding", Body = "<p>New people get their laptop and accounts on their first day.</p>" });
        var leads = new Content { Id = "103", Space = "ENG", Title = "Leads", Body = "<p>The reorganisation of the codec team is planned for April.</p>" };
        leads.RestrictGroups.Add("leads");
        Contents.Add(leads);
        Contents.Add(new Content { Id = "104", Space = "ENG", Title = "Leads budget", Parent = "103", Body = "<p>The hiring budget for the codec team is four engineers.</p>" });
        Contents.Add(new Content { Id = "105", Space = "ENG", Title = "Release notes", Type = "blogpost", Body = "<p>Version nine shipped with the faster decoder.</p>" });
        Contents.Add(new Content { Id = "201", Space = "HR", Title = "Leave policy", Body = "<h1>Leave</h1><p>Everyone gets 30 days of paid leave a year, booked in the HR portal.</p>" });
    }

    private static HttpResponseMessage Json(HttpStatusCode code, JsonNode json) => new(code) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    private JsonObject UserJson(Person p, bool cloud) => cloud
        ? new JsonObject { ["type"] = "known", ["accountId"] = p.AccountId, ["email"] = p.EmailHidden ? "" : p.Email, ["publicName"] = p.Username, ["displayName"] = p.Username }
        : new JsonObject { ["type"] = "known", ["username"] = p.Username, ["userKey"] = "key-" + p.Username, ["displayName"] = p.Username };

    private Person PersonOf(string username) => People.Single(p => p.Username == username);

    private JsonObject Subjects(IEnumerable<string> users, IEnumerable<string> groups, bool cloud) => new()
    {
        ["user"] = new JsonObject { ["results"] = new JsonArray([.. users.Select(u => (JsonNode)UserJson(PersonOf(u), cloud))]) },
        ["group"] = new JsonObject { ["results"] = new JsonArray([.. groups.Select(g => (JsonNode)new JsonObject { ["type"] = "group", ["name"] = g, ["id"] = "gid-" + g })]) },
    };

    private JsonObject ContentJson(Content c, bool cloud, bool expanded)
    {
        var json = new JsonObject
        {
            ["id"] = c.Id, ["type"] = c.Type, ["status"] = "current", ["title"] = c.Title,
            ["_links"] = new JsonObject { ["webui"] = c.Type == "blogpost" ? $"/spaces/{c.Space}/blog/{c.Id}" : $"/spaces/{c.Space}/pages/{c.Id}/{Uri.EscapeDataString(c.Title)}" },
        };
        if (expanded)
        {
            json["version"] = new JsonObject { ["number"] = c.Version };
            var chain = new List<Content>();
            for (var parent = c.Parent; parent is not null; parent = Contents.Single(x => x.Id == parent).Parent)
            {
                chain.Insert(0, Contents.Single(x => x.Id == parent));
            }
            json["ancestors"] = new JsonArray([.. chain.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["type"] = a.Type, ["title"] = a.Title })]);
            json["restrictions"] = new JsonObject { ["read"] = new JsonObject { ["operation"] = "read", ["restrictions"] = Subjects(c.RestrictUsers, c.RestrictGroups, cloud) } };
        }
        return json;
    }

    /// <summary>Two items a page, with Confluence's next link (relative to the site's root, as both editions give it).</summary>
    private static JsonObject Page(List<JsonNode> all, string path, System.Collections.Specialized.NameValueCollection query)
    {
        var start = int.TryParse(query["start"], out var s) ? s : 0;
        var page = all.Skip(start).Take(2).ToList();
        var links = new JsonObject { ["base"] = "https://ignored.test" };
        if (start + 2 < all.Count)
        {
            query["start"] = (start + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            links["next"] = $"/rest/api/{path}?{string.Join('&', query.AllKeys.Select(k => $"{k}={Uri.EscapeDataString(query[k]!)}"))}";
        }
        return new JsonObject { ["results"] = new JsonArray([.. page.Select(p => p.DeepClone())]), ["size"] = page.Count, ["_links"] = links };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var auth = request.Headers.Authorization?.ToString();
        lock (Calls)
        {
            Calls.Add((request.Method.Method, uri.PathAndQuery, auth));
        }
        bool cloud;
        string path;
        if (uri.Host == "confluence.test" && uri.AbsolutePath.StartsWith("/rest/api/", StringComparison.Ordinal))
        {
            (cloud, path) = (false, uri.AbsolutePath["/rest/api/".Length..]);
            if (auth != "Bearer " + Token)
            {
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, new JsonObject { ["message"] = "Unauthorized" }));
            }
        }
        else if (uri.Host == "acme.atlassian.net" && uri.AbsolutePath.StartsWith("/wiki/rest/api/", StringComparison.Ordinal))
        {
            (cloud, path) = (true, uri.AbsolutePath["/wiki/rest/api/".Length..]);
            if (auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CloudEmail}:{CloudToken}")))
            {
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, new JsonObject { ["message"] = "Unauthorized" }));
            }
        }
        else
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        return Task.FromResult(Answer(path, HttpUtility.ParseQueryString(uri.Query), cloud));
    }

    private HttpResponseMessage Answer(string path, System.Collections.Specialized.NameValueCollection query, bool cloud)
    {
        var notFound = Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "No content found" });
        if (path == "user/current")
        {
            return Json(HttpStatusCode.OK, UserJson(PersonOf("svc-bot"), cloud));
        }
        if (path == "space")
        {
            return Json(HttpStatusCode.OK, Page([.. Spaces.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["name"] = s.Name, ["type"] = "global", ["status"] = "current" })], path, query));
        }
        if (SpacePath().Match(path) is { Success: true } sp)
        {
            if (Spaces.FirstOrDefault(s => s.Key == sp.Groups[1].Value) is not { } space)
            {
                return notFound;
            }
            var json = new JsonObject { ["key"] = space.Key, ["name"] = space.Name, ["type"] = "global", ["status"] = "current" };
            if (query["expand"] == "permissions" && space.PermissionsShown)
            {
                json["permissions"] = new JsonArray(
                    new JsonObject { ["operation"] = new JsonObject { ["operation"] = "read", ["targetType"] = "space" }, ["subjects"] = Subjects(space.Users, space.Groups, cloud), ["anonymousAccess"] = false },
                    new JsonObject { ["operation"] = new JsonObject { ["operation"] = "create", ["targetType"] = "page" }, ["subjects"] = Subjects(["carol"], [], cloud) });
            }
            return Json(HttpStatusCode.OK, json);
        }
        if (path == "content/search")
        {
            var cql = CqlPattern().Match(query["cql"] ?? "");
            var start = int.TryParse(query["start"], out var s) ? s : 0;
            if (FailSecondPage && start > 0)
            {
                return Json(HttpStatusCode.InternalServerError, new JsonObject { ["message"] = "search is unavailable" });
            }
            var found = Contents.Where(c => c.Space == cql.Groups[1].Value && c.Type == cql.Groups[2].Value).Select(c => (JsonNode)ContentJson(c, cloud, expanded: true)).ToList();
            return Json(HttpStatusCode.OK, Page(found, path, query));
        }
        if (ContentPath().Match(path) is { Success: true } cp)
        {
            if (Contents.FirstOrDefault(c => c.Id == cp.Groups[1].Value) is not { } content)
            {
                return notFound;
            }
            if (cp.Groups[2].Success)
            {
                return Json(HttpStatusCode.OK, new JsonObject { ["operation"] = "read", ["restrictions"] = Subjects(content.RestrictUsers, content.RestrictGroups, cloud) });
            }
            var json = ContentJson(content, cloud, expanded: false);
            json["body"] = new JsonObject { ["storage"] = new JsonObject { ["value"] = content.Body, ["representation"] = "storage" } };
            return Json(HttpStatusCode.OK, json);
        }
        var members = cloud ? CloudMembers().Match(path) : DataCenterMembers().Match(path);
        if (members.Success)
        {
            var name = cloud ? members.Groups[1].Value["gid-".Length..] : Uri.UnescapeDataString(members.Groups[1].Value);
            return Groups.TryGetValue(name, out var people)
                ? Json(HttpStatusCode.OK, Page([.. people.Select(u => (JsonNode)UserJson(PersonOf(u), cloud))], path, query))
                : notFound;
        }
        return notFound;
    }

    [GeneratedRegex(@"^space/([^/]+)$")]
    private static partial Regex SpacePath();

    [GeneratedRegex(@"^space=""([^""]+)"" and type=(\w+)$")]
    private static partial Regex CqlPattern();

    [GeneratedRegex(@"^content/(\d+)(/restriction/byOperation/read)?$")]
    private static partial Regex ContentPath();

    [GeneratedRegex(@"^group/([^/]+)/member$")]
    private static partial Regex DataCenterMembers();

    [GeneratedRegex(@"^group/([^/]+)/membersByGroupId$")]
    private static partial Regex CloudMembers();
}
