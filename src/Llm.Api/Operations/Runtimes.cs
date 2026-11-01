using System.Globalization;
using System.Text;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Models;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Operations;

/// <summary>
/// An admin's ask of an engine or a sandbox (Admin → Monitoring): a file of key=value lines that the service it is for
/// reads and never runs (gen=, action=, by=, at=, and model= for each model it concerns), written whole. Each ask raises
/// its generation, so the service acts once on each.
/// </summary>
public sealed record RuntimeAsk(long Gen, string Action, string By, DateTimeOffset At, IReadOnlyList<string> Models)
{
    public static RuntimeAsk? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var lines = File.ReadAllLines(path);
            string? Get(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
            return long.TryParse(Get("gen"), CultureInfo.InvariantCulture, out var gen)
                ? new RuntimeAsk(gen, Get("action") ?? "", Get("by") ?? "", DateTimeOffset.FromUnixTimeSeconds(long.TryParse(Get("at"), CultureInfo.InvariantCulture, out var at) ? at : 0),
                    [.. lines.Where(l => l.StartsWith("model=", StringComparison.Ordinal)).Select(l => l[6..])])
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The next ask, in place of the last (its generation one more), renamed into place whole.</summary>
    public static RuntimeAsk Write(string path, string action, string by, DateTimeOffset at, IReadOnlyList<string>? models = null)
    {
        static string Line(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
        var ask = new RuntimeAsk((Read(path)?.Gen ?? 0) + 1, action, Line(by), at, [.. (models ?? []).Select(Line)]);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"gen={ask.Gen}\naction={ask.Action}\nby={ask.By}\nat={ask.At.ToUnixTimeSeconds()}\n");
        foreach (var m in ask.Models)
        {
            text.Append(CultureInfo.InvariantCulture, $"model={m}\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, text.ToString());
        File.Move(temporary, path, overwrite: true);
        return ask;
    }
}

/// <summary>What was asked of an engine or a sandbox last (the audit log's latest runtime.* entry for it).</summary>
public sealed record RuntimeLast(string Action, string? By, DateTimeOffset At);

/// <summary>
/// An engine, a picture or video server, or a sandbox, as Admin → Monitoring shows it: its id and kind (engine, media,
/// sandbox), its state (up, idle, loading, down, reloading, restarting, resetting), since when and why, the answers or jobs
/// running and waiting on it (an engine's: this replica's answers), what an admin may do to it, and what was done last.
/// </summary>
public sealed record RuntimeView(string Id, string Kind, string Label, string State, DateTimeOffset? Since, string? Detail, int Busy, int Waiting,
    IReadOnlyList<string> Actions, RuntimeLast? Last);

/// <summary>An ask refused: its HTTP status, a code, and why.</summary>
public sealed record RuntimeRefusal(int Status, string Code, string Message);

/// <summary>
/// The engines and sandboxes an admin may reload, restart or reset, with no Docker socket: each service follows a control
/// file the app writes on the volume they share (Docker Compose and Kubernetes alike), or the app does it over the
/// engine's own API.
/// <list type="bullet">
/// <item>llama.cpp: Reload unloads each loaded model and loads it again, one after another (the router's API); Restart writes
/// llamacpp.restart, and the router starts llama-server again (services/llamacpp/router.sh).</item>
/// <item>vLLM and SGLang: Reload and Restart write the instance's reload and restart files, which its supervisor follows
/// (services/vllm/supervisor.sh): every model drained and started again, or the supervisor too.</item>
/// <item>The picture and video servers: Restart raises their control file's generation (MediaControl.Restart).</item>
/// <item>The sandboxes (Python, Blender, Lean): Reset writes their reset file; the runner stops its running jobs, answers its
/// waiting ones (each caller is told), and starts again (services/sandbox/runner.py).</item>
/// </list>
/// An ask is pending while the service has not taken it, within <see cref="ComeBack"/>: another is refused meanwhile. The
/// files are shared, so every replica sees it.
/// </summary>
public sealed partial class Runtimes(IOptions<EngineOptions> engine, Features features, Modules modules, EngineClient engines, EngineState state, EngineRoute route,
    EngineWatcher watcher, LlamaCppBackend llama, VllmBackends vllm, MediaControl media, SandboxClient sandbox, BlenderClient blender, Lab.LeanClient lean,
    AnswerGate gate, IServiceScopeFactory scopes, TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<Runtimes> logger)
{
    public const string Reload = "reload";
    public const string Restart = "restart";
    public const string Reset = "reset";

    /// <summary>How long an ask is waited for: past this, one that has not come back is down, and may be asked again.</summary>
    public static readonly TimeSpan ComeBack = TimeSpan.FromMinutes(3);

    /// <summary>llama.cpp's router looks every 5 s and gives llama-server 15 s to stop: past this, the server that answers is the new one.</summary>
    public static readonly TimeSpan RouterSettle = TimeSpan.FromSeconds(25);

    /// <summary>The audit log's actions for each, by its action.</summary>
    private static readonly string[] Audited = ["runtime." + Reload, "runtime." + Restart, "runtime." + Reset];

    /// <summary>One of them: its id, its kind, its name for people, and what may be asked of it.</summary>
    private sealed record Target(string Id, string Kind, string Label, IReadOnlyList<string> Actions);

    /// <summary>What one is doing now.</summary>
    private sealed record Now(string State, DateTimeOffset? Since, string? Detail, int Busy = 0, int Waiting = 0);

    /// <summary>Whether this replica is unloading and loading llama.cpp's models again now.</summary>
    private volatile bool _reloading;

    private IEnumerable<(string Id, JobRunner Runner)> Sandboxes => [("sandbox", sandbox), ("blender", blender), ("lean", lean)];

    private string LlamaFile(string file) => Path.Combine(engine.Value.ConfigDir, file);

    private string MediaFile(string server) => Path.Combine(engine.Value.ConfigDir, server + ".restart");

    /// <summary>Those this stack runs: llama.cpp and each vLLM and SGLang engine (their features on), the picture and video servers, the sandboxes.</summary>
    private async Task<IReadOnlyList<Target>> TargetsAsync(CancellationToken ct)
    {
        var list = new List<Target>();
        var e = engine.Value;
        if (e.Enabled && e.LlamaCpp && features.Runs(llama.Name))
        {
            list.Add(new Target(llama.Name, "engine", "llama.cpp", [Reload, Restart]));
        }
        if (e.Enabled)
        {
            list.AddRange(vllm.All.Where(b => features.Runs(b.Name)).Select(b => new Target(b.Name, "engine", b.Name == b.Kind ? b.Label : $"{b.Label} ({b.Name})", [Reload, Restart])));
        }
        foreach (var server in MediaControl.FileServers)
        {
            if (await modules.HasAsync(server, ct))
            {
                list.Add(new Target(server, "media", server == "imagegen" ? "Pictures (imagegen)" : "Video (videogen)", [Restart]));
            }
        }
        list.AddRange(Sandboxes.Where(s => features.Runs(s.Id)).Select(s => new Target(s.Id, "sandbox", s.Runner.Label, [Reset])));
        return list;
    }

    public async Task<IReadOnlyList<RuntimeView>> ListAsync(CancellationToken ct)
    {
        var last = await LastAsync(ct);
        var views = new List<RuntimeView>();
        foreach (var t in await TargetsAsync(ct))
        {
            var now = await NowAsync(t, ct);
            views.Add(new RuntimeView(t.Id, t.Kind, t.Label, now.State, now.Since, now.Detail, now.Busy, now.Waiting, t.Actions, last.GetValueOrDefault(t.Id)));
        }
        return views;
    }

    /// <summary>What was asked of each last, by its id, from the audit log (the last 90 days).</summary>
    private async Task<Dictionary<string, RuntimeLast>> LastAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var since = clock.GetUtcNow() - TimeSpan.FromDays(90);
        var events = await db.AuditEvents.AsNoTracking().Where(a => a.At > since && Audited.Contains(a.Action) && a.Success && a.Target != null)
            .OrderByDescending(a => a.Id).Take(200).Select(a => new { a.Action, a.Target, a.Actor, a.At }).ToListAsync(ct);
        return events.GroupBy(a => a.Target!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new RuntimeLast(g.First().Action["runtime.".Length..], g.First().Actor, g.First().At), StringComparer.Ordinal);
    }

    private async Task<Now> NowAsync(Target t, CancellationToken ct) => t.Kind switch
    {
        "engine" when t.Id == llama.Name => LlamaNow(),
        "engine" => VllmNow(vllm.All.First(b => b.Name == t.Id)),
        "media" => await MediaNowAsync(t.Id, ct),
        _ => SandboxNow(Sandboxes.First(s => s.Id == t.Id).Runner),
    };

    /// <summary>Answers running and waiting on these models, in this replica's line.</summary>
    private (int Busy, int Waiting) Answers(IEnumerable<string> models)
    {
        int busy = 0, waiting = 0;
        foreach (var m in models.Distinct(StringComparer.Ordinal))
        {
            var (running, queued) = gate.Now(m);
            busy += running;
            waiting += queued;
        }
        return (busy, waiting);
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static string Names(IEnumerable<string> names) => string.Join(", ", names);

    /// <summary>llama.cpp's models, as the watcher last saw them.</summary>
    private List<EngineModel> LlamaModels() => [.. state.Now.Models.Where(m => engines.BackendOf(m.Name) is LlamaCppBackend)];

    private Now LlamaNow()
    {
        var at = clock.GetUtcNow();
        var models = LlamaModels();
        var loaded = models.Where(m => m.Status == "loaded").Select(m => m.Name).ToList();
        var (busy, waiting) = Answers(loaded);
        var seen = state.Now;
        var restart = RuntimeAsk.Read(LlamaFile(LlamaCppBackend.RestartFile));
        var reload = RuntimeAsk.Read(LlamaFile(LlamaCppBackend.ReloadFile));
        // Restarting until the router has had its time to stop the server, and the watcher has seen the new one answer since.
        if (restart is { } r && at - r.At < ComeBack && (reload is null || reload.At <= r.At)
            && (at - r.At < RouterSettle || seen.Error is not null || seen.At is not { } looked || looked < r.At + RouterSettle))
        {
            return new Now("restarting", r.At, $"Asked by {r.By}: llama-server starts again, then the models kept loaded load again.", busy, waiting);
        }
        if (reload is { } l && at - l.At < ComeBack && (restart is null || restart.At < l.At)
            && (_reloading || l.Models.Any(m => state.StatusOf(m) is "loading" or "unloading" or "unloaded")))
        {
            return new Now("reloading", l.At, $"Asked by {l.By}: {Names(l.Models)} unloaded and loaded again, one after another.", busy, waiting);
        }
        if (seen.Error is { } error)
        {
            return new Now("down", seen.At, error);
        }
        if (models.Where(m => m.Status == "loading").Select(m => m.Name).ToList() is { Count: > 0 } loading)
        {
            return new Now("loading", null, $"Loading {Names(loading)}.", busy, waiting);
        }
        return new Now("up", null, loaded.Count == 0 ? "No model loaded." : $"{Count(loaded.Count, "model")} loaded: {Names(loaded)}.", busy, waiting);
    }

    private Now VllmNow(VllmBackend b)
    {
        var at = clock.GetUtcNow();
        var desired = b.Desired();
        var models = desired.Select(e => e.Model).Distinct(StringComparer.Ordinal).ToList();
        var (busy, waiting) = Answers(models);
        // Pending until the supervisor says it has done that generation.
        var ask = new[] { b.Asked(VllmBackend.RestartFile), b.Asked(VllmBackend.ReloadFile) }.OfType<RuntimeAsk>().MaxBy(a => (a.At, a.Action == Restart));
        if (ask is { } a && at - a.At < ComeBack && b.Heartbeat() is var beat && (beat is null || (a.Action == Restart ? beat.Value.Restart : beat.Value.Reload) < a.Gen))
        {
            return a.Action == Restart
                ? new Now("restarting", a.At, $"Asked by {a.By}: every model drained, then the supervisor and the models start again.", busy, waiting)
                : new Now("reloading", a.At, $"Asked by {a.By}: every model drained and started again.", busy, waiting);
        }
        if (b.Down() is { } why)
        {
            return new Now("down", null, why);
        }
        var phases = desired.Select(e => (e.Model, Phase: b.StatusOf(e.Name)?["phase"]?.GetValue<string>())).ToList();
        if (phases.Where(p => p.Phase is null or "loading" or "draining").Select(p => p.Model).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } loading)
        {
            return new Now("loading", null, $"Loading {Names(loading)}.", busy, waiting);
        }
        var failed = phases.Where(p => p.Phase == "failed").Select(p => p.Model).Distinct(StringComparer.Ordinal).ToList();
        return new Now("up", null, models.Count == 0 ? "No model running."
            : $"{Count(models.Count, "model")} running: {Names(models)}." + (failed.Count > 0 ? $" Could not start: {Names(failed)} (Admin → Models says why)." : ""), busy, waiting);
    }

    private async Task<Now> MediaNowAsync(string server, CancellationToken ct)
    {
        var model = media.RunningOn(server);
        if (RuntimeAsk.Read(MediaFile(server)) is { } a && clock.GetUtcNow() - a.At < ComeBack && !media.Current(server))
        {
            return new Now("restarting", a.At, $"Asked by {a.By}: its server starts again with {model ?? "its model"}.");
        }
        if (model is null)
        {
            return new Now("idle", null, "No model loaded: its server starts when one is asked for.");
        }
        var status = (await media.ListAsync(ct)).FirstOrDefault(s => s.Model.Name == model);
        return status?.Now switch
        {
            "loaded" => new Now("up", null, $"Running {model}."),
            "failed" => new Now("down", status.Failure?.Since, $"{model} could not load" + (status.Failure is { } f ? $" ({f.Class}: {f.Detail})." : ".")),
            _ => new Now("loading", status?.Since, $"Loading {model}" + (status?.Why is { } why ? $" ({why})." : ".")),
        };
    }

    private Now SandboxNow(JobRunner runner)
    {
        var load = runner.Load();
        var (busy, waiting) = load is { } l ? (l.Busy, l.Waiting) : (0, 0);
        if (RuntimeAsk.Read(runner.ResetPath) is { } a)
        {
            return new Now("resetting", a.At, clock.GetUtcNow() - a.At < ComeBack
                ? $"Asked by {a.By}: its running jobs are stopped and its waiting ones answered, then it starts again."
                : $"Asked by {a.By} at {a.At:HH:mm} UTC and not done yet: a runner that hangs ends itself within a minute and is started again.", busy, waiting);
        }
        if (runner.Unavailable() is { } why)
        {
            return new Now("down", null, why);
        }
        return new Now("up", null, load is { } s ? $"{s.Busy} of {Count(s.Slots, "slot")} busy." : null, busy, waiting);
    }

    /// <summary>
    /// Asks <paramref name="action"/> of <paramref name="id"/> for <paramref name="who"/>: what it concerns for the audit log
    /// ("2 models", "1 running, 3 waiting told"), or why it is refused.
    /// </summary>
    public async Task<(string? Detail, RuntimeRefusal? Refusal)> AskAsync(string id, string action, string who, CancellationToken ct)
    {
        var target = (await TargetsAsync(ct)).FirstOrDefault(t => t.Id == id);
        if (target is null)
        {
            return (null, new RuntimeRefusal(404, "unknown", $"This stack runs no engine or sandbox named {id}."));
        }
        if (!target.Actions.Contains(action))
        {
            return (null, new RuntimeRefusal(400, "action", $"{target.Label} is not {Done(action)}: it is {string.Join(" or ", target.Actions.Select(Done))}."));
        }
        var now = await NowAsync(target, ct);
        if (now.State is "reloading" or "restarting" or "resetting" && now.Since is { } since && clock.GetUtcNow() - since < ComeBack)
        {
            return (null, new RuntimeRefusal(409, "pending", $"{target.Label} is {now.State} since {since:HH:mm} UTC: wait until it is back."));
        }
        if (target.Kind == "engine" && state.Lent is { } lent)
        {
            return (null, new RuntimeRefusal(409, "lent", $"The GPU is making a {lent.Kind} with {lent.Media} just now (since {lent.Since:HH:mm} UTC): ask once it is done."));
        }
        try
        {
            return target.Kind switch
            {
                "engine" when id == llama.Name => action == Reload ? ReloadLlama(who, now) : await RestartLlamaAsync(who, now),
                "engine" => AskVllm(vllm.All.First(b => b.Name == id), action, who, now),
                "media" => RestartMedia(id, who),
                _ => ResetSandbox(Sandboxes.First(s => s.Id == id).Runner, who, now),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EngineException)
        {
            return (null, new RuntimeRefusal(503, "engine", ex is EngineException ? ex.Message : $"{target.Label}'s control file cannot be written ({ex.Message})."));
        }
    }

    private static string Done(string action) => action switch
    {
        Reload => "reloaded",
        Restart => "restarted",
        _ => "reset",
    };

    private (string?, RuntimeRefusal?) ReloadLlama(string who, Now now)
    {
        var loaded = LlamaModels().Where(m => m.Status == "loaded").Select(m => m.Name).ToList();
        if (loaded.Count == 0)
        {
            return (null, new RuntimeRefusal(409, "nothing", "llama.cpp has no model loaded: there is nothing to reload."));
        }
        RuntimeAsk.Write(LlamaFile(LlamaCppBackend.ReloadFile), Reload, who, clock.GetUtcNow(), loaded);
        _reloading = true;
        _ = Task.Run(() => ReloadLlamaAsync(loaded, who, lifetime.ApplicationStopping), CancellationToken.None);
        return ($"{Count(loaded.Count, "model")} ({Names(loaded)}); {Count(now.Busy, "answer")} cut", null);
    }

    /// <summary>Each model unloaded (requests no longer go to it), then loaded again once the engine has stopped it: one at a time, so the others answer meanwhile.</summary>
    private async Task ReloadLlamaAsync(IReadOnlyList<string> models, string who, CancellationToken ct)
    {
        try
        {
            foreach (var m in models)
            {
                try
                {
                    route.Unloaded(m, $"reloading, asked by {who}");
                    await llama.UnloadAsync(m, ct);
                    for (var i = 0; i < 60 && (await llama.ListAsync(ct)).FirstOrDefault(x => x.Name == m)?.Status is "loaded" or "unloading"; i++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }
                    state.Loading(m, $"reloaded by {who}");
                    state.Asked(m);
                    await llama.LoadAsync(m, ct);
                }
                catch (EngineException ex)
                {
                    LogReloadFailed(logger, m, ex.Message);
                }
                watcher.Wake();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            _reloading = false;
            watcher.Wake();
        }
    }

    private async Task<(string?, RuntimeRefusal?)> RestartLlamaAsync(string who, Now now)
    {
        RuntimeAsk.Write(LlamaFile(LlamaCppBackend.RestartFile), Restart, who, clock.GetUtcNow());
        // The models kept loaded load again once it is back: their cards say why.
        await using var scope = scopes.CreateAsyncScope();
        var kept = scope.ServiceProvider.GetRequiredService<ModelCatalog>().Kept();
        foreach (var m in kept.Where(m => engines.BackendOf(m) is LlamaCppBackend))
        {
            state.Loading(m, $"restarted by {who}");
        }
        watcher.Wake();
        return ($"{Count(now.Busy, "answer")} cut, {now.Waiting} waiting", null);
    }

    private (string?, RuntimeRefusal?) AskVllm(VllmBackend b, string action, string who, Now now)
    {
        b.Ask(action == Reload ? VllmBackend.ReloadFile : VllmBackend.RestartFile, action, who);
        var models = b.Desired().Select(e => e.Model).Distinct(StringComparer.Ordinal).ToList();
        foreach (var m in models)
        {
            state.Loading(m, $"{Done(action)} by {who}");
        }
        watcher.Wake();
        return ($"{Count(models.Count, "model")}; {Count(now.Busy, "answer")} cut, {now.Waiting} waiting", null);
    }

    private (string?, RuntimeRefusal?) RestartMedia(string server, string who)
    {
        if (media.RunningOn(server) is not { } model)
        {
            return (null, new RuntimeRefusal(409, "nothing", "Its server runs no model now: there is nothing to restart."));
        }
        RuntimeAsk.Write(MediaFile(server), Restart, who, clock.GetUtcNow(), [model]);
        media.Restart(server, $"restarted by {who}");
        return (model, null);
    }

    private (string?, RuntimeRefusal?) ResetSandbox(JobRunner runner, string who, Now now)
    {
        RuntimeAsk.Write(runner.ResetPath, Reset, who, clock.GetUtcNow());
        return ($"{now.Busy} running, {now.Waiting} waiting told", null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reloading {Model} on llama.cpp: {Error}")]
    private static partial void LogReloadFailed(ILogger logger, string model, string error);
}
