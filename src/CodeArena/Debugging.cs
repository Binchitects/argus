using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodeArena;

/// <summary>A debug adapter needed for a configuration and not found here: which one, and why.</summary>
internal sealed class DebugAdapterMissing(string adapter, string reason) : Exception(reason)
{
    public string Adapter { get; } = adapter;
}

/// <summary>
/// A page attached to a debug session: what the session sends it, in order. A page that falls too far behind (in
/// messages or in bytes) is let go, and its socket closes: what waits for it is held in this program's memory.
/// </summary>
internal sealed class DebugPage(long maxBytes = DebugPage.MaxBytes)
{
    /// <summary>The messages that may wait for a page.</summary>
    public const int Waiting = 10_000;

    /// <summary>The bytes that may wait for a page: twice the largest message an adapter sends.</summary>
    public const long MaxBytes = 2L * DebugSession.MaxAdapterMessage;

    private long _bytes;

    public Channel<SocketMessage> Out { get; } = Channel.CreateBounded<SocketMessage>(new BoundedChannelOptions(Waiting) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>Queues a message for the page; false when it has fallen too far behind for it.</summary>
    public bool TryWrite(SocketMessage message)
    {
        if (Interlocked.Add(ref _bytes, message.Data.Length) <= maxBytes && Out.Writer.TryWrite(message))
        {
            return true;
        }
        Interlocked.Add(ref _bytes, -message.Data.Length);
        return false;
    }

    /// <summary>A message taken off the queue to be sent: it waits no more.</summary>
    public SocketMessage Taken(SocketMessage message)
    {
        Interlocked.Add(ref _bytes, -message.Data.Length);
        return message;
    }
}

/// <summary>
/// One debug session: a debug adapter (its process, or a connection to one) relayed to any number of pages. The page
/// speaks the Debug Adapter Protocol itself (initialize, launch, breakpoints, configurationDone); the session numbers
/// every message to the adapter itself, in the order written, so each response goes back only to the page that asked,
/// with that page's own number restored (and so does the request a cancel or a progressStart names), and each event
/// goes to every page. It keeps what a page that attaches later needs (the last stop, the threads, the last 256 KB of
/// output), says where it is with its own codeArena.state events (starting, running, stopped, ended), and when the
/// adapter ends without saying so, says it for it (exited, terminated). It records the source files the adapter named,
/// the only files outside the folder a page may read through it.
/// </summary>
internal sealed class DebugSession : IAsyncDisposable
{
    /// <summary>The most an adapter sends in one message.</summary>
    public const int MaxAdapterMessage = 32 * 1024 * 1024;

    /// <summary>The output kept for a page that attaches later.</summary>
    public const int OutputKept = 256 * 1024;

    /// <summary>How long a stop waits for the adapter to end by itself before its processes are killed.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(3);

    private const int MostNamed = 10_000;
    private static readonly StringComparer PathCompare = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Responses that move the program on: it runs once they succeed (an adapter need not say "continued").
    private static readonly HashSet<string> Resumes = new(StringComparer.Ordinal) { "continue", "next", "stepIn", "stepOut", "stepBack", "reverseContinue", "goto" };

    // Responses with no source in them worth walking.
    private static readonly HashSet<string> Sourceless = new(StringComparer.Ordinal) { "variables", "evaluate", "readMemory", "disassemble", "completions" };

    private readonly StdioChild _adapter;
    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;
    private readonly object _gate = new();
    private readonly List<DebugPage> _pages = [];
    private readonly Dictionary<long, Asked> _asked = [];
    private readonly HashSet<long> _reverse = [];
    private readonly Queue<SocketMessage> _output = new();
    private readonly Dictionary<long, JsonObject> _threads = [];
    private readonly HashSet<string> _named = new(PathCompare);
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;
    private long _stops;
    private int _outputBytes;
    private JsonObject? _capabilities;
    private JsonObject? _stopped;
    private long? _exitCode;
    private bool _sawExited;
    private bool _sawTerminated;
    private bool _stopping;
    private bool _over;
    private string? _error;

    /// <summary>
    /// Who asked the adapter something: a page (with its own number for it), or the session itself; and how many times
    /// the program had stopped then (a step answered after the stop it led to does not mean running).
    /// </summary>
    private sealed record Asked(DebugPage? Page, long PageSeq, string Command, TaskCompletionSource<JsonObject?>? Own, long Stops);

