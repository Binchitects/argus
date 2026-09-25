using System.Text.Json;
using System.Text.Json.Nodes;

namespace Argus.Indexing;

/// <summary>The embedding service could not produce vectors.</summary>
public sealed class EmbeddingUnavailable(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Text to unit vectors through Ollama's <c>/api/embed</c> (argus/embed.py).
///
/// Every vector is L2-normalised here, so cosine is a dot product downstream and
/// a zero vector -- which has no direction -- is refused rather than stored.
/// </summary>
public static class Embed
{
    public static string Model => Environment.GetEnvironmentVariable("ARGUS_EMBED_MODEL") is { Length: > 0 } m ? m : "nomic-embed-text";
    public static int Dim => int.TryParse(Environment.GetEnvironmentVariable("ARGUS_EMBED_DIM"), out var d) ? d : 768;
    public const int BatchSize = 64;
    public const string DefaultBaseUrl = "http://localhost:11434";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>Swappable for tests; the production embedder calls Ollama.</summary>
    public static Func<IReadOnlyList<string>, List<double[]>>? Override { get; set; }

    static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = Timeout });

    public static List<double[]> EmbedBatch(IReadOnlyList<string> texts, HttpClient? client = null, string? baseUrl = null)
    {
        if (texts.Count == 0) return [];
        if (Override is not null) return Override(texts);
        var envBase = Environment.GetEnvironmentVariable("ARGUS_OLLAMA_URL");
        var b = (baseUrl ?? (string.IsNullOrEmpty(envBase) ? DefaultBaseUrl : envBase)).TrimEnd('/');
        client ??= SharedClient.Value;
        var vectors = new List<double[]>(texts.Count);
        for (int start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).ToList();
            vectors.AddRange(EmbedOne(client, b, batch));
        }
        return vectors;
    }

    static List<double[]> EmbedOne(HttpClient client, string b, List<string> batch)
    {
        HttpResponseMessage response;
        string text;
        try
        {
            var payload = new JsonObject { ["model"] = Model, ["input"] = new JsonArray(batch.Select(t => (JsonNode?)t).ToArray()) };
            // A sized body, not JsonContent's chunked stream: some proxies in front of
            // Ollama reject a request with no Content-Length.
            using var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            response = client.PostAsync($"{b}/api/embed", content).GetAwaiter().GetResult();
            text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new EmbeddingUnavailable($"POST {b}/api/embed failed: {exc.Message}", exc);
        }
        if ((int)response.StatusCode != 200)
            throw new EmbeddingUnavailable($"POST /api/embed returned {(int)response.StatusCode}: {Util.PyStr.Prefix(text, 200)}");
        JsonNode? body;
        try { body = JsonNode.Parse(text); }
        catch (JsonException exc) { throw new EmbeddingUnavailable($"POST /api/embed: failed to decode JSON: {Util.PyStr.Prefix(text, 200)}", exc); }
        if (body is not JsonObject obj || obj["embeddings"] is not JsonArray raw)
        {
            var error = (body as JsonObject)?["error"]?.ToString() ?? body?.ToJsonString() ?? "null";
            throw new EmbeddingUnavailable($"POST /api/embed returned no embeddings: {error}");
        }
        if (raw.Count != batch.Count)
            throw new EmbeddingUnavailable(
                $"POST /api/embed: expected {batch.Count} embeddings, got {raw.Count} -- refusing to return a misaligned batch");
        var result = new List<double[]>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var vec = (raw[i] as JsonArray ?? []).Select(x => x!.GetValue<double>()).ToArray();
            result.Add(Normalise(vec, i));
        }
        return result;
    }

    public static double[] Normalise(double[] vec, int index)
    {
        if (vec.Length != Dim)
            throw new EmbeddingUnavailable(
                $"embedding {index} has {vec.Length} dimensions, expected {Dim} -- is '{Model}' the model actually serving?");
        double norm = 0;
        foreach (var x in vec) norm += x * x;
        norm = Math.Sqrt(norm);
        if (norm == 0)
            throw new EmbeddingUnavailable($"embedding {index} is the zero vector, which has no direction and would match everything and nothing");
        return vec.Select(x => x / norm).ToArray();
    }
}
