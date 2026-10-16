using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Core.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>Configuration section "Engine": llama.cpp in router mode, the vLLM engines, and the files the app shares with them.</summary>
public sealed class EngineOptions
{
    /// <summary>False only where no engine runs (tests): the other GPU servers' models still reach the gateway.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Whether llama.cpp's router runs (Helm: llamacpp.enabled); false: the models are vLLM's alone.</summary>
    public bool LlamaCpp { get; set; } = true;
    /// <summary>
    /// The vLLM engines, one per machine (or per Helm instance); none listed: one named "vllm" on this stack. Each has
    /// its desired state under <see cref="ConfigDir"/>/NAME and its status under <see cref="VllmStatusDir"/>/NAME.
    /// </summary>
    public List<VllmEngineOptions> Vllm { get; set; } = [];
    /// <summary>How long a request waits for a vLLM model it started (a large one loads in minutes).</summary>
    public TimeSpan VllmStartWait { get; set; } = TimeSpan.FromMinutes(20);
    /// <summary>Where the vLLM supervisors say what they do (services/vllm/supervisor.sh's status, each in its own folder).</summary>
    public string VllmStatusDir { get; set; } = "/vllm-status";

    /// <summary>The vLLM engines as configured, else the one of this stack.</summary>
    public IReadOnlyList<VllmEngineOptions> VllmEngines => Vllm.Count > 0 ? Vllm : [new VllmEngineOptions()];
    public string Url { get; set; } = "http://llamacpp:8080";
    /// <summary>ENGINE_KEY.</summary>
    public string? ApiKey { get; set; }
    /// <summary>MODEL in .env: the first chat model (Hugging Face repo:quant, or a file in the library), added once its files are in the library.</summary>
    public string? FirstModel { get; set; }
    /// <summary>
    /// DEFAULT_MODELS in .env, the installer's choice: "download" has the app download the default models once, at its first
    /// start (Provisioning); anything else, nothing is downloaded but by an admin (Admin → Models → Default models).
    /// </summary>
    public string? DefaultModels { get; set; }
    /// <summary>Where the app writes the engine's presets, the models to keep loaded, how many at once, and Prometheus's targets.</summary>
    public string ConfigDir { get; set; } = "/engine-config";
    /// <summary>Where the picture and video servers say what they do (services/sd-serve.sh's status files; read only).</summary>
    public string MediaStatusDir { get; set; } = "/media-status";
    /// <summary>The model library as the app sees it (MODELS_DIR, writable: downloads land there).</summary>
    public string LibraryDir { get; set; } = "/library";
    /// <summary>The same library inside the engine container.</summary>
    public string EngineLibraryDir { get; set; } = "/library";
    /// <summary>CPU threads for every model; null: llama.cpp's own choice.</summary>
    public int? Threads { get; set; }
    /// <summary>GPU memory the picture server may take beside the engine (its --max-vram), on the GPU it runs on.</summary>
    public long ImageReserveBytes { get; set; } = 2L << 30;
    /// <summary>
    /// The GPU the picture server runs on when its model names none (MEDIA_GPU); null: the first GPU. A picture model's own
    /// GPU (Admin → Models) goes before it.
    /// </summary>
    public int? MediaGpu { get; set; }
    /// <summary>The GPU the embedder runs on (EMBED_GPU); null: the CPU, where it keeps nothing on a GPU.</summary>
    public int? EmbedGpu { get; set; }
    /// <summary>What the embedder keeps on its GPU when it runs on one.</summary>
    public long EmbedReserveBytes { get; set; } = 1L << 30;
    /// <summary>
    /// The backend llama.cpp's image drives the GPUs with (LLAMACPP_BACKEND): cuda, rocm, vulkan, sycl, metal, or cpu.
    /// It names the GPUs in the presets (CUDA0, ROCm0, Vulkan0...); with cpu, the machine has no GPU for the models.
    /// </summary>
    public string Backend { get; set; } = "cuda";
    /// <summary>
    /// Where an engine on the GPUs says what they are (services/gpu-probe.py: nvidia-smi, rocm-smi, or nothing), read
    /// when Prometheus has no GPU figures: a machine with AMD GPUs, or without a GPU exporter.
    /// </summary>
    public string HardwareFile { get; set; } = "/vllm-status/vllm/hardware.json";
    /// <summary>RAM kept for everything but the engine.</summary>
    public long RamReserveBytes { get; set; } = 8L << 30;
    /// <summary>The picture model's folder in the library: its text encoder there is no chat model.</summary>
    public string ImageModelDir { get; set; } = MediaModels.ImageDir;
    public string ImageTextEncoder { get; set; } = MediaModels.ImageTextEncoder;
    /// <summary>
    /// How many models may be loaded at once, those kept loaded included (Settings, Engine). Two: a small
    /// model asked for loads beside the big one (in what is left of the GPU, else in RAM) instead of
    /// unloading it for everyone.
    /// </summary>
    public int ModelsMax { get; set; } = 2;

