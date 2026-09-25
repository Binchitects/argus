using System.Diagnostics;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Packs;
using Argus.Util;

namespace Argus.Server;

/// <summary>
/// Background work the server runs on the operator's behalf (server.py's
/// _index_job / _pack_job): one index run at a time as a child `argus index`
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
    }

    bool Running => _index["state"]?.ToString() == "running";

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
            MarkRunning([], false, "webhook");
        }
        AuditLog.IndexWebhook(only ?? "*", queued: remaining);
        new Thread(() => RunIndex([], false, only)) { IsBackground = true, Name = "argus-index" }.Start();
    }

    /// <summary>A push arrived: start it, queue it behind the running pass, or collapse an overfull queue.</summary>
    public JsonObject EnqueueWebhook(string repo)
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
                AuditLog.IndexWebhook(repo, queued: pending.Count);
                return new JsonObject { ["status"] = "queued", ["repo"] = repo, ["queued"] = pending.Count };
            }
            MarkRunning([], false, "webhook");
        }
        AuditLog.IndexWebhook(repo, started: true);
        new Thread(() => RunIndex([], false, repo)) { IsBackground = true, Name = "argus-index" }.Start();
        return new JsonObject { ["status"] = "started", ["repo"] = repo, ["queued"] = 0 };
    }

    // --- packs ----------------------------------------------------------------------------

    public JsonObject PackJobSnapshot() { lock (_packLock) return (JsonObject)_pack.DeepClone(); }
    public bool PackJobRunning() { lock (_packLock) return _pack["state"]?.ToString() == "running"; }

    public static JsonArray PackRows(string packsDir)
    {
        var rows = Registry.ListInstalled(packsDir).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (JsonNode?)new JsonObject
        {
            ["name"] = p.Name, ["version"] = p.Version, ["model"] = p.EmbeddingModel, ["dim"] = p.EmbeddingDim,
            ["size_bytes"] = p.SizeBytes, ["license"] = p.License, ["commit"] = p.SourceCommit,
            ["compatible"] = p.Compatible, ["incompatible_reason"] = p.IncompatibleReason,
        });
        return new JsonArray(rows.ToArray());
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