    public DebugSession(string id, DebugLaunch launch, StdioChild adapter)
    {
        Id = id;
        Launch = launch;
        _adapter = adapter;
        _reader = new FrameReader(adapter.Reading, MaxAdapterMessage);
        _writer = new FrameWriter(adapter.Writing);
        _ = Task.Run(ReadAsync);
    }

    public string Id { get; }

    /// <summary>The configuration as the adapter takes it, and which adapter.</summary>
    public DebugLaunch Launch { get; }

    /// <summary>starting, running, stopped or ended.</summary>
    public string State { get; private set; } = "starting";

    /// <summary>Done once the adapter has ended (or was let go) and every page was told.</summary>
    public Task Ended => _ended.Task;

    public JsonObject Json() => new()
    {
        ["id"] = Id,
        ["name"] = Launch.Configuration.Str("name"),
        ["adapter"] = Launch.AdapterId,
        ["type"] = Launch.Type,
        ["state"] = State,
    };

    /// <summary>Whether the adapter named this file (a full path) as a source: in a stack, a breakpoint, a loaded source.</summary>
    public bool Named(string full)
    {
        lock (_gate)
        {
            return _named.Contains(full);
        }
    }

    /// <summary>A page attaching: what it is sent first (the snapshot, then the output kept), and where the rest comes.</summary>
    public (IReadOnlyList<SocketMessage> First, DebugPage Page) Attach()
    {
        var page = new DebugPage();
        lock (_gate)
        {
            List<SocketMessage> first = [SocketMessage.Of(Snapshot()), .. _output];
            if (_over)
            {
                page.Out.Writer.TryComplete();
            }
            else
            {
                _pages.Add(page);
            }
            return (first, page);
        }
    }

    public void Detach(DebugPage page)
    {
        lock (_gate)
        {
            _pages.Remove(page);
        }
    }

    /// <summary>What a page sent: a request for the adapter, or its answer to one of the adapter's own requests. Anything else is passed over.</summary>
    public async ValueTask FromPageAsync(DebugPage page, SocketMessage message, CancellationToken ct)
    {
        if (!message.Text || CodeArena.Json.ParseObject(Encoding.UTF8.GetString(FrameReader.Repair(message.Data))) is not { } m || m.Str("type") is not ("request" or "response"))
        {
            return;
        }
        await WriteAsync(() =>
        {
            lock (_gate)
            {
                return Numbered(page, m);
            }
        }, ct);
    }

    /// <summary>A page's message with the session's number for it, as the adapter takes it; null when it is not for the adapter.</summary>
    private JsonObject? Numbered(DebugPage page, JsonObject m)
    {
        if (m.Str("type") == "response")
        {
            if (!_reverse.Remove(m.Long("request_seq") ?? -1))
            {
                return null;
            }
            m["seq"] = ++_seq;
            return m;
        }
        if (_over)
        {
            Send(page, SocketMessage.Of(Failed(m.Long("seq") ?? 0, m.Str("command"), "The debug session has ended.")));
            return null;
        }
        var command = m.Str("command") ?? "";
        if (command == "cancel" && m["arguments"] is JsonObject arguments && arguments["requestId"] is not null)
        {
            // The request given up on, by the number the adapter knows it by; one of another page's is not this page's to cancel.
            var theirs = arguments.Long("requestId");
            if (_asked.FirstOrDefault(a => a.Value.Page == page && a.Value.PageSeq == theirs) is { Value: not null } asked)
            {
                arguments["requestId"] = asked.Key;
            }
            else
            {
                arguments.Remove("requestId");
            }
        }
        var seq = ++_seq;
        _asked[seq] = new Asked(page, m.Long("seq") ?? 0, command, null, _stops);
        m["seq"] = seq;
        return m;
    }

