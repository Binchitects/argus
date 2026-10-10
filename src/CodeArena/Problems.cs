using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A problem a check found: where (a path of the folder, from 1), how bad, its code and what it says.</summary>
internal sealed record Problem(string Path, int Line, int Column, string Severity, string? Code, string Message);

/// <summary>
/// The IDE's Problems: the project's own check run in the folder (its build, type check or linter: "checkCommand" in
/// config.json, else the first found of dotnet build, tsc, npm run lint, cargo check, go vet and ruff), and the errors
/// and warnings it printed, read by the common forms (MSBuild's and tsc's path(line,col), gcc's and most linters'
/// path:line:col). Run by the person, not the agent: no sandbox, as their terminals.
/// </summary>
internal sealed partial class Problems(Workspace workspace, Config config)
{
    /// <summary>The longest a check runs.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>The most problems kept from one run.</summary>
    public const int Most = 2_000;

    private readonly object _gate = new();
    private Task<CheckRun>? _running;

    /// <summary>The last run: when, what ran, how it ended and what it found; null before the first.</summary>
    public CheckRun? Last { get; private set; }

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _running is { IsCompleted: false };
            }
        }
    }

    /// <summary>The project's check: the config's, else one found by the project's files; null: none found.</summary>
    public string? Command()
    {
        if (config.CheckCommand is { Length: > 0 } own)
        {
            return own;
        }
        var root = workspace.Root;
        bool Has(string pattern) => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any();
        if (Has("*.sln") || Has("*.slnx") || Has("*.csproj") || Has("*.fsproj"))
        {
            return "dotnet build -nologo -v q -clp:NoSummary";
        }
        if (File.Exists(Path.Combine(root, "tsconfig.json")))
        {
            return "npx --no-install tsc --noEmit --pretty false -p .";
        }
        if (File.Exists(Path.Combine(root, "package.json")) && File.ReadAllText(Path.Combine(root, "package.json")).Contains("\"lint\"", StringComparison.Ordinal))
        {
            return "npm run --silent lint";
        }
        if (File.Exists(Path.Combine(root, "Cargo.toml")))
        {
            return "cargo check --message-format=short";
        }
        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            return "go vet ./...";
        }
        if (File.Exists(Path.Combine(root, "pyproject.toml")) || Has("*.py"))
        {
            return "ruff check --output-format=concise .";
        }
        return null;
    }

    /// <summary>Runs the check (the one running, when one is) and reads what it found.</summary>
    public Task<CheckRun> RunAsync()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } running)
            {
                return running;
            }
            var command = Command() ?? throw new IdeError(404, "no_check", "No check found for this folder: set \"checkCommand\" in config.json (a build, a type check or a linter).");
            return _running = Task.Run(async () =>
            {
                var run = await RunCommandAsync(command);
                Last = run;
                return run;
            });
        }
    }

    /// <summary>The most of a check's output read: its start and its end, beyond it (a noisy linter).</summary>
    public const int MaxOutput = 4_000_000;

    private async Task<CheckRun> RunCommandAsync(string command)
    {
        var started = DateTimeOffset.Now;
        // cmd.exe takes the command as written, quotes and all; sh as its -c argument.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{command}\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.WorkingDirectory = workspace.Root;
        // Plain output.
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        Proc.Result result;
        try
        {
            // Proc stops it at the limit, and does not wait for a child it left holding the output open.
            result = await Proc.RunAsync(psi, Limit, CancellationToken.None, MaxOutput);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ToolError)
        {
            return new CheckRun(command, started, null, $"It could not be run: {e.Message}", []);
        }
        if (result.TimedOut)
        {
            return new CheckRun(command, started, null, $"It did not end within {Limit.TotalMinutes:0} minutes: stopped.", Read(result.Output));
        }
        var found = Read(result.Output);
        var exit = result.ExitCode < 0 ? (int?)null : result.ExitCode;
        var said = found.Count == 0 && exit != 0 ? Fmt.OneLine(result.Output.Trim(), 400) : null;
        return new CheckRun(command, started, exit, said, found);
    }

    // path(line,col): error CODE: message [project]  (MSBuild, tsc --pretty false)
    [GeneratedRegex(@"^\s*(?<path>[^\s(][^(]*?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning|info)\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex Parenthesised();

    // path:line:col: severity[code]: message, or path:line:col: CODE message (gcc, clang, go, cargo short, ruff, eslint unix)
    [GeneratedRegex(@"^\s*(?<path>[^\s:][^:]*?):(?<line>\d+):(?<col>\d+):?\s*(?:(?<sev>fatal error|error|warning|note|info)(?:\[(?<code>[^\]]+)\])?:\s*)?(?:(?<code2>[A-Z]+\d{2,5})\s+)?(?<msg>.+?)\s*$")]
    private static partial Regex Colons();

    /// <summary>The problems a check printed, in the folder's files only, each once, errors first.</summary>
    public List<Problem> Read(string output)
    {
        var found = new List<Problem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Parenthesised().Match(line);
            if (!m.Success)
            {
                m = Colons().Match(line);
            }
            if (!m.Success || Relative(m.Groups["path"].Value.Trim()) is not { } path)
            {
                continue;
            }
            var code = m.Groups["code"].Success ? m.Groups["code"].Value : m.Groups["code2"].Success ? m.Groups["code2"].Value : null;
            var message = m.Groups["msg"].Value;
            var sev = m.Groups["sev"].Value switch
            {
                "error" or "fatal error" => "error",
                "warning" => "warning",
                "note" or "info" => "info",
                // No word: ESLint's unix form says it in brackets; a linter's rule code (ruff's F401) is a warning; the rest
                // (go vet, Go's compiler) are errors.
                _ when message.Contains("[Error/", StringComparison.Ordinal) => "error",
                _ when message.Contains("[Warning/", StringComparison.Ordinal) => "warning",
                _ when m.Groups["code2"].Success => "warning",
                _ => "error",
            };
            if (!int.TryParse(m.Groups["line"].Value, out var lineNo) || !int.TryParse(m.Groups["col"].Value, out var column) || lineNo < 1)
            {
                continue;
            }
            var p = new Problem(path, lineNo, Math.Max(1, column), sev, code, message);
            if (seen.Add($"{p.Path}:{p.Line}:{p.Column}:{p.Code}:{p.Message}"))
            {
                found.Add(p);
            }
        }
        // The most kept are the worst: errors first.
        return [.. found.OrderBy(p => p.Severity switch { "error" => 0, "warning" => 1, _ => 2 }).ThenBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Line).Take(Most)];
    }

    /// <summary>A path a check printed, as the folder's (relative, with "/"); null: not a file of the folder.</summary>
    private string? Relative(string printed)
    {
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(printed) ? printed : Path.Combine(workspace.Root, printed));
            var rel = Path.GetRelativePath(workspace.Root, full);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) || !File.Exists(full))
            {
                return null;
            }
            return rel.Replace('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>One run of the check: what ran, when, its exit code (null: it did not end, or did not start), what it said when it found nothing it could read, and what it found.</summary>
internal sealed record CheckRun(string Command, DateTimeOffset Started, int? ExitCode, string? Said, List<Problem> Found);
