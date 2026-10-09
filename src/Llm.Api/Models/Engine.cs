using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Core.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>Configuration section "Engine": llama.cpp in router mode, and the files the app shares with it.</summary>
public sealed class EngineOptions
{
    /// <summary>False only where no engine runs (tests): the other GPU servers' models still reach the gateway.</summary>
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "http://llamacpp:8080";
    /// <summary>ENGINE_KEY.</summary>
    public string? ApiKey { get; set; }
    /// <summary>MODEL in .env: the first chat model, fetched on the first start (Hugging Face repo:quant, or a file in the library).</summary>
    public string? FirstModel { get; set; }
    /// <summary>Where the app writes the engine's presets, the models to keep loaded, how many at once, and Prometheus's targets.</summary>
    public string ConfigDir { get; set; } = "/engine-config";
    /// <summary>The model library as the app sees it (MODELS_DIR, writable: downloads land there).</summary>
    public string LibraryDir { get; set; } = "/library";
    /// <summary>The same library inside the engine container.</summary>
    public string EngineLibraryDir { get; set; } = "/library";
    /// <summary>CPU threads for every model; null: llama.cpp's own choice.</summary>
    public int? Threads { get; set; }
    /// <summary>GPU memory the picture server may take beside the engine (its --max-vram).</summary>
    public long ImageReserveBytes { get; set; } = 2L << 30;
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

/// <summary>A model in the engine's list, and whether it is loaded.</summary>
/// <summary>A model of the router: loaded, loading, unloaded, or failed (its last load did not start).</summary>
public sealed record EngineModel(string Name, string Status);

public sealed class EngineException(string message) : Exception(message);

/// <summary>llama.cpp's router API: which models it has, and loading or unloading one.</summary>
public sealed class EngineClient(HttpClient http, IOptions<EngineOptions> options)
{
    public async Task<IReadOnlyList<EngineModel>> ModelsAsync(CancellationToken ct = default)
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
    public Task LoadAsync(string name, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/models/load", new JsonObject { ["model"] = name }, ct);

    public Task UnloadAsync(string name, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/models/unload", new JsonObject { ["model"] = name }, ct);

    /// <summary>
    /// A loaded model's slots as the engine has them (llama-server's /slots): how many, and which are
    /// answering now. llama-server answers between two batches of its work, so while it reads a long
    /// prompt this can take seconds. Null when the engine does not say within <paramref name="patience"/>
    /// (not reachable, the endpoint off, or too busy): the caller goes by what it knows itself.
    /// </summary>
    public async Task<(int Count, IReadOnlySet<int> Busy)?> SlotsAsync(string model, TimeSpan patience, CancellationToken ct = default)
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
            return (all.Count, all.Where(s => s["is_processing"] is JsonValue v && v.TryGetValue<bool>(out var p) && p)
                .Select(s => s["id"] is JsonValue id && id.TryGetValue<int>(out var n) ? n : -1).Where(n => n >= 0).ToHashSet());
        }
        catch (Exception ex) when (ex is EngineException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
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
            res = await http.SendAsync(req, ct);
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
