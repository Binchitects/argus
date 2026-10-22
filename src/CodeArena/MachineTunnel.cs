using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// This folder in Arena's Code Studio (code-arena web --connect): a WebSocket dialled to Arena with the person's API key,
/// over which Code Studio's IDE reaches this run's own web server, as the page on this machine would, with this run's
/// key (which never leaves the machine). Each request comes as a frame of JSON and its answer goes back as it comes (an
/// agent's turn streams); the IDE's WebSockets (terminals, language servers, the debugger) travel the same way, message
/// by message (the frames: Arena's MachineTunnels). Only what this run's web server answers is reached, only by the
/// person signed in to Arena as the key's owner, and only while this runs. Dialled again after a lost connection.
/// </summary>
internal sealed class MachineTunnel(Uri arena, string apiKey, int port, string token, string name, string? project, string? branch, string id, Action<string> say)
{
    /// <summary>The answer's headers sent back to Code Studio (the rest stay here).</summary>
    private static readonly string[] Back = ["Content-Type", "Content-Disposition", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified"];

    /// <summary>The longest wait before dialling again (doubling from a second).</summary>
    public static readonly TimeSpan MostWait = TimeSpan.FromSeconds(30);

    private readonly HttpClient _local = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _requests = new();
    private readonly ConcurrentDictionary<long, ClientWebSocket> _sockets = new();

    /// <summary>The address Code Arena dials: Arena's connect route, with this folder's id, name, project and branch.</summary>
    public Uri ConnectUrl()
    {
        var query = $"id={Uri.EscapeDataString(id)}&name={Uri.EscapeDataString(name)}"
            + (project is { Length: > 0 } p ? $"&project={Uri.EscapeDataString(p)}" : "")
            + (branch is { Length: > 0 } b ? $"&branch={Uri.EscapeDataString(b)}" : "");
        return new UriBuilder(arena) { Scheme = arena.Scheme == "https" ? "wss" : "ws", Path = arena.AbsolutePath.TrimEnd('/') + "/api/code-arena/connect", Query = query }.Uri;
    }

    /// <summary>Connected, and dialled again after a lost connection, until stopped or refused (the key, the permission).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                await socket.ConnectAsync(ConnectUrl(), ct);
            }
            catch (WebSocketException) when (socket.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.BadRequest)
            {
                say($"Arena refused to connect this folder to Code Studio (HTTP {(int)socket.HttpStatusCode}): {(socket.HttpStatusCode == System.Net.HttpStatusCode.Forbidden ? "Code Studio is not among what you may use" : socket.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized ? "your API key is not known there: code-arena login" : "see Code Studio")}.");
                return;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException && !ct.IsCancellationRequested)
            {
                say($"Code Studio is out of reach ({e.Message}); trying again in {wait.TotalSeconds:0} s.");
                await Delay(wait, ct);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MostWait.Ticks));
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            wait = TimeSpan.FromSeconds(1);
            say($"Connected to Code Studio: open it at {new Uri(arena, "code")}, this folder is \"{name}\" there.");
            await ServeAsync(socket, ct);
            foreach (var (_, request) in _requests)
            {
                request.Cancel();
            }
            foreach (var (_, local) in _sockets)
            {
                local.Abort();
            }
            if (!ct.IsCancellationRequested)
            {
                say(socket.CloseStatusDescription is { Length: > 0 } why ? $"Code Studio closed the connection: {why}" : "The connection to Code Studio was lost; connecting again.");
                if (socket.CloseStatusDescription?.StartsWith("Deleted", StringComparison.Ordinal) == true)
                {
                    return;
                }
                await Delay(wait, ct);
            }
        }
    }

    private static async Task Delay(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Frames from Arena until the tunnel closes: each request and WebSocket handled on its own.</summary>
    private async Task ServeAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
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
                var frame = JsonNode.Parse(message.ToArray()) as JsonObject;
                message.SetLength(0);
                if (frame?["id"] is not JsonValue v || !v.TryGetValue<long>(out var id))
                {
                    continue;
                }
                switch (frame.Str("t"))
                {
                    case "req":
                        _ = Task.Run(() => RequestAsync(socket, id, frame, ct), CancellationToken.None);
                        break;
                    case "cancel":
                        if (_requests.TryRemove(id, out var request))
                        {
                            request.Cancel();
                        }
                        break;
                    case "ws":
                        _ = Task.Run(() => OpenSocketAsync(socket, id, frame.Str("p") ?? "/", ct), CancellationToken.None);
                        break;
                    case "wsd" when _sockets.TryGetValue(id, out var local):
                        var (bytes, type) = frame["x"] is JsonValue x
                            ? (Encoding.UTF8.GetBytes(x.GetValue<string>()), WebSocketMessageType.Text)
                            : (Convert.FromBase64String(frame.Str("b") ?? ""), WebSocketMessageType.Binary);
                        try
                        {
                            await local.SendAsync(bytes, type, true, ct);
                        }
                        catch (WebSocketException)
                        {
                        }
                        break;
                    case "wsc" when _sockets.TryRemove(id, out var closing):
                        try
                        {
                            await closing.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                        }
                        catch (WebSocketException)
                        {
                        }
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or JsonException or OperationCanceledException)
        {
        }
    }

    /// <summary>One of the IDE's requests, made to this run's own web server with its key; the answer sent back as it comes.</summary>
    private async Task RequestAsync(ClientWebSocket socket, long id, JsonObject frame, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _requests[id] = cancel;
        try
        {
            var path = frame.Str("p") ?? "/";
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                await SendAsync(socket, new JsonObject { ["t"] = "err", ["id"] = id, ["e"] = "Only the IDE's calls go through." }, ct);
                return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(frame.Str("m") ?? "GET"), new Uri($"http://127.0.0.1:{port}{path}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (frame.Str("b") is { Length: > 0 } body)
            {
                request.Content = new ByteArrayContent(Convert.FromBase64String(body));
            }
            foreach (var (name, value) in frame["h"] as JsonObject ?? [])
            {
                if (value is JsonValue v && !request.Headers.TryAddWithoutValidation(name, v.ToString()))
                {
                    request.Content?.Headers.TryAddWithoutValidation(name, v.ToString());
                }
            }
            using var response = await _local.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            var headers = new JsonObject();
            foreach (var name in Back)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                {
                    headers[name] = string.Join(", ", values);
                }
            }
            await SendAsync(socket, new JsonObject { ["t"] = "res", ["id"] = id, ["s"] = (int)response.StatusCode, ["h"] = headers }, cancel.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
            var buffer = new byte[48 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancel.Token)) > 0)
            {
                await SendAsync(socket, new JsonObject { ["t"] = "data", ["id"] = id, ["b"] = Convert.ToBase64String(buffer, 0, read) }, cancel.Token);
            }
            await SendAsync(socket, new JsonObject { ["t"] = "end", ["id"] = id }, cancel.Token);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or WebSocketException or FormatException)
        {
            await TrySendAsync(socket, new JsonObject { ["t"] = "err", ["id"] = id, ["e"] = e is OperationCanceledException ? "Stopped." : $"Code Arena could not answer: {e.Message}" });
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    /// <summary>One of the IDE's WebSockets, opened on this run's own web server with its key, joined message by message.</summary>
    private async Task OpenSocketAsync(ClientWebSocket socket, long id, string path, CancellationToken ct)
    {
        var local = new ClientWebSocket();
        local.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        try
        {
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                throw new WebSocketException("Only the IDE's sockets go through.");
            }
            await local.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{path}"), ct);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException)
        {
            local.Dispose();
            await TrySendAsync(socket, new JsonObject { ["t"] = "err", ["id"] = id, ["e"] = e.Message });
            return;
        }
        _sockets[id] = local;
        await TrySendAsync(socket, new JsonObject { ["t"] = "wsok", ["id"] = id });
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && local.State == WebSocketState.Open)
            {
                var got = await local.ReceiveAsync(buffer, ct);
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
                await SendAsync(socket, got.MessageType == WebSocketMessageType.Text
                    ? new JsonObject { ["t"] = "wsd", ["id"] = id, ["x"] = Encoding.UTF8.GetString(data) }
                    : new JsonObject { ["t"] = "wsd", ["id"] = id, ["b"] = Convert.ToBase64String(data) }, ct);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            if (_sockets.TryRemove(id, out _))
            {
                await TrySendAsync(socket, new JsonObject { ["t"] = "wsc", ["id"] = id });
            }
            local.Dispose();
        }
    }

    private async Task SendAsync(ClientWebSocket socket, JsonObject frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        await _sending.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task TrySendAsync(ClientWebSocket socket, JsonObject frame)
    {
        try
        {
            await SendAsync(socket, frame, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
