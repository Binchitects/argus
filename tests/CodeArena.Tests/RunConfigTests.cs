using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Run configurations: launch.json read as VS Code reads it, its variables and inputs, configurations the project's files give, and runs without debugging.</summary>
public sealed class RunConfigTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _root = Directory.CreateTempSubdirectory("code-arena-runs-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("code-arena-runs-data-").FullName;
    private readonly Terminals _terminals;
    private readonly WorkspaceState _state;
    private readonly Tasks _tasks;
    private readonly RunConfigs _runs;
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public RunConfigTests()
    {
        var workspace = new Workspace(_root);
        _terminals = new Terminals(workspace, new Config { TerminalShell = "/bin/sh" }, _ => null);
        _state = new WorkspaceState(_data, workspace.Root);
        var problems = new Problems(workspace, new Config(), _state);
        _tasks = new Tasks(workspace, new IdeFiles(workspace), _terminals, problems, _state, Environment.GetEnvironmentVariable);
        _runs = new RunConfigs(workspace, _terminals, _tasks, _state, Environment.GetEnvironmentVariable);
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

    private void LaunchJson(string configurations, string more = "") => Write(".vscode/launch.json", $$"""
        {
          // as VS Code writes it: comments and trailing commas
          "version": "0.2.0",
          {{more}}
          "configurations": [{{configurations}}],
        }
        """);

    private static JsonObject Start(string name, JsonObject? context = null, JsonObject? inputs = null) => new()
    {
        ["name"] = name,
        ["noDebug"] = true,
        ["context"] = context,
        ["inputs"] = inputs,
    };

    private static async Task<IdeError> RefusedAsync(Task task) => await Assert.ThrowsAsync<IdeError>(() => task);

    private static IdeError Refused(Action action) => Assert.Throws<IdeError>(action);

    [Fact]
    public void Launch_json_with_comments_gives_its_configurations_compounds_and_this_systems_part_and_says_what_is_wrong()
    {
        LaunchJson(
            """
            { "name": "App", "type": "cppdbg", "request": "launch", "program": "${workspaceFolder}/app", "args": ["one"],
              "linux": { "args": ["linux"] }, "osx": { "args": ["mac"] }, "windows": { "args": ["windows"] }, "preLaunchTask": "build", },
            { "name": "Attach", "type": "coreclr", "request": "attach", "processId": "${command:pickProcess}" },
            { "name": "Chrome", "type": "chrome", "request": "launch", "url": "http://localhost:5173" },
            { "name": "Broken", "type": "node" },
            { "name": "Hidden", "type": "node", "request": "launch", "program": "server.js", "args": ["two words"], "presentation": { "hidden": true } },
            """,
            """ "compounds": [{ "name": "Both", "configurations": ["App", "Missing"], "stopAll": true }], """);
        var list = _runs.List();
        var configurations = list["configurations"]!.AsArray();
        Assert.Equal(["App", "Attach", "Chrome", "Hidden"], configurations.Select(c => c!["name"]!.GetValue<string>()));
        var app = configurations[0]!;
        var system = OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "windows" : "linux";
        Assert.Equal(("cppdbg", "launch", "launch", "${workspaceFolder}/app"), (app["type"]!.GetValue<string>(), app["request"]!.GetValue<string>(), app["source"]!.GetValue<string>(), app["program"]!.GetValue<string>()));
        Assert.Equal($"${{workspaceFolder}}/app {system}", app["command"]!.GetValue<string>());
        Assert.Equal("build", app["preLaunchTask"]!.GetValue<string>());
        Assert.True(app["supported"]!.GetValue<bool>());
        Assert.Null(app["reason"]);
        Assert.Null(app["configuration"]);
        Assert.False(configurations[1]!["supported"]!.GetValue<bool>());
        Assert.Contains("attaching", configurations[1]!["reason"]!.GetValue<string>());
        Assert.Contains("chrome configuration runs only with its debugger", configurations[2]!["reason"]!.GetValue<string>());
        Assert.True(configurations[3]!["hidden"]!.GetValue<bool>());
        Assert.Equal("node server.js 'two words'", configurations[3]!["command"]!.GetValue<string>());

        var compound = Assert.Single(list["compounds"]!.AsArray())!;
        Assert.Equal(("Both", true), (compound["name"]!.GetValue<string>(), compound["stopAll"]!.GetValue<bool>()));
        Assert.Equal(["App", "Missing"], VsCodeJson.Strings(compound["configurations"]));
        var errors = list["errors"]!.AsArray().Select(e => (e!["line"]?.GetValue<int>(), e["message"]!.GetValue<string>())).ToList();
        Assert.Contains(errors, e => e.Item1 == 9 && e.Item2.Contains("Broken needs a type, and a request", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Item2.Contains("names Missing, which is no configuration", StringComparison.Ordinal));
        Assert.False(list["trusted"]!.GetValue<bool>());

        Write(".vscode/launch.json", "{\n  \"configurations\": [\n    { \"name\": \"x\",, }\n  ]\n}");
        var broken = Assert.Single(_runs.List()["errors"]!.AsArray())!;
        Assert.Equal((".vscode/launch.json", 3), (broken["file"]!.GetValue<string>(), broken["line"]!.GetValue<int>()));
    }

    [Fact]
    public void Every_variable_is_filled_in_from_the_folder_the_editor_the_environment_the_settings_and_the_answers()
    {
        LaunchJson(
            """
            { "name": "Vars", "type": "cppdbg", "request": "launch", "program": "/bin/echo", "args": [
                "${workspaceFolder}", "${workspaceFolderBasename}", "${file}", "${fileBasename}", "${fileBasenameNoExtension}", "${fileDirname}",
                "${fileExtname}", "${relativeFile}", "${relativeFileDirname}", "${lineNumber}", "${selectedText}", "${cwd}", "${pathSeparator}", "${/}",
                "${env:CODE_ARENA_RUN_TEST}", "${env:ARENA_API_KEY}", "${config:editor.tabSize}", "${config:python.venv}", "${config:codeArena.buildConfiguration}",
                "${codeArena.buildConfiguration}", "${userHome}", "${input:name}", "${input:color}", "at ${workspaceFolderBasename}:${lineNumber}"],
              "anyKey": "${workspaceFolder}" },
            """,
            """
             "inputs": [
               { "id": "name", "type": "promptString", "description": "Your name", "default": "you" },
               { "id": "color", "type": "pickString", "description": "A colour", "options": ["red", { "label": "Blue", "value": "blue" }], "default": "red" },
             ],
            """);
        _state.SetTrust(true);
        _state.Merge(new JsonObject { ["buildConfiguration"] = "Release" });
        var context = RunContext.From(new JsonObject
        {
            ["file"] = "src/lib/util.ts",
            ["lineNumber"] = 12,
            ["selectedText"] = "picked",
            ["settings"] = new JsonObject { ["editor.tabSize"] = 4, ["python"] = new JsonObject { ["venv"] = ".venv" } },
        });

        // Inputs first: what to ask, with a pickString's options as {label, value}.
        var asked = Refused(() => _runs.Resolve("Vars", context, new Dictionary<string, string>()));
        Assert.Equal((409, "needs_input"), (asked.Status, asked.Code));
        var inputs = asked.Details!["inputs"]!.AsArray();
        Assert.Equal(["name", "color"], inputs.Select(i => i!["id"]!.GetValue<string>()));
        Assert.Equal(("promptString", "Your name", "you"), (inputs[0]!["type"]!.GetValue<string>(), inputs[0]!["description"]!.GetValue<string>(), inputs[0]!["default"]!.GetValue<string>()));
        Assert.Equal(["red:red", "Blue:blue"], inputs[1]!["options"]!.AsArray().Select(o => $"{o!["label"]}:{o["value"]}"));

        Environment.SetEnvironmentVariable("CODE_ARENA_RUN_TEST", "from-env");
        Environment.SetEnvironmentVariable("ARENA_API_KEY", "sk-never-filled-in");
        JsonObject resolved;
        try
        {
            resolved = _runs.Resolve("Vars", context, new Dictionary<string, string> { ["name"] = "Ada", ["color"] = "blue" });
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARENA_RUN_TEST", null);
            Environment.SetEnvironmentVariable("ARENA_API_KEY", null);
        }
        var sep = Path.DirectorySeparatorChar.ToString();
        var file = Path.Combine(_root, "src", "lib", "util.ts");
        Assert.Equal(
            [
                _root, Path.GetFileName(_root), file, "util.ts", "util", Path.Combine(_root, "src", "lib"),
                ".ts", Path.Combine("src", "lib", "util.ts"), Path.Combine("src", "lib"), "12", "picked", _root, sep, sep,
                "from-env", "", "4", ".venv", "Release",
                "Release", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Ada", "blue", $"at {Path.GetFileName(_root)}:12",
            ],
            VsCodeJson.Strings(resolved["args"]));
        Assert.Equal("Vars", resolved["name"]!.GetValue<string>());
        Assert.Equal(_root, resolved["anyKey"]!.GetValue<string>());
    }

    [Fact]
    public void A_variable_not_known_a_command_a_missing_input_or_file_and_a_file_outside_are_refused_naming_them()
    {
        LaunchJson("""
            { "name": "Unknown", "type": "cppdbg", "request": "launch", "program": "${nope}" },
            { "name": "Command", "type": "cppdbg", "request": "launch", "program": "${command:pickProcess}" },
            { "name": "Input", "type": "cppdbg", "request": "launch", "program": "${input:missing}" },
            { "name": "File", "type": "cppdbg", "request": "launch", "program": "${file}" },
            { "name": "Setting", "type": "cppdbg", "request": "launch", "program": "${config:not.set}" },
            """);
        _state.SetTrust(true);
        var none = new Dictionary<string, string>();
        foreach (var (name, said) in new[]
        {
            ("Unknown", "${nope} is not a variable"),
            ("Command", "${command:pickProcess} runs a command of VS Code's"),
            ("Input", ".vscode/launch.json has no input missing"),
            ("File", "${file} needs a file open in the editor"),
            ("Setting", "the setting not.set is not set"),
        })
        {
            var refused = Refused(() => _runs.Resolve(name, new RunContext(), none));
            Assert.Equal((400, "bad_config"), (refused.Status, refused.Code));
            Assert.Contains(said, refused.Message);
        }
        var outside = Refused(() => _runs.Resolve("File", RunContext.From(new JsonObject { ["file"] = "../secret.txt" }), none));
        Assert.Equal(403, outside.Status);
        Assert.Equal(404, Refused(() => _runs.Resolve("Nothing", new RunContext(), none)).Status);
    }

    [Fact]
    public void An_env_file_is_read_under_the_configurations_own_env()
    {
        Write(".env", "A=1\nexport B=from-file\n# a comment\nC=\"quoted value\"\nD='single'\nE=plain # trailing\n");
        LaunchJson("""
            { "name": "Env", "type": "cppdbg", "request": "launch", "program": "/bin/sh", "envFile": "${workspaceFolder}/.env", "env": { "B": "own" } },
            { "name": "Missing", "type": "cppdbg", "request": "launch", "program": "/bin/sh", "envFile": "${workspaceFolder}/none.env" },
            """);
        _state.SetTrust(true);
        var resolved = _runs.Resolve("Env", new RunContext(), new Dictionary<string, string>());
        Assert.Equal(["A=1", "B=own", "C=quoted value", "D=single", "E=plain"], resolved["env"]!.AsObject().Select(e => $"{e.Key}={e.Value}"));
        Assert.Null(resolved["envFile"]);
        Assert.Contains("is not there", Refused(() => _runs.Resolve("Missing", new RunContext(), new Dictionary<string, string>())).Message);
    }

    [Fact]
    public async Task A_configuration_runs_without_debugging_after_its_preLaunchTask_in_a_terminal_of_its_own_used_again_next_time()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Write(".vscode/tasks.json", """
            {
              "version": "2.0.0",
              "tasks": [
                { "label": "prepare", "type": "shell", "command": "echo prepared > prepared.txt" },
                { "label": "fail", "type": "shell", "command": "exit 4" },
                { "label": "after", "type": "shell", "command": "echo after > after.txt" },
              ],
            }
            """);
        LaunchJson("""
            { "name": "Run", "type": "cppdbg", "request": "launch", "program": "/bin/sh", "args": ["-c", "cat prepared.txt; echo \"[$GREETING]\"; exit 5"],
              "env": { "GREETING": "hi" }, "preLaunchTask": "prepare", "postDebugTask": "after" },
            { "name": "Broken", "type": "cppdbg", "request": "launch", "program": "/bin/sh", "args": ["-c", "echo never > never.txt"], "preLaunchTask": "fail" },
            { "name": "Long", "type": "cppdbg", "request": "launch", "program": "/bin/sh", "args": ["-c", "sleep 30"] },
            """,
            """ "compounds": [{ "name": "Both", "configurations": ["Run", "Long"] }], """);

        // Not trusted: the commands that would run, the task's first.
        var untrusted = await RefusedAsync(_runs.StartAsync(Start("Run")));
        Assert.Equal((409, "untrusted"), (untrusted.Status, untrusted.Code));
        Assert.Equal(["echo prepared > prepared.txt", "/bin/sh -c 'cat prepared.txt; echo \"[$GREETING]\"; exit 5'", "echo after > after.txt"], VsCodeJson.Strings(untrusted.Details!["commands"]));
        Assert.Empty(_terminals.All);
        _state.SetTrust(true);

        var started = await _runs.StartAsync(Start("Run"));
        var terminal = _terminals.Find(started["terminal"]!["id"]!.GetValue<string>())!;
        Assert.Equal(5, await terminal.Exited.WaitAsync(Wait));
        Assert.Equal(("run", "Run"), (started["terminal"]!["kind"]!.GetValue<string>(), started["terminal"]!["title"]!.GetValue<string>()));
        Assert.Equal("/bin/sh", started["configuration"]!["program"]!.GetValue<string>());
        Assert.StartsWith("/bin/sh -c ", started["command"]!.GetValue<string>());
        var output = TerminalTests.Output(terminal);
        Assert.Contains("prepared", output);
        Assert.Contains("[hi]", output);
        // The postDebugTask, once the program ended.
        for (var waited = 0; !File.Exists(Path.Combine(_root, "after.txt")) && waited < 10_000; waited += 50)
        {
            await Task.Delay(50);
        }
        Assert.True(File.Exists(Path.Combine(_root, "after.txt")));

        var again = await _runs.StartAsync(Start("Run"));
        Assert.Equal(terminal.Id, again["terminal"]!["id"]!.GetValue<string>());

        // A preLaunchTask that fails stops the start, with its exit code.
        var failed = await RefusedAsync(_runs.StartAsync(Start("Broken")));
        Assert.Equal((409, "task_failed"), (failed.Status, failed.Code));
        Assert.Equal(("fail", 4), (failed.Details!["task"]!.GetValue<string>(), failed.Details["exitCode"]!.GetValue<int>()));
        Assert.False(File.Exists(Path.Combine(_root, "never.txt")));

        // Running twice is refused; stopped, it says how it ended.
        var long1 = await _runs.StartAsync(Start("Long"));
        var twice = await RefusedAsync(_runs.StartAsync(Start("Long")));
        Assert.Equal((409, "running"), (twice.Status, twice.Code));
        var stopped = await _runs.StopAsync(new JsonObject { ["terminal"] = long1["terminal"]!["id"]!.GetValue<string>() });
        Assert.Equal(128 + 1, stopped["exitCode"]!.GetValue<int>());

        // Debugging, and compounds, come with the debugger.
        var debug = Start("Run");
        debug["noDebug"] = false;
        Assert.Equal((501, "not_yet"), await StatusAsync(_runs.StartAsync(debug)));
        Assert.Equal((501, "not_yet"), await StatusAsync(_runs.StartAsync(Start("Both"))));
        Assert.Equal((404, "not_found"), await StatusAsync(_runs.StartAsync(Start("Nothing"))));
    }

    private static async Task<(int, string)> StatusAsync(Task task)
    {
        var e = await RefusedAsync(task);
        return (e.Status, e.Code);
    }

    [Fact]
    public async Task Configurations_are_detected_from_the_projects_files_and_follow_the_build_configuration()
    {
        Write("Shop.sln", "");
        Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("src/Api/Api.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><AssemblyName>ShopApi</AssemblyName></PropertyGroup></Project>""");
        Write("src/Worker/Worker.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker"></Project>""");
        Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write("tests/T/T.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>""");
        Write("package.json", """{ "scripts": { "start": "node server.js", "dev": "vite", "build": "vite build" } }""");
        Write("web/package.json", """{ "scripts": { "dev": "vite" } }""");
        Write("go.mod", "module shop\n");
        Write("Cargo.toml", "[package]\nname = \"shop\"\n\n[[bin]]\nname = \"cli\"\npath = \"src/cli.rs\"\n");
        Write("CMakeLists.txt", "project(shop C)\nadd_executable(tool main.c)\n");
        Write("app.py", "print('hi')\n");
        Write("Makefile", "all:\n\tcc -o build/prog main.c\n");
        var native = false;
        if (!OperatingSystem.IsWindows() && File.Exists("/bin/true"))
        {
            native = true;
            Directory.CreateDirectory(Path.Combine(_root, "build"));
            File.Copy("/bin/true", Path.Combine(_root, "build", "prog"));
            File.SetUnixFileMode(Path.Combine(_root, "build", "prog"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Write("build/notes.sh", "#!/bin/sh\n");
        }

        Dictionary<string, JsonNode> Listed() => _runs.List()["configurations"]!.AsArray().ToDictionary(c => c!["name"]!.GetValue<string>(), c => c!);
        var debug = Listed();
        Assert.Equal(
            [
                ".NET: Api", ".NET: Worker", "npm: start", "npm: dev", "npm: dev - web", "Python: Current File", "Go: Run Package", "Cargo: Run cli", "CMake: tool",
                .. native ? ["Make: build/prog"] : Array.Empty<string>(),
            ],
            debug.Keys);
        Assert.All(debug.Values, c => Assert.Equal(("detected", true), (c["source"]!.GetValue<string>(), c["supported"]!.GetValue<bool>())));
        var api = debug[".NET: Api"];
        Assert.Equal("dotnet: build Api", api["preLaunchTask"]!.GetValue<string>());
        Assert.Equal("${workspaceFolder}/src/Api/bin/${config:codeArena.buildConfiguration}/net9.0/ShopApi.dll", api["program"]!.GetValue<string>());
        Assert.Equal(("coreclr", "${workspaceFolder}/src/Api"), (api["configuration"]!["type"]!.GetValue<string>(), api["configuration"]!["cwd"]!.GetValue<string>()));
        Assert.Equal("${workspaceFolder}/src/Worker/bin/${config:codeArena.buildConfiguration}/net10.0/Worker.dll", debug[".NET: Worker"]["program"]!.GetValue<string>());
        Assert.Equal("npm run start", debug["npm: start"]["command"]!.GetValue<string>());
        Assert.Equal("${workspaceFolder}/web", debug["npm: dev - web"]["configuration"]!["cwd"]!.GetValue<string>());
        Assert.Equal("python3 ${file}", debug["Python: Current File"]["command"]!.GetValue<string>());
        Assert.Equal("go run ${workspaceFolder}", debug["Go: Run Package"]["command"]!.GetValue<string>());
        Assert.Equal("cargo run --bin=cli", debug["Cargo: Run cli"]["command"]!.GetValue<string>());
        Assert.Equal(("cmake: build", "${workspaceFolder}/build/${config:codeArena.buildConfiguration}/tool"), (debug["CMake: tool"]["preLaunchTask"]!.GetValue<string>(), debug["CMake: tool"]["program"]!.GetValue<string>()));
        if (native)
        {
            Assert.Equal("make: build", debug["Make: build/prog"]["preLaunchTask"]!.GetValue<string>());
        }

        // Release: the build with -c Release and the program it makes, cargo's --release, Python's -O, Go's flags.
        _state.Merge(new JsonObject { ["buildConfiguration"] = "Release" });
        var release = Listed();
        Assert.Equal("cargo run --bin=cli --release", release["Cargo: Run cli"]["command"]!.GetValue<string>());
        Assert.Equal("python3 -O ${file}", release["Python: Current File"]["command"]!.GetValue<string>());
        Assert.Equal("go run -trimpath '-ldflags=-s -w' ${workspaceFolder}", release["Go: Run Package"]["command"]!.GetValue<string>());
        var built = _tasks.List()["tasks"]!.AsArray().Single(t => t!["label"]!.GetValue<string>() == "dotnet: build Api")!;
        Assert.Equal("dotnet build src/Api/Api.csproj -c Release -p:GenerateFullPaths=true", built["command"]!.GetValue<string>());
        var none = new Dictionary<string, string>();
        Assert.Equal($"{_root}/src/Api/bin/Release/net9.0/ShopApi.dll", _runs.Resolve(".NET: Api", new RunContext(), none)["program"]!.GetValue<string>());
        Assert.Equal($"{_root}/build/Release/tool", _runs.Resolve("CMake: tool", new RunContext(), none)["program"]!.GetValue<string>());
        var python = _runs.Resolve("Python: Current File", RunContext.From(new JsonObject { ["file"] = "app.py" }), none);
        Assert.Equal((Path.Combine(_root, "app.py"), "-O"), (python["program"]!.GetValue<string>(), VsCodeJson.Strings(python["pythonArgs"]).Single()));
        // Go's package folder runs as go run . from it.
        var go = _runs.Plain(_runs.Resolve("Go: Run Package", new RunContext(), none), out _)!;
        Assert.Equal(("go", "run -trimpath -ldflags=-s -w .", _root), (go.Program, string.Join(' ', go.Args), go.Cwd));

        // Detected configurations need no trust: they run (or their program is missing here), never 409 untrusted.
        try
        {
            await _runs.StartAsync(Start("Python: Current File", new JsonObject { ["file"] = "app.py" }));
        }
        catch (IdeError e)
        {
            Assert.Equal("no_program", e.Code);
        }

        // launch.json's configuration of the same name is the one.
        LaunchJson("""{ "name": "Go: Run Package", "type": "go", "request": "launch", "mode": "exec", "program": "${workspaceFolder}/bin/shop" }""");
        Assert.Equal(("launch", "${workspaceFolder}/bin/shop"), (Listed()["Go: Run Package"]["source"]!.GetValue<string>(), Listed()["Go: Run Package"]["command"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_page_lists_starts_and_stops_runs_with_this_runs_key_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["terminalShell"] = "/bin/sh");
        h.Write(".vscode/launch.json", """
            {
              "configurations": [
                { "name": "Greet", "type": "cppdbg", "request": "launch", "program": "/bin/sh", "args": ["-c", "echo \"hello ${input:who} at ${lineNumber}\"; sleep 30"] },
              ],
              "inputs": [{ "id": "who", "type": "promptString", "description": "Who?" }],
            }
            """);
        h.Write("src/a.ts", "let a\n");
        await using var web = await WebRun.StartAsync(h);
        var list = await web.GetJsonAsync("/api/run/configs");
        Assert.Equal("Greet", list["configurations"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Debug", list["buildConfiguration"]!.GetValue<string>());

        var body = new JsonObject { ["name"] = "Greet", ["noDebug"] = true, ["context"] = new JsonObject { ["file"] = "src/a.ts", ["lineNumber"] = 7 }, ["cols"] = 90 };
        Assert.Equal("untrusted", await StatusAsync(await web.PostAsync("/api/run/start", body), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true })).StatusCode);
        var asked = await web.PostAsync("/api/run/start", body);
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var needs = JsonNode.Parse(await asked.Content.ReadAsStringAsync())!;
        Assert.Equal(("needs_input", "who"), (needs["status"]!.GetValue<string>(), needs["inputs"]![0]!["id"]!.GetValue<string>()));

        body["inputs"] = new JsonObject { ["who"] = "Ada" };
        var started = await web.PostAsync("/api/run/start", body);
        var run = JsonNode.Parse(await started.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(90, run["terminal"]!["cols"]!.GetValue<int>());
        Assert.Equal("echo \"hello Ada at 7\"; sleep 30", VsCodeJson.Strings(run["configuration"]!["args"])[1]);
        var id = run["terminal"]!["id"]!.GetValue<string>();
        var terminals = await web.GetJsonAsync("/api/terminals");
        Assert.Equal("run", terminals.AsArray().Single(t => t!["id"]!.GetValue<string>() == id)!["kind"]!.GetValue<string>());

        var stopped = await web.PostAsync("/api/run/stop", new JsonObject { ["terminal"] = id });
        Assert.Equal(129, JsonNode.Parse(await stopped.Content.ReadAsStringAsync())!["exitCode"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/run/stop", new JsonObject { ["terminal"] = "99" })).StatusCode);
        body["noDebug"] = false;
        Assert.Equal("not_yet", await StatusAsync(await web.PostAsync("/api/run/start", body), HttpStatusCode.NotImplemented));
        body["noDebug"] = true;
        body["context"] = new JsonObject { ["file"] = "../outside.ts" };
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/run/start", body)).StatusCode);

        // None of it without this run's key.
        using var anonymous = WebRun.Client(web.Port, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/run/configs")).StatusCode);
        foreach (var path in new[] { "/api/run/start", "/api/run/stop" })
        {
            using var content = new StringContent("""{"name":"Greet","noDebug":true,"terminal":"1"}""", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, content)).StatusCode);
        }
    }

    private static async Task<string> StatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!["status"]!.GetValue<string>();
    }
}
