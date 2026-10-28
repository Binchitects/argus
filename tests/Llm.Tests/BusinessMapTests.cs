using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Argus's business map: a project mapped by a model with Argus's read-only tools into what it does for the business (each
/// item with the sources it read), shown to those who may read the project, and used by the chat's Argus to explain the
/// business as the company's senior technical expert.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class BusinessMapTests(AppFixture app)
{
    /// <summary>Its own app and database: the maps are the company's, and must not leak into other tests.</summary>
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("business_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-business-tests", ["Argus:GitlabUrl"] = "https://gitlab.test" });

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "b" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    /// <summary>The map once it is no longer being made (the mapper runs in the background).</summary>
    private static async Task<JsonElement> MappedAsync(TestBrowser b, string id)
    {
        JsonElement got = default;
        for (var i = 0; i < 200; i++)
        {
            got = await b.JsonAsync(await b.GetAsync($"/api/code/business/{id}"));
            if (got.GetProperty("system").GetProperty("status").GetString() != "building")
            {
                return got;
            }
            await Task.Delay(50);
        }
        return got;
    }

    [Fact]
    public async Task A_project_is_mapped_with_what_it_does_for_the_business_each_item_with_its_sources()
    {
        app.Argus.Answers["issue_list"] = """{"result": []}""";
        await using var f = NewApp();
        var (b, email) = await PersonAsync(f);
        var project = "group/shop-" + Guid.NewGuid().ToString("N")[..6];

        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/code/business", new { project = "no-group" }));
        var made = await b.PostAsync("/api/code/business", new { project });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await b.JsonAsync(made)).GetProperty("id").GetString()!;
        var got = await MappedAsync(b, id);
        var system = got.GetProperty("system");
        Assert.True(system.GetProperty("status").GetString() == "ready", system.ToString());
        Assert.Equal(("ready", "Checkout", "online orders and payments"), (system.GetProperty("status").GetString(), system.GetProperty("name").GetString(), system.GetProperty("domain").GetString()));
        Assert.Equal(JsonValueKind.Null, system.GetProperty("error").ValueKind);
        Assert.Equal($"https://gitlab.test/{project}", got.GetProperty("web").GetString());

        // What it found, each with what it read; a guess with no source, and a source of no known kind, left out.
        var profile = got.GetProperty("profile");
        var rule = Assert.Single(profile.GetProperty("rules").EnumerateArray());
        Assert.Equal("Refunds round half up", rule.GetProperty("rule").GetString());
        Assert.Equal([("code", "src/refund.cs:22"), ("issue", "12")], rule.GetProperty("sources").EnumerateArray().Select(s => (s.GetProperty("kind").GetString(), s.GetProperty("ref").GetString())));
        Assert.Equal(["wiki"], profile.GetProperty("glossary")[0].GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));
        Assert.Equal(["The page calls cancel", "Payment is refunded", "Stock is put back"], profile.GetProperty("processes")[0].GetProperty("steps").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(1, system.GetProperty("counts").GetProperty("entities").GetInt32());

        // It read with Argus's tools, as the person who asked; its requests are theirs, tagged business.
        Assert.Contains(app.Argus.McpCalls, c => c.Method == "tools/call" && c.Email == email && c.Params?.GetProperty("name").GetString() == "find_symbol");
        Assert.Contains(app.Model.Requests, r => r.Body["user"]?.GetValue<string>() == email && r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == "business");
        var listed = (await b.JsonAsync(await b.GetAsync("/api/code/business"))).GetProperty("systems").EnumerateArray();
        Assert.Contains(listed, s => s.GetProperty("project").GetString() == project);
        // Mapped again on request.
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync($"/api/code/business/{id}/again", new { }));
        var again = (await MappedAsync(b, id)).GetProperty("system");
        Assert.True(again.GetProperty("status").GetString() == "ready", again.ToString());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains((await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray(),
            e => e.GetProperty("action").GetString() == "business.map" && e.GetProperty("target").GetString() == project);
    }

    [Fact]
    public async Task A_map_is_only_for_those_who_may_read_its_project_and_only_admins_remove_one()
    {
        app.Argus.Answers["issue_list"] = """{"result": []}""";
        await using var f = NewApp();
        var (b, _) = await PersonAsync(f);
        var project = "group/vault-" + Guid.NewGuid().ToString("N")[..6];
        var id = (await b.JsonAsync(await b.PostAsync("/api/code/business", new { project }))).GetProperty("id").GetString()!;
        await MappedAsync(b, id);

        // Someone Argus does not show the project to sees nothing of its map, and cannot map it.
        app.Argus.Unreadable[project] = true;
        try
        {
            var (c, _) = await PersonAsync(f);
            Assert.Empty((await c.JsonAsync(await c.GetAsync("/api/code/business"))).GetProperty("systems").EnumerateArray());
            await StatusAssert.Is(HttpStatusCode.NotFound, await c.GetAsync($"/api/code/business/{id}"));
            await StatusAssert.Is(HttpStatusCode.NotFound, await c.PostAsync("/api/code/business", new { project }));
            await StatusAssert.Is(HttpStatusCode.NotFound, await c.PostAsync($"/api/code/business/{id}/again", new { }));
        }
        finally
        {
            app.Argus.Unreadable.TryRemove(project, out _);
        }
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.Http.DeleteAsync(new Uri($"/api/code/business/{id}", UriKind.Relative)));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/code/business/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/code/business/{id}"));
    }

    [Fact]
    public async Task A_mapper_that_never_reports_fails_and_says_so()
    {
        app.Argus.Answers["issue_list"] = """{"result": []}""";
        await using var f = NewApp();
        var (b, _) = await PersonAsync(f);
        var id = (await b.JsonAsync(await b.PostAsync("/api/code/business", new { project = "group/silent" }))).GetProperty("id").GetString()!;
        var system = (await MappedAsync(b, id)).GetProperty("system");
        Assert.Equal("failed", system.GetProperty("status").GetString());
        Assert.StartsWith("The model stopped without reporting the map", system.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chats_Argus_explains_the_business_from_the_map_as_the_companys_senior_expert()
    {
        app.Argus.Answers["issue_list"] = """{"result": []}""";
        await using var f = NewApp();
        var (b, email) = await PersonAsync(f);
        var project = "group/refunds-" + Guid.NewGuid().ToString("N")[..6];
        var id = (await b.JsonAsync(await b.PostAsync("/api/code/business", new { project }))).GetProperty("id").GetString()!;
        await MappedAsync(b, id);

        var chat = await b.PostAsync("/api/chat/conversations", new { tools = new[] { "argus" } });
        var chatId = (await b.JsonAsync(chat)).GetProperty("id").GetString()!;
        var sent = await b.PostAsync($"/api/chat/conversations/{chatId}/messages", new { content = """Why do refunds round half up? [call business_explain {"question":"why do refunds round half up"}]""" });
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        await sent.Content.ReadAsStringAsync();

        // The chat's own request (not the mapper's, tagged business).
        var asked = app.Model.Requests.First(r => r.Body["user"]?.GetValue<string>() == email && r.Body["tools"] is not null
            && r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() != "business").Body;
        var functions = asked["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("business_map", functions);
        Assert.Contains("business_explain", functions);
        Assert.Contains("the company's technical mind", asked["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        var tool = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chatId}"))).GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
        var found = JsonDocument.Parse(tool).RootElement;
        Assert.Equal(("Checkout", "rule", "Refunds round half up"), (found[0].GetProperty("system").GetString(), found[0].GetProperty("kind").GetString(), found[0].GetProperty("title").GetString()));
        Assert.Equal(["code src/refund.cs:22", "issue 12"], found[0].GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
    }
}
