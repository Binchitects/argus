using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Server;
using Argus.Store;
using Argus.Util;

namespace Argus.Tests;

/// <summary>
/// A GitLab on a real socket: its API answers each token with its own listing, and the
/// repositories are served over git's dumb HTTP protocol, so the indexer's clone path runs for real.
/// </summary>
sealed class SocketGitLab : IDisposable
{
    readonly HttpListener _listener = new();
    readonly string _repos;
    public string Url { get; }
    /// <summary>Per token, the projects it lists: id, path, created_at.</summary>
    public Dictionary<string, List<(long Id, string Path, string Created)>> Listings { get; } = new(StringComparer.Ordinal);

    public SocketGitLab(string repos)
    {
        _repos = repos;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            try { Answer(ctx); }
            catch (Exception) { ctx.Response.StatusCode = 500; }
            finally { ctx.Response.Close(); }
        }
    }

    void Write(HttpListenerContext ctx, int status, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
    }

    void Answer(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path.StartsWith("/repos/", StringComparison.Ordinal))
        {
            var file = Path.GetFullPath(Path.Combine(_repos, Uri.UnescapeDataString(path["/repos/".Length..])));
            if (!file.StartsWith(_repos, StringComparison.Ordinal) || !File.Exists(file)) { Write(ctx, 404, "not found"u8.ToArray()); return; }
            Write(ctx, 200, File.ReadAllBytes(file));
            return;
        }
        var token = ctx.Request.Headers["PRIVATE-TOKEN"] ?? "";
        if (!Listings.TryGetValue(token, out var projects)) { Write(ctx, 401, """{"message":"401 Unauthorized"}"""u8.ToArray()); return; }
        JsonNode body = path switch
        {
            "/api/v4/user" => new JsonObject { ["id"] = 1, ["username"] = "svc", ["is_admin"] = true },
            "/api/v4/projects" when ctx.Request.QueryString["page"] is "1" => new JsonArray([.. projects.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id, ["path_with_namespace"] = p.Path, ["default_branch"] = "main", ["created_at"] = p.Created,
                ["http_url_to_repo"] = $"https://gitlab.advertised.invalid/repos/{p.Path}.git",
            })]),
            _ => new JsonArray(),
        };
        Write(ctx, 200, Encoding.UTF8.GetBytes(body.ToJsonString()));
    }

    public void Dispose() => _listener.Close();
}

/// <summary>Index runs as they happen: the queue, each repository's progress, its log, and a whole run through a GitLab twice.</summary>
[Collection("process-state")]
public class RunTests
{
    static string Line(JsonObject step) => Progress.Prefix + step.ToJsonString();

    [Fact]
    public void Repositories_asked_for_while_a_run_goes_wait_then_run_together_each_with_who_asked()
    {
        using var ix = new TestIndex();
        var jobs = new Jobs(ix.Config());
        var runs = new List<IReadOnlyList<string>>();
        using var first = new ManualResetEventSlim();
        jobs.Runner = (argv, _) =>
        {
            lock (runs) runs.Add(argv);
            if (runs.Count == 1) first.Wait(TimeSpan.FromSeconds(20));
            return 0;
        };
        Assert.Equal("started", jobs.EnqueueRepos(["g/a"], "manual")["g/a"]);
        Assert.Equal("queued", jobs.EnqueueWebhook("g/b")["status"]!.GetValue<string>());
        var again = jobs.EnqueueRepos(["g/b", "g/c"], "repo-schedule");
        Assert.Equal("already_queued", again["g/b"]);
        Assert.Equal("queued", again["g/c"]);
        Assert.Equal(["g/b", "g/c"], jobs.IndexJobSnapshot()["pending"]!.AsArray().Select(n => n!.GetValue<string>()));

        first.Set();
        Assert.True(SpinWait.SpinUntil(() => { lock (runs) return runs.Count == 2; }, TimeSpan.FromSeconds(10)));
        // One run for everything that waited, each repository's log saying who asked for it.
        var argv = runs[1];
        Assert.Equal(["--repo", "g/b", "--repo", "g/c", "--trigger", "webhook", "--trigger", "repo-schedule"],
            argv.SkipWhile(a => a != "--repo").Take(8));
        Assert.True(SpinWait.SpinUntil(() => jobs.IndexJobSnapshot()["state"]?.ToString() == "idle", TimeSpan.FromSeconds(10)));
        Assert.Empty(jobs.IndexJobSnapshot()["pending"]!.AsArray());
    }

