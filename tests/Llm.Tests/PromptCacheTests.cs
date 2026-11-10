using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Models;

using Branding;
namespace Llm.Tests;

/// <summary>What keeps a prompt's start unchanged from one request to the next, so the engine reads it from its cache.</summary>
public sealed class PromptCacheTests
{
    [Fact]
    public void Every_model_reuses_cached_chunks_unless_its_extra_lines_say_otherwise()
    {
        Assert.Contains("cache-reuse = 256\n", ModelCatalog.Preset(new LocalModel { Name = "a", File = "a/A.gguf" }, "/library"), StringComparison.Ordinal);
        var own = ModelCatalog.Preset(new LocalModel { Name = "a", File = "a/A.gguf", ExtraPreset = "cache-reuse = 0" }, "/library");
        Assert.Contains("cache-reuse = 0\n", own, StringComparison.Ordinal);
        Assert.DoesNotContain("cache-reuse = 256", own, StringComparison.Ordinal);
    }

    [Fact]
    public void Chats_that_lose_their_slot_wait_in_the_RAM_the_setting_gives_unless_a_models_extra_lines_say_otherwise()
    {
        var model = new LocalModel { Name = "a", File = "a/A.gguf", Parallel = 4 };
        Assert.Contains("cache-ram = 8192\n", ModelCatalog.Preset(model, "/library", sessionCacheGb: 8), StringComparison.Ordinal);
        Assert.Contains("cache-ram = 0\n", ModelCatalog.Preset(model, "/library", sessionCacheGb: 0), StringComparison.Ordinal);
        Assert.DoesNotContain("cache-ram", ModelCatalog.Preset(model, "/library"), StringComparison.Ordinal);
        var own = new LocalModel { Name = "a", File = "a/A.gguf", Parallel = 4, ExtraPreset = "cache-ram = 2048" };
        var preset = ModelCatalog.Preset(own, "/library", sessionCacheGb: 8);
        Assert.Contains("cache-ram = 2048\n", preset, StringComparison.Ordinal);
        Assert.DoesNotContain("cache-ram = 8192", preset, StringComparison.Ordinal);
        Assert.Equal(8L << 30, TokenCache.Of(model, null, pooled: false, sessionCacheGb: 8).SessionBytes);
        Assert.Equal(2048L << 20, TokenCache.Of(own, null, pooled: false, sessionCacheGb: 8).SessionBytes);
    }

