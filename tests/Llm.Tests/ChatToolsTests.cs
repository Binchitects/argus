using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Tools: the built-in ones, a chat's own choice, who may use what, asking first, pictures, and MCP servers.</summary>
[Collection(nameof(AppCollection))]
public sealed class ChatToolsTests(AppFixture app)
{
    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "t" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/chat/conversations", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    private static JsonElement Event(List<JsonElement> events, string type) => events.Single(e => e.GetProperty("type").GetString() == type);

    private List<string> FunctionsSentFor(string email) =>
        [.. (app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["tools"]?.AsArray() ?? [])
            .Select(t => t!["function"]!["name"]!.GetValue<string>())];

    private static async Task<List<string>> ToolsInConfigAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("id").GetString()!)];

    /// <summary>Its own app and database: tool settings must not leak into other tests.</summary>
    private WebApplicationFactory<Program> NewApp(FakeGateway? gateway = null, Dictionary<string, string?>? settings = null) =>
        app.Create(app.ConnectionStringFor("tools_" + Guid.NewGuid().ToString("N")[..8]), gateway ?? new FakeGateway(),
            new Dictionary<string, string?>(settings ?? []) { ["Auth:DataKey"] = "a-data-key-for-tool-tests" });

    private static Task<HttpResponseMessage> SetToolAsync(TestBrowser admin, string tool, object setting) =>
        admin.Http.PutAsJsonAsync(new Uri($"/api/admin/tools/{tool}", UriKind.Relative), setting);

    [Fact]
    public async Task Past_the_budget_the_model_loads_a_tool_and_the_chat_keeps_it()
    {
        await using var f = NewApp(settings: new() { ["Chat:ToolTextChars"] = "200" });
        var (b, _, email) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { useArgus = true, tools = new[] { "argus", "calculator", "time" } });

        var events = await SendAsync(b, id, """Where is ParseHeader? [call load_tools {"names":["argus"]}]""");
        var requests = app.Model.Requests.Where(r => r.Body["user"]!.GetValue<string>() == email).ToList();
        // First: load_tools only, and a line for each tool in the system prompt.
        Assert.Equal([OnDemandTools.Function], requests[0].Body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        var system = requests[0].Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("[argus]", system, StringComparison.Ordinal);
        Assert.Contains("[calculator]", system, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeArgus.Instructions, system, StringComparison.Ordinal);
        Assert.Equal("Loaded: find_symbol. Call them from now on.", Event(events, "tool_result").GetProperty("text").GetString());
        // Then: Argus whole, its notes in, its line out; the rest still listed.
        Assert.Equal(["find_symbol", OnDemandTools.Function], requests[1].Body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        var after = requests[1].Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains(FakeArgus.Instructions, after, StringComparison.Ordinal);
        Assert.DoesNotContain("[argus]", after, StringComparison.Ordinal);
        Assert.Contains("[calculator]", after, StringComparison.Ordinal);

        // The next turn starts with Argus loaded: the same prompt start as the round before.
        await SendAsync(b, id, "And its callers?");
        var next = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body;
        Assert.Equal(requests[1].Body["tools"]!.ToJsonString(), next["tools"]!.ToJsonString());
        Assert.Equal(after, next["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_calculator_and_the_clock_answer_for_the_model()
    {
        var (b, _, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { useArgus = false });
        var events = await SendAsync(b, id, """Work it out: [call calculate {"expression":"2^10 + sqrt(16) + 1_000"}]""");
        var result = Event(events, "tool_result");
        Assert.Equal("calculator", Event(events, "tool_call").GetProperty("tool").GetString());
        Assert.Equal("2028", JsonDocument.Parse(result.GetProperty("text").GetString()!).RootElement.GetProperty("result").GetString());
        Assert.False(result.GetProperty("isError").GetBoolean());

        events = await SendAsync(b, id, """And this: [call calculate {"expression":"2 +* 3"}]""");
        Assert.True(Event(events, "tool_result").GetProperty("isError").GetBoolean());

        events = await SendAsync(b, id, """Days? [call days_between {"start":"2026-09-01","end":"2026-09-15"}]""");
        var days = JsonDocument.Parse(Event(events, "tool_result").GetProperty("text").GetString()!).RootElement;
        Assert.Equal(14, days.GetProperty("days").GetInt32());
        Assert.Equal(10, days.GetProperty("working_days").GetInt32());

        events = await SendAsync(b, id, """Now? [call current_time {"time_zone":"Asia/Tehran"}]""");
        Assert.Contains("+03:30", Event(events, "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);

        events = await SendAsync(b, id, """Made up: [call launch_rockets {}]""");
        Assert.Contains("There is no tool named launch_rockets", Event(events, "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_asks_the_person_and_its_answer_ends_there_until_they_reply()
    {
        var (b, _, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { tools = new[] { "ask" } });
        var asks = app.Model.Requests.Count;
        var events = await SendAsync(b, id, """Plan it: [call ask_user {"questions":[{"question":"Which database?","options":[{"label":"PostgreSQL"},"SQLite"]}]}]""");
        Assert.False(Event(events, "tool_result").GetProperty("isError").GetBoolean());
        Assert.Single(events, e => e.GetProperty("type").GetString() == "assistant");
        Assert.Single(events, e => e.GetProperty("type").GetString() == "done");
        // The model was asked once: the answer stopped at the questions instead of going on.
        Assert.Equal(1, app.Model.Requests.Skip(asks).Count(r => r.Body["user"]!.GetValue<string>() == email));
        Assert.Contains("ask_user", app.Model.Requests.Last().Body["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        // The reply is the person's next message, after the questions.
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        var asked = chat.GetProperty("currentLeafId").GetGuid();
        var reply = await SendAsync(b, id, "Which database? PostgreSQL");
        Assert.Equal(asked, Event(reply, "question").GetProperty("parentId").GetGuid());

        // Questions in the wrong shape are sent back to the model to fix, and the answer goes on.
        var wrong = await SendAsync(b, id, """Again: [call ask_user {"questions":[{"question":"Which?","options":["only one"]}]}]""");
        Assert.Contains("2 to 6 options", Event(wrong, "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, wrong.Count(e => e.GetProperty("type").GetString() == "assistant"));
    }

    [Fact]
    public async Task Sub_agents_do_the_parts_side_by_side_with_the_chats_tools_and_their_results_come_back()
    {
        var (b, _, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { tools = new[] { "agents", "calculator", "ask" } });
        var parts = new
        {
            tasks = new[]
            {
                new { title = "Sum", instructions = """Work it out: [call calculate {"expression":"2+2"}]""" },
                new { title = "Colour", instructions = "Name a colour." },
            },
        };
        var events = await SendAsync(b, id, $"Split it: [call delegate {System.Text.Json.JsonSerializer.Serialize(parts)}]");

        var result = Event(events, "tool_result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var done = JsonDocument.Parse(result.GetProperty("text").GetString()!).RootElement.EnumerateArray().ToList();
        Assert.Equal(["Sum", "Colour"], done.Select(d => d.GetProperty("title").GetString()));
        Assert.Equal("Found it.", done[0].GetProperty("result").GetString());
        Assert.Equal(1, done[0].GetProperty("tool_calls").GetInt32());
        Assert.Equal("Answer to: Name a colour.", done[1].GetProperty("result").GetString());
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "tool_progress" && e.GetProperty("total").GetDouble() == 2);

        // Each sub-agent's work streams as it happens: it starts, thinks, calls a tool and reads its result, writes, ends.
        var callId = Event(events, "tool_call").GetProperty("id").GetString();
        var sum = events.Where(e => e.GetProperty("type").GetString() == "agent" && e.GetProperty("index").GetInt32() == 0).ToList();
        Assert.All(sum, e => Assert.Equal(callId, e.GetProperty("id").GetString()));
        Assert.Equal(["start", "tool_call", "tool_result", "content", "done"],
            sum.Select(e => e.GetProperty("event").GetString()).Where(k => k != "reasoning").Distinct());
        Assert.Equal("calculate", sum.First(e => e.GetProperty("event").GetString() == "tool_call").GetProperty("call").GetProperty("name").GetString());
        Assert.Contains("4", sum.First(e => e.GetProperty("event").GetString() == "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);

        // And it is kept with the call, for the page: each part's thinking, steps and words (the model read only the results).
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        var tool = chat.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("toolName").GetString() == "delegate");
        var kept = tool.GetProperty("details").GetProperty("agents");
        Assert.Equal("Sum", kept[0].GetProperty("title").GetString());
        Assert.Equal("calculate", kept[0].GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.Equal("Found it.", kept[0].GetProperty("text").GetString());
        Assert.DoesNotContain("steps", tool.GetProperty("content").GetString(), StringComparison.Ordinal);

        // Each sub-agent starts clean, with the chat's tools but not delegating again nor asking the person.
        var agents = app.Model.Requests.Select(r => r.Body)
            .Where(r => r["user"]!.GetValue<string>() == email && r["messages"]![0]!["content"]!.GetValue<string>().Contains("You are a sub-agent", StringComparison.Ordinal))
            .ToList();
        Assert.True(agents.Count >= 3);
        Assert.All(agents, r => Assert.Equal(2, r["messages"]!.AsArray().Count(m => m!["role"]!.GetValue<string>() != "tool" && m["role"]!.GetValue<string>() != "assistant")));
        var offered = agents.SelectMany(r => r["tools"]?.AsArray() ?? []).Select(t => t!["function"]!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("calculate", offered);
        Assert.DoesNotContain("delegate", offered);
        Assert.DoesNotContain("ask_user", offered);

        // One part is not a split: the model is told how to call it.
        var wrong = await SendAsync(b, id, """Again: [call delegate {"tasks":[{"title":"Only","instructions":"x"}]}]""");
        Assert.Contains("2 to 10 tasks", Event(wrong, "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sub_agents_drawing_side_by_side_each_keep_their_picture_and_the_call_shows_them_all()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var f = NewApp(gateway, new() { ["Chat:AgentsAtOnce"] = "3" });
        var (b, _, _) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { tools = new[] { "agents", "image" } });
        var parts = new
        {
            tasks = Enumerable.Range(1, 3).Select(i => new { title = $"Picture {i}", instructions = $$"""Draw: [call generate_image {"prompt":"A fox number {{i}}"}]""" }).ToArray(),
        };
        var events = await SendAsync(b, id, $"Three at once: [call delegate {JsonSerializer.Serialize(parts)}]");

        // Each picture is saved (three sub-agents at once, each with its own database scope) and is the call's.
        var result = Event(events, "tool_result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var pictures = result.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(3, pictures.Count);
        Assert.All(pictures, p => Assert.Equal("image", p.GetProperty("kind").GetString()));
        // As each is made, the page hears of it.
        Assert.Equal(3, events.Count(e => e.GetProperty("type").GetString() == "agent" && e.GetProperty("event").GetString() == "tool_result"
            && e.GetProperty("files").ValueKind == JsonValueKind.Array && e.GetProperty("files").GetArrayLength() == 1));
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        var tool = chat.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("toolName").GetString() == "delegate");
        Assert.Equal(3, tool.GetProperty("attachments").GetArrayLength());
        Assert.All(tool.GetProperty("details").GetProperty("agents").EnumerateArray(), a => Assert.Equal(1, a.GetProperty("steps")[0].GetProperty("files").GetArrayLength()));
        // Each one's tokens are kept (two rounds of the fake model's 100 in, 40 cached, 12 out): the answer's cost counts them.
        Assert.All(tool.GetProperty("details").GetProperty("agents").EnumerateArray(), a =>
        {
            Assert.Equal(200, a.GetProperty("usage").GetProperty("prompt").GetInt32());
            Assert.Equal(80, a.GetProperty("usage").GetProperty("cached").GetInt32());
            Assert.Equal(24, a.GetProperty("usage").GetProperty("completion").GetInt32());
            Assert.False(string.IsNullOrEmpty(a.GetProperty("model").GetString()));
        });
        foreach (var p in pictures)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync($"/api/chat/attachments/{p.GetProperty("id").GetGuid()}/content"));
        }
    }

    [Fact]
    public async Task A_chat_has_its_own_tools_among_those_the_person_may_use()
    {
        var (b, _, email) = await PersonAsync(app.Factory);
        Assert.Equal(["argus", "calculator", "time", "files", "ask", "agents"], await ToolsInConfigAsync(b));
        var id = await NewChatAsync(b, new { tools = new[] { "calculator" } });
        await SendAsync(b, id, "hello");
        Assert.Equal(["calculate"], FunctionsSentFor(email));
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        Assert.Equal(["calculator"], chat.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        Assert.False(chat.GetProperty("useArgus").GetBoolean());

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { tools = new[] { "time", "argus" } }));
        await SendAsync(b, id, "again");
        Assert.Equal(["find_symbol", "current_time", "days_between"], FunctionsSentFor(email));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { tools = new[] { "rockets" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/chat/conversations", new { tools = new[] { "image" } }));

        // useArgus still switches Argus in the chat's list.
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { useArgus = false }));
        Assert.Equal(["time"], (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Admins_decide_which_tools_exist_and_for_whom()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, personId, email) = await PersonAsync(f);
        var listed = await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"));
        var image = listed.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "image");
        Assert.Contains("no picture model", image.GetProperty("unavailable").GetString(), StringComparison.Ordinal);

        // Off for everyone.
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetToolAsync(admin, "calculator", new { enabled = false, audience = "Everyone", onByDefault = true, askFirst = false }));
        Assert.DoesNotContain("calculator", await ToolsInConfigAsync(b));

        // For one group only: the person is outside it, then in it; admins always may.
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Numbers" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await SetToolAsync(admin, "calculator", new { enabled = true, audience = "Groups", groups = Array.Empty<Guid>(), onByDefault = true, askFirst = false }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetToolAsync(admin, "calculator", new { enabled = true, audience = "Groups", groups = new[] { group }, onByDefault = true, askFirst = false }));
        Assert.DoesNotContain("calculator", await ToolsInConfigAsync(b));
        Assert.Contains("calculator", await ToolsInConfigAsync(admin));
        var id = await NewChatAsync(b, new { });
        await SendAsync(b, id, """Try: [call calculate {"expression":"1+1"}]""");
        Assert.DoesNotContain("calculate", FunctionsSentFor(email));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { personId } }));
        Assert.Contains("calculator", await ToolsInConfigAsync(b));

        // Off in new chats: still allowed, but a new chat does not have it until asked.
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetToolAsync(admin, "calculator", new { enabled = true, audience = "Everyone", onByDefault = false, askFirst = false }));
        var fresh = await NewChatAsync(b, new { });
        Assert.DoesNotContain("calculator", (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{fresh}"))).GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{fresh}", UriKind.Relative), new { tools = new[] { "calculator" } }));

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("tool.update", audit);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.GetAsync("/api/admin/tools"));
    }

    [Fact]
    public async Task A_tool_that_asks_first_waits_for_the_person()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetToolAsync(admin, "calculator", new { enabled = true, audience = "Everyone", onByDefault = true, askFirst = true }));
        var (b, _, _) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { useArgus = false });

        async Task<List<JsonElement>> AnswerAsync(bool allow)
        {
            var answering = SendAsync(b, id, """Sum: [call calculate {"expression":"6*7"}]""");
            // The call waits until the person answers; nobody else can answer for them.
            var (other, _, _) = await PersonAsync(f);
            for (var i = 0; ; i++)
            {
                await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/chat/conversations/{id}/tool-calls/call_1", new { allow = true }));
                var res = await b.PostAsync($"/api/chat/conversations/{id}/tool-calls/call_1", new { allow });
                if (res.StatusCode == HttpStatusCode.NoContent)
                {
                    break;
                }
                Assert.True(i < 100, "the call never waited for an answer");
                await Task.Delay(100);
            }
            return await answering;
        }

        var declined = await AnswerAsync(allow: false);
        Assert.Equal("calculator", Event(declined, "approval").GetProperty("tool").GetString());
        var result = Event(declined, "tool_result");
        Assert.True(result.GetProperty("declined").GetBoolean());
        Assert.Contains("did not allow", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        var stored = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("declined", stored.GetProperty("status").GetString());

        var allowed = await AnswerAsync(allow: true);
        Assert.Contains("\"result\":\"42\"", Event(allowed, "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Conflict, await b.PostAsync($"/api/chat/conversations/{id}/tool-calls/call_1", new { allow = true }));
    }

    [Fact]
    public async Task The_image_tool_draws_with_the_gateways_image_model_and_keeps_the_picture()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var f = NewApp(gateway);
        var (b, _, email) = await PersonAsync(f);
        var config = await b.JsonAsync(await b.GetAsync("/api/chat/config"));
        Assert.DoesNotContain("FLUX.2-klein-4B", config.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Contains("image", config.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("id").GetString()));

        var id = await NewChatAsync(b, new { useArgus = false });
        var before = app.Model.ImageRequests.Count;
        var events = await SendAsync(b, id, """Draw: [call generate_image {"prompt":"A red fox in the snow","size":"768x768"}]""");
        var sent = app.Model.ImageRequests.Skip(before).Single();
        Assert.Equal("FLUX.2-klein-4B", sent["model"]!.GetValue<string>());
        Assert.Equal("768x768", sent["size"]!.GetValue<string>());
        Assert.Equal(email, sent["user"]!.GetValue<string>());
        var result = Event(events, "tool_result");
        var picture = result.GetProperty("attachments")[0];
        Assert.Equal("a-red-fox-in-the-snow.png", picture.GetProperty("fileName").GetString());
        Assert.Equal("image", picture.GetProperty("kind").GetString());
        Assert.DoesNotContain("base64", result.GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);

        // Kept as the person's file, on the tool's message, and gone with the chat.
        var content = await b.GetAsync($"/api/chat/attachments/{picture.GetProperty("id").GetGuid()}/content");
        await StatusAssert.Is(HttpStatusCode.OK, content);
        Assert.Equal(FakeModel.Png, await content.Content.ReadAsByteArrayAsync());
        var tool = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal(picture.GetProperty("id").GetGuid(), tool.GetProperty("attachments")[0].GetProperty("id").GetGuid());
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/attachments/{picture.GetProperty("id").GetGuid()}/content"));
    }

    [Fact]
    public async Task An_mcp_server_an_admin_adds_becomes_a_tool()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var server = new { name = "Weather Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey, emailHeader = "X-User-Email" };

        var wrong = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", server with { headerValue = "nope" }));
        Assert.False(wrong.GetProperty("ok").GetBoolean());
        Assert.Contains("refused", wrong.GetProperty("error").GetString(), StringComparison.Ordinal);
        var tested = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", server));
        Assert.True(tested.GetProperty("ok").GetBoolean());
        Assert.Equal("echo", tested.GetProperty("tools")[0].GetProperty("name").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/tools/servers", server with { url = "ftp://x" }));

        var made = await admin.PostAsync("/api/admin/tools/servers", server);
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/tools/servers", server with { name = "weather desk" }));
        var listedText = await (await admin.GetAsync("/api/admin/tools")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(FakeMcp.ApiKey, listedText, StringComparison.Ordinal);
        var listed = JsonDocument.Parse(listedText).RootElement.EnumerateArray().Single(t => t.GetProperty("id").GetString() == toolId);
        Assert.True(listed.GetProperty("server").GetProperty("headerSet").GetBoolean());
        Assert.Equal("weather_desk__", listed.GetProperty("server").GetProperty("prefix").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("server").GetProperty("callTimeoutMinutes").ValueKind);
        var serverId = Guid.Parse(toolId[4..]);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/tools/servers/{serverId}", UriKind.Relative), new { callTimeoutMinutes = 2000 }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/tools/servers/{serverId}", UriKind.Relative), new { callTimeoutMinutes = 90 }));
        var longer = (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == toolId);
        Assert.Equal(90, longer.GetProperty("server").GetProperty("callTimeoutMinutes").GetInt32());

        var (b, _, email) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { tools = new[] { toolId } });
        var events = await SendAsync(b, id, """Echo: [call weather_desk__echo {"text":"hello"}]""");
        Assert.Equal("echo: hello", Event(events, "tool_result").GetProperty("text").GetString());
        var call = app.Mcp.Calls.Last(c => c.Method == "tools/call");
        Assert.Equal("echo", call.Params!.Value.GetProperty("name").GetString());
        Assert.Equal(email, call.Headers["X-User-Email"]);

        // A long call says how far it is on the way, and the chat passes it on.
        var slow = await SendAsync(b, id, """Echo: [call weather_desk__echo {"text":"slow"}]""");
        var progress = Event(slow, "tool_progress");
        Assert.Equal("Warming up", progress.GetProperty("message").GetString());
        Assert.Equal(2, progress.GetProperty("total").GetDouble());
        Assert.Equal(Event(slow, "tool_call").GetProperty("id").GetString(), progress.GetProperty("id").GetString());
        Assert.Equal("echo: slow", Event(slow, "tool_result").GetProperty("text").GetString());

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/tools/servers/{Guid.Parse(toolId[4..])}", UriKind.Relative)));
        Assert.DoesNotContain(toolId, await ToolsInConfigAsync(b));
    }

    [Fact]
    public async Task Answering_again_keeps_the_chat_on_the_shown_answer_until_the_new_one_exists()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var made = await admin.PostAsync("/api/admin/tools/servers", new { name = "Slow Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey });
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        var (b, _, _) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { tools = new[] { toolId } });
        await SendAsync(b, id, "first question");
        async Task<JsonElement> ChatAsync() => await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        var shown = (await ChatAsync()).GetProperty("currentLeafId").GetGuid();

        // The tool's server is slow to answer: the new answer is not there yet. A
        // stop now used to leave the chat on the question, every answer hidden.
        app.Mcp.Hold = new TaskCompletionSource();
        try
        {
            int Initializes() { lock (app.Mcp.Calls) { return app.Mcp.Calls.Count(c => c.Method == "initialize"); } }
            var before = Initializes();
            var again = b.PostAsync($"/api/chat/conversations/{id}/regenerate", new { });
            for (var i = 0; i < 100 && Initializes() == before; i++)
            {
                await Task.Delay(50);
            }
            Assert.Equal(shown, (await ChatAsync()).GetProperty("currentLeafId").GetGuid());
            app.Mcp.Hold.SetResult();
            await StatusAssert.Is(HttpStatusCode.OK, await again);
        }
        finally
        {
            app.Mcp.Hold?.TrySetResult();
            app.Mcp.Hold = null;
        }
        var after = await ChatAsync();
        var answers = after.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "assistant").ToList();
        Assert.Equal(2, answers.Count);
        Assert.Equal(answers[1].GetProperty("id").GetGuid(), after.GetProperty("currentLeafId").GetGuid());
    }

    [Fact]
    public async Task A_long_file_goes_in_part_and_the_model_reads_on_or_searches_it()
    {
        await using var f = NewApp(settings: new() { ["Chat:InlineAttachmentChars"] = "2000" });
        var (b, _, email) = await PersonAsync(f);
        var log = string.Join('\n', Enumerable.Range(1, 1000).Select(i => i == 800 ? $"line {i}: ERROR code ZEBRA-9 in the payment worker" : $"line {i}: all quiet"));
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(log));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", "worker.log");
        var file = await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form));
        var id = await NewChatAsync(b, new { });

        async Task<List<JsonElement>> AskAsync(string text)
        {
            var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text, attachments = new[] { file.GetProperty("id").GetGuid() } });
            return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
        }
        var events = await AskAsync("""Where is the error? [call search_file {"file":"worker.log","text":"zebra"}]""");
        // The question carried the start of the file and a note, not the whole of it.
        var question = app.Model.Requests.First(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("line 1: all quiet", question, StringComparison.Ordinal);
        Assert.DoesNotContain("ZEBRA-9", question, StringComparison.Ordinal);
        Assert.Matches(@"\[This file goes on: lines 1 to \d+ of 1000 are above .* Read on with read_file \(file ""worker\.log"", from_line \d+\)", question);
        Assert.Contains("read_file", FunctionsSentFor(email));
        var found = JsonDocument.Parse(Event(events, "tool_result").GetProperty("text").GetString()!).RootElement;
        Assert.Equal(800, found.GetProperty("matches")[0].GetProperty("line").GetInt32());

        events = await AskAsync("""Read around it [call read_file {"file":"WORKER.LOG","from_line":799,"lines":3}]""");
        var read = JsonDocument.Parse(Event(events, "tool_result").GetProperty("text").GetString()!).RootElement;
        Assert.Equal("799: line 799: all quiet\n800: line 800: ERROR code ZEBRA-9 in the payment worker\n801: line 801: all quiet\n", read.GetProperty("text").GetString());
        Assert.Equal(802, read.GetProperty("next_from_line").GetInt32());
        Assert.Equal(1000, read.GetProperty("total_lines").GetInt32());

        // Attached with each question, the file is listed once.
        events = await AskAsync("""[call list_files {}]""");
        var listed = JsonDocument.Parse(Event(events, "tool_result").GetProperty("text").GetString()!).RootElement.GetProperty("files");
        Assert.Equal(["worker.log"], listed.EnumerateArray().Select(x => x.GetProperty("file").GetString()));
    }

    [Fact]
    public async Task A_document_shows_as_its_pages_drawn_once_in_the_sandbox_and_only_to_its_owner()
    {
        await using var sandbox = new FakeSandbox();
        await using var f = NewApp(settings: new() { ["Sandbox:Dir"] = sandbox.Dir });
        var (b, _, _) = await PersonAsync(f);
        var (other, _, _) = await PersonAsync(f);
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(595, 842).AddText("Page one", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica));
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(builder.Build());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(part, "file", "report.pdf");
        var id = (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();

        var pages = await b.JsonAsync(await b.GetAsync($"/api/chat/attachments/{id}/pages"));
        Assert.Equal(3, pages.GetProperty("total").GetInt32());
        Assert.Equal(2, pages.GetProperty("drawn").GetInt32());
        Assert.Equal([$"/api/chat/attachments/{id}/pages/1", $"/api/chat/attachments/{id}/pages/2"], pages.GetProperty("pages").EnumerateArray().Select(x => x.GetString()));
        var first = await b.GetAsync($"/api/chat/attachments/{id}/pages/1");
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF, 1], await first.Content.ReadAsByteArrayAsync());

        // Drawn once: a second look reads what was kept.
        await b.GetAsync($"/api/chat/attachments/{id}/pages");
        var job = Assert.Single(sandbox.Jobs, j => j["code"]!.GetValue<string>().StartsWith("# pages", StringComparison.Ordinal));
        Assert.Equal(["doc.pdf"], job["files"]!.AsArray().Select(x => x!.GetValue<string>()));

        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/attachments/{id}/pages"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/attachments/{id}/pages/1"));
        var text = await UploadAsync(b, "notes.txt", "no pages here");
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.GetAsync($"/api/chat/attachments/{text}/pages"));
    }

    [Fact]
    public async Task Deep_research_turns_on_sub_agents_for_the_answer_and_asks_for_a_plan_and_a_sourced_report()
    {
        var (b, _, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { tools = new[] { "calculator" } });
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "Compare the codecs", research = true });
        var events = (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement).ToList();
        // Sub-agents for this answer, though the chat has only the calculator; the web is not allowed here, and it says so.
        Assert.Contains("delegate", FunctionsSentFor(email));
        Assert.Contains("calculate", FunctionsSentFor(email));
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "notice" && e.GetProperty("kind").GetString() == "research_no_web");
        var system = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("Deep research: the person asked for a thorough, sourced report", system, StringComparison.Ordinal);
        var sent = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.StartsWith("Compare the codecs\n\n(Deep research: plan the research questions", sent, StringComparison.Ordinal);
        var kept = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("Compare the codecs", kept);
        // The chat keeps its own tools; the next answer, without research, has no sub-agents.
        await SendAsync(b, id, "Thanks");
        Assert.DoesNotContain("delegate", FunctionsSentFor(email));
    }

    [Fact]
    public async Task On_a_new_deployment_the_first_answer_waits_for_the_gateway_to_make_the_chats_key()
    {
        // The gateway answers before it can make keys: the first two tries are refused.
        var gateway = new FakeGateway { ServiceKeyFailures = 2 };
        await using var f = NewApp(gateway);
        var (b, _, _) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { tools = Array.Empty<string>() });
        var events = await SendAsync(b, id, "Hello on day one");
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
        Assert.Equal("done", events.Last().GetProperty("type").GetString());
        Assert.Equal(0, gateway.ServiceKeyFailures);
        Assert.Single(gateway.ServiceKeys);
    }

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Python_runs_in_the_sandbox_with_the_chats_files_and_what_it_writes_comes_back()
    {
        await using var sandbox = new FakeSandbox();
        await using var f = NewApp(settings: new() { ["Sandbox:Dir"] = sandbox.Dir });
        var (b, _, _) = await PersonAsync(f);
        Assert.Contains("python", await ToolsInConfigAsync(b));
        var csv = await UploadAsync(b, "sales.csv", "month,amount\n1,10\n2,32\n");
        var id = await NewChatAsync(b, new { });
        async Task<JsonElement> RunAsync(string text, Guid[]? files = null)
        {
            var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text, attachments = files ?? [] });
            var events = (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement).ToList();
            return Event(events, "tool_result");
        }

        var result = await RunAsync("""Chart it [call run_python {"code":"print(1)  # chart # csv # binary"}]""", [csv]);
        var output = JsonDocument.Parse(result.GetProperty("text").GetString()!).RootElement;
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal(0, output.GetProperty("exit_code").GetInt32());
        Assert.Contains("given: sales.csv\n", output.GetProperty("stdout").GetString(), StringComparison.Ordinal);
        Assert.Equal(["data.bin (in their Files panel)", "figure-1.png (shown as a picture)", "summary.csv (in their Files panel)"],
            output.GetProperty("files_given_to_the_person").EnumerateArray().Select(x => x.GetString()));
        var made = result.GetProperty("attachments").EnumerateArray().ToDictionary(a => a.GetProperty("fileName").GetString()!);
        Assert.Equal("image", made["figure-1.png"].GetProperty("kind").GetString());
        Assert.Equal("text", made["summary.csv"].GetProperty("kind").GetString());
        Assert.Equal("file", made["data.bin"].GetProperty("kind").GetString());
        Assert.True(made["data.bin"].GetProperty("original").GetBoolean());

        // A file it made is the person's to download, as it was written, never rendered.
        var download = await b.GetAsync($"/api/chat/attachments/{made["data.bin"].GetProperty("id").GetGuid()}/content?download=1");
        Assert.Equal([0, 1, 2, 3, 0, 255], await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);

        // What it made is read like any file, and the next run is given it too.
        var read = JsonDocument.Parse((await RunAsync("""[call read_file {"file":"summary.csv"}]""")).GetProperty("text").GetString()!).RootElement;
        Assert.Equal("1: month,total\n2: 1,42\n", read.GetProperty("text").GetString());
        var again = JsonDocument.Parse((await RunAsync("""[call run_python {"code":"print(2)"}]""")).GetProperty("text").GetString()!).RootElement;
        Assert.Contains("given: data.bin,figure-1.png,sales.csv,summary.csv\n", again.GetProperty("stdout").GetString(), StringComparison.Ordinal);

        // Code that fails is an error the model reads, with its traceback.
        var failed = await RunAsync("""[call run_python {"code":"raise ValueError  # fail"}]""");
        Assert.True(failed.GetProperty("isError").GetBoolean());
        Assert.Contains("ValueError: bad", JsonDocument.Parse(failed.GetProperty("text").GetString()!).RootElement.GetProperty("stderr").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_running_sandbox_python_is_not_offered_and_says_why()
    {
        await using var sandbox = new FakeSandbox(alive: false);
        await using var f = NewApp(settings: new() { ["Sandbox:Dir"] = sandbox.Dir });
        var (b, _, _) = await PersonAsync(f);
        Assert.DoesNotContain("python", await ToolsInConfigAsync(b));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var python = (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == "python");
        Assert.Contains("sandbox profile", python.GetProperty("unavailable").GetString(), StringComparison.Ordinal);
    }
}
