using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Commands with no time limit: run in the background, watched to their end, stopped only by the person.</summary>
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
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("Waiting for the server test.")
            : Reply.Call(("run_shell", """{"command":"echo up; sleep 300","no_time_limit":true}"""));
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = TextWriter.Synchronized(output), Err = TextWriter.Synchronized(output), Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        var ui = new Ui(env.In, env.Out, env.Err, false, false);
        await using var rt = await Runtime.StartAsync(new Options(), env, ui, CancellationToken.None);
        using var key = env.Cancel.BeginTurn();
        var turn = rt.Agent.RunAsync("run the long test", new Spend(), key.Token);
        await Until(() => rt.Jobs.All.Count == 1 && rt.Jobs.All[0].Length > 0 && _gateway.Requests.Count == 2, "the job ran and the turn waits for it");
        await Task.Delay(300);
        Assert.False(turn.IsCompleted);

        Assert.True(env.Cancel.Press());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(10)));
        var job = rt.Jobs.All[0];
        await job.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("stopped (by the person) after", job.Status());
        env.Cancel.EndTurn();

        // The next turn is not told of it again: the person stopped it themselves.
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal("ok", await rt.Agent.RunAsync("next", new Spend(), CancellationToken.None));
        Assert.DoesNotContain("has ended", FakeGateway.Last(_gateway.Requests[^1]));
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
    public async Task The_IDE_streams_a_jobs_output_and_its_Stop_ends_it()
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
        var output = await stream.UntilAsync("job_output");
        Assert.Equal("first line\n", output["text"]!.GetValue<string>());
        var state = await web.GetJsonAsync("/api/state");
        Assert.True(state["jobs"]![0]!["running"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/jobs/stop", new JsonObject { ["id"] = 1 })).StatusCode);
        var end = await stream.UntilAsync("job_end");
        Assert.True(end["stopped"]!.GetValue<bool>());
        Assert.StartsWith("stopped (by the person, in the IDE) after", end["status"]!.GetValue<string>());
        var events = await stream.RestAsync();
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "tool_call" && e["name"]!.GetValue<string>() == "command_output");
        Assert.Contains("Told: [Code Arena: job 1 has ended.", string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "content").Select(e => e["text"]!.GetValue<string>())));
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/jobs/stop", new JsonObject { ["id"] = 9 })).StatusCode);
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
