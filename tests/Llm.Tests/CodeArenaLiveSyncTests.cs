extern alias CodeArenaCli;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Ca = CodeArenaCli::CodeArena;

namespace Llm.Tests;

/// <summary>
/// Code Arena's own sync (ChatSync, its session file) against this app, end to end: a session's messages, new and
/// changed, both ways, after a compaction on either side and after Code Arena starts again.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class CodeArenaLiveSyncTests(AppFixture app) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly string _dir = Directory.CreateTempSubdirectory("code-arena-sessions-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("casync_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-sync-tests" });

    private static async Task<(TestBrowser Web, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "c" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!);
    }

    /// <summary>Code Arena's side: its config for this app (its key, its address), and its own HTTP client reaching the app.</summary>
    private static (Ca.Config Config, HttpClient Http) CodeArena(WebApplicationFactory<Program> f, string key) =>
        (new Ca.Config { Url = $"https://{AppFixture.Domain}", ApiKey = key, SyncChats = true }, new HttpClient(f.Server.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan });

    private static JsonObject Said(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    /// <summary>The chat's branch on screen, as the web shows it: each message's id, role, words and summary.</summary>
    private static async Task<List<(Guid Id, string Role, string Content, string? Summary)>> WebChatAsync(TestBrowser web, string chat)
    {
        var read = await web.JsonAsync(await web.GetAsync($"/api/chat/conversations/{chat}"));
        var all = read.GetProperty("messages").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetGuid());
        static Guid? Id(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetGuid();
        var branch = new List<(Guid, string, string, string?)>();
        for (var at = Id(read.GetProperty("currentLeafId")); at is { } id && all.TryGetValue(id, out var m); at = Id(m.GetProperty("parentId")))
        {
            branch.Add((id, m.GetProperty("role").GetString()!, m.GetProperty("content").GetString() ?? "",
                m.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null));
        }
        branch.Reverse();
        return branch;
    }

    private static List<string> Contents(Ca.SessionStore store) => store.Read(d => d.Stream.Select(e => e.Message["content"]?.ToString() ?? "").ToList());

    [Fact]
    public async Task A_session_and_its_chat_stay_in_step_both_ways_through_edits_compactions_and_a_restart()
    {
        await using var f = NewApp();
        var (web, key) = await PersonAsync(f);
        var (config, http) = CodeArena(f, key);
        var notices = new List<string>();
        var store = Ca.SessionStore.Create(_dir, "/home/pat/shop", "model-a");
        var sync = Ca.ChatSync.Start(config, http, store, "/home/pat/shop", () => "model-a", (_, text) => notices.Add(text))!;
        Assert.NotNull(sync);

        // A new session: its chat is made in Arena with its first message, and every message follows.
        store.Message(Said("user", "Fix the parser"));
        store.Message(Said("assistant", "Fixed the parser."), "model-a", new Ca.TokenUsage(900, 0, 20));
        await Until(() => sync.InStep, "Arena has the session's first turn");
        var chat = sync.Conversation!;
        Assert.Equal(["Fix the parser", "Fixed the parser."], (await WebChatAsync(web, chat)).Select(m => m.Content));
        var listed = (await web.JsonAsync(await web.GetAsync("/api/chat/conversations"))).EnumerateArray().Single(c => c.GetProperty("id").GetString() == chat);
        Assert.Equal("code-arena", listed.GetProperty("origin").GetString());

        // The person carries on on the web: the session takes it in, once.
        await (await web.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "And the tests?" })).Content.ReadAsStringAsync();
        var taken = await sync.TakeInAsync(CancellationToken.None);
        Assert.Equal(2, taken.Added.Count);
        Assert.Equal("And the tests?", taken.Added[0]["content"]!.ToString());
        Assert.Equal(["Fix the parser", "Fixed the parser.", "And the tests?"], Contents(store).Take(3));
        Assert.Equal(4, store.Read(d => d.Stream.Count));
        Assert.False((await sync.TakeInAsync(CancellationToken.None)).Any);

        // The session goes on after it: Arena has its messages after the web's.
        store.Message(Said("user", "Run them"));
        store.Message(Said("assistant", "They pass."), "model-a");
        await Until(() => sync.InStep, "Arena has what the session added after the web's");
        Assert.Equal(["Fix the parser", "Fixed the parser.", "And the tests?", (await WebChatAsync(web, chat))[3].Content, "Run them", "They pass."],
            (await WebChatAsync(web, chat)).Select(m => m.Content));

        // A compaction here: Arena keeps its summary on the last message it covers, and the web's next answer reads it.
        var summaryText = "The parser was fixed and the tests pass.";
        store.Summarized(summaryText, 0);
        store.Compacted([Said("user", "[The conversation so far, summarized to fit the model's window]\n\n" + summaryText), Said("assistant", "Understood.")], 0);
        await Until(() => sync.InStep, "Arena has the summary");
        Assert.Equal(summaryText, (await WebChatAsync(web, chat))[^1].Summary);
        // What the session adds after its compaction goes on as before, and the summary does not come back to it.
        store.Message(Said("user", "Now the docs"));
        store.Message(Said("assistant", "Docs written."), "model-a");
        await Until(() => sync.InStep, "Arena has what came after the compaction");
        Assert.Equal(8, (await WebChatAsync(web, chat)).Count);
        Assert.False((await sync.TakeInAsync(CancellationToken.None)).Any);

        // Code Arena stops, and starts again later on the same session: nothing is sent twice, and both sides carry on.
        await sync.DisposeAsync();
        File.AppendAllText(store.File, "\n");
        var again = Ca.SessionStore.Open(store.File);
        Assert.NotSame(store, again);
        Assert.Equal(8, again.Read(d => d.Stream.Count));
        Assert.Equal(0, again.Read(d => d.Waiting));
        sync = Ca.ChatSync.Start(config, http, again, "/home/pat/shop", () => "model-a", (_, text) => notices.Add(text))!;
        await (await web.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Thanks, what is left?" })).Content.ReadAsStringAsync();
        taken = await sync.TakeInAsync(CancellationToken.None);
        Assert.Equal(["Thanks, what is left?"], taken.Added.Where(m => m["role"]!.ToString() == "user").Select(m => m["content"]!.ToString()));
        again.Message(Said("user", "Release it"));
        again.Message(Said("assistant", "Released."), "model-a");
        await Until(() => sync.InStep, "Arena has what the session added after it started again");
        var shown = await WebChatAsync(web, chat);
        Assert.Equal(12, shown.Count);
        Assert.Equal(["Release it", "Released."], shown.TakeLast(2).Select(m => m.Content));

        // A question edited on the web: the session follows the web's conversation from there.
        await (await web.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Thanks, what is left? (edited)", parentId = shown[7].Id })).Content.ReadAsStringAsync();
        taken = await sync.TakeInAsync(CancellationToken.None);
        Assert.NotNull(taken.History);
        Assert.Equal(10, again.Read(d => d.Stream.Count));
        Assert.Equal((await WebChatAsync(web, chat)).Select(m => m.Content), Contents(again));
        Assert.Equal("Thanks, what is left? (edited)", Contents(again)[8]);

        // Code Arena goes back to before that question (/rewind) and goes on otherwise: the web shows its new branch, the
        // edited one kept beside it.
        var model = again.Read(d => d.Messages.Count);
        await sync.RewindAsync(model - 2, CancellationToken.None);
        again.Message(Said("user", "Ship it"));
        again.Message(Said("assistant", "Shipped."), "model-a");
        await Until(() => sync.InStep, "Arena has the session's own branch");
        Assert.Equal([.. Contents(again)], (await WebChatAsync(web, chat)).Select(m => m.Content));
        Assert.Equal(["Ship it", "Shipped."], (await WebChatAsync(web, chat)).TakeLast(2).Select(m => m.Content));
        Assert.False((await sync.TakeInAsync(CancellationToken.None)).Any);

        // The web compacts its chat: the session's model reads the web's summary instead of what it covers, at once.
        var compacted = await web.PostAsync($"/api/chat/conversations/{chat}/compact", new { });
        Assert.Contains("\"compacted\"", await compacted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(await sync.LookAsync(CancellationToken.None));
        taken = await sync.TakeInAsync(CancellationToken.None);
        Assert.NotNull(taken.History);
        Assert.StartsWith("[The conversation so far, summarized", taken.History![0]["content"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, taken.History.Count);
        Assert.Equal(10, again.Read(d => d.Stream.Count));
        Assert.False(await sync.LookAsync(CancellationToken.None));
        await sync.DisposeAsync();
        Assert.DoesNotContain(notices, n => n.Contains("not in step", StringComparison.Ordinal));
    }
}
