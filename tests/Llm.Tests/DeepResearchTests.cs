using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Deep research as a tool (Admin → Tools): who may use it, the composer's switch only for them,
/// the model starting one itself when the person allows it, and Arena MCP running one for an agent.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class DeepResearchTests(AppFixture app)
{
    /// <summary>Its own app and database: tool settings must not leak into other tests.</summary>
    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? settings = null) =>
        app.Create(app.ConnectionStringFor("research_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings);

    private static async Task<(TestBrowser Browser, Guid Id, string Email, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "r" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test",
            made.GetProperty("apiKey").GetString()!);
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object? body = null)
    {
        var res = await b.PostAsync("/api/chat/conversations", body ?? new { });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SetResearchAsync(TestBrowser admin, bool enabled = true, string audience = "Everyone", Guid[]? groups = null, bool onByDefault = true,
        bool askFirst = true) =>
        admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/research", UriKind.Relative), new { enabled, audience, groups = groups ?? [], onByDefault, askFirst });

    private static async Task<JsonElement?> ResearchInConfigAsync(TestBrowser b) =>
        (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("tools").EnumerateArray().Cast<JsonElement?>()
            .FirstOrDefault(t => t!.Value.GetProperty("id").GetString() == "research");

    private static async Task<List<JsonElement>> EventsAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {body}");
        return [.. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    private static List<JsonElement> OfType(List<JsonElement> events, string type) => [.. events.Where(e => e.GetProperty("type").GetString() == type)];

    /// <summary>The answer's own requests for a question with this marker (not its title's, nor its sub-agents').</summary>
    private List<JsonObject> AnswerRequests(string marker) =>
        [.. app.Model.Requests.Select(r => r.Body).Where(r => r["tools"] is not null && r["messages"]!.ToJsonString().Contains(marker, StringComparison.Ordinal)
            && !r["messages"]![0]!["content"]!.GetValue<string>().Contains("You are a sub-agent", StringComparison.Ordinal))];

    private static List<string> Functions(JsonObject request) => [.. request["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())];

    /// <summary>A delegate call with these parts, for a script.</summary>
    private static object Delegate(string marker, params string[] titles) =>
        new { name = "delegate", arguments = new { tasks = titles.Select(t => new { title = t, instructions = $"{marker}: look up {t}." }).ToArray() } };

    /// <summary>Allows (or declines) the call when it waits for the person.</summary>
    private static async Task DecideAsync(TestBrowser b, Guid chat, string call, bool allow)
    {
        for (var i = 0; (await b.PostAsync($"/api/chat/conversations/{chat}/tool-calls/{call}", new { allow })).StatusCode != HttpStatusCode.NoContent; i++)
        {
            Assert.True(i < 100, "the call never waited for the person");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Deep_research_is_on_for_everyone_until_an_admin_chooses_and_only_those_allowed_may_ask_for_it()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, personId, _, _) = await PersonAsync(f);

        // As installed, or upgraded from before it was a tool (no setting saved): on, for everyone, on in new chats, asking first.
        var listed = (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == "research");
        Assert.Equal("Deep research", listed.GetProperty("title").GetString());
        Assert.Equal("telescope", listed.GetProperty("icon").GetString());
        var setting = listed.GetProperty("setting");
        Assert.True(setting.GetProperty("enabled").GetBoolean());
        Assert.Equal("Everyone", setting.GetProperty("audience").GetString());
        Assert.True(setting.GetProperty("onByDefault").GetBoolean());
        Assert.True(setting.GetProperty("askFirst").GetBoolean());
        var mine = await ResearchInConfigAsync(b);
        Assert.NotNull(mine);
        Assert.True(mine.Value.GetProperty("askFirst").GetBoolean());
        var chat = await NewChatAsync(b, new { tools = new[] { "calculator" } });
        // Off in this chat (the model starts none), the person still asks for it.
        Assert.NotEmpty(OfType(await EventsAsync(await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Compare the codecs", research = true })), "research"));

        // Off: not offered, and a message or a queued one asking for it is refused, saying why.
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetResearchAsync(admin, enabled: false));
        Assert.Null(await ResearchInConfigAsync(b));
        var refused = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Compare them again", research = true });
        await StatusAssert.Is(HttpStatusCode.Forbidden, refused);
        Assert.Contains("Deep research is not available to you", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.PostAsync($"/api/chat/conversations/{chat}/queue", new { content = "Compare them again", research = true }));
        // A plain message still goes.
        Assert.NotEmpty(OfType(await EventsAsync(await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Just answer" })), "done"));

        // For admins only: the person is refused, an admin is not.
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetResearchAsync(admin, audience: "Admins"));
        Assert.Null(await ResearchInConfigAsync(b));
        Assert.NotNull(await ResearchInConfigAsync(admin));

        // For a group: the person outside it, then in it.
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Analysts" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetResearchAsync(admin, audience: "Groups", groups: [group]));
        Assert.Null(await ResearchInConfigAsync(b));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Once more", research = true }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { personId } }));
        Assert.NotNull(await ResearchInConfigAsync(b));
        Assert.NotEmpty(OfType(await EventsAsync(await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Once more", research = true })), "research"));
        Assert.Contains((await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray(),
            e => e.GetProperty("action").GetString() == "tool.update" && e.GetProperty("target").GetString() == "research");
    }

    [Fact]
    public async Task A_message_queued_with_deep_research_is_a_plain_answer_once_it_is_taken_away()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, _, _) = await PersonAsync(f);
        var chat = await NewChatAsync(b, new { tools = Array.Empty<string>() });
        var marker = "queued-" + Guid.NewGuid().ToString("N")[..8];

        // An answer runs; a message with deep research waits behind it.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/chat/conversations/{chat}/messages", UriKind.Relative))
        {
            Content = JsonContent.Create(new { content = "Take your time [steady]" }),
        };
        var running = await b.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync($"/api/chat/conversations/{chat}/queue", new { content = $"{marker} [steady]", research = true }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetResearchAsync(admin, enabled: false));
        await running.Content.ReadAsStringAsync();

        // Its answer goes as a plain one: no research steps, and the page is told why.
        for (var i = 0; !app.Model.Requests.Any(r => r.Body["messages"]!.ToJsonString().Contains(marker, StringComparison.Ordinal)); i++)
        {
            Assert.True(i < 100, "the queued message never went");
            await Task.Delay(100);
        }
        var events = await EventsAsync(await b.GetAsync($"/api/chat/conversations/{chat}/stream"));
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "notice" && e.GetProperty("kind").GetString() == "research_off");
        var asked = app.Model.Requests.Select(r => r.Body).Last(r => r["messages"]!.ToJsonString().Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain("Deep research", asked["messages"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_starts_deep_research_when_the_person_allows_it_and_the_answer_becomes_one()
    {
        var (b, _, email, _) = await PersonAsync(app.Factory);
        var marker = "started-" + Guid.NewGuid().ToString("N")[..8];
        var script = JsonSerializer.Serialize(new[]
        {
            new object[] { new { name = ResearchTool.Function, arguments = new { question = "How do the codecs compare?" } } },
            [Delegate(marker, "Speed", "Cost")],
        });
        // A new chat has it on (on in new chats); the person allows the call.
        var chat = await NewChatAsync(b);
        var answering = b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = $"{marker} [script {script}]" });
        await DecideAsync(b, chat, "call_0_0", allow: true);
        var events = await EventsAsync(await answering);

        var approval = Assert.Single(OfType(events, "approval"));
        Assert.Equal("research", approval.GetProperty("tool").GetString());
        Assert.Equal("Deep research", approval.GetProperty("title").GetString());
        var results = OfType(events, "tool_result");
        Assert.Equal(2, results.Count);
        Assert.False(results[0].GetProperty("isError").GetBoolean());
        Assert.Contains("Deep research is on for this answer, on: How do the codecs compare?", results[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("call delegate once", results[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        // The page is told once it is allowed, so its line says which step the research is on.
        Assert.True(events.FindIndex(e => e.GetProperty("type").GetString() == "research") > events.FindIndex(e => e.GetProperty("type").GetString() == "approval"));
        // Its parts ran, as with the composer's switch.
        Assert.Equal(2, results[1].GetProperty("details").GetProperty("agents").GetArrayLength());

        // Offered before; after it, the sub-agents to delegate to, and not another research.
        var rounds = AnswerRequests(marker);
        Assert.Equal(3, rounds.Count);
        Assert.Contains(ResearchTool.Function, Functions(rounds[0]));
        Assert.Contains("delegate", Functions(rounds[1]));
        Assert.Contains("Deep research is on for this answer", rounds[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        // The parts are not offered deep research either.
        var parts = app.Model.Requests.Select(r => r.Body).Where(r => r["messages"]![0]!["content"]!.GetValue<string>().Contains("You are a sub-agent", StringComparison.Ordinal)
            && r["messages"]![1]!["content"]!.GetValue<string>().StartsWith(marker, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.DoesNotContain(ResearchTool.Function, Functions(p)));
        Assert.Equal(email, rounds[0]["user"]!.GetValue<string>());

        // Declined: the answer goes on without it, and nothing is researched.
        var other = await NewChatAsync(b);
        var declined = b.PostAsync($"/api/chat/conversations/{other}/messages", new { content = $"{marker}-no [script {script}]" });
        await DecideAsync(b, other, "call_0_0", allow: false);
        var no = await EventsAsync(await declined);
        Assert.True(OfType(no, "tool_result")[0].GetProperty("declined").GetBoolean());
        Assert.Empty(OfType(no, "research"));
    }

    [Fact]
    public async Task Deep_research_asked_for_is_not_offered_again_and_one_that_does_not_ask_runs_at_once_within_the_days_limit()
    {
        await using var f = NewApp(new() { ["Safeguards:ResearchPerDay"] = "2" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, _, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetResearchAsync(admin, askFirst: false));
        var marker = "limit-" + Guid.NewGuid().ToString("N")[..8];
        var script = JsonSerializer.Serialize(new[] { new object[] { new { name = ResearchTool.Function, arguments = new { question = "Codecs" } } } });

        // Asked for with the switch: the model is not offered to start another.
        var chat = await NewChatAsync(b);
        await EventsAsync(await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = $"{marker}-switch", research = true }));
        Assert.DoesNotContain(ResearchTool.Function, Functions(AnswerRequests($"{marker}-switch")[0]));

        // Not asking first: it starts at once, and counts.
        var started = await EventsAsync(await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = $"{marker}-one [script {script}]" }));
        Assert.Empty(OfType(started, "approval"));
        Assert.Single(OfType(started, "research"));
        // The day's two are used: the model is told, and the answer goes on.
        var third = await EventsAsync(await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = $"{marker}-two [script {script}]" }));
        var result = Assert.Single(OfType(third, "tool_result"));
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("2 deep research answers today", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Empty(OfType(third, "research"));
        Assert.Equal("done", third.Last().GetProperty("type").GetString());
    }

    [Fact]
    public async Task Scheduled_tasks_are_not_offered_deep_research_while_it_asks_first_nor_Compare_at_all()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("Other-Model", 32768, 4096, Vision: false, Tools: true, Thinking: true, null, null, null));
        await using var f = app.Create(app.ConnectionStringFor("research_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, _, _) = await PersonAsync(f);
        var marker = "unwatched-" + Guid.NewGuid().ToString("N")[..8];

        // In the person's own chat the model is offered it (on in new chats).
        await EventsAsync(await b.PostAsync($"/api/chat/conversations/{await NewChatAsync(b)}/messages", new { content = $"{marker}-web" }));
        Assert.Contains(ResearchTool.Function, Functions(Assert.Single(AnswerRequests($"{marker}-web"))));

        // A scheduled task's answer has nobody to press Allow: not offered while it asks first, and the run ends without waiting.
        var made = await b.PostAsync("/api/tasks", new { name = "Weekly research", prompt = $"{marker}-task", cron = "0 9 * * 1", timeZone = "Europe/Berlin" });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var task = (await b.JsonAsync(made)).GetProperty("id").GetGuid();
        async Task<JsonObject> RunTaskAsync(int count)
        {
            await StatusAssert.Is(HttpStatusCode.Accepted, await b.PostAsync($"/api/tasks/{task}/run"));
            for (var i = 0; ; i++)
            {
                var runs = (await b.JsonAsync(await b.GetAsync($"/api/tasks/{task}/runs"))).EnumerateArray().ToList();
                if (runs.Count(r => r.GetProperty("status").GetString() != "running") >= count)
                {
                    Assert.Equal("done", runs[0].GetProperty("status").GetString());
                    return AnswerRequests($"{marker}-task").Last();
                }
                Assert.True(i < 200, "the task's run never ended");
                await Task.Delay(100);
            }
        }
        var asked = Functions(await RunTaskAsync(1));
        Assert.NotEmpty(asked);
        Assert.DoesNotContain(ResearchTool.Function, asked);

        // Compare: neither model is offered it (deep research is one model's report).
        var compared = await b.PostAsync($"/api/chat/conversations/{await NewChatAsync(b)}/compare", new { content = $"{marker}-compare" });
        await StatusAssert.Is(HttpStatusCode.OK, compared);
        await compared.Content.ReadAsStringAsync();
        var both = AnswerRequests($"{marker}-compare");
        Assert.Equal(2, both.Select(r => r["model"]!.GetValue<string>()).Distinct().Count());
        Assert.All(both, r => Assert.DoesNotContain(ResearchTool.Function, Functions(r)));

        // Not asking first (an admin's choice): a task may start one, as the person could. Compare still not.
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetResearchAsync(admin, askFirst: false));
        Assert.Contains(ResearchTool.Function, Functions(await RunTaskAsync(2)));
        var again = await b.PostAsync($"/api/chat/conversations/{await NewChatAsync(b)}/compare", new { content = $"{marker}-again" });
        await again.Content.ReadAsStringAsync();
        Assert.Equal(2, AnswerRequests($"{marker}-again").Count);
        Assert.All(AnswerRequests($"{marker}-again"), r => Assert.DoesNotContain(ResearchTool.Function, Functions(r)));
    }

    [Fact]
    public async Task Arena_MCP_runs_deep_research_in_a_chat_of_the_persons_own_and_says_why_when_it_cannot()
    {
        await using var f = NewApp(new() { ["Auth:DataKey"] = "a-data-key-for-research-tests", ["Web:AllowedSites"] = "docs.example.test" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, email, key) = await PersonAsync(f);
        var endpoint = new Uri($"https://{AppFixture.Domain}/mcp");
        var agent = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        agent.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
        agent.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        agent.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        async Task<(JsonElement Answer, List<JsonElement> Notes)> RpcAsync(string method, object @params)
        {
            var res = await agent.PostAsync(endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params }));
            var body = await res.Content.ReadAsStringAsync();
            Assert.True(res.IsSuccessStatusCode, body);
            if (res.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                return (JsonDocument.Parse(body).RootElement, []);
            }
            var messages = body.Split("\n\n").Where(e => e.StartsWith("data: ", StringComparison.Ordinal)).Select(e => JsonDocument.Parse(e[6..]).RootElement).ToList();
            return (messages.Single(m => m.TryGetProperty("id", out _)), [.. messages.Where(m => !m.TryGetProperty("id", out _))]);
        }
        async Task<JsonElement?> ListedAsync() => (await RpcAsync("tools/list", new { })).Answer.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Cast<JsonElement?>().FirstOrDefault(t => t!.Value.GetProperty("name").GetString() == ResearchTool.Function);

        async Task<JsonElement> InfoAsync() => await b.JsonAsync(await b.GetAsync("/api/account/mcp"));

        // Served, asking first (the client asks the person, as the chat would), and Connect your tools says so.
        Assert.Contains((await InfoAsync()).GetProperty("tools").EnumerateArray(), t => t.GetProperty("id").GetString() == "research" && t.GetProperty("askFirst").GetBoolean());
        Assert.Empty((await InfoAsync()).GetProperty("notServed").EnumerateArray());
        var listed = await ListedAsync();
        Assert.NotNull(listed);
        Assert.True(listed.Value.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.Equal(["question"], listed.Value.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()));

        // A run: a chat of the person's own does the research; its steps come as progress, the report as the result.
        var marker = "mcp-" + Guid.NewGuid().ToString("N")[..8];
        var script = JsonSerializer.Serialize(new[] { new[] { Delegate(marker, "Speed", "Cost") } });
        var (answer, notes) = await RpcAsync("tools/call", new { name = ResearchTool.Function, arguments = new { question = $"{marker} [script {script}]" }, _meta = new { progressToken = "p1" } });
        var result = answer.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean(), result.ToString());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.StartsWith("Found it.", text, StringComparison.Ordinal);
        var said = notes.Select(n => n.GetProperty("params").GetProperty("message").GetString()).ToList();
        Assert.Contains("Planning the research", said);
        Assert.Contains("Researching its parts", said);
        Assert.Contains("Writing the report", said);
        var steps = notes.Select(n => n.GetProperty("params").GetProperty("progress").GetDouble()).ToList();
        Assert.Equal(steps.Order(), steps);
        var chats = (await b.JsonAsync(await b.GetAsync("/api/chat/conversations"))).EnumerateArray().ToList();
        var made = chats.Single(c => c.GetProperty("title").GetString()!.StartsWith("Deep research: " + marker, StringComparison.Ordinal));
        Assert.Contains($"https://{AppFixture.Domain}/chat/{made.GetProperty("id").GetGuid()}", text, StringComparison.Ordinal);
        var kept = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{made.GetProperty("id").GetGuid()}"));
        Assert.Contains(kept.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "tool" && m.GetProperty("toolName").GetString() == "delegate");
        Assert.Equal(email, AnswerRequests(marker)[0]["user"]!.GetValue<string>());
        Assert.Contains((await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray(),
            e => e.GetProperty("action").GetString() == "mcp.call" && e.GetProperty("target").GetString() == "research" && e.GetProperty("success").GetBoolean());

        // The web asks before each call: nobody is in an agent's research to allow it, so it is not served, and says why.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/web", UriKind.Relative),
            new { enabled = true, audience = "Everyone", groups = Array.Empty<Guid>(), onByDefault = true, askFirst = true }));
        var init = (await RpcAsync("initialize", new { protocolVersion = "2025-06-18" })).Answer.GetProperty("result").GetProperty("instructions").GetString()!;
        Assert.Contains("Deep research (the web asks before each call", init, StringComparison.Ordinal);
        Assert.Null(await ListedAsync());
        var why = (await RpcAsync("tools/call", new { name = ResearchTool.Function, arguments = new { question = "Codecs" } })).Answer.GetProperty("result");
        Assert.True(why.GetProperty("isError").GetBoolean());
        Assert.Contains("start deep research in the chat instead", why.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        // Connect your tools does not list it as served, and says why.
        var info = await InfoAsync();
        Assert.DoesNotContain(info.GetProperty("tools").EnumerateArray(), t => t.GetProperty("id").GetString() == "research");
        Assert.Contains(info.GetProperty("tools").EnumerateArray(), t => t.GetProperty("id").GetString() == "web");
        var notServed = Assert.Single(info.GetProperty("notServed").EnumerateArray());
        Assert.Equal("Deep research", notServed.GetProperty("title").GetString());
        Assert.Contains("start deep research in the chat instead", notServed.GetProperty("why").GetString(), StringComparison.Ordinal);
        // A name no tool has is still that, not deep research's refusal.
        var unknown = (await RpcAsync("tools/call", new { name = "fnd_symbol", arguments = new { name = "Parser" } })).Answer;
        Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("fnd_symbol", unknown.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        // Taken away from the person: not there at all.
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetResearchAsync(admin, audience: "Admins"));
        await RpcAsync("initialize", new { protocolVersion = "2025-06-18" });
        Assert.Null(await ListedAsync());
        Assert.Equal(-32602, (await RpcAsync("tools/call", new { name = ResearchTool.Function, arguments = new { question = "Codecs" } })).Answer.GetProperty("error").GetProperty("code").GetInt32());
    }
}
