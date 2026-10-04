using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Llm.Tests;

/// <summary>
/// The embed module's /v1/embeddings, by meaning of a kind: each word of a text (lower case, three letters
/// or more, a plural's s dropped, common words left out) adds to one of 512 dimensions, so texts that share
/// words point the same way. nomic's task prefixes (search_query:, search_document:) are not words.
/// </summary>
public sealed partial class FakeEmbedder : HttpMessageHandler
{
    public const int Dimensions = 512;

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "what", "which", "how", "are", "was", "were", "has", "have", "its",
        "into", "about", "when", "where", "who", "why", "does", "did", "can", "not", "but", "you", "your", "they", "their", "there",
        "search_query", "search_document", "our", "all",
    };

    /// <summary>Texts embedded so far: what a sync embedded again, and what it did not.</summary>
    public int Texts => Volatile.Read(ref _texts);
    private int _texts;

    /// <summary>While set, it answers 503: the embedder is down.</summary>
    public bool Down { get; set; }

    public static float[] Vector(string text)
    {
        var v = new float[Dimensions];
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value.Length > 4 && m.Value.EndsWith('s') ? m.Value[..^1] : m.Value;
            if (Common.Contains(w))
            {
                continue;
            }
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(w))
            {
                hash = (hash ^ b) * 16777619u;
            }
            v[hash % Dimensions] += 1;
        }
        // A text with no words still has a direction.
        v[0] += 0.01f;
        return v;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"error":"loading"}""") };
        }
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var input = body["input"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Interlocked.Add(ref _texts, input.Count);
        var data = new JsonArray([.. input.Select((t, i) => (JsonNode)new JsonObject
        {
            ["object"] = "embedding", ["index"] = i, ["embedding"] = new JsonArray([.. Vector(t).Select(x => (JsonNode)x)]),
        })]);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject { ["object"] = "list", ["data"] = data, ["model"] = body["model"]?.GetValue<string>() }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]{3,}")]
    private static partial Regex Word();
}
