using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A model the gateway serves, with what it says it can do (unknown: tools and thinking yes, as the chat assumes).</summary>
internal sealed record ModelInfo(string Id)
{
    public int? Context { get; init; }
    public int? MaxOutput { get; init; }
    public bool Tools { get; init; } = true;
    public bool Thinking { get; init; } = true;
    /// <summary>Prices per token, when the gateway has them.</summary>
    public decimal? InputCost { get; init; }
    public decimal? CachedCost { get; init; }
    public decimal? OutputCost { get; init; }
}

/// <summary>The tokens of one answer: read (and of those, read from the cache) and written.</summary>
internal sealed record TokenUsage(long Prompt, long Cached, long Completion);

/// <summary>An answer: the assistant message to keep (text and tool calls), its tokens and why it ended.</summary>
internal sealed record Completion(JsonObject Message, TokenUsage? Usage, string? FinishReason)
{
    public string Text => Message.Str("content") ?? "";
    public JsonArray ToolCalls => Message["tool_calls"] as JsonArray ?? [];
}

/// <summary>Where an answer's pieces go while it streams.</summary>
internal interface IStreamSink
{
    void Text(string text);
    void Reasoning(string text);
}

internal sealed class GatewayException(string message, int? status = null, bool perMinute = false) : Exception(message)
{
    public int? Status { get; } = status;

    /// <summary>Refused for the key's requests or tokens a minute: asking again within seconds is refused too.</summary>
    public bool PerMinute { get; } = perMinute;
}

/// <summary>The gateway's OpenAI-compatible API, with the person's key.</summary>
/// <summary>The gateway's stream went quiet for too long: the engine stopped, or the connection did without saying so.</summary>
internal sealed class StreamStalledException(string message) : IOException(message);

internal sealed partial class GatewayClient(HttpClient http, string baseUrl, string key)
{
    /// <summary>The finish reason of an answer cut short by a lost connection after part of it arrived (what arrived is kept).</summary>
    public const string Interrupted = "interrupted";

    /// <summary>How long the stream may stay quiet before its first event (a long prompt is read first) and between two.</summary>
    public TimeSpan FirstEventWait { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan EventWait { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The waits before each new try of a request the gateway could not take (it restarts, a model loads): about a minute and a half.</summary>
    public TimeSpan[] RetryWaits { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45)];

    /// <summary>Told before each new try: what failed, the wait, which try; and when an answer is slow to start.</summary>
    public Action<string>? Retrying { get; set; }

    /// <summary>When the person is told that the answer has not started yet.</summary>
    public TimeSpan SlowNotice { get; set; } = TimeSpan.FromSeconds(60);

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    /// <summary>The ids /v1/models lists for this key.</summary>
    public async Task<List<string>> ModelIdsAsync(CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, "/v1/models");
        using var timeout = Deadline(ct, TimeSpan.FromSeconds(30));
        using var res = await Send(req, timeout.Token);
        var body = await res.Content.ReadAsStringAsync(timeout.Token);
        if (!res.IsSuccessStatusCode)
        {
            throw Failure(res.StatusCode, body);
        }
        var data = Json.ParseObject(body)?["data"] as JsonArray ?? throw new GatewayException($"{BaseUrl}/v1/models did not answer with a model list. Is this the gateway's address?");
        return [.. data.Select(m => m.Str("id")).OfType<string>().Distinct()];
    }

