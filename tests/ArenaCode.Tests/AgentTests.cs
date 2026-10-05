using System.Text.Json.Nodes;

namespace ArenaCode.Tests;

public sealed class AgentTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    [Fact]
    public async Task A_turn_calls_a_local_and_an_Arena_tool_at_once_and_answers_from_their_results()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("notes.txt", "The build uses make.\n");
        h.Write("ARENA.md", "Always answer in one sentence.");
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("Done: " + string.Join(" | ", req["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => Fmt.OneLine(m!["content"]!.GetValue<string>(), 60))))
            : Reply.Call(("read_file", """{"path":"notes.txt"}"""), ("web_search", """{"query":"make docs"}"""));

        Assert.Equal(0, await h.Run("", "-p", "How", "is", "it", "built?"));

        Assert.Equal("Done: 1 The build uses make. | Results for make docs: https://arena.example/answer", h.Out.Trim());
        var requests = _gateway.Requests;
        Assert.Equal(2, requests.Count);
        var first = requests[0];
        Assert.Equal("How is it built?", FakeGateway.Last(first));
        Assert.True(first["parallel_tool_calls"]!.GetValue<bool>());
        var tools = FakeGateway.ToolNames(first);
        Assert.Contains("read_file", tools);
        Assert.Contains("web_search", tools);
        Assert.Contains("arena_read_file", tools); // Arena's read_file, renamed beside the local one
        var system = FakeGateway.System(first);
        Assert.Contains("Always answer in one sentence.", system);
        Assert.Contains("Arena: search the web with web_search", system);
        Assert.Contains($"Working directory: {h.Work}", system);
        // The second request carries both results, each answering its call.
        var results = requests[1]["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "tool").ToList();
        Assert.Equal(["call_0_read_file", "call_1_web_search"], results.Select(m => m!["tool_call_id"]!.GetValue<string>()));
        // Arena's MCP: one handshake, the session id sent back after it.
        var calls = _mcp.Calls;
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/call"], calls.Select(c => c.Method));
        Assert.Null(calls[0].Session);
        Assert.All(calls.Skip(1), c => Assert.Equal("session-42", c.Session));
        // Progress went to stderr, the answer alone to stdout.
        Assert.Contains("● read_file notes.txt", h.Err);
        Assert.Contains("● web_search", h.Err);
    }

    [Fact]
    public async Task An_Arena_tool_marked_as_changing_things_asks_first_and_is_left_out_of_plan_mode()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("Result: " + FakeGateway.Last(req)) : Reply.Call(("create_issue", """{"title":"x"}"""));
        Assert.Equal(0, await h.Run("open an issue\nn\n/exit\n"));
        Assert.Contains("Allow create_issue?", h.Out);
        Assert.Contains("Result: The person declined", h.Out);
        Assert.DoesNotContain(_mcp.Calls, c => c.Method == "tools/call");

        Assert.Equal(0, await h.Run("", "-p", "plan it", "--mode", "plan"));
        var tools = FakeGateway.ToolNames(_gateway.Requests.Last());
        Assert.Contains("web_search", tools);
        Assert.DoesNotContain("create_issue", tools);
    }

    [Fact]
    public async Task Arena_tools_answering_as_an_event_stream_work_the_same()
    {
        _mcp.Sse = true;
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("ok") : Reply.Call(("web_search", """{"query":"q"}"""));
        Assert.Equal(0, await h.Run("", "-p", "search"));
        var result = _gateway.Requests[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Equal("Results for q: https://arena.example/answer", result);
    }

    [Fact]
    public async Task Without_an_MCP_endpoint_the_session_carries_on_with_the_local_tools_and_says_so_once()
    {
        _mcp.Missing = true;
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Equal("Hello from the model.", h.Out.Trim());
        Assert.Single(h.Err.Split("Arena's tools are not available").Skip(1));
        var tools = FakeGateway.ToolNames(_gateway.Requests[0]);
        Assert.Contains("read_file", tools);
        Assert.DoesNotContain("web_search", tools);
        Assert.Contains("Arena's tools are not available in this session", FakeGateway.System(_gateway.Requests[0]));
    }

    [Fact]
    public async Task One_shot_json_carries_the_answer_the_session_and_the_tokens()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "hi", "--output", "json"));
        var json = JsonNode.Parse(h.Out)!.AsObject();
        Assert.Equal("Hello from the model.", json["result"]!.GetValue<string>());
        Assert.False(json["is_error"]!.GetValue<bool>());
        Assert.Equal("model-a", json["model"]!.GetValue<string>());
        Assert.True(json["usage"]!["prompt_tokens"]!.GetValue<long>() > 0);
        Assert.True(json["usage"]!["cached_tokens"]!.GetValue<long>() > 0);
        Assert.Equal(1, json["usage"]!["requests"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(h.Paths.SessionsDir, json["session"]!.GetValue<string>() + ".jsonl")));
    }

    [Fact]
    public async Task A_one_shot_edit_in_ask_mode_is_refused_and_auto_edit_allows_it()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = h.Write("a.txt", "old\n");
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("Result: " + FakeGateway.Last(req))
            : Reply.Call(("edit_file", """{"path":"a.txt","old_string":"old","new_string":"new"}"""));
        Assert.Equal(0, await h.Run("", "-p", "change it"));
        Assert.Contains("cannot ask", h.Out);
        Assert.Equal("old\n", File.ReadAllText(file));

        Assert.Equal(0, await h.Run("", "-p", "change it", "--mode", "auto-edit"));
        Assert.Equal("Result: Edited a.txt.", h.Out.Trim());
        Assert.Equal("new\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Interactive_turns_ask_before_an_edit_and_the_slash_commands_work()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = h.Write("a.txt", "old\n");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "edit it" => Reply.Call(("edit_file", """{"path":"a.txt","old_string":"old","new_string":"new"}""")),
            "say hi" => Reply.Say("hi there", reasoning: "Greeting."),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("edited"),
            _ => Reply.Say("?"),
        };
        var input = string.Join('\n', "edit it", "y", "say hi", "/model", "2", "/mode plan", "/thinking off", "/cost", "/tools", "/nope", "/exit") + "\n";
        Assert.Equal(0, await h.Run(input));

        Assert.Equal("new\n", File.ReadAllText(file));
        Assert.Contains("Allow edit_file?", h.Out);
        Assert.Contains("1 - old", h.Out);
        Assert.Contains("1 + new", h.Out);
        Assert.Contains("thinking: Greeting.", h.Out);
        Assert.Contains("hi there", h.Out);
        Assert.Contains("Model: model-b", h.Out);
        Assert.Contains("Mode: plan", h.Out);
        Assert.Contains("Thinking: off", h.Out);
        Assert.Contains("This session:", h.Out);
        Assert.Contains("web_search", h.Out);
        Assert.Contains("There is no command /nope", h.Err);
        Assert.Contains("Saved as ", h.Out);
        // Each turn ends with its tokens and the cached share.
        Assert.Matches(@"in \(\d+% cached\) · 7 out · 1 request", h.Out);
    }

    [Fact]
    public async Task Without_a_model_of_its_own_it_takes_the_one_a_new_chat_in_Arena_starts_with()
    {
        _mcp.DefaultModel = "model-b";
        try
        {
            using var h = new Harness(_gateway, _mcp);
            Assert.Equal(0, await h.Run("", "-p", "hi"));
            Assert.Equal("model-b", _gateway.Requests[0]["model"]!.GetValue<string>());
        }
        finally
        {
            _mcp.DefaultModel = null;
        }
    }

    [Fact]
    public async Task The_model_and_thinking_level_reach_the_gateway()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "a", "--model", "model-b", "--thinking", "off"));
        Assert.Equal("model-b", _gateway.Requests[0]["model"]!.GetValue<string>());
        Assert.False(_gateway.Requests[0]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(0, await h.Run("", "-p", "b", "--thinking", "high"));
        Assert.Equal("high", _gateway.Requests[1]["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(0, await h.Run("", "-p", "c"));
        Assert.Null(_gateway.Requests[2]["chat_template_kwargs"]);
        Assert.Equal(1, await h.Run("", "-p", "d", "--model", "no-such-model"));
        Assert.Contains("The gateway has no model no-such-model", h.Err);
    }

    [Fact]
    public async Task A_session_is_saved_privately_and_continued_with_its_history()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer {req["messages"]!.AsArray().Count}");
        Assert.Equal(0, await h.Run("", "-p", "first question"));
        var file = Directory.GetFiles(h.Paths.SessionsDir).Single();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        Assert.Equal(0, await h.Run("", "-p", "second question", "--continue"));
        var messages = _gateway.Requests[1]["messages"]!.AsArray().Skip(1).Select(m => $"{m!["role"]}: {m["content"]}").ToList();
        Assert.Equal(["user: first question", "assistant: answer 2", "user: second question"], messages);
        Assert.Single(Directory.GetFiles(h.Paths.SessionsDir));

        var id = Path.GetFileNameWithoutExtension(file);
        Assert.Equal(0, await h.Run("", "-p", "third", "--resume", id));
        Assert.Equal(6, _gateway.Requests[2]["messages"]!.AsArray().Count);
        Assert.Equal(1, await h.Run("", "-p", "x", "--resume", "20000101-000000"));
        Assert.Contains("No saved session", h.Err);
    }

    [Fact]
    public async Task Sub_agents_start_fresh_with_read_only_tools_and_report_back()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("src/a.cs", "class Parser { }\n");
        _gateway.Answer = req =>
        {
            var system = FakeGateway.System(req);
            if (system.StartsWith("You are a sub-agent", StringComparison.Ordinal))
            {
                return FakeGateway.HasToolResults(req) ? Reply.Say("Parser is in src/a.cs:1.") : Reply.Call(("grep", """{"pattern":"class Parser"}"""));
            }
            return FakeGateway.HasToolResults(req)
                ? Reply.Say("The sub-agent says: " + FakeGateway.Last(req))
                : Reply.Call(("task", """{"description":"find the parser","prompt":"Find where class Parser is defined."}"""));
        };
        Assert.Equal(0, await h.Run("", "-p", "where is the parser?"));
        Assert.Equal("The sub-agent says: Parser is in src/a.cs:1.", h.Out.Trim());
        var sub = _gateway.Requests.Where(r => FakeGateway.System(r).StartsWith("You are a sub-agent", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, sub.Count);
        // A fresh context: the task alone, not the main conversation.
        Assert.Equal("Find where class Parser is defined.", sub[0]["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal(2, sub[0]["messages"]!.AsArray().Count);
        var tools = FakeGateway.ToolNames(sub[0]);
        Assert.Contains("grep", tools);
        Assert.Contains("web_search", tools);
        Assert.DoesNotContain("edit_file", tools);
        Assert.DoesNotContain("run_shell", tools);
        Assert.DoesNotContain("task", tools);
        Assert.Contains("⎿ ", h.Err);
    }

    [Fact]
    public async Task Near_the_window_the_older_history_is_summarized_and_the_recent_turn_kept()
    {
        _gateway.Context = 3000;
        using var h = new Harness(_gateway, _mcp);
        bool Summarizing(JsonObject req) => FakeGateway.System(req).StartsWith("You summarize", StringComparison.Ordinal);
        _gateway.Answer = req => Summarizing(req) ? Reply.Say("SUMMARY: the person asked about turns 1 to 3.") : Reply.Say(new string('x', 2500));
        Assert.Equal(0, await h.Run("", "-p", "turn 1 " + new string('a', 2500)));
        Assert.Equal(0, await h.Run("", "-p", "turn 2 " + new string('b', 2500), "-c"));
        Assert.Equal(0, await h.Run("", "-p", "turn 3 " + new string('c', 2500), "-c"));
        Assert.Equal(0, await h.Run("", "-p", "turn 4", "-c"));

        var summaries = _gateway.Requests.Where(Summarizing).ToList();
        Assert.NotEmpty(summaries);
        Assert.Null(summaries[0]["tools"]);
        Assert.Contains("PERSON: turn 1 aaa", FakeGateway.Last(summaries[0]));
        Assert.DoesNotContain("turn 2", FakeGateway.Last(summaries[0]));
        var last = _gateway.Requests.Last();
        var messages = last["messages"]!.AsArray();
        Assert.StartsWith("[The conversation so far, summarized", messages[1]!["content"]!.GetValue<string>());
        Assert.Contains("SUMMARY: the person asked", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("turn 4", FakeGateway.Last(last));
        Assert.Contains("Compacted:", h.Err);
        // The compaction is in the session file: a resumed session starts from the summary.
        var data = SessionStore.Load(Directory.GetFiles(h.Paths.SessionsDir).Single());
        Assert.StartsWith("[The conversation so far", data.Messages[0]["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ctrl_C_stops_the_turn_and_leaves_a_history_the_next_turn_can_follow()
    {
        _gateway.Hang = true;
        using var h = new Harness(_gateway, _mcp);
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var ui = new Ui(env.In, output, output, false, false);
        await using var rt = await Runtime.StartAsync(new Options(), env, ui, CancellationToken.None);
        using var key = env.Cancel.BeginTurn();
        var turn = rt.Agent.RunAsync("think hard", new Spend(), key.Token);
        await _gateway.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.True(env.Cancel.Press());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["user", "assistant"], rt.Agent.Messages.Select(m => m["role"]!.GetValue<string>()));
        Assert.Equal("Let me think\n\n(stopped by the person)", rt.Agent.Messages[1]["content"]!.GetValue<string>());
        env.Cancel.EndTurn();
    }

    [Fact]
    public async Task A_session_where_nothing_was_said_is_not_kept()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/help\n/exit\n"));
        Assert.Contains("/compact", h.Out);
        Assert.DoesNotContain("Saved as", h.Out);
        Assert.Empty(Directory.GetFiles(h.Paths.SessionsDir));
    }

    [Fact]
    public async Task Without_signing_in_it_says_how()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Remove("apiKey"));
        Assert.Equal(1, await h.Run("", "-p", "hi"));
        Assert.Contains("Run: arena-code login", h.Err);
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
