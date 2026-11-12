using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Http.Features;

namespace Llm.Api.Gateway;

/// <summary>
/// API keys' pictures, speech and transcriptions through the app's own gateway: let in as a chat
/// completion is (Admission: the key, its person, the model, the minute's limits, credit), with the person's voice and
/// language as KeySpeech gives them, a picture model loaded first; then sent by ModelGateway to the picture or speech
/// server and booked at its price. A transcription is always asked as verbose_json (its length prices it) and given back
/// in the format asked: json, text, srt, vtt or verbose_json.
/// </summary>
public static class KeyMedia
{
    /// <summary>The longest picture or speech request read (a transcription's sound may be larger).</summary>
    private const long MostRead = 64L * 1024 * 1024;

    private static Task ErrorAsync(HttpContext http, int status, string type, string message, int? retryAfter = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        http.Response.StatusCode = status;
        if (retryAfter is { } after)
        {
            http.Response.Headers.RetryAfter = after.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
        {
            http.Response.Headers[k] = v;
        }
        return http.Response.WriteAsJsonAsync(new { error = new { message, type, code = status.ToString(CultureInfo.InvariantCulture) } }, http.RequestAborted);
    }

    /// <summary>
    /// A browser's preflight answered by the app as OpenAI-compatible gateways answer it: any origin, the headers asked
    /// for, for a day. True when it was one.
    /// </summary>
    public static bool Preflight(HttpContext http)
    {
        var request = http.Request;
        if (!HttpMethods.IsOptions(request.Method))
        {
            return false;
        }
        var headers = http.Response.Headers;
        headers.AccessControlAllowOrigin = "*";
        headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        headers.AccessControlAllowHeaders = request.Headers.AccessControlRequestHeaders.ToString() is { Length: > 0 } asked ? asked : "authorization, content-type, x-api-key";
        headers.AccessControlMaxAge = "86400";
        http.Response.StatusCode = StatusCodes.Status204NoContent;
        return true;
    }

    /// <summary>True when it answered the request (a POST of a picture, speech or transcription); false: not one of them.</summary>
    public static async Task<bool> TryAsync(HttpContext http)
    {
        var request = http.Request;
        var own = http.RequestServices.GetRequiredService<ModelGateway>();
        var path = request.Path.Value ?? "";
        var call = path.EndsWith("/images/generations", StringComparison.Ordinal) ? "image" : path.EndsWith("/audio/speech", StringComparison.Ordinal) ? "speech"
            : path.EndsWith("/audio/transcriptions", StringComparison.Ordinal) ? "transcription" : null;
        if (!HttpMethods.IsPost(request.Method) || call is null)
        {
            return false;
        }
        var ct = http.RequestAborted;
        http.Response.Headers.AccessControlAllowOrigin = "*";
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = 512L * 1024 * 1024;
        }
        var auth = request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        var services = http.RequestServices;
        var media = services.GetRequiredService<Models.MediaControl>();
        var voices = services.GetRequiredService<VoiceCatalog>();

        JsonObject? json = null;
        IFormCollection? form = null;
        string? model;
        if (call == "transcription")
        {
            if (!request.HasFormContentType)
            {
                await ErrorAsync(http, 400, "invalid_request_error", "A transcription is a form: file and model.");
                return true;
            }
            form = await request.ReadFormAsync(ct);
            model = form["model"].ToString() is { Length: > 0 } m ? m : null;
        }
        else
        {
            using var whole = new MemoryStream();
            await request.Body.CopyToAsync(whole, ct);
            if (whole.Length > MostRead)
            {
                await ErrorAsync(http, 413, "invalid_request_error", "The request is too large.");
                return true;
            }
            try
            {
                json = JsonNode.Parse(whole.ToArray()) as JsonObject;
            }
            catch (JsonException)
            {
                json = null;
            }
            if (json is null)
            {
                await ErrorAsync(http, 400, "invalid_request_error", "The request is not a JSON object.");
                return true;
            }
            // The person's voice and speed, and its model, when the request names no voice.
            if (call == "speech" && key is { Length: > 0 } && await services.GetRequiredService<KeyBook>().ForAsync(key, ct) is { } speaker
                && await services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>().FindByIdAsync(speaker.UserId.ToString()) is { } person
                && await KeySpeech.WithVoiceAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), person, voices, ct) is { } voiced)
            {
                json = JsonNode.Parse(voiced) as JsonObject ?? json;
            }
            model = json["model"]?.ToString();
        }

        var check = new JsonObject { ["model"] = model, ["messages"] = new JsonArray() };
        if (call == "image" && json?["prompt"]?.ToString() is { } prompt)
        {
            check["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt });
        }
        else if (call == "speech" && json?["input"]?.ToString() is { } input)
        {
            check["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = input });
        }
        var (admitted, refusal) = await services.GetRequiredService<Admission>().CheckAsync(key, check, ct);
        if (refusal is not null || admitted is null)
        {
            if (key is { Length: > 0 } && await services.GetRequiredService<KeyBook>().ForAsync(key, ct) is { } holder)
            {
                own.Refused(new GatewayCaller(holder.Email, "api", KeyHash: holder.Hash, KeyAlias: holder.Alias), model, refusal!.Status, refusal.Reason, refusal.Message);
            }
            await ErrorAsync(http, refusal!.Status, refusal.Type, refusal.Message, refusal.RetryAfter, refusal.Headers);
            return true;
        }
        // The safeguards' masking of a picture's prompt or a text read aloud.
        if (call == "image" && check["messages"]![0]?["content"]?.ToString() is { } maskedPrompt)
        {
            json!["prompt"] = maskedPrompt;
        }
        else if (call == "speech" && check["messages"]![0]?["content"]?.ToString() is { } maskedInput)
        {
            json!["input"] = maskedInput;
        }
        var caller = new GatewayCaller(admitted.Key.Email, admitted.Key.Kind == Llm.Core.Gateway.ApiKey.Studio ? "studio" : "api", KeyHash: admitted.Key.Hash, KeyAlias: admitted.Key.Alias);

        if (call == "image" && model is not null && media.Find(model) is { Mode: Llm.Core.Models.MediaModes.Image } local)
        {
            // Its turn on the GPU as the chat's pictures take it, the model loaded first.
            http.Response.RegisterForDisposeAsync(await services.GetRequiredService<Models.GpuShare>().HoldAsync(local, ct));
            if (await media.ReadyAsync(model!, ct) is { } notReady)
            {
                await ErrorAsync(http, 503, "model_not_ready", notReady, 30);
                return true;
            }
        }
        else if (model is not null)
        {
            media.Touch(model);
        }

        var asked = form?["response_format"].ToString() is { Length: > 0 } f ? f : "json";
        // The language the person speaks, when the form names none.
        var language = call == "transcription" && string.IsNullOrEmpty(form!["language"].ToString()) ? await voices.LanguageOfAsync(admitted.User, ct) : null;
        HttpResponseMessage res;
        try
        {
            res = call switch
            {
                "transcription" => await own.MediaAsync(path, model!, () => Transcription(form!, model!, language), "application/json", caller, ct,
                    passErrors: true),
                "image" => await own.MediaAsync(path, model!, () => new StringContent(json!.ToJsonString(), Encoding.UTF8, "application/json"), "application/json", caller, ct,
                    pictures: json!["n"] is JsonValue n && n.TryGetValue<int>(out var count) ? Math.Clamp(count, 1, 10) : 1, passErrors: true),
                _ => await own.MediaAsync(path, model!, () => new StringContent(json!.ToJsonString(), Encoding.UTF8, "application/json"), "audio/mpeg", caller, ct,
                    characters: json!["input"]?.ToString().Length ?? 0, passErrors: true),
            };
        }
        catch (ChatGatewayException ex)
        {
            await ErrorAsync(http, ex.Status ?? 502, ex.Status == 404 ? "model_not_found" : "upstream_error", ex.Message);
            return true;
        }
        using (res)
        {
            http.Response.StatusCode = (int)res.StatusCode;
            if (call == "transcription" && res.IsSuccessStatusCode)
            {
                var (text, type) = Render(await res.Content.ReadAsStringAsync(ct), asked);
                http.Response.ContentType = type;
                await http.Response.WriteAsync(text, ct);
                return true;
            }
            foreach (var (name, values) in res.Headers.Concat(res.Content.Headers))
            {
                if (name is not ("Transfer-Encoding" or "Connection" or "Keep-Alive"))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await using var upstream = await res.Content.ReadAsStreamAsync(ct);
            // The sound goes on as the speech server writes it.
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await upstream.ReadAsync(buffer, ct)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        return true;
    }

    /// <summary>The form as the speech server is asked: verbose_json always (its length prices it), the person's language when the form names none.</summary>
    private static MultipartFormDataContent Transcription(IFormCollection form, string model, string? language)
    {
        var sent = new MultipartFormDataContent();
        if (language is not null)
        {
            sent.Add(new StringContent(language), "language");
        }
        foreach (var (name, values) in form.Where(f => f.Key is not ("response_format" or "model" or "file")))
        {
            foreach (var v in values)
            {
                sent.Add(new StringContent(v ?? ""), name);
            }
        }
        sent.Add(new StringContent(model), "model");
        sent.Add(new StringContent("verbose_json"), "response_format");
        foreach (var file in form.Files)
        {
            var part = new StreamContent(file.OpenReadStream());
            part.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out var t) ? t : new MediaTypeHeaderValue("application/octet-stream");
            sent.Add(part, file.Name, file.FileName);
        }
        return sent;
    }

    /// <summary>A verbose_json transcription in the format asked: json ({"text"}), text, srt, vtt, or as it came.</summary>
    public static (string Text, string ContentType) Render(string verbose, string format)
    {
        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(verbose);
        }
        catch (JsonException)
        {
            return (verbose, "application/json");
        }
        var text = answer?["text"]?.ToString() ?? "";
        var segments = (answer?["segments"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => (Start: s["start"]?.GetValue<double>() ?? 0, End: s["end"]?.GetValue<double>() ?? 0, Text: (s["text"]?.ToString() ?? "").Trim())).ToList();
        if (segments.Count == 0 && text.Length > 0)
        {
            segments.Add((0, answer?["duration"]?.GetValue<double>() ?? 0, text.Trim()));
        }
        static string Time(double s, char comma)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{comma}{t.Milliseconds:000}";
        }
        switch (format)
        {
            case "text":
                return (text, "text/plain; charset=utf-8");
            case "srt":
                return (string.Concat(segments.Select((s, i) => $"{i + 1}\n{Time(s.Start, ',')} --> {Time(s.End, ',')}\n{s.Text}\n\n")), "application/x-subrip; charset=utf-8");
            case "vtt":
                return ("WEBVTT\n\n" + string.Concat(segments.Select(s => $"{Time(s.Start, '.')} --> {Time(s.End, '.')}\n{s.Text}\n\n")), "text/vtt; charset=utf-8");
            case "verbose_json":
                return (verbose, "application/json");
            default:
                return (new JsonObject { ["text"] = text }.ToJsonString(), "application/json");
        }
    }
}
