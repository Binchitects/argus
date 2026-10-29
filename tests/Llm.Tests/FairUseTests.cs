using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>Fair use of a model that serves few at once: a limit per person, a line served in turn, and API keys capped too.</summary>
[Collection(nameof(AppCollection))]
public sealed class FairUseTests(AppFixture app)
{
    private static AnswerGate Gate(int perPerson, int atOnce, int queueLength = 0) =>
        new(new StaticOptions(new ChatOptions { AnswersPerPerson = perPerson, AnswersAtOnce = atOnce, QueueLength = queueLength }), TimeProvider.System);

    private sealed class StaticOptions(ChatOptions value) : IOptionsMonitor<ChatOptions>
    {
        public ChatOptions CurrentValue => value;
        public ChatOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ChatOptions, string?> listener) => null;
    }

    [Fact]
    public async Task A_free_place_goes_to_whoever_has_had_least_not_to_whoever_asks_most()
    {
        var gate = Gate(perPerson: 1, atOnce: 2);
        var (alice, bob, carol) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Task Nothing(AnswerGate.Line _) => Task.CompletedTask;
        var a1 = await gate.EnterAsync(alice, "m", Nothing, default);
        // Alice's second answer waits for her first, even with a place free.
        var a2 = gate.EnterAsync(alice, "m", Nothing, default);
        var b1 = await gate.EnterAsync(bob, "m", Nothing, default);
        Assert.False(a2.IsCompleted);
        var c1 = gate.EnterAsync(carol, "m", Nothing, default);
        Assert.Equal((2, 2), gate.Now());

        // Alice's first ends: Carol, who has had nothing yet, goes before Alice's second.
        a1.Dispose();
        using (await c1.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(a2.IsCompleted);
            b1.Dispose();
            using (await a2.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.Equal((2, 0), gate.Now());
            }
        }
        Assert.Equal((0, 0), gate.Now());
    }

    [Fact]
    public async Task Each_person_has_their_own_requests_at_once_and_0_is_no_limit()
    {
        var gate = Gate(perPerson: 1, atOnce: 0);
        var (ann, ben, cy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Task Nothing(AnswerGate.Line _) => Task.CompletedTask;
        // Ann may have 2 (hers, or her group's); Ben the company's 1; Cy no limit.
        using var a1 = await gate.EnterAsync(ann, 0, "m", Nothing, default, atOnce: 2);
        using var a2 = await gate.EnterAsync(ann, 0, "m", Nothing, default, atOnce: 2);
        var a3 = gate.EnterAsync(ann, 0, "m", Nothing, default, atOnce: 2);
        using var b1 = await gate.EnterAsync(ben, "m", Nothing, default);
        var b2 = gate.EnterAsync(ben, "m", Nothing, default);
        var cs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => gate.EnterAsync(cy, 0, "m", Nothing, default, atOnce: 0)));
        Assert.False(a3.IsCompleted);
        Assert.False(b2.IsCompleted);
        Assert.Equal((8, 2), gate.Now());
        a1.Dispose();
        (await a3.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        foreach (var c in cs)
        {
            c.Dispose();
        }
        b1.Dispose();
        (await b2.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Past_the_longest_line_a_request_is_refused_at_once_as_busy()
    {
        var gate = Gate(perPerson: 1, atOnce: 1, queueLength: 1);
        Task Nothing(AnswerGate.Line _) => Task.CompletedTask;
        using var running = await gate.EnterAsync(Guid.NewGuid(), "m", Nothing, default);
        // One may wait; the next is refused at once, and leaves nothing in line.
        var waiting = gate.EnterAsync(Guid.NewGuid(), "m", Nothing, default);
        var busy = await Assert.ThrowsAsync<AnswerGate.BusyException>(() => gate.EnterAsync(Guid.NewGuid(), "m", Nothing, default));
        Assert.Contains("busy", busy.Message, StringComparison.Ordinal);
        Assert.Equal((1, 1), gate.Now());
        running.Dispose();
        using (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            // Served at once, it is never refused, however long the line.
            Assert.Equal((1, 0), gate.Now());
        }
        using var free = await gate.EnterAsync(Guid.NewGuid(), "m", Nothing, default);
    }

    [Fact]
    public async Task The_lines_numbers_for_prometheus_count_running_waiting_people_refusals_and_waits_never_names()
    {
        var gate = Gate(perPerson: 1, atOnce: 1, queueLength: 1);
        var (ann, ben) = (Guid.NewGuid(), Guid.NewGuid());
        Task Nothing(AnswerGate.Line _) => Task.CompletedTask;
        var running = await gate.EnterAsync(ann, 0, "Qwen3-4B", Nothing, default, kind: "api");
        var waiting = gate.EnterAsync(ben, 0, "Qwen3-4B", Nothing, default);
        await Assert.ThrowsAsync<AnswerGate.BusyException>(() => gate.EnterAsync(Guid.NewGuid(), 0, "Qwen3-4B", Nothing, default, kind: "api"));
        var text = LineMetrics.Render(gate);
        Assert.Contains("arena_line_running{model=\"Qwen3-4B\",kind=\"api\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("arena_line_waiting{model=\"Qwen3-4B\",kind=\"chat\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("arena_line_people 1", text, StringComparison.Ordinal);
        Assert.Contains("arena_line_refused_total{reason=\"busy\",kind=\"api\"} 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ann.ToString(), text, StringComparison.Ordinal);
        running.Dispose();
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Contains("arena_line_waited_seconds_count{kind=\"chat\"} 1", LineMetrics.Render(gate), StringComparison.Ordinal);
    }

    [Fact]
    public void Where_each_loaded_model_sits_is_told_by_gpu_and_ram_and_a_model_of_unknown_size_still_shows()
    {
        var state = new Llm.Api.Models.EngineState(TimeProvider.System);
        state.Set([new("split", "loaded"), new("tp", "loaded"), new("asleep", "unloaded"), new("env-model", "loaded")], 0);
        state.Hardware = FakeHardware.Four96();
        state.Places = new Dictionary<string, Llm.Api.Models.EngineState.ModelPlace>
        {
            ["split"] = new("llamacpp", new Dictionary<int, long> { [0] = 10, [1] = 12 }, Ram: 5),
            ["tp"] = new("vllm", new Dictionary<int, long> { [2] = 70, [3] = 70 }),
            ["asleep"] = new("llamacpp", new Dictionary<int, long> { [0] = 1 }),
        };
        var text = LineMetrics.RenderPlaces(state);
        Assert.Contains("arena_model_memory_bytes{model=\"split\",engine=\"llamacpp\",on=\"GPU 0\"} 10", text, StringComparison.Ordinal);
        Assert.Contains("arena_model_memory_bytes{model=\"split\",engine=\"llamacpp\",on=\"GPU 1\"} 12", text, StringComparison.Ordinal);
        Assert.Contains("arena_model_memory_bytes{model=\"split\",engine=\"llamacpp\",on=\"RAM\"} 5", text, StringComparison.Ordinal);
        Assert.Contains("arena_model_memory_bytes{model=\"tp\",engine=\"vllm\",on=\"GPU 3\"} 70", text, StringComparison.Ordinal);
        // The .env model: no place known on a machine with GPUs.
        Assert.Contains("arena_model_memory_bytes{model=\"env-model\",engine=\"llamacpp\",on=\"not known\"} 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("asleep", text, StringComparison.Ordinal);

        // A machine without GPUs: what the app does not know the size of is still in RAM.
        state.Hardware = FakeHardware.CpuOnly;
        Assert.Contains("arena_model_memory_bytes{model=\"env-model\",engine=\"llamacpp\",on=\"RAM\"} 0", LineMetrics.RenderPlaces(state), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Someone_who_gives_up_waiting_leaves_the_line()
    {
        var gate = Gate(perPerson: 1, atOnce: 1);
        var lines = new List<AnswerGate.Line>();
        using var first = await gate.EnterAsync(Guid.NewGuid(), "m", _ => Task.CompletedTask, default);
        using var cts = new CancellationTokenSource();
        var waiting = gate.EnterAsync(Guid.NewGuid(), "m", l => { lines.Add(l); return Task.CompletedTask; }, cts.Token);
        await Task.Delay(200);
        Assert.Equal(0, Assert.Single(lines).Ahead);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal((1, 0), gate.Now());
    }

    [Fact]
    public async Task A_second_answer_of_the_same_person_waits_its_turn_and_says_so()
    {
        await using var f = app.Create(app.ConnectionStringFor("fair_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Chat:AnswersPerPerson"] = "1", ["Chat:AnswersAtOnce"] = "4", ["Auth:DataKey"] = "a-data-key-for-fair-tests" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // A tool whose server is slow to answer holds the first answer (in its place) as long as the test wants.
        var made = await admin.PostAsync("/api/admin/tools/servers", new { name = "Slow Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey });
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        var name = "q" + Guid.NewGuid().ToString("N")[..10];
        var person = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, person.GetProperty("password").GetString()!);
        async Task<Guid> ChatAsync(string[] tools) => (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools }))).GetProperty("id").GetGuid();
        var (one, two) = (await ChatAsync([toolId]), await ChatAsync([]));
        async Task<List<string>> AskAsync(Guid chat) =>
            [.. (await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "hello" })).Content.ReadAsStringAsync())
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
                .Select(l => JsonDocument.Parse(l[6..]).RootElement.GetProperty("type").GetString()!)];

        app.Mcp.Hold = new TaskCompletionSource();
        try
        {
            int Initializes() { lock (app.Mcp.Calls) { return app.Mcp.Calls.Count(c => c.Method == "initialize"); } }
            var before = Initializes();
            var first = AskAsync(one);
            for (var i = 0; i < 100 && Initializes() == before; i++)
            {
                await Task.Delay(50);
            }
            var gate = f.Services.GetRequiredService<AnswerGate>();
            Assert.Equal((1, 0), gate.Now());

            // The second waits in line, and says so, until the first ends.
            var second = AskAsync(two);
            for (var i = 0; i < 100 && gate.Now() != (1, 1); i++)
            {
                await Task.Delay(50);
            }
            Assert.Equal((1, 1), gate.Now());
            await Task.Delay(500);
            Assert.False(second.IsCompleted);
            app.Mcp.Hold.SetResult();
            Assert.Contains("done", await first.WaitAsync(TimeSpan.FromSeconds(20)));
            var types = await second.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(types.IndexOf("queued") >= 0 && types.IndexOf("queued") < types.IndexOf("assistant"), string.Join(",", types));
            Assert.Contains("done", types);
            Assert.Equal((0, 0), gate.Now());
        }
        finally
        {
            app.Mcp.Hold?.TrySetResult();
            app.Mcp.Hold = null;
        }
    }

    [Fact]
    public async Task Api_keys_carry_the_gateways_safety_limit_and_follow_a_change_to_it()
    {
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("fairkeys_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" });
        // The app's line answers 4 at once and has the rest wait; LiteLLM's own limit is the net behind it.
        Assert.Equal(16, gateway.KeysOf($"{name}@example.test").Single().MaxParallel);

        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Chat:ApiRequestsPerKey", value = "5" } } }));
        for (var i = 0; i < 100 && gateway.KeysOf($"{name}@example.test").Single().MaxParallel != 20; i++)
        {
            await Task.Delay(100);
        }
        Assert.Equal(20, gateway.KeysOf($"{name}@example.test").Single().MaxParallel);
    }
}
