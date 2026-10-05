using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// The laya module's server (deploy/services/laya) as the app sees it: /health, and /v1/decide
/// answering every question as Laya shapes its answers (the first option at 0.97231, a score of
/// 1.38721, a yes of 0.84987, each with Laya's act-or-escalate head). A state "refuse" is refused
/// as Laya refuses options past its budget; one starting "long" is cut short, as Laya says in its usage.
/// </summary>
public sealed class FakeLaya : HttpMessageHandler
{
    /// <summary>Nothing listens: every request fails to connect.</summary>
    public bool Down { get; set; }

    /// <summary>Still fetching or loading its checkpoints: /health lists none ready.</summary>
    public bool Loading { get; set; }

    public List<JsonObject> Requests { get; } = [];

    /// <summary>Each decide request's Content-Length (null: sent in chunks).</summary>
    public List<long?> Lengths { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("Connection refused (laya:8000)");
        }
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/health" && request.Method == HttpMethod.Get)
        {
            return Json(new JsonObject
            {
                ["status"] = Loading ? "loading" : "ready",
                ["ready"] = Loading ? new JsonArray() : new JsonArray("english", "multilingual"),
            });
        }
        if (path != "/v1/decide" || request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        // Read before the body is: reading it buffers it, and then any content has a length.
        var length = request.Content!.Headers.ContentLength;
        var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        lock (Requests)
        {
            Requests.Add(body);
            Lengths.Add(length);
        }
        var state = body["state"]!.GetValue<string>();
        if (state == "refuse")
        {
            return Json(new JsonObject { ["error"] = "options exceed head_max_len (192 tokens)" }, HttpStatusCode.UnprocessableEntity);
        }
        var answers = new JsonObject();
        foreach (var (id, q) in body["questions"]!.AsObject())
        {
            var act = new JsonObject { ["act_probability"] = 1.0 };
            answers[id] = q!["type"]!.GetValue<string>() switch
            {
                "choice" => new JsonObject
                {
                    ["type"] = "choice",
                    ["choice"] = q["criteria"]!.AsObject().First().Key,
                    ["probabilities"] = new JsonObject(q["criteria"]!.AsObject().Select((o, i) => KeyValuePair.Create(o.Key, (JsonNode?)(i == 0 ? 0.97231 : 0.01)))),
                    ["confidence"] = 0.90331,
                    ["action"] = act,
                },
                "score" => new JsonObject
                {
                    ["type"] = "score", ["score"] = 1.38721,
                    ["probabilities"] = new JsonObject { ["0"] = 0.11537, ["1"] = 0.38211, ["2"] = 0.50252 },
                    ["confidence"] = 0.12381, ["action"] = act,
                },
                _ => new JsonObject { ["type"] = "noul", ["noul"] = 0.84987, ["confidence"] = 0.84987, ["action"] = act },
            };
        }
        var multilingual = body["checkpoint"]?.GetValue<string>() == "multilingual" || state.Any(c => c is >= '؀' and <= 'ۿ');
        return Json(new JsonObject
        {
            ["answers"] = answers,
            ["usage"] = state.StartsWith("long", StringComparison.Ordinal)
                ? new JsonObject { ["input_tokens"] = 512, ["output_tokens"] = 0, ["state_tokens"] = 812, ["state_tokens_dropped"] = 334, ["truncated"] = true }
                : new JsonObject { ["input_tokens"] = 42, ["output_tokens"] = 0, ["state_tokens"] = 21, ["state_tokens_dropped"] = 0, ["truncated"] = false },
            ["checkpoint"] = multilingual ? "multilingual" : "english",
            ["routing"] = multilingual ? "arabic script" : "latin script, language en",
            ["calibrated"] = !multilingual,
            ["ms"] = 41.7,
        });
    }

    private static HttpResponseMessage Json(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
