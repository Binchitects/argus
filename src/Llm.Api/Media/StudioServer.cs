using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Llm.Api.Models;

namespace Llm.Api.Media;

/// <summary>
/// The studio server (the studio module, services/studio): a picture's background taken away (BiRefNet lite), a text
/// spoken in a voice from a short sample (Chatterbox), music from words (ACE-Step). Each on the GPU, one at a time there.
/// On its CPU: where the things named are in a picture (CLIPSeg), for a traced drawing's parts.
/// </summary>
public sealed class StudioServer(IHttpClientFactory http)
{
    public const string Client = "studio";

    /// <summary>What the gateway's log and the items name the studio's models by.</summary>
    public const string CutoutModel = "BiRefNet-lite";
    public const string VoiceModel = "Chatterbox";
    public const string MusicModel = "ACE-Step-v1-3.5B";

    /// <param name="FreeMb">The GPU's free memory, as the server sees it.</param>
    /// <param name="Loaded">Its models loaded now (cutout, voice, music).</param>
    public sealed record Health(bool Cutout, bool Voice, bool Music, bool Segment, long? FreeMb, IReadOnlyList<string> Loaded);

    /// <summary>The server's name for the model a piece of work needs.</summary>
    public static string KindOf(string action) => action switch { Core.Media.MediaJob.Cutout => "cutout", Core.Media.MediaJob.Speak => "voice", _ => "music" };

    /// <summary>The GPU memory each of its models takes as it works, in MB (as the server reckons).</summary>
    public static int NeedMb(string kind) => kind switch { "cutout" => 1800, "voice" => 4500, _ => 10000 };

    /// <summary>Which of its models the studio server has; null when it does not answer.</summary>
    public async Task<Health?> HealthAsync(CancellationToken ct)
    {
        try
        {
            var h = await http.CreateClient(Client).GetFromJsonAsync<JsonObject>(MediaModels.StudioUrl + "/health", ct);
            bool Has(string kind) => h?["models"]?[kind]?.GetValue<bool>() == true;
            return new Health(Has("cutout"), Has("voice"), Has("music"), Has("segment"), h?["free_mb"] is JsonValue f && f.TryGetValue<long>(out var mb) ? mb : null,
                [.. (h?["loaded"] as JsonArray ?? []).Select(x => x?.GetValue<string>()).OfType<string>()]);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Its models unloaded now: the GPU memory they took goes back (to the chat models that made room).</summary>
    public async Task UnloadAsync()
    {
        try
        {
            using var res = await http.CreateClient(Client).PostAsync(MediaModels.StudioUrl + "/v1/unload", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Not answering: it unloads them itself when unused.
        }
    }

    /// <summary>The picture with its background transparent, as a PNG.</summary>
    public async Task<byte[]> CutoutAsync(byte[] picture, string type, CancellationToken ct)
    {
        using var content = new ByteArrayContent(picture) { Headers = { ContentType = new MediaTypeHeaderValue(type) } };
        using var res = await http.CreateClient(Client).PostAsync(MediaModels.StudioUrl + "/v1/cutout", content, ct);
        return await BytesAsync(res, ct);
    }

    /// <summary>The text spoken, as an MP3, in the voice of <paramref name="sample"/> (an MP3 or WAV of the voice), or the model's own; and its length.</summary>
    public async Task<(byte[] Mp3, double Seconds)> SpeakAsync(string text, byte[]? sample, double exaggeration, CancellationToken ct)
    {
        var body = new JsonObject { ["text"] = text, ["exaggeration"] = exaggeration };
        if (sample is not null)
        {
            body["voice"] = Convert.ToBase64String(sample);
        }
        using var res = await http.CreateClient(Client).PostAsJsonAsync(MediaModels.StudioUrl + "/v1/speak", body, ct);
        return (await BytesAsync(res, ct), SecondsOf(res));
    }

    /// <summary>Music from a description and lyrics ("[instrumental]" when none), as an MP3, and its length.</summary>
    public async Task<(byte[] Mp3, double Seconds)> MusicAsync(string prompt, string? lyrics, double seconds, CancellationToken ct)
    {
        var body = new JsonObject { ["prompt"] = prompt, ["lyrics"] = lyrics ?? "", ["seconds"] = seconds };
        using var res = await http.CreateClient(Client).PostAsJsonAsync(MediaModels.StudioUrl + "/v1/music", body, ct);
        return (await BytesAsync(res, ct), SecondsOf(res));
    }

    /// <summary>
    /// Where each thing named is in the picture: a grey map (PNG) for each label, from its least likely point (black) to its most
    /// likely (white), in the order asked.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> SegmentAsync(byte[] picture, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var body = new JsonObject { ["picture"] = Convert.ToBase64String(picture), ["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)l)]) };
        using var res = await http.CreateClient(Client).PostAsJsonAsync(MediaModels.StudioUrl + "/v1/segment", body, ct);
        var masks = JsonNode.Parse(await BytesAsync(res, ct))?["masks"] as JsonArray ?? [];
        return [.. masks.Select(m => Convert.FromBase64String(m?.GetValue<string>() ?? ""))];
    }

    private static double SecondsOf(HttpResponseMessage res) =>
        res.Headers.TryGetValues("X-Seconds", out var v) && double.TryParse(v.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;

    private static async Task<byte[]> BytesAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
        string? why = null;
        try
        {
            why = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))?["error"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            // not JSON: said by its status below
        }
        throw new SdServer.SdException(why ?? $"the studio server answered HTTP {(int)res.StatusCode}");
    }
}