    [Fact]
    public void The_last_round_keeps_the_tools_but_switches_calling_them_off_and_says_so()
    {
        var tools = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "web_search" } });
        JsonObject Request() => new()
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Weather in Paris?" },
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c1", ["content"] = "18 C" }),
        };

        var middle = Request();
        ChatService.Tools(middle, tools, last: false);
        Assert.Equal("web_search", middle["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.False(middle.ContainsKey("tool_choice"));
        Assert.Equal("18 C", middle["messages"]![1]!["content"]!.GetValue<string>());

        var last = Request();
        ChatService.Tools(last, tools, last: true);
        Assert.Equal(tools.ToJsonString(), last["tools"]!.ToJsonString());
        Assert.Equal("none", last["tool_choice"]!.GetValue<string>());
        Assert.Equal("18 C\n\n" + ChatService.LastRoundNote, last["messages"]![1]!["content"]!.GetValue<string>());
        // Only the request is changed: the tools given stay as they were.
        Assert.Single(tools);
    }

    [Fact]
    public void Old_turns_are_left_out_in_steps_so_the_start_stays_put_for_the_next_turns()
    {
        static (JsonObject, long, ChatMessage) Turn(string role) => (new JsonObject { ["role"] = role }, 10, new ChatMessage { Role = role });
        List<(JsonObject Turn, long Weight, ChatMessage Source)> Chat(int exchanges) =>
            [.. Enumerable.Range(0, exchanges).SelectMany(_ => new[] { Turn("user"), Turn("assistant"), Turn("tool"), Turn("assistant") })];

        // Under the room: nothing goes.
        Assert.Equal(0, ChatService.TrimOldest(Chat(50), 2000));

        // Over a room of 2,000: a quarter of it (500) goes, from a person's turn on, and stays
        // the same while the chat grows by up to another quarter.
        var dropped = new List<int>();
        foreach (var exchanges in new[] { 51, 55, 62, 63 })
        {
            var turns = Chat(exchanges);
            dropped.Add(ChatService.TrimOldest(turns, 2000));
            Assert.Equal("user", turns[0].Turn["role"]!.GetValue<string>());
            Assert.True(turns.Sum(t => t.Weight) <= 2000);
        }
        Assert.Equal(52, dropped[0]);
        Assert.Equal(dropped[0], dropped[1]);
        Assert.Equal(dropped[0], dropped[2]);
        // Past the next quarter: the next step.
        Assert.Equal(100, dropped[3]);
    }

    private sealed class FakeTool(string id, string title, string description) : IChatTool
    {
        public string Id => id;
        public string Title => title;
        public string Description => description;
        public string Icon => "plug";
        public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (JsonArray Tools, Dictionary<string, (ToolChoice, IToolRun)> Runs) Kit(params (string Tool, string Function)[] functions)
    {
        var tools = new JsonArray();
        var runs = new Dictionary<string, (ToolChoice, IToolRun)>();
        foreach (var group in functions.GroupBy(f => f.Tool))
        {
            var tool = new FakeTool(group.Key, group.Key.ToUpperInvariant(), $"The {group.Key} tool.");
            foreach (var (_, name) in group)
            {
                tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["description"] = new string('d', 400) } });
                runs[name] = (new ToolChoice(tool, new Llm.Core.Chat.ToolSetting { ToolId = group.Key }, null), null!);
            }
        }
        return (tools, runs);
    }

    [Fact]
    public void Tools_past_the_budget_are_listed_and_loaded_when_asked_and_stay_loaded()
    {
        var (tools, runs) = Kit(("code-intel", "find_symbol"), ("code-intel", "read_file"), ("python", "run_python"));
        List<(string, string)> notes = [("code-intel", $"{Brand.CodeIntel} notes."), ("python", "Python notes."), ("research", "Research notes.")];

        // Under the budget: as before, every tool whole and every note.
        var whole = new OnDemandTools(tools, runs, notes, [], 100_000);
        Assert.False(whole.Active);
        Assert.Equal(3, whole.Request().Count);
        Assert.Equal($"{Brand.CodeIntel} notes.\n\nPython notes.\n\nResearch notes.", whole.Notes());

        // Past it: only load_tools, a line per tool, and the notes of what has no tools.
        var demand = new OnDemandTools(tools, runs, notes, [], 500);
        Assert.True(demand.Active);
        Assert.Equal([OnDemandTools.Function], demand.Request().Select(f => f!["function"]!["name"]!.GetValue<string>()));
        var listed = demand.Notes();
        Assert.StartsWith("Research notes.\n\nTools to load:", listed, StringComparison.Ordinal);
        Assert.Contains("- CODE-INTEL [code-intel]: The code-intel tool. Functions: find_symbol, read_file.", listed, StringComparison.Ordinal);
        Assert.Contains("- PYTHON [python]: The python tool. Functions: run_python.", listed, StringComparison.Ordinal);

        // A tool by its name loads all its functions; its notes join, its line goes.
        var (added, unknown) = demand.Load(["code-intel", "nothing"]);
        Assert.Equal(["find_symbol", "read_file"], added);
        Assert.Equal(["nothing"], unknown);
        Assert.Equal(["find_symbol", "read_file", OnDemandTools.Function], demand.Request().Select(f => f!["function"]!["name"]!.GetValue<string>()));
        Assert.DoesNotContain("[code-intel]", demand.Notes(), StringComparison.Ordinal);
        Assert.StartsWith($"{Brand.CodeIntel} notes.\n\nResearch notes.\n\nTools to load:", demand.Notes(), StringComparison.Ordinal);

        // The next turn starts with what the chat loaded; with everything loaded, load_tools goes too.
        var next = new OnDemandTools(tools, runs, notes, demand.Loaded, 500);
        Assert.Equal(demand.Request().ToJsonString(), next.Request().ToJsonString());
        next.Load(["run_python"]);
        Assert.Equal(["find_symbol", "read_file", "run_python"], next.Request().Select(f => f!["function"]!["name"]!.GetValue<string>()));
        Assert.DoesNotContain("Tools to load", next.Notes(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_tool_result_is_read_from_its_start_and_kept_whole_as_a_file()
    {
        var text = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"line {i}: " + new string('x', 20)));
        Assert.Null(ChatService.Oversized("search_code", "short", 24_000, true, Guid.NewGuid()));
        Assert.Null(ChatService.Oversized("search_code", text, 0, true, Guid.NewGuid()));

        var (said, file) = ChatService.Oversized("search_code", text, 24_000, true, Guid.NewGuid())!.Value;
        Assert.Equal(text, file.Text);
        Assert.StartsWith("search_code-result-", file.FileName, StringComparison.Ordinal);
        Assert.True(said.Length < 24_500);
        // Cut at a line's end, and the next line is where read_file reads on.
        var lastRead = said[..said.IndexOf("\n\n[The result", StringComparison.Ordinal)].Split('\n')[^1];
        var next = int.Parse(lastRead["line ".Length..lastRead.IndexOf(':')], System.Globalization.CultureInfo.InvariantCulture) + 1;
        Assert.Contains($"read_file reads on from line {next},", said, StringComparison.Ordinal);
        Assert.Contains(file.FileName, said, StringComparison.Ordinal);

        Assert.Contains("which the person can open", ChatService.Oversized("search_code", text, 24_000, false, Guid.NewGuid())!.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_tool_result_shows_its_end_the_errors_of_what_is_not_shown_and_its_outline()
    {
        // A test run's log: its one failure in the middle, its summary at the end. Before: only its start went to the
        // model, which said nothing failed (as a small model did in Code Arena, whose results already show these).
        var lines = Enumerable.Range(1, 4000).Select(i => $"test {i} passed").ToList();
        lines[2199] = "FAILED test 2200: expected 3, got 4";
        lines.Add("4000 tests, 1 failed");
        var log = string.Join("\n", lines);
        var said = ChatService.Oversized("run_tests", log, 24_000, true, Guid.NewGuid())!.Value.Text;
        Assert.True(said.Length < 26_000, $"{said.Length}");
        Assert.Contains("2200\tFAILED test 2200: expected 3, got 4", said, StringComparison.Ordinal);
        Assert.EndsWith("4000 tests, 1 failed", said.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("lines that look like errors", said, StringComparison.Ordinal);

        // A long document (an MCP server's page): its outline, each section's line, to read the one needed.
        var doc = string.Join("\n", Enumerable.Range(1, 60).SelectMany(i => (string[])[$"## Part {i}", .. Enumerable.Range(1, 30).Select(j => $"Words of part {i}, line {j}.")]));
        var page = ChatService.Oversized("fetch_page", doc, 24_000, true, Guid.NewGuid())!.Value.Text;
        Assert.Contains("Part 55", page, StringComparison.Ordinal);
        Assert.Contains("read_file", page, StringComparison.Ordinal);
        Assert.True(page.Length < 26_000, $"{page.Length}");
    }

    [Fact]
    public void Passages_about_a_question_are_its_best_blocks_in_order_with_their_headings()
    {
        var text = "# Install\n\nRun the installer on each server.\n\n# Licensing\n\nThe license key renews yearly.\n\n# Upgrade\n\nUpgrade servers one at a time.";
        var picked = Passages.Pick(text, "license renewal", 1000);
        Assert.Equal(["# Licensing", "The license key renews yearly."], picked.Select(p => p.Text));
        Assert.InRange(picked[1].Start, text.IndexOf("The license", StringComparison.Ordinal) - 2, text.IndexOf("The license", StringComparison.Ordinal));
        Assert.Empty(Passages.Pick(text, "the and", 1000));
        Assert.Empty(Passages.Pick(text, "kubernetes", 1000));
    }

    [Fact]
    public void A_key_the_model_wrote_twice_keeps_its_last_value_at_every_depth()
    {
        var args = ChatService.Arguments("""{"url":"https://x.test","start":0,"start":8000,"body":{"a":1,"a":2},"list":[{"k":1,"k":3}]}""");
        Assert.Equal(8000, args["start"]!.GetValue<int>());
        Assert.Equal(2, args["body"]!["a"]!.GetValue<int>());
        Assert.Equal(3, args["list"]![0]!["k"]!.GetValue<int>());
        Assert.Empty(ChatService.Arguments(""));
        Assert.Empty(ChatService.Arguments("[1,2]"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ChatService.Arguments("{not json"));
    }
}
