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
    public async Task An_admin_takes_a_waiting_request_out_and_stops_a_running_one_by_its_ticket_saying_why()
    {
        var gate = Gate(perPerson: 1, atOnce: 1);
        var (ann, ben, cy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Task Nothing(AnswerGate.Line _) => Task.CompletedTask;
        var running = await gate.EnterAsync(ann, 0, "m", Nothing, default, kind: "api");
        var waiting = gate.EnterAsync(ben, "m", Nothing, default);
        var next = gate.EnterAsync(cy, "m", Nothing, default);
        var line = gate.Snapshot().Waiting;
        var (benTicket, cyTicket) = (line.Single(e => e.Person == ben).Id, line.Single(e => e.Person == cy).Id);

        // Waiting: out of the line, nothing sent, and why; the one behind moves up. Asked to stop it, as if it ran, nothing changes.
        var seenRunning = gate.Remove(benTicket, "Stop it", running: true);
        Assert.Equal((false, false, (string?)null), (seenRunning!.Done, seenRunning.Running, seenRunning.Stopping));
        var removed = gate.Remove(benTicket, "The model is needed for others now", running: false);
        Assert.Equal((ben, "m", "chat", false, true), (removed!.Person, removed.Model, removed.Kind, removed.Running, removed.Done));
        var refused = await Assert.ThrowsAsync<AnswerGate.RemovedException>(() => waiting);
        Assert.Equal("An admin took this request out of the line, and nothing was sent to the model: The model is needed for others now", refused.Message);
        Assert.Equal((1, 1), gate.Now());
        Assert.Equal(1, Assert.Single(gate.Snapshot().Waiting).Position);
        Assert.Null(gate.Remove(benTicket, "again", running: false));

        // Running: its place's token fires with the reason, counted once however often it is asked; the place is held
        // until whoever has it gives it back, and then goes to the next, with the ticket it waited with.
        Assert.Equal(running.Id, Assert.Single(gate.Snapshot().Running).Id);
        Assert.False(running.Stopped.IsCancellationRequested);
        var stopped = gate.Remove(running.Id, "A runaway agent", running: true);
        Assert.Equal((true, true, (string?)null), (stopped!.Done, stopped.Running, stopped.Stopping));
        Assert.True(running.Stopped.IsCancellationRequested);
        // A second admin's stop does nothing more: the first reason holds, and it says so.
        var again = gate.Remove(running.Id, "A second admin", running: true);
        Assert.Equal((false, true, "A runaway agent"), (again!.Done, again.Running, again.Stopping));
        Assert.Equal("A runaway agent", running.StopReason);
        Assert.False(next.IsCompleted);
        running.Dispose();
        using (var place = await next.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.Equal(cyTicket, place.Id);
            Assert.Null(gate.Remove(running.Id, "gone", running: true));
            // Seen waiting, it runs now: taking it out of the line does not stop it.
            var started = gate.Remove(cyTicket, "The model is needed for others now", running: false);
            Assert.Equal((false, true, (string?)null), (started!.Done, started.Running, started.Stopping));
            Assert.False(place.Stopped.IsCancellationRequested);
            Assert.Null(place.StopReason);
        }
        var text = LineMetrics.Render(gate);
        Assert.Contains("arena_line_refused_total{reason=\"removed\",kind=\"chat\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("arena_line_stopped_total{kind=\"api\"} 1", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "LOG-01")]
    [Trait("Control", "IAM-05")]
    public async Task An_admin_takes_a_waiting_answer_out_and_stops_a_running_one_and_the_person_is_told_why()
    {
        await using var f = app.Create(app.ConnectionStringFor("kick_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Chat:AnswersPerPerson"] = "1", ["Chat:AnswersAtOnce"] = "4", ["Auth:DataKey"] = "a-data-key-for-fair-tests" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "kq" + Guid.NewGuid().ToString("N")[..10];
        var person = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, person.GetProperty("password").GetString()!);
        async Task<Guid> ChatAsync() => (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useCodeIntel = false }))).GetProperty("id").GetGuid();
        var (one, two) = (await ChatAsync(), await ChatAsync());
        async Task<List<JsonElement>> AskAsync(Guid chat, string text) =>
            [.. (await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text })).Content.ReadAsStringAsync())
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
                .Select(l => JsonDocument.Parse(l[6..]).RootElement)];
        var gate = f.Services.GetRequiredService<AnswerGate>();
        async Task UntilAsync((int, int) now)
        {
            for (var i = 0; i < 200 && gate.Now() != now; i++)
            {
                await Task.Delay(50);
            }
            Assert.Equal(now, gate.Now());
        }

        // The first answers slowly; the second, the same person's, waits for it.
        var first = AskAsync(one, "Write a lot [slow]");
        await UntilAsync((1, 0));
        var second = AskAsync(two, "hello");
        await UntilAsync((1, 1));
        var line = await admin.JsonAsync(await admin.GetAsync("/api/admin/line"));
        var waiting = Assert.Single(line.GetProperty("waiting").EnumerateArray()).GetProperty("id").GetGuid();
        var running = Assert.Single(line.GetProperty("running").EnumerateArray()).GetProperty("id").GetGuid();

        // A reason is needed, and the person themselves (no usage.line) may not.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/line/{waiting}/remove", new { reason = "  " }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/line/{waiting}/remove", new { reason = new string('x', 301) }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.PostAsync($"/api/admin/line/{waiting}/remove", new { reason = "Mine" }));

        // Waiting: out of the line, and the chat says why; nothing was asked of the model.
        var removed = await admin.PostAsync($"/api/admin/line/{waiting}/remove", new { reason = "The model is needed for others now" });
        await StatusAssert.Is(HttpStatusCode.OK, removed);
        Assert.Equal("waiting", (await admin.JsonAsync(removed)).GetProperty("was").GetString());
        var refused = await second.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("error", refused.Last().GetProperty("type").GetString());
        Assert.EndsWith("nothing was sent to the model: The model is needed for others now", refused.Last().GetProperty("message").GetString(), StringComparison.Ordinal);
        // Kept as an answer that failed, with why, as a refusal is: the chat still says it when it is read again.
        var kept = refused.Single(e => e.GetProperty("type").GetString() == "assistant").GetProperty("id").GetGuid();
        var unanswered = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{two}"))).GetProperty("messages").EnumerateArray().Last();
        Assert.Equal((kept, "failed", ""), (unanswered.GetProperty("id").GetGuid(), unanswered.GetProperty("status").GetString(), unanswered.GetProperty("content").GetString()));
        Assert.Equal(refused.Last().GetProperty("message").GetString(), unanswered.GetProperty("error").GetString());
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync($"/api/admin/line/{waiting}/remove", new { reason = "Again" }));

        // Running: stopped, keeping what it wrote, and the saved answer says why.
        var stopping = await admin.PostAsync($"/api/admin/line/{running}/remove", new { reason = "A runaway agent", running = true });
        await StatusAssert.Is(HttpStatusCode.OK, stopping);
        Assert.Equal("running", (await admin.JsonAsync(stopping)).GetProperty("was").GetString());
        var stopped = (await first.WaitAsync(TimeSpan.FromSeconds(20))).Last();
        Assert.Equal(("stopped", "Stopped by an admin: A runaway agent"), (stopped.GetProperty("type").GetString(), stopped.GetProperty("why").GetString()));
        var answer = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{one}"))).GetProperty("messages").EnumerateArray().Last();
        Assert.Equal(("stopped", "Stopped by an admin: A runaway agent"), (answer.GetProperty("status").GetString(), answer.GetProperty("error").GetString()));
        Assert.Equal((0, 0), gate.Now());

        // Both in the audit log, whose request, its model and kind, and why.
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() is "line.remove" or "line.stop")
            .ToDictionary(e => e.GetProperty("action").GetString()!, e => (Target: e.GetProperty("target").GetString(), Detail: e.GetProperty("detail").GetString()!, Actor: e.GetProperty("actor").GetString()));
        Assert.Equal((name, "admin"), (audit["line.remove"].Target, audit["line.remove"].Actor));
        Assert.Matches(@"^\S+, chat, waited \d+ s: The model is needed for others now$", audit["line.remove"].Detail);
        Assert.Equal(name, audit["line.stop"].Target);
        Assert.Matches(@"^\S+, chat, ran \d+ s: A runaway agent$", audit["line.stop"].Detail);
    }

    [Fact]
    public async Task An_answer_an_admin_stops_before_its_first_word_is_kept_stopped_and_says_why()
    {
        await using var f = app.Create(app.ConnectionStringFor("kickearly_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-fair-tests" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // A tool whose server is slow to answer holds the answer in its place, before its first word.
        var made = await admin.PostAsync("/api/admin/tools/servers", new { name = "Slow Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey });
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        var name = "ke" + Guid.NewGuid().ToString("N")[..10];
        var person = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, person.GetProperty("password").GetString()!);
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools = new[] { toolId } }))).GetProperty("id").GetGuid();

        app.Mcp.Hold = new TaskCompletionSource();
        try
        {
            int Initializes() { lock (app.Mcp.Calls) { return app.Mcp.Calls.Count(c => c.Method == "initialize"); } }
            var before = Initializes();
            var asked = b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "hello" });
            for (var i = 0; i < 100 && Initializes() == before; i++)
            {
                await Task.Delay(50);
            }
            var line = await admin.JsonAsync(await admin.GetAsync("/api/admin/line"));
            var ticket = Assert.Single(line.GetProperty("running").EnumerateArray()).GetProperty("id").GetGuid();
            await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/line/{ticket}/remove", new { reason = "Maintenance on this model", running = true }));
            List<JsonElement> events = [.. (await (await asked.WaitAsync(TimeSpan.FromSeconds(20))).Content.ReadAsStringAsync())
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
                .Select(l => JsonDocument.Parse(l[6..]).RootElement)];

            // The answer is kept, stopped, with why: the chat says it when it is read again, not only while it streams.
            var answer = events.Single(e => e.GetProperty("type").GetString() == "assistant").GetProperty("id").GetGuid();
            var stopped = events.Last();
            Assert.Equal(("stopped", answer, "Stopped by an admin: Maintenance on this model"),
                (stopped.GetProperty("type").GetString(), stopped.GetProperty("id").GetGuid(), stopped.GetProperty("why").GetString()));
            var saved = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().Last();
            Assert.Equal((answer, "stopped", "Stopped by an admin: Maintenance on this model"),
                (saved.GetProperty("id").GetGuid(), saved.GetProperty("status").GetString(), saved.GetProperty("error").GetString()));
            Assert.Equal((0, 0), f.Services.GetRequiredService<AnswerGate>().Now());
        }
        finally
        {
            app.Mcp.Hold?.TrySetResult();
            app.Mcp.Hold = null;
        }
    }

    [Fact]
    public async Task An_admin_who_saw_a_request_waiting_changes_nothing_once_it_runs_and_a_second_stop_is_not_audited_again()
    {
        await using var f = app.Create(app.ConnectionStringFor("kickrace_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Chat:AnswersPerPerson"] = "1" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "kr" + Guid.NewGuid().ToString("N")[..10];
        var person = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("id").GetGuid();
        var gate = f.Services.GetRequiredService<AnswerGate>();
        Task Nothing(AnswerGate.Line _) => Task.CompletedTask;
        var first = await gate.EnterAsync(person, "m", Nothing, default);
        var next = gate.EnterAsync(person, "m", Nothing, default);
        var ticket = Assert.Single(gate.Snapshot().Waiting).Id;

        // Its turn comes while the admin, who saw it waiting, writes why: Remove stops nothing, and says it started.
        first.Dispose();
        using var place = await next.WaitAsync(TimeSpan.FromSeconds(5));
        var started = await admin.PostAsync($"/api/admin/line/{ticket}/remove", new { reason = "The model is needed for others now" });
        await StatusAssert.Is(HttpStatusCode.Conflict, started);
        Assert.Equal("started", (await admin.JsonAsync(started)).GetProperty("status").GetString());
        Assert.False(place.Stopped.IsCancellationRequested);

        // Stopped, then stopped again while it ends: the first reason holds, the second admin is told it, and only the stop is audited.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/line/{ticket}/remove", new { reason = "A runaway agent", running = true }));
        Assert.True(place.Stopped.IsCancellationRequested);
        var again = await admin.PostAsync($"/api/admin/line/{ticket}/remove", new { reason = "A second admin", running = true });
        await StatusAssert.Is(HttpStatusCode.Conflict, again);
        var stopping = await admin.JsonAsync(again);
        Assert.Equal("already_stopping", stopping.GetProperty("status").GetString());
        Assert.EndsWith(": A runaway agent", stopping.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("A runaway agent", place.StopReason);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() is "line.remove" or "line.stop").ToList();
        Assert.Matches(@"^m, chat, ran \d+ s: A runaway agent$", Assert.Single(audit).GetProperty("detail").GetString()!);
        Assert.Contains("arena_line_stopped_total{kind=\"chat\"} 1", LineMetrics.Render(gate), StringComparison.Ordinal);
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

}
