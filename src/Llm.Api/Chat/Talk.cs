using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Chat;

/// <summary>
/// Talk: a voice conversation in the chat. The browser listens, cuts what is said at a pause
/// and sends it here to be written down (the gateway's speech to text); the words go as an
/// ordinary question (saved, answered with the chat's model and tools) with a note to answer
/// in spoken sentences. The page reads the answer aloud sentence by sentence as it is written
/// (/api/chat/speech), and speaking over it stops it (…/stop).
/// </summary>
public static class Talk
{
    /// <summary>Said with a spoken question, for its answer only.</summary>
    public const string Note = "(Asked aloud in a voice conversation: the answer is read aloud as it is written. Answer in plain spoken sentences, " +
        "the answer first and short; no tables, code blocks, headings or links unless asked.)";

    /// <summary>The largest recording taken: minutes of speech (the browser's Opus is about 4 KB a second).</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>What was said in a recording from the browser, written down by the gateway's speech to text in the person's name.</summary>
    public static async Task<IResult> TranscribeAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, GatewayChat gateway, ChatModels models)
    {
        var ct = request.HttpContext.RequestAborted;
        var me = (await users.GetUserAsync(p))!;
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBytes + 64 * 1024;
        }
        if (!request.HasFormContentType || (await request.ReadFormAsync(ct)).Files is not { Count: 1 } files)
        {
            return AuthEndpoints.Problem(400, "file", "Send exactly one recording.");
        }
        var file = files[0];
        var type = (file.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (file.Length == 0 || !(type.StartsWith("audio/", StringComparison.Ordinal) || type == "video/webm"))
        {
            return AuthEndpoints.Problem(400, "sound", "Send a recording of speech (WebM, MP4, OGG, WAV or MP3).");
        }
        if (file.Length > MaxBytes)
        {
            return AuthEndpoints.Problem(413, "too_large", "That recording is too long: say it in shorter parts.");
        }
        if (await models.OfModeAsync("audio_transcription", MediaModels.SpeechToText, ct) is not { } model)
        {
            return AuthEndpoints.Problem(503, "no_transcription", "The gateway has no speech to text model (the audio module).");
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        // MediaRecorder's WebM says video/webm in some browsers; it is sound all the same.
        var sound = type == "video/webm" ? "audio/webm" : type;
        try
        {
            var text = await gateway.TranscribeAsync(model.Name, ms.ToArray(), "speech" + Extension(sound), me.Email!, ct, sound);
            return Results.Ok(new { text = text.Trim() });
        }
        catch (ChatGatewayException ex)
        {
            return AuthEndpoints.Problem(502, "gateway", ex.Message);
        }
    }

    private static string Extension(string contentType) => contentType switch
    {
        "audio/webm" => ".webm", "audio/ogg" => ".ogg", "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav", "audio/mpeg" => ".mp3",
        "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a", "audio/flac" => ".flac", _ => ".webm",
    };
}
