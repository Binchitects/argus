using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Plugins;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/plugins/install", new { accept = true, name = "gitlab-issues", settings = new { gitlab_url = "https://gitlab.test" } }));
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install",
            new { accept = true, name = "gitlab-issues", settings = new { gitlab_url = "https://gitlab.test", client_id = "app-id", client_secret = "app-secret" } }));
        var toolId = made.GetProperty("toolId").GetString()!;
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/plugins/install", new { accept = true, name = "gitlab-issues", settings = new { } }));
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
    public async Task An_expired_oauth_token_is_refreshed_at_the_plugins_token_address_and_a_failed_refresh_says_to_connect_again()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install",
            new { accept = true, name = "gitlab-issues", settings = new { gitlab_url = "https://gitlab.test", client_id = "app-id", client_secret = "app-secret" } }));
        var toolId = made.GetProperty("toolId").GetString()!;
        var (b, _) = await PersonAsync(f);
        var go = await b.GetAsync($"/api/account/connections/{Uri.EscapeDataString(toolId)}/connect");
        var state = System.Web.HttpUtility.ParseQueryString(go.Headers.Location!.Query)["state"]!;
        Assert.Equal("/account?connected=GitLab%20issues", (await b.GetAsync($"/api/account/connections/callback?code=good-code&state={Uri.EscapeDataString(state)}")).Headers.Location!.ToString());
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { toolId } }))).GetProperty("id").GetGuid();

        async Task ExpireAsync()
        {
            await using var scope = f.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PersonCredentials.Where(c => c.ToolId == toolId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
        }
        async Task<DateTimeOffset?> ExpiresAsync()
        {
            await using var scope = f.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PersonCredentials.Where(c => c.ToolId == toolId).Select(c => c.ExpiresAt).SingleAsync();
        }
        int Seen() { lock (app.Mcp.GitLabCalls) { return app.Mcp.GitLabCalls.Count; } }
        List<(string Method, string PathAndQuery, string? Authorization, string? Body)> Since(int from) { lock (app.Mcp.GitLabCalls) { return app.Mcp.GitLabCalls[from..]; } }

        // Expired: the next call trades the refresh token for a new one at the plugin's token address, and goes with it.
        await ExpireAsync();
        var before = Seen();
        var read = Event(await SendAsync(b, chat, "Mine? [call gitlab_issues__my_issues {}]"), "tool_result");
        Assert.StartsWith("HTTP 200", read.GetProperty("text").GetString(), StringComparison.Ordinal);
        var calls = Since(before);
        var refresh = Assert.Single(calls, c => c is { Method: "POST", PathAndQuery: "/oauth/token" });
        var form = System.Web.HttpUtility.ParseQueryString(refresh.Body!);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("refresh-1", form["refresh_token"]);
        Assert.Contains(calls, c => c.PathAndQuery.StartsWith("/api/v4/issues", StringComparison.Ordinal) && c.Authorization == "Bearer token-2");
        Assert.True(await ExpiresAsync() > DateTimeOffset.UtcNow.AddHours(1));

        // Expired again, and GitLab no longer takes the refresh token (refresh-2 here): the chat says to connect again, and nothing is called as the person.
        await ExpireAsync();
        before = Seen();
        var events = await SendAsync(b, chat, "Mine again? [call gitlab_issues__my_issues {}]");
        var notice = Event(events, "notice").GetProperty("text").GetString()!;
        Assert.Contains("GitLab issues sign-in has expired and could not be renewed: connect again, in Your account → Connections", notice, StringComparison.Ordinal);
        Assert.Contains(Since(before), c => c is { Method: "POST", PathAndQuery: "/oauth/token" });
        Assert.DoesNotContain(Since(before), c => c.PathAndQuery.StartsWith("/api/v4/issues", StringComparison.Ordinal));
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
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install", new { accept = true, zip = Convert.ToBase64String(buffer.ToArray()) }));
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

    [Fact]
    public async Task A_groups_own_settings_are_what_its_people_use_and_the_plugins_own_for_everyone_else()
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
            Add("shared-pets/plugin.yaml", "name: shared-pets\nversion: 1.0.0\ntitle: Shared pets\ntools: { openapi: openapi.yaml, url: \"https://pets.test/v1\" }\n" +
                "auth: { header: X-Api-Key, value: \"{token}\" }\nsettings:\n  - { key: token, title: Key, type: secret, required: true }\n");
            Add("shared-pets/openapi.yaml", FakeMcp.PetsSpec);
        }
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install",
            new { accept = true, zip = Convert.ToBase64String(buffer.ToArray()), settings = new { token = "company-key" } }));
        var toolId = made.GetProperty("toolId").GetString()!;
        var id = made.GetProperty("id").GetGuid();
        var vets = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Vets" }))).GetProperty("id").GetGuid();
        var (vet, _) = await PersonAsync(f);
        var (other, _) = await PersonAsync(f);
        var vetId = (await vet.JsonAsync(await vet.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{vets}/members", new { userIds = new[] { vetId } }));

        // The group's own key, kept encrypted and never shown.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/plugins/{id}/groups/{vets}", UriKind.Relative), new { settings = new { colour = "red" } }));
        var saved = await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/plugins/{id}/groups/{vets}", UriKind.Relative), new { settings = new { token = "vets-key" } });
        await StatusAssert.Is(HttpStatusCode.OK, saved);
        var listed = await (await admin.GetAsync($"/api/admin/plugins/{id}/groups")).Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Vets\"", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("vets-key", listed, StringComparison.Ordinal);

        async Task<string?> KeyOfAsync(TestBrowser b)
        {
            var before = app.Mcp.PetCalls.Count;
            var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { toolId } }))).GetProperty("id").GetGuid();
            var read = Event(await SendAsync(b, chat, """[call shared_pets__list_pets {"limit":1}]"""), "tool_result");
            Assert.StartsWith("HTTP 200", read.GetProperty("text").GetString(), StringComparison.Ordinal);
            lock (app.Mcp.PetCalls)
            {
                return app.Mcp.PetCalls.Skip(before).Last().Key;
            }
        }
        Assert.Equal("vets-key", await KeyOfAsync(vet));
        Assert.Equal("company-key", await KeyOfAsync(other));

        // Emptied, the setting is the plugin's again, for the group too.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/plugins/{id}/groups/{vets}", UriKind.Relative), new { settings = new { token = "" } }));
        Assert.Equal("[]", await (await admin.GetAsync($"/api/admin/plugins/{id}/groups")).Content.ReadAsStringAsync());
        Assert.Equal("company-key", await KeyOfAsync(vet));
    }

    [Fact]
    public async Task A_plugin_offers_scheduled_tasks_to_add_and_skills_for_Code_Arena_to_those_who_may_use_it()
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
            Add("vet/plugin.yaml", "name: vet\nversion: 1.0.0\ntitle: Vet\ntools: { openapi: openapi.yaml, url: \"https://pets.test/v1\" }\n" +
                "tasks:\n  - { name: Morning pets, description: The pets to see today., prompt: List today's pets., cron: \"0 8 * * 1-5\" }\n" +
                "skills: [skills/pet-care]\n");
            Add("vet/openapi.yaml", FakeMcp.PetsSpec);
            Add("vet/skills/pet-care/SKILL.md", "---\nname: pet-care\ndescription: How to look after a pet.\n---\nFeed it twice a day.\n");
            Add("vet/skills/pet-care/food/cats.md", "Fish.\n");
        }
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install", new { accept = true, zip = Convert.ToBase64String(buffer.ToArray()) }));
        var toolId = made.GetProperty("toolId").GetString()!;
        var name = "v" + Guid.NewGuid().ToString("N")[..10];
        var person = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, person.GetProperty("password").GetString()!);

        // The task, to add to one's own: its question, when, and the plugin's tool.
        var template = (await b.JsonAsync(await b.GetAsync("/api/tasks/templates"))).EnumerateArray().Single();
        Assert.Equal(("Morning pets", "0 8 * * 1-5", "Vet"), (template.GetProperty("name").GetString(), template.GetProperty("cron").GetString(), template.GetProperty("pluginTitle").GetString()));
        Assert.Equal([toolId], template.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));

        // The skill, as Code Arena reads it with the person's key.
        using var arena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        arena.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", person.GetProperty("apiKey").GetString());
        var skills = JsonDocument.Parse(await arena.GetStringAsync(new Uri("/api/code-arena/skills", UriKind.Relative))).RootElement.GetProperty("skills");
        var skill = skills.EnumerateArray().Single();
        Assert.Equal(("pet-care", "How to look after a pet.", "vet"), (skill.GetProperty("name").GetString(), skill.GetProperty("description").GetString(), skill.GetProperty("plugin").GetString()));
        Assert.Equal("Fish.\n", skill.GetProperty("files").GetProperty("food/cats.md").GetString());

        // A tool turned off brings neither.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ToolSettings.Where(t => t.ToolId == toolId).ExecuteDeleteAsync();
            db.ToolSettings.Add(new Llm.Core.Chat.ToolSetting { ToolId = toolId, Enabled = false });
            await db.SaveChangesAsync();
        }
        Assert.Empty((await b.JsonAsync(await b.GetAsync("/api/tasks/templates"))).EnumerateArray());
        Assert.Empty(JsonDocument.Parse(await arena.GetStringAsync(new Uri("/api/code-arena/skills", UriKind.Relative))).RootElement.GetProperty("skills").EnumerateArray());

        // Removed, the plugin takes its tasks with it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/plugins/{made.GetProperty("id").GetGuid()}", UriKind.Relative)));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().TaskTemplates.AnyAsync());
        }
    }

    [Fact]
    [Trait("Control", "OPS-02")]
    public async Task A_plugins_own_server_runs_in_the_sandbox_with_its_settings_one_job_a_call()
    {
        await using var sandbox = new FakeSandbox();
        await using var f = app.Create(app.ConnectionStringFor("plugins_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-plugin-tests", ["Sandbox:Dir"] = sandbox.Dir });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        byte[] Zip(params (string Name, string Text)[] files)
        {
            using var buffer = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, text) in files)
                {
                    using var w = new StreamWriter(zip.CreateEntry(name).Open());
                    w.Write(text);
                }
            }
            return buffer.ToArray();
        }
        const string manifest = "name: units\nversion: 1.0.0\ntitle: Units\ntools: { sandbox: mcp }\nsettings:\n  - { key: precision, title: Digits, type: text }\n";
        // A server is a folder with its server.py.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/plugins/install",
            new { accept = true, zip = Convert.ToBase64String(Zip(("units/plugin.yaml", manifest), ("units/mcp/other.py", "x = 1\n"))) }));
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install", new
        {
            accept = true, settings = new { precision = "2" },
            zip = Convert.ToBase64String(Zip(("units/plugin.yaml", manifest), ("units/mcp/server.py", "from sandbox_tools.mcp_server import Server\n"), ("units/mcp/lengths.py", "FEET = 3.28084\n"))),
        }));
        var toolId = made.GetProperty("toolId").GetString()!;
        var installed = (await admin.JsonAsync(await admin.GetAsync("/api/admin/plugins"))).GetProperty("installed").EnumerateArray().Single();
        Assert.Equal("sandbox:units", installed.GetProperty("url").GetString());

        var (b, _) = await PersonAsync(f);
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { toolId } }))).GetProperty("id").GetGuid();
        var events = await SendAsync(b, chat, """[call units__convert {"value": 2, "unit": "m"}]""");
        Assert.Equal("6.56 ft", Event(events, "tool_result").GetProperty("text").GetString());
        var failed = Event(await SendAsync(b, chat, """[call units__boom {}]"""), "tool_result");
        Assert.True(failed.GetProperty("isError").GetBoolean());

        // Its files and its settings went in, each time; its list once.
        lock (sandbox.Jobs)
        {
            var jobs = sandbox.Jobs.Where(j => j["code"]!.GetValue<string>().Contains("runpy.run_path('server.py'", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, jobs.Count);
            Assert.All(jobs, j => Assert.Equal(["lengths.py", "server.py", "settings.json"], j["files"]!.AsArray().Select(x => x!.GetValue<string>()).Order()));
        }
        Assert.All(sandbox.McpSettings, s => Assert.Equal("""{"precision":"2"}""", s));
    }
}
