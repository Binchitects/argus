using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeArena;

/// <summary>
/// A program Code Arena talks to over a byte stream (a debug adapter, a language server): its stdin and stdout, or a
/// TCP connection to 127.0.0.1 for one that listens on a port. It is started like the person's own programs, without
/// ARENA_API_KEY in its environment and with its stdin never the console's. The last 200 lines it wrote to stderr (and,
/// when it talks over TCP, to stdout) are kept to say why it failed. Stopping it stops every process it started too.
/// </summary>
internal sealed class StdioChild : IAsyncDisposable
{
    /// <summary>The lines of its output kept.</summary>
    public const int KeptLines = 200;

    private readonly Process? _process;
    private readonly TcpClient? _tcp;
    private readonly LastLines _said;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private StdioChild(Process? process, TcpClient? tcp, Stream reading, Stream writing, LastLines said)
    {
        _process = process;
        _tcp = tcp;
        _said = said;
        Reading = reading;
        Writing = writing;
        if (process is not null)
        {
            _ = WaitAsync(process);
        }
    }

    /// <summary>What the program writes.</summary>
    public Stream Reading { get; }

    /// <summary>What the program reads.</summary>
    public Stream Writing { get; }

    /// <summary>Its process; null for a program reached at a port only.</summary>
    public int? Pid => _process?.Id;

    /// <summary>Its exit code once it ended; -1 for a program reached at a port only, once it is let go.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>The last lines it wrote to stderr, oldest first.</summary>
    public string[] Said() => _said.All();

    /// <summary>Done once its stderr is read to the end: after it ended, what <see cref="Said"/> gives is all of it.</summary>
    public Task SaidAll => _said.Read;

    /// <summary>Starts a program that speaks on its stdin and stdout. Throws IOException when it cannot be started.</summary>
    public static StdioChild Start(string program, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string?>? env = null)
    {
        var process = Launch(program, args, cwd, env);
        var said = new LastLines();
        said.Watch(process.StandardError);
        return new StdioChild(process, null, process.StandardOutput.BaseStream, process.StandardInput.BaseStream, said);
    }

    /// <summary>
    /// Starts a program that listens on 127.0.0.1 at this port, and connects to it once it does (within the time
    /// given). Throws IOException when it cannot be started, or ends or never listens: with what it said.
    /// </summary>
    public static async Task<StdioChild> StartListeningAsync(string program, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string?>? env, int port, TimeSpan wait, CancellationToken ct)
    {
        var process = Launch(program, args, cwd, env);
        var said = new LastLines();
        said.Watch(process.StandardError);
        said.Watch(process.StandardOutput);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(wait);
        while (true)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port, limit.Token);
                var stream = tcp.GetStream();
                return new StdioChild(process, tcp, stream, stream, said);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                tcp.Dispose();
                if (process.HasExited || limit.IsCancellationRequested)
                {
                    var why = process.HasExited
                        ? $"{Path.GetFileName(program)} ended (exit code {process.ExitCode})"
                        : $"{Path.GetFileName(program)} did not listen on port {port} within {wait.TotalSeconds:0} s";
                    KillTree(process);
                    // Its last words come after it ended; a child it left may hold its pipes open, so not for long.
                    await Task.WhenAny(said.Read, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
                    process.Dispose();
                    ct.ThrowIfCancellationRequested();
                    throw new IOException(With(why, said.All()));
                }
            }
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Connects to a program already listening on 127.0.0.1 at this port (one the person started). Throws IOException when none answers.</summary>
    public static async Task<StdioChild> ConnectAsync(int port, TimeSpan wait, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(wait);
            await tcp.ConnectAsync(IPAddress.Loopback, port, limit.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new IOException($"Nothing answers on 127.0.0.1:{port}: {e.Message}", e);
        }
        var stream = tcp.GetStream();
        return new StdioChild(null, tcp, stream, stream, new LastLines());
    }

    /// <summary>A port on 127.0.0.1 free now, for a program told to listen on it.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A reason, with the last lines the program said under it.</summary>
    public static string With(string why, IReadOnlyList<string> said) =>
        said.Count == 0 ? why + "." : $"{why}:\n{string.Join('\n', said.TakeLast(20))}";

    private static Process Launch(string program, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string?>? env)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in env ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                psi.Environment.Remove(name);
            }
            else
            {
                psi.Environment[name] = value;
            }
        }
        // The person's key stays with Code Arena, whatever the environment given says.
        psi.Environment.Remove("ARENA_API_KEY");
        try
        {
            return Process.Start(psi) ?? throw new IOException($"{program} did not start.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new IOException($"{program} could not be started: {e.Message}", e);
        }
    }

    private async Task WaitAsync(Process process)
    {
        await process.WaitForExitAsync();
        _exited.TrySetResult(process.ExitCode);
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended meanwhile.
        }
    }

    /// <summary>Ends it, and every process it started; for a program reached at a port only, the connection.</summary>
    public void Kill()
    {
        if (_process is not null)
        {
            KillTree(_process);
        }
        _tcp?.Close();
        if (_process is null)
        {
            _exited.TrySetResult(-1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        if (_process is not null)
        {
            await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            _process.Dispose();
        }
        _tcp?.Dispose();
    }

    /// <summary>The last lines of what a program wrote, from the readers it was given.</summary>
    private sealed class LastLines
    {
        private readonly Queue<string> _lines = new();
        private readonly List<Task> _watching = [];

        public string[] All()
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }

        /// <summary>Done once every reader reached its end (a program that ended has said all it will).</summary>
        public Task Read => Task.WhenAll(_watching);

        public void Watch(StreamReader reader) => _watching.Add(Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    lock (_lines)
                    {
                        _lines.Enqueue(line);
                        while (_lines.Count > KeptLines)
                        {
                            _lines.Dequeue();
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // It is gone.
            }
        }));
    }
}
