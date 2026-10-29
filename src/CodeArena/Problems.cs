using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// A problem a check or a task found: where (a path of the folder, from 1), how bad, its code, what it says, and what
/// found it ("check", or "task:" and the task's label).
/// </summary>
/// <param name="Ask">What the agent is asked to fix it, when its source says more than its message (a security review's finding).</param>
internal sealed record Problem(string Path, int Line, int Column, string Severity, string? Code, string Message, string Source = Problems.CheckSource, string? Ask = null);

/// <summary>Which group of a task's own pattern holds what: 0 for none (for the message, the whole line).</summary>
internal sealed record ProblemGroups(int File = 1, int Line = 2, int Column = 3, int Location = 0, int Severity = 0, int Code = 0, int Message = 0);

/// <summary>
/// How a task's output is read as problems (VS Code's problemMatcher): single-line patterns, each a regular expression
/// with its groups (null: the named groups of the check's own forms); the severity when a line says none; the folder
/// relative paths start from (null: the working directory); and, for a task that keeps running, the lines that say it
/// began and ended a round of work.
/// </summary>
internal sealed record ProblemMatcher(
    string Name,
    IReadOnlyList<Regex> Patterns,
    ProblemGroups? Groups = null,
    string Severity = "error",
    string? FileBase = null,
    Regex? Begins = null,
    Regex? Ends = null);

/// <summary>
/// The IDE's Problems: the project's own check run in the folder (its build, type check or linter: "checkCommand" in
/// config.json, else the first found of dotnet build, tsc, npm run lint, cargo check, go vet and ruff), and the errors
/// and warnings it printed, read by the common forms (MSBuild's and tsc's path(line,col), gcc's and most linters'
/// path:line:col). Run by the person, not the agent: no sandbox, as their terminals. A build follows the project's
/// build configuration (Debug or Release, kept in its state). Tasks add what their problem matchers found, each task's
/// in place of its last run's.
/// </summary>
internal sealed partial class Problems(Workspace workspace, Config config, WorkspaceState? state = null)
{
    /// <summary>The longest a check runs.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>The most problems kept from one run.</summary>
    public const int Most = 2_000;

    /// <summary>The source of what the check found.</summary>
    public const string CheckSource = "check";

    private readonly object _gate = new();
    private Task<CheckRun>? _running;
    private readonly Dictionary<string, List<Problem>> _tasks = new(StringComparer.Ordinal);

    /// <summary>What a task's matchers found, in place of what its last run found (none: forgotten).</summary>
    public void Found(string source, List<Problem> found)
    {
        lock (_gate)
        {
            if (found.Count == 0)
            {
                _tasks.Remove(source);
            }
            else
            {
                _tasks[source] = found;
            }
        }
    }

