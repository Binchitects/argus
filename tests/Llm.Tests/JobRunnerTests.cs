using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// A sandbox job while its runner stops answering or starts again (runner.py's heartbeat): the job is given up within
/// seconds, not after its whole time and the queue's (minutes the tool seems not to answer).
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
        var running = Client().RunAsync("print(1)", [], timeoutSeconds: 600, CancellationToken.None);
        var job = await QueuedAsync();
        // Taken (it runs), then the runner starts again: prepare() dropped what was in run/.
        Directory.Move(job, Path.Combine(_dir, "run", Path.GetFileName(job)));
        Directory.Delete(Path.Combine(_dir, "run", Path.GetFileName(job)), recursive: true);
        Beat(started: 2);
        var error = await Assert.ThrowsAsync<SandboxException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("started again while it ran this job", error.Message, StringComparison.Ordinal);
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
        // The new runner takes it and answers.
        var id = Path.GetFileName(job);
        var draft = Path.Combine(_dir, "out", ".tmp-" + id);
        Directory.CreateDirectory(Path.Combine(draft, "files"));
        await File.WriteAllTextAsync(Path.Combine(draft, "result.json"), """{"exit_code":0,"timed_out":false,"cancelled":false,"stdout":"1\n","stdout_cut":false,"stderr":"","stderr_cut":false,"killed":null,"error":null,"skipped_files":[],"duration_ms":5}""");
        Directory.Delete(job, recursive: true);
        Directory.Move(draft, Path.Combine(_dir, "out", id));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("1\n", result.Stdout);
    }
}
