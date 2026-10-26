using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Code Arena's working plan, kept with the chat its session syncs to: on the Plans board, read back, changed only by Code Arena.</summary>
[Collection(nameof(AppCollection))]
public sealed class CodeArenaPlanTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("caplan_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-plan-tests" });

    private static async Task<(TestBrowser Browser, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!);
    }

    private static HttpClient CodeArena(WebApplicationFactory<Program> f, string key)
    {
        var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        return http;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();

    private static object Plan(params object[] steps) => new { title = "Rename the parser", goal = "parse() is parse_frame() everywhere, tests pass", place = "codec", steps };

    [Fact]
    public async Task Code_Arenas_plan_is_kept_with_its_chat_on_the_board_and_read_back_and_only_Code_Arena_changes_it()
    {
        await using var f = NewApp();
        var (person, key) = await PersonAsync(f);
        var arena = CodeArena(f, key);
        var chat = (await Json(await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "s1", place = "/w/codec" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await arena.GetAsync($"/api/code-arena/chats/{chat}/plan"));

        var put = await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/plan", Plan(
            new { number = 1, title = "Find every caller", status = "done", check = "grep -rn 'parse(' src is empty", checkResult = "passed", checkOutput = "0 matches", checkedAt = DateTimeOffset.UtcNow },
            new { number = 2, title = "Rename it", status = "doing", files = new[] { "src/parse.c", "src\\parse.h", "src/parse.c" }, dependsOn = new[] { 1 }, check = "make" },
            new { number = 3, title = "Run the tests", check = "make test", dependsOn = new[] { 2 } }));
        await StatusAssert.Is(HttpStatusCode.OK, put);
        var plan = await Json(put);
        var id = plan.GetProperty("id").GetGuid();
        Assert.Equal($"/plans/{id}", plan.GetProperty("link").GetString());
        Assert.Equal("code", plan.GetProperty("source").GetString());
        Assert.Equal("codec", plan.GetProperty("place").GetString());
        var steps = plan.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["src/parse.c", "src/parse.h"], steps[1].GetProperty("files").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("passed", steps[0].GetProperty("checkResult").GetString());
        Assert.Equal("make test", steps[2].GetProperty("check").GetString());
        Assert.Equal(JsonValueKind.Null, steps[2].GetProperty("checkResult").ValueKind);

        // On the person's board, as Code Arena's; read there, not changed there.
        var listed = (await person.JsonAsync(await person.GetAsync("/api/plans"))).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal(("code", "codec", 3, 1), (listed.GetProperty("source").GetString(), listed.GetProperty("place").GetString(), listed.GetProperty("steps").GetInt32(), listed.GetProperty("done").GetInt32()));
        var moved = await person.Http.PatchAsJsonAsync(new Uri($"/api/plans/{id}/steps/2", UriKind.Relative), new { status = "done" });
        await StatusAssert.Is(HttpStatusCode.Conflict, moved);
        Assert.Contains("Code Arena", await moved.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Proposed in plan mode: the board says it waits for its review in Code Arena.
        var proposed = await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/plan", new { title = "Rename the parser", review = "proposed", steps = new[] { new { number = 1, title = "Find every caller" } } });
        Assert.Equal("proposed", (await Json(proposed)).GetProperty("review").GetString());
        Assert.Equal("proposed", (await person.JsonAsync(await person.GetAsync($"/api/plans/{id}"))).GetProperty("review").GetString());

        // Sent again whole: the same plan, its steps as Code Arena has them now.
        var againRes = await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/plan", Plan(
            new { number = 1, title = "Find every caller", status = "done" },
            new { number = 3, title = "Run the tests", status = "done", checkResult = "failed", checkOutput = "1 failed" }));
        await StatusAssert.Is(HttpStatusCode.OK, againRes);
        var again = await Json(againRes);
        Assert.Equal(id, again.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("review").ValueKind);
        Assert.Equal([1, 3], again.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("number").GetInt32()));
        var read = await Json(await arena.GetAsync($"/api/code-arena/chats/{chat}/plan"));
        Assert.Equal("failed", read.GetProperty("steps")[1].GetProperty("checkResult").GetString());

        // What does not hold is refused, and the plan stays as it was.
        var circle = await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/plan", Plan(
            new { number = 1, title = "A", dependsOn = new[] { 2 } }, new { number = 2, title = "B", dependsOn = new[] { 1 } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, circle);
        Assert.Contains("circle", await circle.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/plan", Plan(new { number = 1, title = "A" }, new { number = 1, title = "B" })));
        Assert.Equal(2, (await Json(await arena.GetAsync($"/api/code-arena/chats/{chat}/plan"))).GetProperty("steps").GetArrayLength());

        // Someone else's key reaches no plan of this chat; the plan goes when Code Arena drops it.
        var (_, otherKey) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await CodeArena(f, otherKey).GetAsync($"/api/code-arena/chats/{chat}/plan"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await arena.DeleteAsync($"/api/code-arena/chats/{chat}/plan"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await person.GetAsync($"/api/plans/{id}"));
    }
}
