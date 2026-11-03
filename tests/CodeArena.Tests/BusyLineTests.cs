using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// While the web chat's line for a model is full, Arena holds an API key's request back ("… is busy: … Try again in 30
/// seconds.", a 400 through LiteLLM's guardrail): Code Arena waits in the line and asks again, as a person in the web chat
/// waits, in a turn and in a compaction's summary. A summary held back is waited for, never left out.
/// </summary>
public sealed class BusyLineTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private const string Reason = "model-a is busy: 2 answers of the chat wait for it.";

    /// <summary>Arena's refusal (EngineRoute.LineFull), as LiteLLM passes on its guardrail's reason.</summary>
    private static JsonObject Busy() => new() { ["error"] = new JsonObject { ["message"] = Reason + " Try again in 30 seconds.", ["type"] = "None", ["code"] = "400" } };

    private static JsonObject Hello() => new() { ["model"] = "model-a", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) };

    private async Task<Runtime> StartAsync(Harness h)
    {
        var output = TextWriter.Synchronized(new StringWriter());
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var rt = await Runtime.StartAsync(new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
        rt.Gateway.BusyWait = TimeSpan.FromMilliseconds(10);
        return rt;
    }

    /// <summary>Questions and answers of 3,000 characters together, <paramref name="pairs"/> of them: past the threshold at 40.</summary>
    private static List<JsonObject> History(int pairs)
    {
        var history = new List<JsonObject>();
        for (var i = 0; i < pairs; i++)
        {
            history.Add(new JsonObject { ["role"] = "user", ["content"] = $"question {i} " + new string('q', 1500) });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"answer {i} " + new string('a', 1500) });
        }
        return history;
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.True(await Task.WhenAny(task, Task.Delay(Deadline)) == task, $"not done within {Deadline.TotalSeconds:0} s");
        return await task;
    }

    [Fact]
    public async Task A_request_held_back_while_the_models_line_is_full_waits_in_it_and_is_asked_again()
    {
        var refused = 0;
        _gateway.Failure = _ => Interlocked.Increment(ref refused) <= 3 ? 400 : 0;
        _gateway.FailureBody = _ => Busy();
        var said = new List<string>();
        var client = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key) { Retrying = said.Add, BusyWait = TimeSpan.FromMilliseconds(10) };

        var answer = await client.CompleteAsync(Hello(), null, CancellationToken.None);

        Assert.Equal("Hello from the model.", answer.Text);
        Assert.Equal(4, _gateway.Requests.Count);
        // Said once, in Arena's words: that it waits, and how to stop waiting.
        var notice = Assert.Single(said);
        Assert.StartsWith(Reason + " Waiting in its line: asking again every ", notice, StringComparison.Ordinal);
        Assert.EndsWith("(Ctrl+C or Stop ends the wait).", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Try again", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_that_stays_full_is_given_up_after_a_while_with_arenas_words()
    {
        _gateway.Failure = _ => 400;
        _gateway.FailureBody = _ => Busy();
        var client = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key) { BusyWait = TimeSpan.FromMilliseconds(10), BusyFor = TimeSpan.FromMilliseconds(100) };

        var e = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(Hello(), null, CancellationToken.None));

        Assert.Equal(Reason + " Try again in 30 seconds.", e.Message);
        Assert.Equal(TimeSpan.FromSeconds(30), e.Busy);
        // Asked again for as long as BusyFor: the first, then one after each wait.
        Assert.Equal(11, _gateway.Requests.Count);

        // Another refusal is not waited for: said at once, as before.
        _gateway.FailureBody = _ => new JsonObject { ["error"] = new JsonObject { ["message"] = "Invalid model name" } };
        var other = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(Hello(), null, CancellationToken.None));
        Assert.Null(other.Busy);
        Assert.Equal(12, _gateway.Requests.Count);
    }

    [Fact]
    public async Task A_summary_held_back_waits_in_the_line_and_is_made_not_left_out()
    {
        using var h = new Harness(_gateway, _mcp);
        var refused = 0;
        _gateway.Failure = req => FakeGateway.Summarizing(req) && Interlocked.Increment(ref refused) <= 2 ? 400 : 0;
        _gateway.FailureBody = _ => Busy();
        _gateway.Answer = req => FakeGateway.Summarizing(req) ? Reply.Say("SUMMARY of the earlier turns.") : Reply.Say("ok");
        await using var rt = await StartAsync(h);
        rt.Agent.Load(History(40));

        Assert.Equal("ok", await Within(rt.Agent.RunAsync("next", new Spend(), CancellationToken.None)));

        Assert.Equal(3, _gateway.Requests.Count(FakeGateway.Summarizing));
        var first = rt.Agent.Messages[0]["content"]!.GetValue<string>();
        Assert.StartsWith("[The conversation so far, summarized to fit", first, StringComparison.Ordinal);
        Assert.Contains("SUMMARY of the earlier turns.", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_summary_whose_line_stays_full_ends_the_turn_and_leaves_the_conversation_whole()
    {
        using var h = new Harness(_gateway, _mcp);
        var busy = true;
        _gateway.Failure = req => FakeGateway.Summarizing(req) && Volatile.Read(ref busy) ? 400 : 0;
        _gateway.FailureBody = _ => Busy();
        _gateway.Answer = req => FakeGateway.Summarizing(req) ? Reply.Say("SUMMARY of the earlier turns.") : Reply.Say("ok");
        await using var rt = await StartAsync(h);
        rt.Gateway.BusyFor = TimeSpan.FromMilliseconds(50);
        rt.Agent.Load(History(40));

        var e = await Assert.ThrowsAsync<GatewayException>(() => Within(rt.Agent.RunAsync("next", new Spend(), CancellationToken.None)));

        Assert.StartsWith(Reason, e.Message, StringComparison.Ordinal);
        // Nothing left out: the model still reads the older messages as they were.
        Assert.Equal("question 0 " + new string('q', 1500), rt.Agent.Messages[0]["content"]!.GetValue<string>());
        Assert.DoesNotContain(_gateway.Requests, r => !FakeGateway.Summarizing(r));

        // The line has room again: the next turn's compaction asks for the summary at once (not skipped, as after one that failed).
        Volatile.Write(ref busy, false);
        Assert.Equal("ok", await Within(rt.Agent.RunAsync("again", new Spend(), CancellationToken.None)));
        var first = rt.Agent.Messages[0]["content"]!.GetValue<string>();
        Assert.StartsWith("[The conversation so far, summarized to fit", first, StringComparison.Ordinal);
        Assert.Contains("SUMMARY of the earlier turns.", first, StringComparison.Ordinal);
    }
}
