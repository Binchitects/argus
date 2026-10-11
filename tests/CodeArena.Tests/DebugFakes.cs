using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// A debug adapter in the test's process, listening on 127.0.0.1 (config.json's "debugAdapters": {"gdb": {"port"}}):
/// it answers the requests a page sends as gdb would, in short, and does what each test needs (dies, never ends).
/// </summary>
internal sealed class FakeAdapter : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private int _seq;

    public FakeAdapter()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every request it got, in order.</summary>
    public ConcurrentQueue<JsonObject> Received { get; } = new();

    /// <summary>Done when the last connection was closed, by either side.</summary>
    public TaskCompletionSource Closed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool SupportsTerminate { get; set; } = true;

    /// <summary>Whether terminate ends the program (exited, terminated) and disconnect ends the adapter.</summary>
    public bool Ends { get; set; } = true;

    /// <summary>The source its stack trace names.</summary>
    public string StackPath { get; set; } = "/nowhere/main.c";

    /// <summary>The sources its loadedSources names.</summary>
    public List<string> Loaded { get; } = [];

    // A "slow" request not answered until it is cancelled.
    private JsonNode? _slow;

    public IEnumerable<string> Commands => Received.Select(r => r["command"]!.GetValue<string>());

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_clients)
                {
                    _clients.Add(client);
                }
                Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = ServeAsync(client, Closed);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client, TaskCompletionSource closed)
    {
        var stream = client.GetStream();
        var reader = new FrameReader(stream, 32 * 1024 * 1024);
        using var writer = new FrameWriter(stream);
        try
        {
            while (await reader.ReadAsync(_stop.Token) is { } m)
            {
                Received.Enqueue(m);
                if (!await AnswerAsync(m, writer))
                {
                    client.Client.Shutdown(SocketShutdown.Both);
                    client.Close();
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        closed.TrySetResult();
    }

    /// <summary>Answers a request; false to hang up.</summary>
    private async Task<bool> AnswerAsync(JsonObject m, FrameWriter writer)
    {
        var args = m["arguments"] as JsonObject;
        Task Respond(JsonObject? body = null, bool success = true) => writer.WriteAsync(new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref _seq), ["type"] = "response", ["request_seq"] = m["seq"]!.DeepClone(),
            ["command"] = m["command"]!.DeepClone(), ["success"] = success, ["body"] = body ?? [],
        }, default);
        Task Event(string name, JsonObject? body = null) => writer.WriteAsync(new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref _seq), ["type"] = "event", ["event"] = name, ["body"] = body ?? [],
        }, default);
        switch (m["command"]!.GetValue<string>())
        {
            case "initialize":
                await Respond(new() { ["supportsConfigurationDoneRequest"] = true, ["supportsTerminateRequest"] = SupportsTerminate });
                await Event("initialized");
                break;
            case "configurationDone":
                await Respond();
                await Event("thread", new() { ["reason"] = "started", ["threadId"] = 1 });
                await Event("output", new() { ["category"] = "stdout", ["output"] = "hello\n" });
                await Event("stopped", new() { ["reason"] = "breakpoint", ["threadId"] = 1, ["allThreadsStopped"] = true });
                break;
            case "threads":
                await Respond(new() { ["threads"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "main" }) });
                break;
            case "stackTrace":
                await Respond(new()
                {
                    ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "main", ["line"] = 3, ["column"] = 1, ["source"] = new JsonObject { ["name"] = Path.GetFileName(StackPath), ["path"] = StackPath } }),
                    ["totalFrames"] = 1,
                });
                break;
            case "evaluate":
                await Respond(new() { ["result"] = "=" + args!["expression"]!.GetValue<string>(), ["variablesReference"] = 0 });
                break;
            case "next":
                // A quick step: the stop it led to comes before its answer.
                await Event("stopped", new() { ["reason"] = "step", ["threadId"] = 1, ["allThreadsStopped"] = true });
                await Respond();
                break;
            case "loadedSources":
                await Respond(new() { ["sources"] = new JsonArray([.. Loaded.Select(p => (JsonNode)new JsonObject { ["name"] = Path.GetFileName(p), ["path"] = p })]) });
                break;
            case "slow":
                _slow = m["seq"]!.DeepClone();
                await Event("progressStart", new() { ["progressId"] = "p1", ["title"] = "slow", ["requestId"] = _slow.DeepClone() });
                break;
            case "cancel":
                if (_slow is not null && args?["requestId"] is { } id && id.GetValue<long>() == _slow.GetValue<long>())
                {
                    await writer.WriteAsync(new JsonObject
                    {
                        ["seq"] = Interlocked.Increment(ref _seq), ["type"] = "response", ["request_seq"] = _slow.DeepClone(),
                        ["command"] = "slow", ["success"] = false, ["message"] = "cancelled",
                    }, default);
                    _slow = null;
                }
                await Respond();
                break;
            case "spam":
                await Respond();
                for (var i = 0; i < args!["count"]!.GetValue<int>(); i++)
                {
                    await Event("output", new() { ["category"] = "stdout", ["output"] = $"{i:D4} " + new string('x', args["size"]!.GetValue<int>()) + "\n" });
                }
                break;
            case "crash":
                return false;
            case "terminate":
                await Respond();
                if (Ends)
                {
                    await Event("exited", new() { ["exitCode"] = 0 });
                    await Event("terminated");
                }
                break;
            case "disconnect":
                await Respond();
                return !Ends;
            default:
                await Respond();
                break;
        }
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                c.Dispose();
            }
        }
        _stop.Dispose();
    }
}

