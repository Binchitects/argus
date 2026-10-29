using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Knowledge;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Other services as knowledge sources, each against a fake of its API: GitHub (docs and issues, not pull requests or code),
/// Jira's issues, Notion's pages, an RSS feed, a WebDAV folder, an S3 bucket (signed with Signature Version 4).
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ServiceConnectorTests(AppFixture app)
{
    /// <summary>The services' APIs, by method and address (without the query, when the address with it is not listed).</summary>
    private sealed class FakeServices : HttpMessageHandler
    {
        public ConcurrentDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();
        public ConcurrentQueue<HttpRequestMessage> Calls { get; } = new();

        public void Json(string method, string url, JsonNode body) => Routes[$"{method} {url}"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        public void Text(string method, string url, string body, string type = "application/xml") => Routes[$"{method} {url}"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Enqueue(request);
            var full = request.RequestUri!.ToString();
            var bare = request.RequestUri!.GetLeftPart(UriPartial.Path);
            return Task.FromResult(Routes.TryGetValue($"{request.Method} {full}", out var f) || Routes.TryGetValue($"{request.Method} {bare}", out f)
                ? f(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private WebApplicationFactory<Program> NewApp(FakeServices fake) =>
        app.Create(app.ConnectionStringFor("services_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-knowledge-tests", ["Modules:embed"] = "true",
        }, s => s.AddHttpClient(ServiceConnector.Client).ConfigurePrimaryHttpMessageHandler(() => fake));

    private static async Task<List<string>> TitlesAsync(TestBrowser admin, Guid id) =>
        [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{id}/documents"))).EnumerateArray().Select(d => d.GetProperty("title").GetString()!).Order(StringComparer.Ordinal)];

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task GitHub_gives_its_documentation_and_issues_not_its_code_or_pull_requests()
    {
        var fake = new FakeServices();
        const string Api = "https://api.github.com/repos/acme/handbook";
        fake.Json("GET", Api, new JsonObject { ["full_name"] = "acme/handbook", ["default_branch"] = "main", ["html_url"] = "https://github.com/acme/handbook" });
        fake.Json("GET", $"{Api}/git/trees/main?recursive=1", new JsonObject
        {
            ["tree"] = new JsonArray(
                new JsonObject { ["path"] = "README.md", ["type"] = "blob", ["sha"] = "a1", ["size"] = 40 },
                new JsonObject { ["path"] = "docs/setup.txt", ["type"] = "blob", ["sha"] = "a2", ["size"] = 40 },
                new JsonObject { ["path"] = "src/App.cs", ["type"] = "blob", ["sha"] = "a3", ["size"] = 40 },
                new JsonObject { ["path"] = "docs", ["type"] = "tree", ["sha"] = "a4" }),
        });
        fake.Json("GET", $"{Api}/contents/README.md", new JsonObject { ["content"] = B64("# Handbook\n\nWelcome to the handbook.") });
        fake.Json("GET", $"{Api}/contents/docs/setup.txt", new JsonObject { ["content"] = B64("Install the tools first.") });
        fake.Json("GET", $"{Api}/issues", new JsonArray(
            new JsonObject { ["number"] = 7, ["title"] = "Leave form broken", ["state"] = "open", ["body"] = "The form errors.", ["user"] = new JsonObject { ["login"] = "ann" }, ["html_url"] = "https://github.com/acme/handbook/issues/7", ["updated_at"] = "2026-10-01", ["comments"] = 1 },
            new JsonObject { ["number"] = 8, ["title"] = "A pull request", ["pull_request"] = new JsonObject(), ["updated_at"] = "2026-10-02" }));
        fake.Json("GET", $"{Api}/issues/7/comments", new JsonArray(new JsonObject { ["body"] = "Fixed in 2.1.", ["user"] = new JsonObject { ["login"] = "ben" }, ["created_at"] = "2026-10-03" }));
        await using var f = NewApp(fake);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, synced) = await KnowledgeTests.AddAsync(admin, new { name = "Handbook repo", kind = "github", location = "acme/handbook", issues = true, secret = "ghp-test-token" });
        Assert.Equal("synced", synced.GetProperty("state").GetString());
        Assert.Equal(["acme/handbook#7: Leave form broken", "acme/handbook/README.md", "acme/handbook/docs/setup.txt"], await TitlesAsync(admin, id));
        Assert.All(fake.Calls, c => Assert.Equal("Bearer ghp-test-token", c.Headers.Authorization?.ToString()));
    }

    [Fact]
    public async Task Jira_gives_each_issue_with_its_comments_and_a_link()
    {
        var fake = new FakeServices();
        fake.Json("GET", "https://jira.test/rest/api/2/search", new JsonObject
        {
            ["issues"] = new JsonArray(new JsonObject
            {
                ["key"] = "OPS-1",
                ["fields"] = new JsonObject
                {
                    ["summary"] = "Disk full on db1", ["description"] = "Clean the WAL.", ["updated"] = "2026-10-01", ["status"] = new JsonObject { ["name"] = "Done" },
                    ["issuetype"] = new JsonObject { ["name"] = "Incident" }, ["reporter"] = new JsonObject { ["displayName"] = "Ann" },
                    ["comment"] = new JsonObject { ["comments"] = new JsonArray(new JsonObject { ["body"] = "Archived old WAL.", ["author"] = new JsonObject { ["displayName"] = "Ben" }, ["created"] = "2026-10-02" }) },
                },
            }),
        });
        await using var f = NewApp(fake);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, synced) = await KnowledgeTests.AddAsync(admin, new { name = "Ops", kind = "jira", location = "https://jira.test", spaces = "OPS", secret = "pat" });
        Assert.Equal("synced", synced.GetProperty("state").GetString());
        var doc = Assert.Single((await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{id}/documents"))).EnumerateArray());
        Assert.Equal(("OPS-1: Disk full on db1", "https://jira.test/browse/OPS-1"), (doc.GetProperty("title").GetString(), doc.GetProperty("url").GetString()));
        Assert.Contains(fake.Calls, c => Uri.UnescapeDataString(c.RequestUri!.Query).Contains("project in (OPS)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Notion_gives_the_pages_shared_with_the_integration()
    {
        var fake = new FakeServices();
        fake.Json("POST", "https://api.notion.com/v1/search", new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject
            {
                ["id"] = "p1", ["url"] = "https://notion.so/p1", ["last_edited_time"] = "2026-10-01",
                ["properties"] = new JsonObject { ["Name"] = new JsonObject { ["type"] = "title", ["title"] = new JsonArray(new JsonObject { ["plain_text"] = "Onboarding" }) } },
            }),
            ["has_more"] = false,
        });
        fake.Json("GET", "https://api.notion.com/v1/blocks/p1/children", new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject { ["id"] = "b1", ["type"] = "paragraph", ["paragraph"] = new JsonObject { ["rich_text"] = new JsonArray(new JsonObject { ["plain_text"] = "Day one: meet your buddy." }) } }),
            ["has_more"] = false,
        });
        await using var f = NewApp(fake);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, synced) = await KnowledgeTests.AddAsync(admin, new { name = "Notion", kind = "notion", secret = "secret_x" });
        Assert.Equal("synced", synced.GetProperty("state").GetString());
        Assert.Equal(["Onboarding"], await TitlesAsync(admin, id));
        Assert.All(fake.Calls, c => Assert.Equal("2022-06-28", c.Headers.GetValues("Notion-Version").Single()));
    }

    [Fact]
    public async Task A_feed_gives_each_item()
    {
        var fake = new FakeServices();
        fake.Text("GET", "https://blog.test/feed.xml", """
            <rss version="2.0"><channel><title>Blog</title>
            <item><guid>1</guid><title>Release 7</title><link>https://blog.test/7</link><pubDate>Mon, 01 Oct 2026</pubDate><description>&lt;p&gt;What is new.&lt;/p&gt;</description></item>
            <item><guid>2</guid><title>Release 8</title><link>https://blog.test/8</link><description>More.</description></item>
            </channel></rss>
            """);
        await using var f = NewApp(fake);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, _) = await KnowledgeTests.AddAsync(admin, new { name = "Blog", kind = "feed", location = "https://blog.test/feed.xml" });
        Assert.Equal(["Release 7", "Release 8"], await TitlesAsync(admin, id));
    }

    [Fact]
    public async Task A_webdav_folder_gives_its_files_and_its_subfolders()
    {
        var fake = new FakeServices();
        static string Listing(params (string Href, bool Folder, string Etag)[] entries) =>
            "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">" + string.Concat(entries.Select(e =>
                $"<d:response><d:href>{e.Href}</d:href><d:propstat><d:prop>{(e.Folder ? "<d:resourcetype><d:collection/></d:resourcetype>" : "<d:resourcetype/>")}<d:getetag>{e.Etag}</d:getetag><d:getcontentlength>20</d:getcontentlength></d:prop></d:propstat></d:response>")) + "</d:multistatus>";
        fake.Text("PROPFIND", "https://cloud.test/dav/Docs/", Listing(("/dav/Docs/", true, "r"), ("/dav/Docs/plan.md", false, "e1"), ("/dav/Docs/Team/", true, "t")));
        fake.Text("PROPFIND", "https://cloud.test/dav/Docs/Team/", Listing(("/dav/Docs/Team/", true, "t"), ("/dav/Docs/Team/roles.txt", false, "e2")));
        fake.Text("GET", "https://cloud.test/dav/Docs/plan.md", "# Plan\n\nShip in May.", "text/markdown");
        fake.Text("GET", "https://cloud.test/dav/Docs/Team/roles.txt", "Ann leads the team.", "text/plain");
        await using var f = NewApp(fake);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, synced) = await KnowledgeTests.AddAsync(admin, new { name = "Cloud", kind = "webdav", location = "https://cloud.test/dav/Docs", account = "ann", secret = "app-pass" });
        Assert.Equal("synced", synced.GetProperty("state").GetString());
        Assert.Equal(["Team/roles.txt", "plan.md"], await TitlesAsync(admin, id));
    }

    [Fact]
    public async Task An_s3_bucket_gives_its_objects_under_the_prefix_asked_with_a_signed_request()
    {
        var fake = new FakeServices();
        fake.Text("GET", "https://s3.test/docs-bucket", """
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>false</IsTruncated>
            <Contents><Key>hr/leave.md</Key><ETag>"e1"</ETag><Size>30</Size></Contents>
            <Contents><Key>hr/</Key><ETag>"e0"</ETag><Size>0</Size></Contents>
            </ListBucketResult>
            """);
        fake.Text("GET", "https://s3.test/docs-bucket/hr/leave.md", "Thirty days of paid leave.", "text/markdown");
        await using var f = NewApp(fake);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (id, synced) = await KnowledgeTests.AddAsync(admin, new { name = "Bucket", kind = "s3", location = "https://s3.test/docs-bucket/hr/", account = "AKIDTEST", tenant = "eu-west-1", secret = "s3-secret" });
        Assert.Equal("synced", synced.GetProperty("state").GetString());
        Assert.Equal(["leave.md"], await TitlesAsync(admin, id));
        Assert.All(fake.Calls, c => Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDTEST/", c.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal));
        Assert.Contains(fake.Calls, c => c.RequestUri!.Query.Contains("prefix=hr%2F", StringComparison.Ordinal));
    }
}
