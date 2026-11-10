using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>What the fake model says to one request: text, tool calls, reasoning.</summary>
public sealed record Reply(string Text, (string Name, string Args)[] Calls, string? Reasoning = null)
{
    /// <summary>Why the answer ended ("length": cut at the output limit); null: stop, or tool_calls.</summary>
    public string? Finish { get; init; }
    /// <summary>The answer's tokens, as the usage says.</summary>
    public int Completion { get; init; } = 7;
    /// <summary>The text said again and again until the client goes away: a small model gone round in a loop.</summary>
    public bool Endless { get; init; }
    /// <summary>The reasoning said again and again until the client goes away: a model that thinks and thinks.</summary>
    public bool ThinksOn { get; init; }
    /// <summary>Of <see cref="Completion"/>, the tokens of thinking before the words: a max_tokens no larger is spent on it alone.</summary>
    public int Thought { get; init; }

    public static Reply Say(string text, string? reasoning = null) => new(text, [], reasoning);
    public static Reply Call(params (string Name, string Args)[] calls) => new("", calls);
}

/// <summary>An HTTP server on 127.0.0.1 and a free port, handling each request on its own task.</summary>
public abstract class FakeServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    protected FakeServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            // A listener for each try: one whose start failed is closed, and cannot be started again.
            var listener = new HttpListener();
            listener.Prefixes.Add(BaseUrl + "/");
            try
            {
                listener.Start();
                _listener = listener;
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // Taken between the probe and the start: another port.
                listener.Close();
            }
        }
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; private set; } = "";

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    await HandleAsync(ctx, body, _stop.Token);
                }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // The client went away.
                }
                finally
                {
                    try
                    {
                        ctx.Response.Close();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                    }
                }
            });
        }
    }

    protected abstract Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct);

    /// <summary>Arena's chats as Code Arena keeps them in step (/api/code-arena), held in memory.</summary>
    public FakeChats Chats { get; } = new();

    protected static async Task WriteJson(HttpListenerContext ctx, JsonNode json, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    protected static async Task WriteEvent(HttpListenerContext ctx, string data, string? name = null)
    {
        var bytes = Encoding.UTF8.GetBytes((name is null ? "" : $"event: {name}\n") + $"data: {data}\n\n");
        await ctx.Response.OutputStream.WriteAsync(bytes);
        await ctx.Response.OutputStream.FlushAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The gateway: /v1/models, /v1/model/info and streamed chat completions, as
/// LiteLLM answers them. What the model says comes from <see cref="Answer"/>.
/// </summary>
public sealed class FakeGateway : FakeServer
{
    public const string Key = "sk-arena-test-key-0123456789";
    private readonly List<JsonObject> _requests = [];

    public string[] Models { get; set; } = ["model-a", "model-b"];
    public int Context { get; set; } = 32768;
    /// <summary>The models' longest answer, as the gateway says it (max_output_tokens); null: it says none.</summary>
    public int? MaxOutput { get; set; }
    /// <summary>The models see pictures (the gateway says so).</summary>
    public bool Vision { get; set; }
    public Func<JsonObject, Reply> Answer { get; set; } = _ => Reply.Say("Hello from the model.");
    /// <summary>An HTTP status a chat request is answered with instead of an answer (a backend restarting: 500); 0 answers it.</summary>
    public Func<JsonObject, int> Failure { get; set; } = _ => 0;
    /// <summary>The error body a refused request gets (null: a backend restarting).</summary>
    public Func<JsonObject, JsonObject?> FailureBody { get; set; } = _ => null;
    /// <summary>Holds each answer until the request is cancelled (to test Ctrl+C).</summary>
    public bool Hang { get; set; }
    /// <summary>The requests whose answer stops after half its text, the connection dropped.</summary>
    public Func<JsonObject, bool> Drop { get; set; } = _ => false;
    /// <summary>The requests answered with a stream that never says anything.</summary>
    public Func<JsonObject, bool> Silent { get; set; } = _ => false;
    public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>The prompt's tokens as the engine counts them; null: a quarter of the request's characters.</summary>
    public Func<JsonObject, long>? Tokens { get; set; }
    /// <summary>
    /// The engine's window, as llama.cpp holds it: a prompt past it is refused (n_prompt_tokens, n_ctx), and an answer that
    /// would run past it is cut there ("length"). Null: no window.
    /// </summary>
    public int? Window { get; set; }
    /// <summary>Text completions (/v1/completions): what each asked, how they are answered, and a status to refuse them with (0: none).</summary>
    public List<JsonObject> TextRequests { get; } = [];
    public Func<JsonObject, string> TextAnswer { get; set; } = _ => "a + b;";
    public int TextStatus { get; set; }

    public string Url => BaseUrl;

    public List<JsonObject> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct)
    {
        if (ctx.Request.Headers["Authorization"] != "Bearer " + Key)
        {
            await WriteJson(ctx, new JsonObject { ["error"] = new JsonObject { ["message"] = "Authentication Error, invalid key" } }, 401);
            return;
        }
        var path = ctx.Request.Url!.AbsolutePath;
        if (path.StartsWith("/api/code-arena/", StringComparison.Ordinal))
        {
            await Chats.HandleAsync(ctx, body);
            return;
        }
        if (path == "/v1/models")
        {
            await WriteJson(ctx, new JsonObject { ["data"] = new JsonArray([.. Models.Select(m => (JsonNode)new JsonObject { ["id"] = m, ["object"] = "model" })]) });
            return;
        }
        if (path == "/v1/model/info")
        {
            await WriteJson(ctx, new JsonObject
            {
                ["data"] = new JsonArray([.. Models.Select(m => (JsonNode)new JsonObject
                {
                    ["model_name"] = m,
                    ["model_info"] = new JsonObject
                    {
                        ["max_input_tokens"] = Context, ["max_output_tokens"] = MaxOutput, ["supports_function_calling"] = true, ["supports_reasoning"] = true,
                        ["supports_vision"] = Vision, ["mode"] = "chat",
                    },
                })]),
            });
            return;
        }
        if (path == "/v1/completions")
        {
            var asked = JsonNode.Parse(body)!.AsObject();
            lock (_requests)
            {
                TextRequests.Add(asked);
            }
            if (TextStatus != 0)
            {
                await WriteJson(ctx, new JsonObject { ["error"] = new JsonObject { ["message"] = "Limit type: requests. Current limit: 2" } }, TextStatus);
                return;
            }
            await WriteJson(ctx, new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["text"] = TextAnswer(asked), ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 40, ["completion_tokens"] = 8 },
            });
            return;
        }
        if (path != "/v1/chat/completions")
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var request = JsonNode.Parse(body)!.AsObject();
        lock (_requests)
        {
            _requests.Add(request);
        }
        FirstRequest.TrySetResult();
        var counted = Tokens?.Invoke(request) ?? body.Length / 4;
        if (Window is { } window && counted > window)
        {
            await WriteJson(ctx, new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = $"litellm.ContextWindowExceededError: litellm.BadRequestError: OpenAIException - request ({counted} tokens) exceeds the available context size ({window} tokens), try increasing it",
                    ["type"] = "exceed_context_size_error", ["n_prompt_tokens"] = counted, ["n_ctx"] = window,
                },
            }, 400);
            return;
        }
        if (Failure(request) is > 0 and var status)
        {
            await WriteJson(ctx, FailureBody(request) ?? new JsonObject { ["error"] = new JsonObject { ["message"] = "The model's backend is restarting." } }, status);
            return;
        }
        if (Hang)
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            await WriteEvent(ctx, Chunk(new JsonObject { ["content"] = "Let me think" }));
            await Task.Delay(Timeout.Infinite, ct);
        }
        if (Silent(request))
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            await ctx.Response.OutputStream.FlushAsync(ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
        var reply = Answer(request);
        if (Window is { } room && counted + reply.Completion > room)
        {
            // The answer ran out of the window: cut where it ran out.
            reply = reply with { Finish = "length", Completion = (int)(room - counted) };
        }
        if (request["max_tokens"]?.GetValue<int>() is { } most && reply.Completion > most)
        {
            // Its longest answer reached: cut there, with no words when the thinking took it all.
            reply = reply with { Finish = "length", Completion = most, Text = reply.Thought >= most ? "" : reply.Text };
        }
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
        if (Drop(request))
        {
            await WriteEvent(ctx, Chunk(new JsonObject { ["role"] = "assistant", ["content"] = reply.Text[..(reply.Text.Length / 2)] }));
            // Time for the client to read it: a reset drops what it has not read yet.
            await Task.Delay(300, ct);
            ctx.Response.Abort();
            return;
        }
        if (reply.Reasoning is { } reasoning)
        {
            await WriteEvent(ctx, Chunk(new JsonObject { ["reasoning_content"] = reasoning }));
        }
        if (reply.ThinksOn)
        {
            await WriteEvent(ctx, Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }));
            while (true)
            {
                await WriteEvent(ctx, Chunk(new JsonObject { ["reasoning_content"] = reply.Reasoning ?? "Let me think again. " }));
                await Task.Delay(5, ct);
            }
        }
        if (reply.Endless)
        {
            await WriteEvent(ctx, Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }));
            while (true)
            {
                await WriteEvent(ctx, Chunk(new JsonObject { ["content"] = reply.Text }));
                await Task.Delay(2, ct);
            }
        }
        if (reply.Text.Length > 0)
        {
            var half = reply.Text.Length / 2;
            await WriteEvent(ctx, Chunk(new JsonObject { ["role"] = "assistant", ["content"] = reply.Text[..half] }));
            await WriteEvent(ctx, Chunk(new JsonObject { ["content"] = reply.Text[half..] }));
        }
        for (var i = 0; i < reply.Calls.Length; i++)
        {
            var (name, args) = reply.Calls[i];
            var cut = args.Length / 2;
            await WriteEvent(ctx, Chunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = i, ["id"] = $"call_{i}_{name}", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args[..cut] },
                }),
            }));
            await WriteEvent(ctx, Chunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = i, ["function"] = new JsonObject { ["arguments"] = args[cut..] } }),
            }));
        }
        await WriteEvent(ctx, new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = reply.Finish ?? (reply.Calls.Length > 0 ? "tool_calls" : "stop") }),
        }.ToJsonString());
        var prompt = counted;
        await WriteEvent(ctx, new JsonObject
        {
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt, ["completion_tokens"] = reply.Completion, ["total_tokens"] = prompt + reply.Completion,
                ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = prompt / 2 },
            },
        }.ToJsonString());
        await WriteEvent(ctx, "[DONE]");
    }

    private static string Chunk(JsonObject delta) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta }),
    }.ToJsonString();

    /// <summary>The text of the last message of a request.</summary>
    public static string Last(JsonObject request) => request["messages"]!.AsArray().Last()!["content"]?.GetValue<string>() ?? "";

    public static bool HasToolResults(JsonObject request) => request["messages"]!.AsArray().Any(m => m!["role"]?.GetValue<string>() == "tool");

    /// <summary>What the model is told besides the conversation: the system prompt, and Code Arena's notes (the mode, the date, the tools' instructions).</summary>
    public static string System(JsonObject request) => string.Join("\n\n", request["messages"]!.AsArray()
        .Select((m, i) => (Content: m!["content"]?.GetValue<string>() ?? "", i))
        .Where(x => x.i == 0 || x.Content.StartsWith(SystemPrompt.NoteHead, StringComparison.Ordinal)).Select(x => x.Content));

    /// <summary>The system prompt alone: the first message, the same in every request of a session.</summary>
    public static string SystemOnly(JsonObject request) => request["messages"]![0]!["content"]!.GetValue<string>();

    /// <summary>A long answer of about <paramref name="length"/> characters that never repeats itself (one letter again and again is a loop the stream stops).</summary>
    public static string Prose(int length)
    {
        var sb = new System.Text.StringBuilder(length + 16);
        for (var i = 0; sb.Length < length; i++)
        {
            sb.Append("word").Append(i).Append(' ');
        }
        return sb.ToString(0, length);
    }

    /// <summary>A compaction's request for a summary: the conversation, then Code Arena's ask.</summary>
    public static bool Summarizing(JsonObject request) => Last(request).StartsWith(Agent.SummaryAsk, StringComparison.Ordinal);

    /// <summary>The conversation a request carries: its messages after the system prompt, Code Arena's notes left out.</summary>
    public static List<JsonNode> History(JsonObject request) =>
        [.. request["messages"]!.AsArray().Skip(1).Where(m => !(m!["content"] is JsonValue said && said.GetValue<string>().StartsWith(SystemPrompt.NoteHead, StringComparison.Ordinal))).Select(m => m!)];

    /// <summary>The notes of Code Arena's in a request.</summary>
    public static List<string> Notes(JsonObject request) =>
        [.. request["messages"]!.AsArray().Select(m => m!["content"]?.GetValue<string>() ?? "").Where(c => c.StartsWith(SystemPrompt.NoteHead, StringComparison.Ordinal))];

    public static List<string> ToolNames(JsonObject request) =>
        request["tools"] is JsonArray tools ? [.. tools.Select(t => t!["function"]!["name"]!.GetValue<string>())] : [];
}

