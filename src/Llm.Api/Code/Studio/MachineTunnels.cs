using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Llm.Api.Code.Studio;

/// <summary>
/// The tunnels of people's own machines (code-arena web --connect), held by the replica each one dialled: Code Studio's
/// IDE reaches the folder that Code Arena opened there through it, as it reaches a workspace's engine on the server.
/// Each request is a frame of JSON over the tunnel's WebSocket, and its answer frames back as it comes (an agent's turn
/// streams); the IDE's own WebSockets (terminals, language servers, the debugger) travel the same way, message by
/// message. The machine answers only what its own code-arena web would, with its own key, which never leaves it.
/// <code>
/// to the machine:   {"t":"req","id":1,"m":"GET","p":"/api/state","h":{...},"b":"base64"}   {"t":"cancel","id":1}
///                   {"t":"ws","id":2,"p":"/api/terminals/socket?id=t1"}   {"t":"wsd","id":2,"x":"text"|"b":"base64"}   {"t":"wsc","id":2}
/// from the machine: {"t":"res","id":1,"s":200,"h":{...}}   {"t":"data","id":1,"b":"base64"}   {"t":"end","id":1}   {"t":"err","id":1,"e":"why"}
///                   {"t":"wsok","id":2}   {"t":"wsd",...}   {"t":"wsc","id":2}
/// </code>
/// </summary>
public sealed partial class MachineTunnels(ILogger<MachineTunnels> logger)
{
    /// <summary>The most of a request's body one frame carries: the IDE's saves and uploads.</summary>
    public const int MostBody = 32 * 1024 * 1024;

    /// <summary>How long the machine has to start answering a request.</summary>
    public static readonly TimeSpan FirstFrame = TimeSpan.FromMinutes(2);

