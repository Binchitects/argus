using System.Collections;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodeArena;

/// <summary>What a terminal sends a page: output as it came, or the exit code once its shell ended.</summary>
internal readonly record struct TerminalFrame(byte[]? Output, int? Exit);

/// <summary>
/// One of the IDE's terminals: a shell in a pseudo-terminal, its last output
/// kept so a page that comes back (or a second tab) sees the screen, and the
/// pages watching it. A thread reads the output and another waits for the
/// shell to end; neither ties up the thread pool.
/// </summary>
internal sealed class Terminal
{
    /// <summary>The output kept for a page that attaches later.</summary>
    public const int Backlog = 256 * 1024;

    private readonly IPty _pty;
    private readonly object _gate = new();
    private readonly byte[] _ring = new byte[Backlog];
    private long _written;
    private readonly List<Channel<TerminalFrame>> _watchers = [];
    private readonly ManualResetEventSlim _readDone = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Terminal(string id, string title, IPty pty, int cols, int rows)
    {
        Id = id;
        Title = title;
        _pty = pty;
        Cols = cols;
        Rows = rows;
        new Thread(ReadLoop) { IsBackground = true, Name = $"terminal {id} output" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = $"terminal {id} exit" }.Start();
    }

    public string Id { get; }
    public string Title { get; }
    public int Pid => _pty.Pid;
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int? ExitCode { get; private set; }
    /// <summary>The exit code, once the shell has ended and its output is all read.</summary>
    public Task<int> Exited => _exited.Task;

    public JsonObject Json() => new()
    {
        ["id"] = Id,
        ["title"] = Title,
        ["pid"] = Pid,
        ["cols"] = Cols,
        ["rows"] = Rows,
        ["exitCode"] = ExitCode,
    };

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = _pty.Read(buffer)) > 0)
        {
            Output(buffer.AsSpan(0, n));
        }
        _readDone.Set();
    }

    private void WaitLoop()
    {
        var code = _pty.WaitForExit();
        // The last output comes with the program's side closing; a program left in the background may keep it open.
        _readDone.Wait(TimeSpan.FromMilliseconds(500));
        _pty.Stop();
        _readDone.Wait(TimeSpan.FromSeconds(2));
        _pty.Dispose();
        lock (_gate)
        {
            ExitCode = code;
            foreach (var w in _watchers)
            {
                w.Writer.TryWrite(new TerminalFrame(null, code));
                w.Writer.TryComplete();
            }
            _watchers.Clear();
        }
        _exited.TrySetResult(code);
    }

    private void Output(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        lock (_gate)
        {
            // Into the ring (a read is far smaller than it): the last Backlog bytes, the oldest overwritten.
            var start = (int)(_written % Backlog);
            var first = Math.Min(data.Length, Backlog - start);
            data[..first].CopyTo(_ring.AsSpan(start));
            data[first..].CopyTo(_ring);
            _written += data.Length;
            foreach (var w in _watchers)
            {
                w.Writer.TryWrite(new TerminalFrame(copy, null));
            }
        }
    }

    /// <summary>The output kept so far, then each frame as it comes; detach with what was returned.</summary>
    public (byte[] Backlog, Channel<TerminalFrame> Frames) Attach()
    {
        // A page that does not keep up loses the oldest frames, not this program's memory.
        var channel = Channel.CreateBounded<TerminalFrame>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            if (ExitCode is { } code)
            {
                channel.Writer.TryWrite(new TerminalFrame(null, code));
                channel.Writer.TryComplete();
            }
            else
            {
                _watchers.Add(channel);
            }
            return (Kept(), channel);
        }
    }

    public void Detach(Channel<TerminalFrame> channel)
    {
        lock (_gate)
        {
            _watchers.Remove(channel);
        }
    }

    private byte[] Kept()
    {
        if (_written <= Backlog)
        {
            return _ring.AsSpan(0, (int)_written).ToArray();
        }
        var start = (int)(_written % Backlog);
        var all = new byte[Backlog];
        _ring.AsSpan(start).CopyTo(all);
        _ring.AsSpan(0, start).CopyTo(all.AsSpan(Backlog - start));
        // Cut where a line starts, not in the middle of an escape sequence or a character.
        var line = Array.IndexOf(all, (byte)'\n');
        return line >= 0 && line < 4096 ? all[(line + 1)..] : all;
    }

    public void Input(ReadOnlySpan<byte> data)
    {
        if (ExitCode is null)
        {
            _pty.Write(data);
        }
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 2, Pty.MaxCols);
        rows = Math.Clamp(rows, 2, Pty.MaxRows);
        lock (_gate)
        {
            if (ExitCode is not null || cols == Cols && rows == Rows)
            {
                return;
            }
            Cols = cols;
            Rows = rows;
        }
        _pty.Resize(cols, rows);
    }

    public void Kill()
    {
        if (ExitCode is null)
        {
            _pty.Kill();
        }
    }
}

