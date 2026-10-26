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

    public string Path { get; }

    /// <summary>The calls answered, by name, in order.</summary>
    public List<string> Calls { get; } = [];

    public ToolBridge(string path, Func<string, JsonObject, CancellationToken, Task<JsonObject>> answer)
    {
        Path = path;
        _answer = answer;
        File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(4);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
        File.Delete(Path);
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

    /// <summary>The tools a program may call: every one but those that hand work on, wait on jobs, or run code themselves.</summary>
    private IEnumerable<ToolDef> CodeTools() =>
        Tools.All.Where(t => t.Kind != ToolKind.Agent && t.Name is not ("run_code" or "command_output" or "stop_command" or "ask_agent" or ToolDemand.Function));

    public static ToolDef RunCodeTool(Runtime rt) => new()
    {
        Name = "run_code",
        Kind = ToolKind.Shell,
        Description = "Run a Python program that calls this session's tools as functions, and get what it prints. In it, `import arena`: " +
            "arena.tools.search(\"words\") lists the functions that match (arena.local.read_file, arena.argus.find_symbol, arena.<server>.<tool>), " +
            "help(...) tells one's parameters, and each call (keyword arguments) returns the tool's result, a dict or a list when it is JSON; a refused " +
            $"call raises arena.ToolError. Each call is checked as a direct one is (the mode, the rules). At most {MostCodeCalls} calls; it runs in the " +
            "working directory, in the session's sandbox. Use it for many calls whose results you only need to filter or join; print only what you need.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "code":{"type":"string","description":"The whole program; print() what you need to read."},
              "timeout_seconds":{"type":"integer","description":"Stop it after this long (default 120, at most 600)."}},
             "required":["code"]}
            """),
        Summary = a => Fmt.OneLine(a.Str("code"), 80),
        Run = rt.RunCodeAsync,
    };

    private async Task<ToolResult> RunCodeAsync(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var code = a.Str("code") is { Length: > 0 } given ? given : throw new ToolError("code is required.");
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Workspace.Root, ".code-arena", "r", id);
        Directory.CreateDirectory(Path.Combine(dir, "arena"));
        var socket = Path.Combine(dir, "s");
        if (socket.Length > 100)
        {
            throw new ToolError("This folder's path is too long for code mode's socket (Unix sockets take about 100 characters): use run_shell, or tools one by one.");
        }
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "main.py"), code, ct);
            using (var module = typeof(Runtime).Assembly.GetManifestResourceStream("arena/__init__.py")!)
            await using (var file = File.Create(Path.Combine(dir, "arena", "__init__.py")))
            {
                await module.CopyToAsync(file, ct);
            }
            var callable = CodeTools().ToList();
            var definitions = new JsonArray([.. callable.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["group"] = t.Server ?? "local", ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(),
            })]);
            await File.WriteAllTextAsync(Path.Combine(dir, "tools.json"), definitions.ToJsonString(), ct);
            var calls = 0;
            await using var bridge = new ToolBridge(socket, async (name, args, token) =>
            {
                if (++calls > MostCodeCalls)
                {
                    return new JsonObject { ["ok"] = false, ["error"] = $"A program makes at most {MostCodeCalls} calls." };
                }
                if (callable.FirstOrDefault(t => t.Name == name) is not { } tool)
                {
                    return new JsonObject { ["ok"] = false, ["error"] = $"There is no tool {name} to call from code (arena.tools.search finds them)." };
                }
                if (await Permissions.CheckAsync(tool, args, token) is { } refused)
                {
                    return new JsonObject { ["ok"] = false, ["error"] = refused };
                }
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
            });
            var relative = Path.GetRelativePath(Workspace.Root, dir).Replace('\\', '/');
            var python = Config.Python ?? (OperatingSystem.IsWindows() ? "python" : "python3");
            var psi = LocalTools.ShellCommand($"{python} {relative}/main.py", c);
            psi.Environment["ARENA_SOCKET"] = socket;
            psi.Environment["ARENA_TOOLS"] = Path.Combine(dir, "tools.json");
            psi.Environment["PYTHONPATH"] = dir + (Environment.GetEnvironmentVariable("PYTHONPATH") is { Length: > 0 } more ? Path.PathSeparator + more : "");
            var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Int("timeout_seconds") ?? 120, 1, 600));
            var run = await Proc.RunAsync(psi, timeout, ct);
            var status = run.TimedOut ? $"timed out after {timeout.TotalSeconds:0} s (stopped)" : $"exit code {run.ExitCode}";
            var made = bridge.Calls.GroupBy(n => n).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}").ToList();
            var text = (run.Output.Length > 0 ? run.Output.TrimEnd() + "\n" : "(no output)\n") + $"[{status}" + (made.Count > 0 ? $"; calls: {string.Join(", ", made)}" : "") + "]";
            return new ToolResult(text, run.ExitCode != 0 || run.TimedOut)
            {
                Display = c.Ui.Dim($"{bridge.Calls.Count} call{(bridge.Calls.Count == 1 ? "" : "s")} · {status}"),
            };
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
