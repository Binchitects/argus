using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A formatter that ran and could not format the text: what it said, and where in the text when it said so.</summary>
internal sealed class FormatFailed(string message, int? line, int? column) : Exception(message)
{
    public int? Line { get; } = line;
    public int? Column { get; } = column;
}

/// <summary>A formatter found for a file: what it is, where it was found, and how it runs.</summary>
internal sealed record FoundFormatter
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>"project" (in the folder), "global" (on PATH, or where the tool installs itself) or "config".</summary>
    public required string Source { get; init; }
    public bool Range { get; init; }
    public string? Version { get; init; }
    /// <summary>The program run, as a full path; null when it was not found.</summary>
    public string? Program { get; init; }
    /// <summary>The script the program runs (Prettier's, which node runs).</summary>
    public string? Script { get; init; }
    /// <summary>Why it cannot run, trust aside: "missing" (not installed) or "node" (Prettier without node); null: it can.</summary>
    public string? Missing { get; init; }
    /// <summary>What the person can do to have it.</summary>
    public string? Hint { get; init; }
    /// <summary>Whether what runs lives in the folder (a project's node_modules or .venv): it runs only when the folder is trusted.</summary>
    public bool InFolder { get; init; }
    /// <summary>A config formatter's arguments, with its ${…} still in them.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public TimeSpan Timeout { get; init; } = Formatters.DefaultTimeout;
}

/// <summary>
/// The IDE's formatters, run by the server on the text the editor holds (saved or not): the project's own Prettier
/// (the nearest node_modules, walking up from the file to the folder's root), else a global one; ruff, then black, for
/// Python; gofmt; rustfmt; clang-format; dotnet format (on a copy, in a temporary folder holding the .editorconfig
/// files); and the person's own from config.json "formatters", which add to them or take a built-in's place. They are
/// found by their files, never by running them, and what was found is kept 10 s per folder. What runs from the folder
/// itself (node_modules, a .venv) runs only once the person trusts the folder: a repository someone cloned does not get
/// to run its programs by being opened (until then a global Prettier runs without the project's config, which can be
/// code too). Each runs in a sandbox where the system has one (read-only, secrets hidden, a /tmp of its own, no
/// network), with no API key in its environment, four at once at most, ten seconds each unless set.
/// </summary>
internal sealed partial class Formatters(IdeFiles files, Config config, WorkspaceState state, Func<Sandbox> sandbox, Func<string, string?> env)
{
    /// <summary>How long a formatter runs before it is stopped, unless it says otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The most formatters running at once.</summary>
    public const int MaxRunning = 4;

    private static readonly TimeSpan Kept = TimeSpan.FromSeconds(10);
    private static readonly StringComparison PathCompare = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly SemaphoreSlim _running = new(MaxRunning, MaxRunning);
    private readonly ConcurrentDictionary<(string Folder, string Language), (long At, FoundFormatter[] Found)> _found = new();

    private Workspace Workspace => files.Workspace;

    /// <summary>The languages Prettier formats, as Monaco names them.</summary>
    private static readonly string[] PrettierLanguages = ["javascript", "typescript", "json", "css", "scss", "less", "html", "vue", "markdown", "mdx", "yaml", "graphql", "handlebars"];
    private static readonly string[] ClangLanguages = ["c", "cpp", "objective-c", "java", "protobuf"];

    /// <summary>The built-in formatters: their names, whether they format a range, and what installs them.</summary>
    private static readonly Dictionary<string, (string Name, bool Range, string Hint)> BuiltIns = new()
    {
        ["prettier"] = ("Prettier", true, "npm install --save-dev prettier"),
        ["ruff"] = ("Ruff", true, "pip install ruff"),
        ["black"] = ("Black", true, "pip install black"),
        ["gofmt"] = ("gofmt", false, "gofmt comes with Go: https://go.dev/dl"),
        ["rustfmt"] = ("rustfmt", false, "rustup component add rustfmt"),
        ["clang-format"] = ("clang-format", true, OperatingSystem.IsWindows() ? "winget install LLVM.LLVM" : OperatingSystem.IsMacOS() ? "brew install clang-format" : "apt install clang-format"),
        ["dotnet-format"] = ("dotnet format", false, "dotnet format comes with the .NET SDK: https://dot.net/download"),
    };