    /// <summary>
    /// Ends the session: terminate (when the adapter has it), else disconnect with the program; whatever has not ended
    /// 3 s later is killed, the adapter and every process it started. A program attached to is not the session's: it
    /// is let go, not ended (as VS Code's Disconnect does).
    /// </summary>
    public async Task StopAsync()
    {
        var launched = Launch.Configuration.Str("request") != "attach";
        bool terminate;
        lock (_gate)
        {
            if (_over)
            {
                return;
            }
            _stopping = true;
            terminate = launched && _capabilities.Bool("supportsTerminateRequest") == true && !_sawTerminated;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (terminate)
        {
            // Half the time for the program to end; the rest for the adapter, told to disconnect.
            _ = AskAsync("terminate", []);
            await Task.WhenAny(_terminated.Task, _ended.Task, Task.Delay(StopWait / 2));
        }
        if (!_ended.Task.IsCompleted)
        {
            _ = AskAsync("disconnect", new JsonObject { ["terminateDebuggee"] = launched });
            if (StopWait - clock.Elapsed is { Ticks: > 0 } left)
            {
                await Task.WhenAny(_ended.Task, Task.Delay(left));
            }
        }
        _adapter.Kill();
        await Task.WhenAny(_ended.Task, Task.Delay(TimeSpan.FromSeconds(2)));
    }

    /// <summary>A request of the session's own; the adapter's response, or null when it ended first.</summary>
    private async Task<JsonObject?> AskAsync(string command, JsonObject arguments)
    {
        var own = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = await WriteAsync(() =>
        {
            lock (_gate)
            {
                if (_over)
                {
                    return null;
                }
                var seq = ++_seq;
                _asked[seq] = new Asked(null, 0, command, own, _stops);
                return new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = arguments };
            }
        }, CancellationToken.None);
        return sent ? await own.Task : null;
    }

