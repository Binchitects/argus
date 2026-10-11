using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodeArena;

/// <summary>A message on a page's WebSocket: text (JSON, as the page reads it) or bytes.</summary>
internal readonly record struct SocketMessage(byte[] Data, bool Text)
{
    /// <summary>A JSON value as one text message.</summary>
    public static SocketMessage Of(JsonNode json) => new(Encoding.UTF8.GetBytes(Json.Line(json)), true);
}

/// <summary>
/// A page's WebSocket (a terminal's, a debug session's, a language server's), served by two loops: one alone sends
/// (what the caller kept for a page that attaches, then each message as it comes, and a close once there are no more),
/// the other reads the page's messages, each whole and no larger than the caller's cap, and hands them over. Either
/// side going away ends both; a page that asked to close is answered.
/// </summary>
internal static class SocketRelay
{
    /// <summary>
    /// Runs the socket until the page goes, the server stops, or <paramref name="outgoing"/> is complete (then the
    /// socket closes, saying <paramref name="endReason"/>). A message from the page past <paramref name="maxMessage"/>
    /// bytes ends it.
    /// </summary>
    public static async Task RunAsync<T>(
        WebSocket socket,
        IEnumerable<SocketMessage> first,
        ChannelReader<T> outgoing,
        Func<T, SocketMessage?> encode,
        Func<SocketMessage, CancellationToken, ValueTask> received,
        int maxMessage,
        string endReason,
        CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = SendAsync(socket, first, outgoing, encode, endReason, stop.Token);
        try
        {
            await ReceiveAsync(socket, received, maxMessage, stop.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // The page went away, or the server is stopping.
        }
        finally
        {
            await stop.CancelAsync();
            await sending;
        }
        if (socket.State == WebSocketState.CloseReceived)
        {
            try
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, bye.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
            {
            }
        }
    }

    /// <summary>The only one sending on the socket: what was kept, each message as it comes, and at the end the close.</summary>
    private static async Task SendAsync<T>(WebSocket socket, IEnumerable<SocketMessage> first, ChannelReader<T> outgoing, Func<T, SocketMessage?> encode, string endReason, CancellationToken ct)
    {
        try
        {
            foreach (var message in first)
            {
                await SendAsync(socket, message, ct);
            }
            await foreach (var item in outgoing.ReadAllAsync(ct))
            {
                if (encode(item) is { } message)
                {
                    await SendAsync(socket, message, ct);
                }
            }
            // No more to send (the channel completes only then).
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, endReason, ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The page went away first.
        }
    }

    private static ValueTask SendAsync(WebSocket socket, SocketMessage message, CancellationToken ct) =>
        socket.SendAsync(message.Data.AsMemory(), message.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);

    private static async Task ReceiveAsync(WebSocket socket, Func<SocketMessage, CancellationToken, ValueTask> received, int maxMessage, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var r = await socket.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            if (message.Length + r.Count > maxMessage)
            {
                return;
            }
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage)
            {
                continue;
            }
            var data = message.ToArray();
            message.SetLength(0);
            await received(new SocketMessage(data, r.MessageType == WebSocketMessageType.Text), ct);
        }
    }
}
