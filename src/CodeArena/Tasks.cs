using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// A task as .vscode/tasks.json or the project's files give it: VS Code's shape, with its part for this system merged in
/// and its variables not filled in yet; where it came from ("workspace" or "detected"); and its line in tasks.json.
/// </summary>
internal sealed record TaskDef(string Label, string Source, JsonObject Task, int? Line = null)
{
    public const string Workspace = "workspace";
    public const string Detected = "detected";

    /// <summary>"build", "test", or null.</summary>
    public string? Group => Task["group"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var kind) => kind,
        JsonObject o => o.Str("kind"),
        _ => null,
    };

    /// <summary>Whether it is its group's default (VS Code also takes a glob there: a default too).</summary>
    public bool IsDefault => Task["group"] is JsonObject o && (o.Bool("isDefault") == true || o.Str("isDefault") is { Length: > 0 } and not "false");

    /// <summary>The labels of the tasks it waits for.</summary>
    public IReadOnlyList<string> DependsOn => Task["dependsOn"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var label) => [label],
        JsonArray a => [.. a.Select(d => d is JsonValue v && v.TryGetValue<string>(out var label) ? label : d.Str("label")).OfType<string>()],
        _ => [],
    };

    /// <summary>Whether what it waits for runs one after another (dependsOrder sequence), not all at once.</summary>
    public bool Sequence => Task.Str("dependsOrder") == "sequence";

    /// <summary>Whether it keeps running (a watcher, a server): ready when its matcher's end pattern shows.</summary>
    public bool Background => Task.Bool("isBackground") == true;

    /// <summary>Its type: shell, process, or one of the VS Code extensions' this reads (npm, typescript).</summary>
    public string Type => Task.Str("type") ?? "process";

    /// <summary>The file it is in, for the person; null for a detected one.</summary>
    public string? File => Source == Workspace ? Tasks.Shown : null;
}

/// <summary>How a task's run went: its exit code (null: it keeps running, ready), and its terminal (null: it only waited for others).</summary>
internal sealed record TaskOutcome(string Label, int? ExitCode, Terminal? Terminal)
{
    public bool Failed => ExitCode is { } code && code != 0;
}

/// <summary>A task ready to run: its terminal's program (null: it only waits for others), the line shown for it, and how its output is read.</summary>
internal sealed record TaskPlan(TaskDef Def, TerminalStart? Start, string? Command, IReadOnlyList<ProblemMatcher> Matchers);

/// <summary>Every task: tasks.json's, then the detected ones of other labels; tasks.json's inputs; and what is wrong.</summary>
internal sealed record TaskSet(List<TaskDef> Tasks, Dictionary<string, JsonObject> Inputs, List<JsonObject> Errors)
{
    public TaskDef? Find(string? label) => Tasks.FirstOrDefault(t => t.Label == label);
}

/// <summary>
/// The IDE's tasks, as VS Code runs them: .vscode/tasks.json's (version 2.0.0: shell and process tasks with their
/// options, groups, dependsOn in sequence or in parallel, a part per system, isBackground, problem matchers; npm and
/// typescript tasks too) and the ones the project's files give (detected). A task runs in a terminal of its own, used
/// again by its next run; what it waits for runs first, and one that fails stops what waits for it. One that keeps
/// running is ready when its matcher's end pattern shows (escape sequences dropped). What its problem matchers find goes
/// to Problems as "task:" and its label. tasks.json's tasks run only in a trusted folder, its commands shown first;
/// detected ones show their command, and run anywhere. runOn (folderOpen) is never acted on.
/// </summary>
internal sealed partial class Tasks(Workspace workspace, IdeFiles files, Terminals terminals, Problems problems, WorkspaceState state, Func<string, string?> env)
{
    /// <summary>The file, as the person reads its name.</summary>
    public const string Shown = ".vscode/tasks.json";

    private const string Matchers = "$msCompile, $tsc, $tsc-watch, $gcc, $eslint-compact, $go";

    // A task's keys whose variables are not filled in: what names it, orders it, or reads its output.
    private static readonly HashSet<string> AsWritten = new(StringComparer.Ordinal)
    {
        "label", "taskName", "type", "dependsOn", "dependsOrder", "group", "detail", "presentation", "runOptions", "isBackground", "problemMatcher", "icon", "hide", "promptOnClose",
    };

    private readonly object _gate = new();

    // Each task running: its terminal, and its run (done when it is ready, or ended). Kept while its program runs.
    private readonly Dictionary<string, (Terminal Terminal, Task<TaskOutcome> Ready)> _running = new(StringComparer.Ordinal);

