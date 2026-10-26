using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// Long sessions never get stuck on the model's window: a summary that fails leaves the older messages out instead, a
/// request the window refuses compacts and is asked again once, the size is learned from the gateway's counts, the
/// requests' start stays what the engine has cached, and one long turn compacts its own earlier steps. Each test has a
/// deadline (Task.WhenAny), so a stall makes it fail, never hang.
/// </summary>
public sealed class HangCompactionTests(ITestOutputHelper log) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private async Task<Runtime> StartAsync(Harness h, Options? o = null)
    {
        var output = TextWriter.Synchronized(new StringWriter());
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        return await Runtime.StartAsync(o ?? new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
    }

    /// <summary>Questions and answers of 3,000 characters together, <paramref name="pairs"/> of them.</summary>
    private static List<JsonObject> History(int pairs, string tag = "")
    {
        var history = new List<JsonObject>();
        for (var i = 0; i < pairs; i++)
        {
            history.Add(new JsonObject { ["role"] = "user", ["content"] = $"question {tag}{i} " + new string('q', 1500) });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"answer {tag}{i} " + new string('a', 1500) });
        }
        return history;
    }

    /// <summary>llama.cpp's refusal of a prompt bigger than its window, as LiteLLM passes it on.</summary>
    private static JsonObject OverWindow(long tokens, long window) => new()
    {
        ["error"] = new JsonObject
        {
            ["message"] = "litellm.ContextWindowExceededError: litellm.BadRequestError: OpenAIException - the request exceeds the available context size, try increasing it",
            ["type"] = "exceed_context_size_error",
            ["n_prompt_tokens"] = tokens,
            ["n_ctx"] = window,
        },
    };

    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.True(await Task.WhenAny(task, Task.Delay(Deadline)) == task, $"not done within {Deadline.TotalSeconds:0} s");
        return await task;
    }

    [Fact]
    public async Task A_summary_that_fails_leaves_the_older_messages_out_and_the_turn_answers_and_is_not_tried_on_every_compaction()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Failure = req => FakeGateway.Summarizing(req) ? 400 : 0;
        _gateway.Answer = _ => Reply.Say("ok");
        await using var rt = await StartAsync(h);
        rt.Agent.Load(History(40));
        Assert.True(rt.Agent.Estimate() > rt.Model.Context * rt.Compaction.At / 100.0);

        Assert.Equal("ok", await Within(rt.Agent.RunAsync("next", new Spend(), CancellationToken.None)));

        Assert.Single(_gateway.Requests, FakeGateway.Summarizing);
        var first = rt.Agent.Messages[0]["content"]!.GetValue<string>();
        log.WriteLine(first);
        Assert.StartsWith("[The conversation so far, summarized mechanically", first);
        Assert.Contains("The person's messages:", first);
        Assert.Contains("no summary was made: The gateway answered 400", first);
        Assert.Equal("next", rt.Agent.Messages[^2]["content"]!.GetValue<string>());
        Assert.True(rt.Agent.Estimate() < rt.Model.Context * rt.Compaction.At / 100.0);

        // Full again: compacted at once without asking for a summary (it failed a moment ago), and answered.
        rt.Agent.Load(History(40, "b"));
        Assert.Equal("ok", await Within(rt.Agent.RunAsync("again", new Spend(), CancellationToken.None)));
        Assert.Single(_gateway.Requests, FakeGateway.Summarizing);
        Assert.Contains("a summary failed a short while ago", rt.Agent.Messages[0]["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_the_window_refuses_is_compacted_and_asked_again_once_and_the_size_is_learned_from_the_refusal()
    {
        using var h = new Harness(_gateway, _mcp);
        var refused = 0;
        // The engine counts many more tokens than 3.5 characters a token makes: the estimate stays under the threshold.
        _gateway.Failure = req => !FakeGateway.Summarizing(req) && Interlocked.Increment(ref refused) == 1 ? 400 : 0;
        _gateway.FailureBody = _ => OverWindow(30_000, 20_000);
        _gateway.Answer = req => FakeGateway.Summarizing(req) ? Reply.Say("SUMMARY of the earlier turns.") : Reply.Say("ok");
        await using var rt = await StartAsync(h);
        // Under the threshold by the estimate (the tools' definitions count in it too).
        rt.Agent.Load(History(9));
        Assert.True(rt.Agent.Estimate() < rt.Model.Context * rt.Compaction.At / 100.0, $"{rt.Agent.Estimate()} of {rt.Model.Context}");

        Assert.Equal("ok", await Within(rt.Agent.RunAsync("next", new Spend(), CancellationToken.None)));

        Assert.Single(_gateway.Requests, FakeGateway.Summarizing);
        Assert.Equal(3, _gateway.Requests.Count);
        Assert.StartsWith("[The conversation so far, summarized to fit", rt.Agent.Messages[0]["content"]!.GetValue<string>());
        Assert.Contains("SUMMARY of the earlier turns.", rt.Agent.Messages[0]["content"]!.GetValue<string>());
        // The question was asked again once, after the summary; it is in the history once.
        Assert.Equal("next", FakeGateway.Last(_gateway.Requests[^1]));
        Assert.Single(rt.Agent.Messages, m => m["content"]?.GetValue<string>() == "next");
    }

    [Fact]
    public async Task A_full_shared_cache_asks_again_in_a_moment_without_compacting()
    {
        using var h = new Harness(_gateway, _mcp);
        var refused = 0;
        // llama.cpp's error when the cache its requests share is full: other requests filled it, not this one.
        _gateway.Failure = _ => Interlocked.Increment(ref refused) == 1 ? 500 : 0;
        _gateway.FailureBody = _ => new JsonObject { ["error"] = new JsonObject { ["code"] = 500, ["message"] = "Context size has been exceeded.", ["type"] = "server_error" } };
        _gateway.Answer = _ => Reply.Say("ok");
        Agent.SharedCacheWait = TimeSpan.FromMilliseconds(50);
        await using var rt = await StartAsync(h);
        rt.Agent.Load(History(4));

        Assert.Equal("ok", await Within(rt.Agent.RunAsync("next", new Spend(), CancellationToken.None)));

        Assert.Equal(2, _gateway.Requests.Count);
        Assert.DoesNotContain(_gateway.Requests, FakeGateway.Summarizing);
        Assert.Equal("question 0 " + new string('q', 1500), rt.Agent.Messages[0]["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_that_still_does_not_fit_fails_once_and_the_question_does_not_pile_up()
    {
        using var h = new Harness(_gateway, _mcp);
        // Every request refused, the summary's too: the size is learned from the refusals alone.
        _gateway.Failure = _ => 400;
        _gateway.FailureBody = _ => OverWindow(30_000, 20_000);
        await using var rt = await StartAsync(h);
        rt.Agent.Load(History(12));

        var asked = new List<int>();
        for (var turn = 0; turn < 2; turn++)
        {
            var e = await Assert.ThrowsAsync<GatewayException>(() => Within(rt.Agent.RunAsync("next", new Spend(), CancellationToken.None)));
            Assert.True(e.ContextExceeded);
            Assert.Contains("does not fit the model's window, even compacted", e.Message);
            // Not left in the history: a next try does not add to it.
            Assert.DoesNotContain(rt.Agent.Messages, m => m["content"]?.GetValue<string>() == "next");
            asked.Add(_gateway.Requests.Count(r => !FakeGateway.Summarizing(r)) - asked.Sum());
        }
        // The first turn: the question, then once more after compacting. The second, the same at most; never more than twice.
        Assert.Equal(2, asked[0]);
        Assert.InRange(asked[1], 1, 2);
        log.WriteLine($"characters a token, learned from the refusals: {rt.Agent.CharsPerToken:0.00}");
        Assert.True(rt.Agent.CharsPerToken < 3.5);
    }

    [Fact]
    public async Task The_size_is_learned_from_the_gateways_count_and_a_resumed_session_starts_from_its_last_count()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "hello " + new string('h', 4000)));
        var usage = File.ReadAllLines(Directory.GetFiles(h.Paths.SessionsDir).Single()).Select(l => JsonNode.Parse(l)!.AsObject())
            .Last(e => e["type"]!.GetValue<string>() == "usage");
        var counted = usage["prompt"]!.GetValue<long>() + usage["completion"]!.GetValue<long>();

        await using var rt = await StartAsync(h, new Options { Continue = true });
        Assert.Equal(2, rt.Agent.Messages.Count);
        Assert.Equal(counted, rt.Agent.Estimate());
        log.WriteLine($"characters a token, learned on resuming: {rt.Agent.CharsPerToken:0.00}");
        Assert.NotEqual(3.5, rt.Agent.CharsPerToken);

        // What is added after it is counted at the ratio learned.
        rt.Agent.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = new string('x', 1000) });
        Assert.Equal(counted + (long)(1010 / rt.Agent.CharsPerToken), rt.Agent.Estimate());
    }

    [Fact]
    public async Task The_start_of_the_requests_stays_the_same_when_the_mode_changes_and_the_change_is_noted_where_it_happened()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = _ => Reply.Say("ok");
        await using var rt = await StartAsync(h);
        await Within(rt.Agent.RunAsync("one", new Spend(), CancellationToken.None));
        rt.Permissions.Mode = Mode.Plan;
        await Within(rt.Agent.RunAsync("two", new Spend(), CancellationToken.None));
        rt.Permissions.Mode = Mode.Ask;
        await Within(rt.Agent.RunAsync("three", new Spend(), CancellationToken.None));

        var requests = _gateway.Requests;
        Assert.Equal(3, requests.Count);
        for (var i = 1; i < requests.Count; i++)
        {
            // The same system prompt and tools, and each request starts with the whole of the one before.
            Assert.Equal(FakeGateway.SystemOnly(requests[0]), FakeGateway.SystemOnly(requests[i]));
            Assert.Equal(FakeGateway.ToolNames(requests[0]), FakeGateway.ToolNames(requests[i]));
            var before = requests[i - 1]["messages"]!.AsArray();
            var now = requests[i]["messages"]!.AsArray();
            Assert.All(before.Select((m, k) => (m, k)), x => Assert.Equal(x.m!.ToJsonString(), now[x.k]!.ToJsonString()));
        }
        var notes = FakeGateway.Notes(requests[2]);
        Assert.Equal(3, notes.Count);
        Assert.DoesNotContain("Mode: plan", notes[0]);
        Assert.Contains("Mode: plan", notes[1]);
        Assert.Equal(notes[0], notes[2]);
        Assert.DoesNotContain("Mode:", FakeGateway.SystemOnly(requests[0]));
        // Each note comes before the question it went with.
        var messages = requests[2]["messages"]!.AsArray().Select(m => m!["content"]!.GetValue<string>()).ToList();
        Assert.Equal(messages.IndexOf(notes[1]) + 1, messages.IndexOf("two"));
        // The page and the session file never see a note.
        Assert.DoesNotContain(rt.Agent.Messages, m => m["content"]!.GetValue<string>().StartsWith(SystemPrompt.NoteHead, StringComparison.Ordinal));
        Assert.DoesNotContain(SystemPrompt.NoteHead, File.ReadAllText(rt.Session.File));
    }

    [Fact]
    public void Servers_tools_keep_their_places_and_new_ones_come_at_the_end()
    {
        ToolDef Tool(string name) => new() { Name = name, Description = "", Parameters = [], Run = (_, _, _) => Task.FromResult(new ToolResult("")) };
        var before = new[] { Tool("a"), Tool("b"), Tool("c") };
        var now = new[] { Tool("d"), Tool("c"), Tool("a"), Tool("e") };
        Assert.Equal(["a", "c", "d", "e"], ToolBox.InPlace(before, now).Select(t => t.Name));
    }

    [Fact]
    public async Task One_long_turn_that_writes_big_files_compacts_its_own_earlier_steps_and_finishes()
    {
        using var h = new Harness(_gateway, _mcp);
        const int Files = 14;
        var steps = 0;
        var refusals = 0;
        // The engine's window, as llama.cpp keeps it: a prompt over it is refused.
        _gateway.Failure = req =>
        {
            if (req.ToJsonString().Length / 4 <= _gateway.Context)
            {
                return 0;
            }
            Interlocked.Increment(ref refusals);
            return 400;
        };
        _gateway.FailureBody = req => OverWindow(req.ToJsonString().Length / 4, _gateway.Context);
        _gateway.Answer = req =>
        {
            if (FakeGateway.Summarizing(req))
            {
                return Reply.Say("SUMMARY: wrote the files so far.");
            }
            var n = Interlocked.Increment(ref steps);
            return n <= Files ? Reply.Call(("write_file", new JsonObject { ["path"] = $"f{n}.txt", ["content"] = new string((char)('a' + n % 26), 8000) }.ToJsonString())) : Reply.Say("done");
        };
        await using var rt = await StartAsync(h, new Options { Mode = "yolo" });

        Assert.Equal("done", await Within(rt.Agent.RunAsync("write the files", new Spend(), CancellationToken.None)));

        log.WriteLine($"{_gateway.Requests.Count} requests, {_gateway.Requests.Count(FakeGateway.Summarizing)} summaries, {refusals} refused; {rt.Agent.Messages.Count} messages");
        Assert.All(Enumerable.Range(1, Files), i => Assert.True(File.Exists(Path.Combine(h.Work, $"f{i}.txt"))));
        var head = rt.Agent.Messages.Single(m => m["content"]?.GetValue<string>().StartsWith("[The conversation so far, summarized to fit the model's window: the person's message", StringComparison.Ordinal) == true);
        Assert.Contains("write the files", head["content"]!.GetValue<string>());
        Assert.Contains("SUMMARY: wrote the files so far.", head["content"]!.GetValue<string>());
        // Every compaction changed something: as many entries in the session file as summaries asked.
        var compactions = File.ReadAllLines(rt.Session.File).Count(l => l.StartsWith("{\"type\":\"compact\"", StringComparison.Ordinal));
        Assert.True(compactions <= _gateway.Requests.Count(FakeGateway.Summarizing) + refusals, $"{compactions} compact entries");
    }

    [Fact]
    public async Task A_compaction_that_would_change_nothing_writes_nothing()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var rt = await StartAsync(h);
        rt.Agent.Load([
            new JsonObject { ["role"] = "user", ["content"] = "q" },
            new JsonObject { ["role"] = "assistant", ["content"] = "a" },
            new JsonObject { ["role"] = "user", ["content"] = "q2" },
        ]);
        var events = new Events();
        rt.Agent.Events = events;
        Assert.False(await rt.Agent.CompactAsync(force: false, CancellationToken.None));
        Assert.Empty(events.Compactions);
        Assert.DoesNotContain("\"compact\"", File.ReadAllText(rt.Session.File));
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public void Long_arguments_of_the_models_calls_are_cut_and_stay_JSON()
    {
        var args = new JsonObject { ["path"] = "big.txt", ["content"] = new string('z', 5000), ["edits"] = new JsonArray(new string('y', 3000), "short") }.ToJsonString();
        var shorter = Agent.ShortArguments(args)!;
        var parsed = JsonNode.Parse(shorter)!.AsObject();
        Assert.Equal("big.txt", parsed["path"]!.GetValue<string>());
        Assert.StartsWith(new string('z', 600) + "\n… (4,400 characters cut", parsed["content"]!.GetValue<string>());
        Assert.Equal("short", parsed["edits"]![1]!.GetValue<string>());
        Assert.True(shorter.Length < 2000);
        Assert.Null(Agent.ShortArguments("not json"));
    }

    [Theory]
    [InlineData("the request exceeds the available context size, try increasing it", true)]
    [InlineData("litellm.ContextWindowExceededError: prompt is long", true)]
    [InlineData("This model's maximum context length is 32768 tokens. However, you requested 40000 tokens", true)]
    [InlineData("Limit type: tokens. Current limit: 1000", false)]
    [InlineData("The model's backend is restarting.", false)]
    public void Refusals_for_the_window_are_told_from_others(string text, bool over)
    {
        Assert.Equal(over, GatewayClient.OverWindow(text));
    }

    [Fact]
    public void The_prompts_size_is_read_from_the_refusal()
    {
        Assert.Equal(286_311, GatewayClient.PromptTokensIn("""{"message":"…","n_prompt_tokens":286311,"n_ctx":262144}"""));
        Assert.Equal(34_803, GatewayClient.PromptTokensIn("exceeds the available context size (n_prompt_tokens=34803, n_ctx=32768)"));
        Assert.Null(GatewayClient.PromptTokensIn("no count"));
    }

    [Fact]
    public async Task A_gateway_timeout_is_asked_once_more_like_a_stalled_answer()
    {
        var tries = 0;
        _gateway.Failure = _ => Interlocked.Increment(ref tries) == 1 ? 408 : 0;
        var said = new List<string>();
        var client = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key) { Retrying = said.Add, RetryWaits = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)] };
        var answer = await Within(client.CompleteAsync(Ask(), null, CancellationToken.None));
        Assert.Equal("Hello from the model.", answer.Text);
        Assert.Equal(2, _gateway.Requests.Count);

        // Like a stalled answer, once only: each try may have waited long.
        _gateway.Failure = _ => 408;
        var e = await Assert.ThrowsAsync<GatewayException>(() => Within(client.CompleteAsync(Ask(), null, CancellationToken.None)));
        Assert.Equal(408, e.Status);
        Assert.Equal(4, _gateway.Requests.Count);
    }

    [Fact]
    public async Task A_prompt_expected_to_take_long_to_read_says_how_long_first()
    {
        var said = new List<string>();
        var client = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key) { Retrying = said.Add, PrefillRate = 100 };
        await Within(client.CompleteAsync(Ask(), null, CancellationToken.None, uncached: 20_000));
        Assert.Contains(said, s => s.Contains("about 20k tokens it has not cached") && s.Contains("may take about 3m 20s to start"));
        said.Clear();
        await Within(client.CompleteAsync(Ask(), null, CancellationToken.None, uncached: 500));
        Assert.Empty(said);
    }

    private static JsonObject Ask() => new()
    {
        ["model"] = "model-a",
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
    };

    private sealed class Events : IAgentEvents
    {
        public List<string> Compactions { get; } = [];

        public void Step()
        {
        }

        public void Reasoning(string text)
        {
        }

        public void Text(string text)
        {
        }

        public void StepDone(TokenUsage? usage)
        {
        }

        public void ToolCall(string id, string name, ToolDef? tool, string arguments)
        {
        }

        public void ToolResult(string id, string name, int index, ToolResult result, TimeSpan took, bool declined)
        {
        }

        public void Compacted(string notice) => Compactions.Add(notice);

        public void Notice(string text)
        {
        }
    }
}
