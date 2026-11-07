using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena.Tests;

/// <summary>
/// "I want all features customizable in UI": every CODE_ARENA_* the code reads is a setting too, kept in config.json and
/// shown in the IDE's Settings, the environment still winning; or it says why it is none.
/// </summary>
public sealed partial class EnvSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("env-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [GeneratedRegex(@"""(CODE_ARENA_[A-Z_]+)""")]
    private static partial Regex Variable();

    [Fact]
    public void Every_CODE_ARENA_variable_the_code_reads_is_a_setting_or_says_why_not()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CodeArena");
        var read = Directory.GetFiles(root, "*.cs").Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Variable().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("CODE_ARENA_BROWSER", read);
        Assert.Contains("CODE_ARENA_HOME", read);
        var missing = read.Where(v => EnvSettings.Find(v) is null && !EnvSettings.NotSettings.ContainsKey(v)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "CODE_ARENA_* read with no setting (EnvSettings.All) and no reason (EnvSettings.NotSettings):\n" + string.Join('\n', missing));
        // Each one once, with its own key and setting, none of them a key config.json has for something else.
        Assert.Equal(EnvSettings.All.Count, EnvSettings.All.Select(o => o.Key).Distinct().Count());
        Assert.Equal(EnvSettings.All.Count, EnvSettings.All.Select(o => o.Setting).Distinct().Count());
        var file = Path.Combine(_dir, "config.json");
        new Config { Url = "https://arena.example.com", Model = "m", Shell = "sh" }.Save(file);
        var own = Json.ParseObject(File.ReadAllText(file))!.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        Assert.All(EnvSettings.All, o => Assert.DoesNotContain(o.Key, own));
        Assert.All(EnvSettings.All, o => Assert.True(o.Setting.StartsWith("codeArena.", StringComparison.Ordinal) && o.What.Length > 20));
        Assert.All(EnvSettings.NotSettings, n => Assert.DoesNotContain(n.Key, EnvSettings.All.Select(o => o.Variable)));
    }

    [Fact]
    public void A_value_in_config_json_is_read_when_the_environment_has_none_and_the_environment_wins()
    {
        var paths = new AppPaths(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"));
        var vbox = Path.Combine(_dir, "VBoxManage");
        File.WriteAllText(vbox, "#!/bin/sh\n");
        var env = new Dictionary<string, string?> { ["PATH"] = "" };
        var options = EnvSettings.Over(k => env.GetValueOrDefault(k), paths);
        Assert.Null(options("CODE_ARENA_VBOXMANAGE"));

        EnvSettings.Save(EnvSettings.Find("CODE_ARENA_VBOXMANAGE")!, vbox, paths);
        // Read at its use: no restart.
        Assert.Equal(vbox, options("CODE_ARENA_VBOXMANAGE"));
        Assert.Equal(vbox, Hypervisors.Find(options).First().Program);
        // The file's other keys stay.
        var config = Config.Load(paths.ConfigFile);
        config.Url = "https://arena.example.com";
        config.Save(paths.ConfigFile);
        Assert.Equal(vbox, options("CODE_ARENA_VBOXMANAGE"));

        env["CODE_ARENA_VBOXMANAGE"] = "/elsewhere/VBoxManage";
        Assert.Equal("/elsewhere/VBoxManage", options("CODE_ARENA_VBOXMANAGE"));
        // Other names pass through as they are.
        env["PATH"] = "/usr/bin";
        Assert.Equal("/usr/bin", options("PATH"));

        EnvSettings.Save(EnvSettings.Find("CODE_ARENA_VBOXMANAGE")!, null, paths);
        Assert.Null(Config.Load(paths.ConfigFile).Option("vboxManage"));
        Assert.Equal("https://arena.example.com", Config.Load(paths.ConfigFile).Url);
    }

    [Fact]
    public void The_home_chosen_is_kept_at_the_usual_place_and_moves_the_config_and_data_there()
    {
        var env = new Dictionary<string, string?> { ["HOME"] = _dir, ["XDG_CONFIG_HOME"] = Path.Combine(_dir, ".config"), ["XDG_DATA_HOME"] = Path.Combine(_dir, ".local", "share"),
            ["APPDATA"] = Path.Combine(_dir, "roaming"), ["LOCALAPPDATA"] = Path.Combine(_dir, "local") };
        var usual = AppPaths.From(k => env.GetValueOrDefault(k));
        Assert.Null(usual.UsualConfigFile);
        var portable = Path.Combine(_dir, "portable");
        EnvSettings.Save(EnvSettings.Find("CODE_ARENA_HOME")!, portable, usual);
        Assert.Equal(portable, EnvSettings.Saved(usual.ConfigFile, "home"));

        var moved = AppPaths.From(k => env.GetValueOrDefault(k));
        Assert.Equal(Path.Combine(portable, "config", "config.json"), moved.ConfigFile);
        Assert.Equal(Path.Combine(portable, "data", "sessions"), moved.SessionsDir);
        Assert.Equal(usual.ConfigFile, moved.UsualConfigFile);
        // Changed again from there, it is the usual file that changes.
        Assert.Equal(usual.ConfigFile, EnvSettings.FileOf(EnvSettings.Find("CODE_ARENA_HOME")!, moved));

        // The environment's wins.
        env["CODE_ARENA_HOME"] = Path.Combine(_dir, "other");
        Assert.Equal(Path.Combine(_dir, "other", "config", "config.json"), AppPaths.From(k => env.GetValueOrDefault(k)).ConfigFile);
        // A relative one is not taken.
        Assert.NotNull(EnvSettings.Problem(EnvSettings.Find("CODE_ARENA_HOME")!, "portable"));
        Assert.NotNull(EnvSettings.Problem(EnvSettings.Find("CODE_ARENA_BROWSER")!, Path.Combine(_dir, "no-browser")));
    }

    [Fact]
    public async Task The_IDE_lists_them_with_the_environments_value_and_keeps_a_change_in_config_json()
    {
        using var gateway = new FakeGateway();
        using var mcp = new FakeMcp();
        using var h = new Harness(gateway, mcp);
        var browser = Path.Combine(h.Root, "chrome");
        File.WriteAllText(browser, "");
        h.Env["CODE_ARENA_VIRSH"] = "/usr/bin/virsh";
        await using var web = await WebRun.StartAsync(h);

        var listed = (await web.GetJsonAsync("/api/options"))["options"]!.AsArray();
        Assert.Equal(EnvSettings.All.Select(o => o.Setting), listed.Select(o => o!["setting"]!.GetValue<string>()));
        Assert.Equal("/usr/bin/virsh", listed.Single(o => o!["setting"]!.GetValue<string>() == "codeArena.vm.virsh")!["environment"]!.GetValue<string>());

        var saved = await web.PostAsync("/api/options", new JsonObject { ["setting"] = "codeArena.browser", ["value"] = browser });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(browser, Config.Load(h.Paths.ConfigFile).Option("browser"));
        Assert.Equal(browser, web.Env.Options("CODE_ARENA_BROWSER"));

        var wrong = await web.PostAsync("/api/options", new JsonObject { ["setting"] = "codeArena.browser", ["value"] = Path.Combine(h.Root, "missing") });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("There is no file", await wrong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/options", new JsonObject { ["setting"] = "codeArena.nothing", ["value"] = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/options", new JsonObject { ["setting"] = "codeArena.browser", ["value"] = null })).StatusCode);
        Assert.Null(Config.Load(h.Paths.ConfigFile).Option("browser"));
    }
}
