using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// One vLLM process the supervisor runs: a model, or one copy of it ("NAME@0", "NAME@1"), its port, GPUs, whether it
/// should sleep, its `vllm serve` arguments, and its answers at once (--max-num-seqs).
/// </summary>
public sealed record VllmEntry(string Model, string Name, int Port, IReadOnlyList<int> Gpus, bool Sleep, IReadOnlyList<string> Args, int Parallel)
{
    public JsonObject ToJson() => new()
    {
        ["model"] = Model, ["name"] = Name, ["port"] = Port, ["gpus"] = new JsonArray([.. Gpus.Select(g => (JsonNode)g)]), ["sleep"] = Sleep,
        ["args"] = new JsonArray([.. Args.Select(a => (JsonNode)a)]), ["parallel"] = Parallel,
    };

    public static VllmEntry? From(JsonObject o)
    {
        try
        {
            return new VllmEntry(o["model"]?.GetValue<string>() ?? o["name"]!.GetValue<string>(), o["name"]!.GetValue<string>(), o["port"]!.GetValue<int>(),
                [.. (o["gpus"] as JsonArray ?? []).Select(g => g!.GetValue<int>())], o["sleep"]?.GetValue<bool>() ?? false,
                [.. (o["args"] as JsonArray ?? []).Select(a => a!.GetValue<string>())], o["parallel"]?.GetValue<int>() ?? 1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }
}

/// <summary>
/// A vLLM engine as the app drives it, through files and HTTP with no Docker socket (services/vllm/supervisor.sh): the
/// models it may run are its presets (presets.json, written by ModelCatalog), the ones it runs now its desired state
/// (models.json: loading a model adds its processes, unloading removes them, sleep flips their switch), and what each
/// does is its status file. Its health is the supervisor's heartbeat; what a model is doing, its /metrics
/// (vllm:num_requests_running and _waiting, the cache's use, prefix hits).
/// </summary>
public sealed class VllmBackend(VllmEngineOptions instance, EngineOptions engine, IHttpClientFactory http, TimeProvider clock) : IEngineBackend
{
    public const string PresetsFile = "presets.json";
    public const string DesiredFile = "models.json";

    /// <summary>The supervisor's heartbeat older than this: the engine is not running.</summary>
    public static readonly TimeSpan Silent = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim Writing = new(1, 1);

    public string Name => instance.Name;

    public EngineCapabilities Capabilities => EngineKinds.VllmCapabilities;

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
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
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
            throw new EngineException($"The vLLM engine {Name}'s desired state cannot be written ({ex.Message}).");
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
                return $"The vLLM engine {Name} is not running (no supervisor has written to {StatusDir}): start it (COMPOSE_PROFILES=vllm, or vllm.enabled on Kubernetes).";
            }
            var at = JsonNode.Parse(File.ReadAllText(path))?["at"]?.GetValue<long>() ?? 0;
            var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(at);
            return age > Silent ? $"The vLLM engine {Name} has said nothing for {(int)age.TotalSeconds} s: its supervisor is down." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return $"The vLLM engine {Name}'s heartbeat cannot be read ({ex.Message}).";
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
            throw new EngineException($"The vLLM engine {Name} has no model named {model}.");
        }
        await ChangeAsync(d => [.. d.Where(e => e.Model != model), .. entries.Select(e => e with { Sleep = false })], ct);
    }

    /// <summary>Its processes out of the desired state: the supervisor drains them (vLLM ends the answers it has).</summary>
    public Task UnloadAsync(string model, CancellationToken ct) => ChangeAsync(d => [.. d.Where(e => e.Model != model)], ct);

    /// <summary>Its processes asleep (weights to RAM, GPUs free) or awake; refused for one not loaded, or one started without sleep mode.</summary>
    public async Task SleepAsync(string model, bool sleep, CancellationToken ct)
    {
        var mine = Desired().Where(e => e.Model == model).ToList();
        if (mine.Count == 0)
        {
            throw new EngineException($"{model} is not loaded on {Name}.");
        }
        if (sleep && mine.Any(e => !e.Args.Contains("--enable-sleep-mode")))
        {
            throw new EngineException($"{model} may not sleep (its Sleep setting is off): it unloads instead.");
        }
        await ChangeAsync(d => [.. d.Select(e => e.Model == model ? e with { Sleep = sleep } : e)], ct);
    }

    /// <summary>
    /// What its copies are doing, from their /metrics: requests running and waiting, the cache's use (the most of any
    /// copy) and the share of prompt tokens read from it. Its places are its answers at once times its copies; the first
    /// that many as are running count busy (vLLM has no slots: any request reuses any cached prefix).
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
            var m = Metrics(text);
            running += (int)m.GetValueOrDefault("vllm:num_requests_running");
            waiting += (int)m.GetValueOrDefault("vllm:num_requests_waiting");
            if (m.TryGetValue("vllm:kv_cache_usage_perc", out var u) || m.TryGetValue("vllm:gpu_cache_usage_perc", out u))
            {
                usage = Math.Max(usage ?? 0, u);
            }
            queries += m.GetValueOrDefault("vllm:prefix_cache_queries_total");
            hits += m.GetValueOrDefault("vllm:prefix_cache_hits_total");
        }
        var count = entries.Sum(e => Math.Max(1, e.Parallel));
        return new EngineCapacity(count, Enumerable.Range(0, Math.Min(running, count)).ToHashSet(), running, waiting, usage, queries > 0 ? hits / queries : null);
    }

    /// <summary>Prometheus text as each metric's sum over its labels.</summary>
    public static Dictionary<string, double> Metrics(string text)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
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

    public Task<EngineHealth> HealthAsync(CancellationToken ct) =>
        Task.FromResult(Down() is { } why ? new EngineHealth(false, why, clock.GetUtcNow()) : new EngineHealth(true, null, clock.GetUtcNow()));

    /// <summary>The HTTP client for its models' metrics.</summary>
    public const string Client = "vllm";
}

/// <summary>The vLLM engines of this stack (Engine:Vllm), one backend each.</summary>
public sealed class VllmBackends(IOptions<EngineOptions> options, IHttpClientFactory http, TimeProvider clock)
{
    private readonly Lazy<IReadOnlyList<VllmBackend>> _all = new(() =>
        [.. options.Value.VllmEngines.Select(i => new VllmBackend(i, options.Value, http, clock))]);

    public IReadOnlyList<VllmBackend> All => _all.Value;

    /// <summary>The engine a vLLM model runs on: the one it names, else the first.</summary>
    public VllmBackend? For(string? engine) => All.FirstOrDefault(b => b.Name == engine) ?? (engine is null && All.Count > 0 ? All[0] : null);

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