    /// <summary>Every task, its inputs and what is wrong; read anew each time (the files change as the person edits them).</summary>
    public TaskSet Load()
    {
        var errors = new List<JsonObject>();
        var tasks = new List<TaskDef>();
        var inputs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var (json, text) = VsCodeJson.Read(workspace, "tasks.json", errors);
        if (json is not null && json.Str("version") != "2.0.0")
        {
            errors.Add(VsCodeJson.Error(Shown, VsCodeJson.LineOf(text, "version") ?? 1, "Only version 2.0.0 of tasks.json is read."));
        }
        else if (json is not null)
        {
            var options = VsCodeJson.ForThisSystem(json)["options"] as JsonObject;
            foreach (var node in json["tasks"] as JsonArray ?? [])
            {
                if (node is not JsonObject raw)
                {
                    errors.Add(VsCodeJson.Error(Shown, null, "A task is not an object."));
                    continue;
                }
                var task = VsCodeJson.ForThisSystem(raw);
                if (options is not null)
                {
                    task["options"] = VsCodeJson.Merge(options, task["options"] as JsonObject);
                }
                if ((task.Str("label") ?? task.Str("taskName")) is not { Length: > 0 } label)
                {
                    errors.Add(VsCodeJson.Error(Shown, null, "A task has no label."));
                    continue;
                }
                var line = VsCodeJson.LineOf(text, raw.ContainsKey("label") ? "label" : "taskName", label);
                if (tasks.Any(t => t.Label == label))
                {
                    errors.Add(VsCodeJson.Error(Shown, line, $"Two tasks are labelled {label}: the first is used."));
                    continue;
                }
                tasks.Add(new TaskDef(label, TaskDef.Workspace, task, line));
            }
            inputs = RunVariables.Inputs(json["inputs"]);
        }
        foreach (var detected in RunDetect.Tasks(workspace.Root, state.BuildConfiguration, env))
        {
            var label = detected.Str("label")!;
            if (tasks.All(t => t.Label != label))
            {
                tasks.Add(new TaskDef(label, TaskDef.Detected, detected));
            }
        }
        var set = new TaskSet(tasks, inputs, errors);
        Check(set);
        return set;
    }

