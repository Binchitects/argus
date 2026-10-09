using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Llm.Api.Gateway;

/// <summary>
/// Speech with API keys, by way of the app: Traefik sends gateway.DOMAIN's /v1/audio/speech and /v1/audio/transcriptions
/// here while the app is up, and straight to LiteLLM otherwise. Text to speech that names no voice is read in the key's
/// person's voice for its text's language (Your account → Voice), at their speed when it names none; one that names no
/// model either gets that voice's model. Speech to text that names no language is written down in the language the
/// person speaks, when they chose one. Everything goes on to LiteLLM with the caller's own key, which the gateway
/// checks, and streams back.
/// </summary>
public static class KeySpeech
{
    public const string Client = "key-speech";

    /// <summary>The gateway's speech paths that come by the app (with and without /v1, as LiteLLM takes both).</summary>
    public static readonly IReadOnlyList<string> Paths = ["/v1/audio/speech", "/audio/speech", "/v1/audio/transcriptions", "/audio/transcriptions"];

    /// <summary>Text to speech requests larger than this go on as they came.</summary>
    private const int MostRead = 1024 * 1024;

    /// <summary>Speech to text requests larger than this go on as they came (OpenAI's own limit is 25 MB).</summary>
    private const int MostHeard = 32 * 1024 * 1024;

    public static async Task ProxyAsync(HttpContext http, ILiteLlm gateway, AppDbContext db, VoiceCatalog voices, IHttpClientFactory factory)
    {
        var ct = http.RequestAborted;
        var request = http.Request;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            // The gateway decides, as when it is reached directly.
            limit.MaxRequestBodySize = null;
        }
        var hearing = request.Path.Value?.EndsWith("/transcriptions", StringComparison.Ordinal) == true;
        byte[]? body = null;
        if (request.ContentLength is { } length && length > 0 && length <= (hearing ? MostHeard : MostRead))
        {
            body = new byte[length];
            var read = 0;
            int n;
            while (read < body.Length && (n = await request.Body.ReadAsync(body.AsMemory(read), ct)) > 0)
            {
                read += n;
            }
            if (read < body.Length)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var key = request.Headers.Authorization.ToString();
            body = (hearing ? await WithLanguageAsync(body, request.ContentType, key, gateway, db, voices, ct) : await WithVoiceAsync(body, key, gateway, db, voices, ct)) ?? body;
        }

        using var forward = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Path + request.QueryString, UriKind.Relative))
        {
            Content = body is not null ? new ByteArrayContent(body) : new StreamContent(request.Body),
        };
        foreach (var (name, values) in request.Headers)
        {
            if (!AnswerCacheEndpoints.Dropped.Contains(name))
            {
                forward.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        if (request.ContentType is { } type)
        {
            forward.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        forward.Content.Headers.ContentLength = body?.Length ?? request.ContentLength;
        forward.Headers.TryAddWithoutValidation("X-Forwarded-For", http.Connection.RemoteIpAddress?.ToString());
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value);

        HttpResponseMessage res;
        try
        {
            res = await factory.CreateClient(Client).SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            await http.Response.WriteAsJsonAsync(new { error = new { message = "The model gateway is not reachable right now.", type = "gateway_unreachable", code = "502" } }, ct);
            return;
        }
        using (res)
        {
            http.Response.StatusCode = (int)res.StatusCode;
            foreach (var (name, values) in res.Headers.Concat(res.Content.Headers))
            {
                if (name is not ("Transfer-Encoding" or "Connection" or "Keep-Alive"))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await using var upstream = await res.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            int n;
            // The sound goes on as the speech server writes it.
            while ((n = await upstream.ReadAsync(buffer, ct)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
    }

    /// <summary>The request with the person's voice (and speed, and model) filled in; null: as it came (a voice named, no person to the key, nothing to read).</summary>
    public static async Task<byte[]?> WithVoiceAsync(byte[] body, string authorization, ILiteLlm gateway, AppDbContext db, VoiceCatalog voices, CancellationToken ct)
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
        if (json is null || Str(json, "voice") is { Length: > 0 } || Str(json, "input") is not { Length: > 0 } text
            || await PersonAsync(authorization, gateway, db, ct) is not { } person)
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

    /// <summary>
    /// The form with the language the person speaks added, before its closing line; null: as it came (a language named,
    /// the person lets Whisper hear which, no person to the key, not a form).
    /// </summary>
    public static async Task<byte[]?> WithLanguageAsync(byte[] body, string? contentType, string authorization, ILiteLlm gateway, AppDbContext db, VoiceCatalog voices,
        CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
        {
            return null;
        }
        try
        {
            var reader = new MultipartReader(boundary, new MemoryStream(body, writable: false));
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var part) && HeaderUtilities.RemoveQuotes(part.Name).Equals("language", StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Not a whole form: the gateway says what is wrong with it.
            return null;
        }
        var end = Encoding.ASCII.GetBytes($"\r\n--{boundary}--");
        var at = body.AsSpan().LastIndexOf(end);
        if (at < 0 || await PersonAsync(authorization, gateway, db, ct) is not { } person || voices.LanguageOf(person) is not { } language)
        {
            return null;
        }
        var field = Encoding.ASCII.GetBytes($"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"language\"\r\n\r\n{language}");
        return [.. body.AsSpan(0, at), .. field, .. body.AsSpan(at)];
    }

    /// <summary>The person a key belongs to: null for no key, a key the gateway does not know or blocked, or one of nobody here.</summary>
    private static async Task<AppUser?> PersonAsync(string authorization, ILiteLlm gateway, AppDbContext db, CancellationToken ct)
    {
        var key = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : "";
        if (key.Length == 0)
        {
            return null;
        }
        GatewayKeyInfo? info;
        try
        {
            info = await gateway.KeyInfoAsync(key, ct);
        }
        catch (GatewayException)
        {
            return null;
        }
        if (info is not { Blocked: false, UserId: { Length: > 0 } email })
        {
            return null;
        }
        var normalized = email.ToUpperInvariant();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
