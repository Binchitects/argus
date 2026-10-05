using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>A request as the server read it: the method, the decoded path, the query, the headers and the body.</summary>
internal sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string> Query { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public byte[] Body { get; init; } = [];

    public string? Header(string name) => Headers.GetValueOrDefault(name);

    /// <summary>A cookie's value, or null.</summary>
    public string? Cookie(string name)
    {
        foreach (var part in (Header("Cookie") ?? "").Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim() == name)
            {
                return part[(eq + 1)..].Trim();
            }
        }
        return null;
    }

    /// <summary>The body as a JSON object, or null when it is not one.</summary>
    public JsonObject? Json() => CodeArena.Json.ParseObject(Encoding.UTF8.GetString(Body));

    /// <summary>A WebSocket handshake (RFC 6455): a GET asking to upgrade, version 13, with a key.</summary>
    public bool IsWebSocket =>
        Method == "GET"
        && (Header("Upgrade") ?? "").Contains("websocket", StringComparison.OrdinalIgnoreCase)
        && (Header("Connection") ?? "").Contains("upgrade", StringComparison.OrdinalIgnoreCase)
        && Header("Sec-WebSocket-Version") == "13"
        && Header("Sec-WebSocket-Key") is { Length: > 0 } key
        && Convert.TryFromBase64String(key, new byte[24], out var n) && n == 16;
}

/// <summary>A request the server refuses before it is handled: too large, malformed, or of a kind it does not take.</summary>
internal sealed class HttpProblem(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Where a response goes: whole, or as a stream of server-sent events (chunked).</summary>
internal sealed class HttpResponse(Stream stream)
{
    private static readonly Dictionary<int, string> Reasons = new()
    {
        [100] = "Continue", [101] = "Switching Protocols", [200] = "OK", [204] = "No Content", [302] = "Found", [400] = "Bad Request", [401] = "Unauthorized",
        [403] = "Forbidden", [404] = "Not Found", [405] = "Method Not Allowed", [409] = "Conflict", [413] = "Content Too Large",
        [415] = "Unsupported Media Type", [431] = "Request Header Fields Too Large", [500] = "Internal Server Error", [501] = "Not Implemented",
        [503] = "Service Unavailable",
    };

    /// <summary>Headers for the response, besides the length and the connection's.</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Started { get; private set; }
    /// <summary>Whether the connection serves another request after this one.</summary>
    public bool KeepAlive { get; set; } = true;
    private bool _chunked;

    public async Task SendAsync(int status, byte[] body, string? contentType, CancellationToken ct)
    {
        if (contentType is not null)
        {
            Headers["Content-Type"] = contentType;
        }
        await WriteHeadAsync(status, status == 204 ? null : body.Length, ct);
        if (status != 204 && body.Length > 0)
        {
            await stream.WriteAsync(body, ct);
        }
        await stream.FlushAsync(ct);
    }

    public Task TextAsync(int status, string text, CancellationToken ct, string contentType = "text/plain; charset=utf-8") =>
        SendAsync(status, Encoding.UTF8.GetBytes(text), contentType, ct);

    public Task JsonAsync(int status, JsonNode json, CancellationToken ct) =>
        SendAsync(status, Encoding.UTF8.GetBytes(CodeArena.Json.Line(json)), "application/json; charset=utf-8", ct);

    /// <summary>{"status", "error"}: what the page's API client reads from a refusal.</summary>
    public Task ErrorAsync(int status, string code, string message, CancellationToken ct) =>
        JsonAsync(status, new JsonObject { ["status"] = code, ["error"] = message }, ct);

    public Task NoContentAsync(CancellationToken ct) => SendAsync(204, [], null, ct);

    /// <summary>Starts a stream of server-sent events; each EventAsync is one, EndAsync closes it.</summary>
    public async Task StartEventsAsync(CancellationToken ct)
    {
        Headers["Content-Type"] = "text/event-stream; charset=utf-8";
        Headers["Cache-Control"] = "no-store";
        Headers["X-Accel-Buffering"] = "no";
        _chunked = true;
        await WriteHeadAsync(200, null, ct);
        await stream.FlushAsync(ct);
    }

    public Task EventAsync(string data, CancellationToken ct) => ChunkAsync($"data: {data}\n\n", ct);

    /// <summary>A comment line: keeps the connection from looking idle.</summary>
    public Task CommentAsync(CancellationToken ct) => ChunkAsync(": still here\n\n", ct);

    public async Task EndAsync(CancellationToken ct)
    {
        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private async Task ChunkAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:x}\r\n"), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\r\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private async Task WriteHeadAsync(int status, int? length, CancellationToken ct)
    {
        if (Started)
        {
            throw new InvalidOperationException("The response has started already.");
        }
        Started = true;
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {status} {Reasons.GetValueOrDefault(status, "Status")}\r\n");
        if (_chunked)
        {
            head.Append("Transfer-Encoding: chunked\r\n");
        }
        else if (length is { } n)
        {
            head.Append($"Content-Length: {n}\r\n");
        }
        head.Append(KeepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        foreach (var (name, value) in Headers)
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), ct);
    }

    /// <summary>
    /// Answers a WebSocket handshake (101) and hands the connection over to the
    /// socket: it serves no more requests. Its frames are the base class
    /// library's own WebSocket over this stream.
    /// </summary>
    public async Task<WebSocket> AcceptWebSocketAsync(HttpRequest request, CancellationToken ct)
    {
        if (Started)
        {
            throw new InvalidOperationException("The response has started already.");
        }
        Started = true;
        KeepAlive = false;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.Header("Sec-WebSocket-Key") + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var head = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.FlushAsync(ct);
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
    }

    /// <summary>"100 Continue", for a client that waits for it before sending the body.</summary>
    public static async Task ContinueAsync(Stream stream, CancellationToken ct)
    {
        await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }
}

