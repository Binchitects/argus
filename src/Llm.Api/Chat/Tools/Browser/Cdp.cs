using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Api.Chat.Tools.Browser;

/// <summary>What the browser answered when a command failed (or it could not be reached).</summary>
public sealed class CdpException(string message) : Exception(message);

/// <summary>
/// The Chrome DevTools protocol over one WebSocket to the browser: a command and its answer by id, each chat's page through
/// its own session (flattened), events left aside. Commands time out rather than wait forever on a browser that hangs.
/// </summary>
public sealed class Cdp : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private readonly ClientWebSocket _socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _receiving;
    private int _next;

    private Cdp(ClientWebSocket socket)
    {
        _socket = socket;
        _receiving = Task.Run(ReceiveAsync);
    }

    public bool Alive => _socket.State == WebSocketState.Open && !_receiving.IsCompleted;

    /// <summary>Connects to the browser at its DevTools address (http://browser:9222): its WebSocket, at that same host.</summary>
    public static async Task<Cdp> ConnectAsync(Uri devtools, HttpClient http, CancellationToken ct)
    {
        string version;
        try
        {
            version = await http.GetStringAsync(new Uri(devtools, "/json/version"), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CdpException($"The browser could not be reached ({ex.Message}).");
        }
        var said = JsonNode.Parse(version)?["webSocketDebuggerUrl"]?.GetValue<string>() ?? throw new CdpException("The browser did not say where its DevTools are.");
        // It names itself as it sees itself (localhost): the address the app reaches it at instead.
        var address = new UriBuilder(said) { Host = devtools.Host, Port = devtools.Port, Scheme = devtools.Scheme == "https" ? "wss" : "ws" }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(address, ct);
        return new Cdp(socket);
    }

    /// <summary>A command (to a page's session, or the browser's own), and its result.</summary>
    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters = null, string? session = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
        if (session is not null)
        {
            message["sessionId"] = session;
        }
        try
        {
            await _sending.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sending.Release();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout);
            return await answer.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CdpException($"The browser did not answer {method} in time.");
        }
        catch (WebSocketException ex)
        {
            throw new CdpException($"The browser went away ({ex.Message}).");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[1 << 16];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var got = await _socket.ReceiveAsync(buffer, _closing.Token);
                if (got.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                message.Write(buffer, 0, got.Count);
                if (!got.EndOfMessage)
                {
                    continue;
                }
                JsonObject? o;
                try
                {
                    o = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) as JsonObject;
                }
                catch (JsonException)
                {
                    o = null;
                }
                message.SetLength(0);
                if (o?["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id) || !_pending.TryGetValue(id, out var waiting))
                {
                    continue;
                }
                if (o["error"] is JsonObject error)
                {
                    waiting.TrySetException(new CdpException(error["message"]?.GetValue<string>() ?? "The browser refused the command."));
                }
                else
                {
                    waiting.TrySetResult(o["result"] as JsonObject ?? []);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetException(new CdpException("The browser went away."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closed first: cancelling the receiving aborts the socket, and a close after that fails.
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        await _closing.CancelAsync();
        _socket.Dispose();
        _sending.Dispose();
        _closing.Dispose();
    }
}
