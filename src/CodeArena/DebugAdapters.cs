using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// A debug adapter as config.json's "debugAdapters" names it: the command that starts it (with <c>${port}</c> in an
/// argument, it listens on that port), or the port of one already listening on 127.0.0.1. An entry that is neither keeps
/// what is wrong with it, and is kept as written when the file is saved.
/// </summary>
internal sealed record DebugAdapterConfig(IReadOnlyList<string> Command, int? Port, string? Problem, JsonNode? Raw)
{
    public static DebugAdapterConfig From(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (o["command"] is JsonArray a && a.Count > 0 && a.All(x => x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0))
            {
                return new([.. a.Select(x => x!.GetValue<string>())], null, null, o.DeepClone());
            }
            if (o.Int("port") is { } port && port is > 0 and < 65536)
            {
                return new([], port, null, o.DeepClone());
            }
        }
        return new([], null, "give \"command\" (the program and its arguments, \"${port}\" where it takes a port to listen on) or \"port\" (an adapter already listening on 127.0.0.1)", node?.DeepClone());
    }
}

/// <summary>One of the debug adapters Code Arena knows: its id, its name, and the launch.json types it serves.</summary>
internal sealed record DebugAdapterKind(string Id, string Name, string[] Types);

/// <summary>
/// An adapter found on this computer: the program and arguments that start it (<c>${port}</c> where it takes a port
/// to listen on) and what its environment adds, or the port of one already running; where it was found, and its version.
/// </summary>
internal sealed record DebugAdapter(DebugAdapterKind Kind, string Program, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?> Env, int? Port, string Path, string? Version)
{
    /// <summary>Whether it is told a port to listen on, and reached over TCP.</summary>
    public bool Listens => Args.Any(a => a.Contains(DebugAdapters.PortVariable, StringComparison.Ordinal));
}

/// <summary>A launch configuration put in the words of the adapter that runs it: which adapter, its DAP type (initialize's adapterID), the configuration, what was changed or left out, and the adapter program the configuration names, if any.</summary>
internal sealed record DebugLaunch(string AdapterId, string Type, JsonObject Configuration, IReadOnlyList<string> Notes, string? Program);

/// <summary>
/// The debug adapters Code Arena can start, how each is found, and how a launch.json configuration is put in its
/// words. An adapter is looked for in config.json's "debugAdapters" first, then in the data folder
/// (<c>adapters/ID/current</c>), then on PATH, then where its usual installs put it. Each program found is checked once
/// for as long as the file is unchanged (gdb must be 14 or later: its DAP came then); one config.json names is taken as given.
/// </summary>
internal sealed partial class DebugAdapters(Config config, string dataDir, Func<string, string?> env)
{
    /// <summary>In an adapter's arguments: the port it is told to listen on.</summary>
    public const string PortVariable = "${port}";

    /// <summary>The longest a check (a --version) runs.</summary>
    private static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(5);

    public static readonly IReadOnlyList<DebugAdapterKind> Catalog =
    [
        new("gdb", "GDB", ["cppdbg", "gdb"]),
        new("lldb-dap", "LLDB", ["cppdbg", "lldb", "codelldb", "lldb-dap"]),
        new("netcoredbg", "netcoredbg (.NET)", ["coreclr"]),
        new("debugpy", "debugpy (Python)", ["python", "debugpy"]),
        new("dlv", "Delve (Go)", ["go"]),
        new("js-debug", "js-debug (Node.js)", ["node", "pwa-node"]),
    ];

