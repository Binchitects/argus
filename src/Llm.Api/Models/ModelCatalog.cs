using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Llm.Api.Operations;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// What the engine said last: its models and which are loaded, and when each that failed to load is tried
/// again. Kept by <see cref="EngineWatcher"/>.
/// </summary>
public sealed class EngineState(TimeProvider clock)
{
    /// <summary>After a try, people's requests for the model go too for this long (the engine is loading it).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>A try still seen failed this long after it began has failed (the engine had the request at once).</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a model the app told to unload counts as unloaded while the engine still lists it loaded: the router
    /// lists one it stops as loaded until it has stopped, and kills one that has not within 10 seconds.
    /// </summary>
    private static readonly TimeSpan Stopping = TimeSpan.FromSeconds(30);

    private volatile Snapshot _now = new([], null, null);
    private readonly ConcurrentDictionary<string, Failure> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _dropped = new(StringComparer.Ordinal);

    /// <summary>The models an admin loaded, and when: loaded again should the engine lose the load (a restart under it).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _asked = new(StringComparer.Ordinal);

    /// <summary>How long an admin's Load is followed up: past this, a model the engine never loaded stays as it is.</summary>
    public static readonly TimeSpan AskedFor = TimeSpan.FromMinutes(10);

    /// <summary>The models the app told to unload, and when (a <see cref="TimeProvider.GetTimestamp"/>: <see cref="Unloading"/>).</summary>
    private readonly ConcurrentDictionary<string, long> _stopping = new(StringComparer.Ordinal);

    /// <summary>Since when each loaded model has been seen loaded, as the engine said (<see cref="LoadedSince"/>).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _loaded = new(StringComparer.Ordinal);

    public sealed record Snapshot(IReadOnlyList<EngineModel> Models, string? Error, DateTimeOffset? At);

    /// <summary>
    /// A model the router marked failed: how many times it failed, since when it waits (its last try, or when it was
    /// first seen failed), and until when people's requests go too after a try (null: none, or that try failed).
    /// </summary>
    private sealed record Failure(int Count, DateTimeOffset Since, DateTimeOffset? Until);

    public Snapshot Now => _now;

    /// <summary>
    /// Loaded models that may make room for another, the quickest to load again first: those not in <see cref="Held"/>,
    /// smallest file first, whatever their size (the watcher keeps it).
    /// </summary>
    public IReadOnlyList<string> Spare { get; set; } = [];

    /// <summary>
    /// The models that never make room (the watcher keeps it): those kept loaded and, with 2 or more at once, the one
    /// new chats use (<see cref="Default"/>), no more than the engine holds, and the model for small steps while a place
    /// is left beside it for the others (else it shares that place with them). With one at a time, only the kept.
    /// </summary>
    public IReadOnlyCollection<string> Held { get; set; } = [];

    /// <summary>
    /// The engine model new chats use, as the watcher last worked it out: the working hours', the one under Settings,
    /// the first kept; with none of them, the one new chats got while it was loaded (so neither one loaded on request
    /// nor an engine that unloaded it to load another takes its place). Null: none of the engine's.
    /// </summary>
    public string? Default { get; set; }

    /// <summary>An admin unloaded it (Admin → Models): it is not loaded again by itself until it loads once more.</summary>
    public void Dropped(string model) => _dropped[model] = true;

    /// <summary>Whether an admin unloaded it and it has not loaded since.</summary>
    public bool WasDropped(string model) => _dropped.ContainsKey(model);

    /// <summary>
    /// An admin loaded it (Admin → Models): an Unload before no longer holds it back, and until it loads (for
    /// <see cref="AskedFor"/>) the watcher asks the engine again whenever it lists it unloaded: a load sent while the
    /// engine restarted (its presets changed) is otherwise lost, and the model stayed unloaded.
    /// </summary>
    public void Asked(string model)
    {
        _dropped.TryRemove(model, out _);
        _asked[model] = clock.GetUtcNow();
    }

    /// <summary>An admin unloaded it: an earlier Load of theirs is no longer followed up.</summary>
    public void Forget(string model) => _asked.TryRemove(model, out _);

    /// <summary>Whether an admin's Load of it is still being followed up: asked within <see cref="AskedFor"/>, not loaded since.</summary>
    public bool StillAsked(string model) => _asked.TryGetValue(model, out var at) && clock.GetUtcNow() - at < AskedFor;

    /// <summary>The time to give <see cref="Set"/> and <see cref="Seen"/> (a <see cref="TimeProvider.GetTimestamp"/>): taken just before the engine is asked for its models.</summary>
    public long Asking() => clock.GetTimestamp();

    /// <summary>
    /// The app told the engine to unload <paramref name="model"/> (to make room for another, an admin's Unload, working
    /// hours that ended): it counts as unloaded at once, here and in what the engine says next, until the engine lists it
    /// as anything but loaded (it stopped, or loads again), or <see cref="Stopping"/> passes. Until the watcher looks
    /// again its list said loaded, and the engine says loaded until the model has stopped: a request for it let through
    /// meanwhile would reach an engine without it, which loads it by unloading the model used least recently.
    /// </summary>
    public void Unloading(string model)
    {
        _stopping[model] = clock.GetTimestamp();
        lock (_stopping)
        {
            _now = _now with { Models = [.. _now.Models.Select(m => m.Name == model && m.Status == "loaded" ? m with { Status = "unloaded" } : m)] };
        }
    }

