using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>The tools on the person's own machine: files, search, the shell, git, the to-do list and sub-agents.</summary>
internal static class LocalTools
{
    public const int MaxLines = 2000;
    private const int MaxLineChars = 2000;
    private const int MaxResults = 200;

    public static List<ToolDef> All(string? shell = null) =>
    [
        new()
        {
            Name = "read_file",
            Kind = ToolKind.Read,
            Description = "Read a text file, with line numbers. Up to 2000 lines at a time: give offset (the first line, from 1) and limit for the rest of a long file.",
            Parameters = Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"The file, relative to the working directory or absolute."},
                  "offset":{"type":"integer","description":"The first line to read, from 1."},
                  "limit":{"type":"integer","description":"How many lines to read (at most 2000)."}},
                 "required":["path"]}
                """),
            Summary = a => a.Str("path") + (a.Int("offset") is { } o ? $":{o}" : ""),
            Run = ReadFile,
        },
        new()
        {
            Name = "write_file",
            Kind = ToolKind.Edit,
            Description = "Create a file, or replace a file's whole content. To change part of an existing file, use edit_file.",
            Parameters = Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},
                  "content":{"type":"string","description":"The whole new content."}},
                 "required":["path","content"]}
                """),
            Summary = a => a.Str("path") ?? "",
            Run = WriteFile,
        },
        new()
        {
            Name = "edit_file",
            Kind = ToolKind.Edit,
            Description = "Replace an exact piece of a file. old_string must match the file exactly (whitespace and indentation too) and only once: include enough lines around it to be unique, or set replace_all to change every occurrence. Read the file first. An empty old_string creates a new file.",
            Parameters = Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},
                  "old_string":{"type":"string","description":"The exact text to replace."},
                  "new_string":{"type":"string","description":"The text to put in its place."},
                  "replace_all":{"type":"boolean","description":"Replace every occurrence (default: false)."}},
                 "required":["path","old_string","new_string"]}
                """),
            Summary = a => a.Str("path") ?? "",
            Run = EditFile,
        },
        new()
        {
            Name = "list_dir",
            Kind = ToolKind.Read,
            Description = "List a folder: subfolders (ending in /) first, then files with their sizes.",
            Parameters = Schema("""
                {"type":"object","properties":{"path":{"type":"string","description":"The folder (default: the working directory)."}}}
                """),
            Summary = a => a.Str("path") ?? ".",
            Run = ListDir,
        },
        new()
        {
            Name = "glob",
            Kind = ToolKind.Read,
            Description = "Find files by name pattern, newest first: **/*.cs, src/**/test_*.py, *.{ts,tsx}. A pattern without / matches file names at any depth. Ignored files (.gitignore) are left out.",
            Parameters = Schema("""
                {"type":"object","properties":{
                  "pattern":{"type":"string"},
                  "path":{"type":"string","description":"The folder to search (default: the working directory)."}},
                 "required":["pattern"]}
                """),
            Summary = a => a.Str("pattern") + (a.Str("path") is { } p ? $" in {p}" : ""),
            Run = Glob,
        },
        new()
        {
            Name = "grep",
            Kind = ToolKind.Read,
            Description = "Search file contents with a regular expression (.NET syntax). output: \"content\" (matching lines with path and line number, the default), \"files\" (paths only) or \"count\". Ignored files (.gitignore) and binary files are skipped.",
            Parameters = Schema("""
                {"type":"object","properties":{
                  "pattern":{"type":"string","description":"The regular expression."},
                  "path":{"type":"string","description":"A folder or file (default: the working directory)."},
                  "glob":{"type":"string","description":"Only files matching this pattern, e.g. *.cs"},
                  "ignore_case":{"type":"boolean"},
                  "context":{"type":"integer","description":"Lines to show before and after each match (0-10)."},
                  "output":{"type":"string","enum":["content","files","count"]}},
                 "required":["pattern"]}
                """),
            Summary = a => a.Str("pattern") + (a.Str("glob") is { } g ? $" ({g})" : "") + (a.Str("path") is { } p ? $" in {p}" : ""),
            Run = Grep,
        },
        new()
        {
            Name = "run_shell",
            Kind = ToolKind.Shell,
            Description = $"Run a shell command in the working directory ({ShellName(shell)}) and get its output (stdout and stderr together) and exit code. Each call starts fresh: cd does not carry over. Default timeout 120 s, at most 600. Not for reading or searching files: use read_file, grep and glob.",
            Parameters = Schema("""
                {"type":"object","properties":{
                  "command":{"type":"string"},
                  "timeout_seconds":{"type":"integer","description":"Stop it after this long (default 120, at most 600)."},
                  "description":{"type":"string","description":"What it does, in a few words, for the person."}},
                 "required":["command"]}
                """),
            Summary = a => a.Str("command") ?? "",
            Run = RunShell,
        },
        new()
        {
            Name = "git",
            Kind = ToolKind.Read,
            Description = "Read the repository's state without changing it: status, diff, log, show, blame, branch (listing), ls-files, rev-parse. args are git's own arguments, e.g. [\"diff\", \"--stat\"] or [\"log\", \"-5\", \"--oneline\"]. Anything that changes the repository goes through run_shell.",
            Parameters = Schema("""
                {"type":"object","properties":{"args":{"type":"array","items":{"type":"string"}}},"required":["args"]}
                """),
            Summary = a => string.Join(' ', (a["args"] as JsonArray ?? []).Select(x => x?.ToString())),
            Run = Git,
        },
        new()
        {
            Name = "todo_write",
            Kind = ToolKind.Read,
            Description = "Keep a to-do list for work of several steps: send the whole list each time, each item pending, in_progress (one at a time) or completed. The person sees it.",
            Parameters = Schema("""
                {"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{
                  "content":{"type":"string"},
                  "status":{"type":"string","enum":["pending","in_progress","completed"]}},
                 "required":["content","status"]}}},
                 "required":["todos"]}
                """),
            Summary = a => $"{(a["todos"] as JsonArray)?.Count ?? 0} items",
            Run = Todo,
        },
    ];

    /// <summary>The sub-agent tool: a fresh context with read-only tools, for research that would crowd the main one.</summary>
    public static ToolDef SubAgentTool() => new()
    {
        Name = "task",
        Kind = ToolKind.Agent,
        Description = "Hand a self-contained piece of research to a sub-agent with a fresh context and read-only tools (read, search, git, Arena's tools). It cannot edit or run commands. It returns one report: say exactly what to find and what to report. Several task calls at once run in parallel.",
        Parameters = Schema("""
            {"type":"object","properties":{
              "description":{"type":"string","description":"Three to five words for the person."},
              "prompt":{"type":"string","description":"The full task: the sub-agent knows nothing of this conversation."}},
             "required":["description","prompt"]}
            """),
        Summary = a => a.Str("description") ?? "",
        Run = async (a, c, ct) =>
        {
            var prompt = Required(a, "prompt");
            if (c.SubAgent is null)
            {
                throw new ToolError("Sub-agents cannot start sub-agents.");
            }
            var report = await c.SubAgent(a.Str("description") ?? "task", prompt, ct);
            return new ToolResult(report) { Display = Fmt.OneLine(report, 160) };
        },
    };

    private static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string Required(JsonObject a, string name) =>
        a.Str(name) is { Length: > 0 } v ? v : throw new ToolError($"{name} is required.");

    private static async Task<ToolResult> ReadFile(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var path = c.Workspace.Resolve(Required(a, "path"));
        var show = c.Workspace.Show(path);
        if (Directory.Exists(path))
        {
            throw new ToolError($"{show} is a folder: use list_dir.");
        }
        if (!File.Exists(path))
        {
            throw new ToolError($"{show} does not exist.");
        }
        if (Files.LooksBinary(path))
        {
            return new ToolResult($"{show} is a binary file ({Files.Size(new FileInfo(path).Length)}): not shown.");
        }
        var offset = Math.Max(1, a.Int("offset") ?? 1);
        var limit = Math.Clamp(a.Int("limit") ?? MaxLines, 1, MaxLines);
        var sb = new StringBuilder();
        var number = 0;
        var shown = 0;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                number++;
                if (number < offset || shown >= limit)
                {
                    continue;
                }
                shown++;
                sb.Append(number.ToString().PadLeft(6)).Append('\t')
                    .Append(line.Length > MaxLineChars ? line[..MaxLineChars] + " … (line cut)" : line).Append('\n');
            }
        }
        if (number == 0)
        {
            return new ToolResult($"{show} is empty.") { Display = "empty" };
        }
        if (shown == 0)
        {
            throw new ToolError($"{show} has {number} lines: offset {offset} is past the end.");
        }
        var last = offset + shown - 1;
        if (last < number)
        {
            sb.Append($"(lines {offset}-{last} of {number}; read on with offset={last + 1})\n");
        }
        return new ToolResult(sb.ToString()) { Display = $"{shown} line{(shown == 1 ? "" : "s")}" + (last < number || offset > 1 ? $" ({offset}-{last} of {number})" : "") };
    }

    private static Task<ToolResult> WriteFile(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var path = c.Workspace.Resolve(Required(a, "path"));
        var content = a.Str("content") ?? throw new ToolError("content is required.");
        var show = c.Workspace.Show(path);
        if (Directory.Exists(path))
        {
            throw new ToolError($"{show} is a folder.");
        }
        string? before = null;
        var bom = false;
        if (File.Exists(path))
        {
            (before, bom) = Files.ReadText(path);
        }
        Files.WriteText(path, content, bom);
        var lines = Diff.Lines(content).Length;
        var display = before is null
            ? c.Ui.Green($"new file, {lines} lines") + "\n" + Fmt.Indent(c.Ui.Dim(Fmt.Head(content, 8)), "")
            : Diff.Render(before, content, c.Ui);
        return Task.FromResult(new ToolResult(before is null ? $"Created {show} ({lines} lines)." : $"Wrote {show} ({lines} lines).") { Display = display, Change = new FileChange(show, before, content) });
    }

    /// <summary>
    /// An exact, unique replacement. A file with Windows line endings matches an
    /// old_string written with plain newlines, and keeps its endings.
    /// </summary>
    private static Task<ToolResult> EditFile(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var path = c.Workspace.Resolve(Required(a, "path"));
        var show = c.Workspace.Show(path);
        var oldText = a.Str("old_string") ?? throw new ToolError("old_string is required.");
        var newText = a.Str("new_string") ?? throw new ToolError("new_string is required.");
        var all = a.Bool("replace_all") ?? false;
        if (Directory.Exists(path))
        {
            throw new ToolError($"{show} is a folder.");
        }
        if (!File.Exists(path))
        {
            if (oldText.Length > 0)
            {
                throw new ToolError($"{show} does not exist. To create it, give an empty old_string (or use write_file).");
            }
            Files.WriteText(path, newText, false);
            return Task.FromResult(new ToolResult($"Created {show}.") { Display = c.Ui.Green($"new file, {Diff.Lines(newText).Length} lines"), Change = new FileChange(show, null, newText) });
        }
        var (text, bom) = Files.ReadText(path);
        if (oldText.Length == 0)
        {
            if (text.Length > 0)
            {
                throw new ToolError($"old_string is empty but {show} is not: give the exact text to replace.");
            }
            Files.WriteText(path, newText, bom);
            return Task.FromResult(new ToolResult($"Wrote {show}.") { Display = Diff.Render(text, newText, c.Ui), Change = new FileChange(show, text, newText) });
        }
        if (oldText == newText)
        {
            throw new ToolError("old_string and new_string are the same: nothing to change.");
        }
        var count = Occurrences(text, oldText);
        if (count == 0 && text.Contains("\r\n", StringComparison.Ordinal) && !oldText.Contains("\r\n", StringComparison.Ordinal))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            count = Occurrences(text, oldText);
        }
        if (count == 0)
        {
            throw new ToolError($"old_string was not found in {show}. It must match exactly, whitespace and indentation included: read the file again and copy the text.");
        }
        if (count > 1 && !all)
        {
            throw new ToolError($"old_string appears {count} times in {show}. Include more of the lines around it so it is unique, or set replace_all to change all {count}.");
        }
        string updated;
        if (all)
        {
            updated = text.Replace(oldText, newText, StringComparison.Ordinal);
        }
        else
        {
            var at = text.IndexOf(oldText, StringComparison.Ordinal);
            updated = string.Concat(text.AsSpan(0, at), newText, text.AsSpan(at + oldText.Length));
        }
        Files.WriteText(path, updated, bom);
        return Task.FromResult(new ToolResult(count == 1 ? $"Edited {show}." : $"Edited {show}: {count} replacements.") { Display = Diff.Render(text, updated, c.Ui), Change = new FileChange(show, text, updated) });
    }

    internal static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static Task<ToolResult> ListDir(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var path = c.Workspace.Resolve(a.Str("path"));
        var show = c.Workspace.Show(path);
        if (!Directory.Exists(path))
        {
            throw new ToolError(File.Exists(path) ? $"{show} is a file: use read_file." : $"{show} does not exist.");
        }
        var dirs = Directory.EnumerateDirectories(path).Select(d => Path.GetFileName(d) + "/").Order(StringComparer.OrdinalIgnoreCase).ToList();
        var files = Directory.EnumerateFiles(path).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{f.Name}  ({Files.Size(f.Length)})").ToList();
        var entries = dirs.Concat(files).ToList();
        if (entries.Count == 0)
        {
            return Task.FromResult(new ToolResult($"{show} is empty.") { Display = "empty" });
        }
        var text = string.Join('\n', entries.Take(500)) + (entries.Count > 500 ? $"\n… and {entries.Count - 500} more" : "");
        return Task.FromResult(new ToolResult(text) { Display = $"{Plural(dirs.Count, "folder")}, {Plural(files.Count, "file")}" });
    }

    private static Task<ToolResult> Glob(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var pattern = Required(a, "pattern");
        var dir = c.Workspace.Resolve(a.Str("path"));
        if (!Directory.Exists(dir))
        {
            throw new ToolError($"{c.Workspace.Show(dir)} is not a folder.");
        }
        var regex = Files.Glob(pattern);
        var found = Files.Under(dir, ct)
            .Where(f => regex.IsMatch(Path.GetRelativePath(dir, f).Replace('\\', '/')))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        if (found.Count == 0)
        {
            return Task.FromResult(new ToolResult($"No files match {pattern}.") { Display = "no files" });
        }
        var text = string.Join('\n', found.Take(MaxResults).Select(f => c.Workspace.Show(f.FullName)))
                   + (found.Count > MaxResults ? $"\n… and {found.Count - MaxResults} more: narrow the pattern" : "");
        return Task.FromResult(new ToolResult(text) { Display = Plural(found.Count, "file") });
    }

    private static Task<ToolResult> Grep(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var pattern = Required(a, "pattern");
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | (a.Bool("ignore_case") == true ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw new ToolError($"The pattern is not a valid regular expression: {e.Message}");
        }
        var root = c.Workspace.Resolve(a.Str("path"));
        var filter = a.Str("glob") is { Length: > 0 } g ? Files.Glob(g) : null;
        var context = Math.Clamp(a.Int("context") ?? 0, 0, 10);
        var mode = a.Str("output") ?? "content";
        IEnumerable<string> files = File.Exists(root) ? [root]
            : Directory.Exists(root) ? Files.Under(root, ct).Where(f => filter is null || filter.IsMatch(Path.GetRelativePath(root, f).Replace('\\', '/')))
            : throw new ToolError($"{c.Workspace.Show(root)} does not exist.");
        var output = new List<string>();
        var matches = 0;
        var matchedFiles = 0;
        var cut = false;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > 5_000_000 || Files.LooksBinary(file))
            {
                continue;
            }
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var hits = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                try
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        hits.Add(i);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
            }
            if (hits.Count == 0)
            {
                continue;
            }
            matchedFiles++;
            matches += hits.Count;
            var show = c.Workspace.Show(file);
            if (output.Count >= MaxResults)
            {
                cut = true;
                continue;
            }
            switch (mode)
            {
                case "files":
                    output.Add(show);
                    break;
                case "count":
                    output.Add($"{show}: {hits.Count}");
                    break;
                default:
                    var printed = -1;
                    foreach (var hit in hits)
                    {
                        if (output.Count >= MaxResults)
                        {
                            cut = true;
                            break;
                        }
                        var from = Math.Max(hit - context, printed + 1);
                        if (context > 0 && printed >= 0 && from > printed + 1)
                        {
                            output.Add("--");
                        }
                        for (var i = from; i <= Math.Min(lines.Length - 1, hit + context); i++)
                        {
                            var line = lines[i].Length > 500 ? lines[i][..500] + "…" : lines[i];
                            output.Add($"{show}:{i + 1}{(i == hit ? ':' : '-')} {line}");
                            printed = i;
                        }
                    }
                    break;
            }
        }
        if (matches == 0)
        {
            return Task.FromResult(new ToolResult("No matches.") { Display = "no matches" });
        }
        var text = string.Join('\n', output) + (cut ? $"\n… cut at {MaxResults} lines: {matches} matches in {matchedFiles} files; narrow the search" : "");
        return Task.FromResult(new ToolResult(text) { Display = $"{Plural(matches, "match", "matches")} in {Plural(matchedFiles, "file")}" });
    }

    /// <summary>The shell run_shell uses: the configured one, else bash (sh when missing); cmd.exe on Windows.</summary>
    public static string ShellName(string? configured)
    {
        if (configured is { Length: > 0 })
        {
            return configured;
        }
        if (OperatingSystem.IsWindows())
        {
            return "cmd.exe";
        }
        return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    private static async Task<ToolResult> RunShell(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var command = Required(a, "command");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Int("timeout_seconds") ?? 120, 1, 600));
        var shell = ShellName(c.Shell);
        var psi = new ProcessStartInfo(shell) { WorkingDirectory = c.Workspace.Root };
        var name = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
        if (name == "cmd")
        {
            // cmd.exe has its own quoting: the command goes on its line as it is.
            psi.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            psi.ArgumentList.Add(name is "pwsh" or "powershell" ? "-NoProfile" : "-c");
            if (name is "pwsh" or "powershell")
            {
                psi.ArgumentList.Add("-Command");
            }
            psi.ArgumentList.Add(command);
        }
        var run = await Proc.RunAsync(psi, timeout, ct);
        var status = run.TimedOut ? $"timed out after {timeout.TotalSeconds:0} s (stopped)" : $"exit code {run.ExitCode}";
        var text = (run.Output.Length > 0 ? run.Output.TrimEnd() + "\n" : "(no output)\n") + $"[{status}]";
        var display = (run.Output.Length > 0 ? Fmt.Tail(run.Output.TrimEnd(), 8) + "\n" : "") + (run.ExitCode == 0 && !run.TimedOut ? c.Ui.Dim(status) : c.Ui.Red(status));
        return new ToolResult(text, run.ExitCode != 0 || run.TimedOut) { Display = display };
    }

    private static readonly HashSet<string> GitReading = ["status", "diff", "log", "show", "blame", "branch", "ls-files", "rev-parse", "shortlog", "describe"];

    private static async Task<ToolResult> Git(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var args = (a["args"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
        if (args.Count > 0 && args[0] == "git")
        {
            args.RemoveAt(0);
        }
        if (args.Count == 0 || !GitReading.Contains(args[0]))
        {
            throw new ToolError($"git here only reads ({string.Join(", ", GitReading)}). Use run_shell for {(args.Count > 0 ? args[0] : "the rest")}.");
        }
        // Options that write files, run programs or change branches stay with run_shell.
        if (args.Any(x => x.StartsWith("--output", StringComparison.Ordinal) || x == "--ext-diff" || x.StartsWith("--exec", StringComparison.Ordinal))
            || (args[0] == "branch" && args.Skip(1).Any(x => !(x is "-a" or "--all" or "-r" or "--remotes" or "-v" or "-vv" or "--list" or "--show-current" or "--merged" or "--no-merged" or "--contains"))))
        {
            throw new ToolError("That git call changes something or runs a program: use run_shell.");
        }
        var psi = new ProcessStartInfo("git") { WorkingDirectory = c.Workspace.Root };
        foreach (var arg in new[] { "--no-pager", "-c", "color.ui=never", "-c", "core.fsmonitor=", "-c", "core.quotepath=off" })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.ArgumentList.Add(args[0]);
        if (args[0] is "diff" or "log" or "show")
        {
            psi.ArgumentList.Add("--no-ext-diff");
        }
        foreach (var arg in args.Skip(1))
        {
            psi.ArgumentList.Add(arg);
        }
        Proc.Result run;
        try
        {
            run = await Proc.RunAsync(psi, TimeSpan.FromSeconds(60), ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ToolError("git is not installed here.");
        }
        var output = run.Output.TrimEnd();
        var text = output.Length > 0 ? output : "(no output)";
        if (run.ExitCode != 0)
        {
            text += $"\n[exit code {run.ExitCode}]";
        }
        return new ToolResult(text, run.ExitCode != 0) { Display = Fmt.Head(text, 6) };
    }

    private static Task<ToolResult> Todo(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var items = (a["todos"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(t => new TodoItem(t.Str("content") ?? "", t.Str("status") is "in_progress" or "completed" ? t.Str("status")! : "pending"))
            .Where(t => t.Content.Length > 0)
            .ToList();
        c.Todos = items;
        c.TodosChanged?.Invoke(items);
        var done = items.Count(t => t.Status == "completed");
        return Task.FromResult(new ToolResult($"The to-do list has {items.Count} items: {done} done, {items.Count(t => t.Status == "in_progress")} in progress, {items.Count(t => t.Status == "pending")} pending.")
        {
            Display = RenderTodos(items, c.Ui),
        });
    }

    public static string RenderTodos(IReadOnlyList<TodoItem> items, Ui ui) => items.Count == 0
        ? ui.Dim("(empty)")
        : string.Join('\n', items.Select(t => t.Status switch
        {
            "completed" => ui.Green("☒ ") + ui.Dim(t.Content),
            "in_progress" => ui.Yellow("◐ ") + ui.Bold(t.Content),
            _ => "☐ " + t.Content,
        }));
}

/// <summary>Running a process with a timeout: its output kept within bounds (the start and the end), its whole tree stopped when time is up.</summary>
internal static class Proc
{
    public sealed record Result(int ExitCode, string Output, bool TimedOut);

    public static async Task<Result> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct, int maxChars = 30_000)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["CODE_ARENA"] = "1";
        // The person's key stays with Code Arena, not with the commands the model runs.
        psi.Environment.Remove("ARENA_API_KEY");
        using var process = Process.Start(psi) ?? throw new ToolError($"Could not start {psi.FileName}.");
        process.StandardInput.Close();
        var output = new Bounded(maxChars);
        var readers = new[] { Pump(process.StandardOutput, output), Pump(process.StandardError, output) };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }
            if (!timedOut)
            {
                throw;
            }
        }
        // A child left in the background can hold the pipes open: do not wait for it.
        await Task.WhenAny(Task.WhenAll(readers), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        return new Result(process.HasExited ? process.ExitCode : -1, output.ToString(), timedOut);
    }

    private static async Task Pump(StreamReader reader, Bounded output)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer)) > 0)
            {
                output.Append(buffer.AsSpan(0, n));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The process is gone.
        }
    }

    /// <summary>The first and the last part of a long output, with how much was left out between them.</summary>
    private sealed class Bounded(int max)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _head = new();
        private readonly StringBuilder _tail = new();
        private long _dropped;

        public void Append(ReadOnlySpan<char> text)
        {
            lock (_gate)
            {
                var room = max / 3 - _head.Length;
                if (room > 0)
                {
                    var take = Math.Min(room, text.Length);
                    _head.Append(text[..take]);
                    text = text[take..];
                }
                _tail.Append(text);
                var keep = max - max / 3;
                if (_tail.Length > keep * 2)
                {
                    _dropped += _tail.Length - keep;
                    _tail.Remove(0, _tail.Length - keep);
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                var keep = max - max / 3;
                var dropped = _dropped + Math.Max(0, _tail.Length - keep);
                var tail = _tail.Length > keep ? _tail.ToString(_tail.Length - keep, keep) : _tail.ToString();
                return dropped > 0 ? $"{_head}\n… ({dropped:N0} characters cut) …\n{tail}" : _head.ToString() + tail;
            }
        }
    }
}
