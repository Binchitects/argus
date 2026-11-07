using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Arena's tools on demand and the session's Tools menu, as the web chat has them: through the IDE's API and the requests sent.</summary>
public sealed class ToolDemandTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new()
    {
        // Two of the chat's tools, as Arena MCP lists their functions: each says whose it is.
        ToolList = new JsonArray(
            new JsonObject
            {
                ["name"] = "web_search", ["description"] = "Search the web.",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } } },
                ["_meta"] = new JsonObject { ["arena/tool"] = "web" },
            },
            new JsonObject
            {
                ["name"] = "fetch_page", ["description"] = "Read a web page.", ["inputSchema"] = new JsonObject { ["type"] = "object" },
                ["_meta"] = new JsonObject { ["arena/tool"] = "web" },
            },
            new JsonObject
            {
                ["name"] = "create_issue", ["description"] = "Open an issue in GitLab.", ["inputSchema"] = new JsonObject { ["type"] = "object" },
                ["_meta"] = new JsonObject { ["arena/tool"] = "gitlab" },
            }),
    };

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static async Task<JsonNode> ConnectedAsync(WebRun web)
    {
        JsonNode state = new JsonObject();
        // Arena's groups, once its tools are connected (Code Arena's own families are there from the start).
        for (var i = 0; i < 200 && (state = await web.GetJsonAsync("/api/state"))["toolGroups"]!.AsArray().Count(g => !g!["id"]!.ToString().StartsWith("code-arena", StringComparison.Ordinal)) < 2; i++)
        {
            await Task.Delay(25);
        }
        return state;
    }

    [Fact]
    public async Task Past_the_budget_Arena_s_tools_are_listed_and_the_model_loads_those_it_needs_which_stay_loaded_in_the_session()
    {
        _mcp.Chats.ChatConfig = JsonNode.Parse("""{"tools":[{"id":"web","title":"Web search","description":"Searches the web and reads pages.","icon":"globe"}]}""")!.AsObject();
        using var h = new Harness(_gateway, _mcp, c => c["toolChars"] = 100);
        var asked = 0;
        _gateway.Answer = req => ++asked switch
        {
            1 => Reply.Call(("load_tools", """{"names":["Web search"]}""")),
            2 => Reply.Call(("web_search", """{"query":"news"}""")),
            _ => Reply.Say("done"),
        };
        await using var web = await WebRun.StartAsync(h);
        await ConnectedAsync(web);
        // Arena's word on its tools comes in the background too.
        JsonNode state = new JsonObject();
        for (var i = 0; i < 100 && (state = await web.GetJsonAsync("/api/state")).Servers()[0]!["title"]!.GetValue<string>() != "Web search"; i++)
        {
            await Task.Delay(25);
        }
        Assert.True(state["toolsOnDemand"]!.GetValue<bool>());
        Assert.Equal(("Web search", "globe", "plug"), (state.Servers()[0]!["title"]!.GetValue<string>(), state.Servers()[0]!["icon"]!.GetValue<string>(),
            state.Servers()[1]!["icon"]!.GetValue<string>()));

        await (await web.SendAsync("what is new?")).RestAsync();
        var first = _gateway.Requests[0];
        // The local tools whole; Arena's listed in the note, with load_tools to load them.
        Assert.Contains("read_file", FakeGateway.ToolNames(first));
        Assert.Contains("load_tools", FakeGateway.ToolNames(first));
        Assert.DoesNotContain("web_search", FakeGateway.ToolNames(first));
        var note = FakeGateway.Notes(first)[^1];
        Assert.Contains("# Tools to load", note);
        Assert.Contains("- Web search [web]: Searches the web and reads pages. Functions: web_search, fetch_page.", note);
        Assert.Contains("[gitlab]", note);
        // Loaded by its title: the web's functions from the next step on, the rest still listed.
        var second = _gateway.Requests[1];
        Assert.Contains("Loaded: web_search, fetch_page.", FakeGateway.History(second)[^1]!["content"]!.GetValue<string>());
        Assert.Contains("web_search", FakeGateway.ToolNames(second));
        Assert.Contains("load_tools", FakeGateway.ToolNames(second));
        Assert.DoesNotContain("create_issue", FakeGateway.ToolNames(second));
        Assert.DoesNotContain("[web]", FakeGateway.Notes(second)[^1]);
        Assert.Contains("Results for news", FakeGateway.History(_gateway.Requests[2])[^1]!["content"]!.GetValue<string>());
        Assert.True((await web.GetJsonAsync("/api/state")).Servers()[0]!["loaded"]!.GetValue<bool>());

        // Kept with the session: another one starts with none loaded, and this one read back has them again.
        var id = (await web.GetJsonAsync("/api/session"))["id"]!.GetValue<string>();
        await web.PostAsync("/api/sessions/new", new JsonObject());
        Assert.False((await web.GetJsonAsync("/api/state")).Servers()[0]!["loaded"]!.GetValue<bool>());
        await web.PostAsync("/api/sessions/resume", new JsonObject { ["id"] = id });
        await (await web.SendAsync("again")).RestAsync();
        Assert.Contains("web_search", FakeGateway.ToolNames(_gateway.Requests[^1]));
        Assert.DoesNotContain("create_issue", FakeGateway.ToolNames(_gateway.Requests[^1]));
    }

    [Fact]
    public async Task A_function_called_straight_from_the_list_is_loaded_and_runs()
    {
        using var h = new Harness(_gateway, _mcp, c => c["toolChars"] = 100);
        var asked = 0;
        _gateway.Answer = req => ++asked == 1 ? Reply.Call(("create_issue", "{}")) : Reply.Say("done");
        await using var web = await WebRun.StartAsync(h);
        await ConnectedAsync(web);
        await (await web.SendAsync("open one")).RestAsync();
        Assert.Contains("create_issue", FakeGateway.ToolNames(_gateway.Requests[1]));
        Assert.Contains(_mcp.Calls, c => c.Method == "tools/call");
    }

    [Fact]
    public async Task The_terminal_turns_a_group_off_and_on_and_lists_what_waits_to_be_loaded()
    {
        using var h = new Harness(_gateway, _mcp, c => c["toolChars"] = 100);
        Assert.Equal(0, await h.Run("/mcp\n/tools off gitlab\n/tools off nothing-here\n/tools\nhello\n/tools on create_issue\nagain\n/exit\n", "chat"));
        Assert.Contains("gitlab off in this session.", h.Out);
        Assert.Contains("There are no tools nothing-here here", h.Err);
        Assert.Contains("off in this session (gitlab)", h.Out);
        Assert.Contains("loaded when needed", h.Out);
        Assert.Contains("[web]", FakeGateway.Notes(_gateway.Requests[0])[^1]);
        Assert.DoesNotContain("[gitlab]", FakeGateway.Notes(_gateway.Requests[0])[^1]);
        Assert.Contains("[gitlab]", FakeGateway.Notes(_gateway.Requests[^1])[^1]);
    }

    [Fact]
    public async Task Within_the_budget_every_tool_goes_whole_as_before()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        var state = await ConnectedAsync(web);
        Assert.False(state["toolsOnDemand"]!.GetValue<bool>());
        await (await web.SendAsync("hello")).RestAsync();
        Assert.Contains("web_search", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.Contains("create_issue", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.DoesNotContain("load_tools", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.DoesNotContain("# Tools to load", FakeGateway.System(_gateway.Requests[0]));
    }

    [Fact]
    public async Task A_tool_turned_off_in_the_Tools_menu_leaves_the_requests_and_a_call_to_it_is_refused()
    {
        using var h = new Harness(_gateway, _mcp);
        var asked = 0;
        _gateway.Answer = req => ++asked == 1 ? Reply.Call(("create_issue", "{}")) : Reply.Say("done");
        await using var web = await WebRun.StartAsync(h);
        await ConnectedAsync(web);

        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/settings", new JsonObject { ["tools"] = "web" })).StatusCode);
        var state = JsonNode.Parse(await (await web.PostAsync("/api/settings", new JsonObject { ["tools"] = new JsonArray("web") })).Content.ReadAsStringAsync())!;
        Assert.Equal([("web", true), ("gitlab", false)], state.Servers().Select(g => (g!["id"]!.GetValue<string>(), g["on"]!.GetValue<bool>())));

        await (await web.SendAsync("open an issue")).RestAsync();
        Assert.Contains("web_search", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.DoesNotContain("create_issue", FakeGateway.ToolNames(_gateway.Requests[0]));
        Assert.Contains("turned off in this session", FakeGateway.History(_gateway.Requests[1])[^1]!["content"]!.GetValue<string>());
        Assert.DoesNotContain(_mcp.Calls, c => c.Method == "tools/call");

        // On again.
        await web.PostAsync("/api/settings", new JsonObject { ["tools"] = new JsonArray("web", "gitlab") });
        await (await web.SendAsync("now")).RestAsync();
        Assert.Contains("create_issue", FakeGateway.ToolNames(_gateway.Requests[^1]));
    }

    [Fact]
    public async Task The_Tools_menu_lists_every_tool_the_requests_carry_Code_Arena_s_own_families_too_and_turns_them_off()
    {
        using var h = new Harness(_gateway, _mcp);
        var asked = 0;
        _gateway.Answer = req => ++asked == 2 ? Reply.Call(("recall", """{"query":"x"}""")) : Reply.Say("done");
        await using var web = await WebRun.StartAsync(h);
        var state = await ConnectedAsync(web);
        await (await web.SendAsync("hello")).RestAsync();

        // Each tool the model is given is in one group of the menu, as the context view counts them.
        var listed = state["toolGroups"]!.AsArray().SelectMany(g => g!["names"]!.AsArray().Select(n => n!.GetValue<string>())).ToHashSet();
        Assert.All(FakeGateway.ToolNames(_gateway.Requests[0]).Where(n => n != "load_tools"), n => Assert.Contains(n, listed));
        var core = state["toolGroups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "code-arena")!;
        Assert.True(core["locked"]!.GetValue<bool>());
        Assert.Contains("read_file", core["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        var memory = state["toolGroups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "code-arena:memory")!;
        Assert.Equal("Memory", memory["title"]!.GetValue<string>());

        // Memory off, as the menu sends it (every group on but that one): its tools leave the requests, a call is refused.
        var on = new JsonArray([.. state["toolGroups"]!.AsArray().Select(g => g!["id"]!.GetValue<string>()).Where(id => id != "code-arena:memory").Select(id => (JsonNode)id)]);
        state = JsonNode.Parse(await (await web.PostAsync("/api/settings", new JsonObject { ["tools"] = on })).Content.ReadAsStringAsync())!;
        Assert.False(state["toolGroups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "code-arena:memory")!["on"]!.GetValue<bool>());
        Assert.True(state["toolGroups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "code-arena")!["on"]!.GetValue<bool>());
        await (await web.SendAsync("what did I say")).RestAsync();
        Assert.DoesNotContain("recall", FakeGateway.ToolNames(_gateway.Requests[1]));
        Assert.Contains("read_file", FakeGateway.ToolNames(_gateway.Requests[1]));
        Assert.Contains("turned off in this session", FakeGateway.History(_gateway.Requests[2])[^1]!["content"]!.GetValue<string>());
        // A list of the servers' groups alone (as before) leaves Code Arena's own as they are.
        state = JsonNode.Parse(await (await web.PostAsync("/api/settings", new JsonObject { ["tools"] = new JsonArray("web", "gitlab") })).Content.ReadAsStringAsync())!;
        Assert.False(state["toolGroups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "code-arena:memory")!["on"]!.GetValue<bool>());
        Assert.True(state["toolGroups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "code-arena:agents")!["on"]!.GetValue<bool>());
    }
}

/// <summary>The Tools menu's groups of the servers (Code Arena's own families left out).</summary>
internal static class ToolGroupsOf
{
    public static JsonArray Servers(this JsonNode state) =>
        new([.. state["toolGroups"]!.AsArray().Where(g => !g!["id"]!.GetValue<string>().StartsWith("code-arena", StringComparison.Ordinal)).Select(g => g!.DeepClone())]);
}