    /// <summary>The app did not unload <paramref name="model"/> after all (it was asked for meanwhile, or the engine refused): loaded, as before.</summary>
    public void NotUnloading(string model)
    {
        if (_stopping.TryRemove(model, out _))
        {
            lock (_stopping)
            {
                _now = _now with { Models = [.. _now.Models.Select(m => m.Name == model && m.Status == "unloaded" ? m with { Status = "loaded" } : m)] };
            }
        }
    }

    /// <summary>
    /// <paramref name="model"/> loads: a request for it was let through, or an admin loaded it. What the engine says of it
    /// goes again (though the app told it to unload a moment ago), and it is seen loaded from when the engine says so.
    /// </summary>
    public void Loading(string model)
    {
        _stopping.TryRemove(model, out _);
        _loaded.TryRemove(model, out _);
    }

    /// <summary>
    /// What the engine said (asked at <paramref name="asked"/>, from <see cref="Asking"/>), with the models the app told to
    /// unload shown unloaded while it still lists them loaded (<see cref="Unloading"/>).
    /// </summary>
    public IReadOnlyList<EngineModel> Seen(IReadOnlyList<EngineModel> models, long asked) =>
        _stopping.IsEmpty ? models : [.. models.Select(m => Stopped(m, asked) ? m : m with { Status = "unloaded" })];

    /// <summary>Whether what the engine said of <paramref name="m"/> goes: it was not told to unload, or no longer lists it loaded.</summary>
    private bool Stopped(EngineModel m, long asked)
    {
        if (!_stopping.TryGetValue(m.Name, out var told))
        {
            return true;
        }
        if (clock.GetElapsedTime(told) >= Stopping || (m.Status != "loaded" && asked >= told))
        {
            _stopping.TryRemove(new KeyValuePair<string, long>(m.Name, told));
            return true;
        }
        return m.Status != "loaded";
    }

    /// <summary>
    /// Since when <paramref name="model"/> has been seen loaded (as the engine said, each time it was asked); null while
    /// it is not loaded, or it loaded since it was last asked.
    /// </summary>
    public DateTimeOffset? LoadedSince(string model) => _loaded.TryGetValue(model, out var since) ? since : null;

    /// <summary>What the engine said of its models (the watcher's look), the models the app told to unload unloaded while it lists them loaded.</summary>
    /// <param name="asked">When the engine was asked (<see cref="Asking"/>).</param>
    public void Set(IReadOnlyList<EngineModel> engine, long asked)
    {
        var models = Seen(engine, asked);
        var now = clock.GetUtcNow();
        foreach (var m in models)
        {
            if (m.Status == "loaded")
            {
                _loaded.TryAdd(m.Name, now);
            }
            else
            {
                _loaded.TryRemove(m.Name, out _);
            }
            if (m.Status is "loaded" or "loading")
            {
                _dropped.TryRemove(m.Name, out _);
            }
            if (m.Status == "loaded")
            {
                _asked.TryRemove(m.Name, out _);
            }
            if (m.Status == "failed")
            {
                if (!_failed.TryAdd(m.Name, new Failure(1, now, null)) && _failed.TryGetValue(m.Name, out var f)
                    && f.Until is { } until && now < until && now - f.Since >= Settle)
                {
                    // Failed again after its try: the requests that follow wait for the next try.
                    _failed.TryUpdate(m.Name, f with { Until = null }, f);
                }
            }
            else if (m.Status == "loaded")
            {
                _failed.TryRemove(m.Name, out _);
            }
        }
        foreach (var gone in _loaded.Keys.Where(k => models.All(m => m.Name != k)))
        {
            _loaded.TryRemove(gone, out _);
        }
        lock (_stopping)
        {
            // One told to unload since it was looked at above counts as unloaded too.
            _now = new Snapshot([.. models.Select(m => m.Status == "loaded" && _stopping.ContainsKey(m.Name) ? m with { Status = "unloaded" } : m)], null, now);
        }
    }

    /// <summary>
    /// Whether a model the router marked failed is tried again now (the watcher loads a kept one then). Failed is
    /// not always broken: the router kills a model that does not stop within 10 seconds of being told to (one
    /// stopped while it loads, to make room for another, goes on loading and answering), and marks it failed. So
    /// it is tried again after a minute, then after twice as long at each failure, up to 30 minutes: once each
    /// time, never every few seconds.
    /// </summary>
    public bool MayRetry(string model)
    {
        if (StatusOf(model) != "failed" || !_failed.TryGetValue(model, out var f))
        {
            return false;
        }
        return clock.GetUtcNow() - f.Since >= Wait(f.Count);
    }

