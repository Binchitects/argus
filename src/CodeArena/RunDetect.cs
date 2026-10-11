using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A run configuration a project's files give: launch.json's shape, and for .NET the project whose built program it runs.</summary>
internal sealed record DetectedConfig(JsonObject Configuration, string? Project = null);

/// <summary>
/// What a project's own files say can be built, tested and run, with no .vscode file, as VS Code's extensions detect
/// it: .NET projects, npm scripts, Cargo, Go, Make, CMake, pytest and Python files. Each a task in tasks.json's shape or
/// a configuration in launch.json's (so the page can copy one into .vscode), building and running the project's build
/// configuration (Debug or Release).
/// </summary>
internal static partial class RunDetect
{
    /// <summary>How deep under the folder projects are looked for.</summary>
    private const int Depth = 4;

    /// <summary>The most folder entries looked at.</summary>
    private const int MostEntries = 20_000;

    // Build output, packages and tools' folders: never a project of the person's.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "target", "dist", "build", "out", "venv", "__pycache__", "packages",
    };

    /// <summary>The project files found under a folder, as full paths, nearest first, and whether Python files are near its top.</summary>
    private sealed record Found(List<string> Projects, List<string> Packages, bool Python);

    private static Found Look(string root)
    {
        var projects = new List<string>();
        var packages = new List<string>();
        var python = false;
        var level = new List<string> { root };
        var seen = 0;
        for (var depth = 0; depth <= Depth && level.Count > 0 && seen < MostEntries; depth++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
                    {
                        seen++;
                        var name = Path.GetFileName(file);
                        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                        {
                            projects.Add(file);
                        }
                        else if (name == "package.json" && depth <= 2)
                        {
                            packages.Add(file);
                        }
                        else if (depth <= 2 && (name.EndsWith(".py", StringComparison.Ordinal) || name == "pyproject.toml"))
                        {
                            python = true;
                        }
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
                    {
                        seen++;
                        var info = new DirectoryInfo(sub);
                        // Hidden folders (.git, .vs, .venv) hold no project of the person's; a link may lead anywhere.
                        if (!info.Name.StartsWith('.') && !Skipped.Contains(info.Name) && info.LinkTarget is null)
                        {
                            next.Add(sub);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A folder that cannot be read has nothing to offer.
                }
            }
            level = next;
        }
        return new Found(projects, packages, python);
    }

    /// <summary>A path of the folder as tasks.json writes one: relative, with "/".</summary>
    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>A task in tasks.json's shape.</summary>
    private static JsonObject Task(string label, string command, IEnumerable<string> args, string? group = null, string? dir = null, string? matcher = null, string? detail = null, IEnumerable<string>? dependsOn = null)
    {
        var task = new JsonObject
        {
            ["label"] = label,
            ["type"] = "process",
            ["command"] = command,
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode)JsonValue.Create(a))]),
        };
        if (dir is { Length: > 0 } and not ".")
        {
            task["options"] = new JsonObject { ["cwd"] = "${workspaceFolder}/" + dir };
        }
        if (group is not null)
        {
            task["group"] = group;
        }
        if (matcher is not null)
        {
            task["problemMatcher"] = matcher;
        }
        if (dependsOn is not null)
        {
            task["dependsOn"] = new JsonArray([.. dependsOn.Select(d => (JsonNode)JsonValue.Create(d))]);
            task["dependsOrder"] = "sequence";
        }
        if (detail is not null)
        {
            task["detail"] = detail;
        }
        return task;
    }

    /// <summary>The tasks the project's files give: build and test for each ecosystem found, npm's scripts, in the build configuration given.</summary>
    public static List<JsonObject> Tasks(string root, string configuration, Func<string, string?> env)
    {
        var found = Look(root);
        var tasks = new List<JsonObject>();
        DotnetTasks(root, configuration, found, tasks);
        NpmTasks(root, found, tasks);
        var release = configuration == "Release";
        if (File.Exists(Path.Combine(root, "Cargo.toml")))
        {
            tasks.Add(Task("cargo: build", "cargo", release ? ["build", "--release"] : ["build"], "build"));
            tasks.Add(Task("cargo: test", "cargo", release ? ["test", "--release"] : ["test"], "test"));
        }
        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            tasks.Add(Task("go: build", "go", ["build", "./..."], "build", matcher: "$go"));
            tasks.Add(Task("go: test", "go", ["test", "./..."], "test", matcher: "$go"));
        }
        if (Makefile(root) is { } makefile)
        {
            tasks.Add(Task("make: build", "make", [], "build", matcher: "$gcc"));
            var text = Read(makefile);
            foreach (var target in new[] { "test", "check", "clean" })
            {
                if (Regex.IsMatch(text, $@"^{target}\s*:", RegexOptions.Multiline, TimeSpan.FromSeconds(1)))
                {
                    tasks.Add(Task($"make: {target}", "make", [target], target is "test" or "check" ? "test" : null, matcher: "$gcc"));
                }
            }
        }
        if (File.Exists(Path.Combine(root, "CMakeLists.txt")))
        {
            var dir = $"build/{configuration}";
            tasks.Add(Task("cmake: configure", "cmake", ["-S", ".", "-B", dir, $"-DCMAKE_BUILD_TYPE={configuration}"]));
            tasks.Add(Task("cmake: build", "cmake", ["--build", dir], "build", matcher: "$gcc", dependsOn: ["cmake: configure"]));
            tasks.Add(Task("cmake: test", "ctest", ["--test-dir", dir], "test", dependsOn: ["cmake: build"]));
        }
        if (Pytest(root))
        {
            tasks.Add(Task("python: pytest", Python(root, env), ["-m", "pytest"], "test"));
        }
        return tasks;
    }

    /// <summary>
    /// .NET: the solution's build and test (else the one project's), and a build of each project to run (what its run
    /// builds first) when there are more; -c Debug or -c Release, with full paths for the problems.
    /// </summary>
    private static void DotnetTasks(string root, string configuration, Found found, List<JsonObject> tasks)
    {
        var solution = Solution(root);
        if (solution is null && found.Projects.Count == 0)
        {
            return;
        }
        string[] Args(string verb, string target) => [verb, target, "-c", configuration, "-p:GenerateFullPaths=true"];
        var tests = found.Projects.Where(p => IsTest(Read(p))).ToList();
        if (solution is not null || found.Projects.Count == 1)
        {
            var target = Rel(root, solution ?? found.Projects[0]);
            tasks.Add(Task("dotnet: build", "dotnet", Args("build", target), "build", matcher: "$msCompile"));
            if (tests.Count > 0)
            {
                tasks.Add(Task("dotnet: test", "dotnet", Args("test", target), "test", matcher: "$msCompile"));
            }
        }
        else
        {
            foreach (var test in tests)
            {
                tasks.Add(Task($"dotnet: test {ProjectLabel(found.Projects, test)}", "dotnet", Args("test", Rel(root, test)), "test", matcher: "$msCompile"));
            }
        }
        if (solution is not null || found.Projects.Count > 1)
        {
            // Without a solution every project needs its own build; with one, the projects to run do.
            foreach (var project in found.Projects.Where(p => solution is null || Runnable(Read(p))))
            {
                tasks.Add(Task(BuildLabel(found, solution, project), "dotnet", Args("build", Rel(root, project)), "build", matcher: "$msCompile"));
            }
        }
    }

    private static string? Solution(string root) =>
        Directory.EnumerateFiles(root, "*.sln").Concat(Directory.EnumerateFiles(root, "*.slnx")).Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>The detected task that builds a project: "dotnet: build" when it is the folder's only one, else with its name.</summary>
    private static string BuildLabel(Found found, string? solution, string project) =>
        solution is null && found.Projects.Count == 1 ? "dotnet: build" : $"dotnet: build {ProjectLabel(found.Projects, project)}";

    /// <summary>A project's name, or its path when two projects have the same name.</summary>
    private static string ProjectLabel(List<string> projects, string project)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        return projects.Count(p => Path.GetFileNameWithoutExtension(p) == name) > 1 ? Path.GetFileName(Path.GetDirectoryName(project)) + "/" + name : name;
    }

    /// <summary>Whether a project file is a test project.</summary>
    private static bool IsTest(string project) =>
        project.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
        || project.Contains("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(project, @"<IsTestProject>\s*true\s*</IsTestProject>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>Whether a project file makes a program to run (an Exe, or a web or worker project), not a test project.</summary>
    private static bool Runnable(string project) =>
        !IsTest(project)
        && (Regex.IsMatch(project, @"<OutputType>\s*(Win)?Exe\s*</OutputType>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            || Regex.IsMatch(project, @"Sdk\s*=\s*""Microsoft\.NET\.Sdk\.(Web|Worker|BlazorWebAssembly)""", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));

    /// <summary>npm's scripts, of the folder's package.json and those two levels down: "npm: name", or "npm: name - dir".</summary>
    private static void NpmTasks(string root, Found found, List<JsonObject> tasks)
    {
        foreach (var package in found.Packages)
        {
            if (Json.ParseObject(Read(package))?["scripts"] is not JsonObject scripts)
            {
                continue;
            }
            var dir = Rel(root, Path.GetDirectoryName(package)!);
            var manager = Manager(root, Path.GetDirectoryName(package)!);
            foreach (var (name, script) in scripts)
            {
                // npm's, whichever manager runs it, as VS Code names them.
                var label = dir == "." ? $"npm: {name}" : $"npm: {name} - {dir}";
                var group = name switch
                {
                    "build" or "compile" => "build",
                    "test" => "test",
                    _ => null,
                };
                tasks.Add(Task(label, manager, ["run", name], group, dir, detail: script is JsonValue v && v.TryGetValue<string>(out var text) ? text : script?.ToJsonString()));
            }
        }
    }

    /// <summary>The package manager a package's lock file says: pnpm, yarn or bun, else npm.</summary>
    private static string Manager(string root, string dir)
    {
        foreach (var at in new[] { dir, root })
        {
            if (File.Exists(Path.Combine(at, "pnpm-lock.yaml")))
            {
                return "pnpm";
            }
            if (File.Exists(Path.Combine(at, "yarn.lock")))
            {
                return "yarn";
            }
            if (File.Exists(Path.Combine(at, "bun.lockb")) || File.Exists(Path.Combine(at, "bun.lock")))
            {
                return "bun";
            }
        }
        return "npm";
    }

    private static string? Makefile(string root) =>
        new[] { "GNUmakefile", "makefile", "Makefile" }.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists);

    /// <summary>Whether the folder has pytest's tests: its configuration, a conftest.py, or tests named as pytest finds them.</summary>
    private static bool Pytest(string root)
    {
        if (File.Exists(Path.Combine(root, "pytest.ini")) || File.Exists(Path.Combine(root, "conftest.py"))
            || Read(Path.Combine(root, "pyproject.toml")).Contains("[tool.pytest", StringComparison.Ordinal)
            || Read(Path.Combine(root, "setup.cfg")).Contains("[tool:pytest]", StringComparison.Ordinal)
            || Read(Path.Combine(root, "tox.ini")).Contains("[pytest]", StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            return Directory.EnumerateFiles(root, "test_*.py").Any()
                || new[] { "tests", "test" }.Any(t => Directory.Exists(Path.Combine(root, t)) && Directory.EnumerateFiles(Path.Combine(root, t), "*.py", SearchOption.AllDirectories).Take(1).Any());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The project's Python: its virtual environment's (.venv, venv), else the one VIRTUAL_ENV names, else python3 (python on Windows).</summary>
    public static string Python(string root, Func<string, string?> env)
    {
        var bin = OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python";
        foreach (var venv in new[] { ".venv", "venv" })
        {
            if (File.Exists(Path.Combine(root, venv, bin)))
            {
                return $"${{workspaceFolder}}/{venv}/{bin}";
            }
        }
        if (env("VIRTUAL_ENV") is { Length: > 0 } active && File.Exists(Path.Combine(active, bin)))
        {
            return Path.Combine(active, bin);
        }
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    /// <summary>
    /// The configurations the project's files give: each .NET program (its build first, then the program it built), npm's
    /// start and dev scripts, the Python file open in the editor, Go's package, Cargo's programs, CMake's executables, and
    /// the programs a Makefile built.
    /// </summary>
    public static List<DetectedConfig> Configurations(string root, string configuration, Func<string, string?> env)
    {
        var found = Look(root);
        var release = configuration == "Release";
        var configs = new List<DetectedConfig>();
        var solution = Solution(root);
        foreach (var project in found.Projects)
        {
            var text = Read(project);
            if (!Runnable(text))
            {
                continue;
            }
            var dir = Rel(root, Path.GetDirectoryName(project)!);
            var folder = dir == "." ? "${workspaceFolder}" : $"${{workspaceFolder}}/{dir}";
            var framework = TargetFramework(text) ?? DirectoryFramework(root, Path.GetDirectoryName(project)!) ?? "net10.0";
            var assembly = Property(text, "AssemblyName") ?? Path.GetFileNameWithoutExtension(project);
            configs.Add(new(new JsonObject
            {
                ["name"] = $".NET: {ProjectLabel(found.Projects, project)}",
                ["type"] = "coreclr",
                ["request"] = "launch",
                ["preLaunchTask"] = BuildLabel(found, solution, project),
                ["program"] = $"{folder}/bin/${{config:codeArena.buildConfiguration}}/{framework}/{assembly}.dll",
                ["args"] = new JsonArray(),
                ["cwd"] = folder,
                ["stopAtEntry"] = false,
                ["console"] = "integratedTerminal",
            }, project));
        }
        foreach (var package in found.Packages)
        {
            if (Json.ParseObject(Read(package))?["scripts"] is not JsonObject scripts)
            {
                continue;
            }
            var dir = Rel(root, Path.GetDirectoryName(package)!);
            foreach (var script in new[] { "start", "dev" }.Where(scripts.ContainsKey))
            {
                configs.Add(new(new JsonObject
                {
                    ["name"] = dir == "." ? $"npm: {script}" : $"npm: {script} - {dir}",
                    ["type"] = "node",
                    ["request"] = "launch",
                    ["runtimeExecutable"] = Manager(root, Path.GetDirectoryName(package)!),
                    ["runtimeArgs"] = new JsonArray("run", script),
                    ["cwd"] = dir == "." ? "${workspaceFolder}" : $"${{workspaceFolder}}/{dir}",
                    ["console"] = "integratedTerminal",
                }));
            }
        }
        if (found.Python)
        {
            var python = new JsonObject
            {
                ["name"] = "Python: Current File",
                ["type"] = "debugpy",
                ["request"] = "launch",
                ["program"] = "${file}",
                ["console"] = "integratedTerminal",
                ["python"] = Python(root, env),
            };
            if (release)
            {
                // Release: asserts and __debug__ blocks off.
                python["pythonArgs"] = new JsonArray("-O");
            }
            configs.Add(new(python));
        }
        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            var go = new JsonObject { ["name"] = "Go: Run Package", ["type"] = "go", ["request"] = "launch", ["mode"] = "auto", ["program"] = "${workspaceFolder}" };
            if (release)
            {
                go["buildFlags"] = "-trimpath -ldflags='-s -w'";
            }
            configs.Add(new(go));
        }
        if (File.Exists(Path.Combine(root, "Cargo.toml")))
        {
            var bins = CargoBin().Matches(Read(Path.Combine(root, "Cargo.toml"))).Select(m => m.Groups[1].Value).Distinct().ToList();
            foreach (var bin in bins.Count > 0 ? bins.Cast<string?>() : [null])
            {
                var build = new JsonArray("build");
                if (bin is not null)
                {
                    build.Add($"--bin={bin}");
                }
                if (release)
                {
                    build.Add("--release");
                }
                configs.Add(new(new JsonObject
                {
                    ["name"] = bin is null ? "Cargo: Run" : $"Cargo: Run {bin}",
                    ["type"] = "lldb",
                    ["request"] = "launch",
                    ["cargo"] = new JsonObject { ["args"] = build },
                    ["args"] = new JsonArray(),
                    ["cwd"] = "${workspaceFolder}",
                }));
            }
        }
        if (File.Exists(Path.Combine(root, "CMakeLists.txt")))
        {
            foreach (var target in CmakeExecutable().Matches(Read(Path.Combine(root, "CMakeLists.txt"))).Select(m => m.Groups[1].Value).Distinct())
            {
                configs.Add(new(Native($"CMake: {target}", $"${{workspaceFolder}}/build/${{config:codeArena.buildConfiguration}}/{target}", "cmake: build")));
            }
        }
        if (Makefile(root) is not null)
        {
            foreach (var program in Programs(root))
            {
                configs.Add(new(Native($"Make: {program}", $"${{workspaceFolder}}/{program}", "make: build")));
            }
        }
        return configs;
    }

    /// <summary>A native program's configuration, as VS Code's C/C++ extension writes one (gdb).</summary>
    private static JsonObject Native(string name, string program, string build) => new()
    {
        ["name"] = name,
        ["type"] = "cppdbg",
        ["request"] = "launch",
        ["program"] = program,
        ["args"] = new JsonArray(),
        ["cwd"] = "${workspaceFolder}",
        ["MIMode"] = "gdb",
        ["preLaunchTask"] = build,
    };

    [GeneratedRegex(@"^\[\[bin\]\][^\[]*?^\s*name\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CargoBin();

    [GeneratedRegex(@"add_executable\s*\(\s*([A-Za-z0-9_.+-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CmakeExecutable();

    /// <summary>The programs a Makefile built: executables (ELF, Mach-O or PE files) in the folder, build/ and bin/, as relative paths.</summary>
    private static List<string> Programs(string root)
    {
        var programs = new List<string>();
        foreach (var dir in new[] { root, Path.Combine(root, "build"), Path.Combine(root, "bin") }.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal).Take(500))
                {
                    if (Executable(file))
                    {
                        programs.Add(Rel(root, file));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nothing to offer there.
            }
        }
        return programs;
    }

    /// <summary>Whether a file is a program the system runs: by its first bytes (ELF, Mach-O or PE), and on Unix its execute bit.</summary>
    private static bool Executable(string file)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) == 0)
            {
                return false;
            }
            Span<byte> head = stackalloc byte[4];
            using var stream = File.OpenRead(file);
            if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4)
            {
                return false;
            }
            return head is [0x7F, (byte)'E', (byte)'L', (byte)'F'] or [(byte)'M', (byte)'Z', _, _]
                or [0xCF, 0xFA, 0xED, 0xFE] or [0xCE, 0xFA, 0xED, 0xFE] or [0xCA, 0xFE, 0xBA, 0xBE];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A project's target framework (the first of several).</summary>
    private static string? TargetFramework(string project) =>
        Property(project, "TargetFramework") ?? Property(project, "TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>The target framework a Directory.Build.props above a project sets, up to the folder.</summary>
    private static string? DirectoryFramework(string root, string dir)
    {
        for (var at = dir; at is not null && at.StartsWith(root, StringComparison.Ordinal); at = Path.GetDirectoryName(at))
        {
            if (TargetFramework(Read(Path.Combine(at, "Directory.Build.props"))) is { } framework)
            {
                return framework;
            }
        }
        return null;
    }

    /// <summary>An MSBuild property as a project file writes it, when it is a plain value (not made of other properties).</summary>
    private static string? Property(string project, string name)
    {
        var m = Regex.Match(project, $@"<{name}>\s*([^<$]+?)\s*</{name}>", RegexOptions.None, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>A small file's text; empty when it is missing or cannot be read.</summary>
    private static string Read(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllText(path) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
