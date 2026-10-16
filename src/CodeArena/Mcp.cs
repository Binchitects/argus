using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

internal class McpException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
    /// <summary>The server could not be reached, or went away mid-answer (not an answer of its own): worth connecting again.</summary>
    public bool Lost { get; init; }
    /// <summary>The name in its address does not resolve.</summary>
    public bool NoSuchHost { get; init; }
}

/// <summary>There is no MCP endpoint at the address (404, 405, or a web page answered).</summary>
internal sealed class McpUnavailableException(string message) : McpException(message);

/// <summary>How JSON-RPC messages reach an MCP server and come back.</summary>
internal interface IMcpTransport : IAsyncDisposable
{
    /// <summary>
    /// A request: its result, or McpException with the server's error. <paramref name="progress"/> hears the server's
    /// progress notifications for it (those of its params._meta.progressToken). Given up (cancelled), the server is told
    /// with notifications/cancelled, so it can stop the work.
    /// </summary>
    Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct, Action<JsonObject>? progress = null);
    Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct);
}

/// <summary>An MCP tool's answer as text for the model, and whether it is an error; the files it made, as saved (<see cref="McpMedia"/>).</summary>
internal sealed record McpResult(string Text, bool IsError)
{
    public IReadOnlyList<string> Saved { get; init; } = [];
}

/// <summary>
/// Where the pictures, sound and video a tool returns are saved: a folder in the workspace (.code-arena/media), and its
/// path as the model and the person see it.
/// </summary>
internal sealed record McpMedia(string Folder, string Shown)
{
    /// <summary>The folder of a workspace: .code-arena/media under its root.</summary>
    public static McpMedia For(string root) => new(Path.Combine(root, ".code-arena", "media"), ".code-arena/media");

