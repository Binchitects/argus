using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CodeArena;

/// <summary>
/// A language server as config.json's "languageServers" names it: the command that starts it (the program, then its
/// arguments), the languages it serves (Monaco's ids), what its initialize gets as initializationOptions, the settings
/// the page answers it with, and what its environment adds (null takes a variable away); "disabled" turns a built-in
/// one off. An entry named after a built-in changes only what it gives. An entry that cannot be used keeps what is wrong
/// with it, and is kept as written when the file is saved.
/// </summary>
internal sealed record LanguageServerConfig(
    IReadOnlyList<string> Command,
    IReadOnlyList<string>? Languages,
    JsonObject? InitializationOptions,
    JsonObject? Settings,
    IReadOnlyDictionary<string, string?> Env,
    bool Disabled,
    string? Problem,
    JsonNode? Raw)
{
    public static LanguageServerConfig From(JsonNode? node)
    {
        const string Shape = "give \"command\" (the program and its arguments), \"languages\" (Monaco's language ids), and if it needs them \"initializationOptions\", \"settings\" and \"env\" (objects)";
        if (node is not JsonObject o)
        {
            return Broken(Shape, node);
        }
        static List<string>? Strings(JsonNode? n) =>
            n is JsonArray a && a.All(x => x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) ? [.. a.Select(x => x!.GetValue<string>())] : null;
        var command = o["command"] is null ? [] : Strings(o["command"]);
        var languages = o["languages"] is null ? null : Strings(o["languages"]);
        if (command is null || (o["languages"] is not null && languages is null))
        {
            return Broken(Shape, o);
        }
        if (o["initializationOptions"] is { } options and not JsonObject || o["settings"] is { } settings and not JsonObject || o["env"] is { } e and not JsonObject)
        {
            return Broken("\"initializationOptions\", \"settings\" and \"env\" are objects", o);
        }
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in o["env"] as JsonObject ?? [])
        {
            if (value is null)
            {
                env[name] = null;
            }
            else if (value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                env[name] = s;
            }
            else
            {
                return Broken($"\"env\".\"{name}\" is a string (or null, to take it away)", o);
            }
        }
        return new(command, languages, (o["initializationOptions"] as JsonObject)?.Clone(), (o["settings"] as JsonObject)?.Clone(), env, o.Bool("disabled") == true, null, o.DeepClone());
    }

    private static LanguageServerConfig Broken(string problem, JsonNode? raw) => new([], null, null, null, new Dictionary<string, string?>(), false, problem, raw?.DeepClone());
}

/// <summary>
/// One of the language servers Code Arena knows: its id, its name, the languages it serves (Monaco's ids), the programs
/// that may be it in the order looked for (each with its arguments), what its environment gets, how a person installs
/// it, and the arguments that make it say its version (none: it is not asked).
/// </summary>
internal sealed record LanguageServerKind(string Id, string Name, string[] Languages, (string Program, string[] Args)[] Programs, IReadOnlyDictionary<string, string?> Env, string Install, string[]? VersionArgs = null);

/// <summary>
/// A language server found here, ready to start: the program and arguments, what its environment adds, the options its
/// initialize gets, the settings the page answers it with, its version when it said one, and whether config.json named it.
/// </summary>
internal sealed record FoundLanguageServer(string Id, string Name, string[] Languages, string Program, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?> Env, JsonObject InitializationOptions, JsonObject Settings, string? Version, bool Given);

/// <summary>
/// The IDE's language servers: the ones found on this computer (in PATH's full folders, where their usual installs put
/// them, and for Python in the project's virtual environment) or named in config.json's "languageServers", each started
/// for a page that opens its WebSocket. A server runs the folder's code (tsserver plugins, build scripts, go list), so
/// none starts until the folder is trusted. Each runs in the folder, without the person's key, and in the agent's sandbox
/// where the system has one (the folder and the build tools' caches writable, secrets hidden), without the network: the
/// catalog's servers are also told not to fetch (no type packages, no Go modules, no crates, no schemas). At most six run
/// at once, one per server: a page that opens a server's socket takes it over from the page that had it (that one is
/// told, and its server stopped; a page's documents and the server's are kept in step for one page only). A page that
/// goes stops its servers (a page reloaded starts them again). Nothing is installed: a server that is missing says how
/// to get it.
/// </summary>
internal sealed partial class LanguageServers(Workspace workspace, Config config, string dataDir, Func<string, string?> env, Func<Sandbox> sandbox, Func<bool> trusted) : IAsyncDisposable
{
    /// <summary>The most servers running at once.</summary>
    public const int Max = 6;

