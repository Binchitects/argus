using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// One vLLM or SGLang process the supervisor runs: a model, or one copy of it ("NAME@0", "NAME@1"), its port, GPUs, whether
/// it should sleep, its server's arguments, and its answers at once (vLLM's --max-num-seqs, SGLang's --max-running-requests).
/// </summary>
/// <param name="Env">More environment for its process (LMCACHE_CONFIG_FILE): never a secret (the supervisor refuses keys and tokens).</param>
public sealed record VllmEntry(string Model, string Name, int Port, IReadOnlyList<int> Gpus, bool Sleep, IReadOnlyList<string> Args, int Parallel,
    IReadOnlyDictionary<string, string>? Env = null)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["model"] = Model, ["name"] = Name, ["port"] = Port, ["gpus"] = new JsonArray([.. Gpus.Select(g => (JsonNode)g)]), ["sleep"] = Sleep,
            ["args"] = new JsonArray([.. Args.Select(a => (JsonNode)a)]), ["parallel"] = Parallel,
        };
        if (Env is { Count: > 0 } env)
        {
            o["env"] = new JsonObject([.. env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value))]);
        }
        return o;
    }

    public static VllmEntry? From(JsonObject o)
    {
        try
        {
            return new VllmEntry(o["model"]?.GetValue<string>() ?? o["name"]!.GetValue<string>(), o["name"]!.GetValue<string>(), o["port"]!.GetValue<int>(),
                [.. (o["gpus"] as JsonArray ?? []).Select(g => g!.GetValue<int>())], o["sleep"]?.GetValue<bool>() ?? false,
                [.. (o["args"] as JsonArray ?? []).Select(a => a!.GetValue<string>())], o["parallel"]?.GetValue<int>() ?? 1,
                o["env"] is JsonObject env ? env.ToDictionary(e => e.Key, e => e.Value!.GetValue<string>(), StringComparer.Ordinal) : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }
}

/// <summary>
/// A vLLM or SGLang engine as the app drives it, through files and HTTP with no Docker socket (services/vllm/supervisor.sh,
/// the same for both): the models it may run are its presets (presets.json, written by ModelCatalog), the ones it runs now
/// its desired state (models.json: loading a model adds its processes, unloading removes them, sleep flips their switch),
/// and what each does is its status file. Its health is the supervisor's heartbeat; what a model is doing, its /metrics
/// (vLLM's vllm:num_requests_running and _waiting, SGLang's sglang:num_running_reqs and num_queue_reqs; the cache's use,
/// prefix hits).
/// </summary>
public sealed class VllmBackend(VllmEngineOptions instance, EngineOptions engine, IHttpClientFactory http, TimeProvider clock) : IEngineBackend
{
    public const string PresetsFile = "presets.json";
    public const string DesiredFile = "models.json";

    /// <summary>The supervisor's heartbeat older than this: the engine is not running.</summary>
    public static readonly TimeSpan Silent = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim Writing = new(1, 1);

    public string Name => instance.Name;

    public EngineCapabilities Capabilities => Kind == EngineKinds.Sglang ? EngineKinds.SglangCapabilities : EngineKinds.VllmCapabilities;

    /// <summary>What it runs: vLLM or SGLang.</summary>
    public string Kind => instance.Kind == EngineKinds.Sglang ? EngineKinds.Sglang : EngineKinds.Vllm;

    /// <summary>"vLLM" or "SGLang", for what it says.</summary>
    public string Label => Capabilities.Label;

    public string ConfigDir => instance.ConfigDir is { Length: > 0 } d ? d : Path.Combine(engine.ConfigDir, instance.Name);

    public string StatusDir => instance.StatusDir is { Length: > 0 } d ? d : Path.Combine(engine.VllmStatusDir, instance.Name);

    /// <summary>The host the app and the gateway reach its models on.</summary>
    public string Host => instance.Host is { Length: > 0 } h ? h : instance.Name;

    public int FirstPort => instance.FirstPort;

    /// <summary>The models it may run, each as its processes.</summary>
    public IReadOnlyList<VllmEntry> Presets() => Read(PresetsFile);

    /// <summary>The processes it is to run now.</summary>
    public IReadOnlyList<VllmEntry> Desired() => Read(DesiredFile);

    /// <summary>Whether it has <paramref name="model"/> among its presets.</summary>
    public bool Has(string model) => Presets().Any(e => e.Model == model);

    private IReadOnlyList<VllmEntry> Read(string file)
    {
        try
        {
            var path = Path.Combine(ConfigDir, file);
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["models"] is JsonArray list
                ? [.. list.OfType<JsonObject>().Select(VllmEntry.From).OfType<VllmEntry>()]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes a file of entries, whole (renamed into place), only when it changes.</summary>
    public void Write(string file, IEnumerable<VllmEntry> entries)
    {
        var text = new JsonObject { ["models"] = new JsonArray([.. entries.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => (JsonNode)e.ToJson())]) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        Directory.CreateDirectory(ConfigDir);
        var path = Path.Combine(ConfigDir, file);
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            return;
        }
        Replace(path, text);
    }

    /// <summary>
    /// A file's whole text renamed into place, from a file of this write's own: two writes at once (the catalog and a
    /// replica's watcher) each rename theirs, the last one wins, and neither finds the other's gone.
    /// </summary>
    private static void Replace(string path, string text)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Writes another file of its presets (LMCache's configuration), only when it changes.</summary>
    public void WriteText(string file, string text)
    {
        Directory.CreateDirectory(ConfigDir);
        var path = Path.Combine(ConfigDir, file);
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            return;
        }
        Replace(path, text);
    }

    /// <summary>Changes the desired state under one lock (this replica's): what is read, changed and written is one step.</summary>
    private async Task ChangeAsync(Func<List<VllmEntry>, List<VllmEntry>> change, CancellationToken ct)
    {
        await Writing.WaitAsync(ct);
        try
        {
            Write(DesiredFile, change([.. Desired()]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EngineException($"The {Label} engine {Name}'s desired state cannot be written ({ex.Message}).");
        }
        finally
        {
            Writing.Release();
        }
    }

    /// <summary>What the supervisor says of one process: its phase and the rest of its status file; null when it says nothing.</summary>
    public JsonObject? StatusOf(string entry)
    {
        try
        {
            var path = Path.Combine(StatusDir, entry + ".json");
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Why the engine is not running (its supervisor's heartbeat missing or old), or null.</summary>
    public string? Down()
    {
        try
        {
            var path = Path.Combine(StatusDir, "supervisor.json");
            if (!File.Exists(path))
            {
                return $"The {Label} engine {Name} is not running (no supervisor has written to {StatusDir}): start it (COMPOSE_PROFILES={Kind}, or {Kind}.enabled on Kubernetes).";
            }
            var at = JsonNode.Parse(File.ReadAllText(path))?["at"]?.GetValue<long>() ?? 0;
            var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(at);
            return age > Silent ? $"The {Label} engine {Name} has said nothing for {(int)age.TotalSeconds} s: its supervisor is down." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return $"The {Label} engine {Name}'s heartbeat cannot be read ({ex.Message}).";
        }
    }

    /// <summary>
    /// Each model it may run, as its processes stand: loaded when every copy is ready, sleeping when they sleep, loading
    /// while one starts (or the supervisor has not seen it yet), failed when one failed, unloading while one not wanted
    /// drains, else unloaded. With the supervisor down, every one is unloaded (Admin → Models says why).
    /// </summary>
    public Task<IReadOnlyList<EngineModel>> ListAsync(CancellationToken ct)
    {
        var down = Down() is not null;
        var desired = Desired().ToLookup(e => e.Model);
        var list = new List<EngineModel>();
        foreach (var model in Presets().GroupBy(e => e.Model))
        {
            if (down)
            {
                list.Add(new EngineModel(model.Key, "unloaded"));
                continue;
            }
            var phases = model.Select(e => StatusOf(e.Name)?["phase"]?.GetValue<string>()).ToList();
            string status;
            if (!desired.Contains(model.Key))
            {
                status = phases.Any(p => p is "draining" or "ready" or "loading" or "sleeping") ? "unloading" : "unloaded";
            }
            else if (phases.Any(p => p == "failed"))
            {
                status = "failed";
            }
            else if (phases.All(p => p == "ready"))
            {
                status = "loaded";
            }
            else if (phases.All(p => p is "sleeping" or "ready") && phases.Any(p => p == "sleeping"))
            {
                status = "sleeping";
            }
            else
            {
                status = "loading";
            }
            list.Add(new EngineModel(model.Key, status));
        }
        return Task.FromResult<IReadOnlyList<EngineModel>>(list);
    }

    /// <summary>Its processes into the desired state, awake; the supervisor starts them (or wakes them) within seconds.</summary>
    public async Task LoadAsync(string model, CancellationToken ct)
    {
        if (Down() is { } why)
        {
            throw new EngineException(why);
        }
        var entries = Presets().Where(e => e.Model == model).ToList();
        if (entries.Count == 0)
        {
            throw new EngineException($"The {Label} engine {Name} has no model named {model}.");
        }
        await ChangeAsync(d => [.. d.Where(e => e.Model != model), .. entries.Select(e => e with { Sleep = false })], ct);
    }

    /// <summary>Its processes out of the desired state: the supervisor drains them (each server ends the answers it has).</summary>
    public Task UnloadAsync(string model, CancellationToken ct) => ChangeAsync(d => [.. d.Where(e => e.Model != model)], ct);

    /// <summary>The argument a model is started with to sleep: vLLM's sleep mode, SGLang's memory saver.</summary>
    public static string SleepFlag(string kind) => kind == EngineKinds.Sglang ? "--enable-memory-saver" : "--enable-sleep-mode";

    /// <summary>Its processes asleep (weights to RAM, GPUs free) or awake; refused for one not loaded, or one started without sleep mode.</summary>
    public async Task SleepAsync(string model, bool sleep, CancellationToken ct)
    {
        var mine = Desired().Where(e => e.Model == model).ToList();
        if (mine.Count == 0)
        {
            throw new EngineException($"{model} is not loaded on {Name}.");
        }
        if (sleep && mine.Any(e => !e.Args.Contains(SleepFlag(Kind))))
        {
            throw new EngineException($"{model} may not sleep (its Sleep setting is off): it unloads instead.");
        }
        await ChangeAsync(d => [.. d.Select(e => e.Model == model ? e with { Sleep = sleep } : e)], ct);
    }

    /// <summary>
    /// What its copies are doing, from their /metrics: requests running and waiting, the cache's use (the most of any
    /// copy) and the share of prompt tokens read from it. Its places are its answers at once times its copies; the first
    /// that many as are running count busy (no slots: any request reuses any cached prefix).
    /// </summary>
    public async Task<EngineCapacity?> CapacityAsync(string model, TimeSpan patience, CancellationToken ct)
    {
        var entries = Desired().Where(e => e.Model == model).ToList();
        if (entries.Count == 0)
        {
            return null;
        }
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(patience);
        int running = 0, waiting = 0;
        double? usage = null;
        double queries = 0, hits = 0;
        foreach (var e in entries)
        {
            string text;
            try
            {
                using var res = await http.CreateClient(Client).GetAsync(new Uri($"http://{Host}:{e.Port}/metrics"), quick.Token);
                if (!res.IsSuccessStatusCode)
                {
                    return null;
                }
                text = await res.Content.ReadAsStringAsync(quick.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return null;
            }
            if (Kind == EngineKinds.Sglang)
            {
                var m = Metrics(text, SglangTotal);
                // SGLang's: its token pool's use, and its radix cache's hit rate (a share already, so weighed by the copy's prompt tokens).
                running += (int)m.GetValueOrDefault("sglang:num_running_reqs");
                waiting += (int)m.GetValueOrDefault("sglang:num_queue_reqs");
                if (m.TryGetValue("sglang:token_usage", out var t))
                {
                    usage = Math.Max(usage ?? 0, t);
                }
                if (m.TryGetValue("sglang:cache_hit_rate", out var rate))
                {
                    var prompt = Math.Max(1, m.GetValueOrDefault("sglang:prompt_tokens_total"));
                    queries += prompt;
                    hits += rate * prompt;
                }
                continue;
            }
            var v = Metrics(text);
            running += (int)v.GetValueOrDefault("vllm:num_requests_running");
            waiting += (int)v.GetValueOrDefault("vllm:num_requests_waiting");
            if (v.TryGetValue("vllm:kv_cache_usage_perc", out var u) || v.TryGetValue("vllm:gpu_cache_usage_perc", out u))
            {
                usage = Math.Max(usage ?? 0, u);
            }
            queries += v.GetValueOrDefault("vllm:prefix_cache_queries_total");
            hits += v.GetValueOrDefault("vllm:prefix_cache_hits_total");
        }
        var count = entries.Sum(e => Math.Max(1, e.Parallel));
        return new EngineCapacity(count, Enumerable.Range(0, Math.Min(running, count)).ToHashSet(), running, waiting, usage, queries > 0 ? hits / queries : null);
    }

    /// <summary>
    /// SGLang's series to count: its totals (with priority scheduling each gauge is also written per priority, under
    /// priority="-1" and so on; the total under priority="") of its first tensor parallel rank (each rank may write its own).
    /// </summary>
    public static bool SglangTotal(IReadOnlyDictionary<string, string> labels) =>
        labels.GetValueOrDefault("priority", "") == "" && labels.GetValueOrDefault("tp_rank", "0") == "0";

    /// <summary>Prometheus text as each metric's sum over its labels: of the series <paramref name="keep"/> keeps, else of all.</summary>
    public static Dictionary<string, double> Metrics(string text, Func<IReadOnlyDictionary<string, string>, bool>? keep = null)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (keep is not null && !keep(Labels(line)))
            {
                continue;
            }
            var name = line.Split('{', ' ')[0];
            var value = line[(line.LastIndexOf(' ') + 1)..];
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                sums[name] = sums.GetValueOrDefault(name) + v;
            }
        }
        return sums;
    }

    /// <summary>A Prometheus line's labels (name="value", quotes escaped as Prometheus writes them).</summary>
    private static Dictionary<string, string> Labels(string line)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var open = line.IndexOf('{', StringComparison.Ordinal);
        var close = line.LastIndexOf('}');
        if (open < 0 || close < open)
        {
            return labels;
        }
        var body = line[(open + 1)..close];
        var i = 0;
        while (i < body.Length)
        {
            var eq = body.IndexOf('=', i);
            if (eq < 0 || eq + 1 >= body.Length || body[eq + 1] != '"')
            {
                break;
            }
            var key = body[i..eq].Trim().TrimStart(',').Trim();
            var value = new System.Text.StringBuilder();
            var j = eq + 2;
            for (; j < body.Length && body[j] != '"'; j++)
            {
                if (body[j] == '\\' && j + 1 < body.Length)
                {
                    j++;
                }
                value.Append(body[j]);
            }
            labels[key] = value.ToString();
            i = j + 1;
        }
        return labels;
    }

    public Task<EngineHealth> HealthAsync(CancellationToken ct) =>
        Task.FromResult(Down() is { } why ? new EngineHealth(false, why, clock.GetUtcNow()) : new EngineHealth(true, null, clock.GetUtcNow()));

    /// <summary>The HTTP client for its models' metrics.</summary>
    public const string Client = "vllm";
}

/// <summary>The vLLM and SGLang engines of this stack (Engine:Vllm), one backend each.</summary>
public sealed class VllmBackends(IOptions<EngineOptions> options, IHttpClientFactory http, TimeProvider clock)
{
    private readonly Lazy<IReadOnlyList<VllmBackend>> _all = new(() =>
        [.. options.Value.VllmEngines.Select(i => new VllmBackend(i, options.Value, http, clock))]);

    public IReadOnlyList<VllmBackend> All => _all.Value;

    /// <summary>The engine a vLLM or SGLang model runs on: the one it names, else the first of its kind.</summary>
    public VllmBackend? For(string? engine, string kind) =>
        engine is null ? All.FirstOrDefault(b => b.Kind == kind) : All.FirstOrDefault(b => b.Name == engine && b.Kind == kind);

    /// <summary>The engines of a kind.</summary>
    public IEnumerable<VllmBackend> OfKind(string kind) => All.Where(b => b.Kind == kind);

    /// <summary>The engine that has this model among its presets, or null.</summary>
    public VllmBackend? Find(string model) => All.FirstOrDefault(b => b.Has(model));
}

/// <summary>
/// The OpenAI-compatible servers the app does not run (Admin → Models → Other GPU servers: another vLLM, SGLang, TGI,
/// Ollama, a cloud): registered at the gateway and checked that they answer, never loaded or unloaded from here.
/// </summary>
public sealed class ExternalBackend(IServiceScopeFactory scopes, RemoteHealth health, TimeProvider clock) : IEngineBackend
{
    public string Name => "external";

    public EngineCapabilities Capabilities => EngineKinds.ExternalCapabilities;

    /// <summary>Each server's models: loaded while it answers, failed while it does not.</summary>
    public async Task<IReadOnlyList<EngineModel>> ListAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        var list = new List<EngineModel>();
        foreach (var server in await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.RemoteServers, ct))
        {
            var up = (await health.StatusAsync(server, ct)).Up;
            list.AddRange(server.Models.Select(m => new EngineModel(m.Name, up ? "loaded" : "failed")));
        }
        return list;
    }

    private static Task Refuse() => throw new EngineException("A server the app does not run is loaded and unloaded where it runs.");

    public Task LoadAsync(string model, CancellationToken ct) => Refuse();

    public Task UnloadAsync(string model, CancellationToken ct) => Refuse();

    public Task SleepAsync(string model, bool sleep, CancellationToken ct) => Refuse();

    public Task<EngineCapacity?> CapacityAsync(string model, TimeSpan patience, CancellationToken ct) => Task.FromResult<EngineCapacity?>(null);

    public async Task<EngineHealth> HealthAsync(CancellationToken ct)
    {
        var down = (await ListAsync(ct)).Where(m => m.Status == "failed").Select(m => m.Name).ToList();
        return new EngineHealth(down.Count == 0, down.Count == 0 ? null : $"Not answering: {string.Join(", ", down)}.", clock.GetUtcNow());
    }
}