    /// <summary>The largest file a link is fetched for (resources/read): a bigger one stays a link.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>The extension for a media type; null for anything but a picture, sound or video.</summary>
    public static string? Extension(string? type) => (type ?? "").Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/avif" => ".avif",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/ogg" or "audio/opus" => ".ogg",
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
        "audio/webm" => ".weba",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/ogg" => ".ogv",
        "video/quicktime" => ".mov",
        _ => null,
    };

    /// <summary>
    /// Saves a file a tool made, named after the tool and the time (with a number when that name is taken); the folder is
    /// made with a .gitignore of its own, so nothing saved there is committed. Its path as the model sees it.
    /// </summary>
    public string Save(string tool, string extension, byte[] bytes, DateTime now)
    {
        // A link in the project's own files (.code-arena, or media, leading elsewhere) is not followed: nothing is written outside.
        for (var dir = Folder; dir.Length > 0 && Path.GetFileName(dir) is "media" or ".code-arena"; dir = Path.GetDirectoryName(dir) ?? "")
        {
            if (new DirectoryInfo(dir) is { Exists: true, LinkTarget: not null } || File.Exists(dir))
            {
                throw new IOException($"{dir} is a link or a file, not a folder: nothing is saved through it.");
            }
        }
        if (!Directory.Exists(Folder))
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, ".gitignore"), "# What Arena's tools made for Code Arena: kept out of the repository.\n*\n");
        }
        var stem = $"{Safe(tool)}-{now:yyyyMMdd-HHmmss}";
        for (var n = 1; ; n++)
        {
            var name = (n == 1 ? stem : $"{stem}-{n}") + extension;
            try
            {
                using var file = new FileStream(Path.Combine(Folder, name), FileMode.CreateNew, FileAccess.Write);
                file.Write(bytes);
                return $"{Shown}/{name}";
            }
            catch (IOException) when (n < 1000 && File.Exists(Path.Combine(Folder, name)))
            {
                // Taken (another file of the same second): the next number.
            }
        }
    }

    private static string Safe(string tool) => System.Text.RegularExpressions.Regex.Replace(tool, "[^A-Za-z0-9_-]", "_") is { Length: > 0 } safe ? safe : "tool";
}

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

    /// <summary>How long the handshake (initialize, then the tools listed) may take.</summary>
    public static readonly TimeSpan Handshake = TimeSpan.FromSeconds(20);

    /// <summary>Connects and lists the tools, within <see cref="Handshake"/>.</summary>
    public static async Task<McpClient> ConnectAsync(string name, IMcpTransport transport, CancellationToken ct)
    {
        var client = new McpClient(name, transport);
        if (transport is HttpMcpTransport http)
        {
            http.Reinitialize = client.InitializeAsync;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Handshake);
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
            ["clientInfo"] = new JsonObject { ["name"] = "code-arena", ["version"] = Cli.Version },
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

    /// <summary>How long a tool call may go without an answer (or a progress report), unless the tool says otherwise.</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromMinutes(10);

    /// <summary>How often at most a call's progress is told to the person.</summary>
    public static TimeSpan ProgressEvery { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The time a tool says its calls may take (its _meta's "arena/timeoutSeconds" or "timeoutSeconds": a video takes
    /// longer than a search), else <see cref="CallLimit"/>; from ten seconds to a day.
    /// </summary>
    public static TimeSpan LimitOf(JsonObject? tool) =>
        (tool?["_meta"] is JsonObject meta ? Json.Number(meta["arena/timeoutSeconds"]) ?? Json.Number(meta["timeoutSeconds"]) : null) is { } seconds and > 0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 86_400))
            : CallLimit;

    /// <summary>
    /// Calls a tool. It has <paramref name="limit"/> (else its own, <see cref="LimitOf"/>) to answer, counted anew from each
    /// progress report it sends: past it the call is given up, and the model told it timed out. Its progress is told
    /// through <paramref name="progress"/> (every <see cref="ProgressEvery"/> at most). The pictures, sound and video it
    /// returns (inline, or as links read with resources/read) are saved in <paramref name="media"/>'s folder, and the
    /// model is given their paths.
    /// </summary>
    public async Task<McpResult> CallAsync(string tool, JsonObject arguments, CancellationToken ct, TimeSpan? limit = null, Action<string>? progress = null,
        McpMedia? media = null)
    {
        var wait = limit ?? LimitOf(Tools.FirstOrDefault(t => t.Str("name") == tool));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);
        var told = DateTime.MinValue;
        var reports = 0;
        var parameters = new JsonObject
        {
            ["name"] = tool, ["arguments"] = arguments.Clone(), ["_meta"] = new JsonObject { ["progressToken"] = $"code-arena-{Guid.NewGuid():N}" },
        };
        JsonObject? result;
        try
        {
            result = await _transport.RequestAsync("tools/call", parameters, deadline.Token, p =>
            {
                // Still working: its time starts again.
                Interlocked.Increment(ref reports);
                try
                {
                    deadline.CancelAfter(wait);
                }
                catch (ObjectDisposedException)
                {
                    // The call ended meanwhile.
                }
                if (progress is not null && DateTime.UtcNow - told >= ProgressEvery)
                {
                    told = DateTime.UtcNow;
                    progress(Progress(tool, p));
                }
            }) as JsonObject;
        }
        catch (Exception e) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested && e is OperationCanceledException or McpException or IOException or HttpRequestException)
        {
            var since = Volatile.Read(ref reports) > 0 ? " since its last progress report" : "";
            return new McpResult($"The call to {tool} timed out: the {Name} tools did not answer within {Fmt.Duration(wait)}{since}, so it was given up. " +
                                 "Try again with less to do, or another way.", true);
        }
        if (result is null)
        {
            return new McpResult("(no result)", false);
        }
        var parts = new List<string>();
        var saved = new List<string>();
        // Arena sends a small picture both inline and as a link to its file: the link of one saved already is not fetched.
        var inline = new List<string>();
        foreach (var item in (result["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = item.Str("type");
            var mime = item.Str("mimeType");
            if (type is "image" or "audio" && media is not null && McpMedia.Extension(mime) is { } extension && Decode(item.Str("data")) is { } data)
            {
                try
                {
                    var path = media.Save(tool, extension, data, DateTime.Now);
                    saved.Add(path);
                    inline.Add(mime ?? "");
                    parts.Add($"[{type} saved as {path} ({mime}, {Size(data.Length)})]");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    parts.Add($"[{type}: {mime}, {Size(data.Length)}, not saved: {Fmt.OneLine(e.Message, 160)}]");
                }
                continue;
            }
            if (type == "resource_link" && media is not null && McpMedia.Extension(mime) is { } linked)
            {
                var name = item.Str("name") ?? "link";
                var uri = item.Str("uri") ?? "";
                if (inline.Remove(mime ?? ""))
                {
                    continue;
                }
                if (Json.Number(item["size"]) is > McpMedia.MaxBytes)
                {
                    parts.Add($"[{name}: {uri} (too big to fetch here)]");
                    continue;
                }
                try
                {
                    var bytes = await ReadResourceAsync(uri, ct);
                    var path = media.Save(tool, linked, bytes, DateTime.Now);
                    saved.Add(path);
                    parts.Add($"[{name} saved as {path} ({mime}, {Size(bytes.Length)})]");
                }
                catch (Exception e) when (e is McpException or IOException or HttpRequestException or FormatException or UnauthorizedAccessException
                                          || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    parts.Add($"[{name}: {uri} (it could not be fetched: {Fmt.OneLine(e.Message, 160)})]");
                }
                continue;
            }
            parts.Add(type switch
            {
                "text" => item.Str("text") ?? "",
                "image" or "audio" => $"[{type}: {mime ?? "?"}, {(item.Str("data")?.Length ?? 0) * 3 / 4:N0} bytes, not shown]",
                "resource" => item["resource"].Str("text") ?? $"[resource: {item["resource"].Str("uri")}]",
                "resource_link" => $"[{item.Str("name") ?? "link"}: {item.Str("uri")}]",
                _ => Json.Line(item),
            });
        }
        if (parts.Count == 0 && result["structuredContent"] is JsonNode structured)
        {
            parts.Add(Json.Line(structured));
        }
        if (saved.Count > 0)
        {
            parts.Add($"(Saved in the working directory: {string.Join(", ", saved)}. Give the person these paths: the IDE shows them.)");
        }
        var answer = new McpResult(parts.Count == 0 ? "(empty result)" : string.Join("\n", parts), result.Bool("isError") ?? false);
        return saved.Count > 0 ? answer with { Saved = saved } : answer;

        static byte[]? Decode(string? data)
        {
            try
            {
                return data is { Length: > 0 } ? Convert.FromBase64String(data) : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>A file the server links to (resources/read), within two minutes: its bytes (a blob, or its text).</summary>
    public async Task<byte[]> ReadResourceAsync(string uri, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var result = await _transport.RequestAsync("resources/read", new JsonObject { ["uri"] = uri }, deadline.Token);
        var item = (result?["contents"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? throw new McpException($"{uri} came back empty.");
        return item.Str("blob") is { } blob ? Convert.FromBase64String(blob) : Encoding.UTF8.GetBytes(item.Str("text") ?? "");
    }

    /// <summary>A progress report in words: "generate_video is still working: 3 of 10, sampling."</summary>
    private static string Progress(string tool, JsonObject p)
    {
        var done = Real(p["progress"]);
        var total = Real(p["total"]);
        var how = done is null ? "" : total is > 0 ? $": {done:0.##} of {total:0.##}" : $": {done:0.##}";
        var message = p.Str("message") is { Length: > 0 } m ? $"{(how.Length > 0 ? "," : ":")} {Fmt.OneLine(m, 160)}" : "";
        return $"{tool} is still working{how}{message}.";

        static double? Real(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

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

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct, Action<JsonObject>? progress = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var id = Interlocked.Increment(ref _id);
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters.Clone();
            }
            try
            {
                using var res = await SendAsync(message, ct);
                if (res.StatusCode == HttpStatusCode.NotFound && _session is not null && attempt == 0 && method != "initialize" && Reinitialize is not null)
                {
                    _session = null;
                    Protocol = null;
                    await Reinitialize(ct);
                    continue;
                }
                try
                {
                    return await AnswerAsync(res, method, id, ct, progress);
                }
                catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
                {
                    // The connection dropped while the answer was read (the server went away, a proxy cut it).
                    throw new McpException($"{url} dropped the connection while answering {method}: {e.Message}") { Lost = true };
                }
            }
            catch (Exception) when (ct.IsCancellationRequested && method != "initialize")
            {
                // Given up (stopped, or past its time): the server is told so it stops the work, not waited for.
                _ = CancelledAsync(id);
                throw;
            }
        }
    }

    /// <summary>Tells the server a request was given up (notifications/cancelled), within a few seconds, whatever comes of it.</summary>
    private async Task CancelledAsync(long id)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "given up by code-arena" }, wait.Token);
        }
        catch (Exception e) when (e is McpException or HttpRequestException or OperationCanceledException or IOException)
        {
            // The server forgets it when its answer finds nobody.
        }
    }

    /// <summary>The answer to request id: from the JSON body, or from the event stream (answering the server's own requests on it, hearing its progress).</summary>
    private async Task<JsonNode?> AnswerAsync(HttpResponseMessage res, string method, long id, CancellationToken ct, Action<JsonObject>? progress)
    {
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
                else if (node.Str("method") == "notifications/progress" && node["params"] is JsonObject p)
                {
                    progress?.Invoke(p);
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
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported by code-arena." };
        }
        try
        {
            using var res = await SendAsync(reply, ct);
        }
        catch (McpException)
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
            throw new McpException(Net.Explain(e, url)) { Lost = true, NoSuchHost = Net.NoSuchHost(e) };
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
            // Its reason, when it gives one (Argus: the key is not valid, or GitLab cannot say what the person may read).
            var why = Reason(await res.Content.ReadAsStringAsync(ct));
            throw new McpException($"{url} refused the credentials ({status}){(why is null ? "." : $": {why}")}", status);
        }
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            var detail = Reason(body) ?? (body.Length > 300 ? body[..300] : body);
            // A proxy's 502, 503 or 504: the server behind it is down or does not answer.
            throw new McpException($"{url} answered {status}: {detail.Trim()}", status) { Lost = status is 502 or 503 or 504 };
        }
    }

    /// <summary>The error's words in a JSON body: {"error": "…"} or {"error": {"message": "…"}}.</summary>
    private static string? Reason(string body) => Json.ParseObject(body)?["error"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var text) => Fmt.OneLine(text, 300),
        JsonObject o => o.Str("message") is { } message ? Fmt.OneLine(message, 300) : null,
        _ => null,
    };

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
    /// <summary>Who hears each request's progress, by its progress token.</summary>
    private readonly ConcurrentDictionary<string, Action<JsonObject>> _progress = new(StringComparer.Ordinal);
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

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct, Action<JsonObject>? progress = null)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters.Clone();
        }
        var token = parameters?["_meta"].Str("progressToken");
        if (token is not null && progress is not null)
        {
            _progress[token] = progress;
        }
        try
        {
            await WriteAsync(message, ct);
            return HttpMcpTransport.Unwrap(await tcs.Task.WaitAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && method != "initialize")
        {
            // Given up: the server is told so it stops the work, not waited for.
            _ = CancelledAsync(id);
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            if (token is not null)
            {
                _progress.TryRemove(token, out _);
            }
        }
    }

    /// <summary>Tells the server a request was given up (notifications/cancelled), whatever comes of it.</summary>
    private async Task CancelledAsync(long id)
    {
        try
        {
            await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "given up by code-arena" }, CancellationToken.None);
        }
        catch (McpException)
        {
            // The server is gone already.
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
            throw new McpException("The server is not running" + StderrTail()) { Lost = true };
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
                            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported by code-arena." };
                        }
                        await WriteAsync(reply, CancellationToken.None);
                    }
                    else if (message.Str("method") == "notifications/progress" && message["params"] is JsonObject p
                             && p["progressToken"] is JsonValue v && _progress.TryGetValue(v.ToString(), out var hears))
                    {
                        hears(p);
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
            waiting.TrySetException(new McpException("The server exited" + StderrTail()) { Lost = true });
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
