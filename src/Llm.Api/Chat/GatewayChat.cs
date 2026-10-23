using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Api.Chat;

public abstract record StreamEvent;
public sealed record ReasoningDelta(string Text) : StreamEvent;
public sealed record ContentDelta(string Text) : StreamEvent;
public sealed record ToolCallDelta(int Index, string? Id, string? Name, string? Arguments) : StreamEvent;
public sealed record Finished(string? Reason) : StreamEvent;
public sealed record UsageReport(int Prompt, int Cached, int Completion) : StreamEvent;
/// <summary>The engine's own timings, when the stream carries them (llama.cpp's last chunk): the prompt tokens it read and how long that took, the tokens it wrote and how long.</summary>
public sealed record EngineTimings(double? ReadTokens, double? ReadMs, double? WrittenTokens, double? WriteMs) : StreamEvent;

public sealed class ChatGatewayException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;

    /// <summary>Nothing was sent: the model is the engine's and cannot be loaded now (the engine is full, or it failed to load and waits for its next try).</summary>
    public bool NotLoaded { get; init; }

    /// <summary>The prompt did not fit the model's context, as the engine or the gateway said (<see cref="ContextOverflow"/>): a compaction may make it fit.</summary>
    public bool ContextOverflow { get; init; }

    /// <summary>The engine's cache, shared by the requests it runs at once, was full: asking again in a moment may do, with no compaction.</summary>
    public bool SharedCache { get; init; }

    /// <summary>The prompt's tokens and the context's, as the refusal counted them, when it said.</summary>
    public long? PromptTokens { get; init; }
    public long? ContextTokens { get; init; }

    /// <summary>
    /// A refusal from the gateway or the engine: <paramref name="body"/> as a sentence for the person, and what it says of the
    /// context. <paramref name="status"/> null: an error in the stream, which has none of its own.
    /// </summary>
    public static ChatGatewayException From(string body, int? status)
    {
        var overflow = status is null or 400 or 413 or 422 or 500 ? Llm.Core.Chat.ContextOverflow.Parse(body) : default;
        return new ChatGatewayException(GatewayChat.Explain(body, status ?? 500, overflow), status)
        {
            ContextOverflow = overflow.Overflow, SharedCache = overflow.SharedCache, PromptTokens = overflow.PromptTokens, ContextTokens = overflow.ContextTokens,
        };
    }
}

/// <summary>
/// Streams a chat completion from LiteLLM and turns its SSE lines into events. A request to a model
/// of this engine goes to the slot the <see cref="SlotTable"/> chooses (id_slot, which the gateway
/// passes on): a conversation's turn to the slot that holds its start, a side request to the last
/// slot first (<see cref="EngineRoute"/>, which also makes room for a model that is not loaded).
/// </summary>
public sealed partial class GatewayChat(HttpClient http, ChatKey key, IServiceScopeFactory scopes, EngineRoute route, Models.MediaControl media)
{
    /// <summary>The person a request is for, as LiteLLM attributes spend (its user_header_mappings).</summary>
    public const string UserEmailHeader = "X-LLM-User-Email";

    /// <summary>A side request (a title, the safeguards' check, a summary, Auto's choice): it goes to the side requests' slot first (SlotTable).</summary>
    public IAsyncEnumerable<StreamEvent> StreamAsync(JsonObject request, string personEmail, CancellationToken ct) => StreamAsync(request, personEmail, null, ct);