    /// <summary>The most a page sends in one message (a file of 5 MB opened, and then some).</summary>
    public const int MaxPageMessage = 16 * 1024 * 1024;

    /// <summary>The most a server sends in one message.</summary>
    public const int MaxServerMessage = 64 * 1024 * 1024;

    /// <summary>How long a server stopping has to end by itself (shutdown, then exit) before it is killed.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlyDictionary<string, string?> NoEnv = new Dictionary<string, string?>();
    private const string WebLanguageServers = "npm install -g vscode-langservers-extracted";

    public static readonly IReadOnlyList<LanguageServerKind> Catalog =
    [
        new("typescript", "typescript-language-server", ["typescript", "javascript"], [("typescript-language-server", ["--stdio"])], NoEnv, "npm install -g typescript-language-server typescript", ["--version"]),
        new("pyright", "Pyright", ["python"], [("pyright-langserver", ["--stdio"])], NoEnv, "npm install -g pyright (or pip install pyright)"),
        new("basedpyright", "basedpyright", ["python"], [("basedpyright-langserver", ["--stdio"])], NoEnv, "pip install basedpyright"),
        new("pylsp", "Python LSP Server", ["python"], [("pylsp", [])], NoEnv, "pip install python-lsp-server (in the project's virtual environment, or for the python3 on PATH)"),
        // Nothing fetched: go list and the module cache only, as they are.
        new("gopls", "gopls", ["go"], [("gopls", [])], new Dictionary<string, string?> { ["GOPROXY"] = "off" }, "go install golang.org/x/tools/gopls@latest (it lands in ~/go/bin)", ["version"]),
        new("rust-analyzer", "rust-analyzer", ["rust"], [("rust-analyzer", [])], new Dictionary<string, string?> { ["CARGO_NET_OFFLINE"] = "true" }, "rustup component add rust-analyzer", ["--version"]),
        new("clangd", "clangd", ["c", "cpp"], [("clangd", [])], NoEnv, OperatingSystem.IsWindows() ? "winget install LLVM.LLVM" : OperatingSystem.IsMacOS() ? "brew install llvm" : "apt install clangd (dnf install clang-tools-extra)", ["--version"]),
        new("csharp-ls", "csharp-ls", ["csharp"], [("csharp-ls", [])], NoEnv, "dotnet tool install --global csharp-ls"),
        new("json", "vscode-json-language-server", ["json"], [("vscode-json-language-server", ["--stdio"])], NoEnv, WebLanguageServers),
        new("css", "vscode-css-language-server", ["css", "scss", "less"], [("vscode-css-language-server", ["--stdio"])], NoEnv, WebLanguageServers),
        new("html", "vscode-html-language-server", ["html"], [("vscode-html-language-server", ["--stdio"])], NoEnv, WebLanguageServers),
        new("yaml", "yaml-language-server", ["yaml"], [("yaml-language-server", ["--stdio"])], NoEnv, "npm install -g yaml-language-server"),
        new("bash", "bash-language-server", ["shell"], [("bash-language-server", ["start"])], NoEnv, "npm install -g bash-language-server"),
    ];

    private readonly object _gate = new();
    // The server of each id running for a page, and the last one of each (running or not), for its log.
    private readonly Dictionary<string, LanguageServerRun> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LanguageServerRun> _last = new(StringComparer.Ordinal);
    // Why a server's last start was refused (past the trust), until one starts.
    private readonly Dictionary<string, string> _refused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _versions = new(StringComparer.Ordinal);
    private bool _disposed;

    public static LanguageServerKind? Kind(string? id) => Catalog.FirstOrDefault(k => k.Id == id);

    /// <summary>A server as config.json and the catalog make it: its id, its name, its languages, and what each of them gives.</summary>
    private sealed record Entry(string Id, string Name, string[] Languages, LanguageServerKind? Kind, LanguageServerConfig? Own);

    /// <summary>config.json's own servers first, in its order (they come before the built-in ones for their languages), then the catalog's with config.json's entry of each over it.</summary>
    private List<Entry> Entries()
    {
        var all = new List<Entry>();
        foreach (var (id, own) in config.LanguageServers.Where(s => Kind(s.Key) is null))
        {
            all.Add(new Entry(id, id, [.. own.Languages ?? []], null, own));
        }
        foreach (var kind in Catalog)
        {
            var own = config.LanguageServers.GetValueOrDefault(kind.Id);
            all.Add(new Entry(kind.Id, kind.Name, [.. own?.Languages ?? kind.Languages], kind, own));
        }
        return all;
    }