/// <summary>A debug session's WebSocket as the page opens it: every message kept, requests numbered as the page numbers them.</summary>
internal sealed class DebugSocket : IDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly List<JsonObject> _seen = [];
    private readonly Task _reading;
    private int _seq;

    private DebugSocket(ClientWebSocket socket)
    {
        _socket = socket;
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>Done when the server closed the socket.</summary>
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<JsonObject> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    public static async Task<DebugSocket> ConnectAsync(WebRun web, string id)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + web.Token);
        socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{web.Port}");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{web.Port}/api/debug/socket?id={id}"), timeout.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new DebugSocket(socket);
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage)
                {
                    continue;
                }
                Assert.Equal(WebSocketMessageType.Text, r.MessageType);
                var json = JsonNode.Parse(message.ToArray())!.AsObject();
                message.SetLength(0);
                lock (_seen)
                {
                    _seen.Add(json);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Closed.TrySetResult();
    }

    /// <summary>A request as the page sends it; its number (the page's own).</summary>
    public async Task<int> SendAsync(string command, JsonObject? arguments = null, int? seq = null)
    {
        var n = seq ?? Interlocked.Increment(ref _seq);
        var request = new JsonObject { ["seq"] = n, ["type"] = "request", ["command"] = command };
        if (arguments is not null)
        {
            request["arguments"] = arguments;
        }
        await _socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        return n;
    }

    /// <summary>A request, and its response.</summary>
    public async Task<JsonObject> RequestAsync(string command, JsonObject? arguments = null)
    {
        var seq = await SendAsync(command, arguments);
        return await UntilAsync(m => Is(m, "response") && m["request_seq"]!.GetValue<int>() == seq, $"the response to {command} #{seq}");
    }

    public Task<JsonObject> EventAsync(string name, Func<JsonObject, bool>? also = null) =>
        UntilAsync(m => Is(m, "event") && m["event"]!.GetValue<string>() == name && (also is null || also(m)), $"a {name} event");

    public static bool Is(JsonObject m, string type) => m["type"]?.GetValue<string>() == type;

    /// <summary>The first message seen that matches, waiting for it up to 30 s.</summary>
    public async Task<JsonObject> UntilAsync(Func<JsonObject, bool> match, string what)
    {
        for (var waited = 0; waited < 30_000; waited += 20)
        {
            if (Seen.FirstOrDefault(match) is { } m)
            {
                return m;
            }
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"No {what} within 30 s; seen: {string.Join(" | ", Seen.Select(m => Fmt.OneLine(m.ToJsonString(), 160)))}");
    }

    public void Dispose()
    {
        _socket.Abort();
        _socket.Dispose();
        Task.WhenAny(_reading, Task.Delay(TimeSpan.FromSeconds(5))).Wait();
    }
}
