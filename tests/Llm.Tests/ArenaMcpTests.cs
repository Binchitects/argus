using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Arena MCP: each person's chat tools for outside agents at /mcp, signed in with their own API key.</summary>
[Collection(nameof(AppCollection))]
public sealed class ArenaMcpTests(AppFixture app)
{
    private static readonly Uri Endpoint = new($"https://{AppFixture.Domain}/mcp");

    /// <summary>Its own app and database: tool settings and keys must not leak into other tests.</summary>
    private WebApplicationFactory<Program> NewApp(FakeGateway? gateway = null, Dictionary<string, string?>? settings = null) =>
        app.Create(app.ConnectionStringFor("mcp_" + Guid.NewGuid().ToString("N")[..8]), gateway ?? new FakeGateway(),
            new Dictionary<string, string?>(settings ?? []) { ["Auth:DataKey"] = "a-data-key-for-mcp-tests" });

    private static async Task<(TestBrowser Browser, Guid Id, string Email, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var browser = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (browser, made.GetProperty("id").GetGuid(), $"{name}@example.test", made.GetProperty("apiKey").GetString()!);
    }

    /// <summary>An outside agent: no cookies, the key (if any) in Authorization, both answer kinds accepted, the protocol it agreed on.</summary>
    private static HttpClient Agent(WebApplicationFactory<Program> f, string? key, string? protocol = "2025-06-18")
    {
        var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        if (key is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        if (protocol is not null)
        {
            http.DefaultRequestHeaders.Add("MCP-Protocol-Version", protocol);
        }
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        return http;
    }

    /// <summary>The app's own MCP client (the one the chat uses for admins' servers), as an agent would connect.</summary>
    private static Task<McpSession> ConnectAsync(WebApplicationFactory<Program> f, string key) =>
        Mcp.ConnectAsync(Agent(f, null, protocol: null), Endpoint, new Dictionary<string, string> { ["Authorization"] = "Bearer " + key }, "Arena", CancellationToken.None);

    /// <summary>One JSON-RPC request; its answer read from JSON or from the events, with the notifications before it.</summary>
    private static async Task<(HttpResponseMessage Response, JsonElement Answer, List<JsonElement> Notes)> RpcAsync(HttpClient http, string method, object? @params = null, object? id = null)
    {
        var res = await http.PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = id ?? 1, method, @params = @params ?? new { } }));
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{method}: {(int)res.StatusCode} {body}");
        if (res.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            return (res, JsonDocument.Parse(body).RootElement, []);
        }
        var messages = body.Split("\n\n").Where(e => e.StartsWith("data: ", StringComparison.Ordinal)).Select(e => JsonDocument.Parse(e[6..]).RootElement).ToList();
        return (res, messages.Single(m => m.TryGetProperty("id", out _)), [.. messages.Where(m => !m.TryGetProperty("id", out _))]);
    }

    private static List<string> Names(JsonArray tools) => [.. tools.Select(t => t!["name"]!.GetValue<string>())];

    private static async Task<List<JsonElement>> AuditAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        return [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()];
    }

    private static Task<HttpResponseMessage> SetToolAsync(TestBrowser admin, string tool, object setting) =>
        admin.Http.PutAsJsonAsync(new Uri($"/api/admin/tools/{tool}", UriKind.Relative), setting);

    [Fact]
    public async Task An_agent_signs_in_with_the_persons_key_gets_their_chat_tools_and_each_call_is_audited()
    {
        await using var f = NewApp();
        var (_, _, email, key) = await PersonAsync(f);

        var session = await ConnectAsync(f, key);
        Assert.Contains(email, session.Instructions, StringComparison.Ordinal);
        // The tools' own notes come with it: Argus's, as its server says them.
        Assert.Contains(FakeArgus.Instructions, session.Instructions, StringComparison.Ordinal);
        var tools = await session.ToolsAsync(CancellationToken.None);
        var names = Names(tools);
        Assert.Contains("calculate", names);
        Assert.Contains("current_time", names);
        Assert.Contains("days_between", names);
        Assert.Contains("find_symbol", names);
        // The chat's own: its files, questions to the person, sub-agents.
        Assert.DoesNotContain("read_file", names);
        Assert.DoesNotContain("ask_user", names);
        Assert.DoesNotContain("delegate", names);
        // The same schema as the chat's, as MCP lists it.
        var calculate = tools.Single(t => t!["name"]!.GetValue<string>() == "calculate")!;
        Assert.Equal("object", calculate["inputSchema"]!["type"]!.GetValue<string>());
        Assert.Equal(["expression"], calculate["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Null(calculate["annotations"]);

        var (text, isError) = await session.CallAsync("calculate", new JsonObject { ["expression"] = "2^10 + 4" }, null, CancellationToken.None);
        Assert.False(isError);
        Assert.Equal("1028", JsonNode.Parse(text)!["result"]!.GetValue<string>());
        (text, isError) = await session.CallAsync("calculate", new JsonObject { ["expression"] = "2 +* 3" }, null, CancellationToken.None);
        Assert.True(isError);

        var audit = await AuditAsync(f);
        var calls = audit.Where(e => e.GetProperty("action").GetString() == "mcp.call").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, e => Assert.Equal("calculator", e.GetProperty("target").GetString()));
        Assert.All(calls, e => Assert.Equal("calculate", e.GetProperty("detail").GetString()));
        Assert.All(calls, e => Assert.Equal(email.Split('@')[0], e.GetProperty("actor").GetString()));
        Assert.Equal([false, true], calls.Select(e => e.GetProperty("success").GetBoolean()));

        // The protocol around it: versions, a session label, ping, notifications, what is not served.
        var agent = Agent(f, key);
        var (res, init, _) = await RpcAsync(agent, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());
        // The model a new chat of theirs starts with, for an agent that has none of its own.
        Assert.Equal("Qwen3.8-Flash-Next", init.GetProperty("result").GetProperty("_meta").GetProperty("arena/defaultModel").GetString());
        Assert.Equal("arena", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(init.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.True(res.Headers.Contains("Mcp-Session-Id"));
        Assert.Equal("2024-11-05", (await RpcAsync(agent, "initialize", new { protocolVersion = "2024-11-05" })).Answer.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("2025-06-18", (await RpcAsync(agent, "initialize", new { protocolVersion = "1999-01-01" })).Answer.GetProperty("result").GetProperty("protocolVersion").GetString());
        var ping = (await RpcAsync(agent, "ping", id: "p-1")).Answer;
        Assert.Equal("p-1", ping.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, ping.GetProperty("result").ValueKind);
        Assert.Equal(-32601, (await RpcAsync(agent, "sampling/createMessage")).Answer.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, (await RpcAsync(agent, "tools/call", new { name = "launch_rockets", arguments = new { } })).Answer.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Array, (await RpcAsync(agent, "prompts/list")).Answer.GetProperty("result").GetProperty("prompts").ValueKind);
        await StatusAssert.Is(HttpStatusCode.Accepted, await agent.PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", method = "notifications/initialized" })));
        // A batch, as 2025-03-26 allows: the requests answered together, the notification not.
        var batch = await agent.PostAsync(Endpoint, JsonContent.Create(new object[]
        {
            new { jsonrpc = "2.0", id = 1, method = "ping" },
            new { jsonrpc = "2.0", method = "notifications/initialized" },
            new { jsonrpc = "2.0", id = 2, method = "tools/list" },
        }));
        var answers = JsonDocument.Parse(await batch.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        Assert.Equal([1, 2], answers.Select(a => a.GetProperty("id").GetInt32()));
        Assert.Contains("calculate", answers[1].GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await agent.PostAsync(Endpoint, new StringContent("{not json", System.Text.Encoding.UTF8, "application/json")));
        await StatusAssert.Is(HttpStatusCode.MethodNotAllowed, await agent.GetAsync(Endpoint));
        using var future = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 9, method = "ping" }) };
        future.Headers.Add("MCP-Protocol-Version", "2099-01-01");
        await StatusAssert.Is(HttpStatusCode.BadRequest, await agent.SendAsync(future));

        // Where clients find it.
        var found = JsonDocument.Parse(await (await agent.GetAsync(new Uri("/.well-known/mcp", UriKind.Relative))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https://llm.test/mcp", found.GetProperty("url").GetString());
        Assert.Equal("bearer", found.GetProperty("authentication").GetProperty("type").GetString());
    }

    [Fact]
    public async Task No_key_a_wrong_key_a_blocked_key_and_a_disabled_person_are_all_refused()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var (_, personId, _, key) = await PersonAsync(f);

        var nobody = await Agent(f, null).PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, nobody);
        Assert.Equal("Bearer", nobody.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Contains("API key", JsonDocument.Parse(await nobody.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        var lookups = gateway.KeyLookups.Count;
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Agent(f, "not-a-key").PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" })));
        Assert.Equal(lookups, gateway.KeyLookups.Count);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Agent(f, "sk-not-anyones").PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" })));
        // The app's own client says why.
        var refused = await Assert.ThrowsAsync<McpException>(() => ConnectAsync(f, "sk-not-anyones"));
        Assert.Contains("not known here", refused.Message, StringComparison.Ordinal);

        // The key works, and the gateway is asked once for a while, not on every call.
        lookups = gateway.KeyLookups.Count;
        var agent = Agent(f, key);
        await RpcAsync(agent, "ping");
        await RpcAsync(agent, "ping");
        Assert.Equal(lookups + 1, gateway.KeyLookups.Count);

        // A blocked key (another person's, not asked about yet) is refused.
        var (_, _, otherEmail, otherKey) = await PersonAsync(f);
        gateway.KeysOf(otherEmail).Single().Blocked = true;
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Agent(f, otherKey).PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" })));

        // A disabled person, at once, even with their key remembered.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{personId}", UriKind.Relative), new { disabled = true }));
        var disabled = await agent.PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, disabled);
        Assert.Contains("disabled", await disabled.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The gateway down: nobody can be checked, and the agent hears it is for now.
        var (_, _, _, freshKey) = await PersonAsync(f);
        gateway.Down = true;
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await Agent(f, freshKey).PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" })));
        gateway.Down = false;
    }

    [Fact]
    public async Task Turned_off_there_is_no_server_and_the_page_says_so()
    {
        await using var f = NewApp(settings: new() { ["Mcp:Enabled"] = "false" });
        var (b, _, _, key) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await Agent(f, key).PostAsync(Endpoint, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" })));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync("/.well-known/mcp"));
        var info = await b.JsonAsync(await b.GetAsync("/api/account/mcp"));
        Assert.False(info.GetProperty("enabled").GetBoolean());
        Assert.Empty(info.GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task The_persons_prompts_and_the_companys_are_served_filled_in_and_nobody_elses()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, _, key) = await PersonAsync(f);
        var (_, _, _, otherKey) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/prompts", new { name = "review", title = "Review code", text = "Review {{file}} for {{ focus }}." }));
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/prompts", new { name = "standup", title = "Stand-up notes", text = "Write my stand-up.", sharing = "Company" }));
        // The company's /review too: the person's own comes first, once.
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/prompts", new { name = "review", title = "The company's review", text = "Review it.", sharing = "Company" }));

        var agent = Agent(f, key);
        var listed = (await RpcAsync(agent, "prompts/list")).Answer.GetProperty("result").GetProperty("prompts").EnumerateArray().ToList();
        Assert.Equal(["review", "standup"], listed.Select(p => p.GetProperty("name").GetString()));
        var review = listed[0];
        Assert.Equal("Review code", review.GetProperty("title").GetString());
        Assert.Equal(["file", "focus"], review.GetProperty("arguments").EnumerateArray().Select(a => a.GetProperty("name").GetString()));

        var got = (await RpcAsync(agent, "prompts/get", new { name = "review", arguments = new { file = "Parser.cs", focus = "error handling" } })).Answer.GetProperty("result");
        var message = Assert.Single(got.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Review Parser.cs for error handling.", message.GetProperty("content").GetProperty("text").GetString());

        // Someone else gets the company's /review, never the person's own.
        var theirs = (await RpcAsync(Agent(f, otherKey), "prompts/get", new { name = "review", arguments = new { } })).Answer.GetProperty("result");
        Assert.Equal("Review it.", theirs.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
        Assert.True((await RpcAsync(Agent(f, otherKey), "prompts/get", new { name = "nothing" })).Answer.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task A_person_gets_only_the_tools_they_may_use_and_a_tool_that_asks_first_says_so()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, _, key) = await PersonAsync(f);
        var (_, insiderId, _, insiderKey) = await PersonAsync(f);
        // The calculator for one group, the person outside it; the clock asks first.
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Numbers" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { insiderId } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetToolAsync(admin, "calculator", new { enabled = true, audience = "Groups", groups = new[] { group }, onByDefault = true, askFirst = false }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await SetToolAsync(admin, "time", new { enabled = true, audience = "Everyone", onByDefault = false, askFirst = true }));

        var tools = await (await ConnectAsync(f, key)).ToolsAsync(CancellationToken.None);
        Assert.DoesNotContain("calculate", Names(tools));
        var (_, refused, _) = await RpcAsync(Agent(f, key), "tools/call", new { name = "calculate", arguments = new { expression = "1+1" } });
        Assert.Contains("no tool named calculate", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("calculate", Names(await (await ConnectAsync(f, insiderKey)).ToolsAsync(CancellationToken.None)));

        // Off in new chats is still the person's: served. Asks first: marked, and the client is told to ask.
        var clock = tools.Single(t => t!["name"]!.GetValue<string>() == "current_time")!;
        Assert.True(clock["annotations"]!["destructiveHint"]!.GetValue<bool>());
        Assert.False(clock["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Contains("Ask the person before each call", clock["description"]!.GetValue<string>(), StringComparison.Ordinal);

        // The setup page lists what is served to them.
        var info = await b.JsonAsync(await b.GetAsync("/api/account/mcp"));
        Assert.True(info.GetProperty("enabled").GetBoolean());
        Assert.Equal("https://llm.test/mcp", info.GetProperty("url").GetString());
        var listed = info.GetProperty("tools").EnumerateArray().ToList();
        Assert.DoesNotContain(listed, t => t.GetProperty("id").GetString() == "calculator");
        Assert.True(listed.Single(t => t.GetProperty("id").GetString() == "time").GetProperty("askFirst").GetBoolean());
        Assert.DoesNotContain(listed, t => t.GetProperty("id").GetString() is "files" or "ask" or "agents");
    }

    [Fact]
    public async Task An_Argus_call_goes_as_the_person_whose_key_it_is()
    {
        await using var f = NewApp();
        var (_, _, email, key) = await PersonAsync(f);
        var session = await ConnectAsync(f, key);
        var (text, isError) = await session.CallAsync("find_symbol", new JsonObject { ["name"] = "ParseHeader" }, null, CancellationToken.None);
        Assert.False(isError);
        Assert.Contains("src/parse.c", text, StringComparison.Ordinal);
        // Argus was asked as this person, with the chat's token: no GitLab token of theirs needed.
        Assert.Contains(app.Argus.McpCalls, c => c.Method == "tools/call" && c.Email == email);
        Assert.Contains(await AuditAsync(f), e => e.GetProperty("action").GetString() == "mcp.call" && e.GetProperty("target").GetString() == "argus"
            && e.GetProperty("detail").GetString() == "find_symbol");
    }

    [Fact]
    public async Task An_admins_MCP_server_is_proxied_with_its_progress()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created,
            await admin.PostAsync("/api/admin/tools/servers", new { name = "Echo Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey }));
        var (_, _, _, key) = await PersonAsync(f);

        var session = await ConnectAsync(f, key);
        Assert.Contains("echo_desk__echo", Names(await session.ToolsAsync(CancellationToken.None)));
        var heard = new List<McpProgress>();
        var (text, isError) = await session.CallAsync("echo_desk__echo", new JsonObject { ["text"] = "slow" }, p =>
        {
            heard.Add(p);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.False(isError);
        Assert.Equal("echo: slow", text);
        // The server's progress, passed on to the agent as it came.
        var said = Assert.Single(heard);
        Assert.Equal("Warming up", said.Message);
        Assert.Equal(2, said.Total);
        // The server got the call with its own name and its key.
        Assert.Contains(app.Mcp.Calls, c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "echo"
            && c.Params?.GetProperty("arguments").GetProperty("text").GetString() == "slow");
        Assert.Contains(await AuditAsync(f), e => e.GetProperty("action").GetString() == "mcp.call" && e.GetProperty("target").GetString() == "Echo Desk"
            && e.GetProperty("detail").GetString() == "echo_desk__echo" && e.GetProperty("success").GetBoolean());

        // Called straight away, with no list kept (another agent, or a minute later), it is found by its prefix.
        var (_, _, _, otherKey) = await PersonAsync(f);
        var (_, answer, _) = await RpcAsync(Agent(f, otherKey), "tools/call", new { name = "echo_desk__echo", arguments = new { text = "hi" } });
        Assert.Equal("echo: hi", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_client_cancels_a_running_call_and_it_stops()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created,
            await admin.PostAsync("/api/admin/tools/servers", new { name = "Hold Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey }));
        var (_, _, _, key) = await PersonAsync(f);
        var agent = Agent(f, key);
        agent.DefaultRequestHeaders.Add("Mcp-Session-Id", "session-a");
        int Opened()
        {
            lock (app.Mcp.Calls)
            {
                return app.Mcp.Calls.Count(c => c.Method == "initialize");
            }
        }
        var hold = new TaskCompletionSource();
        var opened = Opened();
        app.Mcp.Hold = hold;
        try
        {
            // The server is slow to open: the call waits on it.
            var call = RpcAsync(agent, "tools/call", new { name = "hold_desk__echo", arguments = new { text = "hi" } }, id: 7);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Opened() == opened && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            await StatusAssert.Is(HttpStatusCode.Accepted, await agent.PostAsync(Endpoint,
                JsonContent.Create(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 7, reason = "The person pressed stop." } })));
            var result = (await call).Answer.GetProperty("result");
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains("cancelled", result.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            app.Mcp.Hold = null;
            hold.TrySetResult();
        }
    }

    [Fact]
    public async Task A_picture_comes_back_inline_and_as_a_link_the_agent_reads_and_the_person_opens()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var f = NewApp(gateway);
        var (b, _, email, key) = await PersonAsync(f);
        var agent = Agent(f, key);

        var before = app.Model.ImageRequests.Count;
        var (res, answer, _) = await RpcAsync(agent, "tools/call", new { name = "generate_image", arguments = new { prompt = "A red fox in the snow" } });
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        // Drawn on the person's credit.
        Assert.Equal(email, app.Model.ImageRequests.Skip(before).Single()["user"]!.GetValue<string>());
        var content = answer.GetProperty("result").GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["text", "image", "resource_link"], content.Select(c => c.GetProperty("type").GetString()));
        Assert.Equal(FakeModel.Png, Convert.FromBase64String(content[1].GetProperty("data").GetString()!));
        Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
        var link = content[2].GetProperty("uri").GetString()!;
        Assert.StartsWith("https://llm.test/api/chat/attachments/", link, StringComparison.Ordinal);
        Assert.Equal("a-red-fox-in-the-snow.png", content[2].GetProperty("name").GetString());

        // The agent reads it with its key; the person opens it signed in; nobody else does.
        var read = (await RpcAsync(agent, "resources/read", new { uri = link })).Answer.GetProperty("result").GetProperty("contents")[0];
        Assert.Equal(FakeModel.Png, Convert.FromBase64String(read.GetProperty("blob").GetString()!));
        var opened = await b.GetAsync(new Uri(link).AbsolutePath);
        await StatusAssert.Is(HttpStatusCode.OK, opened);
        Assert.Equal(FakeModel.Png, await opened.Content.ReadAsByteArrayAsync());
        var (_, _, _, otherKey) = await PersonAsync(f);
        Assert.Equal(-32002, (await RpcAsync(Agent(f, otherKey), "resources/read", new { uri = link })).Answer.GetProperty("error").GetProperty("code").GetInt32());

        // A client of 2025-03-26 knows no links: the address is in the text.
        var parts = (await RpcAsync(Agent(f, key, "2025-03-26"), "tools/call", new { name = "generate_image", arguments = new { prompt = "A fox" } })).Answer
            .GetProperty("result").GetProperty("content");
        Assert.Equal(["text", "image"], parts.EnumerateArray().Select(c => c.GetProperty("type").GetString()));
        Assert.Contains("https://llm.test/api/chat/attachments/", parts[0].GetProperty("text").GetString(), StringComparison.Ordinal);
    }
}
