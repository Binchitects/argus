using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// "In code-arena loading chat and history and syncing with argus-arena is too laggy and slow": a session of 500 messages
/// (125 turns, each a tool call with its result and an answer; every other one an edit of 60 lines), compacted along the
/// way as a long session is, opened in the IDE and sent to Arena. Each test says its numbers.
/// </summary>
public sealed class LongSessionLoadTests(ITestOutputHelper log) : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private const int Turns = 125;

    /// <summary>The messages a compaction keeps whole: a quarter of a 262K window holds about this many such messages.</summary>
    private const int Kept = 60;

    private static string Code(int turn, string what) => string.Join('\n', Enumerable.Range(1, 60).Select(i => $"    var {what}{i} = Compute(\"{turn}\", {i}); // a line of code"));

    /// <summary>The session's file in the harness's sessions folder: 500 messages, its results cleared every 5 turns and compacted every 25.</summary>
    private static string LongSession(Harness h)
    {
        var store = SessionStore.Create(h.Paths.SessionsDir, h.Work, "model-a");
        var history = new List<JsonObject>();
        void Add(JsonObject m, TokenUsage? usage = null)
        {
            history.Add(m);
            store.Message(m, m.Str("role") == "assistant" ? "model-a" : null, usage);
        }
        for (var t = 0; t < Turns; t++)
        {
            Add(new JsonObject { ["role"] = "user", ["content"] = $"Question {t}: look at src/f{t}.cs and fix it." });
            var edit = t % 2 == 1;
            var args = edit
                ? new JsonObject { ["path"] = $"src/f{t}.cs", ["old_string"] = Code(t, "old"), ["new_string"] = Code(t, "fixed") }.ToJsonString()
                : $$"""{"path":"src/f{{t}}.cs"}""";
            Add(new JsonObject
            {
                ["role"] = "assistant", ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = $"c{t}", ["type"] = "function", ["function"] = new JsonObject { ["name"] = edit ? "edit_file" : "read_file", ["arguments"] = args } }),
            }, new TokenUsage(20_000, 18_000, 120));
            Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = $"c{t}", ["content"] = edit ? $"Edited src/f{t}.cs." : Code(t, "line") });
            Add(new JsonObject { ["role"] = "assistant", ["content"] = $"Fixed src/f{t}.cs: " + new string('x', 600) }, new TokenUsage(24_000, 20_000, 300));
            if (t % 5 == 4)
            {
                // Earlier results cleared to make room: the history the model reads is written again, every message in place.
                store.Compacted(history.Select(m => m.Clone()));
            }
            if (t % 25 == 24)
            {
                // A compaction writes the history the model reads from now on: a summary, and the recent turns whole.
                var kept = history.TakeLast(Kept).ToList();
                history = [new JsonObject { ["role"] = "user", ["content"] = Agent.SummaryHead + "\n\nWhat was done so far." }, new JsonObject { ["role"] = "assistant", ["content"] = Agent.Understood }, .. kept];
                store.Compacted(history, kept.Count);
            }
        }
        return store.File;
    }

    private static async Task<(JsonObject Session, int Bytes, long Ms)> ReadAsync(WebRun web, string path)
    {
        var clock = Stopwatch.StartNew();
        using var res = await web.Http.GetAsync(path);
        var body = await res.Content.ReadAsStringAsync();
        return (JsonNode.Parse(body)!.AsObject(), body.Length, clock.ElapsedMilliseconds);
    }

    [Fact]
    public async Task A_500_message_session_opens_in_the_IDE_with_its_last_turns_and_the_earlier_ones_when_asked_for()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = LongSession(h);
        var clock = Stopwatch.StartNew();
        var read = SessionStore.Load(file, whole: true);
        log.WriteLine($"session file: {new FileInfo(file).Length / 1024} KB, read whole in {clock.ElapsedMilliseconds} ms ({read.Stream.Count} messages)");
        clock.Restart();
        await using var web = await WebRun.StartAsync(h, "--continue");
        log.WriteLine($"code-arena web --continue: {clock.ElapsedMilliseconds} ms");

        // The page's first read: the last 30 turns (each edit's diff with them), and how many came before.
        var (first, bytes, ms) = await ReadAsync(web, "/api/session?turns=30");
        var messages = first["messages"]!.AsArray();
        log.WriteLine($"GET /api/session?turns=30: {bytes / 1024} KB, {messages.Count} messages, {first["diffs"]!.AsObject().Count} diffs, {ms} ms");
        Assert.Equal(120, messages.Count);
        Assert.Equal("m380", messages[0]!["id"]!.GetValue<string>());
        Assert.Equal(95, first["earlier"]!.GetValue<int>());
        Assert.Equal(15, first["diffs"]!.AsObject().Count);
        Assert.All(first["diffs"]!.AsObject(), d => Assert.True(int.Parse(d.Key[1..]) >= 380));
        Assert.Equal("edit_file", messages.OfType<JsonObject>().First(m => m.Str("role") == "tool").Str("toolName"));

        // The whole of it, as the page read it before.
        var (whole, wholeBytes, wholeMs) = await ReadAsync(web, "/api/session?turns=1000");
        log.WriteLine($"GET /api/session, whole: {wholeBytes / 1024} KB, {whole["messages"]!.AsArray().Count} messages, {whole["diffs"]!.AsObject().Count} diffs, {wholeMs} ms");
        Assert.Equal(500, whole["messages"]!.AsArray().Count);
        Assert.Equal(0, whole["earlier"]!.GetValue<int>());
        Assert.True(bytes * 3 < wholeBytes, $"{bytes} bytes for 30 turns, {wholeBytes} for all");

        // A compaction's reset of the page carries as many turns as the page reads now, not the whole session.
        await ReadAsync(web, "/api/session?turns=30");
        _gateway.Answer = req => FakeGateway.Summarizing(req) ? Reply.Say("The summary.") : Reply.Say("ok");
        using var compacting = await web.StreamAsync(HttpMethod.Post, "/api/compact", new JsonObject());
        var reset = await compacting.UntilAsync("reset");
        log.WriteLine($"a compaction's reset: {reset.ToJsonString().Length / 1024} KB, {reset["messages"]!.AsArray().Count} messages");
        Assert.Equal(120, reset["messages"]!.AsArray().Count);

        // Arena gets the whole session (it never had it).
        clock.Restart();
        while (_mcp.Chats.All.FirstOrDefault()?.Messages.Count is not 500 && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(20);
        }
        log.WriteLine($"sent to Arena: {_mcp.Chats.All.FirstOrDefault()?.Messages.Count} messages in {clock.ElapsedMilliseconds} ms");
        Assert.Equal(500, _mcp.Chats.All.Single().Messages.Count);
    }
}
