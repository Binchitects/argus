using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Identity;

namespace Llm.Api.Gateway;

/// <summary>
/// Speech and pictures with API keys at gateway.DOMAIN: answered by the app's own gateway (<see cref="KeyMedia"/>), a
/// browser's preflight with its CORS headers. Text to speech that names no voice is read in the key's person's voice for
/// its text's language (Your account → Voice), at their speed when it names none; one that names no model either gets
/// that voice's model (<see cref="WithVoiceAsync"/>).
/// </summary>
public static class KeySpeech
{
    /// <summary>The paths, with and without /v1 as clients send both.</summary>
    public static readonly IReadOnlyList<string> Paths =
        ["/v1/audio/speech", "/audio/speech", "/v1/audio/transcriptions", "/audio/transcriptions", "/v1/images/generations", "/images/generations"];

    public static async Task ProxyAsync(HttpContext http)
    {
        if (KeyMedia.Preflight(http) || await KeyMedia.TryAsync(http))
        {
            return;
        }
        // Only a POST (or a preflight) is a request here.
        http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        http.Response.Headers.Allow = "POST, OPTIONS";
        await http.Response.WriteAsJsonAsync(new { error = new { message = "Send it as a POST.", type = "method_not_allowed", code = "405" } }, http.RequestAborted);
    }

    /// <summary>The request with the person's voice (and speed, and model) filled in; null: as it came (a voice named, nothing to read).</summary>
    public static async Task<byte[]?> WithVoiceAsync(byte[] body, AppUser person, VoiceCatalog voices, CancellationToken ct)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            // Not JSON: the gateway says what is wrong with it.
            return null;
        }
        if (json is null || Str(json, "voice") is { Length: > 0 } || Str(json, "input") is not { Length: > 0 } text)
        {
            return null;
        }
        var speech = await voices.ForAsync(person, ct);
        var model = Str(json, "model") is { Length: > 0 } named ? named : null;
        if (speech.For(text, model) is not { } voice)
        {
            return null;
        }
        json["voice"] = voice.Name;
        json["model"] = voice.Model;
        if (json["speed"] is null && speech.Speed != 1)
        {
            json["speed"] = speech.Speed;
        }
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