    /// <summary>
    /// A turn of <paramref name="conversation"/> (or of a sub-agent's run; null: a side request): it goes back to the
    /// engine slot that holds the conversation's start.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(JsonObject request, string personEmail, Guid? conversation, [EnumeratorCancellation] CancellationToken ct)
    {
        var asked = request["model"] is JsonValue v && v.TryGetValue<string>(out var model) ? model : null;
        using var slot = await route.TakeAsync(asked, conversation, ct);
        if (slot.Model is { } routed)
        {
            // One copy of the model (the conversation's), or its fallback.
            request["model"] = routed;
        }
        if (route.KindOf(asked) == Llm.Core.Models.EngineKinds.Vllm && request["priority"] is null)
        {
            // Inside vLLM the chat goes ahead of API keys (their requests carry none: 0).
            request["priority"] = -1;
        }
        if (route.FallbackOf(asked) is { } fallback && slot.Model != fallback)
        {
            // Should the model fail mid-way (its engine restarts), the gateway asks its fallback.
            request["fallbacks"] = new JsonArray(fallback);
        }
        if (slot.Slot is { } id)
        {
            request["id_slot"] = id;
        }
        else
        {
            request.Remove("id_slot");
        }
        var registered = false;
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? res;
            try
            {
                res = await OpenAsync(request, personEmail, ct);
            }
            catch (ChatGatewayException ex) when (ex.Status == 401 && attempt == 1)
            {
                // The chat key was removed at the gateway: make a new one and try once more.
                await key.ForgetAsync(ct);
                continue;
            }
            catch (ChatGatewayException ex) when (ex.Message.Contains("Loading model", StringComparison.OrdinalIgnoreCase) && attempt <= 24)
            {
                // The engine is still loading the model (it says 503 meanwhile): wait for it, two minutes at most.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            catch (ChatGatewayException ex) when (ex.Status == 400 && !registered && ex.Message.Contains("Invalid model name", StringComparison.OrdinalIgnoreCase))
            {
                // A model the app has not registered yet (a first start, the gateway just up): register now, ask again.
                registered = true;
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<Models.ModelCatalog>().SyncGatewayAsync(ct);
                continue;
            }
            await foreach (var e in ReadAsync(res, ct))
            {
                yield return e;
            }
            yield break;
        }
    }

