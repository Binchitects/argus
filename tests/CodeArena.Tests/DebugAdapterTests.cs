using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Where debug adapters are found, in which order, and how launch.json's types are put in their words.</summary>
public sealed class DebugAdapterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("code-arena-adapters-").FullName;

    /// <summary>A program that prints this as its --version.</summary>
    private string Program(string path, string says)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, $"#!/bin/sh\ncat <<'EOF'\n{says}\nEOF\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return full;
    }

    private DebugAdapters Adapters(Config config, string path) =>
        new(config, Path.Combine(_dir, "data"), k => k == "PATH" ? path : k == "HOME" ? _dir : null);

    [Fact]
    public async Task An_adapter_is_found_in_config_json_first_then_the_data_folder_then_PATH_and_an_old_gdb_is_not_taken()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var onPath = Program("bin/gdb", "GNU gdb (Ubuntu 15.0.50.20240403-0ubuntu1) 15.0.50.20240403-git");
        var bin = Path.GetDirectoryName(onPath)!;
        var none = new Config();

        var (found, _) = await Adapters(none, bin).FindAsync("gdb", default);
        Assert.Equal(onPath, found!.Path);
        Assert.Equal("15.0.50.20240403", found.Version);
        Assert.Equal(["-q", "-i", "dap"], found.Args);
        // A folder in PATH given relatively (".", "bin") would be the project's own: not looked in.
        Assert.Null((await Adapters(none, Path.GetRelativePath(Environment.CurrentDirectory, bin)).FindAsync("gdb", default)).Found);

        // One unpacked in the data folder comes before PATH.
        var own = Program("data/adapters/gdb/current/bin/gdb", "GNU gdb (GDB) 17.1");
        Assert.Equal(own, (await Adapters(none, bin).FindAsync("gdb", default)).Found!.Path);

        // config.json's comes first of all, its arguments as given.
        var named = Program("elsewhere/my-gdb", "GNU gdb (GDB) 16.2");
        var config = Configured(new JsonObject { ["gdb"] = new JsonObject { ["command"] = new JsonArray(named, "--interpreter=dap") } });
        var (configured, _) = await Adapters(config, bin).FindAsync("gdb", default);
        Assert.Equal(named, configured!.Path);
        Assert.Equal("16.2", configured.Version);
        Assert.Equal(["--interpreter=dap"], configured.Args);

        // A port: one already running.
        var port = Configured(new JsonObject { ["gdb"] = new JsonObject { ["port"] = 4711 } });
        Assert.Equal(4711, (await Adapters(port, bin).FindAsync("gdb", default)).Found!.Port);

        // gdb before 14 has no debug adapter: said, not taken.
        File.Delete(own);
        var old = Program("old/gdb", "GNU gdb (GDB) Red Hat Enterprise Linux 10.2-11.el9");
        var (tooOld, reason) = await Adapters(none, Path.GetDirectoryName(old)!).FindAsync("gdb", default);
        Assert.Null(tooOld);
        Assert.Contains("gdb 10.2", reason);
        Assert.Contains("14 or later", reason);

        // A broken entry in config.json is said as such.
        var broken = Configured(new JsonObject { ["gdb"] = new JsonObject { ["command"] = "gdb -i dap" } });
        Assert.Contains("\"debugAdapters\".\"gdb\"", (await Adapters(broken, bin).FindAsync("gdb", default)).Reason);
        var gone = Configured(new JsonObject { ["gdb"] = new JsonObject { ["command"] = new JsonArray(Path.Combine(_dir, "no-such-gdb")) } });
        Assert.Contains("is not there", (await Adapters(gone, bin).FindAsync("gdb", default)).Reason);
        // A relative path would be found from wherever Code Arena was started: a full path or a name on PATH.
        var relative = Configured(new JsonObject { ["gdb"] = new JsonObject { ["command"] = new JsonArray(Path.GetRelativePath(Environment.CurrentDirectory, named)) } });
        Assert.Contains("full path", (await Adapters(relative, bin).FindAsync("gdb", default)).Reason);
        var byName = Configured(new JsonObject { ["gdb"] = new JsonObject { ["command"] = new JsonArray("gdb", "-i", "dap") } });
        Assert.Equal(onPath, (await Adapters(byName, bin).FindAsync("gdb", default)).Found!.Path);
    }

    [Fact]
    public async Task Each_adapter_is_listed_with_what_it_serves_and_how_to_get_it_when_missing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var dlv = Program("go/bin/dlv", "Delve Debugger\nVersion: 1.22.1\nBuild: $Id: abc $");
        var listed = await Adapters(new Config(), "/nonexistent").ListAsync(default);
        var go = listed.Single(a => a!["id"]!.GetValue<string>() == "dlv")!;
        Assert.True(go["found"]!.GetValue<bool>());
        Assert.Equal(dlv, go["path"]!.GetValue<string>());
        Assert.Equal("1.22.1", go["version"]!.GetValue<string>());
        Assert.Null(go["install"]);
        var debugpy = listed.Single(a => a!["id"]!.GetValue<string>() == "debugpy")!;
        Assert.False(debugpy["found"]!.GetValue<bool>());
        Assert.Contains("pip install debugpy", debugpy["install"]!.GetValue<string>());
        Assert.Equal(["python", "debugpy"], debugpy["types"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public void Config_json_keeps_its_debug_adapters_as_written()
    {
        var file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, """{"debugAdapters": {"gdb": {"command": ["/opt/gdb/bin/gdb", "-i", "dap"]}, "dlv": {"port": 4711}, "odd": {"command": "x"}}}""");
        var config = CodeArena.Config.Load(file);
        Assert.Equal(["/opt/gdb/bin/gdb", "-i", "dap"], config.DebugAdapters["gdb"].Command);
        Assert.Equal(4711, config.DebugAdapters["dlv"].Port);
        Assert.NotNull(config.DebugAdapters["odd"].Problem);
        config.Save(file);
        var saved = JsonNode.Parse(File.ReadAllText(file))!["debugAdapters"]!;
        Assert.Equal("""{"gdb":{"command":["/opt/gdb/bin/gdb","-i","dap"]},"dlv":{"port":4711},"odd":{"command":"x"}}""", saved.ToJsonString());
    }

    [Fact]
    public void A_cppdbg_configuration_becomes_gdbs_or_lldb_daps_own()
    {
        var cppdbg = new JsonObject
        {
            ["name"] = "app", ["type"] = "cppdbg", ["request"] = "launch", ["program"] = "/w/app", ["args"] = new JsonArray("--fast"),
            ["stopAtEntry"] = true, ["MIMode"] = "gdb", ["preLaunchTask"] = "build", ["externalConsole"] = false,
            ["environment"] = new JsonArray(new JsonObject { ["name"] = "MODE", ["value"] = "test" }, new JsonObject { ["name"] = "ARENA_API_KEY", ["value"] = "sk-stolen" }),
            ["setupCommands"] = new JsonArray(new JsonObject { ["text"] = "-enable-pretty-printing" }),
            ["miDebuggerPath"] = "/w/tools/gdb",
        };
        var gdb = DebugAdapters.Translate(cppdbg, "/w", trusted: false);
        Assert.Equal("gdb", gdb.AdapterId);
        Assert.Equal("gdb", gdb.Type);
        Assert.Equal("""{"name":"app","type":"gdb","request":"launch","program":"/w/app","args":["--fast"],"cwd":"/w","env":{"MODE":"test"},"stopAtBeginningOfMainSubprogram":true}""", gdb.Configuration.ToJsonString());
        Assert.Contains(gdb.Notes, n => n.Contains("setupCommands", StringComparison.Ordinal));
        // A gdb the configuration names runs only in a trusted folder.
        Assert.Null(gdb.Program);
        Assert.Contains(gdb.Notes, n => n.Contains("trusted folder", StringComparison.Ordinal));
        Assert.Equal("/w/tools/gdb", DebugAdapters.Translate(cppdbg, "/w", trusted: true).Program);

        cppdbg["MIMode"] = "lldb";
        var lldb = DebugAdapters.Translate(cppdbg, "/w", trusted: true);
        Assert.Equal("lldb-dap", lldb.AdapterId);
        Assert.Equal("""{"name":"app","type":"lldb-dap","request":"launch","program":"/w/app","args":["--fast"],"cwd":"/w","env":["MODE=test"],"stopOnEntry":true,"initCommands":["-enable-pretty-printing"]}""", lldb.Configuration.ToJsonString());
        Assert.Null(lldb.Program);

        // Attaching: the process picked is gdb's pid.
        var attach = DebugAdapters.Translate(new JsonObject { ["type"] = "cppdbg", ["request"] = "attach", ["program"] = "/w/app", ["processId"] = "4242", ["MIMode"] = "gdb" }, "/w", false);
        Assert.Equal(4242, attach.Configuration["pid"]!.GetValue<long>());
        Assert.Null(attach.Configuration["cwd"]);

        // A gdbserver: cppdbg launches to reach it, gdb's adapter attaches (a launch would run the program here).
        var remote = DebugAdapters.Translate(new JsonObject { ["type"] = "cppdbg", ["request"] = "launch", ["program"] = "/w/app", ["MIMode"] = "gdb", ["miDebuggerServerAddress"] = "board:2345" }, "/w", false);
        Assert.Equal("attach", remote.Configuration["request"]!.GetValue<string>());
        Assert.Equal("board:2345", remote.Configuration["target"]!.GetValue<string>());
        Assert.Contains(remote.Notes, n => n.Contains("gdbserver", StringComparison.Ordinal));
    }

    [Fact]
    public void The_other_types_go_to_their_adapters_and_those_none_serves_say_why()
    {
        JsonObject Of(string type, JsonObject? more = null)
        {
            var c = new JsonObject { ["type"] = type, ["request"] = "launch", ["program"] = "/w/main" };
            foreach (var (k, v) in more ?? [])
            {
                c[k] = v?.DeepClone();
            }
            return c;
        }
        var codelldb = DebugAdapters.Translate(Of("lldb", new() { ["env"] = new JsonObject { ["A"] = "1" }, ["sourceMap"] = new JsonObject { ["/build"] = "/w" }, ["terminal"] = "integrated" }), "/w", false);
        Assert.Equal(("lldb-dap", "lldb-dap"), (codelldb.AdapterId, codelldb.Type));
        Assert.Equal("""["A=1"]""", codelldb.Configuration["env"]!.ToJsonString());
        Assert.Equal("""[["/build","/w"]]""", codelldb.Configuration["sourceMap"]!.ToJsonString());
        Assert.Null(codelldb.Configuration["terminal"]);
        var cargo = Assert.Throws<IdeError>(() => DebugAdapters.Translate(new JsonObject { ["type"] = "lldb", ["request"] = "launch", ["cargo"] = new JsonObject { ["args"] = new JsonArray("build") } }, "/w", false));
        Assert.Equal("bad_config", cargo.Code);

        Assert.Equal(("debugpy", "debugpy"), Pair(DebugAdapters.Translate(Of("python"), "/w", false)));
        Assert.Equal(("js-debug", "pwa-node"), Pair(DebugAdapters.Translate(Of("node"), "/w", false)));
        Assert.Equal(("dlv", "go"), Pair(DebugAdapters.Translate(Of("go"), "/w", false)));
        var coreclr = DebugAdapters.Translate(Of("coreclr", new() { ["console"] = "integratedTerminal", ["justMyCode"] = false, ["env"] = new JsonObject { ["ARENA_API_KEY"] = "x", ["B"] = "2" } }), "/w", false);
        Assert.Equal(("netcoredbg", "coreclr"), Pair(coreclr));
        Assert.Null(coreclr.Configuration["console"]);
        Assert.False(coreclr.Configuration["justMyCode"]!.GetValue<bool>());
        Assert.Equal("""{"B":"2"}""", coreclr.Configuration["env"]!.ToJsonString());
        var nativeDebug = DebugAdapters.Translate(new JsonObject { ["type"] = "gdb", ["request"] = "launch", ["target"] = "./app", ["arguments"] = "-v  -n 2" }, "/w", false);
        Assert.Equal("./app", nativeDebug.Configuration["program"]!.GetValue<string>());
        Assert.Equal("""["-v","-n","2"]""", nativeDebug.Configuration["args"]!.ToJsonString());

        foreach (var (type, says) in new[] { ("cppvsdbg", "Visual Studio"), ("chrome", "browser"), ("java", "Java"), ("ruby", "No debug adapter here serves \"ruby\"") })
        {
            var refused = Assert.Throws<IdeError>(() => DebugAdapters.Translate(Of(type), "/w", false));
            Assert.Equal((400, "unsupported"), (refused.Status, refused.Code));
            Assert.Contains(says, refused.Message);
        }
        Assert.Equal("bad_config", Assert.Throws<IdeError>(() => DebugAdapters.Translate(new JsonObject { ["type"] = "gdb" }, "/w", false)).Code);
        Assert.Equal("bad_config", Assert.Throws<IdeError>(() => DebugAdapters.Translate(new JsonObject { ["request"] = "launch" }, "/w", false)).Code);
    }

    private static (string, string) Pair(DebugLaunch launch) => (launch.AdapterId, launch.Type);

    private static Config Configured(JsonObject adapters)
    {
        var config = new Config();
        foreach (var (id, node) in adapters)
        {
            config.DebugAdapters[id] = DebugAdapterConfig.From(node);
        }
        return config;
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
