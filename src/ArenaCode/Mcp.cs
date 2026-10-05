using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ArenaCode;

internal class McpException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>There is no MCP endpoint at the address (404, 405, or a web page answered).</summary>
internal sealed class McpUnavailableException(string message) : McpException(message);

/// <summary>How JSON-RPC messages reach an MCP server and come back.</summary>
internal interface IMcpTransport : IAsyncDisposable
{
    /// <summary>A request: its result, or McpException with the server's error.</summary>
    Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct);
    Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct);
}

/// <summary>An MCP tool's answer as text for the model, and whether it is an error.</summary>
internal sealed record McpResult(string Text, bool IsError);

/// <summary>
/// An MCP client: the handshake (initialize, then notifications/initialized),
/// tools/list across pages, and tools/call.
/// </summary>
internal sealed class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    private readonly IMcpTransport _transport;

    private McpClient(string name, IMcpTransport transport)
    {
        Name = name;
        _transport = transport;
    }

    public string Name { get; }
    /// <summary>What the server asks its clients to tell the model.</summary>
    public string? Instructions { get; private set; }

    /// <summary>Arena's default chat model for this person (Arena MCP's initialize, _meta), if the server says.</summary>
    public string? DefaultModel { get; private set; }
    public string? ServerName { get; private set; }
    public List<JsonObject> Tools { get; private set; } = [];

    /// <summary>Connects and lists the tools; the handshake has 20 seconds.</summary>
    public static async Task<McpClient> ConnectAsync(string name, IMcpTransport transport, CancellationToken ct)
    {
        var client = new McpClient(name, transport);
        if (transport is HttpMcpTransport http)
        {
            http.Reinitialize = client.InitializeAsync;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await client.InitializeAsync(deadline.Token);
            client.Tools = await client.ListToolsAsync(deadline.Token);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
        return client;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var result = await _transport.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "arena-code", ["version"] = Cli.Version },
        }, ct) as JsonObject ?? throw new McpException("The server answered initialize with nothing.");
        Instructions = result.Str("instructions");
        DefaultModel = result["_meta"]?["arena/defaultModel"] is JsonValue model && model.TryGetValue<string>(out var name) ? name : null;
        ServerName = result["serverInfo"].Str("name");
        if (_transport is HttpMcpTransport http)
        {
            http.Protocol = result.Str("protocolVersion") ?? ProtocolVersion;
        }
        await _transport.NotifyAsync("notifications/initialized", null, ct);
    }

    private async Task<List<JsonObject>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<JsonObject>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var parameters = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var result = await _transport.RequestAsync("tools/list", parameters, ct);
            tools.AddRange((result?["tools"] as JsonArray ?? []).OfType<JsonObject>().Where(t => t.Str("name") is { Length: > 0 }).Select(t => t.Clone()));
            cursor = result.Str("nextCursor");
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }
        return tools;
    }

    public async Task<McpResult> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        var result = await _transport.RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.Clone() }, ct) as JsonObject;
        if (result is null)
        {
            return new McpResult("(no result)", false);
        }
        var parts = new List<string>();
        foreach (var item in (result["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            parts.Add(item.Str("type") switch
            {
                "text" => item.Str("text") ?? "",
                "image" or "audio" => $"[{item.Str("type")}: {item.Str("mimeType") ?? "?"}, {(item.Str("data")?.Length ?? 0) * 3 / 4:N0} bytes, not shown]",
                "resource" => item["resource"].Str("text") ?? $"[resource: {item["resource"].Str("uri")}]",
                "resource_link" => $"[{item.Str("name") ?? "link"}: {item.Str("uri")}]",
                _ => Json.Line(item),
            });
        }
        if (parts.Count == 0 && result["structuredContent"] is JsonNode structured)
        {
            parts.Add(Json.Line(structured));
        }
        return new McpResult(parts.Count == 0 ? "(empty result)" : string.Join("\n", parts), result.Bool("isError") ?? false);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

/// <summary>
/// MCP over streamable HTTP: each message a POST; the answer is JSON or an event
/// stream; the session id the server gives is sent back on every request after.
/// </summary>
internal sealed class HttpMcpTransport(HttpClient http, string url, IReadOnlyDictionary<string, string> headers) : IMcpTransport
{
    private long _id;
    private string? _session;

    /// <summary>The protocol version agreed at initialize, sent on later requests.</summary>
    public string? Protocol { get; set; }
    /// <summary>Called when the server has forgotten the session (404 with one): a new handshake.</summary>
    public Func<CancellationToken, Task>? Reinitialize { get; set; }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var id = Interlocked.Increment(ref _id);
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters.Clone();
            }
            using var res = await SendAsync(message, ct);
            if (res.StatusCode == HttpStatusCode.NotFound && _session is not null && attempt == 0 && method != "initialize" && Reinitialize is not null)
            {
                _session = null;
                Protocol = null;
                await Reinitialize(ct);
                continue;
            }
            await CheckAsync(res, ct);
            if (res.Headers.TryGetValues("Mcp-Session-Id", out var session))
            {
                _session = session.FirstOrDefault() ?? _session;
            }
            if (res.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                await foreach (var ev in Sse.ReadAsync(stream, ct))
                {
                    if (Json.Parse(ev.Data) is not JsonObject node)
                    {
                        continue;
                    }
                    if (node["method"] is not null && node["id"] is not null)
                    {
                        await AnswerServerAsync(node, ct);
                    }
                    else if (SameId(node["id"], id))
                    {
                        return Unwrap(node);
                    }
                }
                throw new McpException($"{url} closed the stream without answering {method}.");
            }
            var text = await res.Content.ReadAsStringAsync(ct);
            var answer = Json.Parse(text) switch
            {
                JsonObject o => o,
                JsonArray batch => batch.OfType<JsonObject>().FirstOrDefault(o => SameId(o["id"], id)),
                _ => null,
            } ?? throw new McpException($"{url} answered {method} with something that is not JSON-RPC.");
            return Unwrap(answer);
        }
    }

    public async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters.Clone();
        }
        using var res = await SendAsync(message, ct);
        await CheckAsync(res, ct);
    }

    /// <summary>The server's own requests on a stream (ping): answered, or refused as not supported.</summary>
    private async Task AnswerServerAsync(JsonObject request, CancellationToken ct)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone() };
        if (request.Str("method") == "ping")
        {
            reply["result"] = new JsonObject();
        }
        else
        {
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported by arena-code." };
        }
        try
        {
            using var res = await SendAsync(reply, ct);
        }
        catch (HttpRequestException)
        {
            // The answer this stream carries matters more than the server's question.
        }
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject message, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Json.Line(message), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (name, value) in headers)
        {
            req.Headers.TryAddWithoutValidation(name, value);
        }
        if (_session is not null)
        {
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        }
        if (Protocol is not null)
        {
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Protocol);
        }
        try
        {
            return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new McpException(Net.Explain(e, url));
        }
    }

    private async Task CheckAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var status = (int)res.StatusCode;
        var html = res.Content.Headers.ContentType?.MediaType == "text/html";
        if (status is 404 or 405 || (html && res.IsSuccessStatusCode))
        {
            throw new McpUnavailableException($"{url} has no MCP endpoint ({status}).");
        }
        if (status is 401 or 403)
        {
            throw new McpException($"{url} refused the credentials ({status}).", status);
        }
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            var detail = Json.ParseObject(body)?["error"].Str("message") ?? (body.Length > 300 ? body[..300] : body);
            throw new McpException($"{url} answered {status}: {detail.Trim()}", status);
        }
    }

    private static bool SameId(JsonNode? node, long id) => node is JsonValue && Json.Number(node) == id;

    internal static JsonNode? Unwrap(JsonObject answer)
    {
        if (answer["error"] is JsonObject error)
        {
            throw new McpException(error.Str("message") ?? "The server answered with an error.", error.Int("code"));
        }
        return answer["result"];
    }

    /// <summary>Ends the session on the server, when it gave one (best effort).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_session is null)
        {
            return;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
            foreach (var (name, value) in headers)
            {
                req.Headers.TryAddWithoutValidation(name, value);
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var res = await http.SendAsync(req, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            // Leaving anyway: the server forgets idle sessions.
        }
        _session = null;
    }
}