    /// <summary>One picture from an image model at the gateway, as PNG bytes. The spend is the person's, as in chat.</summary>
    public async Task<byte[]> GenerateImageAsync(string model, string prompt, string size, string personEmail, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["size"] = size, ["n"] = 1, ["response_format"] = "b64_json", ["user"] = personEmail };
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                res = await PostAsync("/v1/images/generations", body, personEmail, "application/json", ct);
            }
            catch (ChatGatewayException ex) when (ex.Status == 401 && attempt == 1)
            {
                await key.ForgetAsync(ct);
                continue;
            }
            using (res)
            {
                var b64 = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"]?[0]?["b64_json"]?.GetValue<string>();
                return string.IsNullOrEmpty(b64) ? throw new ChatGatewayException("The image model sent no picture.") : Convert.FromBase64String(b64);
            }
        }
    }

    /// <summary>What was said in a sound, from the speech to text model at the gateway. The spend is the person's.</summary>
    /// <param name="contentType">An MP3 unless said otherwise (Talk sends what the browser recorded: WebM or MP4).</param>
    /// <param name="language">The language spoken (en, fa), when the person chose it; null lets Whisper hear which.</param>
    public async Task<string> TranscribeAsync(string model, byte[] sound, string fileName, string personEmail, CancellationToken ct, string contentType = "audio/mpeg",
        string? language = null)
    {
        media.Touch(model);
        // verbose_json says the sound's length: the gateway prices a transcription by it (a plain json answer is priced at nothing).
        using var res = await WithKeyAsync(() => PostAsync("/v1/audio/transcriptions", () =>
        {
            var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(sound) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } }, "file", fileName },
                { new StringContent(model), "model" },
                { new StringContent("verbose_json"), "response_format" },
                { new StringContent(personEmail), "user" },
            };
            if (language is not null)
            {
                form.Add(new StringContent(language), "language");
            }
            return form;
        }, personEmail, "application/json", ct), ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["text"]?.GetValue<string>() ?? "";
    }

    /// <summary>A part of a sound, as speech to text heard it: from when to when (seconds), and what was said.</summary>
    public sealed record HeardPart(double Start, double End, string Text);

    /// <summary>
    /// What was said in a long sound, part by part with their times (a meeting), and the language heard, from the speech to
    /// text model at the gateway. The spend is the person's.
    /// </summary>
    public async Task<(IReadOnlyList<HeardPart> Parts, string? Language, double Seconds)> TranscribePartsAsync(string model, byte[] sound, string personEmail,
        CancellationToken ct, string? language = null)
    {
        media.Touch(model);
        using var res = await WithKeyAsync(() => PostAsync("/v1/audio/transcriptions", () =>
        {
            var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(sound) { Headers = { ContentType = new MediaTypeHeaderValue("audio/mpeg") } }, "file", "meeting.mp3" },
                { new StringContent(model), "model" },
                { new StringContent("verbose_json"), "response_format" },
                { new StringContent("segment"), "timestamp_granularities[]" },
                { new StringContent(personEmail), "user" },
            };
            if (language is not null)
            {
                form.Add(new StringContent(language), "language");
            }
            return form;
        }, personEmail, "application/json", ct), ct);
        var answer = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var parts = (answer?["segments"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => new HeardPart(s["start"]?.GetValue<double>() ?? 0, s["end"]?.GetValue<double>() ?? 0, (s["text"]?.GetValue<string>() ?? "").Trim()))
            .Where(p => p.Text.Length > 0).ToList();
        if (parts.Count == 0 && (answer?["text"]?.GetValue<string>() ?? "").Trim() is { Length: > 0 } all)
        {
            parts.Add(new HeardPart(0, answer?["duration"]?.GetValue<double>() ?? 0, all));
        }
        return (parts, answer?["language"]?.GetValue<string>(), answer?["duration"]?.GetValue<double>() ?? parts.LastOrDefault()?.End ?? 0);
    }

    /// <summary>A text spoken by a text to speech model at the gateway, as MP3 bytes. The spend is the person's.</summary>
    public async Task<byte[]> SpeakAsync(string model, string text, string voice, string personEmail, CancellationToken ct, double speed = 1)
    {
        using var res = await OpenSpeechAsync(model, text, voice, personEmail, ct, speed);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>The same, as it is made: the response's MP3 can be passed on while the speech server still writes it.</summary>
    /// <param name="speed">0.5 to 2; 1 (as the voice speaks) is not sent.</param>
    public Task<HttpResponseMessage> OpenSpeechAsync(string model, string text, string voice, string personEmail, CancellationToken ct, double speed = 1)
    {
        media.Touch(model);
        var body = new JsonObject { ["model"] = model, ["input"] = text, ["voice"] = voice, ["response_format"] = "mp3", ["user"] = personEmail };
        if (speed != 1)
        {
            body["speed"] = speed;
        }
        return WithKeyAsync(() => PostAsync("/v1/audio/speech", body, personEmail, "audio/mpeg", ct), ct);
    }

    /// <summary>The chat key removed at the gateway: a new one is made and the request tried once more.</summary>
    private async Task<HttpResponseMessage> WithKeyAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            return await send();
        }
        catch (ChatGatewayException ex) when (ex.Status == 401)
        {
            await key.ForgetAsync(ct);
            return await send();
        }
    }

    private Task<HttpResponseMessage> OpenAsync(JsonObject request, string personEmail, CancellationToken ct) =>
        PostAsync("/v1/chat/completions", request, personEmail, "text/event-stream", ct);

    private Task<HttpResponseMessage> PostAsync(string path, JsonObject request, string personEmail, string accept, CancellationToken ct) =>
        PostAsync(path, () => new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), personEmail, accept, ct);

    private async Task<HttpResponseMessage> PostAsync(string path, Func<HttpContent> content, string personEmail, string accept, CancellationToken ct)
    {
        string bearer;
        try
        {
            bearer = await key.GetAsync(ct);
        }
        catch (Gateway.GatewayException ex)
        {
            throw new ChatGatewayException("The model gateway is not reachable right now: " + ex.Message);
        }

        HttpResponseMessage res;
        // A dropped connection (the gateway restarting, a new deployment settling) is tried again for about half a minute:
        // the gateway takes 10-20 s to start after an upgrade or a restart.
        int[] waits = [1, 2, 4, 8, 15];
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = content() };
            // Attribution (LiteLLM's user_header_mappings); enforcement is the body's
            // `user` field, set by the caller.
            req.Headers.Add(UserEmailHeader, personEmail);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            try
            {
                res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                break;
            }
            catch (HttpRequestException) when (attempt <= waits.Length)
            {
                await Task.Delay(TimeSpan.FromSeconds(waits[attempt - 1]), ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ChatGatewayException("The model gateway is not reachable right now.", null) { Data = { ["inner"] = ex.Message } };
            }
        }
        if (!res.IsSuccessStatusCode)
        {
            using (res)
            {
                throw ChatGatewayException.From(await res.Content.ReadAsStringAsync(ct), (int)res.StatusCode);
            }
        }
        return res;
    }

    private static async IAsyncEnumerable<StreamEvent> ReadAsync(HttpResponseMessage res, [EnumeratorCancellation] CancellationToken ct)
    {
        using (res)
        {
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    yield break;
                }
                JsonNode? chunk;
                try
                {
                    chunk = JsonNode.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (chunk?["error"] is JsonObject err)
                {
                    // An error in the stream (the engine's cache filled mid-answer, say): it has no status of its own.
                    throw ChatGatewayException.From(err.ToJsonString(), null);
                }
                if (chunk?["usage"] is JsonObject u && u["prompt_tokens"] is not null)
                {
                    yield return new UsageReport(
                        u["prompt_tokens"]?.GetValue<int>() ?? 0,
                        u["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0,
                        u["completion_tokens"]?.GetValue<int>() ?? 0);
                }
                if (chunk?["timings"] is JsonObject tm)
                {
                    yield return new EngineTimings(Number(tm["prompt_n"]), Number(tm["prompt_ms"]), Number(tm["predicted_n"]), Number(tm["predicted_ms"]));
                }
                if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
                {
                    continue;
                }
                if (choice["delta"] is JsonObject delta)
                {
                    if (Text(delta, "reasoning_content") is { Length: > 0 } r)
                    {
                        yield return new ReasoningDelta(r);
                    }
                    if (Text(delta, "content") is { Length: > 0 } c)
                    {
                        yield return new ContentDelta(c);
                    }
                    foreach (var call in (delta["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        yield return new ToolCallDelta(
                            call["index"]?.GetValue<int>() ?? 0,
                            Text(call, "id"),
                            Text(call["function"] as JsonObject, "name"),
                            Text(call["function"] as JsonObject, "arguments"));
                    }
                }
                if (Text(choice, "finish_reason") is { } reason)
                {
                    yield return new Finished(reason);
                }
            }
        }
    }

    private static string? Text(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Number(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>What LiteLLM says, as a sentence for the person. Credit is the common one.</summary>
    public static string Explain(string body, int status) => Explain(body, status, Llm.Core.Chat.ContextOverflow.Parse(body));

    internal static string Explain(string body, int status, Llm.Core.Chat.ContextOverflow overflow)
    {
        string? message = null;
        try
        {
            var node = JsonNode.Parse(body);
            message = node?["error"]?["message"]?.GetValue<string>() ?? node?["detail"]?.ToString() ?? node?["error"]?.ToString();
        }
        catch (JsonException)
        {
            message = body.Length > 200 ? body[..200] : body;
        }
        message ??= $"HTTP {status}";
        if (message.Contains("budget", StringComparison.OrdinalIgnoreCase) && message.Contains("exceed", StringComparison.OrdinalIgnoreCase))
        {
            return "You have used all your credit. Ask an admin to raise it.";
        }
        if (status == 429)
        {
            return "Too many requests right now. Wait a moment and try again.";
        }
        if (overflow.SharedCache)
        {
            return "The model's memory for the chats it answers at once was full. Ask again in a moment.";
        }
        if (overflow.Overflow)
        {
            var size = overflow is { PromptTokens: { } n, ContextTokens: { } m } ? $" (about {n:N0} of {m:N0} tokens)" : "";
            return $"This chat is longer than the model can read{size}. Compact it (/compact), start a new chat, or remove large attachments.";
        }
        // The engine could not load the model: memory, most often (it needs more beside the models it keeps loaded).
        if (FailedLoad().Match(message) is { Success: true } failed)
        {
            return $"{failed.Groups[1].Value} could not be loaded: most often it needs more GPU memory than is free beside the models kept loaded. "
                + "Try again in a minute or choose another model; an admin can change its placement (Auto fits what is free) or what stays loaded, under Admin → Models.";
        }
        // The gateway's classes, which say nothing to the person ("litellm.InternalServerError: InternalServerError: OpenAIException - ").
        return "The model could not answer: " + GatewayClasses().Replace(message, "").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"model name=(\S+) failed to load")]
    private static partial System.Text.RegularExpressions.Regex FailedLoad();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\blitellm\.)?\b\w*(?:Error|Exception)\b\s*[:-]\s*")]
    private static partial System.Text.RegularExpressions.Regex GatewayClasses();
}
