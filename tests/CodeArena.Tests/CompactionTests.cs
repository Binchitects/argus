using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>When the session compacts itself, and what it keeps: the config, the flags, /compact-at and the IDE's settings.</summary>
public sealed class CompactionTests : IDisposable
{
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    private static bool Summarizing(JsonObject req) => FakeGateway.System(req).StartsWith("You summarize", StringComparison.Ordinal);

    private async Task<Runtime> StartAsync(Harness h, Options? o = null)
    {
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        return await Runtime.StartAsync(o ?? new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
    }

    /// <summary>Turns of the person's and answers filling two fifths of the window, beside the prompt and the tools.</summary>
    private static List<JsonObject> Conversation(Runtime rt)
    {
        var turns = new List<JsonObject>();
        var empty = rt.Agent.Estimate();
        for (var i = 0; rt.Agent.Estimate() - empty < rt.Model.Context * 2 / 5; i++)
        {
            var pair = new[]
            {
                new JsonObject { ["role"] = "user", ["content"] = $"question {i} " + new string('q', 1500) },
                new JsonObject { ["role"] = "assistant", ["content"] = $"answer {i} " + new string('a', 1500) },
            };
            rt.Agent.Messages.AddRange(pair);
            turns.AddRange(pair);
        }
        return turns;
    }

    [Fact]
    public void The_threshold_and_what_is_kept_are_shares_of_the_window_with_safe_bounds()
    {
        var c = new Compaction();
        Assert.Equal((80, 25), (c.At, c.Target));
        Assert.Equal("compacts at 80% of the window, keeping the recent part within 25%", c.Describe());
        Assert.Null(c.Set(60, null));
        Assert.Equal((60, 25), (c.At, c.Target));
        // A target too near the threshold is brought down with it.
        Assert.Null(c.Set(30, null));
        Assert.Equal((30, 20), (c.At, c.Target));
        Assert.Equal("The threshold is from 20% to 95% of the model's window.", c.Set(99, null));
        Assert.Equal("The threshold is from 20% to 95% of the model's window.", c.Set(10, null));
        Assert.Equal("What is kept is from 5% to 60% of the window (10 points under the threshold, 70%).", c.Set(70, 65));
        Assert.Equal((30, 20), (c.At, c.Target));
        Assert.Equal(70, Compaction.Percent("70"));
        Assert.Equal(70, Compaction.Percent("70%"));
        Assert.Equal(70, Compaction.Percent("0.7"));
        Assert.Null(Compaction.Percent("most"));
        Assert.Null(Compaction.Percent("-5"));
    }

    [Fact]
    public async Task The_session_compacts_at_the_threshold_chosen_and_keeps_what_the_target_allows()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Summarizing(req) ? Reply.Say("SUMMARY of the older turns.") : Reply.Say("ok");
        await using var rt = await StartAsync(h);
        var turns = Conversation(rt);
        var share = 100.0 * rt.Agent.Estimate() / rt.Model.Context;

        Assert.InRange(share, 45, 75);
        // Above the share in use: nothing to do. The default (80%) too.
        Assert.Null(rt.Compaction.Set((int)share + 10, null));
        Assert.False(await rt.Agent.MaybeCompactAsync(CancellationToken.None));
        Assert.Null(rt.Compaction.Set(Compaction.DefaultAt, Compaction.DefaultTarget));
        Assert.False(await rt.Agent.MaybeCompactAsync(CancellationToken.None));
        Assert.DoesNotContain(_gateway.Requests, Summarizing);

        // Below it: compacted, the recent part kept within the target.
        Assert.Null(rt.Compaction.Set((int)share - 10, 10));
        Assert.True(await rt.Agent.MaybeCompactAsync(CancellationToken.None));
        Assert.Single(_gateway.Requests, Summarizing);
        Assert.StartsWith("[The conversation so far, summarized", rt.Agent.Messages[0]["content"]!.GetValue<string>());
        var keptSmall = rt.Agent.Messages.Count - 2;
        Assert.True(rt.Agent.Estimate() < rt.Model.Context * rt.Compaction.At / 100.0);

        // A larger target keeps more of the recent turns whole.
        rt.Agent.Messages.Clear();
        rt.Agent.Messages.AddRange(turns.Select(t => t.Clone()));
        Assert.Null(rt.Compaction.Set((int)share - 10, 20));
        Assert.True(await rt.Agent.MaybeCompactAsync(CancellationToken.None));
        Assert.True(rt.Agent.Messages.Count - 2 > keptSmall, $"kept {rt.Agent.Messages.Count - 2} with the larger target, {keptSmall} with 10%");
    }