    /// <summary>The built-in formatters of a language, in order: the first that can run is the default.</summary>
    private static string[] BuiltInFor(string language) => language switch
    {
        _ when PrettierLanguages.Contains(language) => ["prettier"],
        "python" => ["ruff", "black"],
        "go" => ["gofmt"],
        "rust" => ["rustfmt"],
        _ when ClangLanguages.Contains(language) => ["clang-format"],
        "csharp" or "vb" => ["dotnet-format"],
        _ => [],
    };

    private static string[] LanguagesOf(string builtIn) => builtIn switch
    {
        "prettier" => PrettierLanguages,
        "ruff" or "black" => ["python"],
        "gofmt" => ["go"],
        "rustfmt" => ["rust"],
        "clang-format" => ClangLanguages,
        "dotnet-format" => ["csharp", "vb"],
        _ => [],
    };

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript", [".jsx"] = "javascript",
        [".ts"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript", [".tsx"] = "typescript",
        [".json"] = "json", [".jsonc"] = "json", [".json5"] = "json",
        [".css"] = "css", [".scss"] = "scss", [".less"] = "less", [".html"] = "html", [".htm"] = "html", [".vue"] = "vue",
        [".md"] = "markdown", [".markdown"] = "markdown", [".mdx"] = "mdx", [".yml"] = "yaml", [".yaml"] = "yaml",
        [".graphql"] = "graphql", [".gql"] = "graphql", [".hbs"] = "handlebars", [".handlebars"] = "handlebars",
        [".py"] = "python", [".pyi"] = "python", [".go"] = "go", [".rs"] = "rust",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".c++"] = "cpp", [".hpp"] = "cpp", [".hh"] = "cpp",
        [".hxx"] = "cpp", [".ino"] = "cpp", [".m"] = "objective-c", [".java"] = "java", [".proto"] = "protobuf",
        [".cs"] = "csharp", [".csx"] = "csharp", [".vb"] = "vb", [".fs"] = "fsharp", [".fsx"] = "fsharp",
        [".lua"] = "lua", [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell", [".rb"] = "ruby", [".php"] = "php", [".sql"] = "sql",
        [".swift"] = "swift", [".kt"] = "kotlin", [".kts"] = "kotlin", [".dart"] = "dart", [".xml"] = "xml", [".ps1"] = "powershell",
        [".r"] = "r", [".scala"] = "scala", [".ex"] = "elixir", [".exs"] = "elixir", [".tf"] = "hcl", [".hcl"] = "hcl", [".sol"] = "solidity",
        [".pl"] = "perl", [".clj"] = "clojure", [".ini"] = "ini", [".toml"] = "toml",
    };

    /// <summary>A file's language by its name, as Monaco names it; "plaintext" when none is known.</summary>
    public static string LanguageOf(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? "dockerfile" : ByExtension.GetValueOrDefault(Path.GetExtension(name)) ?? "plaintext";
    }

    /// <summary>
    /// The formatters for a file, in order ({language, formatters, default}): each with whether it can run here and why
    /// not; the default is the first that can.
    /// </summary>
    public JsonObject List(string? path, string? language)
    {
        var full = Target(path);
        language = Language(full, language);
        var trusted = state.Trusted;
        var found = Find(full, language, trusted);
        string? first = null;
        var list = new JsonArray();
        foreach (var f in found)
        {
            var reason = Reason(f, trusted);
            first ??= reason is null ? f.Id : null;
            var json = new JsonObject
            {
                ["id"] = f.Id, ["name"] = f.Name, ["version"] = f.Version, ["source"] = f.Source, ["available"] = reason is null, ["reason"] = reason,
                ["hint"] = reason == "trust" ? "Trust this folder to run the formatter it holds." : f.Missing is null ? null : f.Hint,
                ["range"] = f.Range, ["path"] = (f.Script ?? f.Program) is { } where ? Workspace.Show(where) : null,
            };
            foreach (var key in json.Where(p => p.Value is null).Select(p => p.Key).ToList())
            {
                json.Remove(key);
            }
            list.Add(json);
        }
        return new JsonObject { ["language"] = language, ["formatters"] = list, ["default"] = first };
    }