    /// <summary>
    /// RAM each loaded model keeps for the conversations that lost their slot to another (llama.cpp's cache-ram), in GB:
    /// one coming back reads from there what it had, not its whole history again. 8 as llama.cpp's own; 0: none.
    /// </summary>
    public int SessionCacheGb { get; set; } = 8;
}

/// <summary>
/// A model of an engine: "loaded", "loading", "unloading", "unloaded", "failed" (its last load did not start), or
/// "sleeping" (vLLM: its weights in RAM, its GPUs lent; it wakes in seconds).
/// </summary>
public sealed record EngineModel(string Name, string Status);

public sealed class EngineException(string message) : Exception(message);

/// <summary>
/// What a loaded model is doing, as its engine says: its places (llama.cpp's slots, vLLM's --max-num-seqs), which are busy
/// (llama.cpp's own slots; for vLLM the first <see cref="Running"/>, as it has no slots), requests running and waiting
/// inside the engine, and for vLLM its cache's use and the share of prompt tokens read from it.
/// </summary>
public sealed record EngineCapacity(int Count, IReadOnlySet<int> Busy, int Running = 0, int Waiting = 0, double? KvUsage = null, double? PrefixHitRate = null);

/// <summary>Whether an engine answers now, and if not why (for Admin → Models).</summary>
public sealed record EngineHealth(bool Up, string? Error, DateTimeOffset At);

/// <summary>
/// One engine instance of a kind (EngineKinds): llama.cpp's router, a vLLM supervisor, the servers the app does not run.
/// What the app does with models goes through here, whatever the kind; a kind without something (llama.cpp has no sleep)
/// says so (<see cref="Capabilities"/>) and refuses it. A new kind (SGLang) is a new implementation.
/// </summary>
public interface IEngineBackend
{
    /// <summary>The instance's name: "llamacpp", "vllm", a second machine's "vllm-b".</summary>
    string Name { get; }

    EngineCapabilities Capabilities { get; }

    /// <summary>Its models and their status; an EngineException when it cannot be asked.</summary>
    Task<IReadOnlyList<EngineModel>> ListAsync(CancellationToken ct);

    Task LoadAsync(string model, CancellationToken ct);

    Task UnloadAsync(string model, CancellationToken ct);

    /// <summary>Puts a loaded model to sleep (its GPUs lent), or wakes it; an EngineException where the kind cannot.</summary>
    Task SleepAsync(string model, bool sleep, CancellationToken ct);

    /// <summary>What a loaded model is doing, within <paramref name="patience"/>; null when the engine does not say in time.</summary>
    Task<EngineCapacity?> CapacityAsync(string model, TimeSpan patience, CancellationToken ct);

    Task<EngineHealth> HealthAsync(CancellationToken ct);
}

/// <summary>llama.cpp's router API: which models it has, loading or unloading one, and its slots.</summary>
public sealed class LlamaCppBackend(IHttpClientFactory http, IOptions<EngineOptions> options, TimeProvider clock) : IEngineBackend
{
    /// <summary>The engine's HTTP client (its timeout set where the app's services are).</summary>
    public const string Client = "engine";

    public string Name => "llamacpp";

    public EngineCapabilities Capabilities => EngineKinds.LlamaCppCapabilities;