    [Fact]
    public void Each_repository_shows_where_it_is_while_the_run_goes_and_how_it_ended()
    {
        using var ix = new TestIndex();
        var jobs = new Jobs(ix.Config());
        using var midway = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        jobs.Runner = (_, say) =>
        {
            say(Line(new JsonObject { ["stage"] = "pass", ["repos"] = 3, ["names"] = new JsonArray("g/a", "g/b", "g/c") }));
            say(Line(new JsonObject { ["stage"] = "fetching", ["position"] = 1, ["repo"] = "g/a" }));
            say(Line(new JsonObject { ["stage"] = "branch", ["position"] = 1, ["repo"] = "g/a", ["branch"] = "main" }));
            say(Line(new JsonObject { ["stage"] = "files", ["repo"] = "g/a", ["branch"] = "main", ["done"] = 10, ["total"] = 10 }));
            say(Line(new JsonObject { ["stage"] = "symbols", ["repo"] = "g/a", ["branch"] = "main", ["total"] = 9 }));
            say(Line(new JsonObject { ["stage"] = "embedding", ["repo"] = "g/a", ["done"] = 64, ["total"] = 200 }));
            say(Line(new JsonObject { ["stage"] = "repo_done", ["repo"] = "g/a", ["outcome"] = "ok", ["message"] = "Done in 4.0 s: 1 branch, 1 updated." }));
            say(Line(new JsonObject { ["stage"] = "fetching", ["position"] = 2, ["repo"] = "g/b" }));
            say(Line(new JsonObject { ["stage"] = "repo_done", ["repo"] = "g/b", ["outcome"] = "failed", ["message"] = "Could not fetch it from GitLab: 403." }));
            say(Line(new JsonObject { ["stage"] = "fetching", ["position"] = 3, ["repo"] = "g/c" }));
            say(Line(new JsonObject { ["stage"] = "branch", ["position"] = 3, ["repo"] = "g/c", ["branch"] = "main" }));
            say(Line(new JsonObject { ["stage"] = "files", ["repo"] = "g/c", ["branch"] = "main", ["done"] = 30, ["total"] = 120 }));
            say("g/a: indexed=10 deleted=0 skipped=1 errors=0 (4.0s)");
            midway.Set();
            go.Wait(TimeSpan.FromSeconds(20));
            return 1;
        };
        Assert.True(jobs.StartIndex([], false, "manual"));
        Assert.True(midway.Wait(TimeSpan.FromSeconds(10)));

        var job = jobs.IndexJobSnapshot();
        var byRepo = job["progress"]!["by_repo"]!;
        Assert.Equal("done", byRepo["g/a"]!["state"]!.GetValue<string>());
        Assert.Equal("Done in 4.0 s: 1 branch, 1 updated.", byRepo["g/a"]!["message"]!.GetValue<string>());
        Assert.Equal("failed", byRepo["g/b"]!["state"]!.GetValue<string>());
        Assert.Equal("Could not fetch it from GitLab: 403.", byRepo["g/b"]!["message"]!.GetValue<string>());
        Assert.Equal("files", byRepo["g/c"]!["state"]!.GetValue<string>());
        Assert.Equal(30, byRepo["g/c"]!["done"]!.GetValue<int>());
        Assert.Equal(120, byRepo["g/c"]!["total"]!.GetValue<int>());
        // The whole pass, as before: the third of three, a quarter through its files.
        Assert.Equal(3, job["progress"]!["position"]!.GetValue<int>());
        Assert.Equal("g/c", job["progress"]!["repo"]!.GetValue<string>());
        // Progress stays out of the run log; the rest is in it.
        Assert.Equal(["g/a: indexed=10 deleted=0 skipped=1 errors=0 (4.0s)"], job["tail"]!.AsArray().Select(n => n!.GetValue<string>()));

        go.Set();
        Assert.True(SpinWait.SpinUntil(() => jobs.IndexJobSnapshot()["state"]?.ToString() == "idle", TimeSpan.FromSeconds(10)));
        // The run stopped before it finished g/c: that is said, not left as "indexing".
        var ended = jobs.IndexJobSnapshot()["progress"]!["by_repo"]!["g/c"]!;
        Assert.Equal("failed", ended["state"]!.GetValue<string>());
        Assert.Contains("exit 1", ended["message"]!.GetValue<string>());
    }

