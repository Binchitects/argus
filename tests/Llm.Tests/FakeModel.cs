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
///   [steady]    streams 60 small pieces, 25 ms apart (for leaving the page mid-answer)
///   [ponder]    thinks in 400 small pieces, 25 ms apart, then answers; with thinking off, answers "Quick answer." at once
/// Asked to compact a chat (the summarizer's system prompt), it answers
/// "Summary of N characters." (N: the length of what it was given). Asked to sort a question
/// for Auto, it answers the kind a [kind:X] marker names (lookup without one; [kind:?] is not
/// JSON); asked for a chat's title, "Title: "Named &lt;the first three words&gt;".".
///   [budget]   refuses as LiteLLM does when credit is used up
///   [harm]      flagged (as weapons) by the safeguards' check
///   [call NAME {json}]  asks for any tool NAME with those arguments, then answers "Found it."
///   [script [[{"name":N,"arguments":{…}}, …], …]]  the k-th round of tool calls asks for the k-th list's calls, until
///               the lists run out or calling is switched off (tool_choice none); then answers "Found it."
///   [timed]     its last chunk carries llama.cpp's timings: 60 prompt tokens read in 300 ms, 12 written in 1,200 ms
///   [offer {json}]  asks for remember with those arguments (the word itself in a message reads as the person asking to remember)
/// Its /v1/images/generations answers with a small PNG.
/// </summary>
public sealed class FakeModel : HttpMessageHandler
{
    public ConcurrentQueue<(JsonObject Body, Dictionary<string, string> Headers)> Requests { get; } = new();

    /// <summary>Keys the gateway no longer knows: requests with them get 401.</summary>
    public ConcurrentDictionary<string, bool> RevokedKeys { get; } = new();

    /// <summary>Picture requests (the image tool), as sent.</summary>
    public ConcurrentQueue<JsonObject> ImageRequests { get; } = new();

    /// <summary>Models the gateway does not know yet, once each: the first request for one is refused as LiteLLM does.</summary>
    public ConcurrentDictionary<string, bool> UnknownOnce { get; } = new();

    /// <summary>Models still loading, once each: the first request gets llama.cpp's 503 "Loading model" through LiteLLM.</summary>
    public ConcurrentDictionary<string, bool> LoadingOnce { get; } = new();

    /// <summary>What speech to text was sent (the form, as text), and what text to speech was asked.</summary>
    public ConcurrentQueue<string> Transcriptions { get; } = new();
    public ConcurrentQueue<JsonObject> SpeechRequests { get; } = new();

