using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// "The code-arena compact is triggered when it's too late and causes stuck and history is missing after compact": long
/// sessions with real tool calls (files read, searched) against an engine that counts its own tokens and holds a window,
/// as llama.cpp does: it refuses a prompt past it and cuts an answer that runs past it. A thinking model, whose thinking
/// takes room in the window as it writes but is not sent back. Every compaction comes in time (nothing refused, nothing
/// cut, no request past the threshold), its summary is made, and every message stays in the session.
/// </summary>
public sealed partial class LongSessionCompactionTests(ITestOutputHelper log) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    /// <summary>
    /// The engine's count, as a real tokenizer's: prose (the system prompt, the tools' definitions, the person's words) about 4
    /// characters a token, code with its numbers (the tools' results) about 2.6. Code Arena learns one figure for all.
    /// </summary>
    private static long Count(JsonObject request)
    {
        double tokens = (request["tools"]?.ToJsonString().Length ?? 0) / 4.0;
        foreach (var m in request["messages"]!.AsArray().OfType<JsonObject>())
        {
            var text = m["content"] is JsonValue v ? v.GetValue<string>().Length : m["content"]?.ToJsonString().Length ?? 0;
            tokens += text / (m["role"]!.GetValue<string>() == "tool" ? 2.6 : 4.0) + (m["tool_calls"]?.ToJsonString().Length ?? 0) / 3.0 + 4;
        }
        return (long)tokens;
    }

    /// <summary>A file of code, about <paramref name="lines"/> lines.</summary>
    private static string Code(int file, int lines) => string.Join('\n', Enumerable.Range(1, lines).Select(i =>
        i % 10 == 0 ? $"    public int Total{file}_{i}(int a, int b) => Compute(a, b) + {i}; // TODO check the bounds" : $"    var value{i} = Compute(\"{file}\", {i}) * factor{i % 7};"));

    /// <summary>The results of the turn's calls the model reads: after the question (Code Arena's notes are no question).</summary>
    private static int Results(JsonObject req) => req["messages"]!.AsArray().OfType<JsonObject>().Reverse()
        .TakeWhile(m => m["role"]!.GetValue<string>() != "user" || m["content"]?.ToString() is ['[' or '(', ..])
        .Count(m => m["role"]!.GetValue<string>() == "tool");

    /// <summary>What a step's thinking takes of a 32K window: an eighth, as a 262K window's model thinks for 30K tokens.</summary>
    private const int Thinking = 4_000;

    private static string Before(string file, int back) => $"src/big{(int.Parse(file["src/big".Length..^3]) - back + 40) % 40}.cs";

    [GeneratedRegex(@"Fix (src/big\d+\.cs)")]
    private static partial Regex Asked();

    /// <summary>
    /// The model: each turn reads the file asked about and the two before it at once, searches them all, then answers; each
    /// step after some thinking. A summary when asked for one (<paramref name="summaryThought"/>: after thinking that long,
    /// although asked not to think).
    /// </summary>
    private Func<JsonObject, Reply> Model(int summaryThought = 0) => req =>
    {
        if (FakeGateway.Summarizing(req))
        {
            return Reply.Say("## Goal\n- Fix the files one by one.\n## Files\n- src/big*.cs read and searched.\n## Next steps\n- The next file.", summaryThought > 0 ? "Let me think about what to keep. " : null)
                with { Completion = summaryThought + 200, Thought = summaryThought };
        }
        // The file the last question names (after a compaction of the turn, in its summary's first lines).
        if (req["messages"]!.AsArray().OfType<JsonObject>().LastOrDefault(m => m["role"]!.GetValue<string>() == "user" && m["content"] is JsonValue v && Asked().IsMatch(v.GetValue<string>())) is not { } question)
        {
            return Reply.Say("I do not know what you asked any more: the conversation is gone.");
        }
        var file = Asked().Matches(question["content"]!.GetValue<string>())[^1].Groups[1].Value;
        return Results(req) switch
        {
            0 => new Reply("", [("read_file", $$"""{"path":"{{file}}"}"""), ("read_file", $$"""{"path":"{{Before(file, 1)}}"}"""), ("read_file", $$"""{"path":"{{Before(file, 2)}}"}""")],
                "I should read the file and its neighbours first. ") { Completion = Thinking, Thought = Thinking - 100 },
            3 => new Reply("", [("grep", $$"""{"pattern":"TODO","path":"src"}""")], "Now the TODOs everywhere. ") { Completion = Thinking, Thought = Thinking - 50 },
            _ => Reply.Say($"Fixed {file}: the bounds are checked now. " + new string('.', 400), "Writing the answer.") with { Completion = Thinking, Thought = Thinking - 300 },
        };
    };

    /// <summary>What a run of <paramref name="turns"/> turns said, with the requests the engine was sent.</summary>
    private sealed record Run(string Said, List<JsonObject> Requests, List<JsonObject> Stream, int StreamRead, Runtime Runtime);

    private async Task<Run> RunAsync(int turns, int lines, Options? options = null)
    {
        _gateway.Tokens = Count;
        using var h = new Harness(_gateway, _mcp);
        for (var f = 0; f < 40; f++)
        {
            h.Write($"src/big{f}.cs", Code(f, lines));
        }
        var written = new StringWriter();
        var output = TextWriter.Synchronized(written);
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var rt = await Runtime.StartAsync(options ?? new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
        try
        {
            for (var t = 0; t < turns; t++)
            {
                var turn = rt.Agent.RunAsync($"Fix src/big{t % 40}.cs please.", new Spend(), CancellationToken.None);
                Assert.True(await Task.WhenAny(turn, Task.Delay(Deadline)) == turn, $"turn {t} did not end");
                if (!(await turn).StartsWith($"Fixed src/big{t % 40}.cs", StringComparison.Ordinal))
                {
                    lock (output)
                    {
                        log.WriteLine(written.ToString());
                    }
                    log.WriteLine(_gateway.Requests[^1]["messages"]!.ToJsonString()[..Math.Min(4000, _gateway.Requests[^1]["messages"]!.ToJsonString().Length)]);
                }
                Assert.StartsWith($"Fixed src/big{t % 40}.cs", await turn);
            }
            string said;
            lock (output)
            {
                said = written.ToString();
            }
            var requests = _gateway.Requests;
            var window = rt.Model.Context;
            log.WriteLine($"{requests.Count} requests, {requests.Count(FakeGateway.Summarizing)} summaries, the largest {requests.Max(Count)} tokens of {window}; each, in percent of the window:");
            log.WriteLine(string.Join(" ", requests.Select(r => (FakeGateway.Summarizing(r) ? "S" : "") + (100 * Count(r) / window))));
            foreach (var line in said.Split('\n').Where(l => Regex.IsMatch(l, "Compact|Cleared|too big|cut|could not|engine holds")).Distinct())
            {
                log.WriteLine(line.Trim());
            }
            var stream = rt.Session.Read(d => d.Stream.Select(e => e.Message).ToList());
            return new Run(said, requests, stream, SessionStore.Load(rt.Session.File, whole: true).Stream.Count, rt);
        }
        finally
        {
            await rt.DisposeAsync();
        }
    }

    /// <summary>Every turn's question and its four results in the session, read back the same: nothing of the conversation is gone.</summary>
    private static void KeepsEverything(Run run, int turns)
    {
        Assert.Equal(turns, run.Stream.Count(m => m.Str("role") == "user" && m.Str("content")?.StartsWith("Fix src/", StringComparison.Ordinal) == true));
        Assert.Equal(turns * 4, run.Stream.Count(m => m.Str("role") == "tool"));
        Assert.Equal(run.Stream.Count, run.StreamRead);
    }

    /// <summary>No request refused, no answer cut, each compaction with the model's summary.</summary>
    private static void NeverTooLate(Run run)
    {
        Assert.DoesNotContain("too big for the model's window", run.Said);
        Assert.DoesNotContain("reached the end of the model's window", run.Said);
        Assert.DoesNotContain("output limit", run.Said);
        Assert.DoesNotContain("summarized mechanically", run.Said);
        Assert.DoesNotContain("could not be made", run.Said);
        Assert.DoesNotContain("were cut to fit", run.Said);
    }

    [Theory]
    [InlineData(65_536, null)]
    [InlineData(65_536, 32_768)]
    [InlineData(32_768, 16_384)]
    public async Task A_long_session_with_real_tool_calls_compacts_before_the_threshold_three_times_and_keeps_every_message(int window, int? maxOutput)
    {
        (_gateway.Context, _gateway.Window, _gateway.MaxOutput) = (window, window, maxOutput);
        _gateway.Answer = Model();
        const int turns = 50;
        var run = await RunAsync(turns, lines: 90);

        NeverTooLate(run);
        // In time: compacted before the next step's answer and results take the request past the threshold (80%).
        Assert.All(run.Requests.Where(r => !FakeGateway.Summarizing(r)), r => Assert.True(Count(r) <= window * 0.8, $"a request of {Count(r)} tokens, past 80% of {window}"));
        Assert.True(run.Requests.Count(FakeGateway.Summarizing) >= 3, $"{run.Requests.Count(FakeGateway.Summarizing)} summaries");
        KeepsEverything(run, turns);
    }

    [Fact]
    public async Task The_thinking_of_an_answer_which_is_not_sent_back_does_not_make_a_turn_compact_what_it_just_read()
    {
        // One step reads three long files: the request after it holds about 56% of the window, and the thinking before
        // it (an eighth of the window) is not in it.
        (_gateway.Context, _gateway.Window) = (32_768, 32_768);
        _gateway.Answer = Model();
        var run = await RunAsync(turns: 1, lines: 260);

        NeverTooLate(run);
        Assert.DoesNotContain(run.Requests, FakeGateway.Summarizing);
        Assert.DoesNotContain("Compacted", run.Said);
        // Each file read reached the model whole.
        Assert.All(run.Requests.Last()["messages"]!.AsArray().OfType<JsonObject>().Where(m => m.Str("role") == "tool" && m.Str("tool_call_id")!.Contains("read_file")),
            m => Assert.DoesNotContain("cut to save room", m.Str("content")));
        KeepsEverything(run, 1);
    }

    [Fact]
    public async Task An_engine_that_holds_less_than_the_gateway_says_is_learned_from_its_first_refusal_and_compacts_in_time_after()
    {
        // The gateway says 32,768 tokens and a longest answer of 16,384; the engine holds 20,000 (its slots share its context).
        (_gateway.Context, _gateway.Window, _gateway.MaxOutput) = (32_768, 20_000, 16_384);
        _gateway.Answer = Model();
        const int turns = 16;
        var run = await RunAsync(turns, lines: 60);

        Assert.Equal(20_000, run.Runtime.Model.Context);
        Assert.Contains("The engine holds 20k tokens", run.Said);
        // Learned from the first answer it cut (or refused once): never refused after, each compaction with its summary.
        Assert.InRange(Regex.Matches(run.Said, "too big for the model's window").Count, 0, 1);
        Assert.DoesNotContain("summarized mechanically", run.Said);
        Assert.DoesNotContain("could not be made", run.Said);
        Assert.True(run.Requests.Count(FakeGateway.Summarizing) >= 3, $"{run.Requests.Count(FakeGateway.Summarizing)} summaries");
        Assert.All(run.Requests.SkipWhile(r => Count(r) <= 20_000).Skip(1).Where(r => !FakeGateway.Summarizing(r)), r => Assert.True(Count(r) <= 16_000, $"a request of {Count(r)} tokens"));
        KeepsEverything(run, turns);
    }

    [Fact]
    public async Task A_model_that_thinks_before_its_summary_although_asked_not_to_still_gives_the_summary()
    {
        (_gateway.Context, _gateway.Window) = (32_768, 32_768);
        _gateway.Answer = Model(summaryThought: 3_000);
        const int turns = 16;
        var run = await RunAsync(turns, lines: 90);

        NeverTooLate(run);
        Assert.True(run.Requests.Count(FakeGateway.Summarizing) >= 2, $"{run.Requests.Count(FakeGateway.Summarizing)} summaries");
        Assert.Contains("## Goal", run.Runtime.Agent.Messages[0].Str("content"));
        KeepsEverything(run, turns);
    }

    [Fact]
    public async Task A_picture_counts_in_the_size_as_the_engine_reads_one()
    {
        using var h = new Harness(_gateway, _mcp);
        var output = TextWriter.Synchronized(new StringWriter());
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        await using var rt = await Runtime.StartAsync(new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
        var words = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "(The picture the tool took.)" }) };
        rt.Agent.Load([words]);
        var without = rt.Agent.Estimate();
        var picture = (JsonObject)words.DeepClone();
        picture["content"]!.AsArray().Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,iVBORw0KGgo=" } });
        rt.Agent.Load([picture]);
        // About a thousand tokens, as an engine reads a screenshot: not the ten its message's text would be.
        Assert.InRange(rt.Agent.Estimate() - without, 900, 1_100);
    }
}