/// <summary>
/// The IDE's terminals: shells in the working directory, as powerful as the
/// person's own (the page that drives them is behind this run's key). The
/// shell is "terminalShell" in config.json, else $SHELL (a login shell on
/// macOS, as Terminal.app starts it), else bash or sh; on Windows PowerShell 7,
/// Windows PowerShell, else cmd.exe. The API key stays out of their environment.
/// </summary>
internal sealed class Terminals(Workspace workspace, Config config, Func<string, string?> env) : IAsyncDisposable
{
    public const int Max = 10;
    private readonly object _gate = new();
    private readonly List<Terminal> _all = [];
    private int _next;

    public IReadOnlyList<Terminal> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _all];
            }
        }
    }

    public Terminal? Find(string? id)
    {
        lock (_gate)
        {
            return _all.FirstOrDefault(t => t.Id == id);
        }
    }

    /// <summary>A new terminal at this size. Throws IdeError when there are too many, IOException when the shell does not start.</summary>
    public Terminal Open(int cols, int rows)
    {
        lock (_gate)
        {
            if (_all.Count(t => t.ExitCode is null) >= Max)
            {
                throw new IdeError(409, "too_many", $"{Max} terminals are open: close one first.");
            }
        }
        var (program, args) = Shell();
        cols = Math.Clamp(cols, 2, Pty.MaxCols);
        rows = Math.Clamp(rows, 2, Pty.MaxRows);
        var pty = Pty.Start(program, args, workspace.Root, Environment(), cols, rows);
        lock (_gate)
        {
            var terminal = new Terminal((++_next).ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileNameWithoutExtension(program), pty, cols, rows);
            _all.Add(terminal);
            return terminal;
        }
    }

    /// <summary>Ends a terminal's shell (and what runs in it), and forgets it.</summary>
    public bool Close(string? id)
    {
        Terminal? terminal;
        lock (_gate)
        {
            terminal = _all.FirstOrDefault(t => t.Id == id);
            if (terminal is null)
            {
                return false;
            }
            _all.Remove(terminal);
        }
        terminal.Kill();
        return true;
    }

    /// <summary>The shell to start, as a full path (exec does not search PATH), and its arguments.</summary>
    public (string Program, string[] Args) Shell()
    {
        if (config.TerminalShell is { Length: > 0 } configured)
        {
            return (Locate(configured) ?? throw new IdeError(400, "no_shell", $"The terminal's shell {configured} (\"terminalShell\" in config.json) is not there."), []);
        }
        if (OperatingSystem.IsWindows())
        {
            var shell = Locate("pwsh.exe") ?? Locate("powershell.exe") ?? env("COMSPEC") ?? "cmd.exe";
            return (shell, []);
        }
        var unix = env("SHELL") is { Length: > 0 } s && Locate(s) is { } found ? found : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return (unix, OperatingSystem.IsMacOS() ? ["-l"] : []);
    }

    /// <summary>A program as a full path: as given when it has a folder, else found on PATH.</summary>
    private string? Locate(string program)
    {
        if (Path.IsPathRooted(program) || program.Contains('/') || program.Contains('\\'))
        {
            var full = Path.GetFullPath(program, workspace.Root);
            return File.Exists(full) ? full : null;
        }
        foreach (var dir in (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, program);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>This process's environment for the shell, without the API key, with what tells programs about the terminal.</summary>
    private static Dictionary<string, string> Environment()
    {
        var vars = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            vars[(string)e.Key] = e.Value as string ?? "";
        }
        vars.Remove("ARENA_API_KEY");
        if (!OperatingSystem.IsWindows())
        {
            vars["TERM"] = "xterm-256color";
            if (!vars.ContainsKey("LANG") && !vars.ContainsKey("LC_ALL") && !vars.ContainsKey("LC_CTYPE"))
            {
                vars["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
            }
        }
        vars["COLORTERM"] = "truecolor";
        vars["TERM_PROGRAM"] = "code-arena";
        vars["TERM_PROGRAM_VERSION"] = Cli.Version;
        return vars;
    }

    public async ValueTask DisposeAsync()
    {
        List<Terminal> all;
        lock (_gate)
        {
            all = [.. _all];
            _all.Clear();
        }
        foreach (var t in all)
        {
            t.Kill();
        }
        await Task.WhenAny(Task.WhenAll(all.Select(t => t.Exited)), Task.Delay(TimeSpan.FromSeconds(4)));
    }
}
