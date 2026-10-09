using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// The speech server (speaches), as the app asks it: its models' list by task, with each text to speech model's voices
/// as speaches 0.9 lists them (Kokoro's with their language and gender, a Piper model's one voice with its card's
/// language), and the languages its speech to text knows. It also lists a model the app does not run, and takes the
/// app's loading and fetching calls. <see cref="Down"/> makes it answer 503.
/// </summary>
public sealed class FakeAudio : HttpMessageHandler
{
    public ConcurrentQueue<string> Asked { get; } = new();
    public bool Down { get; set; }

    /// <summary>Models it does not list yet, by id: speaches lists only the models it has downloaded.</summary>
    public ConcurrentDictionary<string, bool> Downloading { get; } = new();

    private static JsonObject Voice(string name, string language, string? gender = null)
    {
        var v = new JsonObject { ["name"] = name, ["language"] = language, ["id"] = name };
        if (gender is not null)
        {
            v["gender"] = gender;
        }
        return v;
    }

    private static JsonObject Speaking() => new()
    {
        ["object"] = "list",
        ["data"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "speaches-ai/Kokoro-82M-v1.0-ONNX", ["task"] = "text-to-speech", ["language"] = new JsonArray("multilingual"), ["sample_rate"] = 24000,
                ["voices"] = new JsonArray(
                    Voice("af_heart", "en-us", "female"), Voice("am_adam", "en-us", "male"), Voice("bf_emma", "en-gb", "female"),
                    Voice("ef_dora", "es", "female"), Voice("ff_siwis", "fr-fr", "female"), Voice("jf_alpha", "ja", "female")),
            },
            new JsonObject
            {
                ["id"] = "speaches-ai/piper-fa_IR-gyro-medium", ["task"] = "text-to-speech", ["language"] = new JsonArray("fa"), ["sample_rate"] = 22050,
                ["voices"] = new JsonArray(Voice("gyro", "fa")),
            },
            // On the server, but not one of the app's: never offered.
            new JsonObject
            {
                ["id"] = "speaches-ai/piper-de_DE-thorsten-medium", ["task"] = "text-to-speech", ["language"] = new JsonArray("de"),
                ["voices"] = new JsonArray(Voice("thorsten", "de")),
            }),
    };

    private static JsonObject Hearing() => new()
    {
        ["object"] = "list",
        ["data"] = new JsonArray(new JsonObject
        {
            ["id"] = "deepdml/faster-whisper-large-v3-turbo-ct2", ["task"] = "automatic-speech-recognition",
            ["language"] = new JsonArray("en", "fa", "de", "fr", "es", "ja"),
        }),
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Asked.Enqueue($"{request.Method} {uri.PathAndQuery}");
        if (Down)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/v1/models")
        {
            var list = uri.Query.Contains("task=automatic-speech-recognition", StringComparison.Ordinal) ? Hearing()
                : uri.Query.Contains("task=text-to-speech", StringComparison.Ordinal) ? Speaking()
                : new JsonObject { ["data"] = new JsonArray([.. Speaking()["data"]!.AsArray().Select(m => m!.DeepClone()), .. Hearing()["data"]!.AsArray().Select(m => m!.DeepClone())]) };
            foreach (var model in list["data"]!.AsArray().Where(m => Downloading.ContainsKey(m!["id"]!.GetValue<string>())).ToList())
            {
                list["data"]!.AsArray().Remove(model);
            }
            return Task.FromResult(Json(list));
        }
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/api/ps")
        {
            return Task.FromResult(Json(new JsonObject { ["models"] = new JsonArray() }));
        }
        // Fetching a model, loading or unloading one.
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
    }

    private static HttpResponseMessage Json(JsonObject body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
