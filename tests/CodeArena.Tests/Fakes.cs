using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>What the fake model says to one request: text, tool calls, reasoning.</summary>
public sealed record Reply(string Text, (string Name, string Args)[] Calls, string? Reasoning = null)
{
    public static Reply Say(string text, string? reasoning = null) => new(text, [], reasoning);
    public static Reply Call(params (string Name, string Args)[] calls) => new("", calls);
}

/// <summary>An HTTP server on 127.0.0.1 and a free port, handling each request on its own task.</summary>
public abstract class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
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
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(BaseUrl + "/");
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // Taken between the probe and the start: another port.
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
    public Func<JsonObject, Reply> Answer { get; set; } = _ => Reply.Say("Hello from the model.");
    /// <summary>An HTTP status a chat request is answered with instead of an answer (a backend restarting: 500); 0 answers it.</summary>
    public Func<JsonObject, int> Failure { get; set; } = _ => 0;
    /// <summary>Holds each answer until the request is cancelled (to test Ctrl+C).</summary>
    public bool Hang { get; set; }
    /// <summary>The requests whose answer stops after half its text, the connection dropped.</summary>
    public Func<JsonObject, bool> Drop { get; set; } = _ => false;
    /// <summary>The requests answered with a stream that never says anything.</summary>
    public Func<JsonObject, bool> Silent { get; set; } = _ => false;
    public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                    ["model_info"] = new JsonObject { ["max_input_tokens"] = Context, ["supports_function_calling"] = true, ["supports_reasoning"] = true, ["mode"] = "chat" },
                })]),
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
        if (Failure(request) is > 0 and var status)
        {
            await WriteJson(ctx, new JsonObject { ["error"] = new JsonObject { ["message"] = "The model's backend is restarting." } }, status);
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
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = reply.Calls.Length > 0 ? "tool_calls" : "stop" }),
        }.ToJsonString());
        var prompt = body.Length / 4;
        await WriteEvent(ctx, new JsonObject
        {
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt, ["completion_tokens"] = 7, ["total_tokens"] = prompt + 7,
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

    public static string System(JsonObject request) => request["messages"]![0]!["content"]!.GetValue<string>();

    public static List<string> ToolNames(JsonObject request) =>
        request["tools"] is JsonArray tools ? [.. tools.Select(t => t!["function"]!["name"]!.GetValue<string>())] : [];
}

/// <summary>Arena's MCP endpoint at /mcp: streamable HTTP, answering in JSON or as an event stream, with a session id.</summary>
public sealed class FakeMcp : FakeServer
{
    private readonly List<(string Method, string? Session)> _calls = [];

    public bool Sse { get; set; }
    /// <summary>Arena's default chat model, told in initialize's _meta as Arena MCP does; null: not told.</summary>
    public string? DefaultModel { get; set; }
    /// <summary>No endpoint: 404 for everything.</summary>
    public bool Missing { get; set; }
    /// <summary>Not 0: every request answered with this status (503: the server is down behind its proxy).</summary>
    public int Status { get; set; }
    /// <summary>While set, initialize waits for it: a server slow to answer, or one that never does.</summary>
    public TaskCompletionSource? Hold { get; set; }
    /// <summary>The tools it lists instead of Arena's three (web_search, read_file, create_issue): Argus's, say.</summary>
    public JsonArray? ToolList { get; set; }
    public string Url => BaseUrl + "/mcp";
    /// <summary>The sessions closed (DELETE), as a client that lets a connection go does.</summary>
    public int Closed => Volatile.Read(ref _closed);
    private int _closed;

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
        JsonNode result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "argus-arena", ["version"] = "4.2.0" },
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
        Paths = new AppPaths(Path.Combine(Root, "config"), Path.Combine(Root, "data"));
        Directory.CreateDirectory(Paths.ConfigDir);
        var json = new JsonObject
        {
            // Arena's address: the fake MCP server's, so /mcp is found there (or the gateway's, which has none).
            ["url"] = mcp?.BaseUrl ?? gateway.Url,
            ["gateway"] = gateway.Url,
            ["apiKey"] = FakeGateway.Key,
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

    /// <summary>Runs code-arena with these arguments and typed input; a terminal on stdin unless said otherwise.</summary>
    public async Task<int> Run(string input, params string[] args) => await Run(input, terminal: true, args);

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

/// <summary>Arena's side of Code Arena's chats (/api/code-arena): chats by id, each a list of messages, counted as Arena counts them.</summary>
public sealed class FakeChats
{
    private readonly object _gate = new();

    public sealed class Chat
    {
        public required string Id { get; init; }
        public string? Ref { get; init; }
        public string? Place { get; init; }
        public string Title { get; set; } = "New chat";
        public List<JsonObject> Messages { get; } = [];
        /// <summary>When it last changed: the list is newest first, a page at a time before a time.</summary>
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Reads of a chat that brought its messages, and those answered only "unchanged" (asked with its stamp).</summary>
    public int FullReads { get; private set; }
    public int UnchangedReads { get; private set; }

    /// <summary>The branch as one word, as Arena gives it: here its length (messages are only ever added).</summary>
    private static string Stamp(Chat c) => $"n{c.Messages.Count}";

    private int _made;

    /// <summary>Adds to a chat as the web does, while Code Arena may be reading it.</summary>
    public void Append(Chat chat, params JsonObject[] messages)
    {
        lock (_gate)
        {
            chat.Messages.AddRange(messages);
            chat.Updated = DateTimeOffset.UtcNow;
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
            chat.Messages.AddRange(messages);
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

    internal async Task HandleAsync(HttpListenerContext ctx, string body)
    {
        if (Status != 0)
        {
            ctx.Response.StatusCode = Status;
            return;
        }
        var parts = ctx.Request.Url!.AbsolutePath["/api/code-arena/".Length..].Split('/');
        var method = ctx.Request.HttpMethod;
        JsonNode reply;
        var status = 200;
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
                        ["updatedAt"] = c.Updated.ToString("o"), ["messages"] = c.Messages.Count,
                    })]),
                    ["more"] = page.Count > limit,
                };
            }
            else if (parts is ["chats"] && method == "POST")
            {
                var b = JsonNode.Parse(body)!.AsObject();
                var reference = b["ref"]?.GetValue<string>();
                var known = All.FirstOrDefault(c => c.Ref is not null && c.Ref == reference);
                var chat = known ?? new Chat { Id = Guid.NewGuid().ToString(), Ref = reference, Place = b["place"]?.GetValue<string>(), Updated = DateTimeOffset.UtcNow.AddTicks(++_made * 10) };
                if (known is null)
                {
                    All.Add(chat);
                }
                reply = new JsonObject { ["id"] = chat.Id, ["created"] = known is null };
            }
            else if (parts is ["chats", var id, ..] && All.FirstOrDefault(c => c.Id == id) is not { } chat)
            {
                (reply, status) = (new JsonObject { ["status"] = "gone", ["error"] = "That chat is not there any more: it was deleted." }, 404);
            }
            else if (parts is ["chats", var id2, ..] && Answering.Contains(id2))
            {
                (reply, status) = (new JsonObject { ["status"] = "answering", ["error"] = "The chat is answering on the web." }, 409);
            }
            else if (parts is ["chats", var id3] && method == "GET")
            {
                var c = All.First(x => x.Id == id3);
                var after = int.TryParse(ctx.Request.QueryString["after"], out var a) ? a : 0;
                if (ctx.Request.QueryString["stamp"] is { } stamp && stamp == Stamp(c))
                {
                    UnchangedReads++;
                    reply = new JsonObject { ["unchanged"] = true, ["stamp"] = stamp };
                }
                else
                {
                    FullReads++;
                    reply = new JsonObject { ["id"] = c.Id, ["count"] = c.Messages.Count, ["stamp"] = Stamp(c), ["messages"] = new JsonArray([.. c.Messages.Skip(after).Select(m => (JsonNode)m.DeepClone())]) };
                }
            }
            else if (parts is ["chats", var id4, "messages"] && method == "POST")
            {
                var c = All.First(x => x.Id == id4);
                var b = JsonNode.Parse(body)!.AsObject();
                var after = b["after"]!.GetValue<int>();
                if (after != c.Messages.Count)
                {
                    (reply, status) = (new JsonObject { ["status"] = "behind", ["count"] = c.Messages.Count, ["messages"] = new JsonArray([.. c.Messages.Skip(after).Select(m => (JsonNode)m.DeepClone())]) }, 409);
                }
                else
                {
                    c.Messages.AddRange(b["messages"]!.AsArray().OfType<JsonObject>().Select(m => m.DeepClone().AsObject()));
                    if (c.Title == "New chat" && c.Messages.FirstOrDefault(m => m["role"]?.GetValue<string>() == "user") is { } first)
                    {
                        c.Title = first["content"]!.ToString().Split('\n')[0];
                    }
                    c.Updated = DateTimeOffset.UtcNow;
                    reply = new JsonObject { ["count"] = c.Messages.Count, ["stamp"] = Stamp(c) };
                }
            }
            else
            {
                (reply, status) = (new JsonObject { ["error"] = "no such thing" }, 404);
            }
        }
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }
}