    /// <summary>
    /// Whether a person's request for a model the router marked failed goes to the engine (which loads it again):
    /// once its wait is over (<see cref="MayRetry"/>), and for half a minute after a try, while the engine loads it,
    /// unless that try is seen failed meanwhile.
    /// </summary>
    public bool MayAsk(string model) =>
        MayRetry(model) || (StatusOf(model) == "failed" && _failed.TryGetValue(model, out var f) && f.Until is { } until && clock.GetUtcNow() < until);

    /// <summary>
    /// A failed model is tried again (a request for it is sent, or the watcher loads it): should it fail again, the
    /// next wait is twice as long. Requests within half a minute of a try are part of that try.
    /// </summary>
    public void Tried(string model)
    {
        var now = clock.GetUtcNow();
        _failed.AddOrUpdate(model, _ => new Failure(2, now, now + Grace),
            (_, f) => f.Until is { } until && now < until ? f : new Failure(f.Count + 1, now, now + Grace));
    }

    /// <summary>When a model the router marked failed is next tried again (its wait is over then); null when it is not failed.</summary>
    public DateTimeOffset? NextTry(string model) =>
        StatusOf(model) == "failed" && _failed.TryGetValue(model, out var f) ? f.Since + Wait(f.Count) : null;

    /// <summary>The wait before a model that failed this many times is tried again: 1, 2, 4, 8, 16, then 30 minutes.</summary>
    public static TimeSpan Wait(int failures) => TimeSpan.FromMinutes(Math.Min(30, 1 << Math.Clamp(failures - 1, 0, 5)));

    public void Fail(string error) => _now = _now with { Error = error, At = clock.GetUtcNow() };

    /// <summary>"loaded", "loading", "unloaded", "failed" (its last load ended in an error), or null when the engine does not have that model.</summary>
    public string? StatusOf(string model) => _now.Models.FirstOrDefault(m => m.Name == model)?.Status;
}

