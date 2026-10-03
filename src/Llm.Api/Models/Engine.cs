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
    /// <summary>How many models may be loaded at once, those kept loaded included (Settings, Engine).</summary>
    public int ModelsMax { get; set; } = 1;
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
