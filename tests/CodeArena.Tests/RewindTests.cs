using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Rewind to here in the IDE, as /rewind in the terminal: back to before one of the person's questions, the conversation and
/// the code, only the conversation, or only the code (the same checkpoints), with a preview of the files that would change; the
/// agent's next turn works from there, and the chat in Arena shows the session rewound at once.
/// </summary>
public sealed class RewindTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    public RewindTests() => _gateway.Answer = Answer;

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    /// <summary>"write X" writes a.txt with X, "put F X" writes F with X; a summary is asked for; else the answer names the question.</summary>
    private static Reply Answer(JsonObject req)
    {
        if (FakeGateway.Summarizing(req))
        {
            return Reply.Say("SUMMARY: what was said.");
        }
        if (FakeGateway.History(req)[^1]!["role"]!.GetValue<string>() == "tool")
        {
            return Reply.Say("written");
        }
        var last = FakeGateway.Last(req);
        if (last.StartsWith("put ", StringComparison.Ordinal) && last[4..].Split(' ', 2) is [var path, var text])
        {
            return Reply.Call(("write_file", new JsonObject { ["path"] = path, ["content"] = text }.ToJsonString()));
        }
        return last.StartsWith("write ", StringComparison.Ordinal)
            ? Reply.Call(("write_file", new JsonObject { ["path"] = "a.txt", ["content"] = last[6..] }.ToJsonString()))
            : Reply.Say($"answer to {last}");
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    private static List<string> Said(JsonNode session) => [.. session["messages"]!.AsArray().Select(m => m!["content"]?.GetValue<string>() ?? "")];

    private static int Count(JsonNode session) => session["messages"]!.AsArray().Count;

    private List<string> Asked() => [.. FakeGateway.History(_gateway.Requests[^1]).Select(m => m!["content"] is JsonValue v ? v.GetValue<string>() : "")];

    private static string Content(JsonObject m) => m["content"]!.ToString();

    [Fact]
    public async Task Rewind_to_here_shows_the_files_that_would_change_then_puts_back_the_conversation_and_the_code()
    {
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "auto-edit");
        var file = h.Write("a.txt", "before");
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();
        await (await web.SendAsync("write two")).RestAsync();
        await (await web.SendAsync("write three")).RestAsync();
        Assert.Equal("three", File.ReadAllText(file));
        Assert.Equal(10, Count(await web.GetJsonAsync("/api/session")));

        // From the second question: its turn and the last wrote a.txt, which goes back to what it held before them.
        var preview = await web.GetJsonAsync("/api/rewind?message=m2");
        Assert.Equal(8, preview["messages"]!.GetValue<int>());
        Assert.True(preview["code"]!.GetValue<bool>());
        var change = Assert.Single(preview["files"]!.AsArray());
        Assert.Equal(("a.txt", false), (change!["path"]!.GetValue<string>(), change["removed"]!.GetValue<bool>()));

        var rewound = await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m2" });
        Assert.Equal(HttpStatusCode.OK, rewound.StatusCode);
        Assert.Contains("1 file put back", (await rewound.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Equal("before", File.ReadAllText(file));
        Assert.Equal(["first", "answer to first"], Said(await web.GetJsonAsync("/api/session")));

        // The agent's next turn works from there.
        await (await web.SendAsync("instead")).RestAsync();
        Assert.Equal(["first", "answer to first", "instead"], Asked());
        Assert.Equal("m2", (await web.GetJsonAsync("/api/session"))["messages"]![2]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_conversation_or_only_the_code_goes_back()
    {
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "auto-edit");
        var file = h.Write("a.txt", "before");
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("write two")).RestAsync();
        await (await web.SendAsync("second")).RestAsync();

        // Only the code: the file as it was, the conversation as it is.
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m0", ["chat"] = false })).StatusCode);
        Assert.Equal("before", File.ReadAllText(file));
        Assert.Equal(6, Count(await web.GetJsonAsync("/api/session")));
        // Its checkpoint is used: the code cannot go back from there twice.
        Assert.False((await web.GetJsonAsync("/api/rewind?message=m0"))["code"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m0", ["chat"] = false })).StatusCode);

        // Only the conversation: the file stays as it is.
        await (await web.SendAsync("write three")).RestAsync();
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m6", ["code"] = false })).StatusCode);
        Assert.Equal("three", File.ReadAllText(file));
        Assert.Equal(6, Count(await web.GetJsonAsync("/api/session")));
    }

    [Fact]
    public async Task After_only_the_conversation_went_back_an_earlier_rewind_of_the_code_puts_back_what_the_dropped_turns_wrote()
    {
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "auto-edit");
        var calc = h.Write("calc.py", "def add");
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("put a.txt one")).RestAsync();
        await (await web.SendAsync("put calc.py def sub")).RestAsync();
        await (await web.SendAsync("put b.txt three")).RestAsync();

        // The second question with only the conversation: calc.py and b.txt stay as its turns wrote them.
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m4", ["code"] = false })).StatusCode);
        Assert.Equal("def sub", File.ReadAllText(calc));
        await (await web.SendAsync("put c.txt four")).RestAsync();

        // Then the first with the conversation and the code: as before it, calc.py too, and listed so first.
        var preview = await web.GetJsonAsync("/api/rewind?message=m0");
        Assert.Equal([("a.txt", true), ("b.txt", true), ("c.txt", true), ("calc.py", false)],
            preview["files"]!.AsArray().Select(f => (f!["path"]!.GetValue<string>(), f["removed"]!.GetValue<bool>())).Order());
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m0" })).StatusCode);
        Assert.Equal("def add", File.ReadAllText(calc));
        Assert.All(new[] { "a.txt", "b.txt", "c.txt" }, f => Assert.False(File.Exists(Path.Combine(h.Work, f))));

        // The first question with only the conversation, then a new first one: going back to it puts back what both wrote.
        await (await web.SendAsync("put d.txt five")).RestAsync();
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m0", ["code"] = false })).StatusCode);
        await (await web.SendAsync("put e.txt six")).RestAsync();
        Assert.Equal([("d.txt", true), ("e.txt", true)],
            (await web.GetJsonAsync("/api/rewind?message=m0"))["files"]!.AsArray().Select(f => (f!["path"]!.GetValue<string>(), f["removed"]!.GetValue<bool>())).Order());
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m0" })).StatusCode);
        Assert.False(File.Exists(Path.Combine(h.Work, "d.txt")));
        Assert.False(File.Exists(Path.Combine(h.Work, "e.txt")));
    }

    [Fact]
    public async Task Rewind_in_the_terminal_goes_back_in_a_resumed_session_as_the_IDE_does()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("first\nsecond\n/exit\n", "chat"));
        await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 4, "Arena has both turns");

        // Resumed: the questions are the session's, before this run too; with no checkpoint, only the conversation goes back.
        Assert.Equal(0, await h.Run("/rewind\n/rewind 2 chat\nafter\n/exit\n", "chat", "--continue"));
        Assert.Matches(@" 1\. .*first.*conversation only", h.Out);
        Assert.Matches(@" 2\. .*second.*conversation only", h.Out);
        Assert.Contains("Back to before message 3: the conversation has 2 messages.", h.Out, StringComparison.Ordinal);
        Assert.Equal(["first", "answer to first", "after"], Asked());
        var chat = _mcp.Chats.All.Single();
        await Until(() => chat.Messages.Count == 4 && Content(chat.Messages[^1]) == "answer to after", "the web chat shows it");
        Assert.Equal(["first", "answer to first", "after", "answer to after"], chat.Messages.Select(Content));
    }

    [Fact]
    public async Task Only_a_question_of_the_person_is_rewound_to_and_not_while_an_answer_is_written()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();
        foreach (var bad in new[] { "m1", "m9", "first" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync($"/api/rewind?message={bad}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = bad })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m0", ["chat"] = false, ["code"] = false })).StatusCode);

        var held = new TaskCompletionSource();
        _gateway.Answer = req =>
        {
            held.Task.Wait(TimeSpan.FromSeconds(10));
            return Reply.Say("late");
        };
        var running = await web.SendAsync("slow");
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m0" })).StatusCode);
        held.SetResult();
        await running.RestAsync();
        Assert.Equal(4, Count(await web.GetJsonAsync("/api/session")));
    }

    [Fact]
    public async Task The_chat_in_Arena_shows_the_session_rewound_at_once_and_goes_on_from_there()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();
        await (await web.SendAsync("second")).RestAsync();
        await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 4, "Arena has both turns");
        var chat = _mcp.Chats.All.Single();

        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m2" })).StatusCode);
        // Before anything else is sent: the web's branch on screen ends where the session's does; the turn gone back from stays as another branch.
        await Until(() => chat.Messages.Count == 2, "the web shows the session rewound");
        Assert.Equal(["first", "answer to first"], chat.Messages.Select(Content));
        Assert.Equal(4, chat.Nodes.Count);

        await (await web.SendAsync("instead")).RestAsync();
        await Until(() => chat.Messages.Count == 4, "the turn after the rewind reaches Arena");
        Assert.Equal(["first", "answer to first", "instead", "answer to instead"], chat.Messages.Select(Content));
        Assert.Equal(chat.Nodes[1].Id, chat.Nodes.Single(n => Content(n.Message) == "second").Parent);
        Assert.Equal(chat.Nodes[1].Id, chat.Nodes.Single(n => Content(n.Message) == "instead").Parent);
    }

    [Fact]
    public async Task Rewound_to_before_a_compaction_the_model_reads_the_whole_messages_again()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();
        await (await web.SendAsync("second")).RestAsync();
        await (await web.StreamAsync(HttpMethod.Post, "/api/compact", new JsonObject())).RestAsync();
        await (await web.SendAsync("third")).RestAsync();
        Assert.StartsWith("[The conversation so far, summarized", Asked()[0], StringComparison.Ordinal);

        // The summary covers "second", which goes: not the summary, the messages before it.
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/rewind", new JsonObject { ["message"] = "m2" })).StatusCode);
        var session = await web.GetJsonAsync("/api/session");
        Assert.Equal(["first", "answer to first"], Said(session));
        Assert.All(session["messages"]!.AsArray(), m => Assert.Null(m!["summary"]));
        await (await web.SendAsync("instead")).RestAsync();
        Assert.Equal(["first", "answer to first", "instead"], Asked());
    }

    [Fact]
    public async Task Rewind_in_the_terminal_after_a_compaction_goes_back_to_where_that_turn_began()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("first\nsecond\n/compact\nthird\n/rewind 2 chat\nafter\n/exit\n", "chat"));
        // Not to after the compaction (its summary covers "second", gone now): to before "second".
        Assert.Equal(["first", "answer to first", "after"], Asked());
    }

    [Fact]
    public async Task Rewound_in_the_terminal_the_web_shows_it_at_once()
    {
        using var h = new Harness(_gateway, _mcp);
        var typed = new TypedLines();
        var env = new CliEnv
        {
            In = typed, Out = TextWriter.Synchronized(new StringWriter()), Err = TextWriter.Synchronized(new StringWriter()), Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work, Paths = h.Paths, InTerminal = true,
        };
        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        try
        {
            typed.Add("first");
            typed.Add("second");
            await Until(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 4, "Arena has both turns");
            typed.Add("/rewind 2 chat");
            var chat = _mcp.Chats.All.Single();
            // Not at the session's next message: now.
            await Until(() => chat.Messages.Count == 2, "the web shows the session rewound");
            Assert.Equal(["first", "answer to first"], chat.Messages.Select(Content));
        }
        finally
        {
            typed.Add("/exit");
            Assert.Equal(0, await run.WaitAsync(Deadline));
        }
    }
}
