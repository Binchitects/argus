namespace CodeArena;

/// <summary>Where one of the session's MCP servers stands, as the person is shown it.</summary>
internal enum LinkState
{
    Connecting,
    Connected,
    /// <summary>There is no MCP endpoint at its address (404, 405, a web page): tried again rarely.</summary>
    Unavailable,
    /// <summary>It refused, failed or did not answer in time: tried again soon, then less often.</summary>
    Failed,
}

/// <summary>
/// One MCP server of the session (Arena's, Argus's, or one of the person's own), connected in the
/// background: the session does not wait for it (a few seconds as it starts, at most), its tools
/// join when it answers, and while it does not it is tried again, after 5 seconds and then less
/// often, up to every 5 minutes. <see cref="Retry"/> tries at once; a call that finds it gone
/// (<see cref="Lost"/>) too.
/// </summary>
internal sealed class ServerLink : IAsyncDisposable
{
    /// <summary>The waits between tries after a failure, the last one repeated.</summary>
    public static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>No MCP endpoint at the address: that changes with an upgrade, not in a minute.</summary>
    public static readonly TimeSpan Rarely = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<McpClient>> _connect;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;

    /// <param name="name">The tools' server: "arena", "argus", or the name in the config.</param>
    /// <param name="title">What the person reads: Arena, Argus, the name.</param>
    public ServerLink(string name, string title, string? url, Func<CancellationToken, Task<McpClient>> connect)
    {
        Name = name;
        Title = title;
        Url = url;
        _connect = connect;
    }

    public string Name { get; }
    public string Title { get; }
    public string? Url { get; }
    public LinkState State { get; private set; } = LinkState.Connecting;
    /// <summary>Why it is not connected, in words for the person.</summary>
    public string? Error { get; private set; }
    public McpClient? Client { get; private set; }
    /// <summary>When it is tried next, while it is not connected.</summary>
    public DateTime? NextTry { get; private set; }
    /// <summary>Done when the first try has ended, connected or not.</summary>
    public Task FirstTry => _first.Task;
    /// <summary>
    /// Told of each change, on a background thread: the client connected now (null when there is
    /// none), and the one it replaces or that went (null when there was none).
    /// </summary>
    public Action<ServerLink, McpClient?, McpClient?>? Changed { get; set; }

    /// <summary>"Arena: 14 tools", "Argus: connecting", "Argus: not available (…)".</summary>
    public string Describe() => State switch
    {
        LinkState.Connected => $"{Title}: {Client?.Tools.Count ?? 0} tool{(Client?.Tools.Count == 1 ? "" : "s")}",
        LinkState.Connecting => $"{Title}: connecting",
        LinkState.Unavailable => $"{Title}: not available here",
        _ => $"{Title}: not connected" + (NextTry is { } next ? $", tried again at {next.ToLocalTime():HH:mm:ss}" : ""),
    };

    public void Start() => _loop = Task.Run(LoopAsync);

    /// <summary>Tries now: connects again when it is not connected, or anew (its tools listed again) when it is.</summary>
    public void Retry()
    {
        lock (_gate)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>A call found the server gone (the connection refused or dropped): connect again now.</summary>
    public void Lost(string why)
    {
        lock (_gate)
        {
            Error = why;
            _wake.TrySetResult();
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            await TryAsync();
        }
        finally
        {
            // However the loop ends, its first try has: nothing waits for it for ever.
            _first.TrySetResult();
        }
    }

    private async Task TryAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            Task woken;
            lock (_gate)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                woken = _wake.Task;
            }
            if (Client is null)
            {
                State = LinkState.Connecting;
                NextTry = null;
            }
            TimeSpan wait;
            try
            {
                var client = await _connect(_stop.Token);
                var old = Client;
                Client = client;
                State = LinkState.Connected;
                Error = null;
                NextTry = null;
                failures = 0;
                Changed?.Invoke(this, client, old);
                await CloseAsync(old);
                _first.TrySetResult();
                // Connected: nothing to do until a retry is asked for (or a call finds the server gone).
                await Task.WhenAny(woken, Task.Delay(Timeout.Infinite, _stop.Token));
                continue;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (McpUnavailableException e)
            {
                (State, Error, wait) = (LinkState.Unavailable, e.Message, Rarely);
            }
            catch (McpException e)
            {
                (State, Error, wait) = (LinkState.Failed, e.Message, Backoff[Math.Min(failures++, Backoff.Length - 1)]);
            }
            catch (OperationCanceledException)
            {
                (State, Error, wait) = (LinkState.Failed, $"{Url ?? Title} did not answer in time.", Backoff[Math.Min(failures++, Backoff.Length - 1)]);
            }
            catch (Exception e)
            {
                // Anything else (an address .NET cannot use, a scheme it does not speak, a fault in what hears of the change)
                // is a failure like the others: said, and tried again later. It never ends the loop.
                (State, Error, wait) = (LinkState.Failed, Url is { } url && !e.Message.Contains(url, StringComparison.Ordinal) ? $"{url}: {e.Message}" : e.Message,
                    Backoff[Math.Min(failures++, Backoff.Length - 1)]);
            }
            var gone = Client;
            Client = null;
            NextTry = DateTime.UtcNow + wait;
            try
            {
                Changed?.Invoke(this, null, gone);
            }
            catch
            {
                // What hears of it failed too: the link still tries again, and its state says why it is down.
            }
            await CloseAsync(gone);
            _first.TrySetResult();
            try
            {
                await Task.WhenAny(woken, Task.Delay(wait, _stop.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Lets a client go; one that fails as it closes is gone all the same.</summary>
    private static async Task CloseAsync(McpClient? client)
    {
        if (client is null)
        {
            return;
        }
        try
        {
            await client.DisposeAsync();
        }
        catch
        {
            // Its transport was broken already.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(3)));
        var client = Client;
        Client = null;
        await CloseAsync(client);
        _stop.Dispose();
    }
}
