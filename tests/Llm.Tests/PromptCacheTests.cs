using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Models;

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
}
