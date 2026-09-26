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
    private static AnswerGate Gate(int perPerson, int atOnce) =>
        new(new StaticOptions(new ChatOptions { AnswersPerPerson = perPerson, AnswersAtOnce = atOnce }), TimeProvider.System);

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
        var a1 = await gate.EnterAsync(alice, Nothing, default);
        // Alice's second answer waits for her first, even with a place free.
        var a2 = gate.EnterAsync(alice, Nothing, default);
        var b1 = await gate.EnterAsync(bob, Nothing, default);
        Assert.False(a2.IsCompleted);
        var c1 = gate.EnterAsync(carol, Nothing, default);
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
    public async Task Someone_who_gives_up_waiting_leaves_the_line()
    {
        var gate = Gate(perPerson: 1, atOnce: 1);
        var lines = new List<AnswerGate.Line>();
        using var first = await gate.EnterAsync(Guid.NewGuid(), _ => Task.CompletedTask, default);
        using var cts = new CancellationTokenSource();
        var waiting = gate.EnterAsync(Guid.NewGuid(), l => { lines.Add(l); return Task.CompletedTask; }, cts.Token);
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
    public async Task Api_keys_carry_a_limit_of_requests_at_once_and_follow_a_change_to_it()
    {
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("fairkeys_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" });
        Assert.Equal(2, gateway.KeysOf($"{name}@example.test").Single().MaxParallel);

        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Chat:ApiRequestsPerKey", value = "5" } } }));
        for (var i = 0; i < 100 && gateway.KeysOf($"{name}@example.test").Single().MaxParallel != 5; i++)
        {
            await Task.Delay(100);
        }
        Assert.Equal(5, gateway.KeysOf($"{name}@example.test").Single().MaxParallel);
    }
}
