using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// The IDE's formatters against fake tools: bash scripts on a PATH of the test's own, which print what they were given
/// (their arguments and folder) before the text, so what ran, and how, comes back as the formatted text. Unix only.
/// </summary>
public sealed class FormattingTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public async Task The_nearest_prettier_runs_with_node_once_the_folder_is_trusted_and_the_global_one_until_then()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        var bin = Path.Combine(h.Root, "bin");
        // node runs the "script" it is given: here a bash one, which says which Prettier it is.
        Tool(Path.Combine(bin, "node"), "exec /bin/bash \"$@\"");
        Prettier(Path.Combine(h.Work, "node_modules", "prettier"), "3.0.0", "root");
        Prettier(Path.Combine(h.Work, "web", "node_modules", "prettier"), "3.3.3", "web");
        // pnpm's layout: only a link in node_modules/.bin.
        Prettier(Path.Combine(h.Work, "pkg", "node_modules", ".pnpm", "prettier@3.1.0", "node_modules", "prettier"), "3.1.0", "pnpm");
        Directory.CreateDirectory(Path.Combine(h.Work, "pkg", "node_modules", ".bin"));
        File.CreateSymbolicLink(Path.Combine(h.Work, "pkg", "node_modules", ".bin", "prettier"), "../.pnpm/prettier@3.1.0/node_modules/prettier/bin/prettier.cjs");
        var global = Path.Combine(h.Root, "global");
        Prettier(Path.Combine(global, "lib", "node_modules", "prettier"), "2.8.8", "global");
        Directory.CreateDirectory(Path.Combine(global, "bin"));
        File.CreateSymbolicLink(Path.Combine(global, "bin", "prettier"), "../lib/node_modules/prettier/bin/prettier.cjs");
        h.Env["PATH"] = $"{bin}:{Path.Combine(global, "bin")}";
        await using var web = await WebRun.StartAsync(h);

        // Not trusted yet: the folder's own is listed, not run; the global one runs, without the project's config (it can be code).
        var listed = await web.GetJsonAsync("/api/formatters?path=web/src/app.ts");
        Assert.Equal("typescript", listed["language"]!.GetValue<string>());
        Assert.Equal("prettier", listed["default"]!.GetValue<string>());
        Assert.Equal(
            [
                "prettier Prettier 3.3.3 project false trust range web/node_modules/prettier/bin/prettier.cjs",
                $"prettier Prettier 2.8.8 global true - range {global}/lib/node_modules/prettier/bin/prettier.cjs",
            ],
            Summary(listed));
        var formatted = await Format(web, "web/src/app.ts", "let a=1\n");
        Assert.Equal("global --no-config --stdin-filepath=web/src/app.ts\nlet a=1\n", formatted["text"]!.GetValue<string>());
        Assert.Equal(("prettier", "global"), (formatted["formatter"]!.GetValue<string>(), formatted["source"]!.GetValue<string>()));

        // Trusted: the nearest node_modules wins (the root's for a file beside it), and the global one is no longer offered.
        await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true });
        listed = await web.GetJsonAsync("/api/formatters?path=web/src/app.ts");
        Assert.Equal(["prettier Prettier 3.3.3 project true - range web/node_modules/prettier/bin/prettier.cjs"], Summary(listed));
        formatted = await Format(web, "web/src/app.ts", "let a=1\n");
        Assert.Equal(("web --stdin-filepath=web/src/app.ts\nlet a=1\n", "project"), (formatted["text"]!.GetValue<string>(), formatted["source"]!.GetValue<string>()));
        Assert.Equal("root --stdin-filepath=index.ts\n{}\n", (await Format(web, "index.ts", "{}\n"))["text"]!.GetValue<string>());
        Assert.Equal("pnpm --stdin-filepath=pkg/a.ts\n", (await Format(web, "pkg/a.ts", ""))["text"]!.GetValue<string>());
        Assert.Equal("3.1.0", (await web.GetJsonAsync("/api/formatters?path=pkg/a.ts"))["formatters"]![0]!["version"]!.GetValue<string>());

        // A range (UTF-16 offsets, as Prettier takes them), and the prettier.* settings as flags a project's config file wins over.
        var ranged = await Format(web, "web/src/app.ts", "let a=1\n", new JsonObject
        {
            ["range"] = new JsonObject { ["start"] = 4, ["end"] = 7 },
            ["options"] = new JsonObject { ["tabSize"] = 8, ["prettier"] = new JsonObject { ["printWidth"] = 100, ["semi"] = false, ["trailingComma"] = "none", ["plugins"] = "--x", ["arrowParens"] = "--x" } },
        });
        Assert.Equal("web --config-precedence prefer-file --print-width 100 --no-semi --trailing-comma none --stdin-filepath=web/src/app.ts --range-start 4 --range-end 7\nlet a=1\n", ranged["text"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await Refused(web, "web/src/app.ts", "let a=1\n", new JsonObject { ["range"] = new JsonObject { ["start"] = 5, ["end"] = 99 } })).Status);

        // A language with no formatter, and one asked for that is not there.
        Assert.Empty((await web.GetJsonAsync("/api/formatters?path=notes.txt"))["formatters"]!.AsArray());
        var none = await Refused(web, "notes.txt", "hi\n");
        Assert.Equal((HttpStatusCode.NotFound, "no_formatter"), (none.Status, none.Body["status"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await Refused(web, "index.ts", "{}\n", new JsonObject { ["formatter"] = "biome" })).Status);
        // The page can say the language when the name does not.
        Assert.Equal("root --stdin-filepath=Jakefile\n", (await Format(web, "Jakefile", "", new JsonObject { ["language"] = "javascript" }))["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_tool_gets_its_own_arguments_folder_and_range_flags()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        var bin = Path.Combine(h.Root, "bin");
        foreach (var tool in new[] { "ruff", "black", "gofmt", "rustfmt", "clang-format" })
        {
            Tool(Path.Combine(bin, tool), $"echo \"{tool} $* in $PWD\"; cat");
        }
        // dotnet format changes the file in place: this one writes its arguments and the .editorconfig files it sees into it.
        Tool(Path.Combine(bin, "dotnet"), """
            folder="$3"; file="$folder/${5#--include=}"
            { echo "dotnet $1 $2 $4 $5 $6 $7"; echo "$3"; [ "$(pwd -P)" = "$(cd "$folder" && pwd -P)" ] && echo "in the copy"; cat ../global.json; (cd "$folder" && find . -name .editorconfig | sort); cat "$file"; } > "$file.new" && mv "$file.new" "$file"
            """);
        h.Write("lib/pyproject.toml", "[tool.black]\nline-length = 100\n");
        h.Write("crates/a/Cargo.toml", "[package]\nname = \"a\"\nedition = \"2018\"\n");
        h.Write("crates/c/Cargo.toml", "[package]\nname = \"c\"\n");
        h.Write("crates/w/Cargo.toml", "[package]\nname = \"w\"\nedition.workspace = true\n");
        h.Write(".editorconfig", "root = true\n");
        h.Write("src/.editorconfig", "[*.cs]\nindent_size = 2\n");
        h.Write("other/.editorconfig", "[*]\nindent_size = 8\n");
        foreach (var file in new[] { "cmd/main.go", "crates/a/src/lib.rs", "crates/c/src/lib.rs", "crates/w/src/lib.rs", "native/x.cpp" })
        {
            h.Write(file, "");
        }
        h.Env["PATH"] = bin;
        await using var web = await WebRun.StartAsync(h);
        async Task<string> Text(string path, string text, JsonObject? extra = null) => (await Format(web, path, text, extra))["text"]!.GetValue<string>();
        JsonObject Range(int start, int end) => new() { ["range"] = new JsonObject { ["start"] = start, ["end"] = end } };

        // Python: ruff, then black; black first where pyproject.toml configures black and not ruff. Lines and columns from 1.
        var python = await web.GetJsonAsync("/api/formatters?path=src/tool.py");
        Assert.Equal(["ruff Ruff - global true - range " + Path.Combine(bin, "ruff"), "black Black - global true - range " + Path.Combine(bin, "black")], Summary(python));
        Assert.Equal($"ruff format --no-cache --quiet --stdin-filename=src/tool.py in {h.Work}\nx=1\ny=2\n", await Text("src/tool.py", "x=1\ny=2\n"));
        Assert.Equal($"ruff format --no-cache --quiet --stdin-filename=src/tool.py --range 1:3-2:2 in {h.Work}\nx=1\ny=2\n", await Text("src/tool.py", "x=1\ny=2\n", Range(2, 5)));
        var black = Range(2, 5);
        black["formatter"] = "black";
        Assert.Equal($"black --quiet --stdin-filename=src/tool.py --line-ranges 1-2 - in {h.Work}\nx=1\ny=2\n", await Text("src/tool.py", "x=1\ny=2\n", black));
        Assert.Equal("black", (await web.GetJsonAsync("/api/formatters?path=lib/mod.py"))["default"]!.GetValue<string>());

        // gofmt and rustfmt run in the file's folder (where their config is looked for), whole files only.
        Assert.Equal($"gofmt  in {h.Work}/cmd\npackage main\n", await Text("cmd/main.go", "package main\n"));
        var ranged = await Refused(web, "cmd/main.go", "package main\n", Range(0, 4));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "no_range"), (ranged.Status, ranged.Body["status"]!.GetValue<string>()));
        // Until the folder is trusted, rustfmt runs outside it (rustup would take the toolchain its rust-toolchain.toml
        // names, which can be a program in the folder), and is given the folder's rustfmt.toml.
        h.Write("crates/rustfmt.toml", "max_width = 80\n");
        h.Write("crates/a/rust-toolchain.toml", "[toolchain]\npath = \"./evil\"\n");
        var untrusted = (await Text("crates/a/src/lib.rs", "")).Split(" in ");
        Assert.Equal($"rustfmt --emit stdout --edition 2018 --config-path {h.Work}/crates/rustfmt.toml", untrusted[0]);
        Assert.False(untrusted[1].StartsWith(h.Work, StringComparison.Ordinal), untrusted[1]);
        await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true });
        Assert.Equal($"rustfmt --emit stdout --edition 2018 in {h.Work}/crates/a/src\n", await Text("crates/a/src/lib.rs", ""));
        Assert.Equal($"rustfmt --emit stdout --edition 2015 in {h.Work}/crates/c/src\n", await Text("crates/c/src/lib.rs", ""));
        Assert.Equal($"rustfmt --emit stdout --edition 2021 in {h.Work}\n", await Text("build.rs", ""));
        h.Write("Cargo.toml", "[workspace]\nmembers = [\"crates/*\"]\n\n[workspace.package]\nedition = \"2024\"\n");
        Assert.Equal($"rustfmt --emit stdout --edition 2024 in {h.Work}/crates/w/src\n", await Text("crates/w/src/lib.rs", ""));

        // clang-format: the file's name for its style, and the range in UTF-8 bytes.
        Assert.Equal($"clang-format --assume-filename={h.Work}/native/x.cpp --offset=6 --length=6 in {h.Work}/native\n// é\nint b;\n", await Text("native/x.cpp", "// é\nint b;\n", Range(5, 11)));

        // dotnet format: on a copy outside the folder, with the .editorconfig files from the root down to the file's folder.
        var before = Directory.GetFiles(h.Work, "*", SearchOption.AllDirectories).Order().ToList();
        var lines = (await Text("src/App.cs", "class A{}\n")).Split('\n');
        Assert.Equal("dotnet format whitespace --folder --include=src/App.cs --verbosity quiet", lines[0]);
        Assert.False(lines[1].StartsWith(h.Work, StringComparison.Ordinal));
        Assert.False(Directory.Exists(lines[1]));
        // It runs in the copy, never in the folder, whose global.json could have dotnet load an SDK kept there.
        Assert.Equal(["in the copy", "{}", "./.editorconfig", "./src/.editorconfig", "class A{}", ""], lines[2..]);
        Assert.Equal(before, Directory.GetFiles(h.Work, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Refused(web, "src/App.cs", "class A{}\n", Range(0, 1))).Status);
    }

    [Fact]
    public async Task The_real_dotnet_format_formats_the_buffer_by_the_folders_editorconfig_and_leaves_the_file_alone()
    {
        var dotnet = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(d => Path.IsPathRooted(d) && File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")));
        if (dotnet is null)
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        h.Write(".editorconfig", "root = true\n\n[*.cs]\nindent_style = space\nindent_size = 2\n");
        var file = h.Write("src/App.cs", "class Saved { }\n");
        h.Env["PATH"] = string.Join(Path.PathSeparator, dotnet, OperatingSystem.IsWindows() ? "" : "/usr/bin");
        h.Env["HOME"] = Environment.GetEnvironmentVariable("HOME");
        await using var web = await WebRun.StartAsync(h);
        var formatted = await Format(web, "src/App.cs", "class A\n{\n        void M()\n        {\n        }\n}\n");
        Assert.Equal("class A\n{\n  void M()\n  {\n  }\n}\n", formatted["text"]!.GetValue<string>().ReplaceLineEndings("\n"));
        Assert.Equal("class Saved { }\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_failure_says_what_the_tool_said_with_its_line_and_column_and_a_slow_one_is_stopped_with_what_it_started()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var bin = Directory.CreateTempSubdirectory("code-arena-fake-").FullName;
        try
        {
            var pid = Path.Combine(bin, "slow.pid");
            Tool(Path.Combine(bin, "broken"), @"printf '\e[31m[error] x.bad: SyntaxError: Unexpected token (3:5)\e[0m\n' >&2; exit 2");
            Tool(Path.Combine(bin, "quiet"), "echo 'it broke at 99999999999:1' >&2; exit 1");
            Tool(Path.Combine(bin, "slow"), $"sleep 30 & echo $! > '{pid}'; wait");
            using var h = new Harness(_gateway, _mcp, c => c["formatters"] = new JsonObject
            {
                ["broken"] = Formatter([Path.Combine(bin, "broken")], "*.bad"),
                ["quiet"] = Formatter([Path.Combine(bin, "quiet")], "*.quiet"),
                ["slow"] = new JsonObject { ["command"] = new JsonArray(Path.Combine(bin, "slow")), ["languages"] = new JsonArray("*.slow"), ["timeout"] = 1 },
            });
            h.Env["PATH"] = bin;
            await using var web = await WebRun.StartAsync(h);

            var failed = await Refused(web, "x.bad", "a\nb\nc d\n");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.Status);
            Assert.Equal("format_failed", failed.Body["status"]!.GetValue<string>());
            Assert.Equal("broken could not format x.bad: [error] x.bad: SyntaxError: Unexpected token (3:5)", failed.Body["error"]!.GetValue<string>());
            Assert.Equal((3, 5), (failed.Body["line"]!.GetValue<int>(), failed.Body["column"]!.GetValue<int>()));
            var quiet = await Refused(web, "y.quiet", "a\n");
            Assert.Equal("quiet could not format y.quiet: it broke at 99999999999:1", quiet.Body["error"]!.GetValue<string>());
            Assert.Null(quiet.Body["line"]);

            // Stopped at its time limit, with the program it left running.
            var clock = Stopwatch.StartNew();
            var slow = await Refused(web, "z.slow", "a\n");
            Assert.Equal((HttpStatusCode.GatewayTimeout, "timeout"), (slow.Status, slow.Body["status"]!.GetValue<string>()));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"{clock.Elapsed}");
            var child = int.Parse(File.ReadAllText(pid).Trim());
            for (var waited = 0; Alive(child) && waited < 5000; waited += 50)
            {
                await Task.Delay(50);
            }
            Assert.False(Alive(child), "the formatter's child still runs");
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    [Fact]
    public async Task Too_much_text_a_path_outside_the_folder_and_a_folder_are_refused_and_the_key_stays_out_of_the_formatter()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        var bin = Path.Combine(h.Root, "bin");
        Tool(Path.Combine(bin, "gofmt"), """echo "key=[${ARENA_API_KEY-none}]"; cat""");
        h.Env["PATH"] = bin;
        h.Write("src/main.go", "");
        await using var web = await WebRun.StartAsync(h);

        var large = await Refused(web, "src/main.go", new string('x', 5 * 1024 * 1024 + 1));
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "too_large"), (large.Status, large.Body["status"]!.GetValue<string>()));
        foreach (var outside in new[] { "../secret.go", Path.Combine(h.Root, "secret.go") })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Refused(web, outside, "package x\n")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/formatters?path=" + Uri.EscapeDataString(outside))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Refused(web, "src", "package x\n")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/formatters")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/format", new JsonObject { ["path"] = "src/main.go" })).StatusCode);

        // code-arena's own environment may hold the key: the formatter's does not.
        Environment.SetEnvironmentVariable("ARENA_API_KEY", "sk-not-for-formatters");
        try
        {
            Assert.Equal("key=[none]\npackage main\n", (await Format(web, "src/main.go", "package main\n"))["text"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARENA_API_KEY", null);
        }
    }

    [Fact]
    public async Task A_folders_files_cannot_stall_the_search_nor_pass_flags_by_their_names()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        var bin = Path.Combine(h.Root, "bin");
        Tool(Path.Combine(bin, "node"), "exec /bin/bash \"$@\"");
        Tool(Path.Combine(bin, "ruff"), "echo \"ruff $*\"; cat");
        Tool(Path.Combine(bin, "rustfmt"), "echo \"rustfmt $*\"; cat");
        Tool(Path.Combine(bin, "dotnet"), """file="$3/${5#--include=}"; { find "$3" -name .editorconfig | grep -c .; cat "$file"; } > "$file.new" && mv "$file.new" "$file" """);
        var global = Path.Combine(h.Root, "global");
        Prettier(Path.Combine(global, "lib", "node_modules", "prettier"), "3.3.3", "global");
        Directory.CreateDirectory(Path.Combine(global, "bin"));
        File.CreateSymbolicLink(Path.Combine(global, "bin", "prettier"), "../lib/node_modules/prettier/bin/prettier.cjs");
        h.Env["PATH"] = $"{bin}:{Path.Combine(global, "bin")}";
        // What is read to find formatters, made endless (a link to /dev/zero) or never opening (a named pipe).
        Directory.CreateDirectory(Path.Combine(h.Work, "node_modules", "prettier"));
        File.CreateSymbolicLink(Path.Combine(h.Work, "node_modules", "prettier", "package.json"), "/dev/zero");
        File.CreateSymbolicLink(Path.Combine(h.Work, "Cargo.toml"), "/dev/zero");
        File.CreateSymbolicLink(Path.Combine(h.Work, ".editorconfig"), "/dev/zero");
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", [Path.Combine(h.Work, "pyproject.toml")]) { UseShellExecute = false }))
        {
            await mkfifo!.WaitForExitAsync();
        }
        await using var web = await WebRun.StartAsync(h);
        var limit = TimeSpan.FromSeconds(20);

        var ts = await web.GetJsonAsync("/api/formatters?path=a.ts").WaitAsync(limit);
        Assert.Equal(("prettier", "global"), (ts["default"]!.GetValue<string>(), ts["formatters"]![0]!["source"]!.GetValue<string>()));
        Assert.Equal("ruff", (await web.GetJsonAsync("/api/formatters?path=a.py").WaitAsync(limit))["default"]!.GetValue<string>());
        Assert.Equal("rustfmt --emit stdout --edition 2021\n", (await Format(web, "lib.rs", "").WaitAsync(limit))["text"]!.GetValue<string>());
        Assert.Equal("0\nclass A{}\n", (await Format(web, "App.cs", "class A{}\n").WaitAsync(limit))["text"]!.GetValue<string>());

        // A file named like a flag is given as the flag's value, never as a flag (--plugin would load the folder's code).
        Assert.Equal("global --no-config --stdin-filepath=--plugin=evil.mjs.ts\n", (await Format(web, "--plugin=evil.mjs.ts", ""))["text"]!.GetValue<string>());
        Assert.Equal("ruff format --no-cache --quiet --stdin-filename=--config=x.py\n", (await Format(web, "--config=x.py", ""))["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_untrusted_folder_skips_the_formatters_it_holds()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["formatters"] = new JsonObject { ["tidy"] = Formatter(["./tools/tidy", "${relativeFile}"], "*.tidy") });
        var bin = Path.Combine(h.Root, "bin");
        Tool(Path.Combine(bin, "node"), "exec /bin/bash \"$@\"");
        Tool(Path.Combine(bin, "ruff"), "echo \"global ruff\"; cat");
        Tool(Path.Combine(h.Work, ".venv", "bin", "ruff"), "echo \"project ruff\"; cat");
        Directory.CreateDirectory(Path.Combine(h.Work, ".venv", "lib", "python3.12", "site-packages", "ruff-0.6.9.dist-info"));
        Prettier(Path.Combine(h.Work, "node_modules", "prettier"), "3.3.3", "project prettier");
        // A config formatter whose program is the folder's own needs trust too.
        Tool(Path.Combine(h.Work, "tools", "tidy"), "echo \"tidy $*\"; cat");
        h.Env["PATH"] = bin;
        await using var web = await WebRun.StartAsync(h);

        Assert.Equal(
            ["ruff Ruff 0.6.9 project false trust range .venv/bin/ruff", $"ruff Ruff - global true - range {bin}/ruff", "black Black - global false missing range -"],
            Summary(await web.GetJsonAsync("/api/formatters?path=app.py")));
        Assert.Equal("global ruff\nx = 1\n", (await Format(web, "app.py", "x = 1\n"))["text"]!.GetValue<string>());
        var prettier = await Refused(web, "app.ts", "let a\n");
        Assert.Equal((HttpStatusCode.Forbidden, "not_trusted"), (prettier.Status, prettier.Body["status"]!.GetValue<string>()));
        Assert.Contains("node_modules/prettier/bin/prettier.cjs", prettier.Body["error"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Refused(web, "a.tidy", "x\n")).Status);
        Assert.Equal("tidy tidy - config false trust - tools/tidy", Assert.Single(Summary(await web.GetJsonAsync("/api/formatters?path=a.tidy"))));

        // Restricted on purpose is the same; trusted, the folder's own run.
        await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = false });
        Assert.Equal("global ruff\nx = 1\n", (await Format(web, "app.py", "x = 1\n"))["text"]!.GetValue<string>());
        await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true });
        Assert.Equal("project ruff\nx = 1\n", (await Format(web, "app.py", "x = 1\n"))["text"]!.GetValue<string>());
        Assert.Equal("project prettier --stdin-filepath=app.ts\nlet a\n", (await Format(web, "app.ts", "let a\n"))["text"]!.GetValue<string>());
        Assert.Equal("tidy a.tidy\nx\n", (await Format(web, "a.tidy", "x\n"))["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_persons_own_formatters_add_to_the_built_ins_or_take_their_place()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var bin = Directory.CreateTempSubdirectory("code-arena-fake-").FullName;
        try
        {
            Tool(Path.Combine(bin, "stylua"), "echo \"stylua $*\"; cat");
            Tool(Path.Combine(bin, "biome"), "echo \"biome $*\"; cat");
            Tool(Path.Combine(bin, "mine"), "echo \"mine $*\"; cat");
            Tool(Path.Combine(bin, "empty"), "cat > /dev/null");
            using var h = new Harness(_gateway, _mcp, c => c["formatters"] = new JsonObject
            {
                ["stylua"] = Formatter(["stylua", "--indent-width", "${tabSize}", "--stdin-filepath", "${file}", "-"], "lua"),
                ["biome"] = new JsonObject
                {
                    ["command"] = new JsonArray("biome", "format", "--stdin-file-path=${relativeFile}", "${rangeStart}-${rangeEnd}", "${startLine}-${endLine}"),
                    ["languages"] = new JsonArray("typescript"), ["range"] = true,
                },
                // A built-in's name: its place and its languages.
                ["gofmt"] = Formatter([Path.Combine(bin, "mine"), "${workspaceFolder}"]),
                ["empty"] = Formatter(["empty"], "*.empty"),
                ["gone"] = Formatter(["no-such-formatter"], "*.gone"),
            });
            h.Env["PATH"] = bin;
            await using var web = await WebRun.StartAsync(h);

            var lua = await web.GetJsonAsync("/api/formatters?path=game/main.lua");
            Assert.Equal(("lua", "stylua"), (lua["language"]!.GetValue<string>(), lua["default"]!.GetValue<string>()));
            Assert.Equal($"stylua --indent-width 2 --stdin-filepath {h.Work}/game/main.lua -\nx=1\n",
                (await Format(web, "game/main.lua", "x=1\n", new JsonObject { ["options"] = new JsonObject { ["tabSize"] = 2, ["insertSpaces"] = true } }))["text"]!.GetValue<string>());

            // Added before the built-in it stands beside, as the default; ranges with its own variables.
            var ts = await web.GetJsonAsync("/api/formatters?path=src/a.ts");
            Assert.Equal(["biome biome - config true - range " + Path.Combine(bin, "biome"), "prettier Prettier - global false missing range -"], Summary(ts));
            Assert.Equal("biome format --stdin-file-path=src/a.ts 2-5 1-2\nab\ncd\n",
                (await Format(web, "src/a.ts", "ab\ncd\n", new JsonObject { ["range"] = new JsonObject { ["start"] = 2, ["end"] = 5 } }))["text"]!.GetValue<string>());
            Assert.Equal($"mine {h.Work}\npackage main\n", (await Format(web, "main.go", "package main\n"))["text"]!.GetValue<string>());
            Assert.Equal("gofmt gofmt - config true - - " + Path.Combine(bin, "mine"), Assert.Single(Summary(await web.GetJsonAsync("/api/formatters?path=main.go"))));

            // Nothing back is no formatting: the text stays. A program that is not there is said.
            Assert.Equal("keep me\n", (await Format(web, "a.empty", "keep me\n"))["text"]!.GetValue<string>());
            var gone = await Refused(web, "a.gone", "x\n");
            Assert.Equal((HttpStatusCode.NotFound, "no_formatter"), (gone.Status, gone.Body["status"]!.GetValue<string>()));
            Assert.Contains("no-such-formatter", gone.Body["error"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    [Fact]
    public void The_config_says_what_is_wrong_with_a_formatter_and_saves_them_as_read()
    {
        var dir = Directory.CreateTempSubdirectory("code-arena-config-").FullName;
        try
        {
            var file = Path.Combine(dir, "config.json");
            File.WriteAllText(file, """{"formatters": {"stylua": {"command": ["stylua", "-"], "languages": ["lua", "*.luau"], "range": false, "timeout": 5}}, "other": 1}""");
            var config = Config.Load(file);
            var stylua = Assert.Single(config.Formatters);
            Assert.Equal(("stylua", "stylua -", "lua *.luau", false, 5), (stylua.Name, string.Join(' ', stylua.Command), string.Join(' ', stylua.Languages), stylua.Range, stylua.Timeout));
            config.Save(file);
            Assert.Equal("""{"command":["stylua","-"],"languages":["lua","*.luau"],"timeout":5}""", JsonNode.Parse(File.ReadAllText(file))!["formatters"]!["stylua"]!.ToJsonString());

            foreach (var wrong in new[] { """{"formatters": []}""", """{"formatters": {"x": {"command": "x"}}}""", """{"formatters": {"x": {"command": []}}}""", """{"formatters": {"x": {"command": ["x"], "timeout": 0}}}""" })
            {
                File.WriteAllText(file, wrong);
                Assert.Contains("formatters", Assert.Throws<InvalidOperationException>(() => Config.Load(file)).Message);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task With_bubblewrap_a_formatter_cannot_write_in_the_folder_nor_reach_the_network()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/bwrap"))
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        var bin = Path.Combine(h.Root, "bin");
        h.Env["PATH"] = $"{bin}:/usr/bin:/bin";
        if (Sandbox.Choose(new Config(), new Workspace(h.Work), h.Paths, k => h.Env.GetValueOrDefault(k), "probe").Kind != Sandbox.Kinds.Bubblewrap)
        {
            // bubblewrap cannot make a sandbox here (user namespaces are off).
            return;
        }
        Tool(Path.Combine(bin, "gofmt"), """
            touch "$PWD/written" 2>/dev/null && echo "wrote the folder" || echo "folder read-only"
            touch /tmp/scratch 2>/dev/null && echo "tmp writable" || echo "tmp read-only"
            [ "$(grep -c : /proc/net/dev)" -eq 1 ] && echo "no network" || echo "network"
            cat
            """);
        h.Write("main.go", "");
        await using var web = await WebRun.StartAsync(h);
        Assert.Equal("folder read-only\ntmp writable\nno network\npackage main\n", (await Format(web, "main.go", "package main\n"))["text"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(h.Work, "written")));
    }

    [Fact]
    public async Task Text_goes_through_a_tool_while_it_prints_and_its_errors_come_apart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // More than a pipe holds both ways: written while read, or it would stall.
        var text = string.Concat(Enumerable.Repeat("line ü\n", 300_000));
        var run = await Proc.PipeAsync(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "cat; echo oops >&2" } }, text, TimeSpan.FromSeconds(30), text.Length * 2, CancellationToken.None);
        Assert.Equal((0, false, false), (run.ExitCode, run.TimedOut, run.TooLong));
        Assert.Equal(text, run.Output);
        Assert.Equal("oops\n", run.Error);

        // Past what it may print, it is stopped.
        var flood = await Proc.PipeAsync(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "yes" } }, "", TimeSpan.FromSeconds(30), 1000, CancellationToken.None);
        Assert.True(flood.TooLong);
        Assert.Equal(1000, flood.Output.Length);

        // A tool that does not read its input still ends.
        var deaf = await Proc.PipeAsync(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 3" } }, text, TimeSpan.FromSeconds(30), 1000, CancellationToken.None);
        Assert.Equal(3, deaf.ExitCode);

        // Cancelled: thrown, and stopped.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proc.PipeAsync(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 30" } }, "", TimeSpan.FromSeconds(30), 1000, cancel.Token));
    }

    [Fact]
    public async Task The_formatting_calls_need_this_runs_key_and_its_own_page()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("a.ts", "");
        await using var web = await WebRun.StartAsync(h);
        (string Method, string Path, JsonObject? Body)[] calls =
        [
            ("GET", "/api/formatters?path=a.ts", null),
            ("POST", "/api/format", new() { ["path"] = "a.ts", ["text"] = "let a\n" }),
        ];
        using var anonymous = WebRun.Client(web.Port, token: null);
        var wrong = new List<string>();
        foreach (var (method, path, body) in calls)
        {
            async Task Expect(HttpStatusCode status, string what, HttpClient client, Action<HttpRequestMessage>? change = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (body is not null)
                {
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }
                change?.Invoke(request);
                using var response = await client.SendAsync(request);
                if (response.StatusCode != status)
                {
                    wrong.Add($"{method} {path} {what}: {(int)response.StatusCode}, not {(int)status}");
                }
            }
            await Expect(HttpStatusCode.Unauthorized, "without the key", anonymous);
            await Expect(HttpStatusCode.Forbidden, "for another host name", web.Http, r => r.Headers.Host = $"evil.example:{web.Port}");
            await Expect(HttpStatusCode.Forbidden, "from another site", web.Http, r => r.Headers.Add("Origin", "https://evil.example"));
            await Expect(HttpStatusCode.Forbidden, "said by the browser to come from elsewhere", web.Http, r => r.Headers.Add("Sec-Fetch-Site", "same-site"));
            if (body is not null)
            {
                await Expect(HttpStatusCode.UnsupportedMediaType, "as a form", web.Http, r => r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "text/plain"));
            }
        }
        Assert.Empty(wrong);
    }

    /// <summary>A fake tool: a bash script, executable.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Tool(string path, string script)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/bash\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A Prettier package whose script (run by the fake node, which is bash) says which one it is, then gives the text back.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Prettier(string dir, string version, string label)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""{"name": "prettier", "version": "{{version}}", "bin": "./bin/prettier.cjs"}""");
        Tool(Path.Combine(dir, "bin", "prettier.cjs"), $"echo \"{label} $*\"; cat");
    }

    private static JsonObject Formatter(string[] command, params string[] languages)
    {
        var o = new JsonObject { ["command"] = new JsonArray([.. command.Select(c => (JsonNode)c)]) };
        if (languages.Length > 0)
        {
            o["languages"] = new JsonArray([.. languages.Select(l => (JsonNode)l)]);
        }
        return o;
    }

    /// <summary>Each formatter listed in one line: id, name, version, source, available, reason, range and where it is.</summary>
    private static List<string> Summary(JsonNode listed) =>
        [.. listed["formatters"]!.AsArray().Select(f =>
            $"{f!["id"]} {f["name"]} {f["version"]?.ToString() ?? "-"} {f["source"]} {f["available"]} {f["reason"]?.ToString() ?? "-"} {(f["range"]!.GetValue<bool>() ? "range" : "-")} {f["path"]?.ToString() ?? "-"}")];

    private static async Task<JsonNode> Format(WebRun web, string path, string text, JsonObject? more = null)
    {
        var (status, body) = await Send(web, path, text, more);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body.ToJsonString()}");
        return body;
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> Refused(WebRun web, string path, string text, JsonObject? more = null)
    {
        var (status, body) = await Send(web, path, text, more);
        Assert.NotEqual(HttpStatusCode.OK, status);
        return (status, body);
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> Send(WebRun web, string path, string text, JsonObject? more)
    {
        var body = new JsonObject { ["path"] = path, ["text"] = text };
        foreach (var (key, value) in more ?? [])
        {
            body[key] = value?.DeepClone();
        }
        using var response = await web.PostAsync("/api/format", body);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    /// <summary>Whether a process runs (a zombie no one has reaped yet has ended).</summary>
    private static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
