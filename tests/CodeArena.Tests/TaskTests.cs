using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The IDE's tasks: tasks.json read as VS Code reads it, tasks the project's files give, and tasks run in terminals of their own.</summary>
public sealed class TaskTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _root = Directory.CreateTempSubdirectory("code-arena-tasks-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("code-arena-tasks-data-").FullName;
    private readonly Terminals _terminals;
    private readonly Problems _problems;
    private readonly WorkspaceState _state;
    private readonly Tasks _tasks;
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public TaskTests()
    {
        var workspace = new Workspace(_root);
        _terminals = new Terminals(workspace, new Config { TerminalShell = "/bin/sh" }, _ => null);
        _state = new WorkspaceState(_data, workspace.Root);
        _problems = new Problems(workspace, new Config(), _state);
        _tasks = new Tasks(workspace, new IdeFiles(workspace), _terminals, _problems, _state, Environment.GetEnvironmentVariable);
    }

    public void Dispose()
    {
        _terminals.DisposeAsync().AsTask().Wait();
        _gateway.Dispose();
        _mcp.Dispose();
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_data, recursive: true);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void TasksJson(string tasks, string more = "") => Write(".vscode/tasks.json", $$"""
        {
          // as VS Code writes it: comments and trailing commas
          "version": "2.0.0",
          {{more}}
          "tasks": [{{tasks}}],
        }
        """);

    private static JsonObject Body(string label, JsonObject? more = null)
    {
        var body = new JsonObject { ["label"] = label };
        foreach (var (key, value) in more ?? [])
        {
            body[key] = value?.DeepClone();
        }
        return body;
    }

    /// <summary>Runs a task and waits for its own program to end: the answer, and that exit code.</summary>
    private async Task<(JsonObject Answer, int Exit)> RunToEndAsync(string label, JsonObject? more = null)
    {
        var answer = await _tasks.RunAsync(Body(label, more));
        var terminal = _terminals.Find(answer["terminal"]!["id"]!.GetValue<string>())!;
        return (answer, await terminal.Exited.WaitAsync(Wait));
    }

    private static async Task<IdeError> RefusedAsync(Task task) => await Assert.ThrowsAsync<IdeError>(() => task);

    /// <summary>Waits until a task's output is read (it is, a moment after its program ended).</summary>
    private static async Task UntilAsync(Func<bool> done)
    {
        for (var waited = 0; !done() && waited < 10_000; waited += 50)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public void Tasks_json_with_comments_gives_its_tasks_and_this_systems_part_and_says_what_is_wrong_with_its_lines()
    {
        TasksJson("""
            { "label": "build", "type": "shell", "command": "echo elsewhere", "linux": { "command": "echo linux" }, "osx": { "command": "echo mac" }, "windows": { "command": "echo windows" },
              "group": { "kind": "build", "isDefault": true }, "detail": "Builds it", },
            { "label": "test", "type": "process", "command": "sh", "args": ["-c", "exit 0", "two words"], "group": "test" },
            { "label": "lint", "type": "gulp", "task": "lint" },
            { "label": "a", "dependsOn": ["b"] },
            { "label": "b", "dependsOn": "a" },
            { "label": "c", "dependsOn": ["nowhere"] },
            { "label": "m", "type": "shell", "command": "true", "problemMatcher": ["$nope", { "pattern": [{ "regexp": "a" }, { "regexp": "b" }] }, { "pattern": { "regexp": "(" } }] },
            """);
        var list = _tasks.List();
        var tasks = list["tasks"]!.AsArray();
        Assert.Equal(["build", "test", "lint", "a", "b", "c", "m"], tasks.Select(t => t!["label"]!.GetValue<string>()));
        var build = tasks[0]!;
        Assert.Equal(OperatingSystem.IsMacOS() ? "echo mac" : OperatingSystem.IsWindows() ? "echo windows" : "echo linux", build["command"]!.GetValue<string>());
        Assert.Equal(("workspace", "build", true, "Builds it"), (build["source"]!.GetValue<string>(), build["group"]!.GetValue<string>(), build["isDefault"]!.GetValue<bool>(), build["detail"]!.GetValue<string>()));
        Assert.Equal("sh -c 'exit 0' 'two words'", tasks[1]!["command"]!.GetValue<string>());
        Assert.Equal("test", tasks[1]!["group"]!.GetValue<string>());
        Assert.False(tasks[1]!["isDefault"]!.GetValue<bool>());
        Assert.False(list["trusted"]!.GetValue<bool>());
        Assert.Equal("Debug", list["buildConfiguration"]!.GetValue<string>());

        var errors = list["errors"]!.AsArray().Select(e => (e!["file"]!.GetValue<string>(), e["line"]?.GetValue<int>(), e["message"]!.GetValue<string>())).ToList();
        Assert.All(errors, e => Assert.Equal(".vscode/tasks.json", e.Item1));
        Assert.Contains(errors, e => e.Item2 == 8 && e.Item3.Contains("of type gulp", StringComparison.Ordinal));
        // One circle, said once, wherever it is entered.
        Assert.Single(errors, e => e.Item3.Contains("in a circle: a → b → a", StringComparison.Ordinal) || e.Item3.Contains("in a circle: b → a → b", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Item2 == 11 && e.Item3.Contains("waits for nowhere, which is no task", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Item3.Contains("$nope is not one Code Arena reads", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Item3.Contains("more than one line", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Item3.Contains("is no regular expression", StringComparison.Ordinal));

        // A mistake in the JSON is said with its line; another version is not read.
        Write(".vscode/tasks.json", "{\n  \"version\": \"2.0.0\",\n  \"tasks\": [\n    { \"label\": \"x\" \"command\": \"y\" }\n  ]\n}");
        var broken = Assert.Single(_tasks.List()["errors"]!.AsArray())!;
        Assert.Equal(4, broken["line"]!.GetValue<int>());
        Write(".vscode/tasks.json", "{ \"version\": \"0.1.0\", \"command\": \"make\" }");
        Assert.Contains("Only version 2.0.0", Assert.Single(_tasks.List()["errors"]!.AsArray())!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task What_a_task_waits_for_runs_one_after_another_in_sequence_and_all_at_once_in_parallel()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        TasksJson("""
            { "label": "slow", "type": "shell", "command": "sleep 0.5; echo slow >> order.txt" },
            { "label": "quick", "type": "shell", "command": "echo quick >> order.txt" },
            { "label": "in order", "type": "shell", "command": "echo last >> order.txt", "dependsOn": ["slow", "quick"], "dependsOrder": "sequence" },
            { "label": "at once", "type": "shell", "command": "echo last >> order.txt", "dependsOn": ["slow", "quick"] },
            { "label": "both", "dependsOn": ["slow", "quick"] },
            """);
        _state.SetTrust(true);
        var order = Path.Combine(_root, "order.txt");

        var (answer, exit) = await RunToEndAsync("in order");
        Assert.Equal(0, exit);
        Assert.Equal("slow\nquick\nlast\n", File.ReadAllText(order));
        Assert.Equal("in order", answer["task"]!.GetValue<string>());
        // Every terminal the run opened, the task's own last; each a task's, named after it.
        var terminals = answer["terminals"]!.AsArray();
        Assert.Equal(["slow", "quick", "in order"], terminals.Select(t => t!["title"]!.GetValue<string>()));
        Assert.All(terminals, t => Assert.Equal("task", t!["kind"]!.GetValue<string>()));
        Assert.Equal(answer["terminal"]!["id"]!.GetValue<string>(), terminals[2]!["id"]!.GetValue<string>());

        File.Delete(order);
        (_, exit) = await RunToEndAsync("at once");
        Assert.Equal(0, exit);
        Assert.Equal("quick\nslow\nlast\n", File.ReadAllText(order));

        // A task with no command of its own: done when what it waits for is.
        File.Delete(order);
        var only = await _tasks.RunAsync(Body("both"));
        Assert.Null(only["terminal"]);
        Assert.Equal(2, only["terminals"]!.AsArray().Count);
        Assert.Equal(["quick", "slow"], File.ReadAllLines(order));
    }

    [Fact]
    public async Task A_task_that_fails_stops_what_waits_for_it_and_a_circle_is_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        TasksJson("""
            { "label": "fails", "type": "shell", "command": "echo failing; exit 3" },
            { "label": "fine", "type": "shell", "command": "echo fine > fine.txt" },
            { "label": "after", "type": "shell", "command": "echo ran > ran.txt", "dependsOn": ["fails", "fine"], "dependsOrder": "sequence" },
            { "label": "a", "type": "shell", "command": "true", "dependsOn": ["b"] },
            { "label": "b", "type": "shell", "command": "true", "dependsOn": ["a"] },
            """);
        _state.SetTrust(true);

        var failed = await RefusedAsync(_tasks.RunAsync(Body("after")));
        Assert.Equal((409, "task_failed"), (failed.Status, failed.Code));
        Assert.Equal("fails", failed.Details!["task"]!.GetValue<string>());
        Assert.Equal(3, failed.Details["exitCode"]!.GetValue<int>());
        Assert.Contains("failing", TerminalTests.Output(_terminals.Find(failed.Details["terminal"]!["id"]!.GetValue<string>())!));
        // In sequence, what came after the failure never started, nor did the task.
        Assert.False(File.Exists(Path.Combine(_root, "fine.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "ran.txt")));

        var circle = await RefusedAsync(_tasks.RunAsync(Body("a")));
        Assert.Equal((400, "bad_config"), (circle.Status, circle.Code));
        Assert.Contains("in a circle: a → b → a", circle.Message);
        Assert.Equal(".vscode/tasks.json", circle.Details!["file"]!.GetValue<string>());
        Assert.DoesNotContain(_terminals.All, t => t.Title is "a" or "b");
    }

    [Fact]
    public async Task A_task_that_keeps_running_is_ready_when_its_end_pattern_shows_and_what_waits_for_it_runs_then()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Write("src/app.ts", "let a\n");
        TasksJson("""
            { "label": "watch", "type": "shell", "isBackground": true,
              "command": "echo starting; echo 'src/app.ts|1|not ready'; sleep 0.3; printf '\\033[32mwatching: ready\\033[0m\\n'; sleep 30",
              "problemMatcher": { "owner": "watch", "pattern": { "regexp": "^(.+)\\|(\\d+)\\|(.+)$", "file": 1, "line": 2, "message": 3 },
                                  "background": { "beginsPattern": "^starting", "endsPattern": "^watching: ready$" } } },
            { "label": "serve", "type": "shell", "command": "echo served", "dependsOn": ["watch"] },
            """);
        _state.SetTrust(true);

        var (answer, exit) = await RunToEndAsync("serve");
        Assert.Equal(0, exit);
        var watch = _terminals.All.Single(t => t.Title == "watch");
        // Ready (its colours dropped to read the line), still running, and what it found is in Problems.
        Assert.Null(watch.ExitCode);
        Assert.Equal(["watch", "serve"], answer["terminals"]!.AsArray().Select(t => t!["title"]!.GetValue<string>()));
        var found = Assert.Single(_problems.FromTasks());
        Assert.Equal(("src/app.ts", 1, "not ready", "task:watch"), (found.Path, found.Line, found.Message, found.Source));
        var listed = _tasks.List()["tasks"]!.AsArray().Single(t => t!["label"]!.GetValue<string>() == "watch")!;
        Assert.True(listed["running"]!.GetValue<bool>());
        Assert.Equal(watch.Id, listed["terminal"]!.GetValue<string>());

        // A running task is not started twice; stopped, its terminal says how it ended.
        var twice = await RefusedAsync(_tasks.RunAsync(Body("watch")));
        Assert.Equal((409, "running"), (twice.Status, twice.Code));
        Assert.Equal(watch.Id, twice.Details!["terminal"]!["id"]!.GetValue<string>());
        var stopped = await _tasks.StopAsync(new JsonObject { ["terminal"] = watch.Id });
        Assert.Equal(128 + 1, stopped["exitCode"]!.GetValue<int>());
        Assert.False(_tasks.List()["tasks"]!.AsArray().Single(t => t!["label"]!.GetValue<string>() == "watch")!["running"]!.GetValue<bool>());
        await Assert.ThrowsAsync<IdeError>(() => _tasks.StopAsync(new JsonObject { ["terminal"] = "99" }));
    }

    [Fact]
    public async Task Named_and_own_problem_matchers_put_what_a_task_printed_into_Problems_under_its_label()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Write("src/App.cs", "class App {}\n");
        Write("main.c", "int main() {}\n");
        Write("lib/util.py", "x = 1\n");
        TasksJson("""
            { "label": "msbuild", "type": "shell", "command": "echo 'src/App.cs(3,5): error CS1002: ; expected [/x/App.csproj]'", "problemMatcher": "$msCompile" },
            { "label": "gcc", "type": "shell", "command": "printf 'main.c:7:3: warning: unused variable [-Wunused]\\nother.c:1:1: error: not ours\\n'", "problemMatcher": ["$gcc"] },
            { "label": "own", "type": "shell", "command": "echo 'lib/util.py|4|bad thing|E1'",
              "problemMatcher": { "owner": "mine", "severity": "warning", "pattern": { "regexp": "^(.+)\\|(\\d+)\\|(.+)\\|(\\w+)$", "file": 1, "line": 2, "message": 3, "code": 4 } } },
            { "label": "located", "type": "shell", "command": "echo 'ERROR main.c@2,9 broken'",
              "problemMatcher": { "fileLocation": ["relative", "${workspaceFolder}"], "pattern": { "regexp": "^(\\w+) (.+)@([\\d,]+) (.+)$", "severity": 1, "file": 2, "location": 3, "message": 4 } } },
            """);
        _state.SetTrust(true);
        foreach (var label in new[] { "msbuild", "gcc", "own", "located" })
        {
            Assert.Equal(0, (await RunToEndAsync(label)).Exit);
        }
        await UntilAsync(() => _problems.FromTasks().Count == 4);
        Assert.Equal(
            [
                ("main.c", 7, 3, "warning", (string?)null, "unused variable [-Wunused]", "task:gcc"),
                ("main.c", 2, 9, "error", null, "broken", "task:located"),
                ("src/App.cs", 3, 5, "error", "CS1002", "; expected", "task:msbuild"),
                ("lib/util.py", 4, 1, "warning", "E1", "bad thing", "task:own"),
            ],
            _problems.FromTasks().Select(p => (p.Path, p.Line, p.Column, p.Severity, p.Code, p.Message, p.Source)));

        // Its next run's findings replace its last run's.
        TasksJson("""{ "label": "gcc", "type": "shell", "command": "true", "problemMatcher": "$gcc" }""");
        await RunToEndAsync("gcc");
        await UntilAsync(() => _problems.FromTasks().All(p => p.Source != "task:gcc"));
        Assert.Equal(3, _problems.FromTasks().Count);
    }

    [Fact]
    public void ESLint_compact_Go_and_tsc_watch_matchers_read_their_forms()
    {
        Write("web/app.js", "x\n");
        Write("cmd/main.go", "package main\n");
        Write("src/a.ts", "let a\n");
        var eslint = _problems.Read(
            $"{_root}/web/app.js: line 3, col 9, Warning - Unexpected console statement. (no-console)\n\u001b[31mweb/app.js: line 1, col 1, Error - Parsing error\u001b[0m\nnot a problem\n",
            [Problems.Named("$eslint-compact")!], "task:lint");
        Assert.Equal(
            [("web/app.js", 1, 1, "error", (string?)null, "Parsing error"), ("web/app.js", 3, 9, "warning", "no-console", "Unexpected console statement.")],
            eslint.Select(p => (p.Path, p.Line, p.Column, p.Severity, p.Code, p.Message)));
        Assert.All(eslint, p => Assert.Equal("task:lint", p.Source));

        var go = _problems.Read("# shop/cmd\ncmd/main.go:12:2: undefined: x\n./cmd/main.go:3: syntax error\n", [Problems.Named("$go")!], "task:go");
        Assert.Equal([("cmd/main.go", 3, 1, "syntax error"), ("cmd/main.go", 12, 2, "undefined: x")], go.Select(p => (p.Path, p.Line, p.Column, p.Message)));

        var watch = Problems.Named("$tsc-watch")!;
        Assert.Matches(watch.Begins!, "[10:00:00 AM] File change detected. Starting incremental compilation...");
        Assert.Matches(watch.Ends!, "[10:00:01 AM] Found 1 error. Watching for file changes.");
        Assert.Single(_problems.Read("src/a.ts(1,5): error TS2304: Cannot find name 'b'.\n", [watch], "task:watch"));
        Assert.Null(Problems.Named("$nope"));
    }

    [Fact]
    public void Detection_reads_no_files_name_as_an_option_and_no_link_to_a_device()
    {
        Write("-Evil.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        Write("package.json", """{ "scripts": { "--prefix=/tmp": "never", "build": "tsc" } }""");
        if (!OperatingSystem.IsWindows() && File.Exists("/dev/zero"))
        {
            // A link that says it holds nothing and never ends: read no further than a project's file can be.
            File.CreateSymbolicLink(Path.Combine(_root, "Makefile"), "/dev/zero");
            File.CreateSymbolicLink(Path.Combine(_root, "Cargo.toml"), "/dev/zero");
            Directory.CreateDirectory(Path.Combine(_root, "web"));
            File.CreateSymbolicLink(Path.Combine(_root, "web", "package.json"), "/dev/zero");
            File.CreateSymbolicLink(Path.Combine(_root, "web", "Zero.csproj"), "/dev/zero");
        }
        var tasks = _tasks.List()["tasks"]!.AsArray().ToDictionary(t => t!["label"]!.GetValue<string>(), t => t!["command"]?.GetValue<string>());
        Assert.Equal("dotnet build ./-Evil.csproj -c Debug -p:GenerateFullPaths=true", tasks["dotnet: build"]);
        Assert.Equal("npm run build", tasks["npm: build"]);
        Assert.DoesNotContain(tasks.Keys, label => label.Contains("prefix", StringComparison.Ordinal) || label.Contains("Zero", StringComparison.Ordinal) || label.EndsWith("- web", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tasks_terminal_is_used_again_by_its_next_run()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        TasksJson("""{ "label": "hello", "type": "process", "command": "sh", "args": ["-c", "echo \"hello $WHO\""], "options": { "env": { "WHO": "${workspaceFolderBasename}" } } }""");
        _state.SetTrust(true);
        var (first, _) = await RunToEndAsync("hello");
        var (second, _) = await RunToEndAsync("hello");
        var id = first["terminal"]!["id"]!.GetValue<string>();
        Assert.Equal(id, second["terminal"]!["id"]!.GetValue<string>());
        Assert.Single(_terminals.All);
        Assert.Contains($"hello {Path.GetFileName(_root)}", TerminalTests.Output(_terminals.Find(id)!));
    }

    [Fact]
    public async Task Tasks_json_of_an_untrusted_folder_is_refused_with_its_commands_and_inputs_are_asked_for()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        TasksJson(
            """{ "label": "greet", "type": "shell", "command": "echo ${workspaceFolder} ${input:who} ${env:ARENA_API_KEY} [${env:CODE_ARENA_TASK_TEST}]", "dependsOn": ["prepare"] }, { "label": "prepare", "type": "shell", "command": "true" }""",
            """ "inputs": [{ "id": "who", "type": "promptString", "description": "Who?", "default": "you" }], """);
        Write("Makefile", "all:\n\ttrue\n");

        var untrusted = await RefusedAsync(_tasks.RunAsync(Body("greet")));
        Assert.Equal((409, "untrusted"), (untrusted.Status, untrusted.Code));
        // What the variables may hold that is secret stays as written: the key never, the environment as ${env:…}.
        Assert.Equal(["true", $"echo {_root} ${{input:who}}  [${{env:CODE_ARENA_TASK_TEST}}]"], VsCodeJson.Strings(untrusted.Details!["commands"]));
        Assert.Empty(_terminals.All);
        // A detected task shows its command and needs no trust: it runs (or its program is missing here).
        try
        {
            var made = await _tasks.RunAsync(Body("make: build"));
            Assert.Equal("make: build", made["task"]!.GetValue<string>());
        }
        catch (IdeError e)
        {
            Assert.Equal("no_program", e.Code);
        }

        _state.SetTrust(true);
        var asked = await RefusedAsync(_tasks.RunAsync(Body("greet")));
        Assert.Equal((409, "needs_input"), (asked.Status, asked.Code));
        var input = Assert.Single(asked.Details!["inputs"]!.AsArray())!;
        Assert.Equal(("who", "promptString", "Who?", "you"), (input["id"]!.GetValue<string>(), input["type"]!.GetValue<string>(), input["description"]!.GetValue<string>(), input["default"]!.GetValue<string>()));
        Assert.DoesNotContain(_terminals.All, t => t.Title is "greet" or "prepare");

        Environment.SetEnvironmentVariable("ARENA_API_KEY", "sk-never-in-a-task");
        Environment.SetEnvironmentVariable("CODE_ARENA_TASK_TEST", "filled");
        try
        {
            var (answer, exit) = await RunToEndAsync("greet", new JsonObject { ["inputs"] = new JsonObject { ["who"] = "me" } });
            Assert.Equal(0, exit);
            var output = TerminalTests.Output(_terminals.Find(answer["terminal"]!["id"]!.GetValue<string>())!);
            Assert.Contains($"{_root} me [filled]", output);
            Assert.DoesNotContain("sk-never-in-a-task", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARENA_API_KEY", null);
            Environment.SetEnvironmentVariable("CODE_ARENA_TASK_TEST", null);
        }
    }

    [Fact]
    public void Tasks_are_detected_from_the_projects_files_and_build_its_build_configuration()
    {
        Write("Shop.sln", "");
        Write("src/Shop.Api/Shop.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("src/Shop.Lib/Shop.Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("tests/Shop.Tests/Shop.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>""");
        Write("package.json", """{ "scripts": { "build": "vite build", "start": "vite" } }""");
        Write("pnpm-lock.yaml", "");
        Write("web/package.json", """{ "scripts": { "test": "vitest" } }""");
        Write("node_modules/left-pad/package.json", """{ "scripts": { "build": "never" } }""");
        Write("bin/Hidden/Hidden.csproj", "<Project />");
        Write("Cargo.toml", "[package]\nname = \"shop\"\n");
        Write("go.mod", "module shop\n");
        Write("Makefile", "all:\n\tcc main.c\n\ntest: all\n\t./a.out\n");
        Write("CMakeLists.txt", "add_executable(shop main.c)\n");
        Write("pytest.ini", "[pytest]\n");

        Dictionary<string, (string? Command, string? Group, string Source)> Listed() => _tasks.List()["tasks"]!.AsArray().ToDictionary(
            t => t!["label"]!.GetValue<string>(),
            t => (t!["command"]?.GetValue<string>(), t["group"]?.GetValue<string>(), t["source"]!.GetValue<string>()));
        var debug = Listed();
        Assert.Equal(
            [
                "dotnet: build", "dotnet: test", "dotnet: build Shop.Api", "npm: build", "npm: start", "npm: test - web",
                "cargo: build", "cargo: test", "go: build", "go: test", "make: build", "make: test",
                "cmake: configure", "cmake: build", "cmake: test", "python: pytest",
            ],
            debug.Keys);
        Assert.Equal(("dotnet build Shop.sln -c Debug -p:GenerateFullPaths=true", "build", "detected"), debug["dotnet: build"]);
        Assert.Equal(("dotnet test Shop.sln -c Debug -p:GenerateFullPaths=true", "test", "detected"), debug["dotnet: test"]);
        Assert.Equal("dotnet build src/Shop.Api/Shop.Api.csproj -c Debug -p:GenerateFullPaths=true", debug["dotnet: build Shop.Api"].Command);
        Assert.Equal(("pnpm run build", "build", "detected"), debug["npm: build"]);
        Assert.Equal(("pnpm run test", "test", "detected"), debug["npm: test - web"]);
        Assert.Equal("cargo build", debug["cargo: build"].Command);
        Assert.Equal("go test ./...", debug["go: test"].Command);
        Assert.Equal(("make test", "test", "detected"), debug["make: test"]);
        Assert.Equal("cmake -S . -B build/Debug -DCMAKE_BUILD_TYPE=Debug", debug["cmake: configure"].Command);
        Assert.Equal(("python3 -m pytest", "test", "detected"), debug["python: pytest"]);

        _state.Merge(new JsonObject { ["buildConfiguration"] = "Release" });
        var release = Listed();
        Assert.Equal("dotnet build Shop.sln -c Release -p:GenerateFullPaths=true", release["dotnet: build"].Command);
        Assert.Equal("cargo build --release", release["cargo: build"].Command);
        Assert.Equal("cargo test --release", release["cargo: test"].Command);
        Assert.Equal("cmake --build build/Release", release["cmake: build"].Command);

        // tasks.json's task of the same label is the one.
        TasksJson("""{ "label": "cargo: build", "type": "shell", "command": "cargo build --locked" }""");
        Assert.Equal(("cargo build --locked", null, "workspace"), Listed()["cargo: build"]);
    }

    [Fact]
    public async Task The_page_lists_runs_and_stops_tasks_and_their_problems_with_this_runs_key_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["terminalShell"] = "/bin/sh");
        h.Write("src/a.ts", "let a\nlet b c\n");
        h.Write(".vscode/tasks.json", """
            {
              "version": "2.0.0",
              "tasks": [
                { "label": "check", "type": "shell", "command": "echo 'src/a.ts(2,7): error TS1005: ; expected.'; exit 2", "problemMatcher": "$tsc", "group": { "kind": "build", "isDefault": true } },
                { "label": "other", "type": "shell", "command": "true", "group": "build" },
                { "label": "unit", "type": "shell", "command": "true", "group": "test" },
                { "label": "e2e", "type": "shell", "command": "true", "group": "test" },
                { "label": "long", "type": "shell", "command": "sleep 30" },
              ],
            }
            """);
        await using var web = await WebRun.StartAsync(h);
        var list = await web.GetJsonAsync("/api/tasks");
        Assert.Equal(["check", "other", "unit", "e2e", "long"], list["tasks"]!.AsArray().Select(t => t!["label"]!.GetValue<string>()));
        Assert.Empty(list["errors"]!.AsArray());

        var untrusted = await web.PostAsync("/api/tasks/run", new JsonObject { ["group"] = "build" });
        Assert.Equal(HttpStatusCode.Conflict, untrusted.StatusCode);
        var refusal = JsonNode.Parse(await untrusted.Content.ReadAsStringAsync())!;
        Assert.Equal("untrusted", refusal["status"]!.GetValue<string>());
        Assert.Contains("TS1005", refusal["commands"]![0]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true })).StatusCode);

        // The build group's default; its problems in Problems, under its label.
        var ran = await Json(await web.PostAsync("/api/tasks/run", new JsonObject { ["group"] = "build", ["cols"] = 100, ["rows"] = 30 }));
        Assert.Equal("check", ran["task"]!.GetValue<string>());
        Assert.Equal(100, ran["terminal"]!["cols"]!.GetValue<int>());
        var id = ran["terminal"]!["id"]!.GetValue<string>();
        JsonNode? problem = null;
        for (var waited = 0; problem is null && waited < 10_000; waited += 100)
        {
            await Task.Delay(100);
            problem = (await web.GetJsonAsync("/api/problems"))["problems"]!.AsArray().FirstOrDefault(p => p!["source"]!.GetValue<string>() == "task:check");
        }
        Assert.NotNull(problem);
        Assert.Equal(("src/a.ts", 2, "TS1005"), (problem["path"]!.GetValue<string>(), problem["line"]!.GetValue<int>(), problem["code"]!.GetValue<string>()));
        Assert.Equal(2, (await web.GetJsonAsync("/api/terminals")).AsArray().Single(t => t!["id"]!.GetValue<string>() == id)!["exitCode"]!.GetValue<int>());

        // The test group has two and no default: the page picks.
        var pick = await web.PostAsync("/api/tasks/run", new JsonObject { ["group"] = "test" });
        Assert.Equal(HttpStatusCode.Conflict, pick.StatusCode);
        Assert.Equal(["unit", "e2e"], VsCodeJson.Strings(JsonNode.Parse(await pick.Content.ReadAsStringAsync())!["tasks"]));
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/tasks/run", new JsonObject { ["label"] = "nope" })).StatusCode);

        var running = await Json(await web.PostAsync("/api/tasks/run", new JsonObject { ["label"] = "long" }));
        var stopped = await Json(await web.PostAsync("/api/tasks/stop", new JsonObject { ["terminal"] = running["terminal"]!["id"]!.GetValue<string>() }));
        Assert.Equal(128 + 1, stopped["exitCode"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/tasks/stop", new JsonObject { ["terminal"] = "99" })).StatusCode);

        // None of it without this run's key.
        using var anonymous = WebRun.Client(web.Port, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tasks")).StatusCode);
        foreach (var path in new[] { "/api/tasks/run", "/api/tasks/stop" })
        {
            using var content = new StringContent("""{"label":"long","terminal":"1"}""", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, content)).StatusCode);
        }
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }
}