    /// <summary>How a person gets a server, for the page to show when it is missing.</summary>
    public static string Install(string id) =>
        Kind(id)?.Install is { } how
            ? $"{how}, or name it in config.json: \"languageServers\": {{\"{id}\": {{\"command\": [\"/path/to/{Kind(id)!.Programs[0].Program}\"{string.Concat(Kind(id)!.Programs[0].Args.Select(a => $", \"{a}\""))}]}}}}."
            : $"Give its command in config.json: \"languageServers\": {{\"{id}\": {{\"command\": [...], \"languages\": [...]}}}}.";

    /// <summary>
    /// Every server, as the page lists them: <c>[{id, name, languages, found, path, version, reason, install, settings}]</c>,
    /// config.json's own first. The page starts, for a language, the first found that the person has not turned off.
    /// </summary>
    public async Task<JsonArray> ListAsync(CancellationToken ct)
    {
        var all = new JsonArray();
        foreach (var entry in Entries())
        {
            var (found, reason) = await FindAsync(entry, ct);
            all.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["name"] = entry.Name,
                ["languages"] = new JsonArray([.. entry.Languages.Select(l => (JsonNode)l)]),
                ["found"] = found is not null,
                ["path"] = found?.Program,
                ["version"] = found?.Version,
                ["reason"] = reason,
                ["install"] = found is null && entry.Own?.Disabled != true ? Install(entry.Id) : null,
                ["settings"] = found?.Settings.Clone(),
            });
        }
        return all;
    }

    /// <summary>The server with this id as found here, or why there is none; null for an id no entry has.</summary>
    private async Task<(FoundLanguageServer? Found, string? Reason)?> FindAsync(string? id, CancellationToken ct) =>
        Entries().FirstOrDefault(e => e.Id == id) is { } entry ? await FindAsync(entry, ct) : null;

    private async Task<(FoundLanguageServer? Found, string? Reason)> FindAsync(Entry entry, CancellationToken ct)
    {
        var own = entry.Own;
        if (own?.Problem is { } problem)
        {
            return (null, $"\"languageServers\".\"{entry.Id}\" in config.json: {problem}.");
        }
        if (own?.Disabled == true)
        {
            return (null, $"Turned off in config.json (\"languageServers\".\"{entry.Id}\".\"disabled\").");
        }
        if (entry.Languages.Length == 0)
        {
            return (null, $"\"languageServers\".\"{entry.Id}\" in config.json names no \"languages\" (Monaco's language ids: \"lua\", \"java\"…).");
        }
        string program;
        IReadOnlyList<string> args;
        var given = own is { Command.Count: > 0 };
        if (given)
        {
            if (Locate(own!.Command[0]) is not { } located)
            {
                return (null, $"{own.Command[0]} (\"languageServers\" in config.json) is not there: give its full path, or a name on PATH.");
            }
            (program, args) = (located, own.Command.Skip(1).ToList());
        }
        else if (entry.Kind is { } kind && Candidates(kind).FirstOrDefault() is { Program: not null } candidate)
        {
            (program, args) = (candidate.Program, candidate.Args);
        }
        else
        {
            return (null, entry.Kind is null
                ? $"\"languageServers\".\"{entry.Id}\" in config.json has no \"command\"."
                : $"{entry.Kind.Programs[0].Program} is not installed (looked for on PATH{(entry.Kind.Programs[0].Program is "pylsp" or "pyright-langserver" or "basedpyright-langserver" ? " and in the project's .venv" : "")}).");
        }
        var environment = new Dictionary<string, string?>(entry.Kind?.Env ?? NoEnv, StringComparer.Ordinal);
        foreach (var (name, value) in own?.Env ?? NoEnv)
        {
            environment[name] = value;
        }
        var options = Options(entry);
        Merge(options, own?.InitializationOptions);
        var version = !given && entry.Kind?.VersionArgs is { } versionArgs && !Workspace.Within(program, workspace.Root) ? await VersionAsync(program, versionArgs, ct) : null;
        var settings = Defaults(entry);
        Merge(settings, own?.Settings);
        return (new FoundLanguageServer(entry.Id, entry.Name, entry.Languages, program, args, environment, options, settings, version, given), null);
    }

    /// <summary>
    /// What a built-in server's initialize gets: typescript-language-server never downloads type packages (VS Code's
    /// automatic type acquisition), and uses the project's own TypeScript when the folder has one; the JSON server reads
    /// only files itself, and asks the page for any other schema (which the page does not fetch).
    /// </summary>
    private JsonObject Options(Entry entry)
    {
        switch (entry.Kind?.Id)
        {
            case "typescript":
                var options = new JsonObject { ["disableAutomaticTypingAcquisition"] = true };
                var tsserver = Path.Combine(workspace.Root, "node_modules", "typescript", "lib", "tsserver.js");
                if (File.Exists(tsserver))
                {
                    options["tsserver"] = new JsonObject { ["path"] = tsserver };
                }
                return options;
            case "json":
                return new JsonObject { ["provideFormatter"] = true, ["handledSchemaProtocols"] = new JsonArray("file") };
            default:
                return [];
        }
    }

    /// <summary>The settings a built-in server is answered with before config.json's: the YAML server never fetches the schema store's catalog.</summary>
    private static JsonObject Defaults(Entry entry) => entry.Kind?.Id switch
    {
        "yaml" => new JsonObject { ["yaml"] = new JsonObject { ["schemaStore"] = new JsonObject { ["enable"] = false } } },
        _ => [],
    };

    /// <summary>One object's keys over another's, objects within merged the same way.</summary>
    internal static void Merge(JsonObject into, JsonObject? over)
    {
        foreach (var (key, value) in over ?? [])
        {
            if (value is JsonObject inner && into[key] is JsonObject there)
            {
                Merge(there, inner);
            }
            else
            {
                into[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>A program that may be the server: where it is, and its arguments.</summary>
    private sealed record Candidate(string? Program, string[] Args);

    /// <summary>Where a built-in server may be, in the order looked: the project's virtual environment for Python's, PATH, then the usual installs.</summary>
    private IEnumerable<Candidate> Candidates(LanguageServerKind kind)
    {
        var home = env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (name, args) in kind.Programs)
        {
            var known = kind.Id switch
            {
                "pyright" or "basedpyright" or "pylsp" => new[] { env("VIRTUAL_ENV") is { Length: > 0 } venv ? venv : null, Path.Combine(workspace.Root, ".venv"), Path.Combine(workspace.Root, "venv") }
                    .OfType<string>().Select(v => Path.Combine(v, OperatingSystem.IsWindows() ? "Scripts" : "bin", Exe(name))).ToArray(),
                "gopls" => new[] { env("GOBIN"), env("GOPATH") is { Length: > 0 } gopath ? Path.Combine(gopath, "bin") : null, Path.Combine(home, "go", "bin") }
                    .OfType<string>().Where(d => d.Length > 0).Select(d => Path.Combine(d, Exe(name))).ToArray(),
                "rust-analyzer" => [Path.Combine(home, ".cargo", "bin", Exe(name))],
                "csharp-ls" => [Path.Combine(home, ".dotnet", "tools", Exe(name))],
                "clangd" => [.. Versioned(name), .. new[] { "/opt/homebrew/opt/llvm/bin/clangd", "/usr/local/opt/llvm/bin/clangd", @"C:\Program Files\LLVM\bin\clangd.exe" }],
                _ => [],
            };
            // The project's own environment first (it is the project's Python); the rest after PATH.
            var first = kind.Id is "pyright" or "basedpyright" or "pylsp" ? known : [];
            foreach (var program in first.Append(OnPath(Exe(name))).Concat(first.Length > 0 ? [] : known))
            {
                if (program is not null && File.Exists(program))
                {
                    yield return new Candidate(program, args);
                }
            }
        }
    }

    /// <summary>clangd-19, clangd-18 … on PATH, as LLVM's Debian packages name them, the newest first.</summary>
    private IEnumerable<string> Versioned(string name) =>
        PathFolders().SelectMany(d => Listed(() => Directory.EnumerateFiles(d, name + "-*")))
            .Where(f => int.TryParse(Path.GetFileName(f)[(name.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .OrderByDescending(f => int.Parse(Path.GetFileName(f)[(name.Length + 1)..], CultureInfo.InvariantCulture));

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

    /// <summary>PATH's folders, the full ones only: "." or "bin" in PATH would find the project's own program of that name.</summary>
    private IEnumerable<string> PathFolders() =>
        (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathFullyQualified);

    private string? OnPath(string name) =>
        PathFolders().Select(d => Path.Combine(d, name)).Concat(OperatingSystem.IsWindows() ? PathFolders().Select(d => Path.Combine(d, name + ".cmd")) : []).FirstOrDefault(File.Exists);

    /// <summary>A program as a full path: as given when it is one, else found on PATH; a relative path is none.</summary>
    private string? Locate(string program)
    {
        if (Path.IsPathRooted(program) || program.Contains('/') || program.Contains('\\'))
        {
            return Path.IsPathFullyQualified(program) && File.Exists(program) ? Path.GetFullPath(program) : null;
        }
        return OnPath(program) ?? (OperatingSystem.IsWindows() ? OnPath(Exe(program)) : null);
    }

    /// <summary>What a program says its version is, asked once for as long as it is unchanged; null when it says none.</summary>
    private async Task<string?> VersionAsync(string program, string[] args, CancellationToken ct)
    {
        var key = $"{program}\n{File.GetLastWriteTimeUtc(program).Ticks}";
        lock (_versions)
        {
            if (_versions.TryGetValue(key, out var known))
            {
                return known;
            }
        }
        string? version = null;
        try
        {
            Directory.CreateDirectory(dataDir);
            // Not the project's folder: nothing of the project's is run to list the servers.
            var psi = new ProcessStartInfo(program) { WorkingDirectory = dataDir };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            var result = await Proc.RunAsync(psi, CheckLimit, ct, 4_000);
            if (!result.TimedOut && result.ExitCode == 0)
            {
                version = result.Output.Split('\n').Select(l => VersionPattern().Matches(l)).FirstOrDefault(m => m.Count > 0) is { } matches ? matches[^1].Value : null;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ToolError)
        {
            // It cannot say: it is listed without a version.
        }
        lock (_versions)
        {
            _versions[key] = version;
        }
        return version;
    }

    [GeneratedRegex(@"(?<![\d.])\d+\.\d+(?:\.\d+)*")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// Starts a server for a page that opens its socket, in place of the one of the same id a page had (that one is
    /// stopped, and its page told). Throws IdeError: 403 <c>not_trusted</c> until the folder is trusted, 404
    /// <c>missing</c> for no such server or one not found here (with how to get it), 409 <c>too_many</c> past six,
    /// 409 <c>failed</c> when it does not start. A refusal past the trust is kept for the server's log: the page's
    /// WebSocket sees only that it was refused.
    /// </summary>
    public async Task<LanguageServerRun> StartAsync(string? id, CancellationToken ct)
    {
        if (!trusted())
        {
            // As VS Code's Restricted Mode: a language server runs the folder's code (tsserver plugins, build scripts).
            throw new IdeError(403, "not_trusted", "Language servers run this folder's code: trust the folder first (Manage Workspace Trust).");
        }
        try
        {
            var run = await LaunchAsync(id, ct);
            lock (_gate)
            {
                _refused.Remove(run.Server.Id);
            }
            return run;
        }
        catch (IdeError e) when (id is not null && Entries().Any(x => x.Id == id))
        {
            lock (_gate)
            {
                _refused[id] = e.Message;
            }
            throw;
        }
    }

    private async Task<LanguageServerRun> LaunchAsync(string? id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } result)
        {
            throw new IdeError(404, "missing", $"There is no language server \"{id}\".");
        }
        if (result.Found is not { } found)
        {
            throw new IdeError(404, "missing", result.Reason ?? "It is not installed.", new JsonObject { ["install"] = Install(id!) });
        }
        TooMany(found.Id);
        // The agent's sandbox, without the network: a language server needs none, and so fetches nothing. Its program's
        // folder, and those of the files its arguments name (a script), stay seen when they are in /tmp.
        var named = found.Args.Prepend(found.Program).Where(a => Path.IsPathFullyQualified(a) && File.Exists(a)).Select(a => Path.GetDirectoryName(a)!);
        var box = sandbox().Lasting(named);
        var sandboxed = box.Kind != Sandbox.Kinds.Off;
        StdioChild child;
        try
        {
            child = StdioChild.Start(found.Program, found.Args, workspace.Root, found.Env, sandboxed ? box.Wrap : null);
        }
        catch (IOException e)
        {
            throw new IdeError(409, "failed", e.Message);
        }
        var run = new LanguageServerRun(found, child, workspace.Root, sandboxed);
        LanguageServerRun? replaced = null;
        IdeError? refused = null;
        lock (_gate)
        {
            if (_disposed)
            {
                refused = new IdeError(409, "failed", "Code Arena is stopping.");
            }
            else if (_live.Count(r => r.Key != found.Id) >= Max)
            {
                // Another page started one meanwhile.
                refused = TooManyError();
            }
            else
            {
                _live.Remove(found.Id, out replaced);
                _live[found.Id] = run;
                _last[found.Id] = run;
            }
        }
        if (refused is not null)
        {
            await run.StopAsync(null);
            throw refused;
        }
        if (replaced is not null)
        {
            // Taken over: the page that had it is told, and its server stopped.
            _ = replaced.StopAsync(LanguageServerRun.TakenOver);
        }
        _ = ForgetAsync(run);
        return run;
    }

    private void TooMany(string id)
    {
        lock (_gate)
        {
            if (_live.Count(r => r.Key != id) >= Max)
            {
                throw TooManyError();
            }
        }
    }

    private static IdeError TooManyError() =>
        new(409, "too_many", $"{Max} language servers are running: close the files of one first, or restart this page.");

    /// <summary>A server that ended is no longer running (its log is kept).</summary>
    private async Task ForgetAsync(LanguageServerRun run)
    {
        await run.Ended;
        lock (_gate)
        {
            if (_live.TryGetValue(run.Server.Id, out var now) && now == run)
            {
                _live.Remove(run.Server.Id);
            }
        }
    }

    /// <summary>
    /// The server's last stderr lines (of the one running, else the last that ran): <c>{server, running, exitCode,
    /// lines, refused}</c>, <c>refused</c> saying why its last start was refused, if it was. 404 <c>missing</c> for no
    /// such server.
    /// </summary>
    public JsonObject Log(string? id)
    {
        if (!Entries().Any(e => e.Id == id))
        {
            throw new IdeError(404, "missing", $"There is no language server \"{id}\".");
        }
        LanguageServerRun? run;
        string? refused;
        lock (_gate)
        {
            run = _last.GetValueOrDefault(id!);
            refused = _refused.GetValueOrDefault(id!);
        }
        return new JsonObject
        {
            ["server"] = id,
            ["running"] = run is not null && !run.Ended.IsCompleted,
            ["exitCode"] = run?.ExitCode,
            ["lines"] = new JsonArray([.. (run?.Said() ?? []).Select(l => (JsonNode)l)]),
            ["refused"] = refused,
        };
    }

    /// <summary>The servers running: their ids.</summary>
    public IReadOnlyList<string> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _live.Keys];
            }
        }
    }

    /// <summary>Stops every server (the folder is no longer trusted); their pages are told.</summary>
    public async Task StopAllAsync(string? why)
    {
        List<LanguageServerRun> all;
        lock (_gate)
        {
            all = [.. _live.Values];
            _live.Clear();
        }
        await Task.WhenAll(all.Select(r => r.StopAsync(why)));
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        await StopAllAsync("Code Arena is stopping.");
    }
}

/// <summary>
/// One language server running for one page: its messages relayed both ways, each JSON-RPC message a WebSocket text
/// message, with every file URI rewritten (<see cref="LspUris"/>). The page's initialize is given the folder (rootUri,
/// rootPath, workspaceFolders), the process id, and the initializationOptions of the catalog and config.json; the page
/// chooses none of them. Stopped (its page gone, another page took it, the folder no longer trusted), it is asked to
/// shut down and exit, and whatever is left of it 2 s later is killed. The page is told why it ended, last, as the
/// notification <c>codeArena/ended</c> <c>{reason, exitCode, takenOver}</c>.
/// </summary>
internal sealed class LanguageServerRun
{
    /// <summary>The messages that may wait for the page: the server waits once there are as many.</summary>
    public const int Waiting = 1_000;

    /// <summary>What a page is told when another page took its server over.</summary>
    public const string TakenOver = "Another page took this language server over.";

    private const string ShutdownId = "codeArena/shutdown";

    private readonly StdioChild _child;
    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;
    private readonly LspUris _uris;
    private readonly string _root;
    private readonly bool _sandboxed;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutDown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private int _stopping;
    private string? _why;

    public LanguageServerRun(FoundLanguageServer server, StdioChild child, string root, bool sandboxed)
    {
        Server = server;
        _child = child;
        _root = root;
        _sandboxed = sandboxed;
        _uris = new LspUris(root);
        _reader = new FrameReader(child.Reading, LanguageServers.MaxServerMessage);
        _writer = new FrameWriter(child.Writing);
        _ = Task.Run(ReadAsync);
    }

    public FoundLanguageServer Server { get; }

    /// <summary>What goes to the page, in order; complete once the server ended (the last of it <c>codeArena/ended</c>).</summary>
    public Channel<SocketMessage> Out { get; } = Channel.CreateBounded<SocketMessage>(new BoundedChannelOptions(Waiting) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>Its process (the sandbox's, when it runs in one).</summary>
    public int? Pid => _child.Pid;

    /// <summary>Done once the server ended and the page was told.</summary>
    public Task Ended => _ended.Task;

    /// <summary>Its exit code once it ended.</summary>
    public int? ExitCode => _child.Exited.IsCompletedSuccessfully ? _child.Exited.Result : null;

    /// <summary>The last lines it wrote to stderr.</summary>
    public string[] Said() => _child.Said();

    /// <summary>A message of the page's for the server: its URIs as the server's, and an initialize given what the server decides. Anything that is not a JSON-RPC message is passed over.</summary>
    public async ValueTask FromPageAsync(SocketMessage message, CancellationToken ct)
    {
        if (!message.Text || Json.ParseObject(Encoding.UTF8.GetString(FrameReader.Repair(message.Data))) is not { } m)
        {
            return;
        }
        _uris.ToServer(m);
        if (m.Str("method") == "initialize" && m["id"] is not null)
        {
            Initialize(m["params"] as JsonObject ?? (JsonObject)(m["params"] = new JsonObject()));
        }
        try
        {
            await _writer.WriteAsync(m, ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The server is gone: its reader ends the run, and tells the page.
        }
    }

    /// <summary>What the server decides of initialize: the folder, the process id, the options.</summary>
    private void Initialize(JsonObject p)
    {
        var rootUri = LspUris.FileUri(_root);
        // In the sandbox's own process namespace Code Arena's process id means nothing: a server that watches it would end at once.
        p["processId"] = _sandboxed ? null : Environment.ProcessId;
        p["rootUri"] = rootUri;
        p["rootPath"] = _root;
        p["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(_root) is { Length: > 0 } name ? name : _root });
        p["initializationOptions"] = Server.InitializationOptions.Clone();
    }

    private async Task ReadAsync()
    {
        string? error = null;
        try
        {
            while (await _reader.ReadAsync(CancellationToken.None) is { } message)
            {
                if (message["id"] is JsonValue id && id.TryGetValue<string>(out var own) && own == ShutdownId && message["method"] is null)
                {
                    _shutDown.TrySetResult();
                    continue;
                }
                if (Volatile.Read(ref _stopping) == 1)
                {
                    // What it says while it stops goes nowhere: its page is gone, or is being told.
                    continue;
                }
                _uris.ToPage(message);
                try
                {
                    await Out.Writer.WriteAsync(SocketMessage.Of(message), _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stopped while the page was behind.
                }
            }
        }
        catch (FramingException e)
        {
            error = $"The language server sent what is not the Language Server Protocol: {e.Message}";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or ChannelClosedException)
        {
            // Its end closed.
        }
        await Task.WhenAny(Task.WhenAll(_child.Exited, _child.SaidAll), Task.Delay(TimeSpan.FromSeconds(1)));
        _child.Kill();
        var code = ExitCode;
        var reason = _why ?? error ?? StdioChild.With(code is { } c ? $"The language server ended (exit code {c})" : "The language server ended", _child.Said());
        Out.Writer.TryWrite(SocketMessage.Of(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "codeArena/ended",
            ["params"] = new JsonObject { ["reason"] = reason, ["exitCode"] = code, ["takenOver"] = _why == TakenOver, ["stopped"] = _why is not null },
        }));
        Out.Writer.TryComplete();
        _shutDown.TrySetResult();
        _ended.TrySetResult();
    }

    /// <summary>
    /// Stops the server, once: shutdown, then exit, as a client that is done does; whatever is left 2 s later (it, and
    /// every process it started) is killed. <paramref name="why"/>: what its page is told (null: its page is gone).
    /// </summary>
    public async Task StopAsync(string? why)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            await _ended.Task;
            return;
        }
        _why = why;
        var clock = Stopwatch.StartNew();
        TimeSpan Left() => LanguageServers.StopWait - clock.Elapsed is { Ticks: > 0 } left ? left : TimeSpan.Zero;
        if (!_ended.Task.IsCompleted)
        {
            await _stop.CancelAsync();
            if (await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ShutdownId, ["method"] = "shutdown" }))
            {
                await Task.WhenAny(_shutDown.Task, _child.Exited, Task.Delay(Left()));
                await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" });
            }
            await Task.WhenAny(_child.Exited, Task.Delay(Left()));
        }
        _child.Kill();
        await Task.WhenAny(_ended.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        await _child.DisposeAsync();
        _writer.Dispose();
        _stop.Dispose();
    }

    private async Task<bool> SendAsync(JsonObject message)
    {
        try
        {
            using var limit = new CancellationTokenSource(LanguageServers.StopWait);
            await _writer.WriteAsync(message, limit.Token);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// The page's file URIs and the server's. The page names a file of the folder <c>file:///</c> and its path in the
/// folder (as its editor's models are named); the server names it <c>file://</c> and its full path. Every string in a
/// message that is a whole file URI is rewritten, and so is every key that is one (a WorkspaceEdit's "changes"), but
/// not text: a document's (<c>text</c>), an edit's (<c>newText</c>, <c>insertText</c>) or a hover's (<c>value</c>).
/// A file of the server's outside the folder (a library's, the standard library's) is <c>outside://</c> and its full
/// path for the page, and back. The folder is matched as named and where it leads (its links resolved).
/// </summary>
internal sealed class LspUris
{
    /// <summary>The scheme the page sees a file outside the folder under.</summary>
    public const string Outside = "outside://";

    private const string PageFile = "file:///";
    private const string FileScheme = "file://";
    private static readonly HashSet<string> Text = new(StringComparer.Ordinal) { "text", "newText", "insertText", "value" };

    private readonly string _root;
    private readonly string[] _roots;

    public LspUris(string root)
    {
        _root = root;
        var real = Workspace.Physical(root) ?? root;
        _roots = real == root ? [root] : [root, real];
    }

    /// <summary>The server's URI of a full path: file:// and the path, each byte but letters, digits, - . _ ~ / (and a drive's :) percent-encoded.</summary>
    public static string FileUri(string full)
    {
        var path = full.Replace('\\', '/');
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }
        return FileScheme + Encode(path);
    }

    private static string Encode(string path)
    {
        var sb = new StringBuilder(path.Length + 16);
        var bytes = Encoding.UTF8.GetBytes(path);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            var drive = b == ':' && i == 2 && bytes[0] == '/' && char.IsAsciiLetter((char)bytes[1]);
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/' || drive)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>The page's URIs in a message, as the server's (in place).</summary>
    public void ToServer(JsonNode? node) => Walk(node, ServerUri, 0);

    /// <summary>The server's URIs in a message, as the page's (in place).</summary>
    public void ToPage(JsonNode? node) => Walk(node, PageUri, 0);

    /// <summary>A page's URI as the server's: a file of the folder by its full path, one outside as it was; anything else unchanged.</summary>
    public string ServerUri(string uri)
    {
        if (uri.StartsWith(Outside, StringComparison.Ordinal))
        {
            return FileScheme + uri[Outside.Length..];
        }
        if (!uri.StartsWith(PageFile, StringComparison.Ordinal))
        {
            return uri;
        }
        var (path, rest) = Split(uri[PageFile.Length..]);
        var relative = Uri.UnescapeDataString(path);
        // A path of the folder's only: none that leads out of it (.., a full path after the page's slash, a drive, a backslash).
        if (relative.Contains('\0') || relative.Contains('\\') || relative.StartsWith('/') || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p == ".."))
        {
            return uri;
        }
        var full = relative.Length == 0 ? _root : Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        return FileUri(full) + rest;
    }

    /// <summary>A server's URI as the page's: a file of the folder by its path in it, one outside under outside://; anything else unchanged.</summary>
    public string PageUri(string uri)
    {
        if (!uri.StartsWith(FileScheme, StringComparison.Ordinal))
        {
            return uri;
        }
        var after = uri[FileScheme.Length..];
        if (!after.StartsWith('/'))
        {
            // An authority (a share, file://server/…): not the folder's.
            return Outside + after;
        }
        var (encoded, rest) = Split(after);
        var path = Uri.UnescapeDataString(encoded);
        // file:///C:/x on Windows; file:///c%3A/x as VS Code writes it.
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }
        if (path.Contains('\0'))
        {
            return Outside + after;
        }
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Outside + after;
        }
        foreach (var root in _roots)
        {
            if (Workspace.Within(full, root))
            {
                var relative = full.Length == root.Length ? "" : full[(Path.TrimEndingDirectorySeparator(root).Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
                return PageFile + Encode(relative) + rest;
            }
        }
        return Outside + after;
    }

    /// <summary>A URI's path and what follows it (a query, a fragment).</summary>
    private static (string Path, string After) Split(string s)
    {
        var at = s.IndexOfAny(['?', '#']);
        return at < 0 ? (s, "") : (s[..at], s[at..]);
    }

    private static bool IsUri(string s) =>
        (s.StartsWith(FileScheme, StringComparison.Ordinal) || s.StartsWith(Outside, StringComparison.Ordinal)) && !s.AsSpan().ContainsAny(" \t\r\n");

    private static void Walk(JsonNode? node, Func<string, string> map, int depth)
    {
        if (depth > 64)
        {
            return;
        }
        switch (node)
        {
            case JsonObject o:
                List<(string From, string To)>? keys = null;
                foreach (var (key, value) in o.ToList())
                {
                    if (IsUri(key))
                    {
                        (keys ??= []).Add((key, map(key)));
                    }
                    if (value is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (!Text.Contains(key) && IsUri(s))
                        {
                            o[key] = map(s);
                        }
                    }
                    else
                    {
                        Walk(value, map, depth + 1);
                    }
                }
                foreach (var (from, to) in keys ?? [])
                {
                    if (from != to && o[from] is var moved && o.Remove(from))
                    {
                        o[to] = moved;
                    }
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (IsUri(s))
                        {
                            a[i] = map(s);
                        }
                    }
                    else
                    {
                        Walk(a[i], map, depth + 1);
                    }
                }
                break;
        }
    }
}