    [Fact]
    public void A_repositorys_log_keeps_its_last_runs_in_sentences()
    {
        using var ix = new TestIndex();
        double t = 1000;
        for (long run = 1; run <= RepoLog.KeepRuns + 3; run++)
        {
            var log = new RepoLog(ix.Conn, 7, run, () => t++);
            log.Say($"Run {run} started.");
            if (run % 2 == 0) log.Warn("2 files could not be read.");
            Assert.Equal(run % 2 == 0 ? RepoLog.Warning : RepoLog.Info, log.Worst);
            RepoLog.Trim(ix.Conn, 7);
        }
        var all = RepoLog.Read(ix.Conn, 7, RepoLog.KeepRuns);
        Assert.Equal(RepoLog.KeepRuns, all.Select(l => l.Run).Distinct().Count());
        Assert.Equal("Run 4 started.", all[0].Text);
        var last = RepoLog.Read(ix.Conn, 7, 1);
        Assert.Equal(["Run 23 started."], last.Select(l => l.Text));
        Assert.Empty(RepoLog.Read(ix.Conn, 8));

        Assert.Equal("0.4 s", RepoLog.Took(0.42));
        Assert.Equal("12 s", RepoLog.Took(12.3));
        Assert.Equal("3 min 5 s", RepoLog.Took(185));
        Assert.Equal("10 min", RepoLog.Took(600));
        Assert.Equal("1 h 2 min", RepoLog.Took(3725));
        Assert.Equal("1 file", RepoLog.Count(1, "file"));
        Assert.Equal("1,200 branches", RepoLog.Count(1200, "branch", "branches"));
    }

    [Fact]
    public void A_failure_in_the_morning_is_still_in_the_log_after_a_day_of_runs_that_found_nothing_new()
    {
        using var ix = new TestIndex();
        double t = 1000;
        // As Commands.Index writes a run: its lines, and whether it found nothing new.
        void Run(long run, bool nothingNew, params string[] lines)
        {
            var log = new RepoLog(ix.Conn, 7, run, () => t++);
            foreach (var line in lines)
            {
                if (line.StartsWith("!", StringComparison.Ordinal)) log.Fail(line[1..]);
                else log.Say(line);
            }
            if (nothingNew) log.Quiet();
            RepoLog.Trim(ix.Conn, 7);
        }
        // 09:00: a push, and its symbols could not be read.
        Run(1, false, "Run started by a push or merge in GitLab.", "!main: its symbols could not be read: ctags failed.");
        RepoLog.Note(ix.Conn, 7, "An admin set its schedule: every 6 hours.");
        // 09:15: read again, and fixed.
        Run(2, false, "Run started with the scheduled pass.", "main: at 1a2b3c4d, 14 files changed since 9f8e7d6c: 14 indexed (2.0 s).");
        // Then passes every 15 minutes for the rest of the day, with nothing new.
        for (long run = 3; run < 3 + 4 * 24; run++) Run(run, true, "Run started with the scheduled pass.", "main: up to date at 1a2b3c4d; nothing new to read.");

        var lines = RepoLog.Read(ix.Conn, 7, 5);
        // The failure and the run that fixed it stay; of the runs that found nothing new, the latest; and the admin's change.
        Assert.Equal([1L, 2L, 3 + 4 * 24 - 1], lines.Where(l => l.Text.StartsWith("Run started", StringComparison.Ordinal)).Select(l => l.Run));
        Assert.Contains(lines, l => l is { Level: RepoLog.Error, Text: "main: its symbols could not be read: ctags failed." });
        Assert.Contains(lines, l => l.Text == "An admin set its schedule: every 6 hours.");
        Assert.Equal(3 * 2 + 1, Convert.ToInt64(Sql.Scalar(ix.Conn, "SELECT COUNT(*) FROM index_log WHERE gitlab_id = 7")));

        // Admins' changes are kept apart: many of them push no run out, and only their last ones are kept.
        for (int i = 0; i < RepoLog.KeepNotes + 5; i++) RepoLog.Note(ix.Conn, 7, $"An admin added branches to index: b{i}.");
        RepoLog.Trim(ix.Conn, 7);
        var all = RepoLog.Read(ix.Conn, 7, RepoLog.KeepRuns);
        Assert.Equal(3, all.Count(l => l.Text.StartsWith("Run started", StringComparison.Ordinal)));
        Assert.Equal(RepoLog.KeepNotes, all.Count(l => l.Text.StartsWith("An admin", StringComparison.Ordinal)));
        Assert.Equal($"An admin added branches to index: b{RepoLog.KeepNotes + 4}.", all[^1].Text);
        // Asked for its last 2: two runs (two lines each) and two changes.
        Assert.Equal(6, RepoLog.Read(ix.Conn, 7, 2).Count);
    }

