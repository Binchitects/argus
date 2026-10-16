using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>When the session compacts itself, and what it keeps: the config, the flags, /compact-at and the IDE's settings.</summary>
public sealed class CompactionTests : IDisposable
{
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    private static bool Summarizing(JsonObject req) => FakeGateway.Summarizing(req);

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
        Assert.Equal("compacts at 80% of the window, and always before a request would not fit, keeping the recent part within 25%", c.Describe());
        Assert.Null(c.Set(60, null));
        Assert.Equal((60, 25), (c.At, c.Target));
        // A target too near the threshold is brought down with it.
        Assert.Null(c.Set(30, null));
        Assert.Equal((30, 20), (c.At, c.Target));
        Assert.Equal("The threshold is from 20% to 95% of the model's window, or off (only when a request would not fit).", c.Set(99, null));
        Assert.Equal("The threshold is from 20% to 95% of the model's window, or off (only when a request would not fit).", c.Set(10, null));
        Assert.Equal("What is kept is from 5% to 60% of the window (10 points under the threshold, 70%).", c.Set(70, 65));
        Assert.Equal((30, 20), (c.At, c.Target));
        Assert.Equal(70, Compaction.Percent("70"));
        Assert.Equal(70, Compaction.Percent("70%"));
        Assert.Equal(70, Compaction.Percent("0.7"));
        Assert.Null(Compaction.Percent("most"));
        Assert.Null(Compaction.Percent("-5"));

        // Off: only when a request would not fit; what is kept may then be up to 85%.
        Assert.Equal(Compaction.Off, Compaction.Percent("off"));
        Assert.Equal(Compaction.Off, Compaction.Percent("0"));
        Assert.Null(c.Set(Compaction.Off, 40));
        Assert.Equal((0, 40), (c.At, c.Target));
        Assert.Equal("compacts only when a request would not fit the window (from your Arena account), keeping the recent part within 40%", c.Describe("from your Arena account"));
        Assert.Equal("only when a request would not fit", c.When());
        Assert.Equal("What is kept is from 5% to 85% of the window.", c.Set(Compaction.Off, 90));
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
        Assert.Contains("It compacts at 80% of the window, and always before a request would not fit (Arena's default), keeping the recent part within 25%.", h.Out);
        Assert.Contains("It compacts at 60% of the window, and always before a request would not fit, keeping the recent part within 20% (kept in config.json).", h.Out);
        Assert.Contains("The threshold is from 20% to 95% of the model's window, or off", h.Err);
        Assert.Matches(@"context [\d.]+k? / 20k \(\d+%\), compacts at 60% \(set in config.json\), and always before a request would not fit, keeping the recent part within 20%\.", h.Out);
        var saved = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!;
        Assert.Equal(60, saved["compactAt"]!.GetValue<int>());
        Assert.Equal(20, saved["compactTarget"]!.GetValue<int>());

        // The next session starts with it; "default" puts both back (the Arena account's threshold) and out of the file.
        Assert.Equal(0, await h.Run("/compact-at\n/compact-at default\n/exit\n", "chat"));
        Assert.Contains("It compacts at 60% of the window, and always before a request would not fit (set in config.json), keeping the recent part within 20%.", h.Out);
        Assert.Contains("It compacts at 80% of the window, and always before a request would not fit (Arena's default), keeping the recent part within 25% (config.json no longer sets when).", h.Out);
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
        Assert.Contains("compacts at 70% (set in config.json), and always before a request would not fit, keeping the recent part within 25%", h.Out);

        Assert.Equal(0, await h.Run("/context\n/exit\n", "chat", "--compact-at", "50%", "--compact-to", "0.15"));
        Assert.Contains("compacts at 50% (set with --compact-at), and always before a request would not fit, keeping the recent part within 15%", h.Out);
        Assert.Equal(70, JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["compactAt"]!.GetValue<int>());

        Assert.Equal(2, await h.Run("", "chat", "--compact-at", "most"));
        Assert.Contains("--compact-at is a share of the model's window: 70, 70% or 0.7, or off.", h.Err);

        using var wrong = new Harness(_gateway, _mcp, c => c["compactAt"] = 99);
        Assert.Equal(1, await wrong.Run("", "-p", "hi"));
        Assert.Contains("Compaction: The threshold is from 20% to 95% of the model's window, or off (only when a request would not fit). (--compact-at, --compact-to, or compactAt and compactTarget in", wrong.Err);
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
        Assert.Contains("compacts at 70% (set in config.json), and always before a request would not fit, keeping the recent part within 30%", h.Out);

        // Not a share at all: said, with where, rather than the default quietly kept.
        using var wrong = new Harness(_gateway, _mcp, c => c["compactAt"] = "most of it");
        Assert.Equal(1, await wrong.Run("", "-p", "hi"));
        Assert.Contains($"\"compactAt\" in {wrong.Paths.ConfigFile} is a share of the model's window: 70, \"70%\" or 0.7, or \"off\".", wrong.Err);
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
        Assert.Equal(("arena", 80), (state["compactAtSource"]!.GetValue<string>(), state["compactAtAccount"]!.GetValue<int>()));
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

