using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Apps: published from the chat for oneself, groups or everyone; versions and remixes; asking the model as the person using it; the app's data.</summary>
[Collection(nameof(AppCollection))]
public sealed class AppTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("apps_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());

    private static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(TestBrowser admin, WebApplicationFactory<Program> f, string display)
    {
        var name = "a" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = display }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    private const string Counter = "<!doctype html><html><body><button id=b>Count</button><script>arena.store.get('count')</script></body></html>";

    [Fact]
    public async Task An_app_is_published_for_its_owner_then_groups_then_everyone_with_versions_and_remixes()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, _) = await PersonAsync(admin, f, "Ana");
        var (ben, benId) = await PersonAsync(admin, f, "Ben");
        var (cy, _) = await PersonAsync(admin, f, "Cy");

        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync("/api/apps", new { name = "Counter", kind = "flash", code = Counter }));
        var made = await ana.JsonAsync(await ana.PostAsync("/api/apps", new { name = "Counter", description = "Counts clicks.", kind = "html", code = Counter }));
        var id = made.GetProperty("id").GetGuid();
        Assert.Equal(("private", 1), (made.GetProperty("reach").GetString(), made.GetProperty("version").GetInt32()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/apps/{id}"));
        Assert.Empty((await ben.JsonAsync(await ben.GetAsync("/api/apps"))).EnumerateArray());

        // For a group: its people open it, nobody else.
        var team = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Team" }))).GetProperty("id").GetGuid();
        var anaId = (await ana.JsonAsync(await ana.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/admin/groups/{team}/members", new { userIds = new[] { anaId, benId } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.Http.PutAsJsonAsync(new Uri($"/api/apps/{id}", UriKind.Relative), new { reach = "groups", groupIds = Array.Empty<Guid>() }));
        await StatusAssert.Is(HttpStatusCode.OK, await ana.Http.PutAsJsonAsync(new Uri($"/api/apps/{id}", UriKind.Relative), new { reach = "groups", groupIds = new[] { team } }));
        var opened = await ben.JsonAsync(await ben.GetAsync($"/api/apps/{id}?open=true"));
        Assert.Equal(Counter, opened.GetProperty("code").GetString());
        Assert.Equal(("Ana", false), (opened.GetProperty("app").GetProperty("owner").GetString(), opened.GetProperty("app").GetProperty("mine").GetBoolean()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await cy.GetAsync($"/api/apps/{id}"));

        // For everyone; only its owner changes it, each change of code a version, an old one put back as a new one.
        await ana.Http.PutAsJsonAsync(new Uri($"/api/apps/{id}", UriKind.Relative), new { reach = "company" });
        Assert.Equal(1, (await cy.JsonAsync(await cy.GetAsync("/api/apps"))).EnumerateArray().Single().GetProperty("opens").GetInt32());
        await StatusAssert.Is(HttpStatusCode.NotFound, await cy.Http.PutAsJsonAsync(new Uri($"/api/apps/{id}", UriKind.Relative), new { name = "Mine now" }));
        var v2 = await ana.JsonAsync(await ana.Http.PutAsJsonAsync(new Uri($"/api/apps/{id}", UriKind.Relative), new { code = Counter.Replace("Count", "Add one"), note = "Better words" }));
        Assert.Equal(2, v2.GetProperty("version").GetInt32());
        var versions = (await ana.JsonAsync(await ana.GetAsync($"/api/apps/{id}/versions"))).EnumerateArray().Select(v => (v.GetProperty("version").GetInt32(), v.GetProperty("note").GetString())).ToList();
        Assert.Equal([(2, "Better words"), (1, "Published")], versions);
        Assert.Equal(3, (await ana.JsonAsync(await ana.PostAsync($"/api/apps/{id}/versions/1/restore"))).GetProperty("version").GetInt32());
        Assert.Equal(Counter, (await cy.JsonAsync(await cy.GetAsync($"/api/apps/{id}"))).GetProperty("code").GetString());

        // A remix is the person's own copy, private.
        var remix = await cy.JsonAsync(await cy.PostAsync($"/api/apps/{id}/remix"));
        Assert.Equal(("Counter (remix)", "private", id), (remix.GetProperty("name").GetString(), remix.GetProperty("reach").GetString(), remix.GetProperty("remixOfId").GetGuid()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ana.GetAsync($"/api/apps/{remix.GetProperty("id").GetGuid()}"));

        // Publishing is a feature of its own.
        await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { benId }, steps = new[] { new { kind = "feature.deny", feature = "feature.apps" } }, dryRun = false });
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ben.PostAsync("/api/apps", new { name = "Nope", kind = "html", code = Counter }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await ana.Http.DeleteAsync(new Uri($"/api/apps/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await cy.GetAsync($"/api/apps/{id}"));
    }

    [Fact]
    public async Task An_app_asks_the_model_as_the_person_using_it_and_keeps_its_data_shared_or_each_persons_own()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, _) = await PersonAsync(admin, f, "Ana");
        var (ben, _) = await PersonAsync(admin, f, "Ben");
        var id = (await ana.JsonAsync(await ana.PostAsync("/api/apps", new { name = "Poll", kind = "html", code = Counter, reach = "company" }))).GetProperty("id").GetGuid();

        // arena.ask, as Ben, through his safeguards; a chat's preview asks too.
        var asked = await ben.JsonAsync(await ben.PostAsync($"/api/apps/{id}/ask", new { prompt = "Name a colour", system = "Answer in one word." }));
        Assert.StartsWith("Answer to: Name a colour", asked.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Answer to: In a preview", (await ana.JsonAsync(await ana.PostAsync("/api/apps/ask", new { prompt = "In a preview" }))).GetProperty("text").GetString(), StringComparison.Ordinal);
        var request = app.Model.Requests.Last(r => r.Body["messages"]!.AsArray().Any(m => m!["content"]?.ToString() == "Name a colour"));
        Assert.Equal("Answer in one word.", request.Body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("app", request.Body["metadata"]!["tags"]![0]!.GetValue<string>());
        await SafeguardTestRules.ChangeAsync(f, SafeguardTestRules.BlockedWords, r => (r.Enabled, r.Patterns) = (true, "project falcon"));
        Assert.False((await ben.PostAsync($"/api/apps/{id}/ask", new { prompt = "Tell me about project falcon" })).IsSuccessStatusCode);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ben.PostAsync($"/api/apps/{id}/ask", new { prompt = "" }));

        // arena.store: everyone's, and each person's own.
        await StatusAssert.Is(HttpStatusCode.NoContent, await ben.Http.PutAsJsonAsync(new Uri($"/api/apps/{id}/data/votes", UriKind.Relative), new { value = new { red = 2, blue = 1 } }));
        Assert.Equal(2, (await ana.JsonAsync(await ana.GetAsync($"/api/apps/{id}/data/votes"))).GetProperty("value").GetProperty("red").GetInt32());
        await StatusAssert.Is(HttpStatusCode.NoContent, await ben.Http.PutAsJsonAsync(new Uri($"/api/apps/{id}/data/choice?mine=true", UriKind.Relative), new { value = "red" }));
        Assert.Equal("red", (await ben.JsonAsync(await ben.GetAsync($"/api/apps/{id}/data/choice?mine=true"))).GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, (await ana.JsonAsync(await ana.GetAsync($"/api/apps/{id}/data/choice?mine=true"))).GetProperty("value").ValueKind);
        Assert.Equal(["votes"], (await ana.JsonAsync(await ana.GetAsync($"/api/apps/{id}/data"))).EnumerateArray().Select(d => d.GetProperty("key").GetString()));
        await StatusAssert.Is(HttpStatusCode.NoContent, await ana.Http.DeleteAsync(new Uri($"/api/apps/{id}/data/votes", UriKind.Relative)));
        Assert.Empty((await ben.JsonAsync(await ben.GetAsync($"/api/apps/{id}/data"))).EnumerateArray());

        // A private app's data and asks are its owner's alone.
        var mine = (await ana.JsonAsync(await ana.PostAsync("/api/apps", new { name = "Diary", kind = "react", code = "export default function App() { return <p>Hi</p> }" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/apps/{mine}/data"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.PostAsync($"/api/apps/{mine}/ask", new { prompt = "Hi" }));
    }
}
