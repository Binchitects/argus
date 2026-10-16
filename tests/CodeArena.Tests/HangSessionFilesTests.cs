using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// Long sessions' files stay fast to list and read: each file is scanned once and then only what was appended, and a
/// session is read back from its last compaction, with the same result as reading it whole.
/// </summary>
public sealed class HangSessionFilesTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("arena-sessions-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    /// <summary>A session as Code Arena writes one: messages, answers' tokens, models, to-do lists, sync states, web messages and compactions.</summary>
    private static SessionStore Write(string dir, int compactions, int perPart, int size = 200)
    {
        var store = SessionStore.Create(dir, "/work/project", "model-a");
        var history = new List<JsonObject>();
        for (var c = 0; c <= compactions; c++)
        {
            for (var i = 0; i < perPart; i++)
            {
                var question = Message("user", $"question {c}.{i} " + new string('q', size));
                store.Message(question);
                history.Add(question);
                store.Usage(new TokenUsage(1000 + i, 500, 20 + c));
                var answer = Message("assistant", $"answer {c}.{i} " + new string('a', size));
                store.Message(answer);
                history.Add(answer);
                if (i % 3 == 0)
                {
                    var web = Message("user", $"from the web {c}.{i}");
                    store.Remote(web);
                    history.Add(web);
                }
            }
            store.Todos([new TodoItem($"step {c}", "in_progress")]);
            store.Sync("chat-1", c * 10, c * 7);
            if (c == 1)
            {
                store.Model("model-b");
            }
            if (c < compactions)
            {
                history = [Message("user", $"[The conversation so far, summarized to fit the model's window]\n\nsummary {c}"), Message("assistant", "Understood."), .. history.TakeLast(4)];
                store.Compacted(history);
            }
        }
        return store;
    }

    private static void Same(SessionData whole, SessionData fast)
    {
        Assert.Equal(whole.Cwd, fast.Cwd);
        Assert.Equal(whole.Model, fast.Model);
        Assert.Equal(whole.Messages.Select(m => m.ToJsonString()), fast.Messages.Select(m => m.ToJsonString()));
        Assert.Equal(whole.Answers.OrderBy(a => a.Key).Select(a => $"{a.Key} {a.Value.Model} {a.Value.Usage}"), fast.Answers.OrderBy(a => a.Key).Select(a => $"{a.Key} {a.Value.Model} {a.Value.Usage}"));
        Assert.Equal(whole.Known, fast.Known);
        Assert.Equal(whole.Todos, fast.Todos);
        Assert.Equal((whole.Prompt, whole.Cached, whole.Completion), (fast.Prompt, fast.Cached, fast.Completion));
        Assert.Equal((whole.Conversation, whole.ServerCount, whole.Pushed, whole.Unlinked), (fast.Conversation, fast.ServerCount, fast.Pushed, fast.Unlinked));
    }

    [Fact]
    public void A_session_read_from_its_last_compaction_is_the_session_read_whole()
    {
        var store = Write(_dir, compactions: 5, perPart: 12);
        Same(SessionStore.Load(store.File, whole: true), SessionStore.Load(store.File));

        // Appended to after it was scanned (a turn going on): read again from where the scan stopped, the same.
        store.Message(Message("user", "one more"));
        store.Usage(new TokenUsage(7, 0, 3));
        store.Message(Message("assistant", "and its answer"));
        store.Compacted([Message("user", "[The conversation so far, summarized to fit the model's window]\n\nlast"), Message("assistant", "Understood.")]);
        store.Message(Message("user", "after the last"));
        Same(SessionStore.Load(store.File, whole: true), SessionStore.Load(store.File));

        // The scan starts the read at the last compaction's line.
        var scan = SessionStore.Scan(store.File);
        using var stream = File.OpenRead(store.File);
        stream.Seek(scan.LastCompact, SeekOrigin.Begin);
        Assert.StartsWith("{\"type\":\"compact\"", new StreamReader(stream).ReadLine());
    }

    [Fact]
    public void A_line_cut_short_is_read_once_it_is_whole()
    {
        var store = Write(_dir, compactions: 1, perPart: 2);
        var before = SessionStore.Scan(store.File).Messages;
        // Half a line, as a program stopped mid-write leaves it, then the rest of it.
        var line = Json.Line(new JsonObject { ["type"] = "message", ["message"] = Message("user", "written in two goes") });
        File.AppendAllText(store.File, line[..20]);
        Assert.Equal(before, SessionStore.Scan(store.File).Messages);
        File.AppendAllText(store.File, line[20..] + "\n");
        Assert.Equal(before + 1, SessionStore.Scan(store.File).Messages);
        Assert.Equal("written in two goes", SessionStore.Load(store.File).Messages[^1]["content"]!.GetValue<string>());
    }

    [Fact]
    public void Listing_scans_each_file_once_and_then_only_what_was_added()
    {
        var stores = Enumerable.Range(0, 6).Select(_ => Write(_dir, compactions: 8, perPart: 30, size: 4000)).ToList();
        var clock = Stopwatch.StartNew();
        var first = SessionStore.List(_dir, "/work/project", 100);
        var cold = clock.Elapsed;
        clock.Restart();
        var again = SessionStore.List(_dir, "/work/project", 100);
        var warm = clock.Elapsed;
        log.WriteLine($"{stores.Count} files, {stores.Sum(s => new FileInfo(s.File).Length) / 1024} KB: listed in {cold.TotalMilliseconds:0} ms, again in {warm.TotalMilliseconds:0} ms");
        Assert.Equal(first, again);
        // Unchanged: the very same scan.
        Assert.Same(SessionStore.Scan(stores[0].File), SessionStore.Scan(stores[0].File));

        var summary = first.Single(s => s.File == stores[0].File);
        Assert.StartsWith("question 0.0", summary.Preview);
        Assert.Equal(9 * 30 * 2 + 9 * 10, summary.Messages);
        Assert.Equal(9 * 30 * 2, summary.Local);
        Assert.Equal(("chat-1", true, 56), (summary.Conversation, summary.Synced, summary.Pushed));

        // Appended to: listed with what was added.
        stores[0].Message(Message("user", "one more"));
        stores[0].Sync("chat-2", 1, 1);
        var after = SessionStore.List(_dir, "/work/project", 100).Single(s => s.File == stores[0].File);
        Assert.Equal(summary.Messages + 1, after.Messages);
        Assert.Equal("chat-2", after.Conversation);
        Assert.Equal(stores[0].File, SessionStore.Linked(_dir, "chat-2", "/work/project"));
        Assert.Null(SessionStore.Linked(_dir, "chat-2", "/elsewhere"));
        Assert.Empty(SessionStore.List(_dir, "/elsewhere", 100));
    }
}