/// <summary>Arena's MCP endpoint at /mcp: streamable HTTP, answering in JSON or as an event stream, with a session id.</summary>
public sealed class FakeMcp : FakeServer
{
    private readonly List<(string Method, string? Session)> _calls = [];

    /// <summary>The Code Arena build Arena serves (/api/downloads/code-arena): its version, its bytes, and the SHA-256 it lists (null: theirs).</summary>
    public (string Version, byte[] Bytes, string? Sha256)? Build { get; set; }

    /// <summary>The IDE's launcher beside the build (Windows'), at the build's address/ide: its bytes, and the SHA-256 it lists (null: theirs).</summary>
    public (byte[] Bytes, string? Sha256)? Ide { get; set; }

    /// <summary>Arena signs its builds: the builds' list (SHA256SUMS, made here), its signature with this key, and this certificate chain (PEM).</summary>
    public (System.Security.Cryptography.RSA Key, string ChainPem)? Signing { get; set; }

    /// <summary>The builds' list as sha256sum writes it: this system's build (and its IDE launcher on Windows).</summary>
    private byte[] SignedList((string Version, byte[] Bytes, string? Sha256) build)
    {
        var exe = OperatingSystem.IsWindows() ? "code-arena.exe" : "code-arena";
        var list = $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(build.Bytes))}  {Updates.Rid}/{exe}\n";
        if (Ide is { } ide)
        {
            list += $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ide.Bytes))}  {Updates.Rid}/{Updates.IdeLauncher}\n";
        }
        return Encoding.ASCII.GetBytes(list);
    }

    public bool Sse { get; set; }
    /// <summary>Arena's default chat model, told in initialize's _meta as Arena MCP does; null: not told.</summary>
    public string? DefaultModel { get; set; }
    /// <summary>No endpoint: 404 for everything.</summary>
    public bool Missing { get; set; }
    /// <summary>Not 0: every request answered with this status (503: the server is down behind its proxy).</summary>
    public int Status { get; set; }
    /// <summary>While set, initialize waits for it: a server slow to answer, or one that never does.</summary>
    public TaskCompletionSource? Hold { get; set; }
    /// <summary>The tools it lists instead of Arena's three (web_search, read_file, create_issue): code-intel's, say.</summary>
    public JsonArray? ToolList { get; set; }
    /// <summary>While set, tools/call waits for it: a tool that never answers.</summary>
    public TaskCompletionSource? CallHold { get; set; }
    /// <summary>Over an event stream, the progress reports a tools/call sends before its answer, this far apart.</summary>
    public int CallProgress { get; set; }
    public TimeSpan ProgressGap { get; set; } = TimeSpan.FromMilliseconds(300);
    /// <summary>What tools/call answers instead of its text: pictures, sound, links to files.</summary>
    public JsonArray? CallContent { get; set; }
    /// <summary>The files resources/read gives, by their link: type and bytes.</summary>
    public Dictionary<string, (string Type, byte[] Bytes)> Resources { get; } = [];
    public string Url => BaseUrl + "/mcp";
    /// <summary>The sessions closed (DELETE), as a client that lets a connection go does.</summary>
    public int Closed => Volatile.Read(ref _closed);
    private int _closed;

    private readonly List<JsonObject> _toolCalls = [];

    /// <summary>Each tools/call's params (name and arguments), in order.</summary>
    public List<JsonObject> ToolCalls
    {
        get
        {
            lock (_calls)
            {
                return [.. _toolCalls];
            }
        }
    }

    public List<(string Method, string? Session)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    protected override async Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct)
    {
        if (!Missing && ctx.Request.Url!.AbsolutePath.StartsWith("/api/code-arena/", StringComparison.Ordinal) && ctx.Request.Headers["Authorization"] == "Bearer " + FakeGateway.Key)
        {
            await Chats.HandleAsync(ctx, body);
            return;
        }
        if (!Missing && ctx.Request.Url!.AbsolutePath.StartsWith("/api/chat/attachments", StringComparison.Ordinal))
        {
            await Chats.HandleFilesAsync(ctx, body);
            return;
        }
        if (!Missing && (ctx.Request.Url!.AbsolutePath.StartsWith("/api/projects", StringComparison.Ordinal) || ctx.Request.Url.AbsolutePath.StartsWith("/api/sharing/", StringComparison.Ordinal)))
        {
            await Chats.HandleProjectsAsync(ctx, body);
            return;
        }
        if (Build is { } build && ctx.Request.Url!.AbsolutePath.StartsWith("/api/downloads/code-arena", StringComparison.Ordinal))
        {
            // Arena's Code Arena builds (anyone may read them): its version, this system's build, and the build itself.
            ctx.Response.StatusCode = 200;
            var path = ctx.Request.Url.AbsolutePath;
            var bytes = path == "/api/downloads/code-arena"
                ? Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["version"] = build.Version,
                    ["builds"] = new JsonArray(new JsonObject
                    {
                        ["rid"] = Updates.Rid, ["size"] = build.Bytes.Length, ["sha256"] = build.Sha256 ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(build.Bytes)),
                        ["ide"] = Ide is { } ide
                            ? new JsonObject { ["size"] = ide.Bytes.Length, ["sha256"] = ide.Sha256 ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ide.Bytes)) }
                            : null,
                    }),
                    ["signature"] = Signing is null ? null : new JsonObject { ["by"] = "Code Arena (test)" },
                }.ToJsonString())
                : path == "/api/downloads/code-arena/SHA256SUMS" ? SignedList(build)
                : path == "/api/downloads/code-arena/SHA256SUMS.sig" && Signing is { } signing
                    ? signing.Key.SignData(SignedList(build), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)
                : path == "/api/downloads/code-arena/code-signing.crt" && Signing is { } chain ? Encoding.ASCII.GetBytes(chain.ChainPem)
                : path == "/api/downloads/code-arena/" + Updates.Rid ? build.Bytes
                : path == $"/api/downloads/code-arena/{Updates.Rid}/ide" && Ide is { } launcher ? launcher.Bytes : [];
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            return;
        }
        if (Missing || ctx.Request.Url!.AbsolutePath != "/mcp")
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        if (Status != 0)
        {
            ctx.Response.StatusCode = Status;
            return;
        }
        if (ctx.Request.Headers["Authorization"] != "Bearer " + FakeGateway.Key)
        {
            ctx.Response.StatusCode = 401;
            return;
        }
        if (ctx.Request.HttpMethod == "DELETE")
        {
            Interlocked.Increment(ref _closed);
            ctx.Response.StatusCode = 204;
            return;
        }
        var message = JsonNode.Parse(body)!.AsObject();
        var method = message["method"]?.GetValue<string>() ?? "";
        lock (_calls)
        {
            _calls.Add((method, ctx.Request.Headers["Mcp-Session-Id"]));
            if (method == "tools/call" && message["params"] is JsonObject called)
            {
                _toolCalls.Add(called.DeepClone().AsObject());
            }
        }
        if (message["id"] is null)
        {
            ctx.Response.StatusCode = 202;
            return;
        }
        if (method == "initialize" && Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }
        if (method == "tools/call" && CallHold is { } held)
        {
            await held.Task.WaitAsync(ct);
        }
        JsonNode result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "arena", ["version"] = "4.2.0" },
                ["instructions"] = "Arena: search the web with web_search before answering about recent events.",
                ["_meta"] = DefaultModel is null ? null : new JsonObject { ["arena/defaultModel"] = DefaultModel },
            },
            "tools/list" when ToolList is not null => new JsonObject { ["tools"] = ToolList.DeepClone() },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "web_search",
                        ["description"] = "Search the web.",
                        ["inputSchema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                            ["required"] = new JsonArray("query"),
                        },
                    },
                    new JsonObject { ["name"] = "read_file", ["description"] = "Arena's own file reader.", ["inputSchema"] = new JsonObject { ["type"] = "object" } },
                    new JsonObject
                    {
                        ["name"] = "create_issue",
                        ["description"] = "Open an issue in GitLab.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" },
                        ["annotations"] = new JsonObject { ["destructiveHint"] = true },
                    }),
            },
            "tools/call" when CallContent is not null => new JsonObject { ["content"] = CallContent.DeepClone() },
            "resources/read" when Resources.TryGetValue(message["params"]?["uri"]?.GetValue<string>() ?? "", out var file) => new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject
                {
                    ["uri"] = message["params"]!["uri"]!.DeepClone(), ["mimeType"] = file.Type, ["blob"] = Convert.ToBase64String(file.Bytes),
                }),
            },
            "tools/call" => new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Results for {message["params"]?["arguments"]?["query"]}: https://arena.example/answer",
                }),
            },
            _ => new JsonObject(),
        };
        if (method == "initialize")
        {
            ctx.Response.Headers["Mcp-Session-Id"] = "session-42";
        }
        var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result };
        if (Sse)
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            await WriteEvent(ctx, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/progress", ["params"] = new JsonObject { ["progress"] = 1 } }.ToJsonString(), "message");
            for (var i = 0; method == "tools/call" && i < CallProgress; i++)
            {
                await Task.Delay(ProgressGap, ct);
                await WriteEvent(ctx, new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "notifications/progress",
                    ["params"] = new JsonObject { ["progressToken"] = message["params"]?["_meta"]?["progressToken"]?.DeepClone(), ["progress"] = i + 1, ["total"] = CallProgress, ["message"] = "sampling" },
                }.ToJsonString(), "message");
            }
            await WriteEvent(ctx, answer.ToJsonString(), "message");
            return;
        }
        await WriteJson(ctx, answer);
    }
}

