using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// A sandbox job while its runner stops answering or starts again (runner.py's heartbeat): the job is given up within
/// seconds, not after its whole time and the queue's (minutes the tool seems not to answer); one a runner took after it
/// started again is not lost. One an admin's reset stops, or never runs, tells its caller so, and none is written while
/// a reset is asked.
/// </summary>
public sealed class JobRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("job-runner-").FullName;
    private readonly MovableClock _clock = new(DateTimeOffset.UtcNow);

    private sealed class Monitor(SandboxOptions value) : IOptionsMonitor<SandboxOptions>
    {
        public SandboxOptions CurrentValue => value;
        public SandboxOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SandboxOptions, string?> listener) => null;
    }

    public JobRunnerTests()
    {
        foreach (var d in new[] { "in", "run", "out", "cancel" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, d));
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Beat(double started) => File.WriteAllText(Path.Combine(_dir, "heartbeat"),
        new JsonObject { ["at"] = _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0, ["slots"] = 1, ["busy"] = 1, ["started"] = started }.ToJsonString());

    private SandboxClient Client() => new(new Monitor(new SandboxOptions { Dir = _dir }), _clock);

    /// <summary>The job the runner was handed, once it is there.</summary>
    private async Task<string> QueuedAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if (Directory.GetDirectories(Path.Combine(_dir, "in")).FirstOrDefault(d => !Path.GetFileName(d).StartsWith('.')) is { } job)
            {
                return job;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("no job was queued");
    }

    [Fact]
    public async Task A_runner_that_stops_answering_while_it_has_the_job_is_not_waited_for()
    {
        Beat(started: 1);
        var running = Client().RunAsync("print(1)", [], timeoutSeconds: 600, CancellationToken.None);
        var job = await QueuedAsync();
        Directory.Move(job, Path.Combine(_dir, "run", Path.GetFileName(job)));
        // Its heartbeat stays as it was: silent past 20 s, then past the limit.
        _clock.Now += TimeSpan.FromSeconds(25);
        await Task.Delay(1500);
        Assert.False(running.IsCompleted, "a moment of silence is not yet a stop");
        _clock.Now += JobRunner.SilentLimit + TimeSpan.FromSeconds(1);
        var error = await Assert.ThrowsAsync<SandboxException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("stopped answering while it had this job", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runner_that_starts_again_while_it_runs_the_job_has_lost_it_and_says_so()
    {
        Beat(started: 1);
        var taken = new TaskCompletionSource();
        var running = Client().RunAsync("print(1)", [], timeoutSeconds: 600, CancellationToken.None, waiting: n =>
        {
            if (n is null)
            {
                taken.TrySetResult();
            }
            return Task.CompletedTask;
        });
        var job = await QueuedAsync();
        // Taken (it runs), and seen taken by the runner that started at 1 (no longer in line); then the runner starts
        // again: prepare() dropped what was in run/.
        Directory.Move(job, Path.Combine(_dir, "run", Path.GetFileName(job)));
        await taken.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.Delete(Path.Combine(_dir, "run", Path.GetFileName(job)), recursive: true);
        Beat(started: 2);
        var error = await Assert.ThrowsAsync<SandboxException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("started again while it ran this job", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_waits_in_line_while_its_sandbox_answers_saying_how_many_are_ahead_and_its_time_counts_from_its_start()
    {
        Beat(started: 1);
        // Two jobs before it, in line (their ids sort first, as their time does).
        foreach (var early in new[] { "00000000000000000000000000000001", "00000000000000000000000000000002" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, "in", early));
        }
        var said = new List<int?>();
        var running = Client().RunAsync("print(1)", [], timeoutSeconds: 60, CancellationToken.None, waiting: n =>
        {
            lock (said)
            {
                said.Add(n);
            }
            return Task.CompletedTask;
        });
        var job = await QueuedAsync2();
        // Ten minutes in line, the sandbox answering all along: far past the job's own time, it still waits.
        for (var i = 0; i < 10; i++)
        {
            _clock.Now += TimeSpan.FromMinutes(1);
            Beat(started: 1);
            await Task.Delay(150);
        }
        await Task.Delay(1200);
        Assert.False(running.IsCompleted, "a job in line is not given up while its sandbox answers");
        Directory.Delete(Path.Combine(_dir, "in", "00000000000000000000000000000001"));
        await Task.Delay(1500);
        // Taken: from now on, its own time.
        Directory.Delete(Path.Combine(_dir, "in", "00000000000000000000000000000002"));
        Directory.Move(job, Path.Combine(_dir, "run", Path.GetFileName(job)));
        await Task.Delay(1500);
        _clock.Now += TimeSpan.FromSeconds(30);
        Beat(started: 1);
        await Task.Delay(1500);
        Assert.False(running.IsCompleted, "within its own time once it runs");
        _clock.Now += TimeSpan.FromSeconds(61);
        Beat(started: 1);
        await Assert.ThrowsAsync<SandboxException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (said)
        {
            Assert.Equal([2, 1, null], said);
        }
    }

    /// <summary>This test's own job: the one whose id is not one of those put in line before it.</summary>
    private async Task<string> QueuedAsync2()
    {
        for (var i = 0; i < 100; i++)
        {
            if (Directory.GetDirectories(Path.Combine(_dir, "in")).FirstOrDefault(d => !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).StartsWith("0000000000", StringComparison.Ordinal)) is { } job)
            {
                return job;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("no job was queued");
    }

    /// <summary>The runner's answer to a job an admin's reset stopped or never ran (runner.py's reset_result), then its new start.</summary>
    private async Task ResetAnswerAsync(string job, string reset, double started)
    {
        var id = Path.GetFileName(job);
        var draft = Path.Combine(_dir, "out", ".tmp-reset-" + id);
        Directory.CreateDirectory(Path.Combine(draft, "files"));
        await File.WriteAllTextAsync(Path.Combine(draft, "result.json"), $$"""{"exit_code":null,"timed_out":false,"cancelled":true,"stdout":"","stdout_cut":false,"stderr":"","stderr_cut":false,"files":[],"skipped_files":[],"reset":"{{reset}}","duration_ms":0}""");
        if (Directory.Exists(job))
        {
            Directory.Delete(job, recursive: true);
        }
        Directory.Move(draft, Path.Combine(_dir, "out", id));
        Beat(started);
    }

    [Fact]
    public async Task A_job_an_admins_reset_stops_or_never_runs_tells_its_caller_so()
    {
        Beat(started: 1);
        var running = Client().RunAsync("print(1)", [], timeoutSeconds: 600, CancellationToken.None);
        var job = await QueuedAsync();
        var taken = Path.Combine(_dir, "run", Path.GetFileName(job));
        Directory.Move(job, taken);
        var waiting = Client().RunAsync("print(2)", [], timeoutSeconds: 600, CancellationToken.None);
        var second = await QueuedAsync();

        // The admin asks: the sandbox says so to the tools meanwhile.
        await File.WriteAllTextAsync(Path.Combine(_dir, JobRunner.ResetFile), "gen=1\naction=reset\nby=ada\nat=1\n");
        Assert.Equal("The Python sandbox is being reset by an admin: try again in a minute.", Client().Unavailable());
        await Task.Delay(1500);
        Assert.False(running.IsCompleted, "nothing happens before the runner does it");

        // The runner answers both, then starts again: each caller is told why, not that its job was lost.
        await ResetAnswerAsync(second, "waiting", started: 1);
        await ResetAnswerAsync(taken, "running", started: 2);
        File.Delete(Path.Combine(_dir, JobRunner.ResetFile));
        var stopped = await Assert.ThrowsAsync<SandboxException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("An admin reset the Python sandbox while this job was running: run it again.", stopped.Message);
        var told = await Assert.ThrowsAsync<SandboxException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("An admin reset the Python sandbox while this job waited in line: run it again.", told.Message);
        Assert.Null(Client().Unavailable());
        // Its slots and jobs, as its heartbeat says (one slot, busy), none waiting.
        Assert.Equal((1, 1, 0), Client().Load());
    }

    [Fact]
    public async Task A_runner_that_starts_again_before_taking_the_job_runs_it_still()
    {
        Beat(started: 1);
        var running = Client().RunAsync("print(1)", [], timeoutSeconds: 600, CancellationToken.None);
        var job = await QueuedAsync();
        Beat(started: 2);
        await Task.Delay(2500);
        Assert.False(running.IsCompleted, "the job waits in in/ for the new runner");
        // The new runner takes it: the runner that runs it is the one that took it, so it is not lost.
        var id = Path.GetFileName(job);
        Directory.Move(job, Path.Combine(_dir, "run", id));
        job = Path.Combine(_dir, "run", id);
        await Task.Delay(1500);
        Assert.False(running.IsCompleted, "a job the new runner took is not lost with the one before it");
        // It answers.
        var draft = Path.Combine(_dir, "out", ".tmp-" + id);
        Directory.CreateDirectory(Path.Combine(draft, "files"));
        await File.WriteAllTextAsync(Path.Combine(draft, "result.json"), """{"exit_code":0,"timed_out":false,"cancelled":false,"stdout":"1\n","stdout_cut":false,"stderr":"","stderr_cut":false,"killed":null,"error":null,"skipped_files":[],"duration_ms":5}""");
        Directory.Delete(job, recursive: true);
        Directory.Move(draft, Path.Combine(_dir, "out", id));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("1\n", result.Stdout);
    }

    [Fact]
    public async Task No_job_is_written_while_an_admins_reset_is_asked()
    {
        // The runner gives back what waits when it resets: a job written meanwhile would only be given back too.
        Beat(started: 1);
        await File.WriteAllTextAsync(Path.Combine(_dir, JobRunner.ResetFile), "gen=1\naction=reset\nby=ada\nat=1\n");
        var refused = await Assert.ThrowsAsync<SandboxException>(() =>
            Client().RunAsync("print(1)", [new SandboxFile("data.csv", [1, 2])], timeoutSeconds: 60, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("The Python sandbox is being reset by an admin: try again in a minute.", refused.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dir, "in")));
    }
}
