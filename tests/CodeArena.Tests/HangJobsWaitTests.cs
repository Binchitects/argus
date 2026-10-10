using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Reproduction (hypothesis "jobs-wait"): the model starts a dev server with no_time_limit, says it is up, and
/// the turn never ends: Agent.TurnAsync waits for the job however long (WaitForJobsAsync), so the person's
/// second prompt is never run (queued in the terminal, not read at all with the line editor, refused as busy
/// in the IDE). Each test asserts what the person expects (the turn ends once the model has answered, the
/// next prompt runs) within a deadline, so it FAILS, rather than hangs, while the stall is there.
/// </summary>
public sealed class HangJobsWaitTests : IDisposable
{
    /// <summary>
    /// How long after the model's final answer the turn has to be over (a turn that ends, ends in well under a
    /// second here). Under the 30 s after which EventStream gives up a read, so the IDE's stream stays usable.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(25);

    private const string DevServer = "echo 'VITE ready on http://localhost:5173'; sleep 300";
    private const string Up = "The dev server is up at http://localhost:5173.";
    private const string Second = "what does the README say?";
    private const string SecondAnswer = "The README says hello.";

    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public HangJobsWaitTests()
    {
        // "npm run dev": started with no_time_limit, then the model answers that it is up and stops calling tools.
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            var last when last.StartsWith("Started as job", StringComparison.Ordinal) => Reply.Say(Up),
            var last when last.Contains("has ended", StringComparison.Ordinal) => Reply.Say("The dev server stopped."),
            var last when last.Contains(Second, StringComparison.Ordinal) => Reply.Say(SecondAnswer),
            _ => Reply.Call(("run_shell", new JsonObject { ["command"] = DevServer, ["no_time_limit"] = true, ["description"] = "npm run dev" }.ToJsonString())),
        };
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(seconds), $"Not within {seconds} s: {what}");
            await Task.Delay(50);
        }
    }

    /// <summary>True when the condition held within the time; false when the time ran out (no throw).</summary>
    private static async Task<bool> Within(Func<bool> condition, TimeSpan time)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > time)
            {
                return false;
            }
            await Task.Delay(100);
        }
        return true;
    }

    private bool ModelGot(string text) => _gateway.Requests.Any(r => FakeGateway.Last(r).Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task Agent_turn_ends_once_the_model_has_answered_while_a_dev_server_it_started_runs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        var output = new StringWriter();
        var screen = TextWriter.Synchronized(output);
        string Said()
        {
            lock (screen)
            {
                return output.ToString();
            }
        }
        var env = new CliEnv { In = new StringReader(""), Out = screen, Err = screen, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var ui = new Ui(env.In, env.Out, env.Err, false, false);
        await using var rt = await Runtime.StartAsync(new Options(), env, ui, CancellationToken.None);
        using var key = env.Cancel.BeginTurn();
        var turn = rt.Agent.RunAsync("start the dev server", new Spend(), key.Token);
        try
        {
            await Until(() => _gateway.Requests.Count == 2 && Said().Contains(Up, StringComparison.Ordinal), "the model started the server and answered");
            var answered = Stopwatch.StartNew();

            var ended = await Task.WhenAny(turn, Task.Delay(Deadline)) == turn;

            var job = Assert.Single(rt.Jobs.All);
            Assert.True(ended,
                $"STALL: the model answered \"{Up}\" (its request #{_gateway.Requests.Count}, no tool calls) {answered.Elapsed.TotalSeconds:0} s ago, " +
                $"yet Agent.RunAsync has not returned; job {job.Id} `{job.Command}` is {job.Status()}. The terminal said:\n{Said()}");
        }
        finally
        {
            env.Cancel.Press();
            await Task.WhenAny(turn, Task.Delay(TimeSpan.FromSeconds(10)));
            rt.Jobs.StopAll("the test");
            env.Cancel.EndTurn();
        }
    }

    [Fact]
    public async Task Terminal_with_typed_lines_runs_the_second_prompt_while_a_dev_server_runs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        var typed = new TypedLines();
        var output = new StringWriter();
        var screen = TextWriter.Synchronized(output);
        string Said()
        {
            lock (screen)
            {
                return output.ToString();
            }
        }
        var env = new CliEnv { In = typed, Out = screen, Err = screen, Env = _ => null, Cwd = h.Work, Paths = h.Paths, InTerminal = true };
        var exit = Task.Run(() => Cli.RunAsync(["chat"], env));
        var sentWhileRunning = false;
        var afterCtrlC = "";
        try
        {
            typed.Add("start the dev server");
            await Until(() => _gateway.Requests.Count == 2 && Said().Contains(Up, StringComparison.Ordinal), "the model started the server and answered");

            // The turn is over: the person's next prompt goes at once, the server running on.
            typed.Add(Second);
            sentWhileRunning = await Within(() => ModelGot(Second), Deadline);
            await Until(() => Said().Contains(SecondAnswer, StringComparison.Ordinal), "the second prompt was answered");
            afterCtrlC = "(no Ctrl+C needed)";
        }
        finally
        {
            env.Cancel.Press();
            typed.Add("/exit");
            await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(15)));
        }
        Assert.True(sentWhileRunning,
            $"STALL: \"{Second}\" was typed after the model answered \"{Up}\", and in {Deadline.TotalSeconds:0} s it never reached the model " +
            $"({_gateway.Requests.Count} requests, the last \"{Fmt.OneLine(FakeGateway.Last(_gateway.Requests[^1]), 80)}\"). " +
            $"After Ctrl+C the terminal said:\n{afterCtrlC}\n--- whole terminal ---\n{Said()}");
    }

    [Fact]
    public async Task Terminal_with_the_line_editor_runs_the_second_prompt_while_a_dev_server_runs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        var keys = new BlockingKeys();
        var output = new StringWriter();
        var screen = TextWriter.Synchronized(output);
        string Said()
        {
            lock (screen)
            {
                return output.ToString();
            }
        }
        var env = new CliEnv { In = TextReader.Null, Out = screen, Err = screen, Env = _ => null, Cwd = h.Work, Paths = h.Paths, InTerminal = true, Keys = keys };
        var exit = Task.Run(() => Cli.RunAsync(["chat"], env));
        var sentWhileRunning = false;
        var unread = 0;
        var ranAfterCtrlC = false;
        try
        {
            keys.Type("start the dev server\n");
            await Until(() => _gateway.Requests.Count == 2 && Said().Contains(Up, StringComparison.Ordinal), "the model started the server and answered");

            // The turn is over: the prompt reads the next message at once, the server running on.
            keys.Type(Second + "\n");
            sentWhileRunning = await Within(() => ModelGot(Second), Deadline);
            unread = keys.Waiting;
            ranAfterCtrlC = sentWhileRunning;
        }
        finally
        {
            env.Cancel.Press();
            keys.Type("/exit\n");
            keys.End();
            await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(15)));
        }
        Assert.True(sentWhileRunning,
            $"STALL: \"{Second}\" + Enter was typed after the model answered \"{Up}\"; in {Deadline.TotalSeconds:0} s it never reached the model, " +
            $"and {unread} of its keys were still unread (nothing echoed, nothing queued). Ran only after Ctrl+C stopped the server: {ranAfterCtrlC}.\n" +
            $"--- whole terminal ---\n{Said()}");
    }

    [Fact]
    public async Task IDE_turn_ends_and_takes_the_second_prompt_while_a_dev_server_runs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        await using var web = await WebRun.StartAsync(h);
        using var stream = await web.SendAsync("start the dev server");
        var started = await stream.UntilAsync("job");
        Assert.Equal(DevServer, started["command"]!.GetValue<string>());
        var content = new StringBuilder();
        while (!content.ToString().Contains(Up, StringComparison.Ordinal))
        {
            content.Append((await stream.UntilAsync("content"))["text"]!.GetValue<string>());
        }
        var answered = Stopwatch.StartNew();

        var done = stream.UntilAsync("done");
        await Task.WhenAny(done, Task.Delay(Deadline));
        var ended = done.IsCompletedSuccessfully;
        var busy = (await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>();
        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/messages")
        {
            Content = new StringContent(new JsonObject { ["text"] = Second }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await web.Http.SendAsync(second, HttpCompletionOption.ResponseHeadersRead);
        var refusal = response.StatusCode == HttpStatusCode.OK ? "" : await response.Content.ReadAsStringAsync();
        var seenAtDeadline = string.Join(", ", stream.Seen.Select(e => e["type"]));

        // Clean up: the page's Stop on the job, which lets the turn end.
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/jobs/stop", new JsonObject { ["id"] = 1 })).StatusCode);
        await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(20)));
        var endedAfterStop = done.IsCompletedSuccessfully;

        Assert.True(ended && response.StatusCode == HttpStatusCode.OK,
            $"STALL: the model answered \"{Up}\" with no tool calls; {answered.Elapsed.TotalSeconds:0} s later the turn's stream had no \"done\" " +
            $"(ended: {ended}; events: {seenAtDeadline}), /api/state busy = {busy}, the second prompt got {(int)response.StatusCode} {refusal}; " +
            $"after the job's Stop the turn ended: {endedAfterStop}");
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}