    /// <summary>A real 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter is { } key && RevokedKeys.ContainsKey(key))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Authentication Error, Invalid proxy server token passed."}}""") };
        }
        // Speech to text takes a form, and answers with what was said.
        if (request.RequestUri!.AbsolutePath.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
        {
            Transcriptions.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"What is the capital of France?"}""", Encoding.UTF8, "application/json") };
        }
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        // Text to speech answers with an MP3.
        if (request.RequestUri!.AbsolutePath.EndsWith("/audio/speech", StringComparison.Ordinal))
        {
            SpeechRequests.Enqueue(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x49, 0x44, 0x33, 4, 0]) { Headers = { ContentType = new("audio/mpeg") } } };
        }
        if (request.RequestUri!.AbsolutePath.EndsWith("/images/generations", StringComparison.Ordinal))
        {
            ImageRequests.Enqueue(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["created"] = 1, ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(Png) }) }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
        if (body["model"]?.GetValue<string>() is { } loading && LoadingOnce.TryRemove(loading, out _))
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"error":{"message":"litellm.ServiceUnavailableError: OpenAIException - Loading model. Received Model Group=x","code":"503"}}""", Encoding.UTF8, "application/json"),
            };
        }
        if (body["model"]?.GetValue<string>() is { } asked && UnknownOnce.TryRemove(asked, out _))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(new JsonObject
                {
                    ["error"] = new JsonObject { ["message"] = $"/chat/completions: Invalid model name passed in model={asked}. Call `/v1/models` to view available models for your key.", ["code"] = "400" },
                }.ToJsonString(), Encoding.UTF8, "application/json"),
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
        // The safeguards' check: "[harm]" in a message is flagged as weapons.
        if (messages[0]!["content"]?.GetValue<string>().StartsWith("You check messages sent to an AI assistant", StringComparison.Ordinal) == true)
        {
            var flagged = lastUser.Contains("[harm]", StringComparison.Ordinal);
            chunks = [Delta(new JsonObject { ["content"] = flagged ? "{\"flagged\": true, \"category\": \"weapons\"}" : "{\"flagged\": false, \"category\": null}" }), Finish("stop"), Usage(50, 0, 10)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        // Auto's sorting: the kind a "[kind:X]" marker says (the last one), "lookup" without one; "[kind:?]" is not JSON at all.
        if (messages[0]!["content"]?.GetValue<string>().StartsWith("You sort the questions people send", StringComparison.Ordinal) == true)
        {
            var marks = System.Text.RegularExpressions.Regex.Matches(lastUser, @"\[kind:([a-z?]+)\]");
            var kind = marks.Count > 0 ? marks[^1].Groups[1].Value : "lookup";
            var said = kind == "?" ? "I would say it is hard to tell." : $"{{\"kind\": \"{kind}\", \"reason\": \"looks like {kind}\"}}";
            chunks = [Delta(new JsonObject { ["content"] = said }), Finish("stop"), Usage(80, 0, 12)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        // A chat's title: "Title: <the message's first three words>."
        if (messages[0]!["content"]?.GetValue<string>().StartsWith("You name conversations", StringComparison.Ordinal) == true)
        {
            var words = lastUser.Replace("<message>", "", StringComparison.Ordinal).Replace("</message>", "", StringComparison.Ordinal)
                .Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(3);
            chunks = [Delta(new JsonObject { ["content"] = $"Title: \"Named {string.Join(' ', words)}\"." }), Finish("stop"), Usage(40, 0, 6)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        if (messages[0]!["content"]?.GetValue<string>().StartsWith("You compact a conversation", StringComparison.Ordinal) == true)
        {
            chunks = [Delta(new JsonObject { ["content"] = $"Summary of {lastUser.Length} characters." }), Finish("stop"), Usage(lastUser.Length / 4, 0, 8)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        var call = System.Text.RegularExpressions.Regex.Match(lastUser, @"\[call (\S+) (\{.*\})\]|\[(offer) (\{.*\})\]", System.Text.RegularExpressions.RegexOptions.Singleline);
        var script = lastUser.IndexOf("[script ", StringComparison.Ordinal);
        if (script >= 0)
        {
            // Round k (the rounds of tool calls since the question) asks for the k-th list of calls, while calling is allowed.
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(lastUser[(script + "[script ".Length)..]));
            var rounds = JsonNode.Parse(ref reader)!.AsArray();
            var question = messages.Select((m, i) => (m, i)).Last(x => x.m!["role"]!.GetValue<string>() == "user").i;
            var round = messages.Skip(question + 1).Count(m => m!["role"]!.GetValue<string>() == "assistant" && m["tool_calls"] is JsonArray);
            chunks = round < rounds.Count && body["tool_choice"]?.GetValue<string>() != "none"
                ?
                [
                    .. rounds[round]!.AsArray().Select((c, i) => Delta(new JsonObject
                    {
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = i, ["id"] = $"call_{round}_{i}", ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c!["name"]!.GetValue<string>(), ["arguments"] = c["arguments"]!.ToJsonString() },
                        }),
                    })),
                    Finish("tool_calls"),
                ]
                : [Delta(new JsonObject { ["content"] = "Found it." }), Finish("stop")];
        }
        else if (call.Success && !toolAnswered)
        {
            var (name, arguments) = call.Groups[1].Success ? (call.Groups[1].Value, call.Groups[2].Value) : ("remember", call.Groups[4].Value);
            chunks =
            [
                Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } }) }),
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
        else if (lastUser.Contains("[ponder]", StringComparison.Ordinal))
        {
            var off = body["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false;
            chunks = off
                ? [Delta(new JsonObject { ["content"] = "Quick answer." }), Finish("stop")]
                : [.. Enumerable.Range(0, 400).Select(i => Delta(new JsonObject { ["reasoning_content"] = $"t{i} " })), Delta(new JsonObject { ["content"] = "Slow answer." }), Finish("stop")];
            delay = off ? TimeSpan.Zero : TimeSpan.FromMilliseconds(25);
        }
        else if (lastUser.Contains("[steady]", StringComparison.Ordinal))
        {
            chunks = [.. Enumerable.Range(0, 60).Select(i => Delta(new JsonObject { ["content"] = $"s{i} " })), Finish("stop")];
            delay = TimeSpan.FromMilliseconds(25);
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
        chunks = chunks.Append(Usage(100, 40, 12, timed: lastUser.Contains("[timed]", StringComparison.Ordinal)));
        // Asked for the whole answer at once (an API caller): one chat.completion, as the gateway sends it.
        if (body["stream"]?.GetValue<bool>() != true)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Whole(chunks).ToJsonString(), Encoding.UTF8, "application/json") };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
    }

    /// <summary>The chunks as one chat.completion: the words, the finish and the usage.</summary>
    private static JsonObject Whole(IEnumerable<string> chunks)
    {
        var content = new StringBuilder();
        string? finish = null;
        JsonNode? usage = null;
        foreach (var c in chunks.Select(c => JsonNode.Parse(c)!))
        {
            content.Append(c["choices"]?[0]?["delta"]?["content"]?.GetValue<string>());
            finish = c["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? finish;
            usage = c["usage"]?.DeepClone() ?? usage;
        }
        return new JsonObject
        {
            ["id"] = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12], ["object"] = "chat.completion", ["created"] = 1, ["model"] = "fake",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() }, ["finish_reason"] = finish }),
            ["usage"] = usage,
        };
    }

    private static string Delta(JsonObject delta) =>
        new JsonObject { ["object"] = "chat.completion.chunk", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta }) }.ToJsonString();

    private static string Finish(string reason) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = reason }) }.ToJsonString();

    private static string Usage(int prompt, int cached, int completion, bool timed = false)
    {
        var chunk = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject() }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion, ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached } },
        };
        if (timed)
        {
            chunk["timings"] = new JsonObject
            {
                ["prompt_n"] = 60, ["prompt_ms"] = 300.0, ["prompt_per_second"] = 200.0, ["predicted_n"] = 12, ["predicted_ms"] = 1200.0, ["predicted_per_second"] = 10.0,
            };
        }
        return chunk.ToJsonString();
    }

    /// <summary>Written as it goes, with a pause between pieces, and stopping when the reader leaves.</summary>
    private sealed class SseContent : HttpContent
    {
        private readonly IEnumerable<string> chunks;
        private readonly TimeSpan delay;

        public SseContent(IEnumerable<string> chunks, TimeSpan delay)
        {
            this.chunks = chunks;
            this.delay = delay;
            // As LiteLLM says it.
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        }

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

        /// <summary>
        /// Read as it is written, as from a real server: HttpContent's own way reads
        /// the whole answer into a buffer before the first piece can be read.
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            var pipe = new System.IO.Pipelines.Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var c in chunks.Append("[DONE]"))
                    {
                        // The reader left (stopped, or the answer was abandoned): stop writing.
                        if ((await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {c}\n\n"))).IsCompleted)
                        {
                            return;
                        }
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay);
                        }
                    }
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex);
                }
            }, CancellationToken.None);
            return Task.FromResult(pipe.Reader.AsStream());
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