        // Off, then the Arena account's again (out of config.json).
        state = JsonNode.Parse(await (await web.PostAsync("/api/settings", new JsonObject { ["compactAt"] = 0 })).Content.ReadAsStringAsync())!;
        Assert.Equal((0, "config"), (state["compactAt"]!.GetValue<int>(), state["compactAtSource"]!.GetValue<string>()));
        Assert.Equal(0, JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["compactAt"]!.GetValue<int>());
        state = JsonNode.Parse(await (await web.PostAsync("/api/settings", new JsonObject { ["compactAt"] = null })).Content.ReadAsStringAsync())!;
        Assert.Equal((80, "arena", 30), (state["compactAt"]!.GetValue<int>(), state["compactAtSource"]!.GetValue<string>(), state["compactTarget"]!.GetValue<int>()));
        Assert.Null(JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["compactAt"]);
    }

    [Fact]
    public async Task Off_compacts_only_before_a_request_would_not_fit()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Summarizing(req) ? Reply.Say("SUMMARY of the older turns.") : Reply.Say("ok");
        await using var rt = await StartAsync(h);
        var heard = new Heard();
        Assert.Null(rt.Compaction.Set(Compaction.Off, null));
        rt.Agent.Events = heard;
        // Far past 80% of the window, but it fits with room for the answer: nothing to do.
        while (rt.Agent.Estimate() < rt.Model.Context * 0.84)
        {
            rt.Agent.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = "question " + new string('q', 500) });
            rt.Agent.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "answer " + new string('a', 500) });
        }
        Assert.True(rt.Agent.Estimate() < rt.Agent.Limit);
        Assert.False(await rt.Agent.MaybeCompactAsync(CancellationToken.None));
        Assert.DoesNotContain(_gateway.Requests, Summarizing);

        // It would not fit: compacted, and said why.
        while (rt.Agent.Estimate() < rt.Agent.Limit)
        {
            rt.Agent.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = "more " + new string('m', 500) });
            rt.Agent.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ok" });
        }
        Assert.True(await rt.Agent.MaybeCompactAsync(CancellationToken.None));
        Assert.Single(_gateway.Requests, Summarizing);
        Assert.Contains(heard.Notices, n => n.StartsWith("Compacting: the next request (~", StringComparison.Ordinal) && n.Contains("would not fit the window (20k)", StringComparison.Ordinal));
        Assert.True(rt.Agent.Estimate() < rt.Agent.Limit);
    }

    [Fact]
    public async Task The_account_s_compact_at_is_the_default_when_config_has_none()
    {
        _mcp.Chats.CompactAt = 60;
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/context\n/compact-at 70\n/compact-at account\n/compact-at off\n/context\n/exit\n", "chat"));
        Assert.Contains("compacts at 60% (from your Arena account), and always before a request would not fit", h.Out);
        Assert.Contains("It compacts at 70% of the window, and always before a request would not fit, keeping the recent part within 25% (kept in config.json).", h.Out);
        Assert.Contains("It compacts at 60% of the window, and always before a request would not fit (from your Arena account), keeping the recent part within 25% (config.json no longer sets when).", h.Out);
        Assert.Contains("It compacts only when a request would not fit the window, keeping the recent part within 25% (kept in config.json).", h.Out);
        Assert.Contains("compacts only when a request would not fit (set in config.json), keeping the recent part within 25%.", h.Out);
        Assert.Equal(0, JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["compactAt"]!.GetValue<int>());

        // config.json says otherwise ("off" written as the manual writes it): it wins, and the IDE says whose it is.
        var config = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!.AsObject();
        config["compactAt"] = "off";
        File.WriteAllText(h.Paths.ConfigFile, config.ToJsonString());
        await using (var web = await WebRun.StartAsync(h))
        {
            var state = await web.GetJsonAsync("/api/state");
            Assert.Equal((0, "config"), (state["compactAt"]!.GetValue<int>(), state["compactAtSource"]!.GetValue<string>()));
            // The account's is known all the same (asked in the background when config.json decides).
            for (var i = 0; i < 50 && state["compactAtAccount"] is null; i++)
            {
                await Task.Delay(100);
                state = await web.GetJsonAsync("/api/state");
            }
            Assert.Equal(60, state["compactAtAccount"]!.GetValue<int>());
        }

        // An Arena that does not say: 80%.
        _mcp.Chats.Preferences = false;
        Assert.Equal(0, await h.Run("/compact-at account\n/context\n/exit\n", "chat"));
        Assert.Matches(@"compacts at 80%, and always before a request would not fit, keeping the recent part within 25%\.", h.Out);
    }

    /// <summary>What the session says to a front end: its notices.</summary>
    private sealed class Heard : IAgentEvents
    {
        public List<string> Notices { get; } = [];

        public void Step()
        {
        }

        public void Reasoning(string text)
        {
        }

        public void Text(string text)
        {
        }

        public void StepDone(TokenUsage? usage)
        {
        }

        public void ToolCall(string id, string name, ToolDef? tool, string arguments)
        {
        }

        public void ToolResult(string id, string name, int index, ToolResult result, TimeSpan took, bool declined)
        {
        }

        public void Compacted(string notice) => Notices.Add(notice);

        public void Notice(string text) => Notices.Add(text);
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
