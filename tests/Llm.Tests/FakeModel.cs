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
///   [harm]      flagged (as weapons) by the safeguards' judge; [injection] and [topic:X] too
///   [slow check]  the safeguards' check takes 10 seconds to answer
///   [call NAME {json}]  asks for any tool NAME with those arguments, then answers "Found it."
///   [script [[{"name":N,"arguments":{…}}, …], …]]  the k-th round of tool calls asks for the k-th list's calls, until
///               the lists run out or calling is switched off (tool_choice none); then answers "Found it."
///   [timed]     its last chunk carries llama.cpp's timings: 60 prompt tokens read in 300 ms, 12 written in 1,200 ms
///   [offer {json}]  asks for remember with those arguments (the word itself in a message reads as the person asking to remember)
///   [context overflow]       refused as LiteLLM refuses a prompt bigger than llama.cpp's context (<see cref="Overflow"/>)
///   [context overflow once]  refused so until the chat was compacted (a summary in the system message)
///   [summary limit N]  a summary asked of more than N characters that holds it is refused as too long
/// Its /v1/images/generations answers with a small PNG.
/// </summary>
public sealed class FakeModel : HttpMessageHandler
{
    public ConcurrentQueue<(JsonObject Body, Dictionary<string, string> Headers)> Requests { get; } = new();

    /// <summary>Keys the gateway no longer knows: requests with them get 401.</summary>
    public ConcurrentDictionary<string, bool> RevokedKeys { get; } = new();

    /// <summary>Picture requests (the image tool), as sent.</summary>
    public ConcurrentQueue<JsonObject> ImageRequests { get; } = new();

    /// <summary>Runs before a picture is answered (its prompt): a test holds a slow picture there.</summary>
    public Func<string, Task>? BeforeImage { get; set; }

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
            if (BeforeImage is { } before)
            {
                await before(body["prompt"]?.GetValue<string>() ?? "");
            }
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
        // What a tool showed the model (the browser's screen) comes as a user message after the step's results: not a question.
        static bool Shown(JsonNode? m) => m?["role"]?.GetValue<string>() == "user" && m["content"] is JsonArray p
            && p.OfType<JsonObject>().Any(x => x["text"]?.GetValue<string>()?.StartsWith("[What the screen shows now", StringComparison.Ordinal) == true);
        var lastUserContent = messages.Last(m => m!["role"]!.GetValue<string>() == "user" && !Shown(m))!["content"]!;
        var lastUser = lastUserContent is JsonArray parts
            ? string.Concat(parts.OfType<JsonObject>().Where(x => x["type"]?.GetValue<string>() == "text").Select(x => x["text"]!.GetValue<string>()))
            : lastUserContent.GetValue<string>();
        var toolAnswered = messages.Last()!["role"]!.GetValue<string>() == "tool" || Shown(messages.Last());

