using System.Diagnostics;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Packs;
using Argus.Util;

namespace Argus.Server;

/// <summary>
/// Background work the server runs on the operator's behalf: one index run at
/// a time as a child `argus index`
/// process, webhook pushes queued behind it, the interval scheduler, and pack
/// install/update/remove.
/// </summary>
public sealed class Jobs(ArgusConfig cfg)
{
    public const int DefaultIndexInterval = 0;
    public const int FirstPassGrace = 30;

    readonly Lock _indexLock = new();
    readonly JsonObject _index = new()
    {
        ["state"] = "idle", ["branches"] = new JsonArray(), ["started"] = null, ["finished"] = null,
        ["returncode"] = null, ["tail"] = new JsonArray(), ["trigger"] = null, ["pending"] = new JsonArray(), ["pending_full"] = false,
        ["progress"] = null,
    };

    readonly Lock _packLock = new();
    readonly JsonObject _pack = new()
    {
        ["state"] = "idle", ["action"] = null, ["target"] = null, ["started"] = null,
        ["finished"] = null, ["returncode"] = null, ["tail"] = new JsonArray(),
    };

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static int IndexInterval() =>
        int.TryParse(Environment.GetEnvironmentVariable("ARGUS_INDEX_INTERVAL"), out var v) ? v : DefaultIndexInterval;

    public static string PackIndexUrl() => (Environment.GetEnvironmentVariable("ARGUS_PACK_INDEX_URL") ?? "").Trim();

    string CfgPath => cfg.SourcePath ?? Environment.GetEnvironmentVariable("ARGUS_CONFIG") ?? "/etc/argus/config.yaml";

    public JsonObject IndexJobSnapshot() { lock (_indexLock) return (JsonObject)_index.DeepClone(); }
    public JsonNode? IndexStarted() { lock (_indexLock) return _index["started"]?.DeepClone(); }

    static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)s).ToArray());

    void MarkRunning(IEnumerable<string> branches, bool allowPartial, string trigger)
    {
        _index["state"] = "running";
        _index["branches"] = Strings(branches);
        _index["allow_partial"] = allowPartial;
        _index["trigger"] = trigger;
        _index["started"] = Now();
        _index["finished"] = null;
        _index["returncode"] = null;
        _index["tail"] = new JsonArray();
        _index["progress"] = new JsonObject { ["repos"] = 0, ["position"] = 0, ["stage"] = "starting", ["outcomes"] = new JsonObject() };
    }

    bool Running => _index["state"]?.ToString() == "running";

    /// <summary>
    /// A step the index process reported (Indexing.Progress): the pass's size, the branch
    /// it is on and how many of its files are done, each branch's outcome, the last steps.
    /// </summary>
    void OnProgress(string json)
    {
        JsonObject? step;
        try { step = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (step is not null && _index["progress"] is JsonObject p) Step(p, step);
    }

    /// <summary>One reported step folded into the run's progress.</summary>
    public static void Step(JsonObject p, JsonObject step)
    {
        var stage = step["stage"]?.ToString();
        switch (stage)
        {
            case "pass":
                p["repos"] = step["repos"]?.DeepClone();
                break;
            case "branch":
                p["position"] = step["position"]?.DeepClone();
                p["repo"] = step["repo"]?.DeepClone();
                p["branch"] = step["branch"]?.DeepClone();
                p["done"] = 0;
                p["total"] = null;
                break;
            case "files":
                p["done"] = step["done"]?.DeepClone();
                p["total"] = step["total"]?.DeepClone();
                break;
            case "branch_done":
                if (p["outcomes"] is JsonObject outcomes) outcomes[$"{step["repo"]}@{step["branch"]}"] = step["outcome"]?.DeepClone();
                break;
            case "finishing":
                p["what"] = step["what"]?.DeepClone();
                break;
        }
        p["stage"] = stage;
    }

    /// <param name="trigger">manual, schedule (the app's), webhook: shown with the run.</param>
    public bool StartIndex(IReadOnlyList<string> branches, bool allowPartial, string trigger)
    {
        lock (_indexLock)
        {
            if (Running) return false;
            MarkRunning(branches, allowPartial, trigger);
        }
        new Thread(() => RunIndex(branches, allowPartial, null)) { IsBackground = true, Name = "argus-index" }.Start();
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
                Thread.Sleep(TimeSpan.FromSeconds(delay));
                delay = interval;
                if (!StartIndex([], false, "schedule"))
                    AuditLog.IndexScheduled(interval, skipped: "a run is already in progress");
            }
        }) { IsBackground = true, Name = "argus-scheduler" }.Start();
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

    void RunIndex(IReadOnlyList<string> branches, bool allowPartial, string? only)
    {
        var argv = SelfCommand();
        argv.AddRange(["index", "--config", CfgPath]);
        foreach (var b in branches) argv.AddRange(["--branch", b]);
        if (only is not null) argv.AddRange(["--repo", only]);
        if (allowPartial) argv.Add("--allow-partial-enumeration");
        int rc;
        try
        {
            var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
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
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            rc = proc.ExitCode;
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
        }
        DrainPending();
    }

    void DrainPending()
    {
        string? only;
        int remaining;
        lock (_indexLock)
        {
            if (Running) return;
            bool full = _index["pending_full"]?.GetValue<bool>() ?? false;
            var pending = (_index["pending"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
            if (!full && pending.Count == 0) return;
            _index["pending"] = new JsonArray();
            _index["pending_full"] = false;
            only = null;
            if (!full) { only = pending[0]; pending.RemoveAt(0); }
            if (pending.Count > 0) _index["pending"] = Strings(pending);
            remaining = pending.Count;
            MarkRunning([], false, _index["pending_trigger"]?.ToString() ?? "webhook");
        }
        AuditLog.IndexWebhook(only ?? "*", queued: remaining);
        new Thread(() => RunIndex([], false, only)) { IsBackground = true, Name = "argus-index" }.Start();
    }

    /// <summary>A push arrived: start it, queue it behind the running pass, or collapse an overfull queue.</summary>
    public JsonObject EnqueueWebhook(string repo) => EnqueueRepo(repo, "webhook");

    /// <summary>One repository to bring up to date (a push, or an admin's Update): started now, or queued behind the running pass.</summary>
    public JsonObject EnqueueRepo(string repo, string trigger)
    {
        lock (_indexLock)
        {
            if (Running)
            {
                var pending = (_index["pending"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
                if (pending.Contains(repo))
                    return new JsonObject { ["status"] = "already_queued", ["repo"] = repo, ["queued"] = pending.Count };
                pending.Add(repo);
                if (pending.Count > ArgusServer.WebhookQueueLimit)
                {
                    _index["pending"] = new JsonArray();
                    _index["pending_full"] = true;
                    AuditLog.IndexWebhook(repo, collapsed: pending.Count);
                    return new JsonObject { ["status"] = "collapsed_to_full_pass", ["repo"] = repo, ["queued"] = 0 };
                }
                _index["pending"] = Strings(pending);
                _index["pending_trigger"] = trigger;
                AuditLog.IndexWebhook(repo, queued: pending.Count);
                return new JsonObject { ["status"] = "queued", ["repo"] = repo, ["queued"] = pending.Count };
            }
            MarkRunning([], false, trigger);
        }
        AuditLog.IndexWebhook(repo, started: true);
        new Thread(() => RunIndex([], false, repo)) { IsBackground = true, Name = "argus-index" }.Start();
        return new JsonObject { ["status"] = "started", ["repo"] = repo, ["queued"] = 0 };
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
