using System.Diagnostics;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Packs;
using Argus.Store;
using Argus.Util;

namespace Argus.Server;

/// <summary>
/// Background work the server runs on the operator's behalf: one index run at
/// a time as a child `argus index` process, repositories queued behind it (pushes,
/// an admin's Update, their schedules) and run together when it ends, the pass
/// timer, the repositories' own schedules, and pack install/update/remove.
/// </summary>
public sealed class Jobs(ArgusConfig cfg)
{
    public const int DefaultIndexInterval = 0;
    public const int FirstPassGrace = 30;
    /// <summary>How often the repositories' own schedules are looked at, in seconds.</summary>
    public const int ScheduleTick = 30;
    /// <summary>Repositories that may wait behind a run; more are refused (each is run once, however often asked).</summary>
    public const int QueueLimit = 5000;

    readonly Lock _indexLock = new();
    readonly JsonObject _index = new()
    {
        ["state"] = "idle", ["branches"] = new JsonArray(), ["started"] = null, ["finished"] = null,
        ["returncode"] = null, ["tail"] = new JsonArray(), ["trigger"] = null, ["pending"] = new JsonArray(),
        ["progress"] = null, ["repos"] = null,
    };
    /// <summary>Who asked for each waiting repository: webhook, manual, repo-schedule.</summary>
    readonly Dictionary<string, string> _pendingTriggers = new(StringComparer.Ordinal);

    readonly Lock _packLock = new();
    readonly JsonObject _pack = new()
    {
        ["state"] = "idle", ["action"] = null, ["target"] = null, ["started"] = null,
        ["finished"] = null, ["returncode"] = null, ["tail"] = new JsonArray(),
    };

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static int IndexInterval() =>
        int.TryParse(Environment.GetEnvironmentVariable("ARGUS_INDEX_INTERVAL"), out var v) ? v : DefaultIndexInterval;