    /// <summary>
    /// Writes to the adapter what <paramref name="next"/> makes when it is this message's turn, so that the adapter
    /// gets the session's numbers in order; false when it made none, or the adapter is gone.
    /// </summary>
    private async Task<bool> WriteAsync(Func<JsonObject?> next, CancellationToken ct)
    {
        try
        {
            return await _writer.WriteAsync(next, ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The adapter is gone: its reader ends the session, and answers what was asked.
            return false;
        }
    }

    private async Task ReadAsync()
    {
        string? error = null;
        try
        {
            while (await _reader.ReadAsync(CancellationToken.None) is { } message)
            {
                if (Take(message) is { } reply)
                {
                    await WriteAsync(() =>
                    {
                        lock (_gate)
                        {
                            reply["seq"] = ++_seq;
                        }
                        return reply;
                    }, CancellationToken.None);
                }
            }
        }
        catch (FramingException e)
        {
            error = $"The debug adapter sent what is not the Debug Adapter Protocol: {e.Message}";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Its end closed.
        }
        catch (Exception e)
        {
            // Whatever it was, the pages hear that the session ended rather than wait on it.
            error = $"A message from the debug adapter could not be passed on: {e.Message}";
        }
        await EndAsync(error);
    }

    /// <summary>A message from the adapter, passed on; what to answer the adapter at once, if anything.</summary>
    private JsonObject? Take(JsonObject message)
    {
        lock (_gate)
        {
            switch (message.Str("type"))
            {
                case "response":
                    Respond(message);
                    return null;
                case "event":
                    Happen(message);
                    return null;
                case "request" when _pages.Count == 0:
                    // A reverse request (runInTerminal, startDebugging) with no page to answer it; numbered as it is written.
                    return new JsonObject
                    {
                        ["seq"] = 0, ["type"] = "response", ["request_seq"] = message.Long("seq"), ["command"] = message.Str("command"),
                        ["success"] = false, ["message"] = "No page of Code Arena's is open to answer it.",
                    };
                case "request":
                    _reverse.Add(message.Long("seq") ?? -1);
                    Broadcast(SocketMessage.Of(message));
                    return null;
                default:
                    return null;
            }
        }
    }

    private void Respond(JsonObject message)
    {
        if (!_asked.Remove(message.Long("request_seq") ?? -1, out var asked))
        {
            return;
        }
        var ok = message.Bool("success") == true;
        if (!Sourceless.Contains(asked.Command))
        {
            Name(message["body"], 0);
        }
        if (ok && asked.Command == "initialize")
        {
            _capabilities = message["body"] is JsonObject body ? body.Clone() : [];
        }
        if (asked.Own is { } own)
        {
            own.TrySetResult(message);
        }
        else
        {
            message["request_seq"] = asked.PageSeq;
            if (_pages.Contains(asked.Page!))
            {
                Send(asked.Page!, SocketMessage.Of(message));
            }
        }
        if (ok && asked.Command is "configurationDone" or "launch" or "attach" && State == "starting")
        {
            SetState("running");
        }
        else if (ok && Resumes.Contains(asked.Command) && asked.Stops == _stops && State != "ended")
        {
            _stopped = null;
            SetState("running");
        }
    }

    private void Happen(JsonObject message)
    {
        var body = message["body"] as JsonObject;
        Name(body, 0);
        if (message.Str("event") == "progressStart" && body?["requestId"] is not null)
        {
            Progress(message, body);
            return;
        }
        var sent = SocketMessage.Of(message);
        Broadcast(sent);
        switch (message.Str("event"))
        {
            case "output" when body.Str("category") != "telemetry":
                Keep(sent);
                break;
            case "stopped":
                _stops++;
                _stopped = body?.Clone();
                SetState("stopped");
                break;
            case "continued":
                _stopped = null;
                SetState("running");
                break;
            case "thread" when body.Long("threadId") is { } thread:
                if (body.Str("reason") == "exited")
                {
                    _threads.Remove(thread);
                }
                else
                {
                    _threads[thread] = body!.Clone();
                }
                break;
            case "exited":
                _sawExited = true;
                _exitCode = body.Long("exitCode");
                break;
            case "terminated":
                _sawTerminated = true;
                _terminated.TrySetResult();
                SetState("ended");
                break;
        }
    }

    /// <summary>
    /// A progressStart tied to a request: the page that sent it gets its own number for it; the others get the progress
    /// without one, as none of theirs is meant.
    /// </summary>
    private void Progress(JsonObject message, JsonObject body)
    {
        var asked = _asked.GetValueOrDefault(body.Long("requestId") ?? -1);
        body.Remove("requestId");
        var others = SocketMessage.Of(message);
        foreach (var page in _pages.ToList())
        {
            if (asked?.Page == page)
            {
                body["requestId"] = asked.PageSeq;
                Send(page, SocketMessage.Of(message));
                body.Remove("requestId");
            }
            else
            {
                Send(page, others);
            }
        }
    }

    /// <summary>The adapter ended (or was let go): the pages are told what it did not say, and their sockets close.</summary>
    private async Task EndAsync(string? error)
    {
        // Its exit code and its last words, when it is ending.
        await Task.WhenAny(Task.WhenAll(_adapter.Exited, _adapter.SaidAll), Task.Delay(TimeSpan.FromSeconds(1)));
        int? code = _adapter.Exited.IsCompletedSuccessfully && _adapter.Pid is not null ? _adapter.Exited.Result : null;
        lock (_gate)
        {
            if (_over)
            {
                return;
            }
            _over = true;
            foreach (var asked in _asked.Values)
            {
                if (asked.Own is { } own)
                {
                    own.TrySetResult(null);
                }
                else if (_pages.Contains(asked.Page!))
                {
                    Send(asked.Page!, SocketMessage.Of(Failed(asked.PageSeq, asked.Command, "The debug adapter ended.")));
                }
            }
            _asked.Clear();
            if (!_sawExited)
            {
                Broadcast(Event("exited", new JsonObject { ["exitCode"] = -1 }));
            }
            if (!_sawTerminated)
            {
                Broadcast(Event("terminated", []));
            }
            if (error is null && !_stopping && (!_sawTerminated || code is not (null or 0)))
            {
                var how = code is { } c ? $"The debug adapter ended (exit code {c})" : "The debug adapter ended";
                error = StdioChild.With(_sawTerminated ? how : how + " before the session did", _adapter.Said());
            }
            _error = error;
            if (State != "ended" || error is not null)
            {
                State = "ended";
                Broadcast(StateEvent());
            }
            foreach (var page in _pages)
            {
                page.Out.Writer.TryComplete();
            }
            _pages.Clear();
        }
        _terminated.TrySetResult();
        _adapter.Kill();
        _ended.TrySetResult();
    }

    private void SetState(string state)
    {
        if (State == state || State == "ended")
        {
            return;
        }
        State = state;
        Broadcast(StateEvent());
    }

    private SocketMessage StateEvent()
    {
        var body = new JsonObject { ["state"] = State };
        if (_error is not null)
        {
            body["error"] = _error;
        }
        return Event("codeArena.state", body);
    }

    /// <summary>What a page that attaches is told first: the session, where it is, and what it would have heard.</summary>
    private JsonObject Snapshot() => new()
    {
        ["seq"] = 0,
        ["type"] = "event",
        ["event"] = "codeArena.snapshot",
        ["body"] = new JsonObject
        {
            ["id"] = Id,
            ["name"] = Launch.Configuration.Str("name"),
            ["adapter"] = Launch.AdapterId,
            ["type"] = Launch.Type,
            ["state"] = State,
            ["error"] = _error,
            ["configuration"] = Launch.Configuration.Clone(),
            ["capabilities"] = _capabilities?.Clone(),
            ["stopped"] = _stopped?.Clone(),
            ["threads"] = new JsonArray([.. _threads.Values.Select(t => (JsonNode)t.Clone())]),
            ["exitCode"] = _exitCode,
        },
    };

    private static SocketMessage Event(string name, JsonObject body) =>
        SocketMessage.Of(new JsonObject { ["seq"] = 0, ["type"] = "event", ["event"] = name, ["body"] = body });

    private static JsonObject Failed(long requestSeq, string? command, string why) => new()
    {
        ["seq"] = 0, ["type"] = "response", ["request_seq"] = requestSeq, ["command"] = command, ["success"] = false, ["message"] = why,
    };

    private void Broadcast(SocketMessage message)
    {
        foreach (var page in _pages.ToList())
        {
            Send(page, message);
        }
    }

    /// <summary>To one page; one that has fallen too far behind is let go.</summary>
    private void Send(DebugPage page, SocketMessage message)
    {
        if (!page.TryWrite(message))
        {
            page.Out.Writer.TryComplete();
            _pages.Remove(page);
        }
    }

    private void Keep(SocketMessage output)
    {
        if (output.Data.Length > OutputKept)
        {
            return;
        }
        _output.Enqueue(output);
        _outputBytes += output.Data.Length;
        while (_outputBytes > OutputKept)
        {
            _outputBytes -= _output.Dequeue().Data.Length;
        }
    }

    /// <summary>Records the source files a message names: each "source" object's path, and each of "sources".</summary>
    private void Name(JsonNode? node, int depth)
    {
        if (depth > 32 || _named.Count >= MostNamed)
        {
            return;
        }
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key == "source")
                    {
                        Source(value.Str("path"));
                    }
                    else if (key == "sources" && value is JsonArray sources)
                    {
                        foreach (var source in sources)
                        {
                            Source(source.Str("path"));
                        }
                    }
                    if (value is JsonObject or JsonArray)
                    {
                        Name(value, depth + 1);
                    }
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    Name(item, depth + 1);
                }
                break;
        }
    }

    private void Source(string? path)
    {
        if (path is not null && Path.IsPathFullyQualified(path))
        {
            _named.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _adapter.DisposeAsync();
        _writer.Dispose();
    }
}

