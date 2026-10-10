using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// Hypothesis "stream-waits": the model request waits a long time with nothing shown (a slow prefill, the
/// app's per-model line, a guardrail, a compaction's summary), the watchdog allows 15 minutes before the first
/// event and the retries multiply it, and the second prompt is not performed meanwhile. Each test has a
/// deadline (Task.WhenAny), so a stall makes it fail, never hang.
/// </summary>
public sealed class HangStreamWaitsTests(ITestOutputHelper log) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static bool Summarizing(JsonObject req) => FakeGateway.System(req).StartsWith("You summarize", StringComparison.Ordinal);

    private static JsonObject Ask(string text) => new()
    {
        ["model"] = "model-a",
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }),
    };

    private async Task<Runtime> StartAsync(Harness h)
    {
        var output = TextWriter.Synchronized(new StringWriter());
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        return await Runtime.StartAsync(new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
    }

    /// <summary>The longest a request can wait with nothing shown, with the defaults: every try waits the whole first-event wait.</summary>
    private static TimeSpan WorstCase(GatewayClient g) =>
        g.FirstEventWait * (g.RetryWaits.Length + 1) + g.RetryWaits.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    private static async Task<bool> Until(Func<bool> done, TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > within)
            {
                return false;
            }
            await Task.Delay(100);
        }
        return true;
    }

    // ------------------------------------------------------------------ the stall as the person meets it

    [Fact]
    public async Task Terminal_Ctrl_C_ends_the_wait_for_an_answer_that_never_starts_and_the_second_prompt_runs()
    {
        using var h = new Harness(_gateway, _mcp);
        // The engine takes the first request and says nothing (a long prefill, a queue, a guardrail that hangs).
        _gateway.Silent = req => !Summarizing(req) && FakeGateway.Last(req) == "first";
        _gateway.Answer = req => Reply.Say("answer to " + FakeGateway.Last(req));
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new CliEnv
        {
            In = new StringReader("first\nsecond\n/exit\n"),
            Out = TextWriter.Synchronized(output),
            Err = TextWriter.Synchronized(error),
            Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work,
            Paths = h.Paths,
            InTerminal = true,
        };
        string Shown()
        {
            lock (env.Out)
            {
                lock (env.Err)
                {
                    return output + " | " + error;
                }
            }
        }
        bool SecondSent() => _gateway.Requests.Any(r => !Summarizing(r) && FakeGateway.Last(r) == "second");

        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        Assert.True(await Task.WhenAny(_gateway.FirstRequest.Task, Task.Delay(TimeSpan.FromSeconds(30))) == _gateway.FirstRequest.Task, "the first request never reached the gateway: " + Shown());
        var clock = Stopwatch.StartNew();
        var performed = await Until(() => SecondSent() || run.IsCompleted, TimeSpan.FromSeconds(3));
        var waited = clock.Elapsed;
        var requestsThen = _gateway.Requests.Count;
        var shownThen = Shown();

        // Ctrl+C: does it stop the wait, and does the second prompt then run?
        var ctrlC = Stopwatch.StartNew();
        env.Cancel.Press();
        var sentAfterCtrlC = await Until(SecondSent, TimeSpan.FromSeconds(15));
        var stoppedIn = ctrlC.Elapsed;
        var ended = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))) == run;

        var defaults = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key);
        var report = $"After {waited.TotalSeconds:0} s the second prompt was {(performed ? "" : "NOT ")}performed: {requestsThen} request(s), " +
                     $"nothing said but: [{Fmt.OneLine(shownThen, 600)}]. With the defaults the first notice comes after {defaults.FirstEventWait.TotalMinutes:0} min " +
                     $"and the request is given up after {WorstCase(defaults).TotalMinutes:0.#} min. Ctrl+C then: the second prompt " +
                     $"{(sentAfterCtrlC ? $"was sent {stoppedIn.TotalMilliseconds:0} ms later" : "was NOT sent within 15 s")}; the session {(ended ? "ended at /exit" : "did NOT end")}.";
        log.WriteLine(report);
        // Typed lines are read at the prompt, once the turn ends: the person ends the wait (the turn says how after a minute).
        Assert.False(performed, report);
        Assert.True(sentAfterCtrlC, report);
        Assert.True(ended, report);
    }

    [Fact]
    public async Task IDE_the_second_prompt_is_queued_while_the_first_answer_never_starts_and_runs_once_it_is_stopped()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Silent = req => !Summarizing(req) && FakeGateway.Last(req) == "first";
        _gateway.Answer = req => Reply.Say("answer to " + FakeGateway.Last(req));
        await using var web = await WebRun.StartAsync(h);

        using var first = await web.SendAsync("first");
        await first.UntilAsync("assistant");
        // Sent while the answer is written: refused as a message of its own, taken into the queue.
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/messages", new JsonObject { ["text"] = "second" })).StatusCode);
        var queued = await web.PostAsync("/api/queue", new JsonObject { ["text"] = "second" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var state = await web.GetJsonAsync("/api/state");
        Assert.Equal(["second"], state["queued"]!.AsArray().Select(q => q!.GetValue<string>()));

        // Stop ends the wait, and the queued message runs.
        var stop = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/stop", new JsonObject())).StatusCode);
        var events = await first.RestAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "stopped");
        Assert.True(await Until(() => web.GetJsonAsync("/api/session").GetAwaiter().GetResult()["messages"]!.AsArray()[^1]!["content"]?.ToString() == "answer to second",
            TimeSpan.FromSeconds(15)), "the queued message was not answered after Stop");
        log.WriteLine($"The queued message was answered {stop.Elapsed.TotalMilliseconds:0} ms after Stop.");
        Assert.Empty((await web.GetJsonAsync("/api/state"))["queued"]!.AsArray());
    }

    // ------------------------------------------------------------------ how long the waits can be

    [Fact]
    public async Task A_request_whose_answer_never_starts_is_said_after_a_minute_and_given_up_after_one_more_try()
    {
        _gateway.Silent = _ => true;
        var said = new List<string>();
        // The defaults scaled down: 1 s stands for the 15 minutes before the first event.
        var client = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key)
        {
            FirstEventWait = TimeSpan.FromSeconds(1),
            EventWait = TimeSpan.FromSeconds(1),
            RetryWaits = [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 5)],
            Retrying = said.Add,
            // The minute after which the person is told, scaled down too.
            SlowNotice = TimeSpan.FromMilliseconds(400),
        };
        var clock = Stopwatch.StartNew();
        var call = client.CompleteAsync(Ask("hi"), null, CancellationToken.None);
        var finished = await Task.WhenAny(call, Task.Delay(Deadline)) == call;
        var took = clock.Elapsed;
        var error = call.IsFaulted ? call.Exception!.InnerException : null;
        var defaults = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key);

        var report = $"finished = {finished} after {took.TotalSeconds:0.0} s, {_gateway.Requests.Count} attempts each waiting the whole first-event wait " +
                     $"({error?.GetType().Name}: {error?.Message}); notices: {said.Count}. With the defaults: {defaults.RetryWaits.Length + 1} × " +
                     $"{defaults.FirstEventWait.TotalMinutes:0} min + {defaults.RetryWaits.Sum(w => w.TotalSeconds):0} s = {WorstCase(defaults).TotalMinutes:0.#} min of \"Thinking\".";
        log.WriteLine(report);
        Assert.True(finished, report);
        // A request the engine never starts to answer is asked once more, not 6 times over, and the person is told it waits.
        Assert.True(_gateway.Requests.Count == 2 && took < TimeSpan.FromSeconds(3.5), report);
        Assert.Contains(said, n => n.StartsWith("The model has not started answering for", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compaction_does_not_run_again_on_every_step_of_a_long_turn()
    {
        const int Reads = 45;
        using var h = new Harness(_gateway, _mcp);
        for (var i = 0; i < Reads; i++)
        {
            // About 1,760 characters read back: under the 2,000 that Shrink cuts tool results to.
            h.Write($"f{i}.txt", string.Join('\n', Enumerable.Range(0, 20).Select(_ => new string((char)('a' + i % 26), 80))));
        }
        _gateway.Answer = req =>
        {
            if (Summarizing(req))
            {
                return Reply.Say("SUMMARY of the earlier work.");
            }
            var reads = req["messages"]!.AsArray().Count(m => m!["role"]!.GetValue<string>() == "tool");
            return reads < Reads ? Reply.Call(("read_file", $$"""{"path":"f{{reads}}.txt"}""")) : Reply.Say("done");
        };
        await using var rt = await StartAsync(h);
        // An earlier exchange: the first compaction has something to summarize.
        rt.Agent.Load([new JsonObject { ["role"] = "user", ["content"] = "an earlier question" }, new JsonObject { ["role"] = "assistant", ["content"] = "an earlier answer" }]);

        var run = rt.Agent.RunAsync("read all the files", new Spend(), CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(Deadline)) == run;
        var requests = _gateway.Requests;
        var order = string.Concat(requests.Select(r => Summarizing(r) ? "S" : "."));
        var summaries = requests.Count(Summarizing);
        var steps = requests.Count - summaries;
        var firstSummary = order.IndexOf('S');

        var report = $"finished = {finished} ({(run.IsFaulted ? run.Exception!.InnerException!.Message : "ok")}); {steps} steps and {summaries} summary requests " +
                     $"(. a step, S a summary): {order}. After the first compaction (request {firstSummary}), every step that follows starts with another summary " +
                     $"of the summary: the kept part (the whole current turn) alone stays above {rt.Compaction.At}% of the window, and Shrink cuts nothing under 2,000 characters.";
        log.WriteLine(report);
        Assert.True(finished, report);
        Assert.True(summaries <= 2, report);
    }

    // ------------------------------------------------------------------ Ctrl+C during the waits, and the loops' bounds

    [Fact]
    public async Task Ctrl_C_stops_a_turn_whose_answer_never_starts_at_once()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Silent = _ => true;
        await using var rt = await StartAsync(h);
        using var cts = new CancellationTokenSource();
        var run = rt.Agent.RunAsync("hi", new Spend(), cts.Token);
        await _gateway.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(500);
        var clock = Stopwatch.StartNew();
        cts.Cancel();
        var ended = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))) == run;
        log.WriteLine($"ended = {ended} in {clock.ElapsedMilliseconds} ms, canceled = {run.IsCanceled}, last message: {rt.Agent.Messages[^1].ToJsonString()}");
        Assert.True(ended, "Ctrl+C did not end the wait for the first event within 10 s");
        Assert.True(run.IsCanceled || run.Exception?.InnerException is OperationCanceledException);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Ctrl_C_stops_the_retries_of_a_gateway_that_answers_503_at_once()
    {
        _gateway.Failure = _ => 503;
        var said = new List<string>();
        var client = new GatewayClient(new HttpClient(), _gateway.Url, FakeGateway.Key) { Retrying = said.Add };
        using var cts = new CancellationTokenSource();
        var call = client.CompleteAsync(Ask("hi"), null, cts.Token);
        // In the second wait (5 s) of the defaults.
        Assert.True(await Until(() => said.Count >= 2, TimeSpan.FromSeconds(15)), $"notices: {string.Join(" / ", said)}");
        var clock = Stopwatch.StartNew();
        cts.Cancel();
        var ended = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10))) == call;
        log.WriteLine($"ended = {ended} in {clock.ElapsedMilliseconds} ms after {_gateway.Requests.Count} tries; notices: {string.Join(" / ", said)}");
        Assert.True(ended);
        Assert.True(call.IsCanceled || call.Exception?.InnerException is OperationCanceledException);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Ctrl_C_stops_a_compactions_summary_request_that_never_answers_at_once()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Silent = Summarizing;
        await using var rt = await StartAsync(h);
        // History over the threshold: the next turn compacts first.
        var history = new List<JsonObject>();
        for (var i = 0; i < 40; i++)
        {
            history.Add(new JsonObject { ["role"] = "user", ["content"] = $"question {i} " + new string('q', 1500) });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"answer {i} " + new string('a', 1500) });
        }
        rt.Agent.Load(history);
        Assert.True(rt.Agent.Estimate() > rt.Model.Context * rt.Compaction.At / 100.0);
        using var cts = new CancellationTokenSource();
        var run = rt.Agent.RunAsync("next", new Spend(), cts.Token);
        Assert.True(await Until(() => _gateway.Requests.Any(Summarizing), TimeSpan.FromSeconds(20)), "no summary request");
        await Task.Delay(500);
        var clock = Stopwatch.StartNew();
        cts.Cancel();
        var ended = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))) == run;
        log.WriteLine($"ended = {ended} in {clock.ElapsedMilliseconds} ms; {rt.Agent.Messages.Count} messages, the last: {Fmt.OneLine(rt.Agent.Messages[^1].ToJsonString(), 120)}");
        Assert.True(ended, "Ctrl+C did not end the compaction's summary request within 10 s");
        Assert.True(run.IsCanceled || run.Exception?.InnerException is OperationCanceledException);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task An_answer_that_keeps_stalling_mid_way_is_carried_on_a_bounded_number_of_times()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Hang = true;
        await using var rt = await StartAsync(h);
        rt.Gateway.FirstEventWait = TimeSpan.FromSeconds(2);
        rt.Gateway.EventWait = TimeSpan.FromMilliseconds(500);
        var run = rt.Agent.RunAsync("tell me", new Spend(), CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(Deadline)) == run;
        log.WriteLine($"finished = {finished}, {_gateway.Requests.Count} requests, faulted = {run.IsFaulted}");
        Assert.True(finished, $"the turn did not end within {Deadline.TotalSeconds} s: {_gateway.Requests.Count} requests");
        Assert.Equal(1 + Agent.MaxInterruptions, _gateway.Requests.Count);
    }
}
