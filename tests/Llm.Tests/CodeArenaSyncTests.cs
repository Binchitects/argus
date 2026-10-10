using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Code Arena's sessions and the person's chats here, kept in step both ways with their API key.</summary>
[Collection(nameof(AppCollection))]
public sealed class CodeArenaSyncTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("sync_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-sync-tests" });

    private static async Task<(TestBrowser Browser, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "s" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!);
    }

    /// <summary>Code Arena: no cookies, the person's key.</summary>
    private static HttpClient CodeArena(WebApplicationFactory<Program> f, string? key)
    {
        var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        if (key is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        http.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        return http;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task A_Code_Arena_session_is_a_chat_here_continued_on_the_web_and_back()
    {
        await using var f = NewApp();
        var (web, key) = await PersonAsync(f);
        var arena = CodeArena(f, key);

        var made = await Json(await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "20261010-1200-abc123", place = "/home/pat/parser" }));
        var id = made.GetProperty("id").GetGuid();
        Assert.True(made.GetProperty("created").GetBoolean());
        var again = await Json(await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "20261010-1200-abc123" }));
        Assert.Equal(id, again.GetProperty("id").GetGuid());
        Assert.False(again.GetProperty("created").GetBoolean());

        var calls = new[] { new { id = "c1", type = "function", function = new { name = "read_file", arguments = """{"path":"src/parser.cs"}""" } } };
        object[] turn =
        [
            new { role = "user", content = "Fix the parser\nit drops the last token" },
            new { role = "assistant", content = "", toolCalls = calls, model = "Qwen3.8-Flash-Next", promptTokens = 900, completionTokens = 20 },
            new { role = "tool", content = "class Parser { /* marker-7f3 */ }", toolCallId = "c1", name = "read_file" },
            new { role = "assistant", content = "Fixed: the loop stopped one short.", model = "Qwen3.8-Flash-Next" },
        ];
        var sent = await arena.PostAsJsonAsync($"/api/code-arena/chats/{id}/messages", new { after = 0, messages = turn });
        await StatusAssert.Is(HttpStatusCode.OK, sent);
        Assert.Equal(4, (await Json(sent)).GetProperty("count").GetInt32());

        // On the web: in the chat list with its folder, titled by its first question, with the calls as they were.
        var list = await web.JsonAsync(await web.GetAsync("/api/chat/conversations"));
        var row = list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);
        Assert.Equal("code-arena", row.GetProperty("origin").GetString());
        Assert.Equal("/home/pat/parser", row.GetProperty("originPlace").GetString());
        Assert.Equal("Fix the parser", row.GetProperty("title").GetString());
        var chat = await web.JsonAsync(await web.GetAsync($"/api/chat/conversations/{id}"));
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "tool", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("read_file", messages[1].GetProperty("toolCalls")[0].GetProperty("function").GetProperty("name").GetString());

        // Continued on the web: the model hears the whole session, Code Arena's calls and results with it.
        var events = await (await web.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "And the tests? marker-q2" })).Content.ReadAsStringAsync();
        Assert.Contains("\"done\"", events, StringComparison.Ordinal);
        var asked = app.Model.Requests.Last(r => r.Body.ToJsonString().Contains("marker-q2", StringComparison.Ordinal)).Body.ToJsonString();
        Assert.Contains("marker-7f3", asked, StringComparison.Ordinal);
        Assert.Contains("\"tool_call_id\":\"c1\"", asked, StringComparison.Ordinal);

        // Code Arena behind: what it sends is refused with what it lacks; it takes those in, then sends again.
        var behind = await arena.PostAsJsonAsync($"/api/code-arena/chats/{id}/messages", new { after = 4, messages = new[] { new { role = "user", content = "next" } } });
        Assert.Equal(HttpStatusCode.Conflict, behind.StatusCode);
        var missing = await Json(behind);
        Assert.Equal(6, missing.GetProperty("count").GetInt32());
        Assert.Equal("And the tests? marker-q2", missing.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("assistant", missing.GetProperty("messages")[1].GetProperty("role").GetString());
        var read = await Json(await arena.GetAsync($"/api/code-arena/chats/{id}?after=4"));
        Assert.Equal(2, read.GetProperty("messages").GetArrayLength());
        var caught = await arena.PostAsJsonAsync($"/api/code-arena/chats/{id}/messages", new { after = 6, messages = new[] { new { role = "user", content = "next" } } });
        Assert.Equal(7, (await Json(caught)).GetProperty("count").GetInt32());

        // Code Arena lists the person's chats, to continue one there.
        var chats = await Json(await arena.GetAsync("/api/code-arena/chats"));
        Assert.Contains(chats.GetProperty("chats").EnumerateArray(), c => c.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Only_the_owner_s_key_reads_or_adds_to_a_chat()
    {
        await using var f = NewApp();
        var (_, key) = await PersonAsync(f);
        var (_, otherKey) = await PersonAsync(f);
        var id = (await Json(await CodeArena(f, key).PostAsJsonAsync("/api/code-arena/chats", new { @ref = "r1" }))).GetProperty("id").GetGuid();

        await StatusAssert.Is(HttpStatusCode.Unauthorized, await CodeArena(f, null).GetAsync("/api/code-arena/chats"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await CodeArena(f, "sk-not-a-key").GetAsync($"/api/code-arena/chats/{id}"));
        var other = CodeArena(f, otherKey);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/code-arena/chats/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsJsonAsync($"/api/code-arena/chats/{id}/messages", new { after = 0, messages = new[] { new { role = "user", content = "x" } } }));
        // The same session id from someone else is their own chat.
        Assert.NotEqual(id, (await Json(await other.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "r1" }))).GetProperty("id").GetGuid());
        await StatusAssert.Is(HttpStatusCode.BadRequest,
            await CodeArena(f, key).PostAsJsonAsync($"/api/code-arena/chats/{id}/messages", new { after = 0, messages = new[] { new { role = "system", content = "x" } } }));
    }
}
