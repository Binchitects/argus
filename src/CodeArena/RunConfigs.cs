using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A run configuration as .vscode/launch.json or the project's files give it: VS Code's shape (its part for this system
/// merged in, its variables not filled in yet), where it came from ("launch" or "detected"), its line in launch.json,
/// and for a detected .NET one the project whose built program it runs.
/// </summary>
internal sealed record RunConfig(string Name, string Source, JsonObject Configuration, int? Line = null, string? Project = null)
{
    public const string Launch = "launch";
    public const string Detected = "detected";

    /// <summary>The file it is in, for the person; null for a detected one.</summary>
    public string? File => Source == Launch ? RunConfigs.Shown : null;
}

/// <summary>Every configuration (launch.json's, then the detected ones of other names), launch.json's compounds and inputs, and what is wrong.</summary>
internal sealed record RunSet(List<RunConfig> Configurations, List<JsonObject> Compounds, Dictionary<string, JsonObject> Inputs, List<JsonObject> Errors)
{
    public RunConfig? Find(string? name) => Configurations.FirstOrDefault(c => c.Name == name);
}

/// <summary>A configuration run without its debugger: the program, its arguments, its folder, and the variables set over Code Arena's own (null unsets one).</summary>
internal sealed record PlainRun(string Program, IReadOnlyList<string> Args, string Cwd, IReadOnlyDictionary<string, string?> Env)
{
    /// <summary>The command, as the person reads it.</summary>
    public string Line => Tasks.Line(Program, Args);
}

/// <summary>A configuration with everything filled in, and the tasks that run before and after it, ready to run.</summary>
internal sealed record ResolvedRun(RunConfig Config, JsonObject Configuration, IReadOnlyDictionary<string, TaskPlan>? Before, IReadOnlyDictionary<string, TaskPlan>? After);

/// <summary>
/// The IDE's run configurations, as VS Code's Run and Debug view lists them: .vscode/launch.json's configurations (a
/// part per system, envFile, VS Code's variables and inputs; compounds read: the page starts each of their configurations), then the ones the
/// project's files give (detected). Run Without Debugging turns each type (coreclr, node, python and debugpy, go,
/// cppdbg, lldb with CodeLLDB's cargo, gdb) into a plain program run in a terminal of its own, after its preLaunchTask
/// (a failure stops it) and before its postDebugTask. launch.json's run only in a trusted folder, as tasks.json's do.
/// Debugging itself comes with the debugger; <see cref="Resolve(string, RunContext, IReadOnlyDictionary{string, string})"/>
/// is what it starts from.
/// </summary>
internal sealed class RunConfigs(Workspace workspace, Terminals terminals, Tasks tasks, WorkspaceState state, Func<string, string?> env)
{
    /// <summary>The file, as the person reads its name.</summary>
    public const string Shown = ".vscode/launch.json";

    // A configuration's keys whose variables are not filled in: what names it and what runs around it.
    private static readonly HashSet<string> AsWritten = new(StringComparer.Ordinal)
    {
        "name", "type", "request", "preLaunchTask", "postDebugTask", "presentation", "internalConsoleOptions",
    };

    // The configurations between a start's check that they do not run and their terminal opening (their preLaunchTask's time).
    private readonly HashSet<string> _starting = new(StringComparer.Ordinal);

    /// <summary>Every configuration, its compounds and inputs, and what is wrong; read anew each time.</summary>
    public RunSet Load()
    {
        var errors = new List<JsonObject>();
        var configs = new List<RunConfig>();
        var compounds = new List<JsonObject>();
        var inputs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var (json, text) = VsCodeJson.Read(workspace, "launch.json", errors);
        if (json is not null)
        {
            foreach (var node in json["configurations"] as JsonArray ?? [])
            {
                if (node is not JsonObject raw)
                {
                    errors.Add(VsCodeJson.Error(Shown, null, "A configuration is not an object."));
                    continue;
                }
                var config = VsCodeJson.ForThisSystem(raw);
                if (config.Str("name") is not { Length: > 0 } name)
                {
                    errors.Add(VsCodeJson.Error(Shown, null, "A configuration has no name."));
                    continue;
                }
                var line = VsCodeJson.LineOf(text, "name", name);
                if (config.Str("type") is not { Length: > 0 } || config.Str("request") is not ("launch" or "attach"))
                {
                    errors.Add(VsCodeJson.Error(Shown, line, $"The configuration {name} needs a type, and a request (launch or attach)."));
                    continue;
                }
                if (configs.Any(c => c.Name == name))
                {
                    errors.Add(VsCodeJson.Error(Shown, line, $"Two configurations are named {name}: the first is used."));
                    continue;
                }
                configs.Add(new RunConfig(name, RunConfig.Launch, config, line));
            }
            foreach (var node in json["compounds"] as JsonArray ?? [])
            {
                if (node is not JsonObject compound || compound.Str("name") is not { Length: > 0 } name)
                {
                    errors.Add(VsCodeJson.Error(Shown, null, "A compound has no name."));
                    continue;
                }
                var names = (compound["configurations"] as JsonArray ?? []).Select(c => c is JsonObject o ? o.Str("name") : c is JsonValue v && v.TryGetValue<string>(out var n) ? n : null).OfType<string>().ToList();
                foreach (var missing in names.Where(n => configs.All(c => c.Name != n)))
                {
                    errors.Add(VsCodeJson.Error(Shown, VsCodeJson.LineOf(text, "name", name), $"The compound {name} names {missing}, which is no configuration."));
                }
                compounds.Add(new JsonObject
                {
                    ["name"] = name,
                    ["configurations"] = new JsonArray([.. names.Select(n => (JsonNode)JsonValue.Create(n))]),
                    ["preLaunchTask"] = compound.Str("preLaunchTask"),
                    ["stopAll"] = compound.Bool("stopAll") == true,
                });
            }
            inputs = RunVariables.Inputs(json["inputs"]);
        }
        foreach (var detected in RunDetect.Configurations(workspace.Root, state.BuildConfiguration, env))
        {
            var name = detected.Configuration.Str("name")!;
            if (configs.All(c => c.Name != name))
            {
                configs.Add(new RunConfig(name, RunConfig.Detected, detected.Configuration, null, detected.Project));
            }
        }
        return new RunSet(configs, compounds, inputs, errors);
    }

    /// <summary>The page's list: each configuration with what a run of it runs (as written), whether it can run without its debugger and why not; the compounds; what is wrong.</summary>
    public JsonObject List()
    {
        var set = Load();
        var configurations = new JsonArray();
        foreach (var config in set.Configurations)
        {
            var c = config.Configuration;
            var run = Plain(c, out var reason);
            var json = new JsonObject
            {
                ["name"] = config.Name,
                ["type"] = c.Str("type"),
                ["request"] = c.Str("request"),
                ["source"] = config.Source,
                ["program"] = c.Str("program"),
                ["command"] = run?.Line,
                ["preLaunchTask"] = c.Str("preLaunchTask"),
                ["supported"] = run is not null,
                ["reason"] = reason,
                ["hidden"] = c["presentation"].Bool("hidden") == true,
            };
            if (config.Source == RunConfig.Detected)
            {
                // What "Add Configuration" writes into launch.json.
                json["configuration"] = c.DeepClone();
            }
            configurations.Add(json);
        }
        return new JsonObject
        {
            ["configurations"] = configurations,
            ["compounds"] = new JsonArray([.. set.Compounds.Select(c => (JsonNode)c)]),
            ["errors"] = new JsonArray([.. set.Errors.Select(e => (JsonNode)e)]),
            ["trusted"] = state.Trusted,
            ["buildConfiguration"] = state.BuildConfiguration,
        };
    }

    /// <summary>
    /// A configuration by name with everything filled in: its part for this system, its variables (the page's file,
    /// line, selection and settings in <paramref name="context"/>), the person's answers to its inputs, and its envFile
    /// under its env. 404 for no such configuration, 409 untrusted, 409 needs_input, 400 bad_config.
    /// </summary>
    public JsonObject Resolve(string name, RunContext context, IReadOnlyDictionary<string, string> answers)
    {
        var set = Load();
        return Resolve(set, Find(set, name), context, answers).Configuration;
    }

    private static RunConfig Find(RunSet set, string name)
    {
        if (set.Find(name) is { } config)
        {
            return config;
        }
        if (set.Compounds.Any(c => c.Str("name") == name))
        {
            throw new IdeError(400, "compound", $"{name} is a compound: the page starts each of its configurations.");
        }
        throw new IdeError(404, "not_found", $"There is no configuration {name}.");
    }

    private ResolvedRun Resolve(RunSet set, RunConfig config, RunContext context, IReadOnlyDictionary<string, string> answers)
    {
        var taskSet = tasks.Load();
        var before = Closure(taskSet, config, "preLaunchTask");
        var after = Closure(taskSet, config, "postDebugTask");
        if (!state.Trusted && (config.Source == RunConfig.Launch || tasks.NeedsTrust([.. before ?? [], .. after ?? []])))
        {
            var lenient = tasks.Variables(context, answers, lenient: true);
            var resolved = (JsonObject)lenient.Resolve(config.Configuration, set.Inputs, config.File ?? config.Name, AsWritten)!;
            var commands = tasks.Commands(before ?? [], taskSet, lenient);
            commands.Add(Plain(resolved, out _)?.Line ?? resolved.Str("program") ?? config.Name);
            commands.AddRange(tasks.Commands(after ?? [], taskSet, lenient));
            throw Tasks.Untrusted(commands);
        }
        var vars = tasks.Variables(context, answers);
        var configuration = (JsonObject)vars.Resolve(config.Configuration, set.Inputs, config.File ?? $"the configuration {config.Name}", AsWritten)!;
        var plansBefore = before is null ? null : tasks.Plan(before, taskSet, vars);
        var plansAfter = after is null ? null : tasks.Plan(after, taskSet, vars);
        vars.ThrowIfMissing();
        if (configuration.Str("envFile") is { Length: > 0 } envFile)
        {
            var variables = new JsonObject();
            foreach (var (key, value) in EnvFile(envFile, config))
            {
                variables[key] = value;
            }
            // The configuration's own env wins over its file's.
            configuration["env"] = VsCodeJson.Merge(variables, configuration["env"] as JsonObject);
            configuration.Remove("envFile");
        }
        return new ResolvedRun(config, configuration, plansBefore, plansAfter);
    }

    /// <summary>The tasks a configuration's preLaunchTask or postDebugTask needs, itself last; null when it names none. 400 for one that is no task.</summary>
    private List<TaskDef>? Closure(TaskSet set, RunConfig config, string key)
    {
        if (config.Configuration.Str(key) is not { Length: > 0 } label)
        {
            return null;
        }
        if (set.Find(label) is null)
        {
            throw RunVariables.BadConfig(config.File, config.Line, $"The {key} of {config.Name}, {label}, is no task.");
        }
        return tasks.Closure(set, label);
    }

    /// <summary>A .env file's variables: NAME=value lines, export before it allowed, quotes around the value dropped, # comments.</summary>
    private Dictionary<string, string> EnvFile(string file, RunConfig config)
    {
        string path;
        try
        {
            path = Path.GetFullPath(file, workspace.Root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw RunVariables.BadConfig(config.File, config.Line, $"The envFile of {config.Name}, {file}, is not a file's path.");
        }
        if (!File.Exists(path))
        {
            throw RunVariables.BadConfig(config.File, config.Line, $"The envFile of {config.Name}, {path}, is not there.");
        }
        var text = VsCodeJson.ReadSmall(path) ?? throw RunVariables.BadConfig(config.File, config.Line, $"The envFile of {config.Name} is larger than 1 MB.");
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || equals <= 0)
            {
                continue;
            }
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1].Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
            }
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1];
            }
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment and >= 0)
            {
                value = value[..comment].TrimEnd();
            }
            vars[line[..equals].Trim()] = value;
        }
        return vars;
    }

    /// <summary>
    /// Runs a configuration without its debugger ({name, noDebug: true, context, inputs, cols, rows}): its preLaunchTask
    /// first (waited for; a failure stops it, 409 task_failed), then its program in a terminal of its own, then its
    /// postDebugTask once it ended. {terminal, configuration, command}. Debugging goes through
    /// <see cref="DebugConfigurationAsync"/>; 501 not_yet here.
    /// </summary>
    public async Task<JsonObject> StartAsync(JsonObject body)
    {
        var name = body.Str("name") ?? throw new IdeError(400, "invalid", "Say which configuration to run: its name.");
        if (body.Bool("noDebug") != true)
        {
            throw new IdeError(501, "not_yet", "Debugging comes with the debugger, later: run without debugging (noDebug) for now.");
        }
        var set = Load();
        var config = Find(set, name);
        var resolved = Resolve(set, config, RunContext.From(body["context"]), RunVariables.Answers(body["inputs"]));
        var run = Plain(resolved.Configuration, out var reason) ?? throw RunVariables.BadConfig(config.File, config.Line, $"{name} cannot run: {reason}");
        var key = "run:" + name;
        lock (_starting)
        {
            if (terminals.All.FirstOrDefault(t => t.ReuseKey == key && t.ExitCode is null) is { } running)
            {
                throw new IdeError(409, "running", $"{name} is running.", new JsonObject { ["terminal"] = running.Json() });
            }
            // Its preLaunchTask runs: a second start now would open a second terminal for it.
            if (!_starting.Add(name))
            {
                throw new IdeError(409, "running", $"{name} is starting.", new JsonObject { ["terminal"] = null });
            }
        }
        var (cols, rows) = (body.Int("cols") ?? 80, body.Int("rows") ?? 24);
        Terminal terminal;
        try
        {
            if (resolved.Before is { } before && await tasks.RunToReadyAsync(before, config.Configuration.Str("preLaunchTask")!, cols, rows) is { Failed: true } failed)
            {
                throw Tasks.Failed(failed);
            }
            if (config.Project is { } project && run.Program == "dotnet" && await TargetPathAsync(project) is { } built)
            {
                // The program the build made, wherever its framework and name put it.
                run = run with { Args = [built, .. run.Args.Skip(1)] };
                resolved.Configuration["program"] = built;
            }
            terminal = terminals.Open(cols, rows, new TerminalStart(run.Program, run.Args, run.Cwd, run.Env, name, TerminalKind.Run, key));
        }
        finally
        {
            lock (_starting)
            {
                _starting.Remove(name);
            }
        }
        if (resolved.After is { } after)
        {
            _ = AfterAsync(terminal, after, config.Configuration.Str("postDebugTask")!, cols, rows);
        }
        return new JsonObject
        {
            ["terminal"] = terminal.Json(),
            ["configuration"] = resolved.Configuration,
            ["command"] = run.Line,
        };
    }

    /// <summary>
    /// A configuration made ready for its debugger ({name, context, inputs, cols, rows}): its variables filled in, its
    /// preLaunchTask run first and waited for (a failure stops it, 409 task_failed), a detected .NET program as MSBuild
    /// built it. The debug session's start takes it from there.
    /// </summary>
    public async Task<JsonObject> DebugConfigurationAsync(JsonObject body)
    {
        var name = body.Str("name") ?? throw new IdeError(400, "invalid", "Say which configuration to debug: its name.");
        var set = Load();
        var config = Find(set, name);
        var resolved = Resolve(set, config, RunContext.From(body["context"]), RunVariables.Answers(body["inputs"]));
        var (cols, rows) = (body.Int("cols") ?? 80, body.Int("rows") ?? 24);
        if (resolved.Before is { } before && await tasks.RunToReadyAsync(before, config.Configuration.Str("preLaunchTask")!, cols, rows) is { Failed: true } failed)
        {
            throw Tasks.Failed(failed);
        }
        if (config.Project is { } project && resolved.Configuration.Str("type") == "coreclr" && await TargetPathAsync(project) is { } built)
        {
            resolved.Configuration["program"] = built;
        }
        resolved.Configuration["name"] ??= name;
        return resolved.Configuration;
    }

    /// <summary>A run's postDebugTask, once its program ended; one that cannot start (Code Arena stopping, too many terminals) is not run.</summary>
    private async Task AfterAsync(Terminal terminal, IReadOnlyDictionary<string, TaskPlan> after, string label, int cols, int rows)
    {
        await terminal.Exited;
        try
        {
            await tasks.RunToReadyAsync(after, label, cols, rows);
        }
        catch (Exception e) when (e is IdeError or IOException)
        {
            // No page waits for it: its terminal, when it opened, says how it went.
        }
    }

    /// <summary>Ends a run's program: {terminal} to that terminal, once it ended (5 s at most).</summary>
    public async Task<JsonObject> StopAsync(JsonObject body) =>
        (await terminals.StopAsync(body.Str("terminal"), TerminalKind.Run) ?? throw new IdeError(404, "not_found", "There is no such run's terminal.")).Json();

    /// <summary>The program a .NET project built, as MSBuild says (TargetPath in the build configuration); null when it cannot say.</summary>
    private async Task<string?> TargetPathAsync(string project)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = workspace.Root };
        foreach (var arg in new[] { "msbuild", project, "-getProperty:TargetPath", $"-p:Configuration={state.BuildConfiguration}", "-nologo" })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        try
        {
            var result = await Proc.RunAsync(psi, TimeSpan.FromMinutes(1), CancellationToken.None, 10_000);
            var path = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            return result.ExitCode == 0 && path is not null && Path.IsPathRooted(path) && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ToolError)
        {
            return null;
        }
    }

    /// <summary>
    /// How a configuration runs without its debugger, by its type: coreclr's program (a .dll through dotnet), node's
    /// runtime and program, python's or debugpy's interpreter with the program or module, go run (go test, or the
    /// program itself, by its mode), and the native program of cppdbg, lldb (cargo run for CodeLLDB's cargo), lldb-dap
    /// and gdb. Null, and why, for an attach or a type not known here.
    /// </summary>
    internal PlainRun? Plain(JsonObject c, out string? reason)
    {
        (var run, reason) = Translate(c);
        return run;
    }

    private (PlainRun? Run, string? Reason) Translate(JsonObject c)
    {
        var type = c.Str("type") ?? "";
        if (c.Str("request") == "attach")
        {
            return (null, "attaching needs the debugger.");
        }
        string cwd;
        try
        {
            cwd = c.Str("cwd") is { Length: > 0 } dir ? Path.GetFullPath(dir, workspace.Root) : workspace.Root;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "its cwd is not a folder's path.");
        }
        var vars = VsCodeJson.Variables(c["env"]);
        foreach (var variable in c["environment"] as JsonArray ?? [])
        {
            if (variable.Str("name") is { Length: > 0 } name)
            {
                vars[name] = variable.Str("value") ?? "";
            }
        }
        var args = VsCodeJson.Strings(c["args"]);
        var program = c.Str("program");
        (PlainRun?, string?) Run(string executable, IEnumerable<string> arguments) => (new PlainRun(executable, [.. arguments], cwd, vars), null);
        static (PlainRun?, string?) None(string why) => (null, why);
        // The configuration's own program, named without a folder, is the one in its cwd when there is one; a runtime
        // (dotnet, node, npm, python3, go, cargo) is always the PATH's, never a file of the project's of that name.
        string Own(string named) =>
            !Path.IsPathRooted(named) && !named.Contains('/') && !named.Contains('\\') && File.Exists(Path.Combine(cwd, named)) ? Path.Combine(cwd, named) : named;
        switch (type)
        {
            case "coreclr" or "clr":
                return program is null ? None("it names no program.")
                    : program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Run("dotnet", [program, .. args]) : Run(Own(program), args);
            case "node" or "pwa-node":
            {
                var runtime = c.Str("runtimeExecutable") ?? "node";
                var runtimeArgs = VsCodeJson.Strings(c["runtimeArgs"]);
                return program is null && runtimeArgs.Count == 0 ? None("it names no program.")
                    : Run(runtime, [.. runtimeArgs, .. program is null ? [] : new[] { program }, .. args]);
            }
            case "python" or "debugpy":
            {
                var python = VsCodeJson.Strings(c["python"]);
                if (python.Count == 0)
                {
                    python = [c.Str("pythonPath") ?? RunDetect.Python(workspace.Root, env).Replace("${workspaceFolder}", workspace.Root, StringComparison.Ordinal)];
                }
                var before = python.Skip(1).Concat(VsCodeJson.Strings(c["pythonArgs"]));
                return c.Str("module") is { Length: > 0 } module ? Run(python[0], [.. before, "-m", module, .. args])
                    : program is not null ? Run(python[0], [.. before, program, .. args])
                    : None("it names neither a program nor a module.");
            }
            case "go":
            {
                if (program is null)
                {
                    return None("it names no program.");
                }
                var flags = c["buildFlags"] is JsonArray list ? VsCodeJson.Strings(list) : Words(c.Str("buildFlags") ?? "");
                // A package's folder: go run . from it, as Go's extension runs it there.
                var package = program;
                if (Path.Combine(cwd, program) is var folder && Directory.Exists(folder))
                {
                    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                    if (c.Str("cwd") is not { Length: > 0 })
                    {
                        cwd = full;
                    }
                    package = full == cwd ? "." : program;
                }
                return (c.Str("mode") ?? "auto") switch
                {
                    "exec" => Run(Own(program), args),
                    "test" => Run("go", ["test", .. flags, package, .. args.Count > 0 ? ["-args", .. args] : Array.Empty<string>()]),
                    "debug" or "auto" => Run("go", ["run", .. flags, package, .. args]),
                    var mode => None($"its mode {mode} needs the debugger."),
                };
            }
            case "lldb" when c["cargo"] is JsonObject cargo:
            {
                var cargoArgs = VsCodeJson.Strings(cargo["args"]);
                if (cargoArgs.Count == 0 || cargoArgs[0] == "build")
                {
                    cargoArgs = ["run", .. cargoArgs.Skip(1)];
                }
                return Run("cargo", [.. cargoArgs, .. args.Count > 0 ? ["--", .. args] : Array.Empty<string>()]);
            }
            case "cppdbg" or "cppvsdbg" or "lldb" or "lldb-dap" or "gdb":
                return (program ?? c.Str("target")) is { } native ? Run(Own(native), args) : None("it names no program.");
            default:
                return None($"a {(type.Length > 0 ? type : "typeless")} configuration runs only with its debugger, not here yet.");
        }
    }

    /// <summary>Words of a command line as a shell splits them: by spaces, '…' and "…" keeping theirs.</summary>
    private static List<string> Words(string line)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        var any = false;
        char? quote = null;
        foreach (var ch in line)
        {
            if (quote is { } q)
            {
                if (ch == q)
                {
                    quote = null;
                }
                else
                {
                    word.Append(ch);
                }
            }
            else if (ch is '\'' or '"')
            {
                quote = ch;
                any = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (any || word.Length > 0)
                {
                    words.Add(word.ToString());
                }
                word.Clear();
                any = false;
            }
            else
            {
                word.Append(ch);
            }
        }
        if (any || word.Length > 0)
        {
            words.Add(word.ToString());
        }
        return words;
    }
}
