using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Commands with no time limit: run on their own, watched to their end, stopped only by the person. A turn does not wait for
/// them: the model carries on from each end in a turn of its own (a one-shot run waits, as nobody types next).
/// </summary>
public sealed class JobTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly string _root = Directory.CreateTempSubdirectory("arena-jobs-").FullName;

    private static ProcessStartInfo Sh(string script, string? cwd = null)
    {
        var psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = cwd ?? Path.GetTempPath() };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return psi;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string what, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(seconds), $"Not within {seconds} s: {what}");
            await Task.Delay(100);
        }
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(seconds), $"Not within {seconds} s: {what}");
            await Task.Delay(50);
        }
    }

    /// <summary>Gone, or a zombie no one reaped (a container without an init): it runs no more.</summary>
    private static bool Ended(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [Fact]
    public async Task A_long_command_runs_to_its_end_and_says_its_exit_code_and_the_end_of_its_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var jobs = new CommandJobs();
        var streamed = new StringBuilder();
        jobs.Output += (_, text) =>
        {
            lock (streamed)
            {
                streamed.Append(text);
            }
        };
        var ended = new TaskCompletionSource<CommandJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Ended += job => ended.TrySetResult(job);

        var job = jobs.Start("count to 3000", Sh("i=1; while [ $i -le 3000 ]; do echo line $i; i=$((i+1)); done; sleep 1; echo oops >&2; exit 3"));
        Assert.True(job.Running);
        Assert.StartsWith("running for ", job.Status());
        await job.Done.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(job.Running);
        Assert.Equal(3, job.ExitCode);
        Assert.Matches(@"^exit code 3 after \d+s$", job.Status());
        Assert.Same(job, await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // Streamed as it came, all of it; the end kept for the model.
        lock (streamed)
        {
            Assert.Contains("line 1\n", streamed.ToString());
            Assert.Contains("line 3000\n", streamed.ToString());
        }
        var notice = job.Notice(300);
        Assert.StartsWith("[Code Arena: job 1 has ended. This is a report, not the person's words", notice);
        Assert.Contains("Job 1 (`count to 3000`): exit code 3 after", notice);
        Assert.Contains("characters before)", notice);
        Assert.Contains("line 3000", notice);
        Assert.Contains("oops", notice);
        Assert.DoesNotContain("line 1\n", notice);
        // The model is told of it once.
        Assert.Equal([job], jobs.TakeEnded());
        Assert.Empty(jobs.TakeEnded());
    }

    [Fact]
    public async Task Stopping_ends_the_command_and_what_it_started_and_says_who_stopped_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var jobs = new CommandJobs();
        var pidFile = Path.Combine(_root, "child.pid");
        var job = jobs.Start("serve", Sh($"echo started; sleep 300 & echo $! > '{pidFile}'; wait; echo never"));
        await Until(() => job.Length > 0 && File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0, "the command started its child");
        var child = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        job.Stop("by the person, in the IDE");
        await job.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Matches(@"^stopped \(by the person, in the IDE\) after \d+s$", job.Status());
        Assert.DoesNotContain("never", job.Tail(1000));
        await Until(() => Ended(child), "the child the command started ended too", seconds: 5);
        // Stopping again, or one that ended: nothing.
        job.Stop("again");
        Assert.StartsWith("stopped (by the person, in the IDE)", job.Status());
    }

    [Fact]
    public async Task Several_run_at_once_and_each_is_told_as_it_ends()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var jobs = new CommandJobs();
        var slow = jobs.Start("slow", Sh("sleep 3; echo slow done"));
        var quick = jobs.Start("quick", Sh("sleep 0.3; echo quick done"));
        Assert.Equal([1, 2], jobs.Running.Select(j => j.Id));

        // Waiting returns when one of them ends: the quick one.
        await jobs.WaitAnyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([quick], jobs.TakeEnded());
        Assert.True(slow.Running);
        await jobs.WaitAnyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([slow], jobs.TakeEnded());
        Assert.Empty(jobs.Running);
        Assert.Contains("slow done", slow.Tail(100));
        // Nothing running: waiting returns at once.
        await jobs.WaitAnyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_agent_waits_however_long_the_command_takes_then_carries_on_with_its_exit_code_and_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            var last when last.Contains("has ended", StringComparison.Ordinal) => Reply.Say("Build done: " + Fmt.OneLine(last, 400)),
            var last when last.StartsWith("Started as job", StringComparison.Ordinal) => Reply.Say("The build runs; I wait for it."),
            // A time limit given with it does not apply: it runs past it.
            _ => Reply.Call(("run_shell", """{"command":"sleep 2; echo built ok; exit 4","no_time_limit":true,"timeout_seconds":1,"description":"build"}""")),
        };

        Assert.Equal(0, await h.Run("", "-p", "build it", "--mode", "yolo"));

        Assert.StartsWith("Build done: [Code Arena: job 1 has ended.", h.Out.Trim());
        Assert.Matches(@"exit code 4 after [23]s", h.Out);
        Assert.Contains("built ok", h.Out);
        // The person watched it: its output under its number, how it ended.
        Assert.Contains("job 1 started, with no time limit: sleep 2; echo built ok; exit 4", h.Err);
        Assert.Contains("│1 built ok", h.Err);
        Assert.Matches(@"job 1 ended: exit code 4 after [23]s", h.Err);
        Assert.Contains("Waiting for job 1 (sleep 2; echo built ok; exit 4) to end: no time limit, Ctrl+C stops it.", h.Err);
        // The model was told as a call of command_output and its result, after its answer.
        var last = _gateway.Requests[^1]["messages"]!.AsArray();
        Assert.Equal("command_output", last[^2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("""{"job":1}""", last[^2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("tool", last[^1]!["role"]!.GetValue<string>());
        Assert.Equal(3, _gateway.Requests.Count);
    }

    [Fact]
    public async Task Ctrl_C_stops_the_turn_and_the_commands_it_started()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        _gateway.Answer = _ => Reply.Call(("run_shell", """{"command":"echo up; sleep 300","no_time_limit":true}"""));
        // The model is slow to answer after the command started: the turn still runs when the person presses Ctrl+C.
        _gateway.Silent = FakeGateway.HasToolResults;
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = TextWriter.Synchronized(output), Err = TextWriter.Synchronized(output), Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var ui = new Ui(env.In, env.Out, env.Err, false, false);
        await using var rt = await Runtime.StartAsync(new Options(), env, ui, CancellationToken.None);
        using var key = env.Cancel.BeginTurn();
        var turn = rt.Agent.RunAsync("run the long test", new Spend(), key.Token);
        await Until(() => rt.Jobs.All.Count == 1 && rt.Jobs.All[0].Length > 0 && _gateway.Requests.Count == 2, "the job ran and the model is asked again");
        Assert.False(turn.IsCompleted);

        Assert.True(env.Cancel.Press());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(10)));
        var job = rt.Jobs.All[0];
        await job.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("stopped (by the person) after", job.Status());
        env.Cancel.EndTurn();

        // The next turn is not told of it again: the person stopped it themselves.
        _gateway.Silent = _ => false;
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal("ok", await rt.Agent.RunAsync("next", new Spend(), CancellationToken.None));
        Assert.DoesNotContain("has ended", FakeGateway.Last(_gateway.Requests[^1]));
    }

    [Fact]
    public async Task A_turn_ends_when_the_model_answers_and_a_command_in_the_background_outlives_a_stopped_turn()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("The build runs and the server is up.")
            : Reply.Call(
                ("run_shell", """{"command":"echo building; sleep 300","no_time_limit":true}"""),
                ("run_shell", """{"command":"echo listening on 5173; sleep 300","background":true}"""));
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = TextWriter.Synchronized(output), Err = TextWriter.Synchronized(output), Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var ui = new Ui(env.In, env.Out, env.Err, false, false);
        await using var rt = await Runtime.StartAsync(new Options(), env, ui, CancellationToken.None);

        // The turn is over as soon as the model has answered: both commands run on.
        var answer = await rt.Agent.RunAsync("build and serve", new Spend(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("The build runs and the server is up.", answer);
        Assert.Equal(2, rt.Jobs.Running.Count);
        var server = rt.Jobs.Find(2)!;
        Assert.True(server.Background);
        // The model got the server's first words with the call's result.
        Assert.Contains(_gateway.Requests[^1]["messages"]!.AsArray(), m => m!["role"]!.GetValue<string>() == "tool" && m["content"]!.GetValue<string>().Contains("listening on 5173", StringComparison.Ordinal));

        // A stopped turn stops what it started with no time limit, not what runs in the background.
        _gateway.Silent = _ => true;
        using var key = env.Cancel.BeginTurn();
        var next = rt.Agent.RunAsync("run the tests too", new Spend(), key.Token);
        await Until(() => _gateway.Requests.Count == 3, "the next turn asks the model");
        env.Cancel.Press();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(10)));
        env.Cancel.EndTurn();
        Assert.True(rt.Jobs.Find(1)!.Running, "the build was started by an earlier turn");
        Assert.True(server.Running);
        rt.Jobs.StopAll("the test");
    }

    [Fact]
    public async Task No_time_limit_asks_where_commands_ask_apart_from_always_for_the_command_and_stop_command_asks_even_in_yolo()
    {
        var shell = LocalTools.All().Single(t => t.Name == "run_shell");
        var stop = LocalTools.All().Single(t => t.Name == "stop_command");
        JsonObject Command(string command, bool unlimited = false) => unlimited ? new() { ["command"] = command, ["no_time_limit"] = true } : new() { ["command"] = command };

        // Ask: "always" for `npm test` does not cover it with no time limit.
        var output = new StringWriter();
        var ask = new Permissions(new Ui(new StringReader("a\nn\n"), output, output, false, true), Mode.Ask);
        Assert.Null(await ask.CheckAsync(shell, Command("npm test"), default));
        Assert.Null(await ask.CheckAsync(shell, Command("npm test -- --ci"), default));
        Assert.Contains("declined", await ask.CheckAsync(shell, Command("npm test", unlimited: true), default));
        Assert.Contains("Allow run_shell with no time limit?", output.ToString());
        Assert.Contains("[a]lways for `npm test …` with no time limit", output.ToString());

        // Auto-edit asks too; a run that cannot ask says so.
        var cannot = new Permissions(new Ui(new StringReader(""), output, output, false, false), Mode.AutoEdit);
        Assert.Equal("run_shell with no time limit needs the person's approval, and this run cannot ask. " +
                     "Say what you would have done; the person can run again with --mode auto-edit (edits) or --mode yolo (everything).",
            await cannot.CheckAsync(shell, Command("make", unlimited: true), default));

        // Yolo runs it (watched; Ctrl+C or Stop ends it), but stopping one asks first, every time: no "always" is offered
        // ("a" is no answer to it), and none is kept.
        var said = new StringWriter();
        var yolo = new Permissions(new Ui(new StringReader("n\na\ny\ny\n"), said, said, false, true), Mode.Yolo);
        Assert.Null(await yolo.CheckAsync(shell, Command("make", unlimited: true), default));
        Assert.Contains("declined", await yolo.CheckAsync(stop, new JsonObject { ["job"] = 1 }, default));
        Assert.Null(await yolo.CheckAsync(stop, new JsonObject { ["job"] = 1 }, default));
        Assert.Null(await yolo.CheckAsync(stop, new JsonObject { ["job"] = 2 }, default));
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(said.ToString(), @"Allow stop_command\?").Count);
        Assert.Contains("[y]es, [n]o ›", said.ToString());
        Assert.DoesNotContain("lways", said.ToString());

        // The same in the IDE: its question carries no "always", and "always" sent anyway is a yes for this call only.
        var questions = new List<ApprovalQuestion>();
        var ide = new Permissions(new Ui(new StringReader(""), said, said, false, false), Mode.Yolo)
        {
            Asker = (q, _) =>
            {
                questions.Add(q);
                return Task.FromResult(Approval.Always);
            },
        };
        Assert.Null(await ide.CheckAsync(stop, new JsonObject { ["job"] = 1 }, default));
        Assert.Null(await ide.CheckAsync(stop, new JsonObject { ["job"] = 1 }, default));
        Assert.Equal(2, questions.Count);
        Assert.All(questions, q => Assert.Null(q.Always));
    }

    [Fact]
    public async Task The_model_stops_a_command_only_when_the_person_agrees()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            var last when last.Contains("has ended", StringComparison.Ordinal) => Reply.Say("It ended: " + Fmt.OneLine(last, 300)),
            var last when last.StartsWith("Started as job", StringComparison.Ordinal) => Reply.Call(("stop_command", """{"job":1}""")),
            var last when last.StartsWith("Job 1 is being stopped", StringComparison.Ordinal) => Reply.Say("Stopping it."),
            _ => Reply.Call(("run_shell", """{"command":"sleep 300","no_time_limit":true}""")),
        };
        Assert.Equal(0, await h.Run("watch it then stop it\ny\n/jobs\n/exit\n", "chat", "--mode", "yolo"));
        Assert.Contains("Allow stop_command?", h.Out);
        Assert.Contains("It ended: [Code Arena: job 1 has ended.", h.Out);
        Assert.Contains("stopped (by the person, through stop_command) after", h.Out);
        Assert.Matches(@"job 1  sleep 300  stopped \(by the person, through stop_command\) after \d+s", h.Out);
    }

    [Fact]
    public async Task The_IDE_shows_a_jobs_output_after_the_turn_its_Stop_ends_it_and_the_model_carries_on()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        await using var web = await WebRun.StartAsync(h);
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            var last when last.Contains("has ended", StringComparison.Ordinal) => Reply.Say("Told: " + Fmt.OneLine(last, 300)),
            var last when last.StartsWith("Started as job", StringComparison.Ordinal) => Reply.Say("Watching the tests."),
            _ => Reply.Call(("run_shell", """{"command":"echo first line; sleep 300","no_time_limit":true}""")),
        };
        using var stream = await web.SendAsync("run the slow tests");
        var started = await stream.UntilAsync("job");
        Assert.Equal(1, started["job"]!.GetValue<int>());
        Assert.Equal("echo first line; sleep 300", started["command"]!.GetValue<string>());
        // The turn ends once the model has answered; the command runs on.
        var first = await stream.RestAsync();
        Assert.Contains("Watching the tests.", string.Concat(first.Where(e => e["type"]!.GetValue<string>() == "content").Select(e => e["text"]!.GetValue<string>())));
        Assert.Equal("done", first[^1]["type"]!.GetValue<string>());

        // Its output comes with the state, for as long as it runs.
        await UntilAsync(async () => (await web.GetJsonAsync("/api/state"))["jobs"]![0]!["output"]?.GetValue<string>() == "first line\n", "the state carries its output");
        var state = await web.GetJsonAsync("/api/state");
        Assert.True(state["jobs"]![0]!["running"]!.GetValue<bool>());
        Assert.False(state["busy"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/jobs/stop", new JsonObject { ["id"] = 1 })).StatusCode);
        // Its end is news: the model carries on from it in a turn of its own, saved in the session.
        await UntilAsync(async () => (await web.GetJsonAsync("/api/session")).ToJsonString().Contains("Told: [Code Arena: job 1 has ended.", StringComparison.Ordinal),
            "the model carried on from its end");
        var session = (await web.GetJsonAsync("/api/session")).ToJsonString();
        Assert.Contains("command_output", session);
        Assert.Contains("stopped (by the person, in the IDE)", session);
        await UntilAsync(async () => !(await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>(), "the turn ended");
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/jobs/stop", new JsonObject { ["id"] = 9 })).StatusCode);
    }

    [Fact]
    public async Task A_turn_that_fails_leaves_its_commands_running_and_the_next_turn_tells_the_model_how_they_ended()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        // The model starts the build and carries on; then the model's backend restarts and the gateway answers 500.
        _gateway.Answer = _ => Reply.Call(("run_shell", """{"command":"sleep 1.5; echo built ok","no_time_limit":true}"""));
        _gateway.Failure = req => FakeGateway.HasToolResults(req) ? 500 : 0;
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = TextWriter.Synchronized(output), Err = TextWriter.Synchronized(output), Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var ui = new Ui(env.In, env.Out, env.Err, false, false);
        await using var rt = await Runtime.StartAsync(new Options(), env, ui, CancellationToken.None);
        rt.Gateway.RetryWaits = [];

        var failed = await Assert.ThrowsAsync<GatewayException>(() => rt.Agent.RunAsync("build it", new Spend(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("restarting", failed.Message);
        // The failed request did not stop the build: it runs to its end on its own.
        var job = Assert.Single(rt.Jobs.All);
        await job.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(job.StoppedBy);
        Assert.Equal(0, job.ExitCode);
        var said = output.ToString();
        Assert.Contains("The model cannot carry on: ", said);
        Assert.Contains("the model hears how each ended when it does, or with your next message", said);
        await Until(() => output.ToString().Contains("job 1 ended: exit code 0", StringComparison.Ordinal), "its end is shown");
        Assert.Contains("│1 built ok", output.ToString());

        // The next turn tells the model how it ended, first thing.
        _gateway.Failure = _ => 0;
        _gateway.Answer = req => Reply.Say(FakeGateway.Last(req).Contains("job 1 has ended", StringComparison.Ordinal) ? "told" : "not told");
        Assert.Equal("told", await rt.Agent.RunAsync("go on", new Spend(), CancellationToken.None));
    }

    [Fact]
    public async Task In_a_terminal_read_by_lines_jobs_stop_ends_one_and_the_model_carries_on_from_each_end()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        // The model starts a build and a test server with no time limit, answers, and hears of each as it ends.
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            var last when last.Contains("job 1 has ended", StringComparison.Ordinal) => Reply.Say("Build done."),
            var last when last.Contains("job 2 has ended", StringComparison.Ordinal) => Reply.Say("The server is stopped; the build goes on."),
            "what now?" => Reply.Say("Nothing runs now."),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("Both run."),
            _ => Reply.Call(
                ("run_shell", """{"command":"echo building; sleep 3; echo built","no_time_limit":true}"""),
                ("run_shell", """{"command":"echo up; sleep 300","no_time_limit":true}""")),
        };
        // Plain lines (a pipe, TERM=dumb): read as they are typed, no line editor.
        var terminal = new SharedTerminal();
        var screen = new LockedWriter();
        var env = new CliEnv { In = terminal, Out = screen, Err = screen, Env = k => h.Env.GetValueOrDefault(k), Cwd = h.Work, Paths = h.Paths, InTerminal = true };
        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        string Said() => screen.ToString();
        try
        {
            terminal.Type("build and serve\n");
            await Until(() => Said().Contains("Both run.", StringComparison.Ordinal), "the turn ended once the model answered");
            terminal.Type("/jobs\n/jobs stop 2\n/jobs stop 9\n");
            await Until(() => Said().Contains("The server is stopped; the build goes on.", StringComparison.Ordinal), "the model carried on from job 2's end");
            Assert.Matches(@"job 1  echo building; sleep 3; echo built  running for \d+s", Said());
            Assert.Matches(@"job 2  echo up; sleep 300  running for \d+s", Said());
            Assert.Contains("There is no job 9: /jobs lists them.", Said());
            Assert.Matches(@"job 2 ended: stopped \(by the person, with /jobs stop\) after \d+s", Said());
            Assert.Contains("job 2 ended: the model carries on from it.", Said());
            await Until(() => Said().Contains("Build done.", StringComparison.Ordinal), "the model carried on from the build's end", seconds: 20);
            terminal.Type("what now?\n");
            await Until(() => Said().Contains("Nothing runs now.", StringComparison.Ordinal), "the next message was answered");
            var told = _gateway.Requests.Select(FakeGateway.Last).ToList();
            Assert.Contains(told, t => t.Contains("job 2 has ended", StringComparison.Ordinal) && t.Contains("stopped (by the person, with /jobs stop)", StringComparison.Ordinal));
            Assert.Contains(told, t => t.Contains("job 1 has ended", StringComparison.Ordinal) && t.Contains("exit code 0", StringComparison.Ordinal) && t.Contains("built", StringComparison.Ordinal));
        }
        finally
        {
            terminal.End();
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task Jobs_stop_with_no_turn_running_says_there_is_nothing_running()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/jobs\n/jobs stop\n/jobs stop 1\n/exit\n", "chat"));
        Assert.Contains("No commands with no time limit in this session.", h.Out);
        Assert.Contains("Which job? /jobs stop N (/jobs lists them).", h.Out);
        Assert.Contains("There is no job 1: /jobs lists them.", h.Out);
    }

    [Fact]
    public async Task A_commands_output_waits_while_the_person_is_asked_and_after_the_answers_line()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var typed = new TypedLines();
        var output = new StringWriter();
        var screen = TextWriter.Synchronized(output);
        var ui = new Ui(typed, screen, screen, false, canAsk: true);
        var jobs = new CommandJobs();
        var printer = new JobPrinter(ui);
        jobs.Output += printer.Output;
        jobs.Ended += printer.Ended;

        // The question is asked; the tests run meanwhile, to their end.
        var asked = Task.Run(() => ui.Ask("edit_file src/App.cs", "for file edits"));
        await Until(() => output.ToString().EndsWith("for file edits › ", StringComparison.Ordinal), "the question is asked");
        var job = jobs.Start("tests", Sh("i=1; while [ $i -le 20 ]; do echo test $i; i=$((i+1)); sleep 0.02; done"));
        await job.Done.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.EndsWith("for file edits › ", output.ToString());

        // Answered: what came meanwhile follows, in order.
        typed.Add("y");
        Assert.Equal(Approval.Yes, await asked.WaitAsync(TimeSpan.FromSeconds(5)));
        var after = output.ToString()[(output.ToString().IndexOf("for file edits › ", StringComparison.Ordinal) + 17)..];
        Assert.StartsWith("  │1 test 1\n", after);
        Assert.True(after.IndexOf("│1 test 20\n", StringComparison.Ordinal) < after.IndexOf("job 1 ended: exit code 0", StringComparison.Ordinal), after);

        // The model's answer streaming: no line breaks into it; they come when its line ends.
        output.GetStringBuilder().Clear();
        ui.Write("The tests ");
        var more = jobs.Start("more", Sh("echo more done"));
        await more.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("The tests ", output.ToString());
        ui.Write("pass.\n");
        Assert.StartsWith("The tests pass.\n  │2 more done\n", output.ToString());
    }

    [Fact]
    public async Task The_spinner_goes_on_under_a_commands_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var output = new StringWriter();
        var screen = TextWriter.Synchronized(output);
        var ui = new Ui(TextReader.Null, screen, screen, false, canAsk: false) { Animate = true };
        var jobs = new CommandJobs();
        var printer = new JobPrinter(ui);
        jobs.Output += printer.Output;
        ui.StartSpinner("Working");
        var job = jobs.Start("watch", Sh("echo compiling; sleep 300"));
        try
        {
            await Until(() => output.ToString().Contains("│1 compiling\n", StringComparison.Ordinal), "the output came");
            var said = output.ToString();
            var at = said.IndexOf("│1 compiling\n", StringComparison.Ordinal);
            await Until(() => output.ToString()[at..].Contains("Working", StringComparison.Ordinal), "the spinner is drawn again under it", seconds: 3);
        }
        finally
        {
            ui.StopSpinner();
            job.Stop("the test");
        }
    }

    [Fact]
    public async Task In_the_IDE_a_turn_that_fails_leaves_its_commands_running_with_their_Stop()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        await using var web = await WebRun.StartAsync(h);
        _gateway.Answer = _ => Reply.Call(("run_shell", """{"command":"echo first line; sleep 300","no_time_limit":true}"""));
        _gateway.Failure = req => FakeGateway.HasToolResults(req) ? 500 : 0;
        using var stream = await web.SendAsync("run the slow tests");
        await stream.UntilAsync("job");
        var notice = await stream.UntilAsync("notice");
        Assert.StartsWith("The model cannot carry on: ", notice["text"]!.GetValue<string>());
        var rest = await stream.RestAsync();
        Assert.Contains("restarting", rest.Single(e => e["type"]!.GetValue<string>() == "error")["message"]!.GetValue<string>());
        Assert.Equal("done", rest[^1]["type"]!.GetValue<string>());

        // The turn is over and the command runs on, listed in the state with its output, and the page has its Stop.
        var state = await web.GetJsonAsync("/api/state");
        Assert.False(state["busy"]!.GetValue<bool>());
        Assert.True(state["jobs"]![0]!["running"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/jobs/stop", new JsonObject { ["id"] = 1 })).StatusCode);
        await UntilAsync(async () => (await web.GetJsonAsync("/api/state"))["jobs"]![0]!["status"]!.GetValue<string>().StartsWith("stopped (by the person, in the IDE) after", StringComparison.Ordinal),
            "it stopped");
        // Its end starts a turn of its own (which fails too, the gateway still down), once: the page is not left busy.
        await UntilAsync(async () => !(await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>(), "no turn runs");
    }

    [Fact]
    public async Task The_IDE_streams_a_commands_output_however_much_it_writes_and_a_page_that_comes_back_gets_its_end()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        await using var web = await WebRun.StartAsync(h);
        // 400 KB over a few seconds (past the 256 KB the page once stopped at), a last line, then it runs on.
        const string command = "i=0; while [ $i -lt 40 ]; do head -c 10000 /dev/zero | tr '\\0' x; echo; i=$((i+1)); sleep 0.05; done; echo the last line; sleep 300";
        _gateway.Answer = _ => Reply.Call(("run_shell", new JsonObject { ["command"] = command, ["no_time_limit"] = true }.ToJsonString()));
        // The model is slow to answer after starting it: the turn runs meanwhile, and its stream carries the output.
        _gateway.Silent = FakeGateway.HasToolResults;
        using var stream = await web.SendAsync("build");
        var streamed = new StringBuilder();
        while (!streamed.ToString().Contains("the last line\n", StringComparison.Ordinal))
        {
            streamed.Append((await stream.UntilAsync("job_output").WaitAsync(TimeSpan.FromSeconds(30)))["text"]!.GetValue<string>());
        }
        Assert.Equal(400_000, streamed.ToString().Count(ch => ch == 'x'));

        // A page that comes back mid-turn (reloaded, another tab) is sent the end of it, not all of it.
        using var again = await web.StreamAsync(HttpMethod.Get, "/api/turn", null);
        var replayed = (await again.UntilAsync("job_output"))["text"]!.GetValue<string>();
        Assert.StartsWith("… (the start is not shown here: the agent reads the output with command_output)\n", replayed);
        Assert.EndsWith("the last line\n", replayed);
        Assert.InRange(replayed.Length, 64 * 1024, 64 * 1024 + 100);

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/jobs/stop", new JsonObject { ["id"] = 1 })).StatusCode);
        await stream.UntilAsync("job_end");
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/stop", new JsonObject())).StatusCode);
        await stream.RestAsync();
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A terminal's input: each line waits until the test types it.</summary>
internal sealed class TypedLines : TextReader
{
    private readonly System.Collections.Concurrent.BlockingCollection<string> _lines = [];

    public void Add(string line) => _lines.Add(line);

    public override string? ReadLine() => _lines.Take();
}
