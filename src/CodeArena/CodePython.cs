using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The Python run_code runs: one interpreter per chat, kept between calls as a notebook keeps its kernel (variables,
/// imports and loaded data stay), the project's own virtual environment when it has one. Each call's program runs in it;
/// what it prints, the value of its last line, its error with the line, and its plots come back. A call past its time,
/// or a program that ends the interpreter, ends the session: the next call starts a new one, and says so.
/// </summary>
internal sealed class PythonSession : IAsyncDisposable
{
    /// <summary>The program in the interpreter: it reads each call's code from its own copy of stdin and runs it.</summary>
    private const string Runner = """
        import ast, json, linecache, os, sys, traceback, warnings

        requests = os.fdopen(os.dup(0), "r", encoding="utf-8")
        null = os.open(os.devnull, os.O_RDONLY)
        os.dup2(null, 0)
        sys.stdin = open(os.devnull, encoding="utf-8")
        os.dup2(1, 2)
        sys.stderr = sys.stdout
        MARK = os.environ.pop("ARENA_MARK")
        PLOTS = os.environ.get("ARENA_PLOTS")
        warnings.filterwarnings("ignore", message=".*non-interactive.*")
        scope = {"__name__": "__main__", "__builtins__": __builtins__}
        sys.path.insert(0, os.getcwd())
        cell = 0


        def plots():
            plt = sys.modules.get("matplotlib.pyplot")
            if plt is None or not PLOTS:
                return []
            saved = []
            for num in plt.get_fignums():
                os.makedirs(PLOTS, exist_ok=True)
                path = os.path.join(PLOTS, f"plot-{os.getpid()}-{cell}-{num}.png")
                plt.figure(num).savefig(path, dpi=100, bbox_inches="tight")
                saved.append(path)
            plt.close("all")
            return saved


        for request in requests:
            code = json.loads(request)["code"]
            cell += 1
            name = f"<call {cell}>"
            linecache.cache[name] = (len(code), None, code.splitlines(True), name)
            ok, code_exit = True, None
            try:
                tree = ast.parse(code, name, "exec")
                last = tree.body.pop() if tree.body and isinstance(tree.body[-1], ast.Expr) else None
                exec(compile(tree, name, "exec"), scope)
                if last is not None:
                    value = eval(compile(ast.Expression(last.value), name, "eval"), scope)
                    if value is not None:
                        scope["_"] = value
                        print(repr(value))
            except SystemExit as e:
                code_exit = e.code if isinstance(e.code, int) else (0 if e.code is None else 1)
                ok = code_exit == 0
            except SyntaxError as e:
                ok = False
                traceback.print_exception(type(e), e, None)
            except BaseException as e:
                ok = False
                tb = e.__traceback__
                while tb is not None and tb.tb_frame.f_code.co_filename == __file__:
                    tb = tb.tb_next
                traceback.print_exception(type(e), e, tb)
            try:
                saved = plots()
            except Exception as e:
                saved = []
                print(f"(the plot could not be saved: {e})")
            sys.stdout.flush()
            print("\n" + MARK + json.dumps({"ok": ok, "exit": code_exit, "plots": saved}), flush=True)
        """;

    /// <summary>What one call gave: its output, whether it went well, the plots it made (files), whether the session ended with it, and the code sys.exit gave (the interpreter goes on).</summary>
    public sealed record Ran(string Output, bool Ok, List<string> Plots, bool Ended, bool TimedOut, int? Exit = null);

    private readonly Process _process;
    private readonly string _dir;
    private readonly string _mark = "\u0001arena-done-" + Guid.NewGuid().ToString("N")[..12] + " ";
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private readonly Task _pump, _errors;
    private Proc.Bounded _output = new(30_000);
    private TaskCompletionSource<JsonObject?>? _waiting;

    /// <summary>Whether its output has ended (the interpreter with it): a call made after that is answered at once.</summary>
    private bool _ended;

    /// <summary>The chat it is for, the Python it runs, and how many calls it has run.</summary>
    public string Chat { get; }
    public string Python { get; }
    public int Calls { get; private set; }
    public bool Alive => !_process.HasExited;

    private PythonSession(ProcessStartInfo psi, string dir, string chat, string python)
    {
        (_dir, Chat, Python) = (dir, chat, python);
        psi.Environment["ARENA_MARK"] = _mark;
        try
        {
            _process = Process.Start(psi) ?? throw new ToolError($"Could not start {python}.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolError($"Could not start {python} ({e.Message}): set python in the config to the one to use, or make the project's .venv.");
        }
        // The runner sends its stderr to stdout; what Python says before it runs (a runner it cannot read) comes here.
        _errors = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (_gate)
                    {
                        _output.Append(line + "\n");
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// The Python to run: the one the config names, else the project's virtual environment (.venv, venv or env, or the
    /// one VIRTUAL_ENV names), else python3 (python on Windows).
    /// </summary>
    public static string Find(string? configured, string root, Func<string, string?> env)
    {
        if (configured is { Length: > 0 })
        {
            return configured;
        }
        var windows = OperatingSystem.IsWindows();
        var inside = windows ? Path.Combine("Scripts", "python.exe") : Path.Combine("bin", "python");
        foreach (var dir in new[] { Path.Combine(root, ".venv"), Path.Combine(root, "venv"), Path.Combine(root, "env"), env("VIRTUAL_ENV") }.OfType<string>())
        {
            if (File.Exists(Path.Combine(dir, inside)) && File.Exists(Path.Combine(dir, "pyvenv.cfg")))
            {
                return Path.Combine(dir, inside);
            }
        }
        return windows ? "python" : "python3";
    }

    /// <summary>
    /// Starts the interpreter in <paramref name="dir"/> (made, and taken away when it ends): <paramref name="wrap"/> puts
    /// it in the session's sandbox, <paramref name="env"/> are what the arena module reads, plots go to <paramref name="plots"/>.
    /// </summary>
    public static PythonSession Start(string python, string dir, string root, string chat, string plots, IReadOnlyDictionary<string, string> env, Func<ProcessStartInfo, ProcessStartInfo> wrap)
    {
        Directory.CreateDirectory(dir);
        var runner = Path.Combine(dir, "runner.py");
        File.WriteAllText(runner, Runner);
        var psi = new ProcessStartInfo(python) { WorkingDirectory = root };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(runner);
        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }
        psi.Environment["MPLBACKEND"] = "Agg";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["ARENA_PLOTS"] = plots;
        if (Path.GetDirectoryName(Path.GetDirectoryName(python)) is { } venv && File.Exists(Path.Combine(venv, "pyvenv.cfg")))
        {
            psi.Environment["VIRTUAL_ENV"] = venv;
        }
        psi = wrap(psi);
        Proc.Prepare(psi);
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        return new PythonSession(psi, dir, chat, python);
    }

    /// <summary>Its output a line at a time: to the call running, until the line that says the call ended.</summary>
    private async Task PumpAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                lock (_gate)
                {
                    if (line.StartsWith(_mark, StringComparison.Ordinal))
                    {
                        _waiting?.TrySetResult(Json.ParseObject(line[_mark.Length..]));
                        continue;
                    }
                    _output.Append(line + "\n");
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        lock (_gate)
        {
            _ended = true;
            _waiting?.TrySetResult(null);
        }
    }

    /// <summary>
    /// Runs one call's program, one at a time, its output kept within <paramref name="maxChars"/> (its start and its end).
    /// Past <paramref name="timeout"/>, or when <paramref name="ct"/> is cancelled, the interpreter is stopped.
    /// </summary>
    public async Task<Ran> RunAsync(string code, TimeSpan timeout, int maxChars, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var done = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                // What a thread of an earlier call printed after it ended comes first.
                var before = _output.ToString();
                _output = new Proc.Bounded(maxChars);
                _output.Append(before);
                _waiting = done;
                if (_ended)
                {
                    // It ended before this call: nothing will answer it.
                    done.TrySetResult(null);
                }
            }
            Calls++;
            try
            {
                await _process.StandardInput.WriteLineAsync(new JsonObject { ["code"] = code }.ToJsonString().AsMemory(), ct);
                await _process.StandardInput.FlushAsync(ct);
            }
            catch (IOException)
            {
                // It ended: the pump says so.
            }
            var timedOut = false;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            JsonObject? end;
            try
            {
                end = await done.Task.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                Stop();
                ct.ThrowIfCancellationRequested();
                timedOut = true;
                end = null;
            }
            if (end is null)
            {
                // It ended (or was stopped): what it printed last is read to its end first.
                await Task.WhenAny(Task.WhenAll(_pump, _errors), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            }
            string output;
            lock (_gate)
            {
                (output, _output, _waiting) = (_output.ToString(), new Proc.Bounded(maxChars), null);
            }
            if (end is not null && output.EndsWith('\n'))
            {
                // The line before the end's mark is the runner's own.
                output = output[..^1];
            }
            var plots = (end?["plots"] as JsonArray ?? []).Select(p => p?.ToString()).OfType<string>().ToList();
            return new Ran(output, end?.Bool("ok") == true, plots, end is null, timedOut, end?.Int("exit"));
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The exit code it ended with, or null while it runs.</summary>
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    private void Stop()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
        {
            // It ended meanwhile.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2)));
        _process.Dispose();
        _one.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
