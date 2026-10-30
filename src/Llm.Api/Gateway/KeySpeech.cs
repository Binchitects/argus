using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Llm.Api.Gateway;

/// <summary>
/// Speech with API keys, by way of the app: Traefik sends gateway.DOMAIN's /v1/audio/speech and /v1/audio/transcriptions
/// here while the app is up, and straight to LiteLLM otherwise; the Helm chart's ingress sends them here always, with no
/// fallback. Text to speech that names no voice is read in the key's person's voice for its text's language (Your
/// account → Voice), at their speed when it names none; one that names no model either gets that voice's model. Speech
/// to text that names no language is written down in the language the person speaks, when they chose one. Nothing is
/// read before the key says whose request it is, and sound is never held: everything goes on to LiteLLM with the
/// caller's own key, which the gateway checks, and streams back.
/// </summary>
public static class KeySpeech
{
    public const string Client = "key-speech";

    /// <summary>
    /// The gateway's speech and picture paths that come by the app (with and without /v1, as LiteLLM takes both): a picture
    /// model that is not loaded is loaded first (its server runs one model at a time), as for the chat's own pictures.
    /// </summary>
    public static readonly IReadOnlyList<string> Paths =
        ["/v1/audio/speech", "/audio/speech", "/v1/audio/transcriptions", "/audio/transcriptions", "/v1/images/generations", "/images/generations"];

    /// <summary>Text to speech requests larger than this go on as they came.</summary>
    private const int MostRead = 1024 * 1024;

