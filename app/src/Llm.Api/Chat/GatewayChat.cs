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

public sealed class ChatGatewayException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>Streams a chat completion from LiteLLM and turns its SSE lines into events.</summary>
public sealed class GatewayChat(HttpClient http, ChatKey key)
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(JsonObject request, string personEmail, [EnumeratorCancellation] CancellationToken ct)
    {
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

    private Task<HttpResponseMessage> OpenAsync(JsonObject request, string personEmail, CancellationToken ct) =>
        PostAsync("/v1/chat/completions", request, personEmail, "text/event-stream", ct);

    private async Task<HttpResponseMessage> PostAsync(string path, JsonObject request, string personEmail, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // Attribution (LiteLLM's user_header_mappings) and enforcement (the `user`
        // field, set by the caller): the same two signals identity-proxy gives chat.
        req.Headers.Add("X-OpenWebUI-User-Email", personEmail);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        try
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await key.GetAsync(ct));
        }
        catch (Gateway.GatewayException ex)
        {
            throw new ChatGatewayException("The model gateway is not reachable right now: " + ex.Message);
        }

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatGatewayException("The model gateway is not reachable right now.", null) { Data = { ["inner"] = ex.Message } };
        }
        if (!res.IsSuccessStatusCode)
        {
            using (res)
            {
                throw new ChatGatewayException(Explain(await res.Content.ReadAsStringAsync(ct), (int)res.StatusCode), (int)res.StatusCode);
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
                    throw new ChatGatewayException(Explain(err.ToJsonString(), 500));
                }
                if (chunk?["usage"] is JsonObject u && u["prompt_tokens"] is not null)
                {
                    yield return new UsageReport(
                        u["prompt_tokens"]?.GetValue<int>() ?? 0,
                        u["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0,
                        u["completion_tokens"]?.GetValue<int>() ?? 0);
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

    /// <summary>What LiteLLM says, as a sentence for the person. Credit is the common one.</summary>
    public static string Explain(string body, int status)
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
        if (message.Contains("context", StringComparison.OrdinalIgnoreCase) && message.Contains("length", StringComparison.OrdinalIgnoreCase))
        {
            return "This conversation is longer than the model can read. Start a new chat, or remove large attachments.";
        }
        return "The model could not answer: " + message;
    }
}
