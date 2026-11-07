using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The kernels of the notebooks open in the IDE: one Python per notebook, as Jupyter keeps one (its variables and
/// imports stay between cells), the project's own virtual environment when it has one (as run_code finds it), started in
/// the notebook's folder at its first cell. A cell's outputs come as Jupyter's: what it prints (stdout, stderr), what
/// display() shows and the value of its last line (each object's richest form: HTML, Markdown, SVG, PNG, JPEG, LaTeX,
/// JSON, and its text), the plots matplotlib made (PNG), and its error with the traceback. The page reads them as they
/// come. Interrupt stops the cell running (KeyboardInterrupt), Restart starts a new Python. They run only in a trusted
/// folder, as the person's own (unsandboxed, as the terminal is), and end with the IDE.
/// </summary>
internal sealed class NotebookKernels(Workspace workspace, Config config, WorkspaceState state, Func<string, string?> env, string dataDir) : IAsyncDisposable
{
    /// <summary>The most kernels at once (one per notebook).</summary>
    public const int MostKernels = 10;

    /// <summary>The most of a cell's text output kept (the rest is cut, and said so), and of its pictures.</summary>
    public const int MaxText = 2 * 1024 * 1024;
    public const int MaxPictures = 20 * 1024 * 1024;

    /// <summary>How long a read of a cell's outputs waits for something new.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(25);

    /// <summary>The program in the interpreter: it reads each cell from its own copy of stdin and answers on its own copy of stdout.</summary>
    private const string Runner = """
        import ast, base64, io, json, os, signal, sys, traceback, warnings

        requests = os.fdopen(os.dup(0), "r", encoding="utf-8")
        events = os.fdopen(os.dup(1), "w", encoding="utf-8")
        null = os.open(os.devnull, os.O_RDONLY)
        os.dup2(null, 0)
        # What programs write to fd 1 and 2 themselves (os.system, C code) goes to stderr, which code-arena reads too.
        os.dup2(2, 1)
        sys.stdin = open(os.devnull, encoding="utf-8")
        warnings.filterwarnings("ignore", message=".*non-interactive.*")
        warnings.filterwarnings("ignore", message=".*FigureCanvasAgg.*")
        scope = {"__name__": "__main__", "__builtins__": __builtins__}
        sys.path.insert(0, os.getcwd())
        run = None
        count = 0


        def send(kind, **fields):
            fields["type"] = kind
            fields["run"] = run
            events.write(json.dumps(fields) + "\n")
            events.flush()


        class Stream(io.TextIOBase):
            def __init__(self, name):
                self.name = name

            def writable(self):
                return True

            def write(self, text):
                if text:
                    send("stream", name=self.name, text=str(text))
                return len(text)


        sys.stdout = Stream("stdout")
        sys.stderr = Stream("stderr")

        RICH = (
            ("_repr_html_", "text/html"),
            ("_repr_markdown_", "text/markdown"),
            ("_repr_svg_", "image/svg+xml"),
            ("_repr_png_", "image/png"),
            ("_repr_jpeg_", "image/jpeg"),
            ("_repr_latex_", "text/latex"),
            ("_repr_json_", "application/json"),
        )


        def bundle(value):
            data = {}
            if not isinstance(value, type):
                try:
                    got = value._repr_mimebundle_() if hasattr(value, "_repr_mimebundle_") else None
                    if isinstance(got, tuple):
                        got = got[0]
                    if isinstance(got, dict):
                        data.update(got)
                except Exception:
                    pass
                for method, mime in RICH:
                    if mime in data:
                        continue
                    try:
                        got = getattr(value, method)() if hasattr(value, method) else None
                    except Exception:
                        got = None
                    if isinstance(got, tuple):
                        got = got[0]
                    if got is None:
                        continue
                    if isinstance(got, bytes):
                        got = base64.b64encode(got).decode("ascii")
                    data[mime] = got
            for mime, got in list(data.items()):
                if isinstance(got, bytes):
                    data[mime] = base64.b64encode(got).decode("ascii")
                elif mime != "application/json" and not isinstance(got, str):
                    data[mime] = str(got)
            try:
                data["text/plain"] = repr(value)
            except Exception as e:
                data["text/plain"] = f"<{type(value).__name__}: repr failed: {e}>"
            return data


        def display(*values, **_):
            for value in values:
                send("display_data", data=bundle(value), metadata={})


        scope["display"] = display


        def plots():
            plt = sys.modules.get("matplotlib.pyplot")
            if plt is None:
                return
            for num in plt.get_fignums():
                figure = plt.figure(num)
                out = io.BytesIO()
                figure.savefig(out, format="png", dpi=100, bbox_inches="tight")
                send("display_data", data={"image/png": base64.b64encode(out.getvalue()).decode("ascii"), "text/plain": repr(figure)}, metadata={})
            plt.close("all")


        def ours(tb):
            while tb is not None and tb.tb_frame.f_code.co_filename == __file__:
                tb = tb.tb_next
            return tb


        while True:
            try:
                line = requests.readline()
            except KeyboardInterrupt:
                continue
            if not line:
                break
            request = json.loads(line)
            run = request["run"]
            code = request["code"]
            count += 1
            name = f"<cell {count}>"
            import linecache
            linecache.cache[name] = (len(code), None, code.splitlines(True), name)
            send("start", count=count)
            ok = True
            try:
                tree = ast.parse(code, name, "exec")
                last = tree.body.pop() if tree.body and isinstance(tree.body[-1], ast.Expr) else None
                exec(compile(tree, name, "exec"), scope)
                if last is not None:
                    value = eval(compile(ast.Expression(last.value), name, "eval"), scope)
                    # A last line ending with ; shows nothing, as in Jupyter.
                    if value is not None and not code.rstrip().endswith(";"):
                        scope["_"] = value
                        send("execute_result", count=count, data=bundle(value), metadata={})
            except BaseException as e:
                ok = False
                tb = None if isinstance(e, SyntaxError) else ours(e.__traceback__)
                send("error", ename=type(e).__name__, evalue=str(e), traceback=traceback.format_exception(type(e), e, tb))
            try:
                plots()
            except Exception as e:
                send("stream", name="stderr", text=f"(the plot could not be shown: {e})\n")
            sys.stdout.flush()
            send("done", count=count, ok=ok)
            run = None
        """;

    private readonly object _gate = new();
    private readonly Dictionary<string, Kernel> _kernels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>A cell's run: its outputs so far (nbformat's), whether it ended, its execution count, and whether it went well.</summary>
    private sealed class Run(string id, Kernel kernel)
    {
        public string Id { get; } = id;
        public Kernel Kernel { get; } = kernel;
        public List<JsonObject> Outputs { get; } = [];
        public int Version { get; set; }
        public bool Done { get; set; }
        public bool Ok { get; set; } = true;
        public int? Count { get; set; }
        public int TextBytes { get; set; }
        public int PictureBytes { get; set; }
        public bool Cut { get; set; }
        public TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset Ended { get; set; } = DateTimeOffset.MaxValue;
    }

    private sealed class Kernel(Process process, string python, string dir)
    {
        public Process Process { get; } = process;
        public string Python { get; } = python;
        public string Dir { get; } = dir;
        public Run? Current { get; set; }
        public int Count { get; set; }
        public int Waiting { get; set; }
    }

    /// <summary>A notebook's kernel as the page shows it: the Python it runs (or would), whether it runs, is busy, and its count.</summary>
    public JsonObject Info(string notebook)
    {
        lock (_gate)
        {
            var k = _kernels.GetValueOrDefault(notebook);
            return new JsonObject
            {
                ["python"] = k?.Python ?? PythonSession.Find(config.Python, workspace.Root, env),
                ["running"] = k is not null,
                ["busy"] = k is { Waiting: > 0 },
                ["count"] = k?.Count ?? 0,
                ["trusted"] = state.Trusted,
            };
        }
    }

    /// <summary>A cell sent to the notebook's kernel (started first when it has none): its run's id, to read its outputs by.</summary>
    public string Execute(string notebook, string code)
    {
        if (!state.Trusted)
        {
            throw new IdeError(409, "untrusted", "This folder is not trusted: trust it to run the notebook's cells.");
        }
        if (code.Length > 1024 * 1024)
        {
            throw new IdeError(413, "too_large", "A cell holds at most 1 MB of code.");
        }
        lock (_gate)
        {
            var k = _kernels.GetValueOrDefault(notebook) ?? StartLocked(notebook);
            var run = new Run(Guid.NewGuid().ToString("N")[..16], k);
            _runs[run.Id] = run;
            Forget();
            k.Waiting++;
            try
            {
                k.Process.StandardInput.WriteLine(new JsonObject { ["run"] = run.Id, ["code"] = code }.ToJsonString());
                k.Process.StandardInput.Flush();
            }
            catch (IOException)
            {
                // It ended: the pump ends the runs.
            }
            return run.Id;
        }
    }

    /// <summary>A run's outputs, all of them, once there is more than <paramref name="since"/> (its version) or within 25 seconds.</summary>
    public async Task<JsonObject> OutputAsync(string? id, int since, CancellationToken ct)
    {
        Run run;
        Task changed;
        lock (_gate)
        {
            run = id is not null && _runs.TryGetValue(id, out var r) ? r : throw new IdeError(404, "no_run", "There is no such run (it ended long ago, or the IDE started again).");
            changed = run.Changed.Task;
        }
        if (run.Version <= since && !run.Done)
        {
            await Task.WhenAny(changed, Task.Delay(Wait, ct));
            ct.ThrowIfCancellationRequested();
        }
        lock (_gate)
        {
            return new JsonObject
            {
                ["version"] = run.Version,
                ["outputs"] = new JsonArray([.. run.Outputs.Select(o => o.DeepClone())]),
                ["done"] = run.Done,
                ["ok"] = run.Ok,
                ["count"] = run.Count,
            };
        }
    }

    /// <summary>Interrupt: the cell running gets a KeyboardInterrupt (SIGINT); on Windows the kernel restarts instead.</summary>
    public async Task InterruptAsync(string notebook)
    {
        Kernel? k;
        lock (_gate)
        {
            k = _kernels.GetValueOrDefault(notebook);
        }
        if (k is null)
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            await StopAsync(notebook);
            return;
        }
        _ = kill(k.Process.Id, 2);
    }

    /// <summary>Restart and shutdown: the kernel ends (its variables with it); the next cell starts a new one.</summary>
    public async Task StopAsync(string notebook)
    {
        Kernel? k;
        lock (_gate)
        {
            if (_kernels.Remove(notebook, out k))
            {
                End(k, "The kernel was restarted.");
            }
        }
        if (k is not null)
        {
            await Stop(k);
        }
    }

    private Kernel StartLocked(string notebook)
    {
        if (_kernels.Count >= MostKernels)
        {
            throw new IdeError(409, "too_many", $"{MostKernels} notebooks have a kernel already: shut one down first.");
        }
        var python = PythonSession.Find(config.Python, workspace.Root, env);
        var folder = Path.GetDirectoryName(notebook) ?? workspace.Root;
        var dir = Path.Combine(dataDir, "kernels", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var runner = Path.Combine(dir, "kernel.py");
        File.WriteAllText(runner, Runner);
        var psi = new ProcessStartInfo(python) { WorkingDirectory = Directory.Exists(folder) ? folder : workspace.Root };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(runner);
        psi.Environment["MPLBACKEND"] = "Agg";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (Path.GetDirectoryName(Path.GetDirectoryName(python)) is { } venv && File.Exists(Path.Combine(venv, "pyvenv.cfg")))
        {
            psi.Environment["VIRTUAL_ENV"] = venv;
        }
        Proc.Prepare(psi);
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new IdeError(500, "kernel", $"Could not start {python}.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new IdeError(409, "no_python", $"Could not start {python} ({e.Message}): set python in the config to the one to use, or make the project's .venv.");
        }
        var k = new Kernel(process, python, dir);
        _kernels[notebook] = k;
        _ = Task.Run(() => PumpAsync(notebook, k));
        _ = Task.Run(() => PumpRawAsync(k));
        return k;
    }

    /// <summary>The kernel's answers, a JSON line each, to the run each names; when it ends, what still waits ends with it.</summary>
    private async Task PumpAsync(string notebook, Kernel k)
    {
        try
        {
            while (await k.Process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (Json.ParseObject(line) is not { } e)
                {
                    continue;
                }
                lock (_gate)
                {
                    if (e.Str("run") is not { } id || !_runs.TryGetValue(id, out var run))
                    {
                        continue;
                    }
                    switch (e.Str("type"))
                    {
                        case "start":
                            k.Current = run;
                            run.Count = e.Int("count");
                            k.Count = run.Count ?? k.Count;
                            Changed(run);
                            break;
                        case "stream":
                            Stream(run, e.Str("name") == "stderr" ? "stderr" : "stdout", e.Str("text") ?? "");
                            break;
                        case "display_data":
                        case "execute_result":
                        case "error":
                            Add(run, e);
                            break;
                        case "done":
                            run.Ok = e.Bool("ok") == true && run.Ok;
                            Finish(run);
                            k.Current = null;
                            k.Waiting = Math.Max(0, k.Waiting - 1);
                            break;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        lock (_gate)
        {
            if (_kernels.TryGetValue(notebook, out var now) && now == k)
            {
                _kernels.Remove(notebook);
            }
            var code = k.Process.HasExited ? k.Process.ExitCode : (int?)null;
            End(k, code is null ? "The kernel ended." : $"The kernel ended (exit code {code}): the next cell starts a new one.");
        }
        await Stop(k);
    }

    /// <summary>What programs write themselves (to fd 1 and 2): the running cell's stdout.</summary>
    private async Task PumpRawAsync(Kernel k)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await k.Process.StandardError.ReadAsync(buffer)) > 0)
            {
                lock (_gate)
                {
                    if (k.Current is { } run)
                    {
                        Stream(run, "stdout", new string(buffer, 0, n));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Text to a run's stream: joined to the last output when it is the same stream, as Jupyter shows it.</summary>
    private static void Stream(Run run, string name, string text)
    {
        if (run.Cut)
        {
            return;
        }
        if (run.TextBytes + text.Length > MaxText)
        {
            text = text[..Math.Max(0, MaxText - run.TextBytes)] + $"\n(the output was cut at {MaxText / (1024 * 1024)} MB)\n";
            run.Cut = true;
        }
        run.TextBytes += text.Length;
        if (run.Outputs.Count > 0 && run.Outputs[^1] is var last && last.Str("output_type") == "stream" && last.Str("name") == name)
        {
            last["text"] = last.Str("text") + text;
        }
        else
        {
            run.Outputs.Add(new JsonObject { ["output_type"] = "stream", ["name"] = name, ["text"] = text });
        }
        Changed(run);
    }

    /// <summary>A display, a result or an error, as nbformat has it; pictures past the most kept are left out, and said so.</summary>
    private static void Add(Run run, JsonObject e)
    {
        var type = e.Str("type")!;
        if (type == "error")
        {
            run.Ok = false;
            run.Outputs.Add(new JsonObject
            {
                ["output_type"] = "error",
                ["ename"] = e.Str("ename") ?? "Error",
                ["evalue"] = e.Str("evalue") ?? "",
                ["traceback"] = e["traceback"]?.DeepClone() ?? new JsonArray(),
            });
            Changed(run);
            return;
        }
        var data = e["data"] as JsonObject ?? [];
        var size = data.Sum(d => d.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Length : 0);
        if (run.PictureBytes + size > MaxPictures)
        {
            Stream(run, "stderr", $"(an output was left out: a cell's outputs are kept up to {MaxPictures / (1024 * 1024)} MB)\n");
            return;
        }
        run.PictureBytes += size;
        var output = new JsonObject { ["output_type"] = type, ["data"] = data.DeepClone(), ["metadata"] = e["metadata"]?.DeepClone() ?? new JsonObject() };
        if (type == "execute_result")
        {
            output["execution_count"] = e.Int("count");
        }
        run.Outputs.Add(output);
        Changed(run);
    }

    private static void Changed(Run run)
    {
        run.Version++;
        var was = run.Changed;
        run.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        was.TrySetResult();
    }

    private static void Finish(Run run)
    {
        run.Done = true;
        run.Ended = DateTimeOffset.UtcNow;
        Changed(run);
    }

    /// <summary>The kernel ended (or was restarted): every run of its still waiting ends, saying so.</summary>
    private void End(Kernel k, string why)
    {
        foreach (var run in _runs.Values.Where(r => !r.Done && r.Kernel == k))
        {
            run.Ok = false;
            run.Outputs.Add(new JsonObject { ["output_type"] = "error", ["ename"] = "KernelEnded", ["evalue"] = why, ["traceback"] = new JsonArray() });
            Finish(run);
        }
        k.Waiting = 0;
    }

    /// <summary>Runs ended a while ago are forgotten (their outputs are in the notebook by then).</summary>
    private void Forget()
    {
        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var id in _runs.Where(r => r.Value.Ended < old).Select(r => r.Key).ToList())
        {
            _runs.Remove(id);
        }
    }

    private static async Task Stop(Kernel k)
    {
        try
        {
            if (!k.Process.HasExited)
            {
                k.Process.Kill(entireProcessTree: true);
                await k.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
        }
        try
        {
            Directory.Delete(k.Dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Kernel> all;
        lock (_gate)
        {
            all = [.. _kernels.Values];
            _kernels.Clear();
        }
        await Task.WhenAll(all.Select(Stop));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