    /// <summary>What the tasks' matchers found, task by task.</summary>
    public List<Problem> FromTasks()
    {
        lock (_gate)
        {
            return [.. _tasks.OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t => t.Value)];
        }
    }

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
        var release = state?.BuildConfiguration == "Release";
        bool Has(string pattern) => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any();
        if (Has("*.sln") || Has("*.slnx") || Has("*.csproj") || Has("*.fsproj"))
        {
            return $"dotnet build -c {(release ? "Release" : "Debug")} -nologo -v q -clp:NoSummary";
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
            return release ? "cargo check --release --message-format=short" : "cargo check --message-format=short";
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

    // path: line N, col N, Error - message (rule)  (ESLint's compact form)
    [GeneratedRegex(@"^\s*(?<path>.+?):\s+line\s+(?<line>\d+),\s+col\s+(?<col>\d+),\s+(?<sev>Error|Warning|Info)\s+-\s+(?<msg>.+?)(?:\s+\((?<code>[^()]+)\))?\s*$")]
    private static partial Regex EslintCompact();

    // path.go:line[:col]: message  (Go's compiler, go vet, go test)
    [GeneratedRegex(@"^\s*(?<path>[^\s:][^:]*?\.go):(?<line>\d+)(?::(?<col>\d+))?:\s*(?<msg>.+?)\s*$")]
    private static partial Regex GoLine();

    // path:line:col - error TS2304: message  (tsc's own form, --pretty: its colours are taken off first)
    [GeneratedRegex(@"^\s*(?<path>[^\s:][^:]*?):(?<line>\d+):(?<col>\d+)\s+-\s+(?<sev>error|warning|suggestion|message)\s+(?<code>TS\d+)\s*:\s*(?<msg>.*?)\s*$")]
    private static partial Regex TscPretty();

    // tsc --watch: a round of compiling begins, and ends.
    [GeneratedRegex(@"(?:Starting compilation in watch mode|File change detected\. Starting incremental compilation)\.\.\.")]
    private static partial Regex TscWatchBegins();

    [GeneratedRegex(@"(?:Compilation complete\.|Found \d+ errors?\.) Watching for file changes\.")]
    private static partial Regex TscWatchEnds();

    // Colours, cursor moves and window titles: what a terminal does, not what a program said.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Escapes();

    private static readonly ProblemMatcher Check = new(CheckSource, [Parenthesised(), TscPretty(), Colons()]);

    /// <summary>The problem matchers of VS Code's this reads, by the check's own forms; null for one it does not.</summary>
    public static ProblemMatcher? Named(string name) => name switch
    {
        "$msCompile" => new(name, [Parenthesised()]),
        "$tsc" => new(name, [Parenthesised(), TscPretty()]),
        "$tsc-watch" => new(name, [Parenthesised(), TscPretty()], Begins: TscWatchBegins(), Ends: TscWatchEnds()),
        "$gcc" => new(name, [Colons()]),
        "$eslint-compact" => new(name, [EslintCompact()]),
        "$go" => new(name, [GoLine()]),
        _ => null,
    };

    /// <summary>A terminal's line as text: its escape sequences gone, and what a carriage return wrote over gone too.</summary>
    public static string Plain(string line)
    {
        line = Escapes().Replace(line.TrimEnd('\r'), "");
        var cr = line.LastIndexOf('\r');
        return cr >= 0 ? line[(cr + 1)..] : line;
    }

    /// <summary>The problems a check printed, in the folder's files only, each once, errors first.</summary>
    public List<Problem> Read(string output) => Read(output, [Check], CheckSource);

    /// <summary>The problems these matchers find in a task's output, in the folder's files only, each once, errors first.</summary>
    public List<Problem> Read(string output, IReadOnlyList<ProblemMatcher> matchers, string source)
    {
        var found = new List<Problem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            if (Match(Plain(raw), matchers, source) is { } p && seen.Add($"{p.Path}:{p.Line}:{p.Column}:{p.Code}:{p.Message}"))
            {
                found.Add(p);
            }
        }
        // The most kept are the worst: errors first.
        return [.. found.OrderBy(p => p.Severity switch { "error" => 0, "warning" => 1, _ => 2 }).ThenBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Line).Take(Most)];
    }

    private Problem? Match(string line, IReadOnlyList<ProblemMatcher> matchers, string source)
    {
        foreach (var matcher in matchers)
        {
            foreach (var pattern in matcher.Patterns)
            {
                Match m;
                try
                {
                    m = pattern.Match(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }
                if (m.Success && (matcher.Groups is { } groups ? Custom(m, matcher, groups, source) : Known(m, matcher, source)) is { } p)
                {
                    return p;
                }
            }
        }
        return null;
    }

    /// <summary>A line one of the check's own forms matched, by its named groups.</summary>
    private Problem? Known(Match m, ProblemMatcher matcher, string source)
    {
        if (Relative(m.Groups["path"].Value.Trim(), matcher.FileBase) is not { } path)
        {
            return null;
        }
        var code = m.Groups["code"].Success ? m.Groups["code"].Value : m.Groups["code2"].Success ? m.Groups["code2"].Value : null;
        var message = m.Groups["msg"].Value;
        var sev = m.Groups["sev"].Value.ToLowerInvariant() switch
        {
            "error" or "fatal error" => "error",
            "warning" => "warning",
            "note" or "info" or "suggestion" or "message" => "info",
            // No word: ESLint's unix form says it in brackets; a linter's rule code (ruff's F401) is a warning; the rest
            // (go vet, Go's compiler) are errors.
            _ when message.Contains("[Error/", StringComparison.Ordinal) => "error",
            _ when message.Contains("[Warning/", StringComparison.Ordinal) => "warning",
            _ when m.Groups["code2"].Success => "warning",
            _ => "error",
        };
        if (!int.TryParse(m.Groups["line"].Value, out var lineNo) || lineNo < 1)
        {
            return null;
        }
        var column = m.Groups["col"].Success && int.TryParse(m.Groups["col"].Value, out var col) ? col : 1;
        return new Problem(path, lineNo, Math.Max(1, column), sev, code, message, source);
    }

    /// <summary>A line a task's own pattern matched, by the groups it names (VS Code's file, line, column or location, severity, code, message).</summary>
    private Problem? Custom(Match m, ProblemMatcher matcher, ProblemGroups g, string source)
    {
        string? Group(int index) => index > 0 && index < m.Groups.Count && m.Groups[index].Success ? m.Groups[index].Value : null;
        static int? Number(string? text) => int.TryParse(text?.Trim(), out var n) ? n : null;
        if (Group(g.File) is not { Length: > 0 } file || Relative(file.Trim(), matcher.FileBase) is not { } path)
        {
            return null;
        }
        int? line, column;
        if (g.Location > 0)
        {
            // "line", "line,column" or "line,column,endLine,endColumn"
            var parts = (Group(g.Location) ?? "").Split(',');
            line = Number(parts[0]);
            column = parts.Length > 1 ? Number(parts[1]) : null;
        }
        else
        {
            line = Number(Group(g.Line));
            column = Number(Group(g.Column));
        }
        var said = Group(g.Severity)?.Trim().ToLowerInvariant();
        var severity = said switch
        {
            { Length: > 0 } when said[0] == 'e' => "error",
            { Length: > 0 } when said[0] == 'w' => "warning",
            { Length: > 0 } when said[0] is 'i' or 'n' or 'h' => "info",
            _ => matcher.Severity,
        };
        var message = g.Message > 0 ? Group(g.Message) ?? "" : m.Value;
        // A pattern with no line says something about the whole file: its first line.
        return new Problem(path, Math.Max(1, line ?? 1), Math.Max(1, column ?? 1), severity, Group(g.Code), message.Trim(), source);
    }

    /// <summary>A path a check printed, as the folder's (relative, with "/"); null: not a file of the folder.</summary>
    private string? Relative(string printed, string? from = null)
    {
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(printed) ? printed : Path.Combine(from ?? workspace.Root, printed));
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
