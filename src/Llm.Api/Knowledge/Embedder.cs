using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>Configuration section "Knowledge": the embedder, where folders are, how often sources sync, and what a question gets.</summary>
public sealed class KnowledgeOptions
{
    /// <summary>An OpenAI-compatible embeddings server, without /v1: the stack's embed module (nomic-embed-text).</summary>
    public string EmbedUrl { get; set; } = "http://embed:8080";
    public string EmbedModel { get; set; } = "nomic-embed-text";
    /// <summary>A bearer key for an embeddings server outside the stack; empty: none.</summary>
    public string? EmbedKey { get; set; }
    /// <summary>What nomic-embed-text wants before a passage and before a question; empty for a model that wants none.</summary>
    public string DocumentPrefix { get; set; } = "search_document: ";
    public string QueryPrefix { get; set; } = "search_query: ";
    /// <summary>How often each source is read again (only what changed is embedded again).</summary>
    public TimeSpan SyncEvery { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Folders a folder source may read are under this one (mounted by the admin).</summary>
    public string FolderRoot { get; set; } = "/knowledge";
    /// <summary>The passages of a chat's long files and a project's files that go with each question, in characters.</summary>
    public int PassageChars { get; set; } = 12_000;
    /// <summary>Longest an answer waits for its files to be embedded; those not ready go in as before.</summary>
    public TimeSpan IndexWait { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Compare with pgvector when Postgres has it (the stack's image does); false: plain SQL, slower.</summary>
    public bool PgVector { get; set; } = true;
}

/// <summary>The embedder could not make vectors (down, or an answer that does not fit).</summary>
public sealed class EmbedderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Text to unit vectors through an OpenAI-compatible /v1/embeddings (the embed module, as Argus uses it).
/// Present when its name resolves on the stack's network (<see cref="Operations.Modules"/>): no embedder, no knowledge.
/// </summary>
public sealed class Embedder(IHttpClientFactory http, IOptionsMonitor<KnowledgeOptions> options, Operations.Modules modules)
{
    public const string Client = "embed";
    private const int Batch = 32;
    /// <summary>A text past this is cut before it is embedded (the server reads 2,048 tokens at most).</summary>
    private const int MaxChars = 6_000;

    public string Model => options.CurrentValue.EmbedModel;

    /// <summary>The module's name: the embedder's host (embed in the stack).</summary>
    private string Host => Uri.TryCreate(options.CurrentValue.EmbedUrl, UriKind.Absolute, out var u) ? u.Host : "embed";

    public Task<bool> ReadyAsync(CancellationToken ct) => modules.HasAsync(Host, ct);

    /// <summary>The question's vector.</summary>
    public async Task<float[]> QueryAsync(string text, CancellationToken ct) =>
        (await EmbedAsync([options.CurrentValue.QueryPrefix + text], ct))[0];

    /// <summary>Passages' vectors, in order.</summary>
    public Task<List<float[]>> DocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        EmbedAsync([.. texts.Select(t => options.CurrentValue.DocumentPrefix + t)], ct);

    private async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var url = $"{o.EmbedUrl.TrimEnd('/')}/v1/embeddings";
        var vectors = new List<float[]>(texts.Count);
        for (var start = 0; start < texts.Count; start += Batch)
        {
            var batch = texts.Skip(start).Take(Batch).Select(t => t.Length > MaxChars ? t[..MaxChars] : t).ToList();
            var payload = new JsonObject { ["model"] = o.EmbedModel, ["input"] = new JsonArray([.. batch.Select(t => (JsonNode)t)]) };
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
            if (o.EmbedKey is { Length: > 0 } key)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            string text;
            try
            {
                using var response = await http.CreateClient(Client).SendAsync(request, ct);
                text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new EmbedderException($"the embedder answered {(int)response.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new EmbedderException($"the embedder could not be reached ({ex.Message})", ex);
            }
            JsonNode? body;
            try
            {
                body = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                body = null;
            }
            if (body?["data"] is not JsonArray data || data.Count != batch.Count)
            {
                throw new EmbedderException("the embedder's answer has no vector for each text");
            }
            // In the order of their index, not the order they came in.
            foreach (var item in data.OfType<JsonObject>().OrderBy(d => d["index"]?.GetValue<int>() ?? 0))
            {
                vectors.Add(Unit(item["embedding"] as JsonArray ?? []));
            }
        }
        return vectors;
    }

    /// <summary>Of length one, so cosine is a dot product; a zero vector (no direction) is refused.</summary>
    private static float[] Unit(JsonArray values)
    {
        var v = values.Select(x => x!.GetValue<float>()).ToArray();
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (v.Length == 0 || norm == 0)
        {
            throw new EmbedderException("the embedder answered a vector with no direction");
        }
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(v[i] / norm);
        }
        return v;
    }
}