    /// <summary>The models, with their windows and abilities from /v1/model/info when the gateway shares it.</summary>
    public async Task<List<ModelInfo>> ModelsAsync(CancellationToken ct)
    {
        var ids = await ModelIdsAsync(ct);
        var info = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            using var req = Request(HttpMethod.Get, "/v1/model/info");
            using var timeout = Deadline(ct, TimeSpan.FromSeconds(15));
            using var res = await Send(req, timeout.Token);
            if (res.IsSuccessStatusCode && Json.ParseObject(await res.Content.ReadAsStringAsync(timeout.Token))?["data"] is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonObject>())
                {
                    if (row.Str("model_name") is { } name && row["model_info"] is JsonObject mi)
                    {
                        info.TryAdd(name, mi);
                    }
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Not every gateway shares it: the window then comes from the config, or a default.
        }
        return [.. ids.Select(id =>
        {
            if (!info.TryGetValue(id, out var mi))
            {
                return new ModelInfo(id);
            }
            if (mi.Str("mode") is { } mode && mode != "chat")
            {
                return null;
            }
            return new ModelInfo(id)
            {
                Context = mi.Int("max_input_tokens"),
                MaxOutput = mi.Int("max_output_tokens"),
                Tools = mi.Bool("supports_function_calling") ?? true,
                Thinking = mi.Bool("supports_reasoning") ?? true,
                InputCost = mi.Decimal("input_cost_per_token"),
                CachedCost = mi.Decimal("cache_read_input_token_cost"),
                OutputCost = mi.Decimal("output_cost_per_token"),
            };
        }).OfType<ModelInfo>()];
    }

