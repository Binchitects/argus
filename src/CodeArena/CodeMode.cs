using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A bridge for a program's calls of the session's tools (code mode): a Unix socket, one JSON line each way. Each call
/// is answered by <see cref="Answer"/>; the program waits for it.
/// </summary>
internal sealed class ToolBridge : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonObject>> _answer;

    /// <summary>Where a program connects (ARENA_SOCKET): the socket's path, or tcp:127.0.0.1:port.</summary>
    public string Path { get; }

    /// <summary>On TCP, what a program says first (ARENA_TOKEN), so no other program on the machine calls the tools; null on a Unix socket.</summary>
    public string? Token { get; }

    /// <summary>The calls answered, by name, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>
    /// A bridge at <paramref name="path"/>, a Unix socket only its owner may open; or, with <paramref name="path"/> null,
    /// on TCP at 127.0.0.1 with a token (Python on Windows has no Unix sockets, and a socket's path has about 100 characters).
    /// </summary>
    public ToolBridge(string? path, Func<string, JsonObject, CancellationToken, Task<JsonObject>> answer)
    {
        _answer = answer;
        if (path is null)
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            Path = $"tcp:127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndPoint!).Port}";
            Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        }
        else
        {
            Path = path;
            File.Delete(path);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        _listener.Listen(4);
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket conn;
            try
            {
                conn = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => TalkAsync(conn));
        }
    }

    private async Task TalkAsync(Socket conn)
    {
        using (conn)
        await using (var stream = new NetworkStream(conn, ownsSocket: false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                // On TCP the token comes first: a connection without it is not the program's.
                if (Token is not null && await reader.ReadLineAsync(_stop.Token) != Token)
                {
                    return;
                }
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    JsonObject answer;
                    if (Json.ParseObject(line) is not { } asked || asked.Str("name") is not { Length: > 0 } name)
                    {
                        answer = new JsonObject { ["ok"] = false, ["error"] = "A call is {\"name\": ..., \"args\": {...}}." };
                    }
                    else
                    {
                        lock (Calls)
                        {
                            Calls.Add(name);
                        }
                        answer = await _answer(name, asked["args"] as JsonObject ?? [], _stop.Token);
                    }
                    await writer.WriteLineAsync(answer.ToJsonString());
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        await _loop;
        if (Token is null)
        {
            File.Delete(Path);
        }
        _stop.Dispose();
    }
}

/// <summary>
/// Code mode (run_code): a Python program, in the session's sandbox, calls the session's tools as functions (import
/// arena: its local tools, Arena's, Argus's and the person's MCP servers) through a bridge here, each call checked as a
/// direct one would be (the mode, the rules, asking where it asks), and only what it prints comes back.
/// </summary>
internal sealed partial class Runtime
{
    /// <summary>The most calls of the session's tools one program makes.</summary>
    public const int MostCodeCalls = 100;

    /// <summary>
    /// The tools a program may call: every one but those that hand work on, wait on jobs, or run code themselves, and but the
    /// servers' tools the person turned off in this session.
    /// </summary>
    private IEnumerable<ToolDef> CodeTools() =>
        Tools.All.Where(t => t.Kind != ToolKind.Agent && t.Name is not ("run_code" or "command_output" or "stop_command" or "ask_agent" or ToolDemand.Function)
            && (t.Group is null || !Demand.IsOff(t.Group)));

    /// <summary>
    /// The note for a model given the servers' tools as code: each as a program calls it, with its parameters (the required
    /// ones first) and the first line of what it does; empty in the other modes.
    /// </summary>
    private string ToolsInCode()
    {
        var tools = Tools.InCode();
        if (tools.Count == 0)
        {
            return "";
        }
        var lines = tools.Select(t =>
        {
            var properties = (t.Parameters["properties"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
            var required = (t.Parameters["required"] as JsonArray)?.Select(r => r?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            var args = string.Join(", ", properties.OrderBy(p => !required.Contains(p)).Select(p => required.Contains(p) ? p : p + "=…"));
            var about = t.Description.Split('\n')[0];
            return $"- {PythonName(t.Server ?? "local", t.Name)}({args}): {(about.Length > 140 ? about[..140] + "…" : about)}";
        });
        return "The servers' tools are functions to call from a run_code program, not one by one: write the program, call them with keyword " +
            "arguments, and print what you need (help(...) in it tells one's parameters in full). The files, search, git and shell tools stay as they are. " +
            "They are:\n" + string.Join('\n', lines);
    }

    /// <summary>Python's keywords: a name that is one gets a _ after it, as the arena module names it.</summary>
    private static readonly HashSet<string> PythonKeywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try",
        "while", "with", "yield",
    ];

    /// <summary>A function as a program calls it, arena.&lt;group&gt;.&lt;function&gt;: the names the arena module gives them.</summary>
    internal static string PythonName(string group, string function)
    {
        var g = Identifier(group);
        return $"arena.{(g is "tools" or "call" or "ToolError" ? g + "_" : g)}.{Identifier(function)}";
    }

    private static string Identifier(string name)
    {
        var clean = NotWord().Replace(name, "_").Trim('_');
        clean = clean.Length == 0 ? "tool" : char.IsDigit(clean[0]) ? "_" + clean : clean;
        return PythonKeywords.Contains(clean) ? clean + "_" : clean;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\W")]
    private static partial System.Text.RegularExpressions.Regex NotWord();

    public static ToolDef RunCodeTool(Runtime rt) => new()
    {
        Name = "run_code",
        Kind = ToolKind.Shell,
        Description = "Run Python in this chat's own interpreter, as a notebook does: variables, imports and data stay from one call to the next " +
            "(fresh: true starts anew). It is the project's virtual environment when it has one (.venv, venv), in the working directory and the " +
            "session's sandbox. What it prints comes back, and the value of a last line that is an expression, an error with its line, and the " +
            "plots it makes (matplotlib: they are shown to you and kept in .code-arena/plots). In it, `import arena` calls this session's tools as " +
            "functions: arena.tools.search(\"words\") lists them (arena.local.read_file, arena.argus.find_symbol, arena.<server>.<tool>), help(...) " +
            "tells one's parameters, and each call (keyword arguments) returns the tool's result, a dict or a list when it is JSON; a refused call " +
            $"raises arena.ToolError. Each call is checked as a direct one is (the mode, the rules); at most {MostCodeCalls} in one run. Use it to " +
            "compute, to explore data, and for many tool calls whose results you only filter or join; print only what you need.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "code":{"type":"string","description":"The code to run; print() what you need, or end with the expression to show."},
              "fresh":{"type":"boolean","description":"Start a new interpreter first: what the earlier calls made is gone."},
              "timeout_seconds":{"type":"integer","description":"Stop it after this long (default 120, at most 600): the interpreter is stopped, and its variables with it."}},
             "required":["code"]}
            """),
        Summary = a => (a.Bool("fresh") == true ? "(fresh) " : "") + Fmt.OneLine(a.Str("code"), 80),
        Run = rt.RunCodeAsync,
    };

    /// <summary>This chat's interpreter, and the bridge its programs call the tools through; started at the first run_code.</summary>
    private PythonSession? _python;
    private ToolBridge? _pythonBridge;

    /// <summary>The call running in the interpreter now: its context answers the tools its program calls, and its count of them.</summary>
    private (ToolContext Context, List<ToolDef> Tools, int Calls)? _pythonCall;

    private async Task StopPythonAsync()
    {
        // The bridge first: its socket is in the interpreter's folder, which goes with it.
        if (_pythonBridge is { } bridge)
        {
            await bridge.DisposeAsync();
        }
        if (_python is { } python)
        {
            await python.DisposeAsync();
        }
        (_python, _pythonBridge) = (null, null);
    }

    private async Task<ToolResult> RunCodeAsync(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var code = a.Str("code") is { Length: > 0 } given ? given : throw new ToolError("code is required.");
        var chat = Session?.Id ?? "";
        string? anew = null;
        if (_python is { } running && (a.Bool("fresh") == true || !running.Alive || running.Chat != chat))
        {
            anew = a.Bool("fresh") == true || running.Chat != chat ? "" : $"(The earlier interpreter had ended{(running.ExitCode is { } was ? $" with exit code {was}" : "")}: this is a new one, and what the earlier calls made is gone.)\n";
            await StopPythonAsync();
        }
        var callable = CodeTools().ToList();
        if (_python is null)
        {
            var dir = Path.Combine(Workspace.Root, ".code-arena", "py-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(dir, "arena"));
            using (var module = typeof(Runtime).Assembly.GetManifestResourceStream("arena/__init__.py")!)
            await using (var file = File.Create(Path.Combine(dir, "arena", "__init__.py")))
            {
                await module.CopyToAsync(file, ct);
            }
            var definitions = new JsonArray([.. callable.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["group"] = t.Server ?? "local", ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(),
            })]);
            await File.WriteAllTextAsync(Path.Combine(dir, "tools.json"), definitions.ToJsonString(), ct);
            var socket = Path.Combine(dir, "s");
            // Python has no Unix sockets on Windows, and a socket's path takes about 100 characters: TCP with a token then.
            _pythonBridge = new ToolBridge(OperatingSystem.IsWindows() || socket.Length > 100 ? null : socket, AnswerFromCode);
            var env = new Dictionary<string, string>
            {
                ["ARENA_SOCKET"] = _pythonBridge.Path,
                ["ARENA_TOOLS"] = Path.Combine(dir, "tools.json"),
                ["PYTHONPATH"] = dir + (Environment.GetEnvironmentVariable("PYTHONPATH") is { Length: > 0 } more ? Path.PathSeparator + more : ""),
            };
            if (_pythonBridge.Token is { } token)
            {
                env["ARENA_TOKEN"] = token;
            }
            try
            {
                _python = PythonSession.Start(PythonSession.Find(Config.Python, Workspace.Root, Env.Env), dir, Workspace.Root, chat,
                    Path.Combine(Workspace.Root, ".code-arena", "plots"), env, c.Sandbox.Wrap);
            }
            catch
            {
                await StopPythonAsync();
                throw;
            }
        }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Int("timeout_seconds") ?? 120, 1, 600));
        var made = _pythonBridge!.Calls.Count;
        _pythonCall = (c, callable, 0);
        PythonSession.Ran run;
        try
        {
            run = await _python.RunAsync(code, timeout, Math.Max(4_000, (c.ResultChars?.Invoke() ?? 30_000) - 1_000), ct);
        }
        catch (OperationCanceledException)
        {
            await StopPythonAsync();
            throw;
        }
        finally
        {
            _pythonCall = null;
        }
        var called = _pythonBridge.Calls.Skip(made).ToList();
        var calls = called.GroupBy(n => n).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}").ToList();
        var status = run.TimedOut ? $"timed out after {timeout.TotalSeconds:0} s: the interpreter was stopped, and its variables with it"
            : run.Ended ? $"the interpreter ended{(_python.ExitCode is { } x ? $" with exit code {x}" : "")}: the next call starts a new one"
            : run.Exit is { } exit ? $"exit code {exit} (sys.exit: the interpreter goes on, its variables kept)" : run.Ok ? "ok" : "error";
        var plots = run.Plots.Where(File.Exists).ToList();
        var shown = plots.Select(p => c.Workspace.Show(p)).ToList();
        var text = (anew ?? "") + (run.Output.Length > 0 ? run.Output.TrimEnd() + "\n" : "(no output)\n") +
            (shown.Count > 0 ? $"[{(shown.Count == 1 ? "plot" : "plots")} kept as {string.Join(", ", shown)}: shown to you below]\n" : "") +
            $"[{status}" + (calls.Count > 0 ? $"; calls: {string.Join(", ", calls)}" : "") + "]";
        if (run.Ended)
        {
            await StopPythonAsync();
        }
        JsonArray? pictures = plots.Count == 0 ? null : [.. plots.Take(4).Select(p => (JsonNode)new JsonObject
        {
            ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(p)) },
        })];
        return new ToolResult(text, !run.Ok)
        {
            Display = c.Ui.Dim($"{called.Count} tool call{(called.Count == 1 ? "" : "s")}{(shown.Count > 0 ? $" · {shown.Count} plot{(shown.Count == 1 ? "" : "s")}" : "")} · {status}"),
            Pictures = pictures,
        };
    }

    /// <summary>A tool a program in the interpreter called: checked as a direct call is, run with the context of the call running.</summary>
    private async Task<JsonObject> AnswerFromCode(string name, JsonObject args, CancellationToken token)
    {
        if (_pythonCall is not { } call)
        {
            return new JsonObject { ["ok"] = false, ["error"] = "The tools are called only while run_code runs (not from a thread left running after it)." };
        }
        if (call.Calls >= MostCodeCalls)
        {
            return new JsonObject { ["ok"] = false, ["error"] = $"A run makes at most {MostCodeCalls} calls." };
        }
        _pythonCall = call with { Calls = call.Calls + 1 };
        if (call.Tools.FirstOrDefault(t => t.Name == name) is not { } tool)
        {
            return new JsonObject { ["ok"] = false, ["error"] = $"There is no tool {name} to call from code (arena.tools.search finds them)." };
        }
        if (await Permissions.CheckAsync(tool, args, token) is { } refused)
        {
            return new JsonObject { ["ok"] = false, ["error"] = refused };
        }
        var c = call.Context;
        c.Ui.Line(c.Ui.Dim($"  ↳ {tool.Name} {Fmt.OneLine(tool.Summary?.Invoke(args), 70)}"));
        try
        {
            var result = await tool.Run(args, c, token);
            return result.Error ? new JsonObject { ["ok"] = false, ["error"] = result.Text } : new JsonObject { ["ok"] = true, ["result"] = result.Text };
        }
        catch (ToolError e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }
}
