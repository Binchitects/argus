using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Plugins;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Plugins: the manifest, installing one that comes with the app, each person connecting their own account, and its calls asked and audited.</summary>
[Collection(nameof(AppCollection))]
public sealed class PluginTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("plugins_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-plugin-tests" });

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text) =>
        [.. (await (await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text })).Content.ReadAsStringAsync())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    private static JsonElement Event(List<JsonElement> events, string type) => events.First(e => e.GetProperty("type").GetString() == type);

    [Fact]
    public void The_manifests_that_come_with_the_app_read_and_say_what_they_need()
    {
        var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(AppFixture.PluginsPath, "gitlab-issues", PluginManifest.FileName)));
        Assert.Equal("gitlab-issues", manifest.Name);
        Assert.Equal("oauth2", manifest.PersonAuth);
        Assert.Equal(["gitlab_url", "client_id", "client_secret"], manifest.Settings.Select(s => s.Key));
        Assert.Equal("https://gitlab.test/api/v4", PluginManifest.Fill(manifest.Url, new Dictionary<string, string> { ["gitlab_url"] = "https://gitlab.test/" }));
        Assert.Throws<PluginManifest.ManifestException>(() => PluginManifest.Parse("name: Bad Name\ntools: { mcp: https://x.test/mcp }"));
        Assert.Throws<PluginManifest.ManifestException>(() => PluginManifest.Parse("name: two\ntools: { mcp: https://x.test/mcp, openapi: a.yaml }"));
        Assert.Throws<PluginManifest.ManifestException>(() => PluginManifest.Parse("name: escape\ntools: { openapi: ../../etc/passwd }"));
    }

    [Fact]
    public async Task A_plugin_installs_each_person_connects_their_own_account_and_its_writes_ask_first_and_are_audited()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var listed = await admin.JsonAsync(await admin.GetAsync("/api/admin/plugins"));
        Assert.Contains(listed.GetProperty("catalog").EnumerateArray(), e => e.GetProperty("name").GetString() == "gitlab-issues" && e.GetProperty("source").GetString() == "app");

        // Its settings are checked; the secret is kept encrypted and never shown.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/plugins/install", new { name = "gitlab-issues", settings = new { gitlab_url = "https://gitlab.test" } }));
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install",
            new { name = "gitlab-issues", settings = new { gitlab_url = "https://gitlab.test", client_id = "app-id", client_secret = "app-secret" } }));
        var toolId = made.GetProperty("toolId").GetString()!;
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/plugins/install", new { name = "gitlab-issues", settings = new { } }));
        var installed = (await admin.JsonAsync(await admin.GetAsync("/api/admin/plugins"))).GetProperty("installed").EnumerateArray().Single();
        Assert.Equal("https://gitlab.test/api/v4", installed.GetProperty("url").GetString());
        Assert.DoesNotContain("app-secret", installed.ToString(), StringComparison.Ordinal);
        Assert.True(installed.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "client_secret").GetProperty("set").GetBoolean());

        // Not connected yet: the chat says so, and where.
        var (b, _) = await PersonAsync(f);
        var connections = (await b.JsonAsync(await b.GetAsync("/api/account/connections"))).EnumerateArray().Single();
        Assert.Equal("oauth2", connections.GetProperty("kind").GetString());
        Assert.False(connections.GetProperty("connected").GetBoolean());
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { toolId } }))).GetProperty("id").GetGuid();
        var notice = Event(await SendAsync(b, chat, "My issues?"), "notice");
        Assert.Contains("connect your GitLab issues account first", notice.GetProperty("text").GetString(), StringComparison.Ordinal);

        // Connecting: to GitLab's sign-in with a sealed state, back with a code, traded for tokens.
        var go = await b.GetAsync($"/api/account/connections/{Uri.EscapeDataString(toolId)}/connect");
        Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
        var authorize = go.Headers.Location!;
        Assert.StartsWith("https://gitlab.test/oauth/authorize?", authorize.ToString(), StringComparison.Ordinal);
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("app-id", query["client_id"]);
        Assert.Equal("https://llm.test/api/account/connections/callback", query["redirect_uri"]);
        Assert.Equal("api", query["scope"]);
        // Someone else cannot use the person's state.
        var (other, _) = await PersonAsync(f);
        var stolen = await other.GetAsync($"/api/account/connections/callback?code=good-code&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal("/account?connection=failed", stolen.Headers.Location!.ToString());
        var back = await b.GetAsync($"/api/account/connections/callback?code=good-code&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal("/account?connected=GitLab%20issues", back.Headers.Location!.ToString());
        Assert.True((await b.JsonAsync(await b.GetAsync("/api/account/connections"))).EnumerateArray().Single().GetProperty("connected").GetBoolean());

        // A read goes as the person, at once.
        var read = Event(await SendAsync(b, chat, "Mine? [call gitlab_issues__my_issues {}]"), "tool_result");
        Assert.StartsWith("HTTP 200", read.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains(app.Mcp.GitLabCalls, c => c.PathAndQuery.StartsWith("/api/v4/issues", StringComparison.Ordinal) && c.Authorization == "Bearer token-1");

        // A write waits for Allow.
        var writing = SendAsync(b, chat, """File it: [call gitlab_issues__create_issue {"id":"group/app","body":{"title":"Frames dropped"}}]""");
        for (var i = 0; (await b.PostAsync($"/api/chat/conversations/{chat}/tool-calls/call_1", new { allow = true })).StatusCode != HttpStatusCode.NoContent; i++)
        {
            Assert.True(i < 100, "the write never waited to be allowed");
            await Task.Delay(100);
        }
        Assert.StartsWith("HTTP 201", Event(await writing, "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains(app.Mcp.GitLabCalls, c => c is { Method: "POST", PathAndQuery: "/api/v4/projects/group%2Fapp/issues", Body: """{"title":"Frames dropped"}""" });

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "plugin.install" && e.GetProperty("target").GetString() == "gitlab-issues");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "plugin.connect");
        Assert.Equal(2, audit.Count(e => e.GetProperty("action").GetString() == "plugin.call"));

        // Removed: its tool, and everyone's accounts with it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/plugins/{made.GetProperty("id").GetGuid()}", UriKind.Relative)));
        Assert.Empty((await b.JsonAsync(await b.GetAsync("/api/account/connections"))).EnumerateArray());
    }

    [Fact]
    public async Task A_plugin_installs_from_its_zip_and_one_that_signs_in_with_a_key_takes_each_persons_key()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("pets/plugin.yaml", "name: pets\nversion: 2.0.0\ntitle: Pets\ntools: { openapi: openapi.yaml, url: \"https://pets.test/v1\" }\nauth: { per_person: api_key, header: X-Api-Key, value: \"{token}\", help: Your pet store key. }\n");
            Add("pets/openapi.yaml", FakeMcp.PetsSpec);
        }
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install", new { zip = Convert.ToBase64String(buffer.ToArray()) }));
        var toolId = made.GetProperty("toolId").GetString()!;

        var (b, _) = await PersonAsync(f);
        Assert.Equal("api_key", (await b.JsonAsync(await b.GetAsync("/api/account/connections"))).EnumerateArray().Single().GetProperty("kind").GetString());
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PutAsJsonAsync(new Uri($"/api/account/connections/{Uri.EscapeDataString(toolId)}", UriKind.Relative), new { secret = "persons-own-key" }));
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { toolId } }))).GetProperty("id").GetGuid();
        var read = Event(await SendAsync(b, chat, """[call pets__list_pets {"limit":1}]"""), "tool_result");
        Assert.StartsWith("HTTP 200", read.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains(app.Mcp.PetCalls, c => c.Key == "persons-own-key");
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/account/connections/{Uri.EscapeDataString(toolId)}", UriKind.Relative)));
    }
}
