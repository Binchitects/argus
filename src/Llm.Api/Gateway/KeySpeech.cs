using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>
/// Text to speech with API keys, by way of the app: Traefik sends gateway.DOMAIN's /v1/audio/speech here while the app
/// is up, and straight to LiteLLM otherwise. A request that names no voice is read in the key's person's voice for its
/// text's language (Your account → Voice), at their speed when it names none; one that names no model either gets that
/// voice's model. Everything goes on to LiteLLM with the caller's own key, which the gateway checks, and streams back.
/// </summary>
public static class KeySpeech
{
    public const string Client = "key-speech";

    /// <summary>Requests larger than this go on as they came.</summary>
    private const int MostRead = 1024 * 1024;

    public static async Task ProxyAsync(HttpContext http, ILiteLlm gateway, AppDbContext db, VoiceCatalog voices, IHttpClientFactory factory)
    {
        var ct = http.RequestAborted;
        var request = http.Request;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            // The gateway decides, as when it is reached directly.
            limit.MaxRequestBodySize = null;
        }
        byte[]? body = null;
        if (request.ContentLength is > 0 and <= MostRead)
        {
            body = new byte[request.ContentLength.Value];
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
            body = await WithVoiceAsync(body, request.Headers.Authorization.ToString(), gateway, db, voices, ct) ?? body;
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
        var key = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : "";
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
        if (key.Length == 0 || json is null || Str(json, "voice") is { Length: > 0 } || Str(json, "input") is not { Length: > 0 } text)
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
        if (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct) is not { } person)
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