/// <summary>
/// The models the engine can load: those admins added. Writes
/// the engine's presets (config/engine/models.ini), the models to keep loaded
/// (config/engine/keep, one name a line), Prometheus's scrape targets
/// (config/engine/targets.json), and keeps the gateway's list in step.
/// </summary>
public sealed partial class ModelCatalog(AppDbContext db, ILiteLlm gateway, IOptions<EngineOptions> options, ModelLibrary library, ChatModels chatModels,
    RemoteServerClient remote, ModelHoursState hours, Modules modules, MediaControl media, IOptionsMonitor<PriceOptions> prices, ILogger<ModelCatalog> logger)
{
    public const string PresetsFile = "models.ini";
    public const string KeepFile = "keep";
    public const string TargetsFile = "targets.json";
    public const string MaxFile = "max";

    /// <summary>Preset keys a model may not set: they belong to the router, or would reach outside the library.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "model", "hf-repo", "hf-file", "hf", "model-url", "mmproj", "mmproj-url", "host", "port", "api-key", "api-key-file", "alias",
        "path", "ssl-key-file", "ssl-cert-file", "models-dir", "models-preset", "models-max", "models-autoload", "no-models-autoload", "webui", "no-webui",
        "log-file", "slot-save-path", "lora", "lora-scaled", "control-vector", "control-vector-scaled", "model-draft", "spec-draft-model", "hf-repo-draft",
        "chat-template-file", "grammar-file", "json-schema-file", "media-path", "mcp-servers-config", "log-prompts-dir", "lookup-cache-static",
        "lookup-cache-dynamic", "docker-repo", "hf-token", "help", "version", "completion-bash", "list-devices", "cache-list", "offline",
    };

    /// <summary>Preset keys the form sets: a second value in the extra lines would be ambiguous.</summary>
    private static readonly HashSet<string> Managed = new(StringComparer.Ordinal)
    {
        "ctx-size", "n-gpu-layers", "gpu-layers", "n-cpu-moe", "cpu-moe", "cache-type-k", "cache-type-v", "parallel", "jinja", "metrics", "kv-unified", "no-kv-unified",
        "threads", "ubatch-size", "batch-size", "fit", "device", "rope-scaling", "rope-scale", "yarn-orig-ctx", "spec-type", "spec-draft-n-max", "draft-max", "draft-n",
        "temp", "temperature", "top-p", "top-k", "min-p", "presence-penalty", "embedding", "embeddings", "pooling", "rerank", "reranking",
    };

    /// <summary>
    /// The engine's option names (llama.cpp build 10902, Models/engine-options.txt). A preset key
    /// outside them stops the whole engine, not one model, so none gets through.
    /// </summary>
    public static readonly IReadOnlySet<string> EngineOptionNames = LoadOptionNames();

    private static HashSet<string> LoadOptionNames()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("engine-options.txt")
            ?? throw new InvalidOperationException("engine-options.txt is not embedded");
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];
    }

    private string Dir => options.Value.ConfigDir;

    /// <summary>Why a name cannot be a new model, or null.</summary>
    public static string? CheckName(string name) =>
        NameRule().IsMatch(name) ? null : "A name is 1-100 letters, digits and . _ : - (starting with a letter or digit).";

    /// <summary>Why these extra preset lines cannot be used, or null.</summary>
    public static string? CheckExtra(string? extra)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (extra ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }
            var m = PresetLine().Match(line);
            if (!m.Success)
            {
                return $"\"{line}\" is not a preset line: write key = value, with llama-server's long option names (e.g. flash-attn = on).";
            }
            var key = m.Groups[1].Value;
            if (Reserved.Contains(key))
            {
                return $"{key} is set by the engine or reaches outside the model library: it cannot be set here.";
            }
            if (Managed.Contains(key))
            {
                return $"{key} is set by the form above: change it there.";
            }
            if (!EngineOptionNames.Contains(key))
            {
                return $"{key} is not an option of this engine's llama-server: a preset with it would stop the engine. Check the name (llama-server --help).";
            }
            if (!seen.Add(key))
            {
                return $"{key} is there twice.";
            }
        }
        return null;
    }

    /// <summary>A model's section of the engine's presets: the settings of its kind, and only what the engine should not decide itself.</summary>
    public static string Preset(LocalModel m, string library, int? threads = null, ModelProfile? profile = null, int? sessionCacheGb = null)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        var root = library.TrimEnd('/');
        sb.Append('[').Append(m.Name).Append("]\n");
        sb.Append("model = ").Append(root).Append('/').Append(m.File).Append('\n');
        if (m.Projector is { Length: > 0 } p)
        {
            sb.Append("mmproj = ").Append(root).Append('/').Append(p).Append('\n');
        }
        // One cache for the answers in parallel: a conversation can use all of it.
        sb.Append(inv, $"ctx-size = {m.Context}\nparallel = {m.Parallel}\nkv-unified = true\n");
        sb.Append(inv, $"cache-type-k = {m.KvType}\ncache-type-v = {m.KvType}\n");
        if (m.Placement == "manual")
        {
            sb.Append(inv, $"n-gpu-layers = {m.GpuLayers}\n");
            if (m.CpuMoe > 0)
            {
                sb.Append(inv, $"n-cpu-moe = {m.CpuMoe}\n");
            }
        }
        else
        {
            // llama.cpp fits the layers and experts to the GPU memory free when it loads.
            sb.Append("fit = on\n");
        }
        if (Hardware.ParseDevices(m.Devices) is { Count: > 0 } devices)
        {
            // CUDA numbers GPUs as nvidia-smi does: the engine runs with CUDA_DEVICE_ORDER=PCI_BUS_ID.
            sb.Append("device = ").Append(string.Join(',', devices.Select(d => "CUDA" + d.ToString(inv)))).Append('\n');
        }
        if (threads is > 0)
        {
            sb.Append(inv, $"threads = {threads}\n");
        }
        if (m.Ubatch is { } u)
        {
            sb.Append(inv, $"ubatch-size = {u}\n");
            if (u > 2048)
            {
                sb.Append(inv, $"batch-size = {u}\n");
            }
        }
        if (m.Mtp)
        {
            if (m.DraftHead is { Length: > 0 } head)
            {
                sb.Append("model-draft = ").Append(root).Append('/').Append(head).Append('\n');
            }
            sb.Append(inv, $"spec-type = draft-mtp\nspec-draft-n-max = {m.DraftMax}\n");
        }
        if (m.Yarn && profile?.TrainedContext is { } trained && trained > 0 && m.Context > trained)
        {
            sb.Append(inv, $"rope-scaling = yarn\nrope-scale = {Math.Ceiling(m.Context * 100.0 / trained) / 100:0.##}\nyarn-orig-ctx = {trained}\n");
        }
        if (m.Temperature is { } temp)
        {
            sb.Append(inv, $"temp = {temp:0.###}\n");
        }
        if (m.TopP is { } topP)
        {
            sb.Append(inv, $"top-p = {topP:0.###}\n");
        }
        if (m.TopK is { } topK)
        {
            sb.Append(inv, $"top-k = {topK}\n");
        }
        if (m.MinP is { } minP)
        {
            sb.Append(inv, $"min-p = {minP:0.###}\n");
        }
        if (m.PresencePenalty is { } presence)
        {
            sb.Append(inv, $"presence-penalty = {presence:0.###}\n");
        }
        sb.Append("jinja = true\nmetrics = true\n");
        var own = ExtraValues(m.ExtraPreset);
        // A prompt whose start matches what a slot holds reads that part from the cache; with
        // cache-reuse, chunks further on that match too (after a cut in the middle) are shifted
        // into place. The extra lines may set another size, or 0 for none.
        if (!own.ContainsKey("cache-reuse"))
        {
            sb.Append("cache-reuse = 256\n");
        }
        // Each conversation keeps its slot (SlotTable): an idle slot keeps what it holds. Otherwise, with one
        // cache for all slots, llama.cpp moves idle slots to RAM and empties them at every new request,
        // and a hybrid model's never come back. When the cache is full, it still empties idle slots one by one.
        if (!own.ContainsKey("cache-idle-slots") && !own.ContainsKey("no-cache-idle-slots"))
        {
            sb.Append("no-cache-idle-slots = true\n");
        }
        // A hybrid or recurrent model cannot cut its state back to a shorter prompt: it goes back to a
        // checkpoint, a copy of its state in RAM. Four a slot read as fast as 32 (measured), for an eighth of the RAM.
        if (TokenCache.Checkpointed(profile) && !own.ContainsKey("ctx-checkpoints") && !own.ContainsKey("swa-checkpoints"))
        {
            sb.Append(CultureInfo.InvariantCulture, $"ctx-checkpoints = {TokenCache.DefaultCheckpoints}\n");
        }
        // A conversation that lost its slot to another is kept in RAM, its checkpoints too, and comes back from there
        // (measured: six long chats taking turns over four slots read 57-63% of each prompt from the cache, about all
        // there was to read). Engine:SessionCacheGb sizes it; the extra lines may set their own.
        if (sessionCacheGb is { } gb && !own.ContainsKey("cache-ram"))
        {
            sb.Append(CultureInfo.InvariantCulture, $"cache-ram = {Math.Max(0, gb) * 1024}\n");
        }
        foreach (var raw in (m.ExtraPreset ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }
            // Saved before a key was checked: only what the engine knows, and never what the form sets.
            var key = PresetLine().Match(line) is { Success: true } k ? k.Groups[1].Value : null;
            if (key is not null && !Reserved.Contains(key) && !Managed.Contains(key) && EngineOptionNames.Contains(key))
            {
                sb.Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>What a model's extra preset lines set: each key and its value.</summary>
    public static Dictionary<string, string> ExtraValues(string? extra) =>
        (extra ?? "").Split('\n').Select(l => PresetLine().Match(l.Trim())).Where(k => k.Success)
            .GroupBy(k => k.Groups[1].Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim(), StringComparer.Ordinal);

    /// <summary>Writes the presets for the engine; it restarts when they change.</summary>
    public async Task WritePresetsAsync(CancellationToken ct = default)
    {
        var models = await db.LocalModels.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct);
        // Each file's profile: its attention decides the checkpoints, its training the YaRN stretch.
        var profiles = models.Count > 0 ? library.List().ToDictionary(e => e.File.Path, e => e.Profile, StringComparer.Ordinal) : [];
        var text = "# Written by the app (Admin -> Models). The engine restarts when this changes.\n\n" +
            string.Join("\n", models.Select(m => Preset(m, options.Value.EngineLibraryDir, options.Value.Threads, profiles.GetValueOrDefault(m.File), options.Value.SessionCacheGb)));
        Write(PresetsFile, text);
    }

    /// <summary>
    /// The models kept loaded now (loaded at start, and again whenever one is not): the
    /// working-hours window's in force, else the ones admins pinned.
    /// </summary>
    public IReadOnlyList<string> Kept() => hours.Now.Window is { } window ? window.Keep : Pinned();

    /// <summary>The models admins pinned to keep loaded: what is kept outside working hours.</summary>
    public IReadOnlyList<string> Pinned() =>
        Read(KeepFile) is { } text ? [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)] : [];

    public void SetKept(IEnumerable<string> models) => Write(KeepFile, string.Join('\n', models.Distinct(StringComparer.Ordinal)) + "\n");

    /// <summary>How many models the engine may hold at once (it restarts when this changes).</summary>
    public void WriteMax() => Write(MaxFile, options.Value.ModelsMax.ToString(CultureInfo.InvariantCulture) + "\n");

    /// <summary>Prometheus scrapes each loaded model's metrics (/metrics?model=NAME).</summary>
    public void WriteTargets(IEnumerable<string> loaded)
    {
        var host = new Uri(options.Value.Url).Authority;
        var targets = new JsonArray([.. loaded.Select(m => (JsonNode)new JsonObject
        {
            ["targets"] = new JsonArray(host),
            ["labels"] = new JsonObject { ["model"] = m, ["tier"] = "optional", ["component"] = "inference" },
        })]);
        Write(TargetsFile, targets.ToJsonString());
    }

    /// <summary>
    /// Brings the gateway in step with the app: the engine's models added here, and the models
    /// chosen from other GPU servers. Each deployment is known by a fingerprint of everything
    /// it is registered with, so a changed one is replaced and two of one name (a model here
    /// and its copy on a server) are both kept: the gateway spreads requests between them.
    /// Every model carries its prices (its own, else Settings → Prices), so every request has a cost.
    /// </summary>
    public async Task SyncGatewayAsync(CancellationToken ct = default)
    {
        var defaults = prices.CurrentValue;
        var wanted = new Dictionary<string, (string Name, JsonObject Params, JsonObject Info)>(StringComparer.Ordinal);
        // Each chat model on each server first, with what makes it this one and the requests it serves at once.
        var deployments = new List<(string Name, JsonObject Params, JsonObject Info, object?[] Print, int? Slots)>();
        foreach (var m in await db.LocalModels.AsNoTracking().ToListAsync(ct))
        {
            // What its projector reads: pictures, sound (an omni model's), or both.
            var projector = m.Projector is { Length: > 0 } p ? library.Find(p)?.Profile.Projector ?? new ProjectorInfo(true, false, null, null) : null;
            var info = Info(m.Context, m.MaxOutput ?? DefaultMaxOutput(m.Context), projector?.Vision == true, m.Tools, m.Thinking, projector?.Audio == true);
            var litellm = new JsonObject { ["model"] = "openai/" + m.Name, ["api_base"] = "os.environ/ENGINE_API_BASE", ["api_key"] = "os.environ/ENGINE_API_KEY" };
            var price = TokenPrice.Of(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, defaults);
            Prices(litellm, price);
            deployments.Add((m.Name, litellm, info, ["local", m.Name, m.File, m.Context, m.MaxOutput, m.Projector, projector?.Audio, m.Tools, m.Thinking,
                price.Input, price.CachedInput, price.Output], m.Parallel));
        }
        foreach (var server in await db.RemoteServers.AsNoTracking().ToListAsync(ct))
        {
            var key = remote.Unprotect(server.ApiKeyProtected);
            foreach (var m in server.Models)
            {
                var info = Info(m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking);
                info["llm_app_server"] = server.Name;
                var litellm = new JsonObject { ["model"] = "openai/" + m.Remote, ["api_base"] = server.BaseUrl, ["api_key"] = key ?? "none" };
                if (server.BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                {
                    // The public roots, or no check at all.
                    litellm["ssl_verify"] = server.VerifyTls;
                }
                var price = TokenPrice.Of(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, defaults);
                Prices(litellm, price);
                var keyPrint = key is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
                deployments.Add((m.Name, litellm, info, ["remote", server.Id, server.BaseUrl, keyPrint, server.VerifyTls, m.Remote, m.Name, m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking,
                    price.Input, price.CachedInput, price.Output], m.Parallel));
            }
        }
        foreach (var pool in deployments.GroupBy(d => d.Name, StringComparer.Ordinal))
        {
            var size = pool.Count();
            foreach (var d in pool)
            {
                // A model on one server is as it always was; on several, each copy carries its share of the pool.
                wanted[Fingerprint(size == 1 ? d.Print : [.. d.Print, "pool", size, d.Slots])] = (d.Name, size == 1 ? d.Params : Pooled(d.Params, d.Info, size, d.Slots), d.Info);
            }
        }
        await MediaAsync(wanted, defaults, ct);
        var managed = await gateway.ManagedModelsAsync(ct);
        foreach (var old in managed.Where(g => g.Fingerprint is null || !wanted.ContainsKey(g.Fingerprint)))
        {
            await gateway.DeleteModelAsync(old.Id, ct);
        }
        var kept = managed.Where(g => g.Fingerprint is not null && wanted.ContainsKey(g.Fingerprint)).Select(g => g.Fingerprint!).ToHashSet(StringComparer.Ordinal);
        foreach (var (fingerprint, w) in wanted.Where(w => !kept.Contains(w.Key)))
        {
            await gateway.AddModelAsync(w.Name, w.Params, w.Info, fingerprint, ct);
            LogRegistered(logger, w.Name);
        }
        chatModels.Forget();
    }

    /// <summary>The picture and speech models turned on, while their servers run: people's keys reach them too.</summary>
    private async Task MediaAsync(Dictionary<string, (string Name, JsonObject Params, JsonObject Info)> wanted, PriceOptions defaults, CancellationToken ct)
    {
        void Add(string name, string model, string url, JsonObject info)
        {
            var litellm = new JsonObject { ["model"] = model, ["api_base"] = url + "/v1", ["api_key"] = "none" };
            MediaPrices(litellm, info["mode"]!.GetValue<string>(), defaults);
            wanted[Fingerprint("media", name, model, url, info.ToJsonString(), litellm.ToJsonString())] = (name, litellm, info);
        }
        var on = (await media.ListAsync(ct)).Where(x => x.State.Enabled).Select(x => x.Model.Name).ToHashSet(StringComparer.Ordinal);
        if (on.Contains(MediaModels.ImageModel) && await modules.HasAsync("imagegen", ct))
        {
            Add(MediaModels.ImageModel, "openai/sd-cpp-local", MediaModels.ImageUrl, new JsonObject { ["mode"] = "image_generation" });
        }
        if (await modules.HasAsync("audio", ct))
        {
            foreach (var (name, id, mode) in MediaModels.Speech.Where(s => on.Contains(s.Name)))
            {
                Add(name, "openai/" + id, MediaModels.AudioUrl, new JsonObject { ["mode"] = mode });
            }
        }
    }

    /// <summary>
    /// A picture or speech model's price as LiteLLM reads it: per picture, per second of sound
    /// transcribed, per character read aloud. Pictures and speech also carry token prices of 0:
    /// LiteLLM prices a request by its deployment's own entry only when that entry has a token
    /// or per-second price (LiteLLM 1.100, cost_calculator._select_model_name_for_cost_calc).
    /// </summary>
    private static void MediaPrices(JsonObject litellm, string mode, PriceOptions p)
    {
        switch (mode)
        {
            case "image_generation":
                litellm["input_cost_per_image"] = p.PerImage;
                litellm["input_cost_per_token"] = 0m;
                litellm["output_cost_per_token"] = 0m;
                break;
            case "audio_transcription":
                litellm["input_cost_per_second"] = p.PerAudioMinute / 60m;
                break;
            case "audio_speech":
                litellm["input_cost_per_character"] = p.PerThousandCharacters / 1000m;
                litellm["input_cost_per_token"] = 0m;
                litellm["output_cost_per_token"] = 0m;
                break;
        }
    }

    /// <summary>
    /// One copy of a model served by several engines (this machine's and other GPU servers'): the gateway sends each
    /// request to the least busy copy (router_settings in config/litellm.yaml), never more at once than a copy's
    /// slots, and weighs the copies by their slots when it shuffles.
    /// </summary>
    public static JsonObject Pooled(JsonObject litellm, JsonObject info, int copies, int? slots)
    {
        if (slots is > 0 and var s)
        {
            litellm["max_parallel_requests"] = s;
        }
        litellm["weight"] = slots is > 0 ? slots : 1;
        info["llm_app_pool"] = copies;
        return litellm;
    }

    private static JsonObject Info(int? context, int? maxOutput, bool vision, bool tools, bool thinking, bool audio = false)
    {
        var info = new JsonObject
        {
            ["mode"] = "chat", ["supports_vision"] = vision, ["supports_function_calling"] = tools, ["supports_reasoning"] = thinking, ["supports_audio_input"] = audio,
        };
        if (context is { } c)
        {
            info["max_input_tokens"] = c;
            info["max_tokens"] = c;
        }
        if (maxOutput is { } o)
        {
            info["max_output_tokens"] = o;
        }
        return info;
    }

    /// <summary>A chat model's prices, per token: LiteLLM prices a prompt's cached tokens at the cache read price, and at nothing without one.</summary>
    private static void Prices(JsonObject litellm, TokenPrice price)
    {
        litellm["input_cost_per_token"] = price.Input / 1_000_000m;
        litellm["cache_read_input_token_cost"] = price.CachedInput / 1_000_000m;
        litellm["output_cost_per_token"] = price.Output / 1_000_000m;
    }

    /// <summary>The longest answer when none is set: half the context, at most 32,768 tokens.</summary>
    public static int DefaultMaxOutput(int context) => Math.Max(ModelAdvisor.MinOutput, Math.Min(32768, context / 2 / 1024 * 1024));

    private static string Fingerprint(params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))))))[..16];

    private string? Read(string file)
    {
        try
        {
            return File.ReadAllText(Path.Combine(Dir, file)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Atomic, and only when the text changes: the engine restarts on a new presets file.</summary>
    private void Write(string file, string text)
    {
        var path = Path.Combine(Dir, file);
        if (Read(file) == text.Trim())
        {
            return;
        }
        Directory.CreateDirectory(Dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$")]
    private static partial Regex NameRule();

    [GeneratedRegex(@"^([a-z0-9][a-z0-9-]*)\s*=\s*([^\r\n\[\]]+)$")]
    private static partial Regex PresetLine();

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {Model} registered at the gateway")]
    private static partial void LogRegistered(ILogger logger, string model);
}

/// <summary>Who may use which model, and whether it can answer now.</summary>
public sealed class ModelPolicy(AppDbContext db, AccessService access, EngineState engine, ModelCatalog catalog, ModelHoursState hours, IOptions<EngineOptions> options,
    Gateway.Credit credit, IOptionsMonitor<Chat.ChatOptions> chat)
{
    /// <summary>The models on the engine. Their answers need them loaded.</summary>
    public async Task<HashSet<string>> OnEngineAsync(CancellationToken ct = default)
    {
        if (!options.Value.Enabled)
        {
            return [];
        }
        var names = await db.LocalModels.AsNoTracking().Select(m => m.Name).ToListAsync(ct);
        // A model with a copy on another GPU server answers from there while it is not loaded here.
        var elsewhere = (await db.RemoteServers.AsNoTracking().ToListAsync(ct)).SelectMany(s => s.Models).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        return [.. names.Where(n => !elsewhere.Contains(n))];
    }

    /// <summary>The names, of those given, this person may use.</summary>
    public async Task<HashSet<string>> AllowedAsync(AppUser user, IEnumerable<string> models, CancellationToken ct = default)
    {
        var member = await access.MembershipAsync(user, ct);
        var rules = await db.ModelAccess.AsNoTracking().ToDictionaryAsync(r => r.Model, ct);
        return [.. models.Where(m => !rules.TryGetValue(m, out var r) || member.May(r.Audience, r.Groups))];
    }

    /// <summary>Whether a model is there to answer: always, unless it is the engine's and not loaded.</summary>
    public bool Loaded(string model, IReadOnlySet<string> onEngine) =>
        !onEngine.Contains(model) || engine.Now is not { Error: null, At: not null } || engine.StatusOf(model) == "loaded";

    /// <summary>
    /// An engine model that is not loaded but loads when asked for: one of those that never make room (those kept
    /// loaded, the one new chats use, the one for small steps while it keeps a place: EngineState.Held), which has its
    /// place unless an admin unloaded it, or another while the engine has a place besides theirs (an idle one that may
    /// makes room: EngineRoute). One that failed to load does too once its wait is over (EngineState.MayAsk).
    /// </summary>
    public bool OnRequest(string model, IReadOnlySet<string> onEngine) =>
        onEngine.Contains(model) && !Loaded(model, onEngine) && (engine.StatusOf(model) is "unloaded" or "loading" || engine.MayAsk(model))
        && ((engine.Held.Contains(model) && !engine.WasDropped(model)) || PlaceLeft);

    /// <summary>
    /// Whether the engine has a place for any other model: the places of those that never make room (the models kept
    /// loaded, the one new chats use, the one for small steps while it keeps one), loaded or kept for them until they load
    /// again, are fewer than it holds at once. One an admin unloaded gives its place up until it loads again.
    /// </summary>
    public bool PlaceLeft =>
        catalog.Kept().Concat(engine.Held.Where(h => !engine.WasDropped(h))).Distinct(StringComparer.Ordinal).Count() < options.Value.ModelsMax;

    /// <summary>Whether a model can answer: loaded, or loaded on request.</summary>
    public bool Ready(string model, IReadOnlySet<string> onEngine) => Loaded(model, onEngine) || OnRequest(model, onEngine);

    /// <summary>
    /// The chat's models this person may use, and the one a chat uses when it chose none: the
    /// working hours' default when it can answer, else the admin's (Chat:DefaultModel) when it can
    /// answer (loaded, or loaded when asked), else the first loaded, then the first that loads when
    /// asked, the model for small steps last. A model that happens to be loaded (one person's agent
    /// asked for the small model) never takes the admin's choice away from everyone.
    /// </summary>
    public async Task<(IReadOnlyList<GatewayModel> Models, GatewayModel? Default, HashSet<string> OnEngine)> ForAsync(AppUser user, IReadOnlyList<GatewayModel> chatModels, CancellationToken ct = default)
    {
        var allowed = await AllowedAsync(user, chatModels.Select(m => m.Name), ct);
        var onEngine = await OnEngineAsync(ct);
        var mine = chatModels.Where(m => allowed.Contains(m.Name)).ToList();
        return (mine, DefaultOf(mine, onEngine), onEngine);
    }

    /// <summary>
    /// The model a chat that chose none uses, of <paramref name="mine"/> (ForAsync): the working hours' default
    /// when it can answer, else the admin's, else the one new chats have been using while it can answer (the
    /// watcher keeps it: EngineState.Default; unloaded by the engine, it keeps its place and loads again), else
    /// the first loaded, then the first that loads when asked, the model for small steps last.
    /// </summary>
    public GatewayModel? DefaultOf(IReadOnlyList<GatewayModel> mine, IReadOnlySet<string> onEngine)
    {
        var hoursDefault = hours.Now.Window?.DefaultModel is { } preferred ? mine.FirstOrDefault(m => m.Name == preferred && Ready(m.Name, onEngine)) : null;
        var o = chat.CurrentValue;
        var adminDefault = o.DefaultModel is { Length: > 0 } chosen && chosen != Chat.SmallModel.Auto ? mine.FirstOrDefault(m => m.Name == chosen && Ready(m.Name, onEngine)) : null;
        var others = mine.Where(m => m.Name != o.SmallModel).ToList();
        // With none named: a model loaded on request that the list puts first, or that took the place of the one everyone is on
        // while the engine had it unloaded, does not become theirs.
        var usual = engine.Default is { } d && !engine.WasDropped(d) ? others.FirstOrDefault(m => m.Name == d && onEngine.Contains(d) && Ready(d, onEngine)) : null;
        return hoursDefault ?? adminDefault ?? usual
            ?? others.FirstOrDefault(m => Loaded(m.Name, onEngine)) ?? others.FirstOrDefault(m => Ready(m.Name, onEngine))
            ?? mine.FirstOrDefault(m => Loaded(m.Name, onEngine)) ?? mine.FirstOrDefault(m => Ready(m.Name, onEngine)) ?? (mine.Count > 0 ? mine[0] : null);
    }

    /// <summary>Why this person cannot have an answer from this model now, or null.</summary>
    public async Task<string?> RefusalAsync(AppUser user, string model, CancellationToken ct = default)
    {
        if (!(await AllowedAsync(user, [model], ct)).Contains(model))
        {
            return $"You may not use {model}. Choose another model.";
        }
        // Only when the engine answered its last check: an engine that cannot be reached says so in its own error.
        var onEngine = await OnEngineAsync(ct);
        if (onEngine.Contains(model) && engine.Now is { Error: null, At: not null } && !Ready(model, onEngine))
        {
            return engine.StatusOf(model) == "loading"
                ? $"{model} is loading. Try again in a minute, or choose another model."
                : engine.StatusOf(model) == "failed"
                    ? $"{model} could not be loaded just now. Choose another model, or try again in a few minutes; an admin can see why under Admin → Models."
                    : $"{model} is not loaded right now, and the engine has no place for it: each place is taken by, or kept for, a model kept loaded or used "
                      + "by everyone (the model new chats use, the one for small steps). Choose another model, or ask an admin to raise Models loaded at once.";
        }
        // One credit over the chat and API keys, and the groups' credit.
        return await credit.RefusalAsync(user, ct);
    }
}