    /// <summary>
    /// The text formatted by the formatter asked for (else the file's default), the whole of it even for a range
    /// (UTF-16 offsets, as the editor counts); and the formatter's id.
    /// </summary>
    public async Task<(string Text, string Formatter)> FormatAsync(string? path, string text, string? id, (int Start, int End)? range, JsonObject? options, string? language, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(text) > IdeFiles.MaxTextBytes)
        {
            throw new IdeError(413, "too_large", $"Formatting takes files up to {IdeFiles.MaxTextBytes / 1024 / 1024} MB.");
        }
        if (range is { } r && (r.Start < 0 || r.End < r.Start || r.End > text.Length))
        {
            throw new IdeError(400, "invalid", $"The range is two offsets in the text, 0 to {text.Length}, start first.");
        }
        var full = Target(path);
        language = Language(full, language);
        var trusted = state.Trusted;
        var found = Find(full, language, trusted);
        var named = id is null ? found : found.Where(f => f.Id == id).ToList();
        var formatter = named.FirstOrDefault(f => Reason(f, trusted) is null) ?? named.FirstOrDefault(f => Reason(f, trusted) == "trust") ?? named.FirstOrDefault();
        if (formatter is null)
        {
            throw new IdeError(404, "no_formatter", id is null
                ? $"Code Arena knows no formatter for {language}: \"formatters\" in config.json can name one."
                : $"There is no formatter {id} for {Workspace.Show(full)}.");
        }
        switch (Reason(formatter, trusted))
        {
            case "trust":
                throw new IdeError(403, "not_trusted", $"{formatter.Name} lives in this folder ({Workspace.Show(formatter.Script ?? formatter.Program!)}): trust the folder to run it.");
            case { } missing:
                throw new IdeError(404, "no_formatter", missing == "node" ? $"{formatter.Name} needs node, which is not on PATH." : $"{formatter.Name} is not installed: {formatter.Hint}.");
        }
        if (range is not null && !formatter.Range)
        {
            throw new IdeError(422, "no_range", $"{formatter.Name} formats whole files only.");
        }
        await _running.WaitAsync(ct);
        var tmp = Directory.CreateTempSubdirectory("code-arena-format-").FullName;
        try
        {
            return (await RunAsync(formatter, full, text, range, options, trusted, tmp, ct), formatter.Id);
        }
        finally
        {
            _running.Release();
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Something it left running holds it: the system's temporary folder is cleaned in time.
            }
        }
    }

    private string Target(string? path)
    {
        var full = files.Resolve(path);
        return Directory.Exists(full) ? throw new IdeError(400, "folder", $"{files.Show(full)} is a folder: give a file.") : full;
    }

    private static string Language(string full, string? asked) =>
        asked is { Length: > 0 and <= 64 } && asked.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? asked : LanguageOf(full);

    private static string? Reason(FoundFormatter f, bool trusted) => f.Missing ?? (f.InFolder && !trusted ? "trust" : null);

    /// <summary>
    /// The formatters for a file: the person's own that add one first, then the built-ins (one the config replaces in its
    /// place), each the project's before the global one. A global one is left out once the project's of the same name can
    /// run: it would never be used (as in VS Code, the folder's own Prettier wins once the folder is trusted).
    /// </summary>
    private List<FoundFormatter> Find(string full, string language, bool trusted)
    {
        var all = Candidates(full, language);
        return [.. all.Where((f, i) => !all.Take(i).Any(earlier => earlier.Id == f.Id && Reason(earlier, trusted) is null))];
    }

    private List<FoundFormatter> Candidates(string full, string language)
    {
        var folder = Path.GetDirectoryName(full) ?? Workspace.Root;
        var now = Environment.TickCount64;
        FoundFormatter[] builtIns;
        if (_found.TryGetValue((folder, language), out var kept) && now - kept.At < Kept.TotalMilliseconds)
        {
            builtIns = kept.Found;
        }
        else
        {
            if (_found.Count > 512)
            {
                _found.Clear();
            }
            builtIns = [.. BuiltInFor(language).SelectMany(id => Detect(id, folder))];
            if (language == "python" && PrefersBlack(folder))
            {
                builtIns = [.. builtIns.OrderBy(f => f.Id == "black" ? 0 : 1)];
            }
            _found[(folder, language)] = (now, builtIns);
        }
        var configured = config.Formatters.Where(c => Applies(c, full, language)).ToDictionary(c => c.Name, c => Configured(c));
        var list = configured.Values.Where(c => !BuiltIns.ContainsKey(c.Id)).ToList();
        foreach (var b in builtIns)
        {
            var mine = configured.GetValueOrDefault(b.Id);
            if (mine is null)
            {
                list.Add(b);
            }
            else if (!list.Contains(mine))
            {
                list.Add(mine);
            }
        }
        // One named as a built-in, for a language that built-in does not take (prettier for .lua).
        list.AddRange(configured.Values.Where(c => !list.Contains(c)));
        return list;
    }

    private static bool Applies(FormatterConfig c, string full, string language)
    {
        var languages = c.Languages.Count > 0 ? c.Languages : LanguagesOf(c.Name).ToList();
        return languages.Any(l => l.Contains('*') || l.Contains('?')
            ? Glob(l).IsMatch(Path.GetFileName(full))
            : string.Equals(l, language, StringComparison.OrdinalIgnoreCase));
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private FoundFormatter Configured(FormatterConfig c)
    {
        var program = Program(c.Command[0]);
        return new FoundFormatter
        {
            Id = c.Name, Name = c.Name, Source = "config", Range = c.Range, Program = program, Arguments = c.Command.Skip(1).ToList(),
            Missing = program is null ? "missing" : null, Hint = $"{c.Command[0]} (\"formatters\" in config.json) was not found",
            InFolder = InFolder(program), Timeout = c.Timeout is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultTimeout,
        };
    }

    /// <summary>A config command's program as a full path: with ~, rooted, with a folder (from the working directory), or found on PATH.</summary>
    private string? Program(string command)
    {
        var path = Workspace.HomePath(command, Home());
        if (Path.IsPathRooted(path) || path.Contains('/') || path.Contains('\\'))
        {
            var full = Path.GetFullPath(path, Workspace.Root);
            return File.Exists(full) ? full : null;
        }
        return Which(path);
    }

    /// <summary>A built-in formatter as found for a folder: the project's own, then the global one; one "missing" when neither is there.</summary>
    private IEnumerable<FoundFormatter> Detect(string id, string folder)
    {
        var (name, range, hint) = BuiltIns[id];
        var node = id == "prettier" ? Which("node") : null;
        FoundFormatter?[] candidates = id switch
        {
            "prettier" => [PrettierProject(folder, node), PrettierGlobal(node)],
            "ruff" or "black" => [Venv(id, folder), Global(Which(id))],
            "gofmt" => [Global(Which("gofmt") ?? Existing(env("GOROOT") is { Length: > 0 } go ? Path.Combine(go, "bin", Exe("gofmt")) : null, "/usr/local/go/bin/gofmt"))],
            "rustfmt" => [Global(Which("rustfmt") ?? Existing(Path.Combine(env("CARGO_HOME") is { Length: > 0 } cargo ? cargo : Path.Combine(Home(), ".cargo"), "bin", Exe("rustfmt"))))],
            "clang-format" => [Global(Which("clang-format") ?? (OperatingSystem.IsWindows() ? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clang-format.exe")) : null))],
            "dotnet-format" => [Global(Which("dotnet") ?? Existing(env("DOTNET_ROOT") is { Length: > 0 } dotnet ? Path.Combine(dotnet, Exe("dotnet")) : null, Path.Combine(Home(), ".dotnet", Exe("dotnet"))))],
            _ => [],
        };
        // A virtual environment on PATH is the project's own, found twice.
        var found = candidates.OfType<FoundFormatter>().DistinctBy(f => (f.Program, f.Script)).ToList();
        if (found.Count == 0)
        {
            found.Add(new FoundFormatter { Id = id, Name = name, Source = "global", Missing = "missing" });
        }
        return found.Select(f => f with
        {
            Id = id, Name = name, Range = range, Hint = f.Missing == "node" ? "install Node.js: https://nodejs.org" : hint,
            Timeout = id == "dotnet-format" ? TimeSpan.FromSeconds(30) : DefaultTimeout,
        });
    }

    private FoundFormatter? Global(string? program) =>
        program is null ? null : new FoundFormatter { Id = "", Name = "", Source = InFolder(program) ? "project" : "global", Program = program, InFolder = InFolder(program) };

    /// <summary>Prettier run as <c>node its-script</c>, never through a shell's or cmd.exe's shim.</summary>
    private FoundFormatter PrettierScript(string? node, string script, string? version, bool project) => new()
    {
        Id = "", Name = "", Source = project || InFolder(script) ? "project" : "global", Program = node, Script = script, Version = version,
        Missing = node is null ? "node" : null, InFolder = project || InFolder(script),
    };

    /// <summary>The project's Prettier: the nearest node_modules/prettier (or node_modules/.bin/prettier, a link to it) from the file's folder up to the root.</summary>
    private FoundFormatter? PrettierProject(string folder, string? node)
    {
        foreach (var dir in Up(folder))
        {
            if (PrettierPackage(Path.Combine(dir, "node_modules", "prettier")) is { } package)
            {
                return PrettierScript(node, package.Script, package.Version, project: true);
            }
            if (Linked(Path.Combine(dir, "node_modules", ".bin", "prettier")) is { } linked)
            {
                return PrettierScript(node, linked, PrettierVersion(linked), project: true);
            }
        }
        return null;
    }

    /// <summary>A Prettier installed for every project: on PATH (npm's link to its script), or in npm's global folder.</summary>
    private FoundFormatter? PrettierGlobal(string? node)
    {
        var shim = Which(OperatingSystem.IsWindows() ? "prettier.cmd" : "prettier", exe: false);
        if (shim is not null && Linked(shim) is { } script)
        {
            return PrettierScript(node, script, PrettierVersion(script), project: false);
        }
        foreach (var dir in PathFolders().Concat(node is null ? [] : [Path.Combine(Path.GetDirectoryName(node)!, "..", "lib")]))
        {
            if (PrettierPackage(Path.Combine(dir, "node_modules", "prettier")) is { } package)
            {
                return PrettierScript(node, package.Script, package.Version, project: false);
            }
        }
        // A wrapper of its own (Nix's, say): run as it is, never cmd.exe's.
        return shim is not null && !OperatingSystem.IsWindows() ? Global(shim) : null;
    }

    /// <summary>Prettier's package in a folder: the script its package.json names, and its version.</summary>
    private static (string Script, string? Version)? PrettierPackage(string dir)
    {
        var manifest = Json.ParseObject(Read(Path.Combine(dir, "package.json")));
        if (manifest is null)
        {
            return null;
        }
        var bin = manifest["bin"] is JsonObject bins ? bins.Str("prettier") : manifest.Str("bin");
        foreach (var candidate in new[] { bin, "bin/prettier.cjs", "bin-prettier.js" })
        {
            if (candidate is { Length: > 0 } && Path.GetFullPath(candidate, dir) is var script && File.Exists(script))
            {
                return (script, manifest.Str("version"));
            }
        }
        return null;
    }

    /// <summary>The script a link leads to (npm's .bin/prettier, a global prettier), when it is one node runs.</summary>
    private static string? Linked(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
            var target = info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return target is not null && Path.GetExtension(target) is ".js" or ".cjs" or ".mjs" && File.Exists(target) ? target : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The version in the package.json of the Prettier a script belongs to.</summary>
    private static string? PrettierVersion(string script)
    {
        for (var dir = Path.GetDirectoryName(script); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Json.ParseObject(Read(Path.Combine(dir, "package.json"))) is { } manifest)
            {
                return manifest.Str("name") == "prettier" ? manifest.Str("version") : null;
            }
        }
        return null;
    }

    /// <summary>ruff or black: a virtual environment's (.venv, venv) from the file's folder up to the root, else on PATH.</summary>
    private FoundFormatter? Venv(string id, string folder)
    {
        foreach (var dir in Up(folder))
        {
            foreach (var venv in new[] { ".venv", "venv" })
            {
                var home = Path.Combine(dir, venv);
                var program = OperatingSystem.IsWindows() ? Path.Combine(home, "Scripts", id + ".exe") : Path.Combine(home, "bin", id);
                if (File.Exists(program))
                {
                    return new FoundFormatter { Id = id, Name = "", Source = "project", Program = program, InFolder = true, Version = Installed(home, id) };
                }
            }
        }
        return null;
    }

    /// <summary>A package's version in a virtual environment, by its .dist-info folder.</summary>
    private static string? Installed(string venv, string package)
    {
        try
        {
            var sites = OperatingSystem.IsWindows()
                ? [Path.Combine(venv, "Lib", "site-packages")]
                : Directory.Exists(Path.Combine(venv, "lib")) ? Directory.EnumerateDirectories(Path.Combine(venv, "lib"), "python*").Select(p => Path.Combine(p, "site-packages")) : [];
            foreach (var site in sites.Where(Directory.Exists))
            {
                if (Directory.EnumerateDirectories(site, $"{package}-*.dist-info").FirstOrDefault() is { } info)
                {
                    var name = Path.GetFileName(info);
                    return name[(package.Length + 1)..^".dist-info".Length];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>Whether a Python project formats with black: its pyproject.toml has [tool.black] and no [tool.ruff].</summary>
    private bool PrefersBlack(string folder)
    {
        foreach (var dir in Up(folder))
        {
            if (Read(Path.Combine(dir, "pyproject.toml")) is { } toml)
            {
                return toml.Contains("[tool.black]", StringComparison.Ordinal) && !toml.Contains("[tool.ruff", StringComparison.Ordinal);
            }
        }
        return false;
    }

    /// <summary>The Rust edition the nearest Cargo.toml gives (2015 for a package that names none, as Cargo has it; 2021 with no Cargo.toml).</summary>
    private string Edition(string folder)
    {
        foreach (var dir in Up(folder))
        {
            if (Read(Path.Combine(dir, "Cargo.toml")) is not { } toml)
            {
                continue;
            }
            if (EditionLine().Match(toml) is { Success: true } m)
            {
                return m.Groups[1].Value;
            }
            if (toml.Contains("[package]", StringComparison.Ordinal) && !toml.Contains("edition", StringComparison.Ordinal))
            {
                return "2015";
            }
            // edition.workspace = true: the workspace's Cargo.toml, further up, says.
        }
        return "2021";
    }

    [GeneratedRegex(@"^\s*edition\s*=\s*""(\d{4})""", RegexOptions.Multiline)]
    private static partial Regex EditionLine();

    private async Task<string> RunAsync(FoundFormatter f, string full, string text, (int Start, int End)? range, JsonObject? options, bool trusted, string tmp, CancellationToken ct)
    {
        var root = Workspace.Roots.Where(r => Workspace.Within(full, r)).MaxBy(r => r.Length) ?? Workspace.Root;
        var folder = Path.GetDirectoryName(full)!;
        // Where the tools that look for their config from the file run: its folder (the root while it is not made yet).
        var here = Directory.Exists(folder) ? folder : root;
        var relative = Path.GetRelativePath(root, full);
        var box = sandbox().Only([], network: false, tmp, [.. Workspace.Roots, Path.GetDirectoryName(f.Program!)!, .. f.Script is null ? Array.Empty<string>() : [Path.GetDirectoryName(f.Script)!]]);
        var args = new List<string>();
        var cwd = root;
        var input = text;
        string? result = null;
        switch (f.Id)
        {
            case "prettier" when f.Source != "config":
                if (f.Script is not null)
                {
                    args.Add(f.Script);
                }
                if (!trusted)
                {
                    // A project's Prettier config can be JavaScript, or name plugins: code, which an untrusted folder does not get to run.
                    args.Add("--no-config");
                }
                args.AddRange(PrettierFlags(options?["prettier"] as JsonObject));
                args.AddRange(["--stdin-filepath", relative]);
                if (range is { } r)
                {
                    args.AddRange(["--range-start", Number(r.Start), "--range-end", Number(r.End)]);
                }
                break;
            case "ruff" when f.Source != "config":
                args.AddRange(["format", "--no-cache", "--quiet", "--stdin-filename", relative]);
                if (range is { } rr)
                {
                    var (startLine, startColumn) = LineColumn(text, rr.Start);
                    var (endLine, endColumn) = LineColumn(text, rr.End);
                    args.AddRange(["--range", $"{startLine}:{startColumn}-{endLine}:{endColumn}"]);
                }
                break;
            case "black" when f.Source != "config":
                args.AddRange(["--quiet", "--stdin-filename", relative]);
                if (range is { } br)
                {
                    args.AddRange(["--line-ranges", $"{LineColumn(text, br.Start).Line}-{EndLine(text, br)}"]);
                }
                args.Add("-");
                break;
            case "gofmt" when f.Source != "config":
                cwd = here;
                break;
            case "rustfmt" when f.Source != "config":
                // rustfmt.toml is looked for from where it runs.
                cwd = here;
                args.AddRange(["--emit", "stdout", "--edition", Edition(folder)]);
                break;
            case "clang-format" when f.Source != "config":
                cwd = here;
                args.Add($"--assume-filename={full}");
                if (range is { } cr)
                {
                    var start = Utf8Offset(text, cr.Start, back: true);
                    args.AddRange([$"--offset={Number(start)}", $"--length={Number(Utf8Offset(text, cr.End, back: false) - start)}"]);
                }
                break;
            case "dotnet-format" when f.Source != "config":
            {
                // It formats files in place: a copy of the buffer, with the .editorconfig files from the root down beside it.
                var mirror = Path.Combine(box.Temp, "mirror");
                foreach (var dir in Up(folder).Reverse())
                {
                    var editorconfig = Path.Combine(dir, ".editorconfig");
                    if (File.Exists(editorconfig))
                    {
                        var copy = Path.Combine(mirror, Path.GetRelativePath(root, dir), ".editorconfig");
                        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                        File.Copy(editorconfig, copy);
                    }
                }
                result = Path.Combine(mirror, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(result)!);
                await File.WriteAllTextAsync(result, text, new UTF8Encoding(false), ct);
                args.AddRange(["format", "whitespace", box.Inside(mirror), "--folder", "--include", relative.Replace('\\', '/'), "--verbosity", "quiet"]);
                input = "";
                break;
            }
            default:
                args.AddRange(f.Arguments.Select(a => Fill(a, full, root, text, range, options)));
                break;
        }
        var psi = new ProcessStartInfo(f.Program!) { WorkingDirectory = cwd };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        if (f.Id == "dotnet-format")
        {
            // Its first run writes in its home, which is read-only here.
            psi.Environment["DOTNET_CLI_HOME"] = box.Inside(box.Temp);
        }
        Proc.PipeResult run;
        try
        {
            run = await Proc.PipeAsync(box.Wrap(psi), input, f.Timeout, (int)Math.Min(int.MaxValue, text.Length * 2L + 1024 * 1024), ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ToolError)
        {
            throw new IdeError(404, "no_formatter", $"{f.Name} could not be started: {e.Message}");
        }
        if (run.TimedOut)
        {
            throw new IdeError(504, "timeout", $"{f.Name} did not finish within {f.Timeout.TotalSeconds:0} s: it was stopped.");
        }
        if (run.TooLong)
        {
            throw new FormatFailed($"{f.Name} printed far more than the file holds: it was stopped.", null, null);
        }
        if (run.ExitCode != 0)
        {
            var said = Said(run.Error.Trim().Length > 0 ? run.Error : run.Output);
            var at = Where().Match(said);
            throw new FormatFailed(said.Length > 0 ? $"{f.Name} could not format {Workspace.Show(full)}: {said}" : $"{f.Name} ended with exit code {run.ExitCode}.",
                at.Success ? int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture) : null, at.Success ? int.Parse(at.Groups[2].Value, CultureInfo.InvariantCulture) : null);
        }
        var formatted = result is not null ? await File.ReadAllTextAsync(result, ct) : run.Output;
        // Nothing back for something to format is no formatting: the text stays as it was, never emptied.
        return formatted.Length == 0 && text.Trim().Length > 0 ? text : formatted;
    }

    /// <summary>What a tool said, for the person: its first 20 lines, colours taken out.</summary>
    private static string Said(string text) =>
        string.Join('\n', Colours().Replace(text, "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Take(20)).Trim();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Colours();

    // line:column, as tools say where: prettier's (3:5), ruff's and gofmt's file:3:5, rustfmt's --> <stdin>:3:5.
    [GeneratedRegex(@"(?<![\d.])(\d+):(\d+)")]
    private static partial Regex Where();

    /// <summary>Prettier's options the page may set (VS Code's prettier.* settings) as its command line takes them: only where the project has no config file.</summary>
    internal static List<string> PrettierFlags(JsonObject? prettier)
    {
        var flags = new List<string>();
        if (prettier is null)
        {
            return flags;
        }
        void Count(string key, string flag)
        {
            if (prettier.Int(key) is { } n && n is >= 0 and <= 1000)
            {
                flags.AddRange([flag, Number(n)]);
            }
        }
        void Switch(string key, string flag, bool when)
        {
            if (prettier.Bool(key) == when)
            {
                flags.Add(flag);
            }
        }
        void Choice(string key, string flag, params string[] values)
        {
            if (prettier.Str(key) is { } value && values.Contains(value))
            {
                flags.AddRange([flag, value]);
            }
        }
        Count("printWidth", "--print-width");
        Count("tabWidth", "--tab-width");
        Switch("useTabs", "--use-tabs", true);
        Switch("semi", "--no-semi", false);
        Switch("singleQuote", "--single-quote", true);
        Switch("jsxSingleQuote", "--jsx-single-quote", true);
        Switch("bracketSpacing", "--no-bracket-spacing", false);
        Switch("bracketSameLine", "--bracket-same-line", true);
        Switch("singleAttributePerLine", "--single-attribute-per-line", true);
        Switch("vueIndentScriptAndStyle", "--vue-indent-script-and-style", true);
        Choice("trailingComma", "--trailing-comma", "all", "es5", "none");
        Choice("arrowParens", "--arrow-parens", "always", "avoid");
        Choice("endOfLine", "--end-of-line", "lf", "crlf", "cr", "auto");
        Choice("proseWrap", "--prose-wrap", "always", "never", "preserve");
        Choice("quoteProps", "--quote-props", "as-needed", "consistent", "preserve");
        Choice("htmlWhitespaceSensitivity", "--html-whitespace-sensitivity", "css", "strict", "ignore");
        Choice("embeddedLanguageFormatting", "--embedded-language-formatting", "auto", "off");
        return flags.Count == 0 ? flags : ["--config-precedence", "prefer-file", .. flags];
    }

    /// <summary>A config formatter's argument with its ${…} filled in.</summary>
    private static string Fill(string arg, string full, string root, string text, (int Start, int End)? range, JsonObject? options)
    {
        if (!arg.Contains("${", StringComparison.Ordinal))
        {
            return arg;
        }
        var (start, end) = range ?? (0, text.Length);
        return arg
            .Replace("${file}", full, StringComparison.Ordinal)
            .Replace("${relativeFile}", Path.GetRelativePath(root, full), StringComparison.Ordinal)
            .Replace("${fileDirname}", Path.GetDirectoryName(full), StringComparison.Ordinal)
            .Replace("${workspaceFolder}", root, StringComparison.Ordinal)
            .Replace("${tabSize}", Number(Math.Clamp(options.Int("tabSize") ?? 4, 1, 100)), StringComparison.Ordinal)
            .Replace("${insertSpaces}", options.Bool("insertSpaces") == false ? "false" : "true", StringComparison.Ordinal)
            .Replace("${rangeStart}", Number(start), StringComparison.Ordinal)
            .Replace("${rangeEnd}", Number(end), StringComparison.Ordinal)
            .Replace("${startLine}", Number(LineColumn(text, start).Line), StringComparison.Ordinal)
            .Replace("${endLine}", Number(EndLine(text, (start, end))), StringComparison.Ordinal);
    }

    private static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The line (from 1) and column (from 1, in characters as Python and Rust count them) of a UTF-16 offset.</summary>
    internal static (int Line, int Column) LineColumn(string text, int offset)
    {
        int line = 1, column = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else if (!char.IsLowSurrogate(text[i]))
            {
                column++;
            }
        }
        return (line, column);
    }

    /// <summary>The last line a range takes: a range that ends at the start of a line ends on the line before.</summary>
    private static int EndLine(string text, (int Start, int End) range) =>
        LineColumn(text, range.End > range.Start && text[range.End - 1] == '\n' ? range.End - 1 : range.End).Line;

    /// <summary>A UTF-16 offset as UTF-8 bytes (clang-format's), never in the middle of a character.</summary>
    internal static int Utf8Offset(string text, int offset, bool back)
    {
        if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))
        {
            offset += back ? -1 : 1;
        }
        return Encoding.UTF8.GetByteCount(text.AsSpan(0, offset));
    }

    /// <summary>The folders from the file's up to the root of the working directory that holds it, nearest first.</summary>
    private IEnumerable<string> Up(string folder)
    {
        var root = Workspace.Roots.Where(r => Workspace.Within(folder, r)).MaxBy(r => r.Length) ?? Workspace.Root;
        for (var dir = folder; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            yield return dir;
            if (string.Equals(dir, root, PathCompare) || !Workspace.Within(dir, root))
            {
                yield break;
            }
        }
    }

    /// <summary>Whether a program lives in the working directory (by its name, or where it leads).</summary>
    private bool InFolder(string? path) =>
        path is not null && (Workspace.Inside(path) || Workspace.Roots.Any(r => Workspace.Within(Sandbox.Real(path), Sandbox.Real(r))));

    /// <summary>PATH's folders: absolute ones only (a relative one would be the working directory's).</summary>
    private IEnumerable<string> PathFolders() =>
        (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Path.IsPathRooted);

    private string? Which(string name, bool exe = true) =>
        PathFolders().Select(d => Path.Combine(d, exe ? Exe(name) : name)).FirstOrDefault(File.Exists);

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? Existing(params string?[] paths) => paths.FirstOrDefault(p => p is not null && File.Exists(p));

    private string Home() => env("HOME") is { Length: > 0 } h ? h : env("USERPROFILE") is { Length: > 0 } u ? u : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) && new FileInfo(file).Length < 1024 * 1024 ? File.ReadAllText(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