    /// <summary>
    /// One answer, streamed: text and reasoning go to the sink as they come, tool
    /// calls are put together from their pieces. A gateway that is briefly away
    /// (502, 503, 504, 429, a dropped connection) is tried again while nothing has
    /// been shown yet; a key past its requests or tokens a minute is not (the
    /// minute is not over in a few seconds, and each try counts).
    /// </summary>
    public async Task<Completion> CompleteAsync(JsonObject body, IStreamSink? sink, CancellationToken ct)
    {
        var request = body.Clone();
        request["stream"] = true;
        request["stream_options"] = new JsonObject { ["include_usage"] = true };
        var shown = false;
        var guard = new GuardSink(sink, () => shown = true);
        var stalls = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await StreamOnceAsync(request, guard, ct);
            }
            // An answer that never started is asked once more, not five times: each such try waits the whole first wait.
            catch (Exception e) when (!shown && attempt < RetryWaits.Length && !ct.IsCancellationRequested && Retryable(e) && (e is not StreamStalledException || stalls++ < 1))
            {
                var wait = RetryWaits[attempt];
                Retrying?.Invoke($"The gateway did not answer ({Fmt.OneLine(e.Message, 120)}): trying again in {wait.TotalSeconds:0} s ({attempt + 2} of {RetryWaits.Length + 1}).");
                await Task.Delay(wait, ct);
            }
        }
    }

    internal static bool Retryable(Exception e)
    {
        if (e is GatewayException g)
        {
            return (g.Status is 429 or 502 or 503 or 504) && !g.PerMinute;
        }
        if (e is not (HttpRequestException or IOException))
        {
            return false;
        }
        // A certificate or a name that does not resolve will not mend in a few seconds.
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound })
            {
                return false;
            }
        }
        return true;
    }

    private async Task<Completion> StreamOnceAsync(JsonObject request, IStreamSink sink, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, "/v1/chat/completions");
        req.Content = new StringContent(Json.Line(request), Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // A watchdog: no answer at all for too long (not even its headers), or the stream quiet for too long, is a stalled one.
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(FirstEventWait);
        // The person is told when the answer is slow to start (the engine busy with others, or reading a long prompt).
        var talking = false;
        using var slow = new Timer(_ =>
        {
            if (!Volatile.Read(ref talking))
            {
                Retrying?.Invoke($"The model has not started answering for {SlowNotice.TotalSeconds:0} s (the engine may be busy, or reading a long prompt): Ctrl+C or Stop ends the wait.");
            }
        }, null, SlowNotice, Timeout.InfiniteTimeSpan);
        HttpResponseMessage started;
        try
        {
            started = await Send(req, quiet.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StreamStalledException($"the answer did not start (nothing for {FirstEventWait.TotalMinutes:0.#} minutes)");
        }
        using var res = started;
        if (!res.IsSuccessStatusCode)
        {
            throw Failure(res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        if (res.Content.Headers.ContentType?.MediaType == "application/json")
        {
            // A gateway that ignored stream: true answers whole.
            var whole = Json.ParseObject(await res.Content.ReadAsStringAsync(ct)) ?? throw new GatewayException("The gateway's answer was not JSON.");
            var message = First(whole["choices"])?["message"] as JsonObject ?? new JsonObject { ["content"] = "" };
            if (message.Str("reasoning_content") is { Length: > 0 } r)
            {
                sink.Reasoning(r);
            }
            if (message.Str("content") is { Length: > 0 } t)
            {
                sink.Text(t);
            }
            var calls = message["tool_calls"] as JsonArray;
            return new Completion(Assistant(message.Str("content") ?? "", calls?.DeepClone() as JsonArray), ReadUsage(whole["usage"]), First(whole["choices"]).Str("finish_reason"));
        }

        var text = new StringBuilder();
        var pieces = new SortedDictionary<int, ToolCallPieces>();
        TokenUsage? usage = null;
        string? finish = null;
        var said = false;
        var ended = false;
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        try
        {
            await foreach (var ev in Sse.ReadAsync(stream, quiet.Token))
            {
                Volatile.Write(ref talking, true);
                quiet.CancelAfter(EventWait);
                if (Event(ev.Data))
                {
                    ended = true;
                    break;
                }
            }
            // A connection closed without the stream's end (no [DONE], no finish reason): cut short, not finished.
            if (!ended && finish is null)
            {
                throw new IOException("the connection closed before the answer's end");
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is OperationCanceledException || Retryable(e)))
        {
            var why = e is OperationCanceledException ? new StreamStalledException($"the answer stopped coming (nothing for {(said ? EventWait : FirstEventWait).TotalMinutes:0.#} minutes)") : e;
            if (!said)
            {
                throw why;
            }
            // Part of the answer reached the person: it is kept, and the agent asks for the rest (its unfinished calls are dropped).
            return new Completion(Assistant(text.ToString(), null), usage, Interrupted);
        }

        // One event of the stream; true at its end.
        bool Event(string data)
        {
            if (data == "[DONE]")
            {
                return true;
            }
            var chunk = Json.ParseObject(data);
            if (chunk is null)
            {
                return false;
            }
            if (chunk["error"] is JsonObject error)
            {
                throw new GatewayException("The gateway stopped the answer: " + (error.Str("message") ?? error.ToJsonString()));
            }
            if (chunk["usage"] is JsonObject u)
            {
                usage = ReadUsage(u);
            }
            var choice = First(chunk["choices"]);
            if (choice is null)
            {
                return false;
            }
            finish = choice.Str("finish_reason") ?? finish;
            var delta = choice["delta"];
            if ((delta.Str("reasoning_content") ?? delta.Str("reasoning")) is { Length: > 0 } reasoning)
            {
                said = true;
                sink.Reasoning(reasoning);
            }
            if (delta.Str("content") is { Length: > 0 } content)
            {
                said = true;
                text.Append(content);
                sink.Text(content);
            }
            if (delta?["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls.OfType<JsonObject>())
                {
                    var index = call.Int("index") ?? pieces.Count;
                    if (!pieces.TryGetValue(index, out var p))
                    {
                        pieces[index] = p = new ToolCallPieces();
                    }
                    p.Id ??= call.Str("id") is { Length: > 0 } id ? id : null;
                    p.Name ??= call["function"].Str("name") is { Length: > 0 } name ? name : null;
                    p.Arguments.Append(call["function"].Str("arguments"));
                }
            }
            return false;
        }
        JsonArray? toolCalls = null;
        if (pieces.Count > 0)
        {
            toolCalls = [];
            var n = 0;
            foreach (var p in pieces.Values.Where(p => p.Name is not null))
            {
                n++;
                toolCalls.Add(new JsonObject
                {
                    ["id"] = p.Id ?? $"call_{Guid.NewGuid():N}"[..20] + n,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = p.Name, ["arguments"] = p.Arguments.Length > 0 ? p.Arguments.ToString() : "{}" },
                });
            }
        }
        return new Completion(Assistant(text.ToString(), toolCalls), usage, finish);
    }

    private sealed class ToolCallPieces
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Arguments = new();
    }

    /// <summary>Notes whether anything reached the person, so a retry never shows an answer twice.</summary>
    private sealed class GuardSink(IStreamSink? inner, Action shown) : IStreamSink
    {
        public void Text(string text)
        {
            shown();
            inner?.Text(text);
        }

        public void Reasoning(string text)
        {
            shown();
            inner?.Reasoning(text);
        }
    }

    private static JsonNode? First(JsonNode? choices) => choices is JsonArray { Count: > 0 } list ? list[0] : null;

    private static JsonObject Assistant(string text, JsonArray? toolCalls)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
        if (toolCalls is { Count: > 0 })
        {
            message["tool_calls"] = toolCalls;
        }
        return message;
    }

    private static TokenUsage? ReadUsage(JsonNode? u)
    {
        if (u is not JsonObject o)
        {
            return null;
        }
        var cached = o["prompt_tokens_details"].Long("cached_tokens") ?? o.Long("cache_read_input_tokens") ?? 0;
        return new TokenUsage(o.Long("prompt_tokens") ?? 0, cached, o.Long("completion_tokens") ?? 0);
    }

    /// <summary>
    /// A plain text completion (/v1/completions): what follows a prompt, as code completion in the editor asks for it.
    /// Not tried again: the person types on, and the next asks anew.
    /// </summary>
    public async Task<(string Text, TokenUsage? Usage)> TextCompleteAsync(JsonObject body, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, "/v1/completions");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Send(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw Failure(res.StatusCode, text);
        }
        var o = Json.ParseObject(text);
        var usage = o?["usage"] is JsonObject u ? new TokenUsage(u.Long("prompt_tokens") ?? 0, u["prompt_tokens_details"].Long("cached_tokens") ?? 0, u.Long("completion_tokens") ?? 0) : null;
        return ((o?["choices"] as JsonArray)?.FirstOrDefault().Str("text") ?? "", usage);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new HttpRequestException(Net.Explain(e, BaseUrl), e);
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken ct, TimeSpan after)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(after);
        return cts;
    }

    private GatewayException Failure(HttpStatusCode status, string body)
    {
        var detail = Json.ParseObject(body)?["error"] is JsonNode e ? e.Str("message") ?? e.ToString() : body.Trim();
        if (detail.Length > 400)
        {
            detail = detail[..400] + "…";
        }
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new GatewayException($"The gateway refused your API key ({(int)status}). Make a new one under Your account → API key, then run code-arena login.", (int)status),
            HttpStatusCode.NotFound when detail.Length == 0 || detail.StartsWith('<') =>
                new GatewayException($"{BaseUrl} has no OpenAI API (404). Is this the gateway's address (https://gateway.DOMAIN)?", 404),
            HttpStatusCode.TooManyRequests when RateLimit().Match(detail) is { Success: true } m && m.Groups[1].Value == "requests" =>
                new GatewayException($"Your API key reached its limit of {Count(m.Groups[2].Value, "request")} a minute. Try again in a minute; " +
                    "Your account → API key shows your limits and what you used.", 429, perMinute: true),
            // The gateway counts a request's prompt and answer before it runs: one bigger than the limit is refused every minute.
            HttpStatusCode.TooManyRequests when RateLimit().Match(detail) is { Success: true } m && m.Groups[1].Value == "tokens" =>
                new GatewayException($"Your API key reached its limit of {Count(m.Groups[2].Value, "token")} a minute. Try again in a minute; " +
                    "a request bigger than the limit (its prompt and the answer it asks for) is refused every time, so if this one is, " +
                    "make it smaller (/compact) or ask an admin to raise the limit. Your account → API key shows your limits and what you used.", 429, perMinute: true),
            HttpStatusCode.TooManyRequests when RateLimit().Match(detail) is { Success: true } m && m.Groups[1].Value == "max_parallel_requests" =>
                new GatewayException($"Your API key reached its limit of {Count(m.Groups[2].Value, "request")} at once. Wait for one to finish, then try again.", 429),
            _ => new GatewayException($"The gateway answered {(int)status}: {detail}", (int)status),
        };
    }

    /// <summary>A limit with its word: "1 request", "60 requests".</summary>
    private static string Count(string limit, string one) => limit == "1" ? $"1 {one}" : $"{limit} {one}s";

    /// <summary>The gateway's (LiteLLM's) refusal for a key's rate limit: "Limit type: requests. Current limit: 60, …".</summary>
    [GeneratedRegex(@"Limit type: (\w+)\. Current limit: (\d+)")]
    private static partial Regex RateLimit();
}
