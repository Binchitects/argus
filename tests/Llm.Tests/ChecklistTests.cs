using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Checklists: a person's own, turned on in a chat, gone through and ticked by the model and the person, the same in Code Arena.</summary>
[Collection(nameof(AppCollection))]
public sealed class ChecklistTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("checklist_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static async Task<(TestBrowser Browser, string Key, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "cl" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!, $"{name}@example.test");
    }

    private static HttpClient CodeArena(WebApplicationFactory<Program> f, string key)
    {
        var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        return http;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();

    private static IEnumerable<(bool Done, string? By, string? Note)> Ticks(JsonElement run) =>
        run.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("done").GetBoolean(), i.GetProperty("by").GetString(), i.GetProperty("note").GetString()));

    [Fact]
    public async Task A_checklist_turned_on_in_a_chat_is_ticked_by_the_model_and_the_person_and_Code_Arena_sees_and_ticks_the_same()
    {
        await using var f = NewApp();
        var (me, key, email) = await PersonAsync(f);
        var (other, _, _) = await PersonAsync(f);

        // One's own: a name and 1 to 50 items, empty lines left out.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/checklists", new { name = "Empty", items = new[] { " " } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync("/api/checklists", new { name = "Long", items = Enumerable.Range(1, 51).Select(i => $"item {i}") }));
        var made = await me.PostAsync("/api/checklists", new { name = "Done means done", description = "Before a change is done", items = new[] { "The tests pass", "", "The docs say what changed" } });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var list = await me.JsonAsync(made);
        Assert.Equal(["The tests pass", "The docs say what changed"], list.GetProperty("items").EnumerateArray().Select(i => i.GetString()));
        Assert.Empty((await other.JsonAsync(await other.GetAsync("/api/checklists"))).EnumerateArray());

        // Turned on in a chat: none ticked; nobody else's to turn on or read.
        var chat = (await me.JsonAsync(await me.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.GetAsync($"/api/chat/conversations/{chat}/checklist"));
        var on = new { checklistId = list.GetProperty("id").GetGuid() };
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/checklist", UriKind.Relative), on));
        var otherChat = (await other.JsonAsync(await other.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await other.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{otherChat}/checklist", UriKind.Relative), on));
        var run = await me.JsonAsync(await me.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/checklist", UriKind.Relative), on));
        Assert.Equal((0, 2), (run.GetProperty("done").GetInt32(), run.GetProperty("total").GetInt32()));

        // The answer goes through it: told the items, given checklist_check, and it ticks one, saying how.
        var sent = await me.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = """Make the change. [call checklist_check {"item":1,"note":"42 of 42 pass"}]""" });
        await sent.Content.ReadAsStringAsync();
        var asked = app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email && r.Body["tools"] is not null).Body;
        Assert.Contains("checklist_check", asked["tools"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Checklist \\u0022Done means done\\u0022 (0 of 2 done)", asked["messages"]!.ToJsonString(), StringComparison.Ordinal);
        run = await me.JsonAsync(await me.GetAsync($"/api/chat/conversations/{chat}/checklist"));
        Assert.Equal([(true, "model", "42 of 42 pass"), (false, null, null)], Ticks(run));

        // The person ticks too; Code Arena, with their key, sees the same and ticks as they do.
        run = await me.JsonAsync(await me.PostAsync($"/api/chat/conversations/{chat}/checklist/tick", new { item = 2 }));
        Assert.Equal((2, "person"), (run.GetProperty("done").GetInt32(), Ticks(run).Last().By));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.PostAsync($"/api/chat/conversations/{chat}/checklist/tick", new { item = 3 }));
        var arena = CodeArena(f, key);
        Assert.Equal("Done means done", (await Json(await arena.GetAsync("/api/code-arena/checklists")))[0].GetProperty("name").GetString());
        Assert.Equal(2, (await Json(await arena.GetAsync($"/api/code-arena/chats/{chat}/checklist"))).GetProperty("done").GetInt32());
        run = await Json(await arena.PostAsJsonAsync($"/api/code-arena/chats/{chat}/checklist/tick", new { item = 1, done = false, by = "person" }));
        Assert.Equal([(false, null, null), (true, "person", null)], Ticks(run));
        await StatusAssert.Is(HttpStatusCode.NotFound, await arena.GetAsync($"/api/code-arena/chats/{otherChat}/checklist"));

        // The checklist changed or deleted: the chat keeps its copy, until turned off.
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.Http.DeleteAsync(new Uri($"/api/checklists/{on.checklistId}", UriKind.Relative)));
        run = await me.JsonAsync(await me.GetAsync($"/api/chat/conversations/{chat}/checklist"));
        Assert.Equal((JsonValueKind.Null, 2), (run.GetProperty("checklistId").ValueKind, run.GetProperty("total").GetInt32()));
        await StatusAssert.Is(HttpStatusCode.NoContent, await arena.DeleteAsync($"/api/code-arena/chats/{chat}/checklist"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.GetAsync($"/api/chat/conversations/{chat}/checklist"));

        // Off: the answers have no checklist.
        var after = await me.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Anything else?" });
        await after.Content.ReadAsStringAsync();
        var plain = app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email && r.Body["messages"]!.ToJsonString().Contains("Anything else?", StringComparison.Ordinal)).Body;
        Assert.DoesNotContain("checklist_check", plain["tools"]?.ToJsonString() ?? "", StringComparison.Ordinal);
    }
}
