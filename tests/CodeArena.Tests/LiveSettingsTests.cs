using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// "When configs in argus-arena changes the code-arena requires restart to get new configs": a change made in Arena (the
/// models and their windows, the thinking default, when chats compact, the tools, the skills) reaches a Code Arena that
/// runs, within seconds and without a restart; the IDE's page hears of it at once.
/// </summary>
public sealed class LiveSettingsTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static JsonObject Config(int compactAt, string source, string? thinking, params string[] tools) => new()
    {
        ["model"] = "model-a",
        ["models"] = new JsonArray(new JsonObject { ["name"] = "model-a", ["vision"] = false, ["loaded"] = true }, new JsonObject { ["name"] = "model-b", ["vision"] = false, ["loaded"] = true }),
        ["defaultThinking"] = thinking,
        ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject { ["id"] = t, ["title"] = t })]),
        ["compactAt"] = compactAt,
        ["compactSource"] = source,
    };

    private static async Task Within(Func<bool> condition)
    {
        var deadline = Task.Delay(Deadline);
        while (!condition())
        {
            Assert.True(await Task.WhenAny(deadline, Task.Delay(20)) != deadline, "not within the deadline");
        }
    }

    [Fact]
    public async Task A_change_in_Arena_applies_in_a_running_Code_Arena_within_seconds()
    {
        _mcp.Chats.ChatConfig = Config(80, "company", null, "web");
        using var h = new Harness(_gateway, _mcp);
        var output = TextWriter.Synchronized(new StringWriter());
        var env = new CliEnv
        {
            In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths, SettingsEvery = TimeSpan.FromMilliseconds(100),
        };
        await using var rt = await Runtime.StartAsync(new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
        await Within(() => rt.ArenaChat is not null);
        Assert.Equal((80, "arena"), (rt.Compaction.At, rt.CompactAtSource));
        Assert.Null(rt.Model.DefaultLevel);
        Assert.Equal(32768, rt.Model.Context);
        var asked = _mcp.Calls.Count(c => c.Method == "tools/list");

        // An admin gives the model a larger window and thinking a default; the person chooses when their chats compact;
        // a tool is added for them.
        _gateway.Context = 65536;
        _mcp.Chats.ChatConfig = Config(60, "account", "high", "web", "argus");
        await Within(() => rt.SettingsVersion == 1);
        Assert.Equal((60, "account"), (rt.Compaction.At, rt.CompactAtSource));
        Assert.Equal("high", rt.Model.DefaultLevel);
        Assert.Equal(65536, rt.Model.Context);
        // Arena's tools are listed again.
        await Within(() => _mcp.Calls.Count(c => c.Method == "tools/list") > asked);

        // Nothing changed: nothing is read again.
        await Task.Delay(400);
        Assert.Equal(1, rt.SettingsVersion);

        // A skill made in Arena is there too.
        _mcp.Chats.ArenaSkills = new JsonArray(FakeChats.OwnSkill("deploy-notes", "Write the deploy notes.", "Write them."));
        await Within(() => rt.SettingsVersion == 2);
        Assert.Contains(rt.ReloadExtensions().Skills, s => s.Name == "deploy-notes");
    }

    [Fact]
    public async Task A_compaction_threshold_the_person_set_here_stays_when_Arena_s_changes()
    {
        _mcp.Chats.ChatConfig = Config(80, "company", null);
        using var h = new Harness(_gateway, _mcp);
        var output = TextWriter.Synchronized(new StringWriter());
        var env = new CliEnv
        {
            In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths, SettingsEvery = TimeSpan.FromMilliseconds(100),
        };
        await using var rt = await Runtime.StartAsync(new Options { CompactAt = 50 }, env, new Ui(env.In, output, output, false, false), CancellationToken.None);
        _mcp.Chats.ChatConfig = Config(70, "account", null);
        await Within(() => rt.SettingsVersion == 1);
        Assert.Equal((50, "flag"), (rt.Compaction.At, rt.CompactAtSource));
        Assert.Equal(70, rt.AccountCompactAt?.At);
    }

    [Fact]
    public async Task The_IDE_s_page_hears_of_a_change_at_once_and_reads_the_config_as_it_is_now()
    {
        _mcp.Chats.ChatConfig = Config(80, "company", null);
        using var h = new Harness(_gateway, _mcp);
        h.SettingsEvery = TimeSpan.FromMilliseconds(100);
        await using var web = await WebRun.StartAsync(h);
        Assert.Equal(0, (await web.GetJsonAsync("/api/settings/changed"))["version"]!.GetValue<int>());

        var waiting = web.GetJsonAsync("/api/settings/changed?after=0");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        _mcp.Chats.ChatConfig = Config(80, "company", "high");
        Assert.True(await Task.WhenAny(waiting, Task.Delay(Deadline)) == waiting, "the page did not hear of the change");
        Assert.Equal(1, (await waiting)["version"]!.GetValue<int>());
        Assert.Equal("high", (await web.GetJsonAsync("/api/chat/config"))["defaultThinking"]?.GetValue<string>());
    }
}
