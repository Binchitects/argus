using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

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

internal sealed class GatewayException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>The gateway's OpenAI-compatible API, with the person's key.</summary>
internal sealed class GatewayClient(HttpClient http, string baseUrl, string key)
{
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
    /// been shown yet.
    /// </summary>
    public async Task<Completion> CompleteAsync(JsonObject body, IStreamSink? sink, CancellationToken ct)
    {
        var request = body.Clone();
        request["stream"] = true;
        request["stream_options"] = new JsonObject { ["include_usage"] = true };
        var shown = false;
        var guard = new GuardSink(sink, () => shown = true);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await StreamOnceAsync(request, guard, ct);
            }
            catch (Exception e) when (!shown && attempt < 2 && !ct.IsCancellationRequested && Retryable(e))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 2 : 5), ct);
            }
        }
    }

    private static bool Retryable(Exception e)
    {
        if (e is GatewayException g)
        {
            return g.Status is 429 or 502 or 503 or 504;
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
        using var res = await Send(req, ct);
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
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var ev in Sse.ReadAsync(stream, ct))
        {
            if (ev.Data == "[DONE]")
            {
                break;
            }
            var chunk = Json.ParseObject(ev.Data);
            if (chunk is null)
            {
                continue;
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
                continue;
            }
            finish = choice.Str("finish_reason") ?? finish;
            var delta = choice["delta"];
            if ((delta.Str("reasoning_content") ?? delta.Str("reasoning")) is { Length: > 0 } reasoning)
            {
                sink.Reasoning(reasoning);
            }
            if (delta.Str("content") is { Length: > 0 } content)
            {
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
            _ => new GatewayException($"The gateway answered {(int)status}: {detail}", (int)status),
        };
    }
}
