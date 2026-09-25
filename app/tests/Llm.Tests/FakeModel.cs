using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// LiteLLM's /v1/chat/completions, streaming in the gateway's exact chunk format
/// (reasoning_content, content, tool_calls pieces, a usage chunk, [DONE]).
/// What it does depends on markers in the last user message:
///   [tool]      asks for find_symbol, then answers "Found it." once the tool result is back
///   [noaccess]  asks for find_symbol on something the person cannot read
///   [slow]      streams 400 small pieces, 25 ms apart (for stop)
///   [budget]    refuses as LiteLLM does when credit is used up
///   [call NAME {json}]  asks for any tool NAME with those arguments, then answers "Found it."
/// Its /v1/images/generations answers with a small PNG.
/// </summary>
public sealed class FakeModel : HttpMessageHandler
{
    public ConcurrentQueue<(JsonObject Body, Dictionary<string, string> Headers)> Requests { get; } = new();

    /// <summary>Keys the gateway no longer knows: requests with them get 401.</summary>
    public ConcurrentDictionary<string, bool> RevokedKeys { get; } = new();

    /// <summary>Picture requests (the image tool), as sent.</summary>
    public ConcurrentQueue<JsonObject> ImageRequests { get; } = new();

    /// <summary>A real 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter is { } key && RevokedKeys.ContainsKey(key))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Authentication Error, Invalid proxy server token passed."}}""") };
        }
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        if (request.RequestUri!.AbsolutePath.EndsWith("/images/generations", StringComparison.Ordinal))
        {
            ImageRequests.Enqueue(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["created"] = 1, ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(Png) }) }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
        Requests.Enqueue((body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
        var messages = body["messages"]!.AsArray();
        // A question with pictures comes as parts; its text is the text part.
        var lastUserContent = messages.Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!;
        var lastUser = lastUserContent is JsonArray parts
            ? string.Concat(parts.OfType<JsonObject>().Where(x => x["type"]?.GetValue<string>() == "text").Select(x => x["text"]!.GetValue<string>()))
            : lastUserContent.GetValue<string>();
        var toolAnswered = messages.Last()!["role"]!.GetValue<string>() == "tool";

        if (lastUser.Contains("[budget]", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"Budget has been exceeded! Current cost: 5.1, Max budget: 5.0","type":"budget_exceeded"}}"""),
            };
        }
        IEnumerable<string> chunks;
        var delay = TimeSpan.Zero;
        var call = System.Text.RegularExpressions.Regex.Match(lastUser, @"\[call (\S+) (\{.*\})\]", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (call.Success && !toolAnswered)
        {
            chunks =
            [
                Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = call.Groups[1].Value, ["arguments"] = call.Groups[2].Value } }) }),
                Finish("tool_calls"),
            ];
        }
        else if ((lastUser.Contains("[tool]", StringComparison.Ordinal) || lastUser.Contains("[noaccess]", StringComparison.Ordinal)) && !toolAnswered)
        {
            var symbol = lastUser.Contains("[noaccess]", StringComparison.Ordinal) ? "SecretThing" : "ParseHeader";
            chunks =
            [
                Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "find_symbol", ["arguments"] = "{\"name\":" } }) }),
                Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = $"\"{symbol}\"}}" } }) }),
                Finish("tool_calls"),
            ];
        }
        else if (toolAnswered)
        {
            chunks = [Delta(new JsonObject { ["content"] = "Found it." }), Finish("stop")];
        }
        else if (lastUser.Contains("[slow]", StringComparison.Ordinal))
        {
            chunks = [.. Enumerable.Range(0, 400).Select(i => Delta(new JsonObject { ["content"] = $"w{i} " })), Finish("stop")];
            delay = TimeSpan.FromMilliseconds(25);
        }
        else
        {
            chunks =
            [
                Delta(new JsonObject { ["reasoning_content"] = "Thinking about it." }),
                Delta(new JsonObject { ["content"] = "Answer to: " }),
                Delta(new JsonObject { ["content"] = lastUser[..Math.Min(20, lastUser.Length)] }),
                Finish("stop"),
            ];
        }
        chunks = chunks.Append(Usage(100, 40, 12));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
    }

    private static string Delta(JsonObject delta) =>
        new JsonObject { ["object"] = "chat.completion.chunk", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta }) }.ToJsonString();

    private static string Finish(string reason) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = reason }) }.ToJsonString();

    private static string Usage(int prompt, int cached, int completion) =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject() }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion, ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached } },
        }.ToJsonString();

    /// <summary>Written as it goes, with a pause between pieces, and stopping when the reader leaves.</summary>
    private sealed class SseContent(IEnumerable<string> chunks, TimeSpan delay) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            foreach (var c in chunks)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {c}\n\n"), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
            await stream.WriteAsync("data: [DONE]\n\n"u8.ToArray(), cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