    static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "t";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "t@example.invalid";
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }

    /// <summary>A repository committed and published bare, for GitLab's dumb HTTP.</summary>
    static void Publish(TempDir dir, string path, Dictionary<string, string> files)
    {
        var tree = Path.Combine(dir.Path, "trees-src", path);
        foreach (var (name, text) in files) dir.File(Path.Combine("trees-src", path, name), text);
        Git(tree, "init", "-q", "-b", "main");
        Git(tree, "add", "-A");
        Git(tree, "commit", "-q", "-m", "initial");
        var bare = Path.Combine(dir.Path, "repos", path + ".git");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        Git(dir.Path, "clone", "-q", "--bare", tree, bare);
        Git(bare, "update-server-info");
    }

    [Fact]
    public void Indexed_through_one_token_then_another_on_a_gitlab_set_up_anew_every_repository_stays_one_with_a_log_people_read()
    {
        if (Ctags.Which("ctags") is null || Ctags.Which("git") is null || Cli.Commands.Preflight() is not null) return;
        using var dir = new TempDir();
        using var env = new EnvScope(("ARGUS_AUDIT_LOG", "0"), ("ARGUS_EMBED_PER_PASS", "100000"));
        Publish(dir, "root/eal-core", new()
        {
            ["src/decoder.h"] = "#pragma once\n/** Decode one frame of the stream. */\nint DecodeFrame(const char *in, int len);\n",
            ["src/decoder.c"] = "#include \"decoder.h\"\n\n/** Decode one frame of the stream. */\nint DecodeFrame(const char *in, int len) { return len; }\n",
            ["README.md"] = "# eal-core\n",
        });
        Publish(dir, "root/etl-decoder", new() { ["etl.py"] = "def decode_all(frames):\n    \"\"\"Decode every frame.\"\"\"\n    return frames\n" });
        using var gitlab = new SocketGitLab(Path.Combine(dir.Path, "repos"));
        gitlab.Listings["token-a"] = [(1, "root/eal-core", "2026-09-01T10:00:00.000Z"), (2, "root/etl-decoder", "2026-09-01T10:00:01.000Z")];
        var data = Path.Combine(dir.Path, "data");
        ArgusConfig Cfg(string token) => new()
        {
            GitLab = GitLabConfig.Create(gitlab.Url, token),
            Index = new IndexConfig { DataDir = data, DbPath = Path.Combine(data, "index.db") },
            PacksDirSetting = Path.Combine(dir.Path, "packs"),
        };
        var steps = new List<JsonObject>();
        Progress.Write = line => { lock (steps) steps.Add((JsonObject)JsonNode.Parse(line[Progress.Prefix.Length..])!); };
        Embed.Override = texts => [.. texts.Select(t => Enumerable.Range(0, Embed.Dim).Select(i => i == t.Length % Embed.Dim ? 1.0 : 0.01).ToArray())];
        try
        {
            Assert.Equal(0, Cli.Commands.Index(Cfg("token-a"), null, allowPartial: true, triggers: ["manual"]));
            long ealRow;
            using (var conn = Db.Open(Path.Combine(data, "index.db")))
            {
                ealRow = Sql.One(conn, "SELECT id FROM repos WHERE gitlab_id = 1")!.Long("id");
                Assert.NotEmpty(Queries.FindSymbol([ealRow], conn, "DecodeFrame"));
                var log = RepoLog.Read(conn, 1).Select(l => l.Text).ToList();
                Assert.Equal("Run started by an admin.", log[0]);
                Assert.StartsWith("Fetched from GitLab (", log[1]);
                Assert.EndsWith("): 1 branch to index.", log[1]);
                Assert.Matches(@"^main: at [0-9a-f]{8} \(""initial""\), read for the first time: 3 indexed \(\d", log[2]);
                Assert.Matches(@"^Embedded \d+ new symbols? for meaning search", log[3]);
                Assert.Matches(@"^Done in .+: 1 branch, 1 updated\.$", log[4]);
            }
            // Each repository went queued, fetching, files, symbols, embedding, done.
            var eal = steps.Where(s => s["repo"]?.ToString() == "root/eal-core").Select(s => s["stage"]!.ToString()).Distinct().ToList();
            Assert.Equal(["fetching", "branch", "files", "symbols", "branch_done", "embedding", "repo_done"], eal);
            Assert.Equal(["root/eal-core", "root/etl-decoder"], steps.First(s => s["stage"]!.ToString() == "pass")["names"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.True(Directory.Exists(Path.Combine(data, "mirrors", "1.git")));

            // Brought up again with another token, on the GitLab set up anew: the same repositories, new ids.
            gitlab.Listings["token-b"] = [(11, "root/eal-core", "2026-10-05T08:00:00.000Z"), (12, "root/etl-decoder", "2026-10-05T08:00:01.000Z")];
            steps.Clear();
            Assert.Equal(0, Cli.Commands.Index(Cfg("token-b"), null, allowPartial: true, triggers: ["schedule"]));
            using (var conn = Db.Open(Path.Combine(data, "index.db")))
            {
                Assert.Equal([11L, 12L], Choices.List(conn).Select(c => c.GitlabId).Order());
                Assert.Equal(2L, Convert.ToInt64(Sql.Scalar(conn, "SELECT COUNT(*) FROM repos")));
                Assert.Equal(11L, Sql.One(conn, "SELECT gitlab_id FROM repos WHERE id = ?", ealRow)!.Long("gitlab_id"));
                Assert.NotEmpty(Queries.FindSymbol([ealRow], conn, "DecodeFrame"));
                // Its log came with it, and says this run found nothing new.
                var runs = RepoLog.Read(conn, 11).GroupBy(l => l.Run).ToList();
                Assert.Equal(2, runs.Count);
                var second = runs[1].Select(l => l.Text).ToList();
                Assert.Equal("Run started with the scheduled pass.", second[0]);
                Assert.Matches(@"^main: up to date at [0-9a-f]{8} \(""initial""\); nothing new to read\.$", second[2]);
                Assert.Matches(@"^Done in .+: already up to date \(1 branch\)\.$", second[^1]);
            }
            // What the old ids left on disk is gone.
            Assert.False(Directory.Exists(Path.Combine(data, "mirrors", "1.git")));
            Assert.True(Directory.Exists(Path.Combine(data, "mirrors", "11.git")));

            // A scheduled pass leaves a repository that is off (or on its own schedule) to it.
            using (var conn = Db.Open(Path.Combine(data, "index.db"))) Choices.SetSchedule(conn, 11, "off", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            steps.Clear();
            Assert.Equal(0, Cli.Commands.Index(Cfg("token-b"), null, allowPartial: true, scheduled: true, triggers: ["schedule"]));
            Assert.Equal(["root/etl-decoder"], steps.First(s => s["stage"]!.ToString() == "pass")["names"]!.AsArray().Select(n => n!.GetValue<string>()));
            // So does `argus index --interval`: its passes are scheduled passes.
            steps.Clear();
            Assert.Equal(0, Cli.Commands.IndexRepeatedly(Cfg("token-b"), null, false, true, interval: 1, maxPasses: 1));
            Assert.Equal(["root/etl-decoder"], steps.First(s => s["stage"]!.ToString() == "pass")["names"]!.AsArray().Select(n => n!.GetValue<string>()));
            using (var conn = Db.Open(Path.Combine(data, "index.db")))
            {
                Assert.Equal(2, RepoLog.Read(conn, 11).Count(l => l.Text.StartsWith("Run started", StringComparison.Ordinal)));
                // Runs that found nothing new: the latest one is kept.
                Assert.Single(RepoLog.Read(conn, 12), l => l.Text == "Run started with the scheduled pass.");
            }
        }
        finally
        {
            Progress.Write = line => Console.Out.WriteLine(line);
            Embed.Override = null;
        }
    }
}