/// <summary>A terminal read key by key, as a person types: each key waits until the test types it; KeyAvailable is false (typed, not pasted).</summary>
internal sealed class BlockingKeys : IKeyboard
{
    private readonly BlockingCollection<ConsoleKeyInfo> _keys = [];

    public int Width => 80;
    public int Height => 1000;
    public bool KeyAvailable => false;
    public Action? Suspend => null;
    /// <summary>Keys typed and not read yet.</summary>
    public int Waiting => _keys.Count;

    public ConsoleKeyInfo? ReadKey() => _keys.TryTake(out var key, Timeout.Infinite) ? key : null;

    public bool WaitForKey(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (_keys.Count == 0 && !_keys.IsCompleted && DateTime.UtcNow < until)
        {
            Thread.Sleep(5);
        }
        return _keys.Count > 0 || _keys.IsCompleted;
    }

    public IDisposable CaptureCtrlC() => new Nothing();

    /// <summary>Each character as its key; \n as Enter.</summary>
    public void Type(string text)
    {
        foreach (var c in text)
        {
            _keys.Add(c == '\n'
                ? new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)
                : new ConsoleKeyInfo(c, char.IsAsciiLetter(c) ? ConsoleKey.A + (char.ToUpperInvariant(c) - 'A') : 0, char.IsAsciiLetterUpper(c), false, false));
        }
    }

    public void End() => _keys.CompleteAdding();

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