    public async Task<IReadOnlyList<EngineModel>> ListAsync(CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, "/models", null, ct);
        return [.. (res?["data"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(m => m["id"] is not null)
            .Select(m => new EngineModel(m["id"]!.GetValue<string>(), StatusOf(m["status"] as JsonObject)))];
    }

    /// <summary>The router marks a model whose load exited with an error as unloaded and failed.</summary>
    private static string StatusOf(JsonObject? status)
    {
        var value = status?["value"]?.GetValue<string>() ?? "loaded";
        return value == "unloaded" && status?["failed"] is JsonValue f && f.TryGetValue<bool>(out var failed) && failed ? "failed" : value;
    }

    /// <summary>Starts loading a model. With one model at a time, the one loaded before is unloaded first.</summary>
    public Task LoadAsync(string model, CancellationToken ct) => SendAsync(HttpMethod.Post, "/models/load", new JsonObject { ["model"] = model }, ct);

    public Task UnloadAsync(string model, CancellationToken ct) => SendAsync(HttpMethod.Post, "/models/unload", new JsonObject { ["model"] = model }, ct);

    public Task SleepAsync(string model, bool sleep, CancellationToken ct) =>
        throw new EngineException("llama.cpp has no sleep: its models unload to lend their GPU.");

    /// <summary>
    /// A loaded model's slots as the engine has them (llama-server's /slots): how many, and which are answering now.
    /// llama-server answers between two batches of its work, so while it reads a long prompt this can take seconds. Null
    /// when the engine does not say within <paramref name="patience"/> (not reachable, the endpoint off, or too busy).
    /// </summary>
    public async Task<EngineCapacity?> CapacityAsync(string model, TimeSpan patience, CancellationToken ct)
    {
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(patience);
        try
        {
            if (await SendAsync(HttpMethod.Get, "/slots?model=" + Uri.EscapeDataString(model), null, quick.Token) is not JsonArray slots)
            {
                return null;
            }
            var all = slots.OfType<JsonObject>().ToList();
            var busy = all.Where(s => s["is_processing"] is JsonValue v && v.TryGetValue<bool>(out var p) && p)
                .Select(s => s["id"] is JsonValue id && id.TryGetValue<int>(out var n) ? n : -1).Where(n => n >= 0).ToHashSet();
            return new EngineCapacity(all.Count, busy, busy.Count);
        }
        catch (Exception ex) when (ex is EngineException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<EngineHealth> HealthAsync(CancellationToken ct)
    {
        try
        {
            await ListAsync(ct);
            return new EngineHealth(true, null, clock.GetUtcNow());
        }
        catch (EngineException ex)
        {
            return new EngineHealth(false, ex.Message, clock.GetUtcNow());
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(options.Value.Url.TrimEnd('/') + path));
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrEmpty(options.Value.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);
        }
        HttpResponseMessage res;
        try
        {
            res = await http.CreateClient(Client).SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new EngineException("The engine is not reachable (it may be restarting).");
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try
            {
                json = text.Length > 0 ? JsonNode.Parse(text) : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // not JSON: the status says enough
            }
            if (!res.IsSuccessStatusCode)
            {
                var message = json?["error"]?["message"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}";
                throw new EngineException(res.StatusCode == System.Net.HttpStatusCode.NotFound && path == "/models"
                    ? "The engine is not in router mode (an older stack or engine image)."
                    : $"The engine refused: {message}");
            }
            return json;
        }
    }
}

/// <summary>
/// Every engine of this stack as one: llama.cpp's router (when it runs) and each vLLM instance. The app's model state
/// (EngineState), its room-making (EngineRoute), the watcher and the GPU lending ask here, and each model goes to the
/// engine that lists it. llama.cpp's list failing fails the whole (as it always did: the engine is down); a vLLM
/// instance that does not answer only has its models listed unloaded, and says why (<see cref="Problems"/>).
/// </summary>
public sealed class EngineClient(LlamaCppBackend llama, VllmBackends vllm, ExternalBackend external, IOptions<EngineOptions> options)
{
    private volatile Dictionary<string, IEngineBackend> _owners = new(StringComparer.Ordinal);
    private volatile IReadOnlyDictionary<string, string> _problems = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The engines the app runs (llama.cpp when on, each vLLM instance), and the servers it does not.</summary>
    public IReadOnlyList<IEngineBackend> Backends => [.. Managed, external];

    private IEnumerable<IEngineBackend> Managed => (options.Value.LlamaCpp ? [llama] : Array.Empty<IEngineBackend>()).Concat(vllm.All);

    /// <summary>Why an engine instance's models could not be listed at the last look, by its name.</summary>
    public IReadOnlyDictionary<string, string> Problems => _problems;

    /// <summary>Every model of every engine the app runs, as each says.</summary>
    public async Task<IReadOnlyList<EngineModel>> ModelsAsync(CancellationToken ct = default)
    {
        var models = new List<EngineModel>();
        var owners = new Dictionary<string, IEngineBackend>(StringComparer.Ordinal);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var backend in Managed)
        {
            IReadOnlyList<EngineModel> listed;
            try
            {
                listed = await backend.ListAsync(ct);
            }
            catch (EngineException ex) when (backend != llama)
            {
                problems[backend.Name] = ex.Message;
                continue;
            }
            foreach (var m in listed.Where(m => owners.TryAdd(m.Name, backend)))
            {
                models.Add(m);
            }
            if (backend is VllmBackend v && v.Presets().Count > 0 && v.Down() is { } why)
            {
                problems[v.Name] = why;
            }
        }
        (_owners, _problems) = (owners, problems);
        return models;
    }

    /// <summary>The engine that has <paramref name="model"/>, as last listed; llama.cpp's when none listed it (its router loads by name).</summary>
    public IEngineBackend BackendOf(string model) =>
        _owners.TryGetValue(model, out var b) ? b : vllm.Find(model) ?? (IEngineBackend)llama;

    /// <summary>Its engine's kind (EngineKinds).</summary>
    public string KindOf(string model) => BackendOf(model).Capabilities.Kind;

    /// <summary>Starts loading a model on its engine.</summary>
    public Task LoadAsync(string name, CancellationToken ct = default) => BackendOf(name).LoadAsync(name, ct);

    public Task UnloadAsync(string name, CancellationToken ct = default) => BackendOf(name).UnloadAsync(name, ct);

    /// <summary>Puts a model to sleep or wakes it, where its engine can (vLLM).</summary>
    public Task SleepAsync(string name, bool sleep, CancellationToken ct = default) => BackendOf(name).SleepAsync(name, sleep, ct);

    /// <summary>What a loaded model is doing (its engine's word); null when it does not say within <paramref name="patience"/>.</summary>
    public Task<EngineCapacity?> CapacityAsync(string model, TimeSpan patience, CancellationToken ct = default) => BackendOf(model).CapacityAsync(model, patience, ct);

    /// <summary>
    /// A loaded model's places and which are busy, for the room-making and the slot table: llama.cpp's slots; vLLM's
    /// answers at once with as many busy as it runs. Null when the engine does not say in time.
    /// </summary>
    public async Task<(int Count, IReadOnlySet<int> Busy)?> SlotsAsync(string model, TimeSpan patience, CancellationToken ct = default) =>
        await CapacityAsync(model, patience, ct) is { } c ? (c.Count, c.Busy) : null;
}

/// <summary>
/// One vLLM engine: its name (its Service or container), where the app reaches its models (http://HOST:PORT), and the
/// folders it shares with the app (its desired state, its status); empty folders: those of this stack, by its name.
/// Another machine's is one more of these, its folders on storage both see (NFS, a ReadWriteMany claim).
/// </summary>
public sealed class VllmEngineOptions
{
    public string Name { get; set; } = "vllm";
    /// <summary>Its host as the app and the gateway reach it; null: <see cref="Name"/>.</summary>
    public string? Host { get; set; }
    public string? ConfigDir { get; set; }
    public string? StatusDir { get; set; }
    /// <summary>The first port its models get (8001: the chart's Service opens 16 from there).</summary>
    public int FirstPort { get; set; } = 8001;
}