/// <summary>A home of its own (config and data folders) and a working folder, and the program run in them.</summary>
public sealed class Harness : IDisposable
{
    public Harness(FakeGateway gateway, FakeMcp? mcp = null, Action<JsonObject>? config = null)
    {
        Root = Directory.CreateTempSubdirectory("code-arena-").FullName;
        Work = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;
        // The person's home of its own: ~/.code-arena/skills and ~/.claude/skills are read from it.
        Paths = new AppPaths(Path.Combine(Root, "config"), Path.Combine(Root, "data")) { Home = Path.Combine(Root, "home") };
        Directory.CreateDirectory(Paths.ConfigDir);
        var json = new JsonObject
        {
            // Arena's address: the fake MCP server's, so /mcp is found there (or the gateway's, which has none).
            ["url"] = mcp?.BaseUrl ?? gateway.Url,
            ["gateway"] = gateway.Url,
            ["apiKey"] = FakeGateway.Key,
            // Arena's own skills only where a test brings them: the others measure the prompt as it was.
            ["builtInSkills"] = false,
            // The project's long memory (.remember/) only where a test brings it, for the same reason.
            ["projectMemory"] = false,
        };
        config?.Invoke(json);
        File.WriteAllText(Paths.ConfigFile, json.ToJsonString());
    }

    public string Root { get; }
    public string Work { get; }
    internal AppPaths Paths { get; }
    public Dictionary<string, string?> Env { get; } = [];
    public string Out { get; private set; } = "";
    public string Err { get; private set; } = "";
    public string? Secret { get; set; }
    /// <summary>A terminal read key by key (the prompt's line editor); null: lines from the typed input.</summary>
    internal IKeyboard? Keys { get; set; }
    /// <summary>The folder it runs in; null: Work.</summary>
    public string? Cwd { get; set; }
    /// <summary>How often a running Code Arena asks Arena for its skills again; null: as it does (CliEnv's).</summary>
    public TimeSpan? ArenaSkillsEvery { get; set; }
    /// <summary>How often a running Code Arena asks Arena whether its settings changed; null: as it does (CliEnv's).</summary>
    public TimeSpan? SettingsEvery { get; set; }
    /// <summary>The IDE's log (WebRun); null: none.</summary>
    internal LogFile? Log { get; set; }
    /// <summary>Told whether the IDE got a window of its own (WebRun); null: nobody is.</summary>
    internal Action<bool>? Windowed { get; set; }

    /// <summary>Runs code-arena with these arguments and typed input; a terminal on stdin unless said otherwise.</summary>
    public async Task<int> Run(string input, params string[] args) => await Run(input, terminal: true, args);