        if (lastUser.Contains("[budget]", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"Budget has been exceeded! Current cost: 5.1, Max budget: 5.0","type":"budget_exceeded"}}"""),
            };
        }
        IEnumerable<string> chunks;
        var delay = TimeSpan.Zero;
        // The safeguards' judge: "[harm]" matches the weapons item, "[injection]" the injection one, "[topic:X]" the item naming X;
        // "[slow check]" takes 10 seconds to say.
        if (messages[0]!["content"]?.GetValue<string>().StartsWith("You check texts at a company's AI assistant", StringComparison.Ordinal) == true)
        {
            var items = System.Text.RegularExpressions.Regex.Matches(messages[0]!["content"]!.GetValue<string>(), @"^(\d+)\. (.*)$", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => (Number: int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Text: m.Groups[2].Value)).ToList();
            var wanted = System.Text.RegularExpressions.Regex.Matches(lastUser, @"\[topic:([^\]]+)\]").Select(m => m.Groups[1].Value).ToList();
            if (lastUser.Contains("[harm]", StringComparison.Ordinal))
            {
                wanted.Add("weapons capable of mass casualties");
            }
            if (lastUser.Contains("[injection]", StringComparison.Ordinal))
            {
                wanted.Add("prompt injection");
            }
            var matched = items.Where(i => wanted.Any(a => i.Text.Contains(a, StringComparison.OrdinalIgnoreCase))).Select(i => i.Number);
            var slow = lastUser.Contains("[slow check]", StringComparison.Ordinal);
            chunks = [.. Enumerable.Repeat(Delta(new JsonObject { ["content"] = " " }), slow ? 100 : 0),
                Delta(new JsonObject { ["content"] = $"{{\"matched\": [{string.Join(", ", matched)}]}}" }), Finish("stop"), Usage(50, 0, 10)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, slow ? TimeSpan.FromMilliseconds(100) : delay) };
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
        // Code review: a finding on each diff's first added line that says "[bug]", refuted when it says "[harmless]"; then a summary.
        var system0 = messages[0]!["content"]?.GetValue<string>() ?? "";
        if (system0.StartsWith("You review code changes", StringComparison.Ordinal))
        {
            var hit = lastUser.Split('\n').FirstOrDefault(l => l.Contains("[bug]", StringComparison.Ordinal) && l.Length > 5 && int.TryParse(l[..5].Trim(), out _));
            var findings = hit is null ? "[]"
                : $$"""[{"line": {{int.Parse(hit[..5].Trim(), System.Globalization.CultureInfo.InvariantCulture)}}, "severity": "major", "category": "bug", "title": "Off by one{{(hit.Contains("[harmless]", StringComparison.Ordinal) ? " (harmless)" : "")}}", "body": "The loop reads one past the end.", "suggestion": "for (i = 0; i < n; i++)"}]""";
            chunks = [Delta(new JsonObject { ["content"] = $"```json\n{{\"findings\": {findings}}}\n```" }), Finish("stop"), Usage(300, 0, 40)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        // Asked for JSON only (Agent Studio's ask step): the JSON a "[json:…]" marker in the question holds.
        if (system0.EndsWith("Answer with JSON only, no other words.", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Match(lastUser, @"\[json:(.*)\]", System.Text.RegularExpressions.RegexOptions.Singleline) is { Success: true } jsonMarker)
        {
            chunks = [Delta(new JsonObject { ["content"] = jsonMarker.Groups[1].Value }), Finish("stop"), Usage(100, 0, 20)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        // Stories: as many shots as asked ("Write N shots"), each a fox in the snow, with a line to say over it.
        if (system0.StartsWith("You write storyboards", StringComparison.Ordinal))
        {
            var n = int.Parse(System.Text.RegularExpressions.Regex.Match(system0, @"Write (\d+) shots").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var shots = string.Join(",", Enumerable.Range(1, n).Select(i => $$$"""{"visual": "A red fox in the snow, shot {{{i}}}, slow push in", "narration": "Line {{{i}}} of the story.", "seconds": 3}"""));
            chunks = [Delta(new JsonObject { ["content"] = $$"""Here it is: {"title": "The fox in winter", "music": "calm piano, soft strings", "shots": [{{shots}}],}""" }), Finish("stop"), Usage(200, 0, 120)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        // Decks: a deck of a title slide, points (a picture), a chart and an odd layout; "[nojson]" answers in words the
        // first time. A change appends a slide on costs to the deck it was given (each picture's fileId dropped, as small
        // models do), or with "[retitle]" renames it.
        if (system0.StartsWith("You make slide decks", StringComparison.Ordinal))
        {
            var about = messages.AsArray().First(m => m!["role"]?.GetValue<string>() == "user")!["content"]!.GetValue<string>();
            var deck = about.Contains("[nojson]", StringComparison.Ordinal) && !lastUser.StartsWith("That was not the deck", StringComparison.Ordinal)
                ? "Here is a lovely deck about your quarter!"
                : """
                  ```json
                  {"title": "Our quarter", "slides": [
                    {"layout": "title", "title": "Our quarter", "subtitle": "What went well", "notes": "Welcome everyone."},
                    {"layout": "bullets", "title": "Highlights", "bullets": ["Sales grew", "Two new customers", ""], "picture": {"prompt": "A rising arrow over a city"}, "notes": "The good news first."},
                    {"layout": "chart", "title": "Sales by region", "chart": {"type": "bar", "categories": ["North", "South"], "series": [{"name": "Sales", "values": [120, 80]}]}, "bullets": ["North leads"]},
                    {"layout": "spiral", "title": "Next steps", "bullets": ["Hire two people"]}
                  ]}
                  ```
                  """;
            chunks = [Delta(new JsonObject { ["content"] = deck }), Finish("stop"), Usage(800, 0, 300)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        if (system0.StartsWith("You change slide decks", StringComparison.Ordinal))
        {
            var given = system0.Split('\n')[1];
            var deck = JsonNode.Parse(given)!.AsObject();
            foreach (var slide in deck["slides"]!.AsArray())
            {
                (slide!["picture"] as JsonObject)?.Remove("fileId");
            }
            if (lastUser.Contains("[retitle]", StringComparison.Ordinal))
            {
                deck["title"] = "Our quarter, again";
            }
            else
            {
                deck["slides"]!.AsArray().Add(new JsonObject { ["layout"] = "bullets", ["title"] = "Costs", ["bullets"] = new JsonArray("Rent", "Salaries") });
            }
            chunks = [Delta(new JsonObject { ["content"] = deck.ToJsonString() }), Finish("stop"), Usage(900, 0, 320)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        // Ask your data: the SQL a "[sql:…]" marker in the question says, chart "[chart:…]" (table by default); asked again
        // after a failure, the "[fix:…]" marker's SQL, else the same again. "[cannot]": no SQL, and why.
        if (system0.StartsWith("You answer questions about a PostgreSQL database", StringComparison.Ordinal))
        {
            var question = messages.AsArray().First(m => m!["role"]?.GetValue<string>() == "user")!["content"]!.GetValue<string>();
            string Marker(string name) => System.Text.RegularExpressions.Regex.Match(question, $@"\[{name}:(.*?)\](?!\])") is { Success: true } m ? m.Groups[1].Value : "";
            var again = lastUser.StartsWith("That query failed", StringComparison.Ordinal);
            var sql = question.Contains("[cannot]", StringComparison.Ordinal) ? "" : again && Marker("fix") is { Length: > 0 } fix ? fix : Marker("sql");
            var said = new JsonObject
            {
                ["sql"] = sql, ["explanation"] = sql.Length == 0 ? "The tables say nothing about the weather." : "Counts what was asked.", ["title"] = "The answer",
                ["chart"] = new JsonObject { ["type"] = Marker("chart") is { Length: > 0 } chart ? chart : "table" },
            };
            chunks = [Delta(new JsonObject { ["content"] = $"```json\n{said.ToJsonString()}\n```" }), Finish("stop"), Usage(400, 0, 40)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        if (system0.StartsWith("You check another reviewer's finding", StringComparison.Ordinal))
        {
            var real = !lastUser.Contains("(harmless)", StringComparison.Ordinal);
            chunks = [Delta(new JsonObject { ["content"] = $$"""{"real": {{(real ? "true" : "false")}}, "why": "{{(real ? "The diff shows it." : "The caller checks the bound.")}}"}""" }), Finish("stop"), Usage(200, 0, 20)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        if (system0.StartsWith("You write the summary of a code review", StringComparison.Ordinal))
        {
            chunks = [Delta(new JsonObject { ["content"] = "The change parses headers.\n\nLook at the loop first.\n{\"verdict\": \"changes\", \"risk\": \"medium\"}" }), Finish("stop"), Usage(250, 0, 30)];
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
            if (System.Text.RegularExpressions.Regex.Match(lastUser, @"\[summary limit (\d+)\]") is { Success: true } limit && lastUser.Length > int.Parse(limit.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            {
                return OverflowResponse(lastUser.Length / 2);
            }
            chunks = [Delta(new JsonObject { ["content"] = $"Summary of {lastUser.Length} characters." }), Finish("stop"), Usage(lastUser.Length / 4, 0, 8)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new SseContent(chunks, delay) };
        }
        if (lastUser.Contains("[context overflow]", StringComparison.Ordinal)
            || (lastUser.Contains("[context overflow once]", StringComparison.Ordinal) && !messages[0]!["content"]!.GetValue<string>().Contains("<summary>", StringComparison.Ordinal)))
        {
            return OverflowResponse(40_000);
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

    /// <summary>
    /// LiteLLM's refusal of a prompt bigger than llama.cpp's context (32,768 tokens), as the gateway sends it: a 400 whose
    /// message is ContextWindowExceededError around llama.cpp's own words.
    /// </summary>
    public static string Overflow(long tokens) =>
        $$$"""{"error":{"message":"litellm.ContextWindowExceededError: litellm.BadRequestError: ContextWindowExceededError: OpenAIException - request ({{{tokens}}} tokens) exceeds the available context size (32768 tokens), try increasing it","type":null,"param":null,"code":"400"}}""";

    /// <summary>llama.cpp's own refusal, straight from the engine (no gateway between).</summary>
    public const string LlamaOverflow =
        """{"error":{"code":400,"message":"request (34803 tokens) exceeds the available context size (32768 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":34803,"n_ctx":32768}}""";

    /// <summary>llama.cpp's error in the middle of a stream when the cache its requests share is full.</summary>
    public const string SharedCacheFull = """{"error":{"code":500,"message":"Context size has been exceeded.","type":"server_error"}}""";

    /// <summary>OpenAI's and vLLM's words for the same.</summary>
    public const string OpenAiOverflow =
        """{"error":{"message":"This model's maximum context length is 32768 tokens. However, you requested 40000 tokens (36000 in the messages, 4000 in the completion). Please reduce the length of the messages or completion.","type":"invalid_request_error","param":"messages","code":"context_length_exceeded"}}""";

    private static HttpResponseMessage OverflowResponse(long tokens) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent(Overflow(tokens), Encoding.UTF8, "application/json") };

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
