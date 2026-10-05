using System.Text.Json;
using System.Text.Json.Nodes;

namespace Argus.Indexing;

/// <summary>The embedding service could not produce vectors.</summary>
public sealed class EmbeddingUnavailable(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Text to unit vectors through an OpenAI-compatible <c>/v1/embeddings</c>
/// endpoint -- in the stack, a llama.cpp server running nomic-embed-text.
///
/// Every vector is L2-normalised here, so cosine is a dot product downstream and
/// a zero vector -- which has no direction -- is refused rather than stored.
///
/// A text longer than the model's window (nomic-embed-text: 2,048 tokens) is cut
/// to fit, as Ollama does by itself: llama.cpp refuses it, so the batch is sent
/// again item by item and the long one shortened until the server takes it.
/// </summary>
public static class Embed
{
    public static string Model => Environment.GetEnvironmentVariable("ARGUS_EMBED_MODEL") is { Length: > 0 } m ? m : "nomic-embed-text";
    public static int Dim => int.TryParse(Environment.GetEnvironmentVariable("ARGUS_EMBED_DIM"), out var d) ? d : 768;
    public const int BatchSize = 64;
    public const string DefaultBaseUrl = "http://localhost:8081";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>Where the embedding server lives: <c>ARGUS_EMBED_URL</c>, without the <c>/v1</c>.</summary>
    public static string BaseUrl =>
        (Environment.GetEnvironmentVariable("ARGUS_EMBED_URL") is { Length: > 0 } u ? u : DefaultBaseUrl).TrimEnd('/');

    /// <summary>Swappable for tests; the production embedder calls the embedding server.</summary>
    public static Func<IReadOnlyList<string>, List<double[]>>? Override { get; set; }

    static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = Timeout });

    public static List<double[]> EmbedBatch(IReadOnlyList<string> texts, HttpClient? client = null, string? baseUrl = null)
    {
        if (texts.Count == 0) return [];
        if (Override is not null) return Override(texts);
        var b = (baseUrl ?? BaseUrl).TrimEnd('/');
        client ??= SharedClient.Value;
        var vectors = new List<double[]>(texts.Count);
        for (int start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).ToList();
            vectors.AddRange(EmbedOne(client, b, batch));
        }
        return vectors;
    }

    /// <summary>How many times a text too long for the model is shortened before giving up.</summary>
    const int Shortenings = 6;

    static List<double[]> EmbedOne(HttpClient client, string b, List<string> batch)
    {
        try
        {
            return Send(client, b, batch);
        }
        catch (TooLong) when (batch.Count > 1)
        {
            // Which one is too long the server does not say: each on its own.
            return [.. batch.SelectMany(t => EmbedOne(client, b, [t]))];
        }
        catch (TooLong first)
        {
            var text = batch[0];
            var tooLong = first;
            for (var i = 0; i < Shortenings; i++)
            {
                // The share of the text that fits, with a margin: tokens are not spread evenly.
                var keep = (int)(text.Length * Math.Min(0.9, 0.9 * tooLong.Fits / Math.Max(tooLong.Tokens, 1)));
                text = text[..Math.Max(1, Math.Min(keep, text.Length - 1))];
                try
                {
                    return Send(client, b, [text]);
                }
                catch (TooLong again)
                {
                    tooLong = again;
                }
            }
            throw new EmbeddingUnavailable($"a text of {batch[0].Length:N0} characters is still too long for the model after {Shortenings} cuts");
        }
    }

    /// <summary>The server refused a text longer than the model's window: its length and what fits, in tokens.</summary>
    sealed class TooLong(int tokens, int fits) : Exception
    {
        public int Tokens { get; } = tokens;
        public int Fits { get; } = fits;
    }

    // llama.cpp: "input (4092 tokens) is larger than the max context size (2048 tokens)", or
    // "input (4092 tokens) is too large to process. increase the physical batch size (current batch size: 2048)".
    static readonly System.Text.RegularExpressions.Regex TooLongMessage =
        new(@"input \((\d+) tokens\) is (?:larger than the max context size|too large to process)\D*(\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static List<double[]> Send(HttpClient client, string b, List<string> batch)
    {
        HttpResponseMessage response;
        string text;
        var url = $"{b}/v1/embeddings";
        try
        {
            var payload = new JsonObject { ["model"] = Model, ["input"] = new JsonArray(batch.Select(t => (JsonNode?)t).ToArray()) };
            // A sized body, not JsonContent's chunked stream: some proxies reject a
            // request with no Content-Length.
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
            if (Environment.GetEnvironmentVariable("ARGUS_EMBED_API_KEY") is { Length: > 0 } key)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            response = client.Send(request);
            text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new EmbeddingUnavailable($"POST {url} failed: {exc.Message}", exc);
        }
        if ((int)response.StatusCode is 400 or 413 or 500 && TooLongMessage.Match(text) is { Success: true } m)
            throw new TooLong(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        if ((int)response.StatusCode != 200)
            throw new EmbeddingUnavailable($"POST /v1/embeddings returned {(int)response.StatusCode}: {Util.PyStr.Prefix(text, 200)}");
        JsonNode? body;
        try { body = JsonNode.Parse(text); }
        catch (JsonException exc) { throw new EmbeddingUnavailable($"POST /v1/embeddings: failed to decode JSON: {Util.PyStr.Prefix(text, 200)}", exc); }
        if (body is not JsonObject obj || obj["data"] is not JsonArray data)
        {
            var error = (body as JsonObject)?["error"]?.ToJsonString() ?? body?.ToJsonString() ?? "null";
            throw new EmbeddingUnavailable($"POST /v1/embeddings returned no embeddings: {error}");
        }
        if (data.Count != batch.Count)
            throw new EmbeddingUnavailable(
                $"POST /v1/embeddings: expected {batch.Count} embeddings, got {data.Count} -- refusing to return a misaligned batch");
        // The protocol carries an index per item; order by it rather than trusting
        // the array order, so a server that answers out of order cannot misalign.
        var ordered = data.Select((item, i) => (Index: (item as JsonObject)?["index"]?.GetValue<int>() ?? i, Item: item))
            .OrderBy(x => x.Index).ToList();
        var result = new List<double[]>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var vec = ((ordered[i].Item as JsonObject)?["embedding"] as JsonArray ?? []).Select(x => x!.GetValue<double>()).ToArray();
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