    /// <summary>What is wrong with tasks.json's tasks, said in the set's errors: a type not read, no command, a task waited for that is not there, a circle, a matcher not read.</summary>
    private void Check(TaskSet set)
    {
        var circles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in set.Tasks.Where(t => t.Source == TaskDef.Workspace))
        {
            void Wrong(string message) => set.Errors.Add(VsCodeJson.Error(Shown, task.Line, message));
            if (task.Type is not ("shell" or "process" or "npm" or "typescript"))
            {
                Wrong($"The task {task.Label} is of type {task.Type}, which a VS Code extension runs: not run here.");
            }
            else if (task.Task["command"] is null && task.Type is "shell" or "process" && task.DependsOn.Count == 0)
            {
                Wrong($"The task {task.Label} has no command.");
            }
            foreach (var dependency in task.DependsOn.Where(d => set.Find(d) is null))
            {
                Wrong($"The task {task.Label} waits for {dependency}, which is no task.");
            }
            var wrong = new List<string>();
            ReadMatchers(task.Task["problemMatcher"], s => s, wrong);
            foreach (var message in wrong)
            {
                Wrong($"The task {task.Label}: {message}");
            }
            try
            {
                Closure(set, task.Label);
            }
            // The same circle once, from wherever it is entered.
            catch (IdeError e) when (e.Details?["circle"] is JsonArray circle && circles.Add(string.Join(",", VsCodeJson.Strings(circle).Skip(1).Order(StringComparer.Ordinal))))
            {
                Wrong(e.Message);
            }
            catch (IdeError)
            {
                // Said above, or already.
            }
        }
    }

    /// <summary>The tasks a task needs, each once, those it waits for before it and itself last; 400 bad_config for a circle or a task that is not there.</summary>
    public List<TaskDef> Closure(TaskSet set, string label)
    {
        var order = new List<TaskDef>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        void Visit(string current, TaskDef? from)
        {
            if (path.Contains(current))
            {
                var circle = path.SkipWhile(p => p != current).Append(current).ToList();
                var e = RunVariables.BadConfig(from?.File, from?.Line, $"The tasks wait for each other in a circle: {string.Join(" → ", circle)}.");
                e.Details!["circle"] = new JsonArray([.. circle.Select(c => (JsonNode)JsonValue.Create(c))]);
                throw e;
            }
            if (!done.Add(current))
            {
                return;
            }
            var task = set.Find(current) ?? throw RunVariables.BadConfig(from?.File, from?.Line, from is null ? $"There is no task {current}." : $"The task {from.Label} waits for {current}, which is no task.");
            path.Add(current);
            foreach (var dependency in task.DependsOn)
            {
                Visit(dependency, task);
            }
            path.RemoveAt(path.Count - 1);
            order.Add(task);
        }
        Visit(label, null);
        return order;
    }

    /// <summary>The page's list: each task with its group, its command as written, and its terminal while kept; what is wrong; whether the folder is trusted.</summary>
    public JsonObject List()
    {
        var set = Load();
        var json = new JsonArray();
        foreach (var task in set.Tasks)
        {
            string? command = null;
            try
            {
                command = Command(task.Task, task)?.Line;
            }
            catch (IdeError)
            {
                // Said in the errors.
            }
            var running = Running(task.Label) ?? terminals.All.LastOrDefault(t => t.ReuseKey == Key(task.Label));
            json.Add(new JsonObject
            {
                ["label"] = task.Label,
                ["source"] = task.Source,
                ["group"] = task.Group,
                ["isDefault"] = task.IsDefault,
                ["command"] = command,
                ["detail"] = task.Task.Str("detail"),
                ["dependsOn"] = new JsonArray([.. task.DependsOn.Select(d => (JsonNode)JsonValue.Create(d))]),
                ["background"] = task.Background,
                ["terminal"] = running?.Id,
                ["running"] = running is { ExitCode: null },
            });
        }
        return new JsonObject
        {
            ["tasks"] = json,
            ["errors"] = new JsonArray([.. set.Errors.Select(e => (JsonNode)e)]),
            ["trusted"] = state.Trusted,
            ["buildConfiguration"] = state.BuildConfiguration,
        };
    }

    private static string Key(string label) => "task:" + label;

    private Terminal? Running(string label)
    {
        lock (_gate)
        {
            return _running.TryGetValue(label, out var running) && running.Terminal.ExitCode is null ? running.Terminal : null;
        }
    }

    /// <summary>
    /// Runs a task ({label}, or {group}: its default task, else its only one) and what it waits for first; answers once
    /// the task's own terminal is open: {task, terminal, terminals} (every terminal the run opened, in order). With
    /// wait: true, once it ended or is ready (a compound's preLaunchTask), a failure 409 task_failed.
    /// </summary>
    public async Task<JsonObject> RunAsync(JsonObject body)
    {
        var set = Load();
        var task = Pick(set, body);
        var context = RunContext.From(body["context"]);
        var answers = RunVariables.Answers(body["inputs"]);
        var closure = Closure(set, task.Label);
        if (NeedsTrust(closure))
        {
            throw Untrusted(Commands(closure, set, Variables(context, answers, lenient: true)));
        }
        if (Running(task.Label) is { } busy)
        {
            throw new IdeError(409, "running", $"The task {task.Label} is running.", new JsonObject { ["terminal"] = busy.Json() });
        }
        var vars = Variables(context, answers);
        var plans = Plan(closure, set, vars);
        vars.ThrowIfMissing();
        var run = new TaskRun(this, plans, body.Int("cols") ?? 80, body.Int("rows") ?? 24);
        var top = plans[task.Label];
        if (body.Bool("wait") == true)
        {
            // A compound's preLaunchTask: done when it ended, or is ready when it keeps running; a failure is 409 task_failed.
            var outcome = await run.RunAsync(task.Label);
            if (outcome.Failed)
            {
                throw Failed(outcome);
            }
            return new JsonObject
            {
                ["task"] = task.Label,
                ["terminal"] = outcome.Terminal?.Json(),
                ["terminals"] = new JsonArray([.. run.Terminals.Select(t => (JsonNode)t.Json())]),
            };
        }
        if (await run.WaitedForAsync(top) is { } failed)
        {
            throw Failed(failed);
        }
        Terminal? terminal = null;
        if (top.Start is not null)
        {
            terminal = Start(top, run.Cols, run.Rows, join: false).Terminal;
            run.Opened(terminal);
        }
        return new JsonObject
        {
            ["task"] = task.Label,
            ["terminal"] = terminal?.Json(),
            ["terminals"] = new JsonArray([.. run.Terminals.Select(t => (JsonNode)t.Json())]),
        };
    }

    /// <summary>Ends a task's program: {terminal} to that terminal, once it ended (5 s at most).</summary>
    public async Task<JsonObject> StopAsync(JsonObject body) =>
        (await terminals.StopAsync(body.Str("terminal"), TerminalKind.Task) ?? throw new IdeError(404, "not_found", "There is no such task's terminal.")).Json();

    /// <summary>The task asked for: by label, or the group's default (else its only task); 409 pick with the group's labels when it has several and no default.</summary>
    private static TaskDef Pick(TaskSet set, JsonObject body)
    {
        if (body.Str("label") is { } label)
        {
            return set.Find(label) ?? throw new IdeError(404, "not_found", $"There is no task {label}.");
        }
        var group = body.Str("group") ?? throw new IdeError(400, "invalid", "Say which task to run: its label, or its group (build or test).");
        var tasks = set.Tasks.Where(t => t.Group == group).ToList();
        if (tasks.FirstOrDefault(t => t.IsDefault) is { } chosen)
        {
            return chosen;
        }
        return tasks.Count switch
        {
            0 => throw new IdeError(404, "not_found", $"There is no {group} task: add one to {Shown}."),
            1 => tasks[0],
            _ => throw new IdeError(409, "pick", $"There are {tasks.Count} {group} tasks and none is the default: pick one.", new JsonObject { ["tasks"] = new JsonArray([.. tasks.Select(t => (JsonNode)JsonValue.Create(t.Label))]) }),
        };
    }

    /// <summary>The variables of a run in this folder.</summary>
    public RunVariables Variables(RunContext context, IReadOnlyDictionary<string, string> answers, bool lenient = false) =>
        new(workspace, files, context, state.BuildConfiguration, answers, env, lenient);

    /// <summary>Whether running these needs the folder trusted (tasks.json's are among them) and it is not.</summary>
    public bool NeedsTrust(IEnumerable<TaskDef> tasks) => !state.Trusted && tasks.Any(t => t.Source == TaskDef.Workspace);

    /// <summary>409 untrusted: the folder is not trusted, and these commands would run.</summary>
    public static IdeError Untrusted(IEnumerable<string> commands) =>
        new(409, "untrusted", "This folder is not trusted: trust it to run what its .vscode files say.", new JsonObject { ["commands"] = new JsonArray([.. commands.Select(c => (JsonNode)JsonValue.Create(c))]) });

    /// <summary>409 task_failed: a task ended with an exit code other than 0, and what waited for it did not start.</summary>
    public static IdeError Failed(TaskOutcome outcome) =>
        new(409, "task_failed", $"The task {outcome.Label} ended with exit code {outcome.ExitCode}.", new JsonObject { ["task"] = outcome.Label, ["exitCode"] = outcome.ExitCode, ["terminal"] = outcome.Terminal?.Json() });

    /// <summary>The commands these tasks run, their variables filled in as far as they can be: what the person reads before trusting the folder.</summary>
    public List<string> Commands(IEnumerable<TaskDef> tasks, TaskSet set, RunVariables lenient)
    {
        var commands = new List<string>();
        foreach (var task in tasks)
        {
            try
            {
                if (Command((JsonObject)lenient.Resolve(task.Task, set.Inputs, task.File ?? task.Label, AsWritten)!, task) is { } command)
                {
                    commands.Add(command.Line);
                }
            }
            catch (IdeError)
            {
                commands.Add(task.Task["command"]?.ToJsonString() ?? task.Label);
            }
        }
        return commands;
    }

    /// <summary>Each task with its variables filled in; inputs not answered are noted in <paramref name="vars"/>, for the caller to ask.</summary>
    public Dictionary<string, TaskPlan> Plan(IEnumerable<TaskDef> tasks, TaskSet set, RunVariables vars)
    {
        var plans = new Dictionary<string, TaskPlan>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var resolved = (JsonObject)vars.Resolve(task.Task, set.Inputs, task.File ?? $"the task {task.Label}", AsWritten)!;
            var command = Command(resolved, task);
            var matchers = ReadMatchers(resolved["problemMatcher"] ?? DefaultMatcher(resolved), text => vars.Resolve(text, set.Inputs, task.File ?? task.Label), []);
            plans[task.Label] = new TaskPlan(task, command?.Start, command?.Line, matchers);
        }
        return plans;
    }

    private static JsonNode? DefaultMatcher(JsonObject task) => task.Str("type") == "typescript" ? (task.Str("option") == "watch" ? "$tsc-watch" : "$tsc") : null;

    /// <summary>What a task runs: its terminal's start and the line shown for it; null when it only waits for others. 400 bad_config for a type not read.</summary>
    private (TerminalStart Start, string Line)? Command(JsonObject task, TaskDef def)
    {
        var options = task["options"] as JsonObject;
        var cwd = workspace.Root;
        if (options.Str("cwd") is { Length: > 0 } dir)
        {
            cwd = Full(dir, def);
        }
        var vars = VsCodeJson.Variables(options?["env"]);
        var args = Arguments(task["args"]);
        string program;
        List<string> argv;
        string? line = null;
        switch (def.Type)
        {
            case "npm":
                var script = task.Str("script") ?? throw RunVariables.BadConfig(def.File, def.Line, $"The npm task {def.Label} names no script.");
                if (task.Str("path") is { Length: > 0 } path)
                {
                    cwd = Full(path, def);
                }
                program = "npm";
                argv = ["run", script];
                if (args.Count > 0)
                {
                    argv.Add("--");
                    argv.AddRange(args.Select(a => a.Value));
                }
                break;
            case "typescript":
                var local = Path.Combine(workspace.Root, "node_modules", ".bin", OperatingSystem.IsWindows() ? "tsc.cmd" : "tsc");
                program = File.Exists(local) ? local : "tsc";
                argv = task.Str("option") == "watch" ? ["-w"] : [];
                argv.AddRange(["-p", task.Str("tsconfig") ?? "tsconfig.json"]);
                break;
            case "shell":
                if (Text(task["command"]) is not { } shellCommand)
                {
                    return null;
                }
                var (shell, shellArgs) = Shell(options?["shell"] as JsonObject);
                var windows = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant() is "cmd" or "pwsh" or "powershell";
                var words = new List<string> { args.Count > 0 && shellCommand.Any(char.IsWhiteSpace) ? Quote(shellCommand, "strong", windows) : shellCommand };
                words.AddRange(args.Select(a => Quote(a.Value, a.Quoting, windows)));
                line = string.Join(' ', words);
                program = shell;
                argv = [.. shellArgs, line];
                break;
            case "process":
                if (Text(task["command"]) is not { } processCommand)
                {
                    return null;
                }
                program = processCommand;
                argv = [.. args.Select(a => a.Value)];
                break;
            default:
                throw RunVariables.BadConfig(def.File, def.Line, $"The task {def.Label} is of type {def.Type}, which a VS Code extension runs: not run here.");
        }
        line ??= Line(program, argv);
        return (new TerminalStart(program, argv, cwd, vars, def.Label, TerminalKind.Task, Key(def.Label)), line);
    }

    /// <summary>A folder a task names, from the working directory when relative.</summary>
    private string Full(string dir, TaskDef def)
    {
        try
        {
            return Path.GetFullPath(dir, workspace.Root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw RunVariables.BadConfig(def.File, def.Line, $"The task {def.Label}'s folder {dir} is not a folder's path.");
        }
    }

    /// <summary>The shell a shell task runs in, with what comes before its command: options.shell, else the terminals' shell.</summary>
    private (string Program, List<string> Args) Shell(JsonObject? shell)
    {
        if (shell.Str("executable") is { Length: > 0 } own)
        {
            return shell!["args"] is JsonArray args ? (own, VsCodeJson.Strings(args)) : (own, Before(own, []));
        }
        // A login shell on macOS, as the terminals start it.
        var (program, login) = terminals.Shell();
        return (program, Before(program, login));
    }

    /// <summary>What a shell takes before a command line to run it.</summary>
    private static List<string> Before(string shell, IEnumerable<string> first) => Path.GetFileNameWithoutExtension(shell).ToLowerInvariant() switch
    {
        "cmd" => ["/d", "/s", "/c"],
        "pwsh" or "powershell" => ["-NoProfile", "-Command"],
        _ => [.. first, "-c"],
    };

    /// <summary>A command as VS Code takes one: a string, {value}, or strings to join.</summary>
    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonObject o => Text(o["value"]),
        JsonArray a => string.Join(' ', VsCodeJson.Strings(a)),
        _ => null,
    };

    /// <summary>A task's arguments: strings, or {value, quoting} (escape, strong or weak), as VS Code takes them.</summary>
    private static List<(string Value, string? Quoting)> Arguments(JsonNode? node)
    {
        var args = new List<(string, string?)>();
        foreach (var arg in node as JsonArray ?? [])
        {
            if (arg is JsonObject o)
            {
                args.Add((Text(o["value"]) ?? "", o.Str("quoting")));
            }
            else if (arg is JsonValue v)
            {
                args.Add((v.TryGetValue<string>(out var text) ? text : v.ToJsonString(), null));
            }
        }
        return args;
    }

    /// <summary>An argument as a shell task's command line writes it: as it is, unless it holds a space or quoting is asked for.</summary>
    private static string Quote(string value, string? quoting, bool windows)
    {
        if (quoting is null)
        {
            if (value.Length > 0 && !value.Any(char.IsWhiteSpace))
            {
                return value;
            }
            quoting = "strong";
        }
        if (windows)
        {
            return quoting == "escape" ? value.Replace(" ", "^ ", StringComparison.Ordinal) : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }
        return quoting switch
        {
            "escape" => Escape().Replace(value, @"\$0"),
            "weak" => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            _ => $"'{value.Replace("'", @"'\''", StringComparison.Ordinal)}'",
        };
    }

    /// <summary>A command as the person reads it: the program and its arguments, each quoted when it holds a space or a quote, or is empty.</summary>
    internal static string Line(string program, IEnumerable<string> args) => string.Join(' ', args.Select(Show).Prepend(Show(program)));

    /// <summary>A program or argument as the person reads a command: quoted when it holds a space or is empty.</summary>
    private static string Show(string word) => word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? word : $"'{word.Replace("'", @"'\''", StringComparison.Ordinal)}'";

    /// <summary>
    /// The matchers a task names (problemMatcher: a name, an object, or a list of them), each with its patterns, the
    /// folder its paths start from and its background patterns. What cannot be read is said in <paramref name="wrong"/>
    /// and left out: a name not known here, a pattern that is no regular expression, a pattern of more than one line.
    /// </summary>
    public List<ProblemMatcher> ReadMatchers(JsonNode? node, Func<string, string> resolve, List<string> wrong)
    {
        var matchers = new List<ProblemMatcher>();
        foreach (var item in node is JsonArray all ? all.ToArray() : [node])
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var name))
            {
                if (Problems.Named(name) is { } named)
                {
                    matchers.Add(named);
                }
                else
                {
                    wrong.Add($"the problem matcher {name} is not one Code Arena reads ({Matchers}, or patterns of one line).");
                }
            }
            else if (item is JsonObject o && Matcher(o, resolve, wrong) is { } custom)
            {
                matchers.Add(custom);
            }
        }
        return matchers;
    }

    private ProblemMatcher? Matcher(JsonObject o, Func<string, string> resolve, List<string> wrong)
    {
        ProblemMatcher? baseMatcher = null;
        if (o.Str("base") is { } baseName && (baseMatcher = Problems.Named(baseName)) is null)
        {
            wrong.Add($"the problem matcher {baseName} is not one Code Arena reads ({Matchers}).");
            return null;
        }
        IReadOnlyList<Regex> patterns = baseMatcher?.Patterns ?? [];
        ProblemGroups? groups = baseMatcher?.Groups;
        switch (o["pattern"])
        {
            case JsonValue v when v.TryGetValue<string>(out var name):
                if (Problems.Named(name) is not { } named)
                {
                    wrong.Add($"the pattern {name} is not one Code Arena reads ({Matchers}).");
                    return null;
                }
                patterns = named.Patterns;
                groups = named.Groups;
                break;
            case JsonArray { Count: > 1 }:
                wrong.Add("problem patterns of more than one line are not read yet.");
                return null;
            case JsonArray { Count: 1 } one when one[0] is JsonObject only:
                if (Pattern(only, wrong) is not { } single)
                {
                    return null;
                }
                patterns = [single.Regex];
                groups = single.Groups;
                break;
            case JsonObject pattern:
                if (Pattern(pattern, wrong) is not { } custom)
                {
                    return null;
                }
                patterns = [custom.Regex];
                groups = custom.Groups;
                break;
        }
        if (patterns.Count == 0)
        {
            wrong.Add("a problem matcher has no pattern.");
            return null;
        }
        string? fileBase = baseMatcher?.FileBase;
        if (o["fileLocation"] is JsonArray { Count: > 1 } location && location[1] is JsonValue at && at.TryGetValue<string>(out var dir))
        {
            try
            {
                fileBase = Path.GetFullPath(resolve(dir), workspace.Root);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IdeError)
            {
                wrong.Add($"the problem matcher's fileLocation {dir} is not a folder's path.");
            }
        }
        var background = o["background"] as JsonObject ?? o["watching"] as JsonObject;
        var begins = background is null ? baseMatcher?.Begins : Background(background["beginsPattern"], wrong);
        var ends = background is null ? baseMatcher?.Ends : Background(background["endsPattern"], wrong);
        var severity = o.Str("severity")?.ToLowerInvariant() switch
        {
            "warning" or "warn" => "warning",
            "info" or "information" or "hint" => "info",
            "error" => "error",
            _ => baseMatcher?.Severity ?? "error",
        };
        return new ProblemMatcher(o.Str("owner") ?? baseMatcher?.Name ?? "custom", patterns, groups, severity, fileBase, begins, ends);
    }

    /// <summary>A pattern of one line: its regular expression and which groups hold what (VS Code's defaults: file 1, line 2, column 3, the whole line as the message).</summary>
    private static (Regex Regex, ProblemGroups Groups)? Pattern(JsonObject p, List<string> wrong)
    {
        if (Compile(p.Str("regexp"), wrong) is not { } regex)
        {
            return null;
        }
        int Index(string key, int fallback) => p.Int(key) ?? fallback;
        var located = p.ContainsKey("location") || p.Str("kind") == "file";
        var groups = new ProblemGroups(
            File: Index("file", 1),
            Line: Index("line", located ? 0 : 2),
            Column: Index("column", located ? 0 : 3),
            Location: Index("location", 0),
            Severity: Index("severity", 0),
            Code: Index("code", 0),
            Message: Index("message", 0));
        return (regex, groups);
    }

    private static Regex? Background(JsonNode? node, List<string> wrong) =>
        node is JsonObject o ? Compile(o.Str("regexp"), wrong) : node is JsonValue v && v.TryGetValue<string>(out var text) ? Compile(text, wrong) : null;

    /// <summary>A regular expression a task gives, given a little time per line: a pattern that takes longer reads nothing.</summary>
    private static Regex? Compile(string? pattern, List<string> wrong)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            wrong.Add("a pattern has no regexp.");
            return null;
        }
        try
        {
            return new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException e)
        {
            wrong.Add($"the pattern {pattern} is no regular expression: {e.Message}");
            return null;
        }
    }

    [GeneratedRegex(@"[\s'""\\$`]")]
    private static partial Regex Escape();

    /// <summary>
    /// Opens a task's terminal and reads its output as it runs (its problems, and when one that keeps running is
    /// ready). A task running already is joined (by what waits for it), or refused (409 running).
    /// </summary>
    internal (Terminal Terminal, Task<TaskOutcome> Ready) Start(TaskPlan plan, int cols, int rows, bool join)
    {
        var label = plan.Def.Label;
        lock (_gate)
        {
            // One whose program ended is forgotten a moment later: its next run starts anew now.
            if (_running.TryGetValue(label, out var running) && running.Terminal.ExitCode is null)
            {
                return join ? running : throw new IdeError(409, "running", $"The task {label} is running.", new JsonObject { ["terminal"] = running.Terminal.Json() });
            }
            var terminal = terminals.Open(cols, rows, plan.Start);
            var watch = new TaskWatch(problems, plan, terminal);
            _running[label] = (terminal, watch.Ready);
            _ = terminal.Exited.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (_running.TryGetValue(label, out var r) && r.Terminal == terminal)
                    {
                        _running.Remove(label);
                    }
                }
            }, TaskScheduler.Default);
            return (terminal, watch.Ready);
        }
    }

    /// <summary>Runs a task and what it waits for: done when it ended, or is ready when it keeps running. For a run configuration's preLaunchTask and postDebugTask.</summary>
    public Task<TaskOutcome> RunToReadyAsync(IReadOnlyDictionary<string, TaskPlan> plans, string label, int cols, int rows) =>
        new TaskRun(this, plans, cols, rows).RunAsync(label);

    /// <summary>One run of a task and what it waits for: each task once (one waited for twice runs once), the terminals it opened in order.</summary>
    private sealed class TaskRun(Tasks tasks, IReadOnlyDictionary<string, TaskPlan> plans, int cols, int rows)
    {
        private readonly Dictionary<string, Task<TaskOutcome>> _once = new(StringComparer.Ordinal);
        private readonly List<Terminal> _opened = [];

        public int Cols => cols;
        public int Rows => rows;

        public List<Terminal> Terminals
        {
            get
            {
                lock (_opened)
                {
                    return [.. _opened];
                }
            }
        }

        public void Opened(Terminal terminal)
        {
            lock (_opened)
            {
                if (!_opened.Contains(terminal))
                {
                    _opened.Add(terminal);
                }
            }
        }

        /// <summary>The task, after what it waits for: ended, or ready.</summary>
        public Task<TaskOutcome> RunAsync(string label)
        {
            lock (_once)
            {
                if (!_once.TryGetValue(label, out var run))
                {
                    _once[label] = run = Task.Run(() => RunOnceAsync(label));
                }
                return run;
            }
        }

        /// <summary>What a task waits for, in its order: the first that failed (the rest not started, in sequence), or null.</summary>
        public async Task<TaskOutcome?> WaitedForAsync(TaskPlan plan)
        {
            if (plan.Def.Sequence)
            {
                foreach (var dependency in plan.Def.DependsOn)
                {
                    if (await RunAsync(dependency) is { Failed: true } failed)
                    {
                        return failed;
                    }
                }
                return null;
            }
            var all = await Task.WhenAll(plan.Def.DependsOn.Select(RunAsync));
            return all.FirstOrDefault(r => r.Failed);
        }

        private async Task<TaskOutcome> RunOnceAsync(string label)
        {
            var plan = plans[label];
            if (await WaitedForAsync(plan) is { } failed)
            {
                return failed;
            }
            if (plan.Start is null)
            {
                return new TaskOutcome(label, 0, null);
            }
            var (terminal, ready) = tasks.Start(plan, cols, rows, join: true);
            Opened(terminal);
            return await ready;
        }
    }

    /// <summary>Reads a task's terminal as it runs: what its matchers find, and for one that keeps running, when it is ready.</summary>
    private sealed class TaskWatch
    {
        private readonly TaskCompletionSource<TaskOutcome> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Problems _problems;
        private readonly TaskPlan _plan;
        private readonly Terminal _terminal;
        private readonly string _source;
        private readonly Regex? _begins;
        private readonly Regex? _ends;
        private readonly StringBuilder _output = new();

        public TaskWatch(Problems problems, TaskPlan plan, Terminal terminal)
        {
            _problems = problems;
            _plan = plan;
            _terminal = terminal;
            _source = "task:" + plan.Def.Label;
            if (plan.Def.Background)
            {
                _begins = plan.Matchers.Select(m => m.Begins).FirstOrDefault(r => r is not null);
                _ends = plan.Matchers.Select(m => m.Ends).FirstOrDefault(r => r is not null);
                if (_ends is null)
                {
                    // Nothing says when it is ready: VS Code goes on at once.
                    _ready.TrySetResult(new TaskOutcome(plan.Def.Label, null, terminal));
                }
            }
            problems.Found(_source, []);
            var (backlog, frames) = terminal.Attach();
            _ = Task.Run(() => ReadAsync(backlog, frames));
        }

        public Task<TaskOutcome> Ready => _ready.Task;

        private async Task ReadAsync(byte[] backlog, System.Threading.Channels.Channel<TerminalFrame> frames)
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var pending = new StringBuilder();
            void Take(byte[] bytes, bool last)
            {
                var chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length, last)];
                decoder.GetChars(bytes, 0, bytes.Length, chars, 0, last);
                foreach (var c in chars)
                {
                    if (c == '\n')
                    {
                        Line(pending.ToString());
                        pending.Clear();
                    }
                    else if (pending.Length < 64 * 1024)
                    {
                        pending.Append(c);
                    }
                }
            }
            int? exit = null;
            try
            {
                Take(backlog, last: false);
                await foreach (var frame in frames.Reader.ReadAllAsync())
                {
                    if (frame.Output is { } output)
                    {
                        Take(output, last: false);
                    }
                    else if (frame.Exit is { } code)
                    {
                        exit = code;
                    }
                }
                Take([], last: true);
                if (pending.Length > 0)
                {
                    Line(pending.ToString());
                }
                Publish();
            }
            finally
            {
                exit ??= _terminal.ExitCode ?? -1;
                _ready.TrySetResult(new TaskOutcome(_plan.Def.Label, exit, _terminal));
            }
        }

        private void Line(string raw)
        {
            var line = Problems.Plain(raw);
            if (_begins is not null && Matches(_begins, line))
            {
                // A new round of work: what the last one found is replaced when it ends.
                _output.Clear();
            }
            // Kept only to be read by a matcher: a server's log, with none, is its terminal's alone.
            if (_plan.Matchers.Count > 0)
            {
                _output.Append(line).Append('\n');
                if (_output.Length > Problems.MaxOutput)
                {
                    _output.Remove(0, _output.Length - Problems.MaxOutput / 2);
                }
            }
            if (_ends is not null && Matches(_ends, line))
            {
                Publish();
                _ready.TrySetResult(new TaskOutcome(_plan.Def.Label, null, _terminal));
            }
        }

        private static bool Matches(Regex regex, string line)
        {
            try
            {
                return regex.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private void Publish()
        {
            if (_plan.Matchers.Count > 0)
            {
                _problems.Found(_source, _problems.Read(_output.ToString(), _plan.Matchers, _source));
            }
        }
    }
}