    /// <summary>code-arena with these arguments, as if this were the program at <paramref name="exe"/>.</summary>
    public async Task<int> RunAs(string exe, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = error, Env = k => Env.GetValueOrDefault(k), Cwd = Cwd ?? Work, Paths = Paths, Exe = exe };
        var code = await Cli.RunAsync(args, env);
        (Out, Err) = (output.ToString(), error.ToString());
        return code;
    }

    /// <summary>code-arena update, as if this were the program it replaces.</summary>
    public async Task<int> RunUpdate(string exe)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = error, Env = k => Env.GetValueOrDefault(k), Cwd = Cwd ?? Work, Paths = Paths, Exe = exe };
        var code = await Cli.RunAsync(["update"], env);
        (Out, Err) = (output.ToString(), error.ToString());
        return code;
    }

    public async Task<int> Run(string input, bool terminal, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new CliEnv
        {
            In = new StringReader(input),
            Out = output,
            Err = error,
            Env = k => Env.GetValueOrDefault(k),
            Cwd = Cwd ?? Work,
            Paths = Paths,
            InTerminal = terminal,
            ReadSecret = Secret is null ? null : _ => Secret,
            Keys = Keys,
            ArenaSkillsEvery = ArenaSkillsEvery ?? CliEnv.ArenaSkillsAgain,
        };
        var code = await Cli.RunAsync(args, env);
        Out = output.ToString();
        Err = error.ToString();
        return code;
    }

    public string Write(string relative, string text)
    {
        var path = Path.Combine(Work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>What is not a plain file to read, made for a test (Unix).</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class Special
{
    /// <summary>A named pipe: opened to read, it waits for a writer.</summary>
    public static void Pipe(string path) => Assert.Equal(0, mkfifo(path, 0b110_000_000));

    /// <summary>A file no one but root may read; false when this test runs as root, who reads it all the same.</summary>
    public static bool Locked(string file)
    {
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            File.ReadAllBytes(file);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);
}

/// <summary>
/// Arena's side of Code Arena's chats (/api/code-arena), as Arena keeps them: each chat a tree of messages with ids (an
/// edited question or a regenerated answer is a sibling), the branch on screen the path to its leaf. Reads go on after a
/// message, sends add after one; a turn of Code Arena's holds the chat.
/// </summary>
public sealed class FakeChats
{
    private readonly object _gate = new();

    /// <summary>Whether Arena counts tokens (/api/code-arena/tokenize: its engine has a tokenizer); else it says nothing (404).</summary>
    public bool Tokenizer { get; set; }

    /// <summary>How many texts each count was asked for.</summary>
    public System.Collections.Concurrent.ConcurrentBag<int> Tokenized { get; } = [];

    /// <summary>The tokens the fake chat format adds for each message (its role's marks).</summary>
    public const int FormatPerMessage = 4;

    /// <summary>The fake tokenizer: each word and each other sign is a token.</summary>
    public static int CountTokens(string text) => System.Text.RegularExpressions.Regex.Count(text, @"\w+|[^\w\s]");

    /// <summary>Each plan Code Arena sent, in order: the chat, and the plan (null: dropped).</summary>
    public List<(string Chat, JsonObject? Plan)> Plans { get; } = [];

    public List<(string Chat, JsonObject? Plan)> PlansSent()
    {
        lock (_gate)
        {
            return [.. Plans];
        }
    }

    /// <summary>Each rating Code Arena sent, in order: the chat, the message, and the rating (null: taken back).</summary>
    public List<(string Chat, string Message, JsonObject? Rating)> Ratings { get; } = [];

    public List<(string Chat, string Message, JsonObject? Rating)> RatingsSent()
    {
        lock (_gate)
        {
            return [.. Ratings];
        }
    }

    /// <summary>Each goal Code Arena sent, in order: the chat, and the goal (null: dropped).</summary>
    public List<(string Chat, JsonObject? Goal)> Goals { get; } = [];

    public List<(string Chat, JsonObject? Goal)> GoalsSent()
    {
        lock (_gate)
        {
            return [.. Goals];
        }
    }

    /// <summary>The person's assistants, as /api/code-arena/assistants gives them; null: Arena does not answer it.</summary>
    public JsonArray? Assistants { get; set; }

    /// <summary>A message in a chat's tree: Arena's shape (id, role, content, toolCalls, ref...), and the one before it.</summary>
    public sealed class Node
    {
        public required string Id { get; init; }
        public string? Parent { get; set; }
        public required JsonObject Message { get; init; }
        public string? Summary { get; set; }
    }

    public sealed class Chat
    {
        public required string Id { get; init; }
        public string? Ref { get; init; }
        public string? Place { get; init; }
        public string Title { get; set; } = "New chat";
        /// <summary>Every message, in the order they were added, on every branch.</summary>
        public List<Node> Nodes { get; } = [];
        /// <summary>The last message of the branch on screen.</summary>
        public string? Leaf { get; set; }
        /// <summary>When it last changed: the list is newest first, a page at a time before a time.</summary>
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
        /// <summary>Code Arena's turn holds it until then.</summary>
        public DateTimeOffset? Turn { get; set; }
        /// <summary>What the person queued on the web while Code Arena's turn holds it: taken in at the turn's next step.</summary>
        public List<string> Queued { get; } = [];
        /// <summary>The project it is in (Arena's Projects); null: none.</summary>
        public string? ProjectId { get; set; }

        /// <summary>The branch on screen, first to last.</summary>
        public List<Node> Branch
        {
            get
            {
                var path = new List<Node>();
                for (var id = Leaf; id is not null && Nodes.FirstOrDefault(n => n.Id == id) is { } n; id = n.Parent)
                {
                    path.Add(n);
                }
                path.Reverse();
                return path;
            }
        }

        /// <summary>The branch on screen's messages, as Arena gives them.</summary>
        public List<JsonObject> Messages => [.. Branch.Select(n => n.Message)];
    }

    /// <summary>Reads of a chat that brought its messages, and those answered only "unchanged" (asked with its stamp).</summary>
    public int FullReads { get; private set; }
    public int UnchangedReads { get; private set; }
    /// <summary>Each read: the message it went on after (null: from the start), and how many it gave.</summary>
    public List<(string? Since, int Given)> Reads { get; } = [];
    /// <summary>Each turn asked for and let go, in order: "hold" or "end".</summary>
    public List<string> Turns { get; } = [];
    /// <summary>The most messages one read gives (Arena: 200 unless asked for fewer).</summary>
    public int Page { get; set; } = 200;
    /// <summary>The projects the person may add to (/api/code-arena/projects), and the one a new chat of no project chosen goes in (its repository's).</summary>
    public List<(string Id, string Name)> Projects { get; } = [];
    public string? RepositoryProject { get; set; }
    /// <summary>Each chat put in a project or taken out (PUT /chats/{id}/project): the chat and the project (null: none).</summary>
    public List<(string Chat, string? Project)> Moves { get; } = [];

    /// <summary>Files attached on the web, by id: what /api/code-arena/attachments/{id} gives.</summary>
    public Dictionary<string, (byte[] Data, string ContentType)> Files { get; } = [];
    /// <summary>The chats the person continued from Code Arena (/continued), in order.</summary>
    public List<string> Continued { get; } = [];
    /// <summary>When the person's chats compact, as /api/code-arena/preferences says: their own choice (null: none), and Arena's default.</summary>
    public int? CompactAt { get; set; }
    public int CompactAtDefault { get; set; } = 80;
    /// <summary>Arena's own skills its admins turned off, as /api/code-arena/skills says (null: it does not answer that).</summary>
    public List<string>? SkillsOff { get; set; }

    /// <summary>The skills made in Arena and the plugins' that /api/code-arena/skills brings (each its name and files).</summary>
    public JsonArray? ArenaSkills { get; set; }

    /// <summary>Whether Arena keeps Code Arena's memory (/api/code-arena/memories; an older one does not).</summary>
    public bool Memory { get; set; } = true;

    /// <summary>The person may not use memory (feature.memory denied): /api/code-arena/memories answers 403.</summary>
    public bool MemoryDenied { get; set; }

    /// <summary>Arena's memories, as /api/code-arena/memories keeps them: id, text, scope, kind, project.</summary>
    public List<JsonObject> Memories { get; } = [];

    /// <summary>The recalls Code Arena asked: the query and the memories it had been given already.</summary>
    public List<(string Query, List<string> Besides)> Recalls { get; } = [];

    /// <summary>The skills Code Arena kept as the person's (POST /api/code-arena/skills/learn).</summary>
    public List<JsonObject> SkillsLearned { get; } = [];

    /// <summary>
    /// What Code Arena sent to make one of the person's own skills (POST /api/code-arena/skills) and to change one (PUT
    /// /api/code-arena/skills/NAME): each is in <see cref="ArenaSkills"/> from then on, theirs (mine), its SKILL.md as Arena writes it.
    /// </summary>
    public List<JsonObject> SkillsMade { get; } = [];
    public List<JsonObject> SkillsChanged { get; } = [];

    /// <summary>
    /// What Code Arena sent to import skills (POST /api/code-arena/skills/import), previews too; a folder named in
    /// <see cref="SkillsRefused"/> is refused as Arena's safeguards refuse one. Those imported are the person's from then on.
    /// </summary>
    public List<JsonObject> SkillsImported { get; } = [];
    public Dictionary<string, string> SkillsRefused { get; } = [];

    /// <summary>
    /// One of the person's own skills, as /api/code-arena/skills brings it: its SKILL.md as Arena writes it (in double
    /// quotes, its own quotes and backslashes escaped), with its parameters ({ name, description, required, default }).
    /// </summary>
    public static JsonObject OwnSkill(string name, string description, string instructions, JsonArray? parameters = null)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var md = new StringBuilder($"---\nname: {name}\ntitle: {Q(name.Replace('-', ' '))}\ndescription: {Q(description)}\n");
        if (parameters is { Count: > 0 })
        {
            md.Append("parameters:\n");
            foreach (var p in parameters.OfType<JsonObject>())
            {
                md.Append("  - { name: ").Append(p["name"]!.GetValue<string>());
                if (p["description"]?.GetValue<string>() is { } d)
                {
                    md.Append(", description: ").Append(Q(d));
                }
                md.Append(", required: ").Append(p["required"]?.GetValue<bool>() == false ? "false" : "true");
                if (p["default"]?.GetValue<string>() is { } def)
                {
                    md.Append(", default: ").Append(Q(def));
                }
                md.Append(" }\n");
            }
        }
        return new()
        {
            ["name"] = name, ["description"] = description, ["version"] = "1", ["mine"] = true, ["parameters"] = parameters?.DeepClone(),
            ["files"] = new JsonObject { ["SKILL.md"] = md.Append($"---\n{instructions}\n").ToString() },
        };
    }

    /// <summary>What Code Arena asked of Arena's past chats: search or open, and its body.</summary>
    public List<(string What, JsonObject Body)> HistoryAsked { get; } = [];

    private static string MemoryLine(JsonObject m)
    {
        var tags = new List<string>();
        if (m["kind"]?.GetValue<string>() is { } k && k != "fact")
        {
            tags.Add(k == "howto" ? "how to" : k);
        }
        if (m["scope"]?.GetValue<string>() == "project")
        {
            tags.Add("this project's");
        }
        return m["text"]!.GetValue<string>() + (tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "");
    }

    private static HashSet<string> MemoryWords(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]{4,}").Select(m => m.Value)];

    /// <summary>Arena's memory routes: as Arena answers them, with words standing in for meaning.</summary>
    private async Task MemoryAsync(HttpListenerContext ctx, string[] rest, string method, string body)
    {
        var json = body.Length > 0 ? JsonNode.Parse(body) as JsonObject : null;
        JsonNode reply;
        var status = 200;
        lock (_gate)
        {
            var project = ctx.Request.QueryString["project"] ?? json?["project"]?.GetValue<string>();
            var readable = Memories.Where(m => m["scope"]!.GetValue<string>() == "person" || m["project"]?.GetValue<string>() == project).ToList();
            switch (rest, method)
            {
                case ([], "GET"):
                    reply = new JsonObject
                    {
                        ["on"] = true,
                        ["note"] = readable.Count == 0 ? null : "What you remember about the person, from their earlier chats:" + string.Concat(readable.Select(m => "\n- " + MemoryLine(m))),
                        ["inNote"] = new JsonArray([.. readable.Select(m => (JsonNode)m["id"]!.GetValue<string>())]),
                        ["memories"] = new JsonArray([.. readable.Select(m => m.DeepClone())]),
                    };
                    break;
                case ([], "POST") or (["import"], "POST"):
                    var items = rest is ["import"] ? (json!["memories"] as JsonArray)!.OfType<JsonObject>().ToList() : [json!];
                    var kept = 0;
                    JsonObject? last = null;
                    foreach (var item in items)
                    {
                        var text = item["text"]?.GetValue<string>() ?? "";
                        var scope = item["scope"]?.GetValue<string>() ?? "person";
                        var key = scope == "project" ? item["project"]?.GetValue<string>() : null;
                        last = Memories.FirstOrDefault(m => m["text"]!.GetValue<string>() == text && m["scope"]!.GetValue<string>() == scope && m["project"]?.GetValue<string>() == key);
                        if (last is null && text.Length > 0)
                        {
                            last = new JsonObject
                            {
                                ["id"] = Guid.NewGuid().ToString(), ["text"] = text, ["scope"] = scope, ["kind"] = item["kind"]?.GetValue<string>() ?? "fact",
                                ["project"] = key, ["pinned"] = false, ["mine"] = true,
                            };
                            Memories.Add(last);
                            kept++;
                        }
                    }
                    reply = rest is ["import"] ? new JsonObject { ["kept"] = kept, ["skipped"] = items.Count - kept } : last!.DeepClone();
                    break;
                case (["recall"], "POST"):
                    var besides = (json!["besides"] as JsonArray ?? []).Select(b => b!.GetValue<string>()).ToList();
                    Recalls.Add((json["query"]!.GetValue<string>(), besides));
                    var words = MemoryWords(json["query"]!.GetValue<string>());
                    var found = readable.Where(m => !besides.Contains(m["id"]!.GetValue<string>()) && MemoryWords(m["text"]!.GetValue<string>()).Overlaps(words)).ToList();
                    reply = new JsonObject
                    {
                        ["note"] = found.Count == 0 ? null : "(Memories that may matter to this question; use them where they help:" + string.Concat(found.Select(m => "\n- " + MemoryLine(m))) + ")",
                        ["memories"] = new JsonArray([.. found.Select(m => m.DeepClone())]),
                    };
                    break;
                case ([var id], "DELETE"):
                    status = Memories.RemoveAll(m => m["id"]!.GetValue<string>() == id) > 0 ? 204 : 404;
                    reply = new JsonObject();
                    break;
                default:
                    status = 404;
                    reply = new JsonObject();
                    break;
            }
        }
        if (status == 204)
        {
            ctx.Response.StatusCode = 204;
            return;
        }
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()));
    }

    /// <summary>The latest security review, as /api/code-arena/security gives it; null: Arena does not answer it (an older one).</summary>
    public JsonObject? Security { get; set; }
    /// <summary>What Code Arena asked /api/code-arena/security (its query: the project and the branch).</summary>
    public List<string> SecurityAsked { get; } = [];

    /// <summary>Whether Arena answers /api/code-arena/preferences (an older one does not).</summary>
    public bool Preferences { get; set; } = true;
    /// <summary>The web chat's config for the person, as /api/code-arena/config gives it; null: Arena does not answer it.</summary>
    public JsonObject? ChatConfig { get; set; }

    /// <summary>The branch as one word, as Arena gives it: its last message.</summary>
    private static string Stamp(Chat c) => c.Leaf ?? "empty";

    private int _made;
    private int _ids;

    private string NewId() => $"00000000-0000-0000-0000-{++_ids:D12}";

    private Node AddNode(Chat chat, string? parent, JsonObject message)
    {
        var node = new Node { Id = NewId(), Parent = parent, Message = message.DeepClone().AsObject() };
        node.Message["id"] = node.Id;
        chat.Nodes.Add(node);
        return node;
    }

    /// <summary>Adds to a chat as the web does, after the branch on screen, while Code Arena may be reading it.</summary>
    public void Append(Chat chat, params JsonObject[] messages)
    {
        lock (_gate)
        {
            foreach (var m in messages)
            {
                chat.Leaf = AddNode(chat, chat.Leaf, m).Id;
            }
            chat.Updated = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// The web edits the branch's message at <paramref name="index"/> (a question asked again, an answer written again): a
    /// sibling of it, with what follows, shown from then on.
    /// </summary>
    public void Edit(Chat chat, int index, params JsonObject[] messages)
    {
        lock (_gate)
        {
            var branch = chat.Branch;
            var parent = index == 0 ? null : branch[index - 1].Id;
            foreach (var m in messages)
            {
                parent = AddNode(chat, parent, m).Id;
            }
            chat.Leaf = parent;
            chat.Updated = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>The web shows another branch: the one ending at <paramref name="leaf"/>.</summary>
    public void Show(Chat chat, string leaf)
    {
        lock (_gate)
        {
            chat.Leaf = leaf;
        }
    }

    /// <summary>The web compacts its chat: a summary of the branch down to its message at <paramref name="index"/>.</summary>
    public void Summarize(Chat chat, int index, string text)
    {
        lock (_gate)
        {
            chat.Branch[index].Summary = text;
        }
    }

    /// <summary>As if Arena never had the branch's messages from <paramref name="count"/> on (they never arrived).</summary>
    public void Lose(Chat chat, int count)
    {
        lock (_gate)
        {
            var branch = chat.Branch;
            foreach (var n in branch.Skip(count))
            {
                chat.Nodes.Remove(n);
            }
            chat.Leaf = count == 0 ? null : branch[count - 1].Id;
        }
    }

    public List<Chat> All { get; } = [];
    /// <summary>Not 0: every request answered with this status (Arena down: 503; an Arena without the sync: 404).</summary>
    public int Status { get; set; }
    /// <summary>The chats answering on the web: reads and sends get 409.</summary>
    public HashSet<string> Answering { get; } = [];

    public Chat Add(string title, params JsonObject[] messages)
    {
        lock (_gate)
        {
            // Each a moment after the one before: the newest is the last added.
            var chat = new Chat { Id = Guid.NewGuid().ToString(), Title = title, Updated = DateTimeOffset.UtcNow.AddTicks(++_made * 10) };
            foreach (var m in messages)
            {
                chat.Leaf = AddNode(chat, chat.Leaf, m).Id;
            }
            All.Add(chat);
            return chat;
        }
    }

    public Chat? Find(string id)
    {
        lock (_gate)
        {
            return All.FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>The branch after <paramref name="since"/> as Arena reads it: after the last message both have, and whether that is a fork.</summary>
    private JsonObject Read(Chat c, string? since, int limit)
    {
        var branch = c.Branch;
        var (start, @base, fork) = (0, (string?)null, false);
        if (since is not null)
        {
            var at = branch.FindIndex(n => n.Id == since);
            if (at >= 0)
            {
                (start, @base) = (at + 1, since);
            }
            else
            {
                fork = true;
                for (var up = c.Nodes.FirstOrDefault(n => n.Id == since)?.Parent; up is not null; up = c.Nodes.FirstOrDefault(n => n.Id == up)?.Parent)
                {
                    if (branch.FindIndex(n => n.Id == up) is var shared and >= 0)
                    {
                        (start, @base) = (shared + 1, up);
                        break;
                    }
                }
            }
        }
        var page = branch.Skip(start).Take(limit).ToList();
        Reads.Add((since, page.Count));
        var summary = branch.LastOrDefault(n => n.Summary is not null);
        return new JsonObject
        {
            ["id"] = c.Id, ["count"] = branch.Count, ["stamp"] = Stamp(c), ["base"] = @base, ["fork"] = fork, ["more"] = start + page.Count < branch.Count,
            ["summary"] = summary is null ? null : new JsonObject { ["upTo"] = summary.Id, ["text"] = summary.Summary },
            ["messages"] = new JsonArray([.. page.Select(n => (JsonNode)n.Message.DeepClone())]),
        };
    }

    /// <summary>The files the relay sent on: what each request was (method, path and query, its type, the key it came with, its body).</summary>
    public List<(string Method, string Path, string? Type, string? Authorization, string? Range, string Body)> Relayed { get; } = [];

    /// <summary>
    /// Arena's projects as its page and Code Arena reach them (/api/projects…), by id: each the page's shape (name, role, global,
    /// status, goals, tasks); a role below contributor changes nothing (403), as Arena decides.
    /// </summary>
    public Dictionary<string, JsonObject> ArenaProjects { get; } = [];

    /// <summary>Whether Arena lets the person make projects (feature.projects): else POST /api/projects is 403.</summary>
    public bool MayCreate { get; set; } = true;

    /// <summary>What each project's report says it used (its totals), by id: the report of one not here is all zeros.</summary>
    public Dictionary<string, JsonObject> Used { get; } = [];

    /// <summary>A project of the page's shape, of the person's in the role given (it is in the choices too when they may add to it).</summary>
    public JsonObject AddProject(string id, string name, string role = "owner", params (string Title, string Status)[] goals)
    {
        var p = new JsonObject
        {
            ["id"] = id, ["name"] = name, ["description"] = "", ["global"] = false, ["status"] = "active", ["role"] = role, ["mine"] = true, ["spentThisMonth"] = 0.5,
            ["members"] = new JsonArray(), ["repositories"] = new JsonArray(), ["items"] = new JsonArray(), ["chats"] = new JsonArray(), ["otherChats"] = new JsonArray(),
            ["goals"] = new JsonArray([.. goals.Select((g, i) => (JsonNode)new JsonObject { ["id"] = $"{id}-g{i}", ["title"] = g.Title, ["status"] = g.Status, ["dueOn"] = null, ["position"] = i })]),
            ["tasks"] = new JsonArray(),
        };
        lock (_gate)
        {
            ArenaProjects[id] = p;
            if (role is "owner" or "contributor")
            {
                Projects.Add((id, name));
            }
        }
        return p;
    }

    /// <summary>Arena's project routes (and sharing's lookups), taken with the key alone (as Arena's KeyAuthentication does).</summary>
    internal async Task HandleProjectsAsync(HttpListenerContext ctx, string body)
    {
        var path = ctx.Request.Url!.PathAndQuery;
        lock (_gate)
        {
            Relayed.Add((ctx.Request.HttpMethod, path, ctx.Request.ContentType, ctx.Request.Headers["Authorization"], ctx.Request.Headers["Range"], body));
        }
        if (ctx.Request.Headers["Authorization"] != "Bearer " + FakeGateway.Key)
        {
            ctx.Response.StatusCode = 401;
            return;
        }
        if (ctx.Request.HttpMethod != "GET" && ctx.Request.Headers["X-Requested-With"] is null)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        var parts = ctx.Request.Url.AbsolutePath.Trim('/').Split('/')[1..];
        var method = ctx.Request.HttpMethod;
        JsonNode? reply = null;
        var status = 200;
        lock (_gate)
        {
            switch (parts)
            {
                case ["sharing", "groups"]:
                    reply = new JsonArray(new JsonObject { ["id"] = "g-1", ["name"] = "Payments team", ["mine"] = true });
                    break;
                case ["projects"] when method == "GET":
                    reply = new JsonArray([.. ArenaProjects.Values.Select(p => (JsonNode)p.DeepClone())]);
                    break;
                case ["projects"] when method == "POST":
                    if (!MayCreate)
                    {
                        status = 403;
                        break;
                    }
                    var id = Guid.NewGuid().ToString();
                    var name = Json.ParseObject(body)?.Str("name") ?? "";
                    // Under the lock already: added as AddProject does.
                    ArenaProjects[id] = new JsonObject
                    {
                        ["id"] = id, ["name"] = name, ["description"] = "", ["global"] = false, ["status"] = "active", ["role"] = "owner", ["mine"] = true, ["spentThisMonth"] = 0,
                        ["goals"] = new JsonArray(), ["tasks"] = new JsonArray(),
                    };
                    Projects.Add((id, name));
                    (status, reply) = (201, new JsonObject { ["id"] = id });
                    break;
                case ["projects", "may"]:
                    reply = new JsonObject { ["create"] = MayCreate, ["global"] = false };
                    break;
                case ["projects", var pid, ..] when !ArenaProjects.ContainsKey(pid):
                    status = 404;
                    break;
                case ["projects", var pid] when method == "GET":
                    reply = ArenaProjects[pid].DeepClone();
                    break;
                case ["projects", var pid, "report"]:
                    reply = new JsonObject
                    {
                        ["from"] = ctx.Request.QueryString["from"], ["to"] = ctx.Request.QueryString["to"],
                        ["totals"] = Used.TryGetValue(pid, out var used) ? used.DeepClone()
                            : new JsonObject { ["requests"] = 0, ["input"] = 0, ["cached"] = 0, ["output"] = 0, ["cost"] = 0, ["agentSeconds"] = 0, ["activeSeconds"] = 0, ["people"] = 0, ["messages"] = 0 },
                        ["models"] = new JsonArray(new JsonObject { ["name"] = "qwen", ["requests"] = 3, ["tokens"] = 1200, ["cost"] = 0.25 }),
                        ["budget"] = null,
                    };
                    break;
                case ["projects", var pid, "goals"] when method == "POST":
                    if (ArenaProjects[pid].Str("role") is not ("owner" or "contributor"))
                    {
                        (status, reply) = (403, new JsonObject { ["status"] = "role", ["error"] = "Viewers see the project; its contributors and owners add to it." });
                        break;
                    }
                    var goal = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["title"] = Json.ParseObject(body)?.Str("title"), ["status"] = "planned" };
                    ArenaProjects[pid]["goals"]!.AsArray().Add(goal);
                    (status, reply) = (201, new JsonObject { ["id"] = goal["id"]!.GetValue<string>() });
                    break;
                default:
                    status = 404;
                    break;
            }
        }
        ctx.Response.StatusCode = status;
        if (reply is not null)
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()));
        }
    }

    /// <summary>The person's files at /api/chat/attachments, taken with the key (as Arena's KeyAuthentication does): an upload, a file's content.</summary>
    internal async Task HandleFilesAsync(HttpListenerContext ctx, string body)
    {
        var path = ctx.Request.Url!.PathAndQuery;
        lock (_gate)
        {
            Relayed.Add((ctx.Request.HttpMethod, path, ctx.Request.ContentType, ctx.Request.Headers["Authorization"], ctx.Request.Headers["Range"], body));
        }
        if (ctx.Request.Headers["Authorization"] != "Bearer " + FakeGateway.Key)
        {
            ctx.Response.StatusCode = 401;
            return;
        }
        if (ctx.Request.HttpMethod == "POST" && ctx.Request.Headers["X-Requested-With"] is null)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        var parts = ctx.Request.Url.AbsolutePath["/api/chat/attachments".Length..].Trim('/').Split('/');
        if (ctx.Request.HttpMethod == "POST" && parts is [""])
        {
            var id = Guid.NewGuid().ToString();
            var name = System.Text.RegularExpressions.Regex.Match(body, "filename=\"?([^\";\r\n]+)").Groups[1].Value;
            lock (_gate)
            {
                Files[id] = (Encoding.UTF8.GetBytes(body), "text/plain");
            }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["fileName"] = name, ["kind"] = "text" }.ToJsonString()));
            return;
        }
        if (parts is [var file, "content"])
        {
            (byte[] Data, string ContentType) found;
            bool known;
            lock (_gate)
            {
                known = Files.TryGetValue(file, out found);
            }
            ctx.Response.StatusCode = known ? 200 : 404;
            if (known)
            {
                ctx.Response.ContentType = found.ContentType;
                ctx.Response.Headers["Content-Disposition"] = "inline";
                ctx.Response.Headers["X-Not-Passed"] = "no";
                await ctx.Response.OutputStream.WriteAsync(found.Data);
            }
            return;
        }
        ctx.Response.StatusCode = 404;
    }

    internal async Task HandleAsync(HttpListenerContext ctx, string body)
    {
        if (Status != 0)
        {
            ctx.Response.StatusCode = Status;
            return;
        }
        var parts = ctx.Request.Url!.AbsolutePath["/api/code-arena/".Length..].Split('/');
        var method = ctx.Request.HttpMethod;
        JsonNode? reply;
        var status = 200;
        if (parts is ["tokenize"] && method == "POST" && Tokenizer)
        {
            // The engine's tokenizer, as Arena counts with it: each text, and the whole request with its chat format.
            var counting = Json.ParseObject(body)!;
            var texts = (counting["texts"] as JsonArray ?? []).Select(t => t?.GetValue<string>() ?? "").ToList();
            Tokenized.Add(texts.Count);
            var messages = (counting["request"]?["messages"] as JsonArray)?.Count ?? 0;
            var answer = new JsonObject
            {
                ["counts"] = new JsonArray([.. texts.Select(t => (JsonNode)CountTokens(t))]),
                ["total"] = texts.Sum(CountTokens) + FormatPerMessage * messages,
                ["by"] = "llama.cpp",
            };
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answer.ToJsonString()));
            return;
        }
        if (parts is ["attachments", var file] && method == "GET")
        {
            (byte[] Data, string ContentType) found;
            bool known;
            lock (_gate)
            {
                known = Files.TryGetValue(file, out found);
            }
            ctx.Response.StatusCode = known ? 200 : 404;
            if (known)
            {
                ctx.Response.ContentType = found.ContentType;
                await ctx.Response.OutputStream.WriteAsync(found.Data);
            }
            return;
        }
        if (parts is ["chats", var planChat, "plan"] && method is "PUT" or "DELETE")
        {
            lock (_gate)
            {
                Plans.Add((planChat, method == "PUT" ? Json.ParseObject(body) : null));
            }
            ctx.Response.StatusCode = method == "PUT" ? 200 : 204;
            if (method == "PUT")
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""{"id":"plan-1","link":"/plans/plan-1"}"""));
            }
            return;
        }
        if (parts is ["chats", var ratedChat, "messages", var ratedMessage, "feedback"] && method is "PUT" or "DELETE")
        {
            var rating = method == "PUT" ? Json.ParseObject(body) : null;
            lock (_gate)
            {
                Ratings.Add((ratedChat, ratedMessage, rating));
            }
            ctx.Response.StatusCode = rating is null ? 204 : 200;
            if (rating is not null)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["up"] = rating.Bool("up") == true, ["reason"] = rating.Str("reason"), ["comment"] = rating.Str("comment"), ["shared"] = rating.Bool("share") == true,
                }.ToJsonString()));
            }
            return;
        }
        if (parts is ["chats", var goalChat, "goal"] && method is "PUT" or "DELETE")
        {
            lock (_gate)
            {
                Goals.Add((goalChat, method == "PUT" ? Json.ParseObject(body) : null));
            }
            ctx.Response.StatusCode = method == "PUT" ? 200 : 204;
            if (method == "PUT")
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""{"saved":true}"""));
            }
            return;
        }
        if (parts is ["assistants"] && method == "GET")
        {
            ctx.Response.StatusCode = Assistants is null ? 404 : 200;
            if (Assistants is { } assistants)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(assistants.ToJsonString()));
            }
            return;
        }
        if (parts is ["security"] && method == "GET")
        {
            lock (_gate)
            {
                SecurityAsked.Add(ctx.Request.Url!.Query);
            }
            ctx.Response.StatusCode = Security is null ? 404 : 200;
            if (Security is { } security)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(security.ToJsonString()));
            }
            return;
        }
        if (parts is ["settings"] && method == "GET")
        {
            // Arena's word for the settings it gives: it changes with any of them.
            var said = $"{ChatConfig?.ToJsonString()}|{ArenaSkills?.ToJsonString()}|{string.Join(",", SkillsOff ?? [])}";
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(said)));
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["stamp"] = stamp }.ToJsonString()));
            return;
        }
        if (parts is ["config"] && method == "GET")
        {
            ctx.Response.StatusCode = ChatConfig is null ? 404 : 200;
            if (ChatConfig is { } config)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(config.ToJsonString()));
            }
            return;
        }
        if (parts is ["skills"] && method == "GET" && (SkillsOff is not null || ArenaSkills is not null))
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject
            {
                ["skills"] = ArenaSkills?.DeepClone() ?? new JsonArray(), ["off"] = new JsonArray([.. (SkillsOff ?? []).Select(n => (JsonNode)n)]),
            }.ToJsonString()));
            return;
        }
        if (parts is ["memories", ..] && MemoryDenied)
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""{"error":"permission","detail":"That needs feature.memory."}"""));
            return;
        }
        if (parts is ["memories", ..] && Memory)
        {
            await MemoryAsync(ctx, parts[1..], method, body);
            return;
        }
        if (parts is ["skills"] && method == "POST" || parts is ["skills", not "learn"] && method == "PUT")
        {
            // One of the person's own, made or changed from Code Arena: theirs in Arena from then on (409: they have one of
            // that name; 404: they have none to change).
            var json = (JsonNode.Parse(body) as JsonObject)!;
            var making = parts.Length == 1;
            var name = making ? json["name"]?.GetValue<string>() ?? "" : parts[1];
            JsonObject? skill = null;
            lock (_gate)
            {
                (making ? SkillsMade : SkillsChanged).Add(json);
                var had = (ArenaSkills ?? []).OfType<JsonObject>().FirstOrDefault(s => s["name"]?.GetValue<string>() == name && s["mine"]?.GetValue<bool>() == true);
                if (making == (had is null))
                {
                    // A description or parameters not sent stay as they were.
                    skill = OwnSkill(name, json["description"]?.GetValue<string>() ?? had?["description"]?.GetValue<string>() ?? "", json["instructions"]?.GetValue<string>() ?? "",
                        (json["parameters"] ?? had?["parameters"])?.DeepClone().AsArray());
                    // A new list: GET reads the one it has.
                    ArenaSkills = [.. (ArenaSkills ?? []).OfType<JsonObject>().Where(s => s != had).Select(s => s.DeepClone()), skill.DeepClone()];
                }
            }
            ctx.Response.StatusCode = skill is not null ? 200 : making ? 409 : 404;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes((skill ?? new JsonObject { ["status"] = "name", ["error"] = making ? $"You have a skill /{name} already." : $"You have no skill /{name}." }).ToJsonString()));
            return;
        }
        if (parts is ["skills", "import"] && method == "POST")
        {
            // Arena's import: a preview says what it would do (its safeguards' refusals), an import makes them the person's own.
            var json = (JsonNode.Parse(body) as JsonObject)!;
            var preview = json["preview"]?.GetValue<bool>() == true;
            var answer = new JsonObject();
            lock (_gate)
            {
                SkillsImported.Add(json);
                var mine = (ArenaSkills ?? []).OfType<JsonObject>().Where(s => s["mine"]?.GetValue<bool>() == true).Select(s => s["name"]!.GetValue<string>()).ToHashSet();
                var looked = new JsonArray();
                var imported = new JsonArray();
                var skipped = new JsonArray();
                foreach (var s in (json["skills"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var folder = s["folder"]!.GetValue<string>();
                    var md = s["files"]!["SKILL.md"]!.GetValue<string>();
                    var name = System.Text.RegularExpressions.Regex.Match(md, "name: *([^\n]+)") is { Success: true } m ? m.Groups[1].Value.Trim() : folder;
                    var problem = SkillsRefused.TryGetValue(folder, out var why) ? "The safeguards refused " + why : null;
                    looked.Add(new JsonObject { ["folder"] = folder, ["name"] = name, ["problem"] = problem, ["clash"] = mine.Contains(name) ? new JsonObject { ["title"] = name } : null, ["suggested"] = name + "-2" });
                    var choice = (json["choices"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => c["folder"]?.GetValue<string>() == folder);
                    var action = choice?["action"]?.GetValue<string>() ?? (mine.Contains(name) ? "skip" : "import");
                    if (problem is not null || action == "skip")
                    {
                        skipped.Add(new JsonObject { ["folder"] = folder, ["name"] = name, ["reason"] = problem ?? "Skipped." });
                        continue;
                    }
                    var made = action == "rename" ? choice!["name"]!.GetValue<string>() : name;
                    imported.Add(new JsonObject { ["folder"] = folder, ["name"] = made, ["replaced"] = action == "replace" });
                    if (!preview)
                    {
                        ArenaSkills = [.. (ArenaSkills ?? []).OfType<JsonObject>().Where(x => !(x["mine"]?.GetValue<bool>() == true && x["name"]!.GetValue<string>() == made)).Select(x => x.DeepClone()),
                            OwnSkill(made, "Imported", "Imported instructions.")];
                    }
                }
                answer = preview ? new JsonObject { ["skills"] = looked, ["leftOut"] = new JsonArray(), ["room"] = 100 } : new JsonObject { ["imported"] = imported, ["skipped"] = skipped };
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answer.ToJsonString()));
            return;
        }
        if (parts is ["skills", "learn"] && method == "POST")
        {
            // A skill the agent worked out, kept as the person's: what Code Arena sent, and Arena's word.
            lock (_gate)
            {
                SkillsLearned.Add((JsonNode.Parse(body) as JsonObject)!);
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["text"] = "It waits for the person on the Skills page.", ["error"] = false }.ToJsonString()));
            return;
        }
        if (parts is ["history", var asked] && method == "POST")
        {
            // Arena's search of past chats: what Code Arena asked, and Arena's answer as the chat's tool words it.
            var json = JsonNode.Parse(body) as JsonObject;
            lock (_gate)
            {
                HistoryAsked.Add((asked, json!));
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject
            {
                ["text"] = asked == "search" ? "[c1] \"Parts vendor\" (chat, last 12 Oct 2026, /chat/c1)\nChose Northwind Traders." : "#1 the person: We chose Northwind Traders.",
                ["error"] = false,
            }.ToJsonString()));
            return;
        }
        if (parts is ["preferences"] && method == "GET")
        {
            ctx.Response.StatusCode = Preferences ? 200 : 404;
            if (Preferences)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["compactAt"] = CompactAt, ["compactAtDefault"] = CompactAtDefault }.ToJsonString()));
            }
            return;
        }
        lock (_gate)
        {
            if (parts is ["chats"] && method == "GET")
            {
                var limit = int.TryParse(ctx.Request.QueryString["limit"], out var l) ? Math.Clamp(l, 1, 200) : 30;
                var before = DateTimeOffset.TryParse(ctx.Request.QueryString["before"], out var b) ? b : (DateTimeOffset?)null;
                var page = All.OrderByDescending(c => c.Updated).Where(c => before is null || c.Updated < before).Take(limit + 1).ToList();
                reply = new JsonObject
                {
                    ["chats"] = new JsonArray([.. page.Take(limit).Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id, ["title"] = c.Title, ["origin"] = c.Ref is null ? null : "code-arena", ["originPlace"] = c.Place,
                        ["updatedAt"] = c.Updated.ToString("o"), ["messages"] = c.Branch.Count,
                    })]),
                    ["more"] = page.Count > limit,
                    ["me"] = new JsonObject { ["email"] = "pat@example.test", ["name"] = "Pat" },
                };
            }
            else if (parts is ["projects"] && method == "GET")
            {
                reply = new JsonObject
                {
                    ["projects"] = new JsonArray([.. Projects.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["global"] = false })]),
                    ["suggested"] = ctx.Request.QueryString["repo"] is { Length: > 0 } ? RepositoryProject : null,
                };
            }
            else if (parts is ["chats", var moved, "project"] && method == "PUT" && All.FirstOrDefault(c => c.Id == moved) is { } movedChat)
            {
                var to = JsonNode.Parse(body)?["projectId"]?.GetValue<string>();
                movedChat.ProjectId = to;
                Moves.Add((moved, to));
                reply = new JsonObject { ["projectId"] = to };
            }
            else if (parts is ["chats"] && method == "POST")
            {
                var b = JsonNode.Parse(body)!.AsObject();
                var reference = b["ref"]?.GetValue<string>();
                var known = All.FirstOrDefault(c => c.Ref is not null && c.Ref == reference);
                var chosen = b["projectId"]?.GetValue<string>();
                var chat = known ?? new Chat
                {
                    Id = Guid.NewGuid().ToString(), Ref = reference, Place = b["place"]?.GetValue<string>(), Updated = DateTimeOffset.UtcNow.AddTicks(++_made * 10),
                    // As Arena: the project chosen when the person may add to it, else the repository's.
                    ProjectId = Projects.Any(p => p.Id == chosen) ? chosen : RepositoryProject,
                };
                if (known is null)
                {
                    All.Add(chat);
                }
                reply = new JsonObject
                {
                    ["id"] = chat.Id, ["created"] = known is null, ["projectId"] = chat.ProjectId,
                    ["projectName"] = Projects.FirstOrDefault(p => p.Id == chat.ProjectId).Name,
                };
            }
            else if (parts is ["chats", var id, ..] && All.FirstOrDefault(c => c.Id == id) is not { } chat)
            {
                (reply, status) = (new JsonObject { ["status"] = "gone", ["error"] = "That chat is not there any more: it was deleted." }, 404);
            }
            else if (parts is ["chats", var id2, ..] && Answering.Contains(id2) && method != "DELETE")
            {
                (reply, status) = (new JsonObject { ["status"] = "answering", ["error"] = "The chat is answering on the web." }, 409);
            }
            else if (parts is ["chats", var id3] && method == "GET")
            {
                var c = All.First(x => x.Id == id3);
                var since = ctx.Request.QueryString["since"];
                var limit = int.TryParse(ctx.Request.QueryString["limit"], out var l) ? Math.Clamp(l, 0, Page) : Page;
                if (ctx.Request.QueryString["stamp"] is { } stamp && stamp == Stamp(c))
                {
                    UnchangedReads++;
                    reply = new JsonObject { ["unchanged"] = true, ["stamp"] = stamp };
                }
                else
                {
                    if (limit > 0)
                    {
                        FullReads++;
                    }
                    reply = Read(c, since, limit);
                }
            }
            else if (parts is ["chats", var id4, "messages"] && method == "POST")
            {
                (reply, status) = Append(All.First(x => x.Id == id4), JsonNode.Parse(body)!.AsObject());
            }
            else if (parts is ["chats", var id5, "turn"] && method == "POST")
            {
                All.First(x => x.Id == id5).Turn = DateTimeOffset.UtcNow.AddSeconds(90);
                Turns.Add("hold");
                reply = new JsonObject { ["until"] = DateTimeOffset.UtcNow.AddSeconds(90).ToString("o") };
            }
            else if (parts is ["chats", var id6, "turn"] && method == "DELETE")
            {
                All.First(x => x.Id == id6).Turn = null;
                Turns.Add("end");
                (reply, status) = (null, 204);
            }
            else if (parts is ["chats", var id8, "queue", "take"] && method == "POST")
            {
                // What the person queued on the web while the turn holds the chat, out of its line.
                var c = All.First(x => x.Id == id8);
                var taken = c.Turn is not null ? c.Queued.ToList() : [];
                c.Queued.Clear();
                reply = new JsonObject { ["messages"] = new JsonArray([.. taken.Select((q, i) => (JsonNode)new JsonObject { ["id"] = $"w{i}", ["content"] = q, ["quote"] = null })]) };
            }
            else if (parts is ["chats", var id7, "continued"] && method == "POST")
            {
                Continued.Add(id7);
                (reply, status) = (null, 204);
            }
            else
            {
                (reply, status) = (new JsonObject { ["error"] = "no such thing" }, 404);
            }
        }
        ctx.Response.StatusCode = status;
        if (reply is not null)
        {
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
    }

    /// <summary>A send of Code Arena's, as Arena takes it (CodeArenaSync.AppendAsync).</summary>
    private (JsonNode Reply, int Status) Append(Chat c, JsonObject b)
    {
        var parent = b["parent"]?.GetValue<string>();
        var branch = b["branch"]?.GetValue<bool>() == true;
        var messages = b["messages"]!.AsArray().OfType<JsonObject>().ToList();
        if (parent is not null && c.Nodes.All(n => n.Id != parent))
        {
            var behind = Read(c, parent, Page);
            behind["status"] = "behind";
            return (behind, 409);
        }
        var ids = new JsonArray();
        var rest = messages;
        while (rest.Count > 0 && rest[0]["ref"]?.GetValue<string>() is { } reference
               && c.Nodes.FirstOrDefault(n => n.Parent == parent && n.Message["ref"]?.GetValue<string>() == reference) is { } had)
        {
            ids.Add(had.Id);
            parent = had.Id;
            rest = rest[1..];
        }
        var path = c.Branch;
        if (!branch && (parent is null ? path.Count > 0 : parent != c.Leaf))
        {
            var place = path.FindIndex(n => n.Id == parent);
            if (place >= 0 && rest.Count > 0 && rest[0]["role"]?.GetValue<string>() == "tool" && place + 1 < path.Count
                && path[place].Message["toolCalls"] is JsonArray { Count: > 0 } calls && path[place + 1].Message["role"]?.GetValue<string>() != "tool")
            {
                var callIds = calls.OfType<JsonObject>().Select(x => x["id"]?.GetValue<string>()).ToHashSet();
                var results = rest.TakeWhile(m => m["role"]?.GetValue<string>() == "tool" && callIds.Contains(m["toolCallId"]?.GetValue<string>())).ToList();
                foreach (var m in results)
                {
                    parent = AddNode(c, parent, m).Id;
                    ids.Add(parent);
                }
                if (results.Count > 0)
                {
                    path[place + 1].Parent = parent;
                }
            }
            var behind = Read(c, parent, Page);
            behind["status"] = "behind";
            behind["ids"] = ids;
            return (behind, 409);
        }
        foreach (var m in rest)
        {
            parent = AddNode(c, parent, m).Id;
            ids.Add(parent);
        }
        if (rest.Count > 0 || branch)
        {
            c.Leaf = parent;
        }
        if (b["summary"] is JsonObject summary && c.Nodes.FirstOrDefault(n => n.Id == summary["upTo"]?.GetValue<string>()) is { } covered)
        {
            covered.Summary = summary["text"]?.GetValue<string>();
        }
        if (c.Title == "New chat" && c.Messages.FirstOrDefault(m => m["role"]?.GetValue<string>() == "user") is { } first)
        {
            c.Title = first["content"]!.ToString().Split('\n')[0];
        }
        if (c.Turn is not null)
        {
            c.Turn = DateTimeOffset.UtcNow.AddSeconds(90);
        }
        c.Updated = DateTimeOffset.UtcNow;
        return (new JsonObject { ["ids"] = ids, ["count"] = c.Branch.Count, ["stamp"] = Stamp(c) }, 200);
    }
}
