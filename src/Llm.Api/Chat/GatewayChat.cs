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
/// The app's own calls to its models, through its own gateway (<see cref="Gateway.ModelGateway"/>: straight to the
/// engine, booked in the ledger here), a chat completion's SSE lines turned into events. A request to a model of this
/// engine goes to the slot the <see cref="SlotTable"/> chooses (id_slot): a conversation's turn to the slot that holds its
/// start, a side request to the last slot first (<see cref="EngineRoute"/>, which also makes room for a model that is not loaded).
/// </summary>
public sealed partial class GatewayChat(EngineRoute route, Models.MediaControl media, Gateway.ModelGateway own)
{
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
        if (Llm.Core.Models.EngineKinds.Priority(route.KindOf(asked)) && request["priority"] is null)
        {
            // Inside vLLM or SGLang the chat goes ahead of API keys (their requests carry none: 0; the lower goes first on both).
            request["priority"] = -1;
        }
        string[] fallbacks = [];
        if (route.FallbackOf(asked) is { } fallback && slot.Model != fallback)
        {
            // Should the model fail (its engine restarts), the gateway asks its fallback.
            fallbacks = [fallback];
        }
        if (slot.Slot is { } id)
        {
            request["id_slot"] = id;
        }
        else
        {
            request.Remove("id_slot");
        }
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? res;
            try
            {
                res = await own.ChatAsync(request, Caller(request, personEmail, conversation), fallbacks, ct);
            }
            catch (ChatGatewayException ex) when (ex.Message.Contains("Loading model", StringComparison.OrdinalIgnoreCase) && attempt <= 24)
            {
                // The engine is still loading the model (it says 503 meanwhile): wait for it, two minutes at most.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            await foreach (var e in ReadAsync(res, ct))
            {
                yield return e;
            }
            yield break;
        }
    }

    /// <summary>
    /// Who a request is for, as the ledger books it: the person, the part of the app that asked (its first tag that names no
    /// chat or project), its chat and its project (the tags chat:ID and project:ID, else the turn's chat).
    /// </summary>
    internal static Gateway.GatewayCaller Caller(JsonObject request, string personEmail, Guid? conversation)
    {
        var tags = (request["metadata"]?["tags"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();
        Guid? Id(string prefix) => tags.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal)) is { } t && Guid.TryParse(t[prefix.Length..], out var g) ? g : null;
        var tag = tags.FirstOrDefault(t => !t.StartsWith(Projects.ProjectTags.ChatPrefix, StringComparison.Ordinal) && !t.StartsWith(Projects.ProjectTags.ProjectPrefix, StringComparison.Ordinal));
        return new Gateway.GatewayCaller(personEmail, tag is null ? "chat" : "app", tag, Id(Projects.ProjectTags.ChatPrefix) ?? conversation, Id(Projects.ProjectTags.ProjectPrefix));
    }

    /// <summary>One picture from an image model at the gateway, as PNG bytes. The spend is the person's, as in chat.</summary>
    public async Task<byte[]> GenerateImageAsync(string model, string prompt, string size, string personEmail, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["size"] = size, ["n"] = 1, ["response_format"] = "b64_json", ["user"] = personEmail };
        using var res = await own.MediaAsync("/v1/images/generations", model, () => new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), "application/json",
            new Gateway.GatewayCaller(personEmail), ct, pictures: 1);
        var b64 = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["data"]?[0]?["b64_json"]?.GetValue<string>();
        return string.IsNullOrEmpty(b64) ? throw new ChatGatewayException("The image model sent no picture.") : Convert.FromBase64String(b64);
    }

    /// <summary>What was said in a sound, from the speech to text model at the gateway. The spend is the person's.</summary>
    /// <param name="contentType">An MP3 unless said otherwise (Talk sends what the browser recorded: WebM or MP4).</param>
    /// <param name="language">The language spoken (en, fa), when the person chose it; null lets Whisper hear which.</param>
    public async Task<string> TranscribeAsync(string model, byte[] sound, string fileName, string personEmail, CancellationToken ct, string contentType = "audio/mpeg",
        string? language = null)
    {
        media.Touch(model);
        // verbose_json says the sound's length: the gateway prices a transcription by it.
        using var res = await MediaPostAsync("/v1/audio/transcriptions", model, () =>
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
        }, personEmail, "application/json", ct);
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
        using var res = await MediaPostAsync("/v1/audio/transcriptions", model, () =>
        {
            var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(sound) { Headers = { ContentType = new MediaTypeHeaderValue("audio/mpeg") } }, "file", "meeting.mp3" },
                { new StringContent(model), "model" },
                { new StringContent("verbose_json"), "response_format" },
                { new StringContent("segment"), "timestamp_granularities[]" },
                { new StringContent("word"), "timestamp_granularities[]" },
                { new StringContent(personEmail), "user" },
            };
            if (language is not null)
            {
                form.Add(new StringContent(language), "language");
            }
            return form;
        }, personEmail, "application/json", ct);
        var answer = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        // With each word's time: parts cut where a speaker may change (a sentence's end, a pause), as short as a turn can be;
        // a segment of speech to text's own runs over several turns when nobody pauses long.
        var words = (answer?["words"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(w => new HeardPart(w["start"]?.GetValue<double>() ?? 0, w["end"]?.GetValue<double>() ?? 0, w["word"]?.GetValue<string>() ?? ""))
            .Where(w => w.Text.Trim().Length > 0).ToList();
        var parts = words.Count > 0 ? Utterances(words)
            : [.. (answer?["segments"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(s => new HeardPart(s["start"]?.GetValue<double>() ?? 0, s["end"]?.GetValue<double>() ?? 0, (s["text"]?.GetValue<string>() ?? "").Trim()))
                .Where(p => p.Text.Length > 0)];
        if (parts.Count == 0 && (answer?["text"]?.GetValue<string>() ?? "").Trim() is { Length: > 0 } all)
        {
            parts.Add(new HeardPart(0, answer?["duration"]?.GetValue<double>() ?? 0, all));
        }
        return (parts, answer?["language"]?.GetValue<string>(), answer?["duration"]?.GetValue<double>() ?? parts.LastOrDefault()?.End ?? 0);
    }

    /// <summary>A pause this long (seconds) between two words may be a new speaker.</summary>
    public const double TurnPause = 0.6;

    /// <summary>The longest part (seconds): one who speaks on is cut at a comma, or at any word past it.</summary>
    public const double LongestPart = 12;

    /// <summary>
    /// Words made parts that end where a speaker may change: after a sentence's end, at a pause of <see cref="TurnPause"/>,
    /// and past <see cref="LongestPart"/> at a comma (at any word at half as long again).
    /// </summary>
    public static List<HeardPart> Utterances(IReadOnlyList<HeardPart> words)
    {
        var parts = new List<HeardPart>();
        var said = new StringBuilder();
        double start = 0, end = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (said.Length > 0 && w.Start - end >= TurnPause)
            {
                Flush();
            }
            if (said.Length == 0)
            {
                start = w.Start;
            }
            said.Append(w.Text);
            end = w.End;
            var text = w.Text.TrimEnd();
            var length = end - start;
            if (text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!') || text.EndsWith('…') || text.EndsWith('。') || text.EndsWith('؟')
                || (length >= LongestPart && (text.EndsWith(',') || text.EndsWith('،'))) || length >= LongestPart * 1.5)
            {
                Flush();
            }
        }
        Flush();
        return parts;

        void Flush()
        {
            if (said.ToString().Trim() is { Length: > 0 } t)
            {
                parts.Add(new HeardPart(start, end, t));
            }
            said.Clear();
        }
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
        return MediaPostAsync("/v1/audio/speech", model, () => new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), personEmail,
            "audio/mpeg", ct, characters: text.Length);
    }

    /// <summary>A picture or speech request, to the app's own gateway.</summary>
    private Task<HttpResponseMessage> MediaPostAsync(string path, string model, Func<HttpContent> content, string personEmail, string accept, CancellationToken ct, int characters = 0) =>
        own.MediaAsync(path, model, content, accept, new Gateway.GatewayCaller(personEmail), ct, characters: characters);

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

    /// <summary>What the engine or the gateway says, as a sentence for the person. Credit is the common one.</summary>
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
            // A prompt that fits (vLLM and SGLang refuse one that leaves too little for its max_tokens) says no size.
            var size = overflow is { PromptTokens: { } n, ContextTokens: { } m } && n > m ? $" (about {n:N0} of {m:N0} tokens)" : "";
            return $"This chat is longer than the model can read{size}. Compact it (/compact), start a new chat, or remove large attachments.";
        }
        // The engine could not load the model: memory, most often (it needs more beside the models it keeps loaded).
        if (FailedLoad().Match(message) is { Success: true } failed)
        {
            return $"{failed.Groups[1].Value} could not be loaded: most often it needs more GPU memory than is free beside the models kept loaded. "
                + "Try again in a minute or choose another model; an admin can change its placement (Auto fits what is free) or what stays loaded, under Admin → Models.";
        }
        // Error classes, which say nothing to the person ("InternalServerError: OpenAIException - ").
        return "The model could not answer: " + GatewayClasses().Replace(message, "").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"model name=(\S+) failed to load")]
    private static partial System.Text.RegularExpressions.Regex FailedLoad();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b\w*(?:Error|Exception)\b\s*[:-]\s*")]
    private static partial System.Text.RegularExpressions.Regex GatewayClasses();
}
