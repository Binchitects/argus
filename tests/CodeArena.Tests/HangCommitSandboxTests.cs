using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// Reproductions for "the arena code stucks in middle of work and second promt does not perform", hypothesis
/// commit-sandbox: the end of a turn runs git (TurnCommits.CommitAsync: status, add, commit with the repository's hooks
/// and signing, rev-parse) outside the sandbox, each call allowed two minutes and none of them stoppable (Agent passes
/// CancellationToken.None). Every wait here has a deadline, so a stall fails the test instead of hanging it.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class HangCommitSandboxTests(ITestOutputHelper log) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly List<string> _pidFiles = [];
    private readonly List<string> _dirs = [];

    /// <summary>
    /// The commit is made at once; what follows is the wait. post-commit: a hook that starts something in the background
    /// (a ctags or a notification run, nohup ... &amp;) leaves it holding git's stderr. pre-commit: a hook that runs the test
    /// suite, or waits for an answer. sign: commit.gpgsign with a signing program waiting for a passphrase.
    /// </summary>
    [Theory]
    [InlineData("post-commit-background-child")]
    [InlineData("pre-commit-long")]
    [InlineData("signing-waits-for-passphrase")]
    public async Task A_turns_commit_returns_within_30s_whatever_the_repositorys_hooks_do_or_when_the_person_skips_it(string kind)
    {
        var repo = NewRepo();
        var pid = Path.Combine(repo, ".git", "hang.pid");
        _pidFiles.Add(pid);
        switch (kind)
        {
            case "post-commit-background-child":
                Hook(repo, "post-commit", $"sleep 600 &\necho $! > '{pid}'");
                break;
            case "pre-commit-long":
                Hook(repo, "pre-commit", $"echo $$ > '{pid}'\nexec sleep 600");
                break;
            case "signing-waits-for-passphrase":
                var gpg = Path.Combine(repo, ".git", "fake-gpg");
                File.WriteAllText(gpg, $"#!/bin/sh\necho $$ > '{pid}'\nexec sleep 600\n");
                File.SetUnixFileMode(gpg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Git(repo, "config", "commit.gpgsign", "true");
                Git(repo, "config", "gpg.program", gpg);
                break;
        }

        var before = await TurnCommits.TakeAsync(repo, CancellationToken.None);
        Assert.NotNull(before);
        File.WriteAllText(Path.Combine(repo, "b.txt"), "written by the turn\n");

        var clock = Stopwatch.StartNew();
        // A hook that runs the test suite, or a signing program asking for a passphrase, may take minutes: the turn says
        // "Committing… Ctrl+C or Stop skips it", and the person skips it (here after 3 s). One that left a child is not waited for.
        using var skip = new CancellationTokenSource();
        if (kind != "post-commit-background-child")
        {
            skip.CancelAfter(TimeSpan.FromSeconds(3));
        }
        var commit = TurnCommits.CommitAsync(before, "add b.txt", CommitIdentity.Default, "model-a", "s1", skip.Token);
        var inTime = await Task.WhenAny(commit, Task.Delay(Deadline)) == commit;
        var waited = clock.Elapsed;
        var head = Git(repo, "log", "-1", "--format=%an|%s").Trim();

        // Unblock it (as a hook ending, or the two-minute limit, would) and see how it ends.
        KillPidFile(pid);
        var ended = await Task.WhenAny(commit, Task.Delay(TimeSpan.FromSeconds(20))) == commit;
        var outcome = !ended ? "still running 20 s after the hook's process was killed" : await OutcomeAsync(commit);
        var report = $"[{kind}] CommitAsync {(inTime ? "returned" : "had NOT returned")} after {waited.TotalSeconds:0.0} s; " +
                     $"HEAD at that moment: '{head}'; after the hook's process was killed at {clock.Elapsed.TotalSeconds:0.0} s: {outcome}.";
        log.WriteLine(report);
        Assert.True(inTime, report);
        // Skipped, git was told to stop as an interrupted git is: it left no lock to break the next commit, or the person's git.
        Assert.False(File.Exists(Path.Combine(repo, ".git", "index.lock")), "git left .git/index.lock behind: " + report);
    }

    /// <summary>
    /// The terminal: two prompts typed one after the other. A post-commit hook left a child running, so the first turn's
    /// end waits on git's stderr until the two-minute limit, and then (ReadToEndAsync cancelled, outside the catch that
    /// handles the limit) an OperationCanceledException that no one catches leaves the chat: the second prompt is never sent.
    /// </summary>
    [Fact]
    public async Task In_the_terminal_the_second_prompt_runs_after_a_turn_whose_post_commit_hook_left_a_child()
    {
        using var h = new Harness(_gateway, _mcp);
        InitRepo(h.Work);
        var pid = Path.Combine(h.Work, ".git", "hang.pid");
        _pidFiles.Add(pid);
        Hook(h.Work, "post-commit", $"sleep 600 &\necho $! > '{pid}'");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "say hi" => Reply.Say("hi"),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("written"),
            _ => Reply.Call(("write_file", """{"path":"b.txt","content":"written by the turn\n"}""")),
        };

        var clock = Stopwatch.StartNew();
        var run = h.Run("add b.txt\nsay hi\n/exit\n", "chat", "--mode", "auto-edit");
        // First: does the chat get past the first turn's end in time?
        var inTime = await Task.WhenAny(run, Task.Delay(Deadline)) == run;
        var firstLook = clock.Elapsed;
        var askedHiByThen = _gateway.Requests.Any(r => FakeGateway.Last(r) == "say hi");
        // Then: what becomes of it after the two-minute limit (up to 3 minutes in all).
        if (!inTime)
        {
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(150)));
        }
        string outcome;
        try
        {
            outcome = run.IsCompleted ? $"exit code {await run}" : "still running after 3 minutes";
        }
        catch (Exception e)
        {
            var frames = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at CodeArena.", StringComparison.Ordinal)).Take(4);
            outcome = $"the chat died with {e.GetType().Name}: {e.Message} [{string.Join(" | ", frames)}]";
        }
        var askedHi = _gateway.Requests.Any(r => FakeGateway.Last(r) == "say hi");
        var report = $"At {firstLook.TotalSeconds:0.0} s: chat {(inTime ? "done" : "still in the first turn's end")}, second prompt sent: {askedHiByThen}. " +
                     $"At {clock.Elapsed.TotalSeconds:0.0} s: {outcome}; second prompt ever sent: {askedHi}; " +
                     $"HEAD: '{Git(h.Work, "log", "-1", "--format=%an|%s").Trim()}'.";
        log.WriteLine(report);
        Assert.True(inTime && askedHi && run.IsCompletedSuccessfully, report);
    }

    /// <summary>
    /// The IDE: the answer is written, but the turn's commit waits on a pre-commit hook (a test suite, a prompt). The job
    /// stays busy, so the next message is refused (409), and Stop cannot end it: the commit runs with CancellationToken.None.
    /// </summary>
    [Fact]
    public async Task In_the_IDE_Stop_ends_a_turn_whose_commit_waits_on_a_pre_commit_hook_and_the_next_message_is_taken()
    {
        using var h = new Harness(_gateway, _mcp);
        InitRepo(h.Work);
        var pid = Path.Combine(h.Work, ".git", "hang.pid");
        _pidFiles.Add(pid);
        // Slow the first time only: the commit that waits is the first turn's (the next turn's goes through).
        var once = Path.Combine(h.Work, ".git", "hook-ran");
        Hook(h.Work, "pre-commit", $"[ -e '{once}' ] && exit 0\ntouch '{once}'\necho $$ > '{pid}'\nexec sleep 600");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "say hi" => Reply.Say("hi"),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("written"),
            _ => Reply.Call(("write_file", """{"path":"b.txt","content":"written by the turn\n"}""")),
        };
        await using var web = await WebRun.StartAsync(h, "--mode", "auto-edit");

        using var stream = await web.SendAsync("add b.txt");
        // The answer streams in pieces: read it whole.
        var answer = "";
        while (answer != "written")
        {
            answer += (await stream.UntilAsync("content"))["text"]!.GetValue<string>();
        }
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(3));
        var busyAfterAnswer = (await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>();
        var second = (await web.PostAsync("/api/messages", new JsonObject { ["text"] = "say hi" })).StatusCode;
        var stop = (await web.PostAsync("/api/stop", new JsonObject())).StatusCode;
        var done = Task.Run(() => stream.UntilAsync("done"));
        var endedAfterStop = await Task.WhenAny(done, Task.Delay(Deadline)) == done && done.IsCompletedSuccessfully;
        var busyAfterStop = (await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>();
        var secondAfterStop = (await web.PostAsync("/api/messages", new JsonObject { ["text"] = "say hi" })).StatusCode;
        var report = $"answer written; 3 s later busy={busyAfterAnswer}, a second message got {(int)second} {second}; Stop got {(int)stop}; " +
                     $"{Deadline.TotalSeconds:0} s after Stop the turn {(endedAfterStop ? "had ended" : "had NOT ended")}, busy={busyAfterStop}, " +
                     $"a second message got {(int)secondAfterStop} {secondAfterStop} ({clock.Elapsed.TotalSeconds:0.0} s since the answer); " +
                     $"events: {string.Join(", ", stream.Seen.Select(e => e["type"]))}.";
        log.WriteLine(report);

        // Let it end (the hook gone, git commit fails) so the server stops cleanly.
        KillPidFile(pid);
        await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(endedAfterStop && !busyAfterStop && secondAfterStop != HttpStatusCode.Conflict, report);
    }

    /// <summary>
    /// How long a turn's start and end take on a large working tree: tracked files, and files the person has not
    /// committed nor ignored (a build folder, a data dump) which TurnCommits hashes in full at every turn's start and end.
    /// </summary>
    [Fact]
    public async Task On_a_large_working_tree_a_turns_start_and_end_take_seconds()
    {
        var repo = NewRepo();
        var clock = Stopwatch.StartNew();
        Spread(repo, "src", 20_000, "tracked source line\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "twenty thousand files");
        // The person's own, uncommitted and not ignored.
        Spread(repo, "build", 30_000, new string('x', 2048));
        Directory.CreateDirectory(Path.Combine(repo, "data"));
        using (var dump = File.Create(Path.Combine(repo, "data", "dump.bin")))
        {
            var block = new byte[1 << 20];
            Random.Shared.NextBytes(block);
            for (var i = 0; i < 400; i++)
            {
                dump.Write(block);
            }
        }
        var setup = clock.Elapsed;

        clock.Restart();
        var before = await TurnCommits.TakeAsync(repo, CancellationToken.None);
        var start = clock.Elapsed;
        Assert.NotNull(before);
        Assert.Equal(30_001, before.Dirty.Count);

        // The turn: 2,000 new files and 50 edits.
        Spread(repo, "gen", 2_000, "generated\n");
        for (var i = 0; i < 50; i++)
        {
            File.AppendAllText(Path.Combine(repo, "src", $"d{i % 100:000}", $"f{i:00000}.txt"), "edited\n");
        }
        clock.Restart();
        var commit = TurnCommits.CommitAsync(before, "generate", CommitIdentity.Default, "model-a", "s1", CancellationToken.None);
        var inTime = await Task.WhenAny(commit, Task.Delay(TimeSpan.FromSeconds(120))) == commit;
        var end = clock.Elapsed;

        // A turn that changes nothing still pays both.
        clock.Restart();
        var idleBefore = await TurnCommits.TakeAsync(repo, CancellationToken.None);
        var idle = await TurnCommits.CommitAsync(idleBefore!, "look only", CommitIdentity.Default, "model-a", "s1", CancellationToken.None);
        var idleBoth = clock.Elapsed;

        var report = $"setup {setup.TotalSeconds:0.0} s; turn start (status + hash of 30,001 untracked incl. a 400 MB file) {start.TotalSeconds:0.0} s; " +
                     $"turn end (2,050 files) {(inTime ? $"{end.TotalSeconds:0.0} s: {(await commit)?.Describe()}" : "NOT done in 120 s")}; " +
                     $"an idle turn's start+end {idleBoth.TotalSeconds:0.0} s (commit: {idle?.Describe() ?? "none"}).";
        log.WriteLine(report);
        Assert.True(inTime && start < Deadline && end < Deadline && idleBoth < Deadline, report);
    }

    /// <summary>A turn that adds very many files (an npm install with node_modules not ignored): git add's argument list.</summary>
    [Fact]
    public async Task A_turn_that_adds_sixty_thousand_files_is_committed_or_told_the_real_reason()
    {
        var repo = NewRepo();
        var before = await TurnCommits.TakeAsync(repo, CancellationToken.None);
        Spread(repo, Path.Combine("node_modules", "some-package-with-a-long-name", "lib"), 60_000, "module.exports = 1;\n");
        var clock = Stopwatch.StartNew();
        var commit = TurnCommits.CommitAsync(before!, "npm install", CommitIdentity.Default, "model-a", "s1", CancellationToken.None);
        var inTime = await Task.WhenAny(commit, Task.Delay(TimeSpan.FromSeconds(120))) == commit;
        var report = inTime ? $"{clock.Elapsed.TotalSeconds:0.0} s: {await OutcomeAsync(commit)}" : "NOT done in 120 s";
        log.WriteLine(report);
        Assert.True(inTime, report);
        Assert.DoesNotContain("git is not installed", report);
    }

    private static async Task<string> OutcomeAsync(Task<TurnCommit?> commit)
    {
        try
        {
            return await commit is { } r ? (r.Hash is not null ? $"committed {r.Hash}: {r.Describe()}" : $"not committed: {r.Describe()}") : "nothing to commit";
        }
        catch (Exception e)
        {
            return $"threw {e.GetType().Name}: {e.Message}";
        }
    }

    private static void Spread(string repo, string folder, int count, string text)
    {
        for (var i = 0; i < count; i++)
        {
            var dir = Path.Combine(repo, folder, $"d{i % 100:000}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"f{i:00000}.txt"), text);
        }
    }

    private string NewRepo()
    {
        var dir = Directory.CreateTempSubdirectory("hang-commit-").FullName;
        _dirs.Add(dir);
        InitRepo(dir);
        return dir;
    }

    private static void InitRepo(string dir)
    {
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.name", "Pat Person");
        Git(dir, "config", "user.email", "pat@example.test");
        Git(dir, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "old\n");
        Git(dir, "add", ".");
        Git(dir, "commit", "-q", "-m", "first");
    }

    private static void Hook(string repo, string name, string body)
    {
        var path = Path.Combine(repo, ".git", "hooks", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void KillPidFile(string file)
    {
        if (!File.Exists(file) || !int.TryParse(File.ReadAllText(file).Trim(), out var pid))
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var git = Process.Start(psi)!;
        var output = git.StandardOutput.ReadToEndAsync();
        var error = git.StandardError.ReadToEndAsync();
        Assert.True(git.WaitForExit(120_000), $"git {string.Join(' ', args)} did not end");
        // Not waiting for the pipes: a hook's child may hold them.
        return (output.Wait(2000) ? output.Result : "") + (error.Wait(2000) ? error.Result : "");
    }

    public void Dispose()
    {
        foreach (var pid in _pidFiles)
        {
            KillPidFile(pid);
        }
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
