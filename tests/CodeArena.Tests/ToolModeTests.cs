using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>How the model is given the session's tools, as Arena says for each model (Admin → Models → Tools) or config.json does.</summary>
public sealed class ToolModeTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new()
    {
        // One of the chat's tools, as Arena MCP lists it: it says whose it is.
        ToolList = new JsonArray(new JsonObject
        {
            ["name"] = "web_search", ["description"] = "Search the web.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("query"),
            },
            ["_meta"] = new JsonObject { ["arena/tool"] = "web" },
        }),
    };

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    /// <summary>Arena's tools connected and its chat config read: both come in the background.</summary>
    private static async Task ReadyAsync(WebRun web)
    {
        for (var i = 0; i < 200; i++)
        {
            var groups = (await web.GetJsonAsync("/api/state"))["toolGroups"]!.AsArray();
            if (groups.Count > 0 && groups[0]!["title"]!.GetValue<string>() == "Web search")
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("Arena's tools and its chat config did not come.");
    }

    private void ArenaSays(string a, string b) => _mcp.Chats.ChatConfig = new JsonObject
    {
        ["tools"] = new JsonArray(new JsonObject { ["id"] = "web", ["title"] = "Web search", ["description"] = "Searches the web.", ["icon"] = "globe" }),
        ["models"] = new JsonArray(new JsonObject { ["name"] = "model-a", ["toolMode"] = a }, new JsonObject { ["name"] = "model-b", ["toolMode"] = b }),
    };

    [Fact]
    public async Task The_servers_tools_go_as_code_direct_or_both_as_Arena_says_for_the_model_in_use()
    {
        ArenaSays("code", "direct");
        _gateway.Answer = _ => Reply.Say("ok");
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        await ReadyAsync(web);

        // As code (model-a): run_code, the local tools as they are, Arena's named for a program in the note.
        await (await web.SendAsync("hello")).RestAsync();
        var sent = FakeGateway.ToolNames(_gateway.Requests[^1]);
        Assert.Equal("model-a", _gateway.Requests[^1]["model"]!.GetValue<string>());
        Assert.Contains("run_code", sent);
        Assert.Contains("read_file", sent);
        Assert.DoesNotContain("web_search", sent);
        var note = FakeGateway.Notes(_gateway.Requests[^1])[^1];
        Assert.Contains("# Tools as code", note);
        Assert.Contains("- arena.arena.web_search(query): Search the web.", note);

        // Direct (model-b): every tool, no run_code, no note.
        await web.PostAsync("/api/settings", new JsonObject { ["model"] = "model-b" });
        await (await web.SendAsync("again")).RestAsync();
        sent = FakeGateway.ToolNames(_gateway.Requests[^1]);
        Assert.Equal("model-b", _gateway.Requests[^1]["model"]!.GetValue<string>());
        Assert.Contains("web_search", sent);
        Assert.DoesNotContain("run_code", sent);
        Assert.DoesNotContain("# Tools as code", FakeGateway.Notes(_gateway.Requests[^1])[^1]);
    }

    [Fact]
    public async Task Config_json_s_tool_mode_has_the_last_word_and_without_any_word_both_go()
    {
        ArenaSays("code", "code");
        _gateway.Answer = _ => Reply.Say("ok");
        using (var h = new Harness(_gateway, _mcp, c => c["toolMode"] = "both"))
        {
            await using var web = await WebRun.StartAsync(h);
            await ReadyAsync(web);
            await (await web.SendAsync("hello")).RestAsync();
            var sent = FakeGateway.ToolNames(_gateway.Requests[^1]);
            Assert.Contains("run_code", sent);
            Assert.Contains("web_search", sent);
        }

        // An Arena that says nothing of its models: both.
        _mcp.Chats.ChatConfig = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["id"] = "web", ["title"] = "Web search", ["description"] = "Searches the web." }) };
        using (var h = new Harness(_gateway, _mcp))
        {
            await using var web = await WebRun.StartAsync(h);
            await ReadyAsync(web);
            await (await web.SendAsync("hello")).RestAsync();
            var sent = FakeGateway.ToolNames(_gateway.Requests[^1]);
            Assert.Contains("run_code", sent);
            Assert.Contains("web_search", sent);
        }

        // A mode that is none is refused in config.json, by name.
        using var wrong = new Harness(_gateway, _mcp, c => c["toolMode"] = "sometimes");
        Assert.NotEqual(0, await wrong.Run("/exit\n", "chat"));
        Assert.Contains("\"toolMode\"", wrong.Err, StringComparison.Ordinal);
    }
}