    public static async Task ProxyAsync(HttpContext http, AppDbContext db, VoiceCatalog voices, IHttpClientFactory factory, Models.MediaControl media)
    {
        var ct = http.RequestAborted;
        var request = http.Request;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            // The gateway decides, as when it is reached directly.
            limit.MaxRequestBodySize = null;
        }
        HttpContent? content = null;
        long? length = request.ContentLength;
        var picture = request.Path.Value?.EndsWith("/images/generations", StringComparison.Ordinal) == true;
        if (HttpMethods.IsPost(request.Method) && picture && request.ContentLength is > 0 and <= MostRead)
        {
            var body = await ReadAsync(request, ct);
            if (body is null)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            // A picture model of this stack's, whoever's key it is: loaded first (a few minutes the first time), or the caller told why not.
            if (SpokenBy(body) is { } model && media.Find(model) is { Mode: Llm.Core.Models.MediaModes.Image } local)
            {
                // The key's person's picture credit first: a request the gateway refuses anyway does not load the model for nothing.
                if (await PersonAsync(request.Headers.Authorization.ToString(), http.RequestServices.GetRequiredService<KeyBook>(), db, http.RequestServices.GetRequiredService<Access.AccessService>(), ct) is { } owner
                    && await http.RequestServices.GetRequiredService<Credit>().RefusalAsync(owner, Llm.Core.Access.CreditKind.Pictures, ct) is { } refusal)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await http.Response.WriteAsJsonAsync(new { error = new { message = refusal, type = "credit", code = "400" } }, ct);
                    return;
                }
                // Its turn on the GPU as the chat's pictures take it, until it is answered (the response's end lets it go).
                http.Response.RegisterForDisposeAsync(await http.RequestServices.GetRequiredService<Models.GpuShare>().HoldAsync(local, ct));
                if (await media.ReadyAsync(model, ct) is { } notReady)
                {
                    http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await http.Response.WriteAsJsonAsync(new { error = new { message = notReady, type = "model_not_ready", code = "503" } }, ct);
                    return;
                }
            }
            content = new ByteArrayContent(body);
            length = body.Length;
        }
        // Only a person's speech is changed: who it is comes first, from the key alone, and anyone else's goes on as it came.
        else if (HttpMethods.IsPost(request.Method) && !picture && await PersonAsync(request.Headers.Authorization.ToString(), http.RequestServices.GetRequiredService<KeyBook>(), db, http.RequestServices.GetRequiredService<Access.AccessService>(), ct) is { } person)
        {
            if (request.Path.Value?.EndsWith("/transcriptions", StringComparison.Ordinal) == true)
            {
                // Its model is in the form, which goes on as it came: every speech to text model counts as used.
                media.TouchMode("audio_transcription");
                if (await WithLanguageAsync(request.Body, request.ContentType, await voices.LanguageOfAsync(person, ct), ct) is (var form, var added))
                {
                    content = form;
                    length += added;
                }
            }
            else if (request.ContentLength is > 0 and <= MostRead)
            {
                var body = new byte[request.ContentLength.Value];
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
                body = await WithVoiceAsync(body, person, voices, ct) ?? body;
                if (SpokenBy(body) is { } model)
                {
                    media.Touch(model);
                }
                else
                {
                    media.TouchMode("audio_speech");
                }
                content = new ByteArrayContent(body);
                length = body.Length;
            }
        }
        // A request with no body (a browser's preflight) goes on with none.
        var hasBody = http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? (request.ContentLength > 0 || request.Headers.ContainsKey(HeaderNames.TransferEncoding));
        content ??= hasBody ? new StreamContent(request.Body) : null;

        using var forward = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(request.Path + request.QueryString, UriKind.Relative)) { Content = content };
        foreach (var (name, values) in request.Headers)
        {
            if (!AnswerCacheEndpoints.Dropped.Contains(name))
            {
                forward.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        if (content is not null)
        {
            if (request.ContentType is { } type)
            {
                content.Headers.TryAddWithoutValidation("Content-Type", type);
            }
            content.Headers.ContentLength = length;
        }
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

    /// <summary>The whole body (a request this short), or null when it ends early.</summary>
    private static async Task<byte[]?> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        var body = new byte[request.ContentLength!.Value];
        var read = 0;
        int n;
        while (read < body.Length && (n = await request.Body.ReadAsync(body.AsMemory(read), ct)) > 0)
        {
            read += n;
        }
        return read < body.Length ? null : body;
    }

    /// <summary>The model a text to speech request names, if it is JSON that names one.</summary>
    private static string? SpokenBy(byte[] body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject json && json["model"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    /// <summary>
    /// The form with a part for the language the person speaks put first, and the rest streamed on as it comes: a
    /// language the request names comes after it, and the gateway keeps a field's last value. Also how many bytes it
    /// adds. Null: as it came, with nothing read (no language chosen, not a form).
    /// </summary>
    public static async Task<(HttpContent Form, int Added)?> WithLanguageAsync(Stream body, string? contentType, string? language, CancellationToken ct)
    {
        if (language is null || !MediaTypeHeaderValue.TryParse(contentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
        {
            return null;
        }
        // The form's first boundary, with or without a line break before it; a form that starts with anything else goes as it came.
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var broken = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        var first = new byte[broken.Length];
        var read = 0;
        int n;
        while (read < first.Length && (n = await body.ReadAsync(first.AsMemory(read), ct)) > 0)
        {
            read += n;
        }
        var start = first[..read];
        // The part ends with the line break before the form's first boundary, which it may bring itself.
        var close = start.AsSpan().StartsWith(delimiter) ? "\r\n" : start.AsSpan().StartsWith(broken) ? "" : null;
        byte[] part = close is null ? [] : Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"language\"\r\n\r\n{language}{close}");
        return (new Spliced([.. part, .. start], body), part.Length);
    }

    /// <summary>The person a key belongs to: null for no key, a key the app does not know or expired, or one of nobody here.</summary>
    private static async Task<AppUser?> PersonAsync(string authorization, KeyBook book, AppDbContext db, Access.AccessService access, CancellationToken ct)
    {
        var key = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : "";
        if (key.Length == 0 || await book.ForAsync(key, ct) is not { } holder || holder.ExpiredAt(DateTimeOffset.UtcNow))
        {
            return null;
        }
        // A person whose API access was taken is not one here, whatever the key.
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == holder.UserId && !u.IsDisabled, ct);
        return user is null || await access.ApiOffAsync(user, ct) ? null : user;
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Bytes the app put first, then the rest of the caller's body as it comes.</summary>
    private sealed class Spliced(byte[] head, Stream rest) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await stream.WriteAsync(head, ct);
            await rest.CopyToAsync(stream, ct);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
