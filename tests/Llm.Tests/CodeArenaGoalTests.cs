using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Code Arena's goal kept with its chat (the web chat shows it), its sub-agents' summaries kept with their messages, and the
/// person's assistants for Code Arena, with the skills each uses.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class CodeArenaGoalTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("cagoal_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-goal-tests" });

    private static async Task<(TestBrowser Browser, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
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

    private static object Goal(string status, object? check = null, string? outcome = null) => new
    {
        text = "The tests pass", check = check ?? new { kind = "command", text = "dotnet test", by = "person" }, budget = new { steps = 200, minutes = 60, tokens = 5_000_000 },
        status, turns = 3, steps = 41, tokens = 812_000, ms = 420_000, passed = status == "met", said = "Exit code 0.", checkedAt = DateTimeOffset.UtcNow, outcome,
        started = DateTimeOffset.UtcNow.AddMinutes(-9), updated = DateTimeOffset.UtcNow, idle = 0,
    };

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Code_Arenas_goal_is_kept_with_its_chat_shown_on_the_web_and_only_Code_Arena_changes_it()
    {
        await using var f = NewApp();
        var (person, key) = await PersonAsync(f);
        var arena = CodeArena(f, key);
        var chat = (await Json(await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "s1", place = "/w/codec" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await arena.GetAsync(new Uri($"/api/code-arena/chats/{chat}/goal", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await person.GetAsync($"/api/chat/conversations/{chat}/goal"));

        await StatusAssert.Is(HttpStatusCode.OK, await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/goal", Goal("working")));
        var shown = await person.JsonAsync(await person.GetAsync($"/api/chat/conversations/{chat}/goal"));
        Assert.Equal(("The tests pass", "working", "dotnet test", "person"), (shown.GetProperty("text").GetString(), shown.GetProperty("status").GetString(),
            shown.GetProperty("check").GetProperty("text").GetString(), shown.GetProperty("check").GetProperty("by").GetString()));
        Assert.Equal((3, 41, 812_000L, 420_000L), (shown.GetProperty("turns").GetInt32(), shown.GetProperty("steps").GetInt32(), shown.GetProperty("tokens").GetInt64(), shown.GetProperty("ms").GetInt64()));
        Assert.Equal(200, shown.GetProperty("budget").GetProperty("steps").GetInt32());

        // Sent again whole as it ends: the same goal, as it is now.
        await StatusAssert.Is(HttpStatusCode.OK, await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/goal", Goal("met", outcome: "Goal met: `dotnet test` succeeded (exit code 0).")));
        var met = await Json(await arena.GetAsync(new Uri($"/api/code-arena/chats/{chat}/goal", UriKind.Relative)));
        Assert.Equal(("met", true), (met.GetProperty("status").GetString(), met.GetProperty("passed").GetBoolean()));
        Assert.StartsWith("Goal met:", met.GetProperty("outcome").GetString(), StringComparison.Ordinal);

        // What does not hold is refused; the goal stays as it was.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/goal", Goal("winning")));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/goal", Goal("working", new { kind = "vibes", text = "x" })));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await arena.PutAsJsonAsync($"/api/code-arena/chats/{chat}/goal", new { text = "", status = "working" }));
        Assert.Equal("met", (await Json(await arena.GetAsync(new Uri($"/api/code-arena/chats/{chat}/goal", UriKind.Relative)))).GetProperty("status").GetString());

        // Nobody else's key or session reaches it; it goes when Code Arena drops it.
        var (other, otherKey) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await CodeArena(f, otherKey).GetAsync(new Uri($"/api/code-arena/chats/{chat}/goal", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await CodeArena(f, otherKey).PutAsJsonAsync($"/api/code-arena/chats/{chat}/goal", Goal("working")));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/conversations/{chat}/goal"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await arena.DeleteAsync(new Uri($"/api/code-arena/chats/{chat}/goal", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await person.GetAsync($"/api/chat/conversations/{chat}/goal"));
    }

    [Fact]
    public async Task A_task_calls_sub_agents_reach_the_web_chat_as_summaries_without_their_steps()
    {
        await using var f = NewApp();
        var (person, key) = await PersonAsync(f);
        var arena = CodeArena(f, key);
        var chat = (await Json(await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "s2", place = "/w" }))).GetProperty("id").GetGuid();
        var agent = new
        {
            title = "find the parser", instructions = "Find the parser.", status = "done", ms = 4200, model = "m", usage = new { prompt = 900, cached = 300, completion = 80 },
            text = "Parser is in src/a.cs:1.", reasoning = "secret thoughts", steps = new[] { new { id = "c1", name = "grep", arguments = "{}", result = "src/a.cs:1" } },
        };
        var messages = new object[]
        {
            new { role = "user", content = "where is the parser?", @ref = "s2:0" },
            new { role = "assistant", content = "", model = "m", toolCalls = new[] { new { id = "call_1", type = "function", function = new { name = "task", arguments = "{\"description\":\"find the parser\"}" } } }, @ref = "s2:1" },
            new { role = "tool", content = "Parser is in src/a.cs:1.", toolCallId = "call_1", name = "task", @ref = "s2:2", details = new { agents = new[] { agent } } },
            new { role = "assistant", content = "It is in src/a.cs.", model = "m", @ref = "s2:3" },
        };
        await StatusAssert.Is(HttpStatusCode.OK, await arena.PostAsJsonAsync($"/api/code-arena/chats/{chat}/messages", new { parent = (Guid?)null, messages }));

        var read = await person.JsonAsync(await person.GetAsync($"/api/chat/conversations/{chat}"));
        var tool = read.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        var kept = tool.GetProperty("details").GetProperty("agents").EnumerateArray().Single();
        Assert.Equal(("find the parser", "done", "Parser is in src/a.cs:1.", 4200L), (kept.GetProperty("title").GetString(), kept.GetProperty("status").GetString(),
            kept.GetProperty("text").GetString(), kept.GetProperty("ms").GetInt64()));
        Assert.Equal(900, kept.GetProperty("usage").GetProperty("prompt").GetInt64());
        // Its steps and its thinking stay in Code Arena.
        Assert.Equal(0, kept.GetProperty("steps").GetArrayLength());
        Assert.Equal("", kept.GetProperty("reasoning").GetString());
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Code_Arena_lists_the_assistants_the_person_may_use_with_their_skills()
    {
        await using var f = NewApp();
        var (person, key) = await PersonAsync(f);
        var (stranger, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await person.PostAsync("/api/skills", new { name = "review", title = "Review code", instructions = "Review it." }));
        // A skill the person may not use is refused; their own is kept.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/assistants", new { name = "Reviewer", skills = new[] { "nope" } }));
        var made = await person.PostAsync("/api/assistants", new { name = "Reviewer", description = "Reviews changes", instructions = "Risks first.", skills = new[] { "review" } });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await person.JsonAsync(made)).GetProperty("id").GetGuid();
        Assert.Equal(["review"], (await person.JsonAsync(await person.GetAsync($"/api/assistants/{id}"))).GetProperty("skills").EnumerateArray().Select(s => s.GetString()));
        await StatusAssert.Is(HttpStatusCode.Created, await stranger.PostAsync("/api/assistants", new { name = "Theirs", instructions = "Not yours." }));

        var listed = (await Json(await CodeArena(f, key).GetAsync(new Uri("/api/code-arena/assistants", UriKind.Relative)))).EnumerateArray().ToList();
        var reviewer = Assert.Single(listed);
        Assert.Equal(("Reviewer", "Risks first.", "review", true), (reviewer.GetProperty("name").GetString(), reviewer.GetProperty("instructions").GetString(),
            reviewer.GetProperty("skills")[0].GetString(), reviewer.GetProperty("mine").GetBoolean()));
    }

    [Fact]
    public async Task Upgrading_keeps_the_assistants_and_gives_each_no_skills()
    {
        var cs = app.ConnectionStringFor("goalup_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_CodeArenaGoals", StringComparison.Ordinal)) - 1]);
        var user = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            await V61MigrationTests.InsertAsync(conn, "projects", new() { ["Id"] = Guid.NewGuid(), ["UserId"] = user, ["Name"] = "Old one" });
        }
        await db.Database.MigrateAsync();
        var kept = await db.Assistants.AsNoTracking().SingleAsync();
        Assert.Equal(("Old one", 0), (kept.Name, kept.Skills.Count));
        Assert.Empty(await db.ChatGoals.ToListAsync());
    }
}
