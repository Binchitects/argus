using System.Net;
using System.Text.Json;
using Llm.Api.Chat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Answer traces (Admin → Traces): where an answer's time went, step by step, for admins only, and never what was said.</summary>
[Collection(nameof(AppCollection))]
public sealed class AnswerTraceTests(AppFixture app)
{
    private static async Task<(TestBrowser Browser, string UserName)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "tr" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), name);
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object body) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", body))).GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text) =>
        [.. (await (await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text })).Content.ReadAsStringAsync())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    private static Guid Done(List<JsonElement> events) => events.Last(e => e.GetProperty("type").GetString() == "done").GetProperty("id").GetGuid();

    [Fact]
    public async Task An_answer_with_tool_calls_and_sub_agents_has_a_trace_that_names_its_slowest_step_and_keeps_its_words_out()
    {
        var (b, userName) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { tools = new[] { "agents", "calculator" } });
        var parts = new
        {
            tasks = new[]
            {
                new { title = "zebra-sum", instructions = """Work zebra out: [call calculate {"expression":"2+2"}]""" },
                new { title = "zebra-essay", instructions = "Write the zebra essay slowly [steady]" },
            },
        };
        var events = await SendAsync(b, id, $"zebra question [timed] Split it: [call delegate {JsonSerializer.Serialize(parts)}]");
        var answer = Done(events);

        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await admin.GetAsync($"/api/admin/traces/{answer}");
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var raw = await res.Content.ReadAsStringAsync();
        var trace = JsonDocument.Parse(raw).RootElement;

        // The timeline: getting ready, the round that called delegate, the sub-agents, the round that answered.
        var steps = trace.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["setup", "round", "agents", "round"], steps.Select(s => s.GetProperty("kind").GetString()));
        Assert.Equal(2, trace.GetProperty("rounds").GetInt32());
        Assert.Equal(1, trace.GetProperty("toolCalls").GetInt32());
        Assert.Equal(userName, trace.GetProperty("person").GetProperty("userName").GetString());

        // Each round with its tokens, the cache share, its first token, and the engine's own speeds (llama.cpp's timings).
        var round = steps[1];
        Assert.Equal(100, round.GetProperty("tokens").GetProperty("prompt").GetInt64());
        Assert.Equal(0.4, round.GetProperty("tokens").GetProperty("cacheShare").GetDouble());
        Assert.Equal("engine", round.GetProperty("speedFrom").GetString());
        Assert.Equal(200, round.GetProperty("readPerSecond").GetDouble());
        Assert.Equal(10, round.GetProperty("writePerSecond").GetDouble());
        Assert.True(round.GetProperty("firstTokenMs").GetInt32() >= 0);
        Assert.Equal(200, trace.GetProperty("tokens").GetProperty("prompt").GetInt64());
        Assert.Equal(80, trace.GetProperty("tokens").GetProperty("cached").GetInt64());
        Assert.Equal(24, trace.GetProperty("tokens").GetProperty("completion").GetInt64());

        // The prompt by part, in characters and tokens.
        var prompt = trace.GetProperty("prompt").EnumerateArray().ToList();
        Assert.Contains(prompt, p => p.GetProperty("kind").GetString() == "system" && p.GetProperty("tokens").GetInt32() > 0);
        Assert.Contains(prompt, p => p.GetProperty("kind").GetString() == "you");

        // Each sub-agent with its time and tokens, its tool calls timed, and the speeds from the clock (no timings came).
        var agents = steps[2].GetProperty("agents").EnumerateArray().ToList();
        Assert.Equal(2, agents.Count);
        Assert.Equal("calculate", agents[0].GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.True(agents[0].GetProperty("steps")[0].GetProperty("ms").GetInt32() >= 0);
        Assert.Equal(200, agents[0].GetProperty("tokens").GetProperty("prompt").GetInt64());
        Assert.Equal("clock", agents[1].GetProperty("speedFrom").GetString());
        Assert.True(agents[1].GetProperty("ms").GetInt32() >= 1400, agents[1].ToString());
        Assert.True(agents[1].GetProperty("modelMs").GetInt32() >= 1400, agents[1].ToString());
        Assert.Equal(300, trace.GetProperty("agentTokens").GetProperty("prompt").GetInt64());

        // The slowest step is named: the sub-agent that wrote slowly, mostly the model's time.
        var slowest = trace.GetProperty("slowest");
        Assert.Equal("agent", slowest.GetProperty("kind").GetString());
        Assert.Equal("Sub-agent 2", slowest.GetProperty("label").GetString());
        Assert.StartsWith("Mostly the model", slowest.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(agents[1].GetProperty("slowest").GetBoolean());
        Assert.True(trace.GetProperty("ms").GetInt32() >= agents[1].GetProperty("ms").GetInt32());

        // Any message of the answer finds it; a question is not an answer.
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        var first = messages.First(m => m.GetProperty("role").GetString() == "assistant").GetProperty("id").GetGuid();
        Assert.Equal(answer, (await admin.JsonAsync(await admin.GetAsync($"/api/admin/traces/{first}"))).GetProperty("id").GetGuid());
        var question = messages.First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/admin/traces/{question}"));

        // Among the slowest answers of the day, with who asked and the tools it used (its sub-agents' too).
        var list = await admin.JsonAsync(await admin.GetAsync($"/api/admin/traces?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}&limit=200"));
        var listed = list.GetProperty("answers").EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == answer);
        Assert.Equal(userName, listed.GetProperty("person").GetProperty("userName").GetString());
        Assert.Equal("Sub-agent 2", listed.GetProperty("slowest").GetProperty("label").GetString());
        Assert.Contains(listed.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "calculate" && t.GetProperty("count").GetInt32() == 1);
        Assert.Contains(listed.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "delegate");

        // No words anywhere: not the question, the parts, the sub-agents' work, nor the answer.
        foreach (var json in new[] { raw, listed.ToString() })
        {
            foreach (var said in new[] { "zebra", "Found it", "s0 s1", "2+2", "Answer to" })
            {
                Assert.DoesNotContain(said, json, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Only_admins_see_traces()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { });
        var answer = Done(await SendAsync(b, id, "Hello there"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.GetAsync($"/api/admin/traces/{answer}"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.GetAsync("/api/admin/traces"));
        var anonymous = new TestBrowser(app.Factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/admin/traces/{answer}")).StatusCode);
    }

    [Fact]
    public async Task Time_in_line_is_on_the_trace_and_named_when_it_was_the_slowest()
    {
        await using var f = app.Create(app.ConnectionStringFor("traces_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Chat:AnswersPerPerson"] = "1", ["Chat:AnswersAtOnce"] = "4", ["Auth:DataKey"] = "a-data-key-for-trace-tests" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // A tool whose server is slow to answer holds the first answer (in its place) as long as the test wants.
        var made = await admin.PostAsync("/api/admin/tools/servers", new { name = "Slow Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey });
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        var (b, _) = await PersonAsync(f);
        var (one, two) = (await NewChatAsync(b, new { tools = new[] { toolId } }), await NewChatAsync(b, new { tools = Array.Empty<string>() }));

        app.Mcp.Hold = new TaskCompletionSource();
        try
        {
            int Initializes() { lock (app.Mcp.Calls) { return app.Mcp.Calls.Count(c => c.Method == "initialize"); } }
            var before = Initializes();
            var first = SendAsync(b, one, "hello");
            for (var i = 0; i < 100 && Initializes() == before; i++)
            {
                await Task.Delay(50);
            }
            var gate = f.Services.GetRequiredService<AnswerGate>();
            var second = SendAsync(b, two, "hello again");
            for (var i = 0; i < 100 && gate.Now() != (1, 1); i++)
            {
                await Task.Delay(50);
            }
            await Task.Delay(700);
            app.Mcp.Hold.SetResult();
            var held = Done(await first.WaitAsync(TimeSpan.FromSeconds(20)));
            var waited = Done(await second.WaitAsync(TimeSpan.FromSeconds(20)));

            var trace = await admin.JsonAsync(await admin.GetAsync($"/api/admin/traces/{waited}"));
            var queue = trace.GetProperty("steps")[0];
            Assert.Equal("queue", queue.GetProperty("kind").GetString());
            Assert.True(queue.GetProperty("ms").GetInt32() >= 600, queue.ToString());
            Assert.Equal("Waiting in line", trace.GetProperty("slowest").GetProperty("label").GetString());
            Assert.True(trace.GetProperty("ms").GetInt32() >= queue.GetProperty("ms").GetInt32());

            // The held one spent its time getting ready (its tool's server was slow to start).
            var slow = await admin.JsonAsync(await admin.GetAsync($"/api/admin/traces/{held}"));
            Assert.Equal("Getting ready", slow.GetProperty("slowest").GetProperty("label").GetString());
        }
        finally
        {
            app.Mcp.Hold?.TrySetResult();
            app.Mcp.Hold = null;
        }
    }

    [Fact]
    public void A_round_without_the_engines_timings_takes_its_speeds_from_the_clock()
    {
        var total = new TimingTotal();
        var call = new ModelTiming();
        call.Saw(new ContentDelta("Hi"));
        Thread.Sleep(30);
        total.Add(call, prompt: 1_000, cached: 400, completion: 12);
        var json = total.ToJson();
        Assert.Equal("clock", json["speedFrom"]!.GetValue<string>());
        Assert.NotNull(json["readPerSecond"]);
        Assert.True(json["writePerSecond"]!.GetValue<double>() > 0);

        var measured = new TimingTotal();
        var timed = new ModelTiming();
        timed.Saw(new EngineTimings(600, 3_000, 50, 5_000));
        measured.Add(timed, 1_000, 400, 50);
        Assert.Equal(200, measured.ToJson()["readPerSecond"]!.GetValue<double>());
        Assert.Equal(10, measured.ToJson()["writePerSecond"]!.GetValue<double>());
    }
}