    /// <summary>The request headers the machine gets, and the answer's the page gets back.</summary>
    private static readonly string[] Passed = ["Accept", "Content-Type", "Range", "If-None-Match", "If-Modified-Since", "Last-Event-ID"];
    private static readonly string[] Back = ["Content-Type", "Content-Disposition", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified"];

    private readonly ConcurrentDictionary<Guid, Tunnel> _open = new();

    /// <summary>Whether this replica holds the machine's tunnel now.</summary>
    public bool Holds(Guid workspaceId) => _open.ContainsKey(workspaceId);

    /// <summary>The tunnel run until either side closes it; one held for the same workspace before is closed (the machine dialled again).</summary>
    public async Task RunAsync(Guid workspaceId, WebSocket socket, CancellationToken ct)
    {
        using var tunnel = new Tunnel(socket);
        if (_open.TryGetValue(workspaceId, out var before))
        {
            await before.CloseAsync("Your machine connected again.");
        }
        _open[workspaceId] = tunnel;
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var frame = await ReceiveAsync(socket, ct);
                if (frame is null)
                {
                    break;
                }
                if (frame["id"] is JsonValue v && v.TryGetValue<long>(out var id) && tunnel.Pending.TryGetValue(id, out var waiting))
                {
                    waiting.Writer.TryWrite(frame);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or OperationCanceledException)
        {
            LogLost(logger, workspaceId, ex.Message);
        }
        finally
        {
            _open.TryRemove(new KeyValuePair<Guid, Tunnel>(workspaceId, tunnel));
            tunnel.Fail();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    /// <summary>The machine's tunnel closed from this side (the workspace deleted).</summary>
    public async Task CloseAsync(Guid workspaceId, string why)
    {
        if (_open.TryRemove(workspaceId, out var tunnel))
        {
            await tunnel.CloseAsync(why);
        }
    }

    /// <summary>
    /// One of the IDE's requests through the machine's tunnel (<paramref name="path"/>: its path and query there), its answer
    /// written back as it comes; false when this replica holds no tunnel for it.
    /// </summary>
    public async Task<bool> ForwardAsync(Guid workspaceId, HttpContext http, string path, CancellationToken ct)
    {
        if (!_open.TryGetValue(workspaceId, out var tunnel))
        {
            return false;
        }
        byte[]? body = null;
        if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            using var copy = new MemoryStream();
            await http.Request.Body.CopyToAsync(copy, ct);
            if (copy.Length > MostBody)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return true;
            }
            body = copy.ToArray();
        }
        var headers = new JsonObject();
        foreach (var name in Passed)
        {
            if (http.Request.Headers.TryGetValue(name, out var value))
            {
                headers[name] = value.ToString();
            }
        }
        var (id, frames) = tunnel.Open();
        try
        {
            await tunnel.SendAsync(new JsonObject
            {
                ["t"] = "req", ["id"] = id, ["m"] = http.Request.Method, ["p"] = path, ["h"] = headers, ["b"] = body is null ? null : Convert.ToBase64String(body),
            }, ct);
            using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
            first.CancelAfter(FirstFrame);
            var answer = await frames.Reader.ReadAsync(first.Token);
            if (Kind(answer) != "res")
            {
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                await http.Response.WriteAsJsonAsync(new { status = "machine", error = Said(answer) }, ct);
                return true;
            }
            http.Response.StatusCode = answer["s"]?.GetValue<int>() ?? 502;
            foreach (var name in Back)
            {
                if (answer["h"]?[name] is JsonValue h)
                {
                    http.Response.Headers[name] = h.ToString();
                }
            }
            http.Response.Headers.CacheControl = "no-store";
            var events = http.Response.Headers.ContentType.ToString().StartsWith("text/event-stream", StringComparison.Ordinal);
            if (events)
            {
                http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            }
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
            {
                switch (Kind(frame))
                {
                    case "data":
                        await http.Response.Body.WriteAsync(Convert.FromBase64String(frame["b"]!.GetValue<string>()), ct);
                        if (events)
                        {
                            await http.Response.Body.FlushAsync(ct);
                        }
                        break;
                    case "end" or "err":
                        return true;
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            http.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return true;
        }
        catch (OperationCanceledException)
        {
            // The page went: the machine stops what it does for it.
            await tunnel.TrySendAsync(new JsonObject { ["t"] = "cancel", ["id"] = id });
            throw;
        }
        catch (ChannelClosedException)
        {
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsJsonAsync(new { status = "offline", error = "Your machine disconnected." }, CancellationToken.None);
            }
            return true;
        }
        finally
        {
            tunnel.Close(id);
        }
    }

    /// <summary>One of the IDE's WebSockets joined to the machine's, message by message; false when this replica holds no tunnel for it.</summary>
    public async Task<bool> RelayAsync(Guid workspaceId, HttpContext http, string path, CancellationToken ct)
    {
        if (!_open.TryGetValue(workspaceId, out var tunnel))
        {
            return false;
        }
        var (id, frames) = tunnel.Open();
        try
        {
            await tunnel.SendAsync(new JsonObject { ["t"] = "ws", ["id"] = id, ["p"] = path }, ct);
            using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
            first.CancelAfter(FirstFrame);
            var answer = await frames.Reader.ReadAsync(first.Token);
            if (Kind(answer) != "wsok")
            {
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                return true;
            }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var fromPage = Task.Run(async () =>
            {
                var buffer = new byte[32 * 1024];
                using var message = new MemoryStream();
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var got = await socket.ReceiveAsync(buffer, ct);
                        if (got.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                        message.Write(buffer, 0, got.Count);
                        if (!got.EndOfMessage)
                        {
                            continue;
                        }
                        var data = message.ToArray();
                        message.SetLength(0);
                        await tunnel.SendAsync(got.MessageType == WebSocketMessageType.Text
                            ? new JsonObject { ["t"] = "wsd", ["id"] = id, ["x"] = Encoding.UTF8.GetString(data) }
                            : new JsonObject { ["t"] = "wsd", ["id"] = id, ["b"] = Convert.ToBase64String(data) }, ct);
                    }
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ChannelClosedException)
                {
                }
                // Closed by the page: the machine's side closed too, and nothing more is read from it.
                await tunnel.TrySendAsync(new JsonObject { ["t"] = "wsc", ["id"] = id });
                tunnel.Close(id);
            }, CancellationToken.None);
            try
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                {
                    if (Kind(frame) == "wsc" || socket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    if (Kind(frame) == "wsd")
                    {
                        var (bytes, type) = frame["x"] is JsonValue x
                            ? (Encoding.UTF8.GetBytes(x.GetValue<string>()), WebSocketMessageType.Text)
                            : (Convert.FromBase64String(frame["b"]!.GetValue<string>()), WebSocketMessageType.Binary);
                        await socket.SendAsync(bytes, type, true, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ChannelClosedException)
            {
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
            await fromPage;
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            http.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return true;
        }
        catch (ChannelClosedException)
        {
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
            }
            return true;
        }
        finally
        {
            tunnel.Close(id);
        }
    }

    private static string? Kind(JsonObject frame) => frame["t"]?.GetValue<string>();

    private static string Said(JsonObject frame) => frame["e"]?.GetValue<string>() is { Length: > 0 } e ? e : "Your machine could not answer.";

    /// <summary>One whole text message from the tunnel, as JSON; null when it closed.</summary>
    private static async Task<JsonObject?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var got = await socket.ReceiveAsync(buffer, ct);
            if (got.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            message.Write(buffer, 0, got.Count);
            if (message.Length > MostBody * 2)
            {
                throw new JsonException("A frame from the machine is too large.");
            }
            if (got.EndOfMessage)
            {
                return JsonNode.Parse(message.ToArray()) as JsonObject ?? throw new JsonException("A frame from the machine is not an object.");
            }
        }
    }

    /// <summary>A machine's tunnel: its WebSocket (one writer at a time), and the requests waiting for its frames, by id.</summary>
    private sealed class Tunnel(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sending = new(1, 1);
        private long _next;

        public ConcurrentDictionary<long, Channel<JsonObject>> Pending { get; } = new();

        public (long Id, Channel<JsonObject> Frames) Open()
        {
            var id = Interlocked.Increment(ref _next);
            var frames = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
            Pending[id] = frames;
            return (id, frames);
        }

        public void Close(long id)
        {
            if (Pending.TryRemove(id, out var frames))
            {
                frames.Writer.TryComplete();
            }
        }

        public async Task SendAsync(JsonObject frame, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
            try
            {
                await _sending.WaitAsync(ct);
            }
            catch (ObjectDisposedException ex)
            {
                throw new ChannelClosedException("The machine's tunnel is closed.", ex);
            }
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    throw new ChannelClosedException("The machine's tunnel is closed.");
                }
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            catch (WebSocketException ex)
            {
                throw new ChannelClosedException("The machine's tunnel is closed.", ex);
            }
            finally
            {
                _sending.Release();
            }
        }

        public async Task TrySendAsync(JsonObject frame)
        {
            try
            {
                await SendAsync(frame, CancellationToken.None);
            }
            catch (ChannelClosedException)
            {
            }
        }

        /// <summary>Every request waiting told the machine is gone.</summary>
        public void Fail()
        {
            foreach (var (_, frames) in Pending)
            {
                frames.Writer.TryComplete(new ChannelClosedException("Your machine disconnected."));
            }
        }

        public void Dispose() => _sending.Dispose();

        public async Task CloseAsync(string why)
        {
            Fail();
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, why, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A machine's tunnel for workspace {Id} ended: {Why}")]
    private static partial void LogLost(ILogger logger, Guid id, string why);
}