/// <summary>
/// A small HTTP/1.1 server on 127.0.0.1 only, for the web interface: requests with
/// a body of known length, keep-alive, and responses whole or as server-sent
/// events. The base class library's HttpListener needs rights to reserve an
/// address on Windows and works differently there; this runs the same everywhere.
/// </summary>
internal sealed class HttpServer : IAsyncDisposable
{
    public const int MaxHead = 32 * 1024;
    public const int MaxBody = 8 * 1024 * 1024;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Task, bool> _connections = new();
    private Task _accepting = Task.CompletedTask;

    /// <summary>Listens on 127.0.0.1 at the port (0: a free one). Throws SocketException when it is taken.</summary>
    public HttpServer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
    }

    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Told of a request that failed for an unexpected reason (answered 500).</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Starts taking connections; each request goes to the handler, one at a time per connection.</summary>
    public void Start(Func<HttpRequest, HttpResponse, CancellationToken, Task> handler) => _accepting = AcceptAsync(handler);

    private async Task AcceptAsync(Func<HttpRequest, HttpResponse, CancellationToken, Task> handler)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            var task = Task.Run(() => ConnectionAsync(client, handler));
            _connections[task] = true;
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task ConnectionAsync(TcpClient client, Func<HttpRequest, HttpResponse, CancellationToken, Task> handler)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new RequestReader(stream);
            while (!_stop.IsCancellationRequested)
            {
                HttpRequest? request;
                try
                {
                    // A connection kept alive, or a client that stalls mid-request, is let go after a minute.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    request = await reader.ReadAsync(idle.Token);
                }
                catch (HttpProblem e)
                {
                    try
                    {
                        await new HttpResponse(stream) { KeepAlive = false }.TextAsync(e.Status, e.Message, _stop.Token);
                    }
                    catch (Exception x) when (x is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                    }
                    return;
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                if (request is null)
                {
                    return;
                }
                var close = string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                var response = new HttpResponse(stream) { KeepAlive = !close };
                try
                {
                    await handler(request, response, _stop.Token);
                    if (!response.Started)
                    {
                        await response.TextAsync(404, "Not found.", _stop.Token);
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                catch (Exception e)
                {
                    OnError?.Invoke(e);
                    if (!response.Started)
                    {
                        try
                        {
                            response.KeepAlive = false;
                            await response.TextAsync(500, "Something went wrong in code-arena: " + e.Message, _stop.Token);
                        }
                        catch (Exception x) when (x is IOException or OperationCanceledException or ObjectDisposedException)
                        {
                        }
                    }
                    return;
                }
                if (!response.KeepAlive)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accepting;
        // Event streams end with their turn; a connection still open is cut after a moment.
        await Task.WhenAny(Task.WhenAll(_connections.Keys), Task.Delay(TimeSpan.FromSeconds(2)));
        _stop.Dispose();
    }

    /// <summary>Reads requests off a connection: the head up to the empty line, then the body by its length.</summary>
    private sealed class RequestReader(Stream stream)
    {
        private byte[] _buffer = new byte[8192];
        private int _length;

        /// <summary>The next request, or null when the client closed the connection between requests.</summary>
        public async Task<HttpRequest?> ReadAsync(CancellationToken ct)
        {
            var head = await HeadAsync(ct);
            if (head is null)
            {
                return null;
            }
            var lines = head.Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !start[1].StartsWith('/'))
            {
                throw new HttpProblem(400, "Not an HTTP/1.1 request this server takes.");
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    throw new HttpProblem(400, "A header line without a name.");
                }
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (headers.TryGetValue(name, out var earlier))
                {
                    // Two Host headers could be read two ways: refused, as is anything else ambiguous about who it is for.
                    if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new HttpProblem(400, $"Two {name} headers.");
                    }
                    value = earlier + (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ") + value;
                }
                headers[name] = value;
            }
            if (headers.ContainsKey("Transfer-Encoding"))
            {
                throw new HttpProblem(501, "Request bodies must come with a Content-Length.");
            }
            var length = 0;
            if (headers.TryGetValue("Content-Length", out var given) && (!int.TryParse(given, out length) || length < 0))
            {
                throw new HttpProblem(400, "Content-Length is not a length.");
            }
            if (length > MaxBody)
            {
                throw new HttpProblem(413, "The request is too large.");
            }
            if (length > 0 && string.Equals(headers.GetValueOrDefault("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await HttpResponse.ContinueAsync(stream, ct);
            }
            var target = start[1];
            var q = target.IndexOf('?');
            string path;
            try
            {
                path = Uri.UnescapeDataString(q < 0 ? target : target[..q]);
            }
            catch (UriFormatException)
            {
                throw new HttpProblem(400, "The path is not well formed.");
            }
            return new HttpRequest
            {
                Method = start[0].ToUpperInvariant(),
                Path = path,
                Query = ParseQuery(q < 0 ? "" : target[(q + 1)..]),
                Headers = headers,
                Body = await BodyAsync(length, ct),
            };
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair[..eq];
                var value = eq < 0 ? "" : pair[(eq + 1)..];
                try
                {
                    result.TryAdd(Uri.UnescapeDataString(key.Replace('+', ' ')), Uri.UnescapeDataString(value.Replace('+', ' ')));
                }
                catch (UriFormatException)
                {
                    // A parameter that is not well formed is left out.
                }
            }
            return result;
        }

        private async Task<string?> HeadAsync(CancellationToken ct)
        {
            var scanned = 0;
            while (true)
            {
                var end = _buffer.AsSpan(0, _length).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    var head = Encoding.Latin1.GetString(_buffer, 0, end);
                    Consume(end + 4);
                    if (head.Length == 0)
                    {
                        continue; // an empty line between requests
                    }
                    return head;
                }
                scanned = _length;
                if (_length == _buffer.Length)
                {
                    if (_buffer.Length >= MaxHead)
                    {
                        throw new HttpProblem(431, "The request's headers are too large.");
                    }
                    Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, MaxHead));
                }
                var n = await stream.ReadAsync(_buffer.AsMemory(_length), ct);
                if (n == 0)
                {
                    if (scanned == 0)
                    {
                        return null;
                    }
                    throw new IOException("The connection closed in the middle of a request.");
                }
                _length += n;
            }
        }

        private async Task<byte[]> BodyAsync(int length, CancellationToken ct)
        {
            var body = new byte[length];
            var have = Math.Min(length, _length);
            Array.Copy(_buffer, body, have);
            Consume(have);
            while (have < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(have), ct);
                if (n == 0)
                {
                    throw new IOException("The connection closed in the middle of a request.");
                }
                have += n;
            }
            return body;
        }

        private void Consume(int count)
        {
            Array.Copy(_buffer, count, _buffer, 0, _length - count);
            _length -= count;
        }
    }
}