/// <summary>MCP over a child process's stdin and stdout, one JSON message per line.</summary>
internal sealed class StdioMcpTransport : IMcpTransport
{
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly Process? _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private readonly Task _reader;
    private long _id;

    public StdioMcpTransport(Stream toServer, Stream fromServer, Process? process = null)
    {
        _input = new StreamWriter(toServer, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        _output = new StreamReader(fromServer, Encoding.UTF8);
        _process = process;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Starts the server's command; its stderr is kept (the last lines) to explain a failure.</summary>
    public static StdioMcpTransport Start(McpServerConfig server, string cwd)
    {
        var psi = new ProcessStartInfo(server.Command!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = server.Cwd is { Length: > 0 } dir ? Path.GetFullPath(dir, cwd) : cwd,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in server.Args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in server.Env)
        {
            psi.Environment[name] = Expand(value);
        }
        psi.Environment.Remove("ARENA_API_KEY");
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new McpException($"Could not start {server.Command}.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new McpException($"Could not start {server.Command}: {e.Message}" +
                (OperatingSystem.IsWindows() ? " (for npx and other .cmd scripts, use \"command\": \"cmd\", \"args\": [\"/c\", \"npx\", ...])" : ""));
        }
        var transport = new StdioMcpTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, process);
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (transport._stderr)
                {
                    transport._stderr.Enqueue(line);
                    while (transport._stderr.Count > 20)
                    {
                        transport._stderr.Dequeue();
                    }
                }
            }
        });
        return transport;
    }

    /// <summary>${NAME} in a value becomes that environment variable: secrets stay out of the config.</summary>
    public static string Expand(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters.Clone();
        }
        try
        {
            await WriteAsync(message, ct);
            return HttpMcpTransport.Unwrap(await tcs.Task.WaitAsync(ct));
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters.Clone();
        }
        return WriteAsync(message, ct);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            await _input.WriteLineAsync(Json.Line(message).AsMemory(), ct);
            await _input.FlushAsync(ct);
        }
        catch (IOException)
        {
            throw new McpException("The server is not running" + StderrTail());
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _output.ReadLineAsync() is { } line)
            {
                if (Json.Parse(line) is not JsonObject message)
                {
                    continue; // a server that logs to stdout: not ours to read
                }
                if (message["method"] is not null)
                {
                    if (message["id"] is not null)
                    {
                        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone() };
                        if (message.Str("method") == "ping")
                        {
                            reply["result"] = new JsonObject();
                        }
                        else
                        {
                            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported by arena-code." };
                        }
                        await WriteAsync(reply, CancellationToken.None);
                    }
                    continue;
                }
                if (Json.Number(message["id"]) is { } id && _pending.TryGetValue(id, out var waiting))
                {
                    waiting.TrySetResult(message);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or McpException)
        {
            // The server went away: the pending requests are failed below.
        }
        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetException(new McpException("The server exited" + StderrTail()));
        }
    }

    private string StderrTail()
    {
        lock (_stderr)
        {
            return _stderr.Count == 0 ? "." : ": " + string.Join(" / ", _stderr.TakeLast(3));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _input.Close();
        }
        catch (IOException)
        {
            // Already gone.
        }
        if (_process is not null)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited meanwhile.
                }
            }
            _process.Dispose();
        }
        await Task.WhenAny(_reader, Task.Delay(1000));
    }
}