    /// <summary>The launch.json types no adapter here serves, and why.</summary>
    public static readonly IReadOnlyDictionary<string, string> Unsupported = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cppvsdbg"] = "Microsoft's Windows debugger (vsdbg) may be used with Microsoft's own products only: debug MSVC builds in Visual Studio, or build with MinGW or clang and use cppdbg with gdb or lldb.",
        ["chrome"] = "Debugging in a browser is not supported here.",
        ["msedge"] = "Debugging in a browser is not supported here.",
        ["pwa-chrome"] = "Debugging in a browser is not supported here.",
        ["pwa-msedge"] = "Debugging in a browser is not supported here.",
        ["node-terminal"] = "A JavaScript Debug Terminal is VS Code's own: use a node configuration.",
        ["extensionHost"] = "VS Code's extension host runs only in VS Code.",
        ["pwa-extensionHost"] = "VS Code's extension host runs only in VS Code.",
        ["java"] = "There is no Java debug adapter here.",
    };

    // VS Code's own keys, not the adapter's.
    private static readonly string[] VsCodeOnly = ["preLaunchTask", "postDebugTask", "presentation", "internalConsoleOptions", "debugServer", "serverReadyAction", "windows", "linux", "osx"];

    private readonly Dictionary<string, (string? Version, string? Problem)> _checked = new(StringComparer.Ordinal);

    public static DebugAdapterKind? Kind(string? id) => Catalog.FirstOrDefault(k => k.Id == id);

    /// <summary>How a person gets the adapter, for the page to show when it is missing.</summary>
    public string Install(string id)
    {
        var own = Path.Combine(dataDir, "adapters", id, "current");
        return id switch
        {
            "gdb" => "Install gdb 14 or later, built with Python (apt install gdb, dnf install gdb, brew install gdb, MSYS2: pacman -S mingw-w64-ucrt-x86_64-gdb), or name it in config.json: \"debugAdapters\": {\"gdb\": {\"command\": [\"/path/to/gdb\", \"-i\", \"dap\"]}}.",
            "lldb-dap" => "Install LLVM 18 or later, which has lldb-dap (apt install lldb-18, brew install llvm; Xcode 16 has it too), or name it in config.json: \"debugAdapters\": {\"lldb-dap\": {\"command\": [\"/path/to/lldb-dap\"]}}.",
            "netcoredbg" => $"Unpack a netcoredbg release (github.com/Samsung/netcoredbg/releases) into {own}, or name it in config.json: \"debugAdapters\": {{\"netcoredbg\": {{\"command\": [\"/path/to/netcoredbg\", \"--interpreter=vscode\"]}}}}.",
            "debugpy" => "Install debugpy for your Python: python3 -m pip install debugpy (in your project's virtual environment, or for the python3 on PATH).",
            "dlv" => "Install Delve: go install github.com/go-delve/delve/cmd/dlv@latest (it lands in ~/go/bin).",
            "js-debug" => $"Unpack a js-debug-dap release (github.com/microsoft/vscode-js-debug/releases) into {own}; it needs node 18 or later on PATH.",
            _ => "",
        };
    }

    /// <summary>The adapter with this id as found here, or why there is none.</summary>
    public async Task<(DebugAdapter? Found, string? Reason)> FindAsync(string id, CancellationToken ct)
    {
        var kind = Kind(id) ?? throw new ArgumentException($"No debug adapter {id}.", nameof(id));
        if (config.DebugAdapters.TryGetValue(id, out var own))
        {
            if (own.Problem is { } problem)
            {
                return (null, $"\"debugAdapters\".\"{id}\" in config.json: {problem}.");
            }
            if (own.Port is { } port)
            {
                return (new DebugAdapter(kind, "", [], Empty, port, $"127.0.0.1:{port}", null), null);
            }
            if (Locate(own.Command[0]) is not { } program)
            {
                return (null, $"{own.Command[0]} (\"debugAdapters\" in config.json) is not there.");
            }
            return await CheckedAsync(new Candidate(kind, program, [.. own.Command.Skip(1)], Empty), ct, given: true);
        }
        string? first = null;
        foreach (var candidate in Candidates(kind))
        {
            var (found, reason) = await CheckedAsync(candidate, ct, given: false);
            if (found is not null)
            {
                return (found, null);
            }
            first ??= reason;
        }
        return (null, first ?? $"{kind.Name} is not installed.");
    }

    /// <summary>Every adapter of the catalog, as the page lists them: <c>[{id, name, types, found, path, version, reason, install}]</c>.</summary>
    public async Task<JsonArray> ListAsync(CancellationToken ct)
    {
        var all = new JsonArray();
        foreach (var kind in Catalog)
        {
            var (found, reason) = await FindAsync(kind.Id, ct);
            all.Add(new JsonObject
            {
                ["id"] = kind.Id,
                ["name"] = kind.Name,
                ["types"] = new JsonArray([.. kind.Types.Select(t => (JsonNode)t)]),
                ["found"] = found is not null,
                ["path"] = found?.Path,
                ["version"] = found?.Version,
                ["reason"] = reason,
                ["install"] = found is null ? Install(kind.Id) : null,
            });
        }
        return all;
    }

    private static readonly IReadOnlyDictionary<string, string?> Empty = new Dictionary<string, string?>();

    /// <summary>A program that may be the adapter: where it is, its arguments, and what its environment adds.</summary>
    private sealed record Candidate(DebugAdapterKind Kind, string Program, string[] Args, IReadOnlyDictionary<string, string?> Env);

    /// <summary>Where the adapter may be, in the order looked: the data folder, PATH, then the usual installs.</summary>
    private IEnumerable<Candidate> Candidates(DebugAdapterKind kind)
    {
        var current = Path.Combine(dataDir, "adapters", kind.Id, "current");
        var home = env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> Programs(string name, IEnumerable<string> known) =>
            new[] { Path.Combine(current, "bin", Exe(name)), Path.Combine(current, Exe(name)), Path.Combine(current, name, Exe(name)) }
                .Where(File.Exists)
                .Concat(OnPath(Exe(name)) is { } onPath ? [onPath] : [])
                .Concat(known.Where(File.Exists));
        switch (kind.Id)
        {
            case "gdb":
                return Programs("gdb", ["/opt/homebrew/bin/gdb", "/usr/local/bin/gdb"]).Select(p => new Candidate(kind, p, ["-q", "-i", "dap"], Empty));
            case "lldb-dap":
                var llvm = new[] { "/usr/lib", "/usr/local/opt", "/opt/homebrew/opt" }
                    .SelectMany(d => Listed(() => Directory.EnumerateDirectories(d, "llvm*")))
                    .OrderByDescending(d => d, StringComparer.Ordinal)
                    .SelectMany(d => new[] { Path.Combine(d, "bin", "lldb-dap"), Path.Combine(d, "bin", "lldb-vscode") });
                var apple = new[] { "/Applications/Xcode.app/Contents/Developer/usr/bin/lldb-dap", "/Library/Developer/CommandLineTools/usr/bin/lldb-dap" };
                return Programs("lldb-dap", [])
                    .Concat(Programs("lldb-vscode", []))
                    .Concat(Versioned("lldb-dap"))
                    .Concat(llvm.Concat(apple).Where(File.Exists))
                    .Select(p => new Candidate(kind, p, [], Empty));
            case "netcoredbg":
                return Programs("netcoredbg", []).Select(p => new Candidate(kind, p, ["--interpreter=vscode"], Empty));
            case "dlv":
                var go = new[] { env("GOBIN"), env("GOPATH") is { Length: > 0 } gopath ? Path.Combine(gopath, "bin") : null, Path.Combine(home, "go", "bin") }
                    .OfType<string>().Where(d => d.Length > 0).Select(d => Path.Combine(d, Exe("dlv")));
                return Programs("dlv", go).Select(p => new Candidate(kind, p, ["dap", $"--listen=127.0.0.1:{PortVariable}"], Empty));
            case "debugpy":
                var pythons = new[] { "python3", "python" }.Select(n => OnPath(Exe(n))).OfType<string>()
                    .Concat(env("VIRTUAL_ENV") is { Length: > 0 } venv ? [Path.Combine(venv, OperatingSystem.IsWindows() ? "Scripts" : "bin", Exe("python"))] : [])
                    .Where(File.Exists).Distinct().ToList();
                // A debugpy unpacked in the data folder runs on the first Python found.
                var unpacked = Directory.Exists(Path.Combine(current, "debugpy")) && pythons.Count > 0
                    ? [new Candidate(kind, pythons[0], ["-m", "debugpy.adapter"], new Dictionary<string, string?> { ["PYTHONPATH"] = current })]
                    : Array.Empty<Candidate>();
                return unpacked.Concat(pythons.Select(p => new Candidate(kind, p, ["-m", "debugpy.adapter"], Empty)));
            case "js-debug":
                var script = new[] { Path.Combine(current, "src", "dapDebugServer.js"), Path.Combine(current, "js-debug", "src", "dapDebugServer.js") }.FirstOrDefault(File.Exists);
                return script is not null && OnPath(Exe("node")) is { } node
                    ? [new Candidate(kind, node, [script, PortVariable, "127.0.0.1"], Empty)]
                    : [];
            default:
                return [];
        }
    }

    /// <summary>lldb-dap-19, lldb-dap-18 … on PATH, as LLVM's Debian packages name them, the newest first.</summary>
    private IEnumerable<string> Versioned(string name) =>
        (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathFullyQualified)
            .SelectMany(d => Listed(() => Directory.EnumerateFiles(d, name + "-*")))
            .Where(f => int.TryParse(Path.GetFileName(f)[(name.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .OrderByDescending(f => int.Parse(Path.GetFileName(f)[(name.Length + 1)..], CultureInfo.InvariantCulture));

    /// <summary>A folder's listing; none when it is not there or not readable.</summary>
    private static string[] Listed(Func<IEnumerable<string>> list)
    {
        try
        {
            return [.. list()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>A program as a full path: as given when it has a folder, else found on PATH.</summary>
    private string? Locate(string program)
    {
        if (Path.IsPathRooted(program) || program.Contains('/') || program.Contains('\\'))
        {
            var full = Path.GetFullPath(program);
            return File.Exists(full) ? full : null;
        }
        return OnPath(program) ?? (OperatingSystem.IsWindows() ? OnPath(Exe(program)) : null);
    }

    /// <summary>
    /// A program found in PATH's folders. Only full ones: "." or "bin" in PATH would find the project's own program
    /// of that name, and listing the adapters runs what is found.
    /// </summary>
    private string? OnPath(string name) =>
        (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);

    /// <summary>The candidate with its version, once checked; or why it will not do (one config.json names is taken as given).</summary>
    private async Task<(DebugAdapter? Found, string? Reason)> CheckedAsync(Candidate c, CancellationToken ct, bool given)
    {
        var (args, judge) = Check(c);
        var key = $"{c.Program}\n{File.GetLastWriteTimeUtc(c.Program).Ticks}\n{string.Join('\n', args)}\n{string.Join('\n', c.Env.Select(e => $"{e.Key}={e.Value}"))}";
        (string? Version, string? Problem) result;
        bool known;
        lock (_checked)
        {
            known = _checked.TryGetValue(key, out result);
        }
        if (!known)
        {
            result = judge(await RunAsync(c.Program, args, c.Env, ct));
            lock (_checked)
            {
                _checked[key] = result;
            }
        }
        return result.Problem is { } problem && !given
            ? (null, problem)
            : (new DebugAdapter(c.Kind, c.Program, c.Args, c.Env, null, c.Program, result.Version), null);
    }

    /// <summary>How a candidate is checked: the arguments to run it with, and what its answer says (its version, or why it will not do).</summary>
    private static (string[] Args, Func<(int? Code, string Output), (string? Version, string? Problem)> Judge) Check(Candidate c) => c.Kind.Id switch
    {
        "gdb" => (["--version"], r => Version(r.Output) is { } v
            ? Major(v) >= 14 ? (v, null) : (v, $"{c.Program} is gdb {v}: its debug adapter (gdb -i dap) needs gdb 14 or later.")
            : (null, $"{c.Program} did not say its version: {Fmt.OneLine(r.Output, 200)}")),
        "debugpy" => (["-c", "import debugpy; print(debugpy.__version__)"], r => r.Code == 0 && Version(r.Output) is { } v
            ? (v, null)
            : (null, $"debugpy is not installed for {c.Program}.")),
        "js-debug" => (["--version"], r => Version(r.Output) is { } v && Major(v) >= 18
            ? (v, null)
            : (null, $"js-debug needs node 18 or later; {c.Program} is {Fmt.OneLine(r.Output, 40)}.")),
        "dlv" => (["version"], r => (Version(r.Output), null)),
        _ => (["--version"], r => (r.Code == 0 ? Version(r.Output) : null, null)),
    };

    /// <summary>The last version number on the first line that has one ("GNU gdb (GDB) 17.1" is 17.1).</summary>
    private static string? Version(string output) =>
        output.Split('\n').Select(l => VersionPattern().Matches(l)).FirstOrDefault(m => m.Count > 0) is { } matches ? matches[^1].Value : null;

    private static int Major(string version) => int.TryParse(version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    [GeneratedRegex(@"(?<![\d.])\d+\.\d+(?:\.\d+)*")]
    private static partial Regex VersionPattern();

    /// <summary>A program's exit code and output; no code when it could not be run or did not end in time.</summary>
    private async Task<(int? Code, string Output)> RunAsync(string program, string[] args, IReadOnlyDictionary<string, string?> extra, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDir);
        // Not the project's folder: a Python run there would import the project's own modules first.
        var psi = new ProcessStartInfo(program) { WorkingDirectory = dataDir };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in extra)
        {
            psi.Environment[name] = value;
        }
        try
        {
            var result = await Proc.RunAsync(psi, CheckLimit, ct, 4_000);
            return (result.TimedOut ? null : result.ExitCode, result.Output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ToolError)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// A configuration as launch.json has it (variables already filled in) in the words of the adapter that serves its
    /// type. Throws IdeError 400: <c>unsupported</c> for a type no adapter here serves (with why), <c>bad_config</c> for
    /// one that cannot run. <paramref name="trusted"/>: the folder is trusted, so an adapter program the configuration
    /// names (miDebuggerPath) may run.
    /// </summary>
    public static DebugLaunch Translate(JsonObject configuration, string root, bool trusted)
    {
        var type = configuration.Str("type") ?? throw new IdeError(400, "bad_config", "The configuration has no \"type\".");
        if (Unsupported.TryGetValue(type, out var why))
        {
            throw new IdeError(400, "unsupported", why);
        }
        var request = configuration.Str("request");
        if (request is not ("launch" or "attach"))
        {
            throw new IdeError(400, "bad_config", "The configuration's \"request\" is \"launch\" or \"attach\".");
        }
        var c = configuration.Clone();
        foreach (var key in VsCodeOnly)
        {
            c.Remove(key);
        }
        if (request == "launch" && c.Str("cwd") is null)
        {
            c["cwd"] = root;
        }
        var notes = new List<string>();
        string? program = null;
        // Which adapter, and the type it is told (initialize's adapterID).
        string adapter, dapType;
        switch (type)
        {
            case "cppdbg":
                var lldb = (c.Str("MIMode") ?? (OperatingSystem.IsMacOS() ? "lldb" : "gdb")) == "lldb";
                (adapter, dapType) = lldb ? ("lldb-dap", "lldb-dap") : ("gdb", "gdb");
                c = lldb ? CppdbgToLldb(c, notes) : CppdbgToGdb(c, notes);
                if (!lldb && configuration.Str("miDebuggerPath") is { Length: > 0 } gdb)
                {
                    if (trusted)
                    {
                        program = gdb;
                    }
                    else
                    {
                        notes.Add($"miDebuggerPath ({gdb}) is used only in a trusted folder: the gdb found here runs instead.");
                    }
                }
                break;
            case "gdb":
                (adapter, dapType) = ("gdb", "gdb");
                // The Native Debug extension's words for the same.
                if (c.Str("program") is null && c.Str("target") is { } target)
                {
                    c["program"] = target;
                    c.Remove("target");
                }
                if (c["args"] is null && c.Str("arguments") is { } arguments)
                {
                    c["args"] = new JsonArray([.. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => (JsonNode)a)]);
                    c.Remove("arguments");
                }
                break;
            case "lldb" or "codelldb":
                (adapter, dapType) = ("lldb-dap", "lldb-dap");
                c = CodeLldbToLldb(c, notes);
                break;
            case "lldb-dap" or "lldb-vscode":
                (adapter, dapType) = ("lldb-dap", "lldb-dap");
                break;
            case "coreclr":
                (adapter, dapType) = ("netcoredbg", "coreclr");
                if (c.Remove("console"))
                {
                    notes.Add("console: the program's output goes to the Debug Console.");
                }
                break;
            case "python" or "debugpy":
                (adapter, dapType) = ("debugpy", "debugpy");
                break;
            case "go":
                (adapter, dapType) = ("dlv", "go");
                break;
            case "node" or "pwa-node":
                (adapter, dapType) = ("js-debug", "pwa-node");
                break;
            default:
                throw new IdeError(400, "unsupported", $"No debug adapter here serves \"{type}\" configurations.");
        }
        c["type"] = dapType;
        ScrubKey(c);
        return new DebugLaunch(adapter, dapType, c, notes, program);
    }

    /// <summary>cppdbg (the C/C++ extension's) for gdb's own adapter.</summary>
    private static JsonObject CppdbgToGdb(JsonObject c, List<string> notes)
    {
        var gdb = Common(c);
        if (c["environment"] is JsonArray environment)
        {
            gdb["env"] = EnvOf(environment);
        }
        if (c.Bool("stopAtEntry") == true)
        {
            gdb["stopAtBeginningOfMainSubprogram"] = true;
        }
        if (c["processId"] is { } pid)
        {
            gdb["pid"] = Json.Number(pid);
        }
        if (c["setupCommands"] is JsonArray { Count: > 0 })
        {
            notes.Add("setupCommands are not run: gdb's own debug adapter takes no commands.");
        }
        if (c.Str("miDebuggerServerAddress") is { } remote)
        {
            gdb["target"] = remote;
        }
        return gdb;
    }

    /// <summary>cppdbg with MIMode lldb, for lldb-dap.</summary>
    private static JsonObject CppdbgToLldb(JsonObject c, List<string> notes)
    {
        var lldb = Common(c);
        if (c["environment"] is JsonArray environment)
        {
            lldb["env"] = Pairs(EnvOf(environment));
        }
        if (c.Bool("stopAtEntry") == true)
        {
            lldb["stopOnEntry"] = true;
        }
        if (c["processId"] is { } pid)
        {
            lldb["pid"] = Json.Number(pid);
        }
        if (c["setupCommands"] is JsonArray setup)
        {
            lldb["initCommands"] = new JsonArray([.. setup.Select(s => s.Str("text")).OfType<string>().Select(t => (JsonNode)t)]);
        }
        if (c.Str("miDebuggerPath") is not null)
        {
            notes.Add("miDebuggerPath is for gdb: lldb-dap runs instead.");
        }
        return lldb;
    }

    /// <summary>CodeLLDB's lldb configurations, for lldb-dap.</summary>
    private static JsonObject CodeLldbToLldb(JsonObject c, List<string> notes)
    {
        if (c["cargo"] is not null && c["program"] is null)
        {
            throw new IdeError(400, "bad_config", "CodeLLDB's \"cargo\" builds are not run here yet: build first, and give the program.");
        }
        var lldb = c.Clone();
        lldb.Remove("cargo");
        if (c["env"] is JsonObject env)
        {
            lldb["env"] = Pairs(env);
        }
        if (c["args"] is JsonValue line && line.TryGetValue<string>(out var text))
        {
            lldb["args"] = new JsonArray([.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => (JsonNode)a)]);
        }
        if (c["sourceMap"] is JsonObject map)
        {
            lldb["sourceMap"] = new JsonArray([.. map.Select(m => (JsonNode)new JsonArray(m.Key, m.Value?.DeepClone()))]);
        }
        foreach (var key in new[] { "terminal", "console", "expressions", "sourceLanguages" })
        {
            if (lldb.Remove(key))
            {
                notes.Add($"{key} is CodeLLDB's own: left out.");
            }
        }
        return lldb;
    }

    /// <summary>What every C and C++ adapter takes as cppdbg has it: the name, the request, the program, its arguments and folder.</summary>
    private static JsonObject Common(JsonObject c)
    {
        var o = new JsonObject();
        foreach (var key in new[] { "name", "type", "request", "program", "args", "cwd" })
        {
            if (c[key] is { } value)
            {
                o[key] = value.DeepClone();
            }
        }
        return o;
    }

    /// <summary>cppdbg's [{name, value}] as an object.</summary>
    private static JsonObject EnvOf(JsonArray environment)
    {
        var env = new JsonObject();
        foreach (var e in environment)
        {
            if (e.Str("name") is { Length: > 0 } name)
            {
                env[name] = e.Str("value") ?? "";
            }
        }
        return env;
    }

    /// <summary>An environment object as lldb-dap's "NAME=value" strings.</summary>
    private static JsonArray Pairs(JsonObject env) =>
        new([.. env.Select(e => (JsonNode)$"{e.Key}={(e.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : e.Value?.ToJsonString())}")]);

    /// <summary>The person's key never reaches the program debugged, whatever the configuration says.</summary>
    private static void ScrubKey(JsonObject c)
    {
        switch (c["env"])
        {
            case JsonObject env:
                env.Remove("ARENA_API_KEY");
                break;
            case JsonArray pairs:
                foreach (var pair in pairs.Where(p => p is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("ARENA_API_KEY=", StringComparison.Ordinal)).ToList())
                {
                    pairs.Remove(pair);
                }
                break;
        }
    }
}