    [Fact]
    public async Task Compact_at_shows_and_sets_it_and_keeps_it_for_the_next_sessions()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/compact-at\n/compact-at 60 20\n/compact-at 99\n/context\n/exit\n", "chat"));
        Assert.Contains("It compacts at 80% of the window, keeping the recent part within 25%.", h.Out);
        Assert.Contains("It compacts at 60% of the window, keeping the recent part within 20% (kept in config.json).", h.Out);
        Assert.Contains("The threshold is from 20% to 95% of the model's window.", h.Err);
        Assert.Matches(@"context [\d.]+k? / 20k \(\d+%\), compacts at 60%, keeping the recent part within 20%\.", h.Out);
        var saved = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!;
        Assert.Equal(60, saved["compactAt"]!.GetValue<int>());
        Assert.Equal(20, saved["compactTarget"]!.GetValue<int>());

        // The next session starts with it; "default" puts both back and out of the file.
        Assert.Equal(0, await h.Run("/compact-at\n/compact-at default\n/exit\n", "chat"));
        Assert.Contains("It compacts at 60% of the window, keeping the recent part within 20%.", h.Out);
        Assert.Contains("It compacts at 80% of the window, keeping the recent part within 25% (kept in config.json).", h.Out);
        saved = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!;
        Assert.Null(saved["compactAt"]);
        Assert.Null(saved["compactTarget"]);
        Assert.Equal(FakeGateway.Key, saved["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_flags_set_it_for_one_run_and_a_wrong_value_in_the_config_says_where_it_is()
    {
        using var h = new Harness(_gateway, _mcp, c => c["compactAt"] = 70);
        Assert.Equal(0, await h.Run("/context\n/exit\n", "chat"));
        Assert.Contains("compacts at 70%, keeping the recent part within 25%", h.Out);

        Assert.Equal(0, await h.Run("/context\n/exit\n", "chat", "--compact-at", "50%", "--compact-to", "0.15"));
        Assert.Contains("compacts at 50%, keeping the recent part within 15%", h.Out);
        Assert.Equal(70, JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["compactAt"]!.GetValue<int>());

        Assert.Equal(2, await h.Run("", "chat", "--compact-at", "most"));
        Assert.Contains("--compact-at is a share of the model's window: 70, 70% or 0.7.", h.Err);

        using var wrong = new Harness(_gateway, _mcp, c => c["compactAt"] = 99);
        Assert.Equal(1, await wrong.Run("", "-p", "hi"));
        Assert.Contains("Compaction: The threshold is from 20% to 95% of the model's window. (--compact-at, --compact-to, or compactAt and compactTarget in", wrong.Err);
    }

    [Fact]
    public async Task The_config_file_takes_a_share_written_as_the_flags_take_it()
    {
        // As the manual writes them: a fraction, and a percent as text.
        using var h = new Harness(_gateway, _mcp, c =>
        {
            c["compactAt"] = 0.7;
            c["compactTarget"] = "30%";
        });
        Assert.Equal(0, await h.Run("/context\n/exit\n", "chat"));
        Assert.Contains("compacts at 70%, keeping the recent part within 30%", h.Out);

        // Not a share at all: said, with where, rather than the default quietly kept.
        using var wrong = new Harness(_gateway, _mcp, c => c["compactAt"] = "most of it");
        Assert.Equal(1, await wrong.Run("", "-p", "hi"));
        Assert.Contains($"\"compactAt\" in {wrong.Paths.ConfigFile} is a share of the model's window: 70, \"70%\" or 0.7.", wrong.Err);
        Assert.Equal("most of it", JsonNode.Parse(File.ReadAllText(wrong.Paths.ConfigFile))!["compactAt"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_turns_summary_says_how_full_the_window_is()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("hello\n/exit\n", "chat"));
        Assert.Matches(@"· context [\d.]+k? / 20k \(\d+%\), compacts at 80%", h.Out);
    }

    [Fact]
    public async Task The_IDE_shows_how_full_the_window_is_and_sets_the_threshold()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        var state = await web.GetJsonAsync("/api/state");
        Assert.Equal(80, state["compactAt"]!.GetValue<int>());
        Assert.Equal(25, state["compactTarget"]!.GetValue<int>());
        Assert.InRange(state["contextUsed"]!.GetValue<long>(), 1, 20_000);
        Assert.Equal(20_000, state["context"]!.GetValue<int>());

        var set = await web.PostAsync("/api/settings", new JsonObject { ["compactAt"] = 70, ["compactTarget"] = 30 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        state = JsonNode.Parse(await set.Content.ReadAsStringAsync())!;
        Assert.Equal(70, state["compactAt"]!.GetValue<int>());
        Assert.Equal(30, state["compactTarget"]!.GetValue<int>());
        Assert.Equal(70, JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["compactAt"]!.GetValue<int>());

        var wrong = await web.PostAsync("/api/settings", new JsonObject { ["compactAt"] = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("The threshold is from 20% to 95%", await wrong.Content.ReadAsStringAsync());
        Assert.Equal(70, (await web.GetJsonAsync("/api/state"))["compactAt"]!.GetValue<int>());
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