/// <summary>
/// The IDE's debug sessions: each a debug adapter started for one launch configuration (or reached at the port its
/// "debugServer" names), run in the working directory, at most 8 at once, and none until the folder is trusted.
/// Started by the person, as their terminals are: no sandbox.
/// </summary>
internal sealed class DebugSessions(Workspace workspace, DebugAdapters adapters, Func<bool> trusted) : IAsyncDisposable
{
    public const int Max = 8;

    /// <summary>How long an adapter that listens on a port has to answer there.</summary>
    public static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<DebugSession> _all = [];
    private int _next;

    public IReadOnlyList<DebugSession> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _all];
            }
        }
    }

    public DebugSession? Find(string? id)
    {
        lock (_gate)
        {
            return _all.FirstOrDefault(s => s.Id == id);
        }
    }

    /// <summary>
    /// Starts the adapter for a launch configuration (variables already filled in). Throws IdeError (400 for a
    /// configuration no adapter here takes, 409 <c>untrusted</c> in a folder not trusted, 409 <c>too_many</c>, 409
    /// <c>adapter_failed</c>) or <see cref="DebugAdapterMissing"/>.
    /// </summary>
    public async Task<DebugSession> StartAsync(JsonObject configuration, CancellationToken ct)
    {
        var trust = trusted();
        var launch = DebugAdapters.Translate(configuration, workspace.Root, trust);
        int? server = configuration["debugServer"] is not { } given ? null
            : Json.Number(given) is { } n and > 0 and < 65536 ? (int)n
            : throw new IdeError(400, "bad_config", "\"debugServer\" is the port (1 to 65535) of a debug adapter listening on 127.0.0.1.");
        if (!trust)
        {
            // As in VS Code's Restricted Mode: a debug session runs the folder's programs, as its configuration says.
            throw new IdeError(409, "untrusted", "Debugging runs this folder's programs: trust the folder first.");
        }
        TooMany();
        StdioChild adapter;
        try
        {
            adapter = server is { } port
                ? await StdioChild.ConnectAsync(port, ConnectWait, ct)
                : await OpenAsync(await AdapterAsync(launch, ct), ct);
        }
        catch (IOException e)
        {
            throw new IdeError(409, "adapter_failed", e.Message);
        }
        DebugSession? session = null;
        List<DebugSession> dropped = [];
        lock (_gate)
        {
            if (_all.Count(s => !s.Ended.IsCompleted) < Max)
            {
                // Ended sessions are kept for a page to see how they ended, the 8 newest.
                dropped = [.. _all.Where(s => s.Ended.IsCompleted).SkipLast(Max - 1)];
                _all.RemoveAll(dropped.Contains);
                session = new DebugSession((++_next).ToString(System.Globalization.CultureInfo.InvariantCulture), launch, adapter);
                _all.Add(session);
            }
        }
        await Task.WhenAll(dropped.Select(s => s.DisposeAsync().AsTask()));
        if (session is null)
        {
            await adapter.DisposeAsync();
            throw TooManyError();
        }
        return session;
    }

    private void TooMany()
    {
        lock (_gate)
        {
            if (_all.Count(s => !s.Ended.IsCompleted) >= Max)
            {
                throw TooManyError();
            }
        }
    }

    private static IdeError TooManyError() => new(409, "too_many", $"{Max} debug sessions are running: stop one first.");

    /// <summary>The adapter that runs the configuration: the program it names (miDebuggerPath), else the one found here.</summary>
    private async Task<DebugAdapter> AdapterAsync(DebugLaunch launch, CancellationToken ct)
    {
        if (launch.Program is { } own)
        {
            // A name alone is looked for on PATH, as cppdbg does (else "gdb" would be a program in the folder); a path is the folder's.
            var named = Path.IsPathRooted(own) || own.Contains('/') || own.Contains('\\');
            var program = named ? Path.GetFullPath(own, workspace.Root) : adapters.Locate(own);
            return program is not null && File.Exists(program)
                ? new DebugAdapter(DebugAdapters.Kind("gdb")!, program, ["-q", "-i", "dap"], new Dictionary<string, string?>(), null, program, null)
                : throw new DebugAdapterMissing(launch.AdapterId, $"{own} (the configuration's miDebuggerPath) is not {(named ? "there" : "on PATH")}.");
        }
        var (found, reason) = await adapters.FindAsync(launch.AdapterId, ct);
        return found ?? throw new DebugAdapterMissing(launch.AdapterId, reason ?? "It is not installed.");
    }

    private async Task<StdioChild> OpenAsync(DebugAdapter adapter, CancellationToken ct)
    {
        if (adapter.Port is { } port)
        {
            return await StdioChild.ConnectAsync(port, ConnectWait, ct);
        }
        if (!adapter.Listens)
        {
            return StdioChild.Start(adapter.Program, adapter.Args, workspace.Root, adapter.Env);
        }
        var free = StdioChild.FreePort();
        var args = adapter.Args.Select(a => a.Replace(DebugAdapters.PortVariable, free.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList();
        return await StdioChild.StartListeningAsync(adapter.Program, args, workspace.Root, adapter.Env, free, ConnectWait, ct);
    }

    /// <summary>Stops a session (as <see cref="DebugSession.StopAsync"/> does) and forgets it; false when there is none.</summary>
    public async Task<bool> StopAsync(string? id)
    {
        DebugSession? session;
        lock (_gate)
        {
            session = _all.FirstOrDefault(s => s.Id == id);
            if (session is null)
            {
                return false;
            }
            _all.Remove(session);
        }
        await session.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        List<DebugSession> all;
        lock (_gate)
        {
            all = [.. _all];
            _all.Clear();
        }
        await Task.WhenAll(all.Select(s => s.DisposeAsync().AsTask()));
    }
}