    /// <summary>ARGUS_INDEX_SCHEDULER=off: the repositories' own schedules are not run (tests, or an Argus that only serves).</summary>
    public static bool SchedulerEnabled() =>
        !string.Equals((Environment.GetEnvironmentVariable("ARGUS_INDEX_SCHEDULER") ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);

    public static string PackIndexUrl() => (Environment.GetEnvironmentVariable("ARGUS_PACK_INDEX_URL") ?? "").Trim();

    string CfgPath => cfg.SourcePath ?? Environment.GetEnvironmentVariable("ARGUS_CONFIG") ?? "/etc/argus/config.yaml";

    public JsonObject IndexJobSnapshot() { lock (_indexLock) return (JsonObject)_index.DeepClone(); }
    public JsonNode? IndexStarted() { lock (_indexLock) return _index["started"]?.DeepClone(); }

    /// <summary>When the pass timer (ARGUS_INDEX_INTERVAL) starts its next pass; null when it is off.</summary>
    public double? NextPassAt { get; private set; }

    static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)s).ToArray());

    /// <param name="repos">The repositories of the run; null: every one chosen (or, scheduled, every one that goes with the passes).</param>
    void MarkRunning(IEnumerable<string> branches, bool allowPartial, string trigger, IReadOnlyList<string>? repos)
    {
        _index["state"] = "running";
        _index["branches"] = Strings(branches);
        _index["allow_partial"] = allowPartial;
        _index["trigger"] = trigger;
        _index["repos"] = repos is null ? null : Strings(repos);
        _index["started"] = Now();
        _index["finished"] = null;
        _index["returncode"] = null;
        _index["tail"] = new JsonArray();
        var byRepo = new JsonObject();
        foreach (var r in repos ?? []) byRepo[r] = new JsonObject { ["state"] = "queued" };
        _index["progress"] = new JsonObject
        {
            ["repos"] = repos?.Count ?? 0, ["position"] = 0, ["stage"] = "starting", ["outcomes"] = new JsonObject(), ["by_repo"] = byRepo,
        };
    }

    bool Running => _index["state"]?.ToString() == "running";

    /// <summary>
    /// A step the index process reported (Indexing.Progress): the pass's size, the repository
    /// and branch it is on and how far, each branch's and repository's outcome.
    /// </summary>
    void OnProgress(string json)
    {
        JsonObject? step;
        try { step = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (step is not null && _index["progress"] is JsonObject p) Step(p, step, Now());
    }

    /// <summary>
    /// One reported step folded into the run's progress: the pass as a whole (repos, position,
    /// repo, branch, done, total, stage) and each repository in it (by_repo: its state, branch,
    /// done of total, when it started and finished, and how it ended).
    /// </summary>
    public static void Step(JsonObject p, JsonObject step, double now = 0)
    {
        var stage = step["stage"]?.ToString();
        var name = step["repo"]?.ToString();
        if (p["by_repo"] is not JsonObject byRepo) p["by_repo"] = byRepo = new JsonObject();
        JsonObject Repo()
        {
            if (byRepo[name!] is JsonObject r) return r;
            var made = new JsonObject { ["state"] = "queued" };
            byRepo[name!] = made;
            return made;
        }
        void Set(JsonObject r, string state, JsonNode? done = null, JsonNode? total = null)
        {
            r["state"] = state;
            r["done"] = done?.DeepClone();
            r["total"] = total?.DeepClone();
        }
        switch (stage)
        {
            case "pass":
                p["repos"] = step["repos"]?.DeepClone();
                foreach (var n in (step["names"] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>())
                    if (byRepo[n] is null) byRepo[n] = new JsonObject { ["state"] = "queued" };
                break;
            case "fetching" when name is not null:
                p["position"] = step["position"]?.DeepClone();
                p["repo"] = name;
                p["branch"] = null;
                p["done"] = 0;
                p["total"] = null;
                var started = Repo();
                Set(started, "fetching");
                started["branch"] = null;
                started["started"] = now;
                break;
            case "branch" when name is not null:
                p["position"] = step["position"]?.DeepClone();
                p["repo"] = name;
                p["branch"] = step["branch"]?.DeepClone();
                p["done"] = 0;
                p["total"] = null;
                var onBranch = Repo();
                Set(onBranch, "files", JsonValue.Create(0));
                onBranch["branch"] = step["branch"]?.DeepClone();
                onBranch["started"] ??= now;
                break;
            case "files" when name is not null:
                p["done"] = step["done"]?.DeepClone();
                p["total"] = step["total"]?.DeepClone();
                Set(Repo(), "files", step["done"], step["total"]);
                break;
            case "symbols" when name is not null:
                Set(Repo(), "symbols", null, step["total"]);
                break;
            case "branch_done" when name is not null:
                if (p["outcomes"] is JsonObject outcomes) outcomes[$"{name}@{step["branch"]}"] = step["outcome"]?.DeepClone();
                break;
            case "embedding" when name is not null:
                Set(Repo(), "embedding", step["done"], step["total"]);
                break;
            case "repo_done" when name is not null:
                var ended = Repo();
                var outcome = step["outcome"]?.ToString();
                Set(ended, outcome == "failed" ? "failed" : "done");
                ended["outcome"] = outcome;
                ended["message"] = step["message"]?.DeepClone();
                ended["finished"] = now;
                break;
            case "finishing":
                p["what"] = step["what"]?.DeepClone();
                break;
        }
        if (stage is "pass" or "fetching" or "branch" or "files" or "branch_done" or "finishing") p["stage"] = stage;
    }

    /// <param name="trigger">manual, schedule (the app's or the pass timer: only the repositories that go with the passes), webhook: shown with the run.</param>
    public bool StartIndex(IReadOnlyList<string> branches, bool allowPartial, string trigger)
    {
        lock (_indexLock)
        {
            if (Running) return false;
            MarkRunning(branches, allowPartial, trigger, null);
        }
        new Thread(() => RunIndex(branches, allowPartial, null, trigger == "schedule", trigger, null)) { IsBackground = true, Name = "argus-index" }.Start();
        return true;
    }

    bool IndexIsCurrent()
    {
        try
        {
            var snap = Metrics.Take(cfg.Index.DbPath);
            return snap.Repos.Count > 0 && snap.StaleRepos == 0;
        }
        catch (Exception) { return false; }
    }

    public void StartScheduler()
    {
        new Thread(() =>
        {
            int interval = IndexInterval();
            if (interval <= 0) return;
            bool fresh = IndexIsCurrent();
            int delay = fresh ? interval : FirstPassGrace;
            AuditLog.IndexScheduled(interval, delay, fresh ? null : "the index is not current");
            while (true)
            {
                NextPassAt = Now() + delay;
                Thread.Sleep(TimeSpan.FromSeconds(delay));
                delay = interval;
                if (!StartIndex([], false, "schedule"))
                    AuditLog.IndexScheduled(interval, skipped: "a run is already in progress");
            }
        }) { IsBackground = true, Name = "argus-scheduler" }.Start();
    }

    /// <summary>Runs each repository's own schedule (every N hours, daily, weekly): looked at every <see cref="ScheduleTick"/> seconds.</summary>
    public void StartRepoScheduler()
    {
        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(TimeSpan.FromSeconds(ScheduleTick));
                try { RunDue(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
                catch (Exception exc) { Console.Error.WriteLine($"the repositories' schedules could not be run: {exc.GetType().Name}: {exc.Message}"); }
            }
        }) { IsBackground = true, Name = "argus-repo-scheduler" }.Start();
    }

    /// <summary>
    /// Starts, or queues behind the run going, every repository whose schedule is due. One being
    /// indexed or waiting already is not asked for twice: its schedule counts from now.
    /// </summary>
    public IReadOnlyList<string> RunDue(long now)
    {
        List<RepoChoice> due;
        using (var conn = Db.Open(cfg.Index.DbPath))
        {
            due = Choices.Due(conn, now);
            if (due.Count == 0) return [];
            Choices.MarkScheduled(conn, [.. due.Select(c => c.GitlabId)], now);
        }
        var busy = Busy();
        var start = due.Select(c => c.Path).Where(p => !busy.Contains(p)).ToList();
        if (start.Count > 0) EnqueueRepos(start, "repo-schedule");
        return start;
    }

    /// <summary>The repositories being indexed now (the run's own, still to come) or waiting for their turn.</summary>
    HashSet<string> Busy()
    {
        lock (_indexLock)
        {
            var busy = (_index["pending"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            if (Running && (_index["progress"] as JsonObject)?["by_repo"] is JsonObject byRepo)
                foreach (var (name, state) in byRepo)
                    if (state?["state"]?.ToString() is not ("done" or "failed")) busy.Add(name);
            return busy;
        }
    }

    /// <summary>How to launch this same program as a child: the apphost, or `dotnet argus.dll`.</summary>
    public static List<string> SelfCommand()
    {
        var exe = Environment.ProcessPath ?? "argus";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return [exe, typeof(Jobs).Assembly.Location];
        return [exe];
    }

    /// <summary>The child's command line: a pass, or these repositories, each with who asked for it.</summary>
    public List<string> IndexCommand(IReadOnlyList<string> branches, bool allowPartial, IReadOnlyList<string>? only, bool scheduled, string trigger,
        IReadOnlyDictionary<string, string>? triggers)
    {
        var argv = SelfCommand();
        argv.AddRange(["index", "--config", CfgPath]);
        foreach (var b in branches) argv.AddRange(["--branch", b]);
        foreach (var r in only ?? []) argv.AddRange(["--repo", r]);
        if (only is { Count: > 0 } && triggers is not null)
            foreach (var r in only) argv.AddRange(["--trigger", triggers.GetValueOrDefault(r, trigger)]);
        else argv.AddRange(["--trigger", trigger]);
        if (scheduled) argv.Add("--scheduled");
        if (allowPartial) argv.Add("--allow-partial-enumeration");
        return argv;
    }

    /// <summary>Tests run the child here instead: its command line, and where its output lines go; the exit code back.</summary>
    public Func<IReadOnlyList<string>, Action<string?>, int>? Runner { get; set; }

    static int RunChild(IReadOnlyList<string> argv, Action<string?> onLine)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => onLine(e.Data);
        proc.ErrorDataReceived += (_, e) => onLine(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        return proc.ExitCode;
    }

    void RunIndex(IReadOnlyList<string> branches, bool allowPartial, IReadOnlyList<string>? only, bool scheduled, string trigger,
        IReadOnlyDictionary<string, string>? triggers)
    {
        var argv = IndexCommand(branches, allowPartial, only, scheduled, trigger, triggers);
        int rc;
        try
        {
            var tail = new List<string>();
            void OnLine(string? line)
            {
                if (line is null) return;
                var clean = PyStr.RStrip(line);
                if (clean.StartsWith(Indexing.Progress.Prefix, StringComparison.Ordinal))
                {
                    lock (_indexLock) OnProgress(clean[Indexing.Progress.Prefix.Length..]);
                    return;
                }
                lock (_indexLock)
                {
                    tail.Add(clean);
                    if (tail.Count > 200) tail.RemoveRange(0, tail.Count - 200);
                    _index["tail"] = Strings(tail);
                }
                Console.Out.WriteLine(clean);
                Console.Out.Flush();
            }
            rc = (Runner ?? RunChild)(argv, OnLine);
        }
        catch (Exception exc)
        {
            rc = -1;
            lock (_indexLock) _index["tail"] = Strings([$"failed to start: {exc.GetType().Name}({PyStr.Repr(exc.Message)})"]);
        }
        lock (_indexLock)
        {
            _index["state"] = "idle";
            _index["finished"] = Now();
            _index["returncode"] = rc;
            // A repository the run never reached (it stopped early): no longer "queued".
            if ((_index["progress"] as JsonObject)?["by_repo"] is JsonObject byRepo)
                foreach (var (_, state) in byRepo)
                    if (state is JsonObject s && s["state"]?.ToString() is not ("done" or "failed"))
                    {
                        s["state"] = "failed";
                        s["outcome"] = "failed";
                        s["message"] ??= rc == 0 ? "The run ended before it." : $"The run stopped before it (exit {rc}): the run log says why.";
                    }
        }
        DrainPending();
    }

    /// <summary>Everything that waited for the run that just ended, as one run.</summary>
    void DrainPending()
    {
        List<string> only;
        Dictionary<string, string> triggers;
        string trigger;
        lock (_indexLock)
        {
            if (Running) return;
            only = (_index["pending"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
            if (only.Count == 0) return;
            triggers = new Dictionary<string, string>(_pendingTriggers, StringComparer.Ordinal);
            _index["pending"] = new JsonArray();
            _pendingTriggers.Clear();
            trigger = triggers.Values.Distinct().Count() == 1 ? triggers.Values.First() : "queued";
            MarkRunning([], false, trigger, only);
        }
        AuditLog.IndexWebhook(only.Count == 1 ? only[0] : $"{only.Count} repositories", started: true);
        new Thread(() => RunIndex([], false, only, false, trigger, triggers)) { IsBackground = true, Name = "argus-index" }.Start();
    }

    /// <summary>A push arrived: start it, or queue it behind the running pass.</summary>
    public JsonObject EnqueueWebhook(string repo) => EnqueueRepo(repo, "webhook");

    /// <summary>One repository to bring up to date (a push, or an admin's Update): started now, or queued behind the running pass.</summary>
    public JsonObject EnqueueRepo(string repo, string trigger)
    {
        var outcome = EnqueueRepos([repo], trigger);
        var status = outcome[repo] ?? "queued";
        int queued;
        lock (_indexLock) queued = (_index["pending"] as JsonArray)?.Count ?? 0;
        return new JsonObject { ["status"] = status, ["repo"] = repo, ["queued"] = status == "started" ? 0 : queued };
    }

    /// <summary>
    /// Repositories to bring up to date: started now as one run, or queued behind the run going
    /// (all that wait run together when it ends). Each one's outcome: started, queued,
    /// already_queued, or refused (the queue is full).
    /// </summary>
    public Dictionary<string, string> EnqueueRepos(IReadOnlyList<string> repos, string trigger)
    {
        var outcome = new Dictionary<string, string>(StringComparer.Ordinal);
        var distinct = repos.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        lock (_indexLock)
        {
            if (Running)
            {
                var pending = (_index["pending"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
                foreach (var repo in distinct)
                {
                    if (pending.Contains(repo)) { outcome[repo] = "already_queued"; continue; }
                    if (pending.Count >= QueueLimit) { outcome[repo] = "refused"; continue; }
                    pending.Add(repo);
                    _pendingTriggers[repo] = trigger;
                    outcome[repo] = "queued";
                    AuditLog.IndexWebhook(repo, queued: pending.Count);
                }
                _index["pending"] = Strings(pending);
                return outcome;
            }
            if (distinct.Count == 0) return outcome;
            MarkRunning([], false, trigger, distinct);
        }
        foreach (var repo in distinct) outcome[repo] = "started";
        AuditLog.IndexWebhook(distinct.Count == 1 ? distinct[0] : $"{distinct.Count} repositories", started: true);
        new Thread(() => RunIndex([], false, distinct, false, trigger, null)) { IsBackground = true, Name = "argus-index" }.Start();
        return outcome;
    }

    // --- packs ----------------------------------------------------------------------------

    public JsonObject PackJobSnapshot() { lock (_packLock) return (JsonObject)_pack.DeepClone(); }
    public bool PackJobRunning() { lock (_packLock) return _pack["state"]?.ToString() == "running"; }

    /// <summary>The installed packs; "source" says whether one is loaded from the pack library (a link) or was installed (a copy).</summary>
    public static JsonArray PackRows(string packsDir)
    {
        var rows = Registry.ListInstalled(packsDir).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (JsonNode?)new JsonObject
        {
            ["name"] = p.Name, ["version"] = p.Version, ["model"] = p.EmbeddingModel, ["dim"] = p.EmbeddingDim,
            ["size_bytes"] = p.SizeBytes, ["license"] = p.License, ["commit"] = p.SourceCommit,
            ["compatible"] = p.Compatible, ["incompatible_reason"] = p.IncompatibleReason,
            ["source"] = Registry.LinkedFrom(p.Path) is null ? "installed" : "library",
            ["details"] = Details(p),
        });
        return new JsonArray(rows.ToArray());
    }

    /// <summary>The pack library's files, and which are loaded.</summary>
    public static JsonArray LibraryRows(string libraryDir, string packsDir) =>
        new([.. Registry.ListLibrary(libraryDir, packsDir).Select(l => (JsonNode?)new JsonObject
        {
            ["file"] = l.File, ["name"] = l.Pack.Name, ["version"] = l.Pack.Version, ["model"] = l.Pack.EmbeddingModel, ["dim"] = l.Pack.EmbeddingDim,
            ["size_bytes"] = l.Pack.SizeBytes, ["license"] = l.Pack.License, ["commit"] = l.Pack.SourceCommit,
            ["compatible"] = l.Pack.Compatible, ["incompatible_reason"] = l.Pack.IncompatibleReason, ["loaded"] = l.Loaded,
            ["details"] = Details(l.Pack),
        })]);

    /// <summary>What a pack says of itself beyond the columns above: its contents and where it came from.</summary>
    static JsonObject Details(InstalledPack p)
    {
        var meta = p.Meta ?? new Dictionary<string, string>();
        long? Count(string key) => long.TryParse(meta.GetValueOrDefault(key), out var n) ? n : null;
        string? Text(string key) => meta.GetValueOrDefault(key) is { Length: > 0 } v ? v : null;
        return new JsonObject
        {
            ["docs"] = Count("doc_count"), ["chunks"] = Count("chunk_count"), ["symbols"] = Count("symbol_count"),
            ["source_repo"] = Text("source_repo"), ["source_branch"] = Text("source_branch"), ["attribution"] = Text("attribution"),
            ["license_url"] = Text("license_url"), ["builder_version"] = Text("builder_version"),
        };
    }

    public bool StartPackJob(string action, string? source = null, string? sha256 = null, string? name = null, string? indexUrl = null)
    {
        lock (_packLock)
        {
            if (_pack["state"]?.ToString() == "running") return false;
            _pack["state"] = "running";
            _pack["action"] = action;
            _pack["target"] = !string.IsNullOrEmpty(source) ? source : name ?? "";
            _pack["started"] = Now();
            _pack["finished"] = null;
            _pack["returncode"] = null;
            _pack["tail"] = new JsonArray();
        }
        new Thread(() => RunPackJob(action, source, sha256, name, indexUrl)) { IsBackground = true, Name = "argus-packs" }.Start();
        return true;
    }

    void RunPackJob(string action, string? source, string? sha256, string? name, string? indexUrl)
    {
        var dest = cfg.PacksDir;
        void Say(string line)
        {
            lock (_packLock)
            {
                var tail = (_pack["tail"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).Append(line).TakeLast(40);
                _pack["tail"] = Strings(tail);
            }
        }
        int rc = 0;
        try
        {
            switch (action)
            {
                case "install":
                {
                    var s = PyStr.Strip(source ?? "");
                    if (s.Length == 0) throw new RegistryError("no pack URL or path given");
                    var sha = PyStr.Strip(sha256 ?? "");
                    Say($"fetching {s}");
                    var pack = Registry.Install(s, dest, sha.Length > 0 ? sha : null);
                    Say($"installed {pack.Name} {pack.Version} ({pack.SizeBytes / 1048576.0:0.0} MB)");
                    if (!pack.Compatible) Say($"warning: {pack.IncompatibleReason}");
                    break;
                }
                case "update":
                {
                    var url = PyStr.Strip(indexUrl ?? "");
                    if (url.Length == 0) url = PackIndexUrl();
                    if (url.Length == 0) throw new RegistryError("no pack index configured; set ARGUS_PACK_INDEX_URL or give one here");
                    var available = Registry.FetchIndex(url).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Last());
                    var installed = Registry.ListInstalled(dest);
                    var wanted = PyStr.Strip(name ?? "");
                    if (wanted.Length > 0)
                    {
                        installed = installed.Where(p => p.Name == wanted).ToList();
                        if (installed.Count == 0) throw new RegistryError($"no installed pack named {PyStr.Repr(wanted)}");
                    }
                    int updated = 0;
                    foreach (var pack in installed)
                    {
                        if (!available.TryGetValue(pack.Name, out var entry)) { Say($"{pack.Name}: not in the index, leaving alone"); continue; }
                        if (entry.Version == pack.Version) { Say($"{pack.Name}: {pack.Version} is current"); continue; }
                        Say($"{pack.Name}: {pack.Version} -> {entry.Version}, downloading");
                        Registry.Install(entry.Url, dest, entry.Sha256);
                        updated++;
                    }
                    Say($"{updated} pack(s) updated");
                    break;
                }
                case "remove":
                {
                    var n = PyStr.Strip(name ?? "");
                    if (!Registry.Remove(n, dest)) throw new RegistryError($"no installed pack named {PyStr.Repr(n)}");
                    Say($"removed {n}");
                    break;
                }
                default:
                    throw new RegistryError($"unknown action {PyStr.Repr(action)}");
            }
        }
        catch (Exception exc)
        {
            rc = 1;
            Say($"failed: {exc.GetType().Name}: {exc.Message}");
        }
        finally
        {
            lock (_packLock)
            {
                _pack["state"] = "idle";
                _pack["finished"] = Now();
                _pack["returncode"] = rc;
            }
        }
    }
}
