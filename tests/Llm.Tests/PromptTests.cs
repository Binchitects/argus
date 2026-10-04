using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Api.Plugins;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The prompt library: a person's own prompts, shared with groups, the company's, and those a plugin brings.</summary>
[Collection(nameof(AppCollection))]
public sealed class PromptTests(AppFixture app)
{
    /// <summary>Its own app and database: plugins and groups must not leak into other tests.</summary>
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("prompts_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-prompt-tests" });

    private static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "q" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Quinn " + name[..4] }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    private static async Task<List<JsonElement>> PromptsAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/prompts"))).GetProperty("prompts").EnumerateArray()];

    private static Task<HttpResponseMessage> PutAsync(TestBrowser b, string id, object body) =>
        b.Http.PutAsJsonAsync(new Uri($"/api/prompts/{id}", UriKind.Relative), body);

    [Fact]
    public async Task A_prompt_is_its_owners_shared_with_their_groups_or_the_companys_and_only_its_owner_changes_it()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (a, aId) = await PersonAsync(f);
        var (b, bId) = await PersonAsync(f);

        var made = await a.PostAsync("/api/prompts", new { name = "/Review", title = "Review code", text = "Review {{file}} for {{ focus }}, then {{file}} again." });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await a.JsonAsync(made)).GetProperty("id").GetString()!;
        var mine = Assert.Single(await PromptsAsync(a));
        Assert.Equal("review", mine.GetProperty("name").GetString());
        Assert.Equal(["file", "focus"], mine.GetProperty("variables").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("mine", mine.GetProperty("source").GetString());
        Assert.True(mine.GetProperty("canEdit").GetBoolean());
        Assert.Empty(await PromptsAsync(b));

        // Checked: a slash name, no second /review of theirs, the company is for admins.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await a.PostAsync("/api/prompts", new { name = "Not a name", title = "x", text = "y" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await a.PostAsync("/api/prompts", new { name = "empty", title = "x", text = "" }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await a.PostAsync("/api/prompts", new { name = "review", title = "Again", text = "y" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await a.PostAsync("/api/prompts", new { name = "all", title = "x", text = "y", sharing = "Company" }));

        // Shared with a group: its members use it, only the owner changes it.
        var team = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Reviewers" }))).GetProperty("id").GetGuid();
        var outsiders = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Outsiders" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{team}/members", new { userIds = new[] { aId, bId } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PutAsync(a, id, new { sharing = "Groups", groups = new[] { outsiders } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PutAsync(a, id, new { sharing = "Groups" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(a, id, new { sharing = "Groups", groups = new[] { team } }));
        var shared = Assert.Single(await PromptsAsync(b));
        Assert.Equal("group", shared.GetProperty("source").GetString());
        Assert.False(shared.GetProperty("canEdit").GetBoolean());
        Assert.Equal("Reviewers", shared.GetProperty("groups").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.StartsWith("Quinn", shared.GetProperty("from").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(b, id, new { title = "Mine now" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.Http.DeleteAsync(new Uri($"/api/prompts/{id}", UriKind.Relative)));
        Assert.Empty(await PromptsAsync(admin));

        // The company's: an admin makes it, everyone uses it, any admin changes it; audited.
        var company = (await admin.JsonAsync(await admin.PostAsync("/api/prompts", new { name = "standup", title = "Stand-up notes", text = "Write my stand-up from {{notes}}.", sharing = "Company" })))
            .GetProperty("id").GetString()!;
        Assert.Contains(await PromptsAsync(b), p => p.GetProperty("name").GetString() == "standup" && p.GetProperty("source").GetString() == "company");
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(a, company, new { text = "Changed" }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/prompts", new { name = "standup", title = "Twice", text = "y", sharing = "Company" }));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "prompt.create" && e.GetProperty("target").GetString() == "/standup");

        // Deleted by its owner: gone for the group.
        await StatusAssert.Is(HttpStatusCode.NoContent, await a.Http.DeleteAsync(new Uri($"/api/prompts/{id}", UriKind.Relative)));
        Assert.DoesNotContain(await PromptsAsync(b), p => p.GetProperty("name").GetString() == "review");
    }

    [Fact]
    public async Task A_plugin_brings_its_prompts_to_whoever_may_use_it_and_takes_them_when_removed()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var preview = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/preview", new { name = "gitlab-issues" }));
        Assert.Equal("triage", preview.GetProperty("prompts").EnumerateArray().Single().GetProperty("name").GetString());
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install",
            new { name = "gitlab-issues", settings = new { gitlab_url = "https://gitlab.test", client_id = "app-id", client_secret = "app-secret" } }));
        var toolId = made.GetProperty("toolId").GetString()!;

        var (b, _) = await PersonAsync(f);
        var triage = Assert.Single(await PromptsAsync(b));
        Assert.Equal("triage", triage.GetProperty("name").GetString());
        Assert.Equal("plugin", triage.GetProperty("source").GetString());
        Assert.Equal("GitLab issues", triage.GetProperty("from").GetString());
        Assert.Equal(["issue", "project"], triage.GetProperty("variables").EnumerateArray().Select(v => v.GetString()));
        Assert.False(triage.GetProperty("canEdit").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(admin, triage.GetProperty("id").GetString()!, new { title = "Changed" }));

        // For admins only: the person no longer sees it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/tools/{toolId}", UriKind.Relative),
            new { enabled = true, audience = "Admins", onByDefault = true, askFirst = false }));
        Assert.Empty(await PromptsAsync(b));
        Assert.Single(await PromptsAsync(admin));

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/plugins/{made.GetProperty("id").GetGuid()}", UriKind.Relative)));
        Assert.Empty(await PromptsAsync(admin));
    }

    [Fact]
    public void A_plugins_prompt_file_has_a_front_matter_with_its_name()
    {
        var p = PromptLibrary.FromFile("p.md", "---\r\nname: review\ntitle: Review a file\n---\nReview {{file}}.\n");
        Assert.Equal(new PluginPrompt("review", "Review a file", "Review {{file}}."), p);
        Assert.Equal("plain", PromptLibrary.FromFile("p.md", "---\nname: plain\n---\nText").Title);
        Assert.Throws<PluginCatalog.PluginException>(() => PromptLibrary.FromFile("p.md", "Just text"));
        Assert.Throws<PluginCatalog.PluginException>(() => PromptLibrary.FromFile("p.md", "---\ntitle: No name\n---\nText"));
        Assert.Throws<PluginCatalog.PluginException>(() => PromptLibrary.FromFile("p.md", "---\nname: Bad Name\n---\nText"));
        Assert.Throws<PluginCatalog.PluginException>(() => PromptLibrary.FromFile("p.md", "---\nname: empty\n---\n"));
        Assert.Throws<PluginManifest.ManifestException>(() => PluginManifest.Parse("name: escape\ntools: { mcp: https://x.test/mcp }\nprompts: [../secret.md]"));
        Assert.Equal(["a", "b_2"], PromptLibrary.Variables("{{a}} {{ b_2 }} {{a}} {{ 9 }} {single}"));
    }
}
