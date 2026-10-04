using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Operations;
using Llm.Api.Schedules;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// Several app replicas on one database: one leads, scheduled tasks run once, a decision or a stop
/// posted to either reaches the answer, saved settings apply on both; and the line's priorities per
/// group, and the routing a model on several servers gets at the gateway.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ScaleOutTests(AppFixture app)
{
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["Auth:DataKey"] = "a-data-key-for-scale-tests", ["Replicas:Renew"] = "00:00:01", ["GitLab:Url"] = "https://gitlab.test", ["GitLab:BotToken"] = "bot-token",
    };

    /// <summary>Two replicas of the app on one new database, started together.</summary>
    private async Task<(WebApplicationFactory<Program> A, WebApplicationFactory<Program> B)> TwoAsync(Dictionary<string, string?>? more = null)
    {
        var db = app.ConnectionStringFor("scale_" + Guid.NewGuid().ToString("N")[..8]);
        var gateway = new FakeGateway();
        var settings = new Dictionary<string, string?>(Settings);
        foreach (var (k, v) in more ?? [])
        {
            settings[k] = v;
        }
        var (a, b) = (app.Create(db, gateway, settings), app.Create(db, gateway, settings));
        // Both start at once on a database that does not exist yet: one at a time through the first steps.
        await Task.WhenAll(Task.Run(() => a.Server), Task.Run(() => b.Server));
        await UntilAsync(() => Replicas(a).Count == 2 && Replicas(b).Count == 2, "both replicas counted");
        return (a, b);
    }

    private static Replicas Replicas(WebApplicationFactory<Program> f) => f.Services.GetRequiredService<Replicas>();

    private static Task UntilAsync(Func<bool> done, string what, int seconds = 20) => UntilAsync(() => Task.FromResult(done()), what, seconds);

    private static async Task UntilAsync(Func<Task<bool>> done, string what, int seconds = 20, Func<Task<string>>? said = null)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (await done())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Never: {what}. {(said is null ? "" : await said())}");
    }

    private static async Task<(TestBrowser A, TestBrowser B, Guid Id)> PersonOnBothAsync(WebApplicationFactory<Program> a, WebApplicationFactory<Program> b)
    {
        var admin = await new TestBrowser(a).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "r" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var password = made.GetProperty("password").GetString()!;
        return (await new TestBrowser(a).SignedInAsync(name, password), await new TestBrowser(b).SignedInAsync(name, password), made.GetProperty("id").GetGuid());
    }

    private static List<JsonElement> Events(string body) =>
        [.. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return Events(await res.Content.ReadAsStringAsync());
    }

    /// <summary>Posts until the replica says yes (the answer may not have got there yet on the other).</summary>
    private static async Task PostUntilAsync(TestBrowser b, string path, object body, HttpStatusCode wanted)
    {
        for (var i = 0; i < 150; i++)
        {
            var res = await b.PostAsync(path, body);
            if (res.StatusCode == wanted)
            {
                return;
            }
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            await Task.Delay(100);
        }
        Assert.Fail($"{path} never answered {(int)wanted}.");
    }

    [Fact]
    public async Task Replicas_started_together_elect_one_leader_and_the_other_takes_over_when_it_stops()
    {
        var (a, b) = await TwoAsync();
        await using var _b = b;
        await UntilAsync(() => Replicas(a).IsLeader != Replicas(b).IsLeader, "exactly one leader");
        Assert.NotEqual(Replicas(a).Id, Replicas(b).Id);
        // Both answer: the start lock let each through in turn.
        Assert.Equal(HttpStatusCode.OK, (await a.CreateClient().GetAsync(new Uri("/healthz", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.CreateClient().GetAsync(new Uri("/healthz", UriKind.Relative))).StatusCode);

        var (leader, other) = Replicas(a).IsLeader ? (a, b) : (b, a);
        var follower = Replicas(other);
        await leader.DisposeAsync();
        await UntilAsync(() => follower.IsLeader, "the other replica leading");
        await UntilAsync(() => follower.Count == 1, "one replica counted");
        if (other == a)
        {
            await a.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_due_task_runs_once_though_both_replicas_look_and_events_take_turns_across_replicas()
    {
        var (a, b) = await TwoAsync();
        await using var _a = a;
        await using var _b = b;
        var (onA, onB, _) = await PersonOnBothAsync(a, b);
        var made = await onA.JsonAsync(await onA.PostAsync("/api/tasks", new { name = "Daily", prompt = "What is new?", cron = "0 8 * * *", timeZone = "UTC" }));
        var id = made.GetProperty("id").GetGuid();
        using (var scope = a.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = await db.ScheduledTasks.SingleAsync(x => x.Id == id);
            t.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        // Both replicas look at the clock at the same moment (only the leader does, but even so).
        await Task.WhenAll(
            a.Services.GetRequiredService<Scheduler>().RunDueAsync(CancellationToken.None),
            b.Services.GetRequiredService<Scheduler>().RunDueAsync(CancellationToken.None));
        await UntilAsync(async () => (await Runs(onA, id)).Count(r => r.GetProperty("status").GetString() == "done") == 1, "the run done",
            said: async () => string.Join("; ", (await Runs(onA, id)).Select(r => r.GetRawText())));
        await Task.Delay(500);
        Assert.Single(await Runs(onA, id));

        // A task run by events: the first reaches one replica, the second the other while the first answers.
        var hook = await onA.JsonAsync(await onA.PostAsync("/api/tasks", new { name = "Busy", prompt = "Say it [steady]", trigger = "webhook" }));
        var (url, secret, hookId) = (new Uri(hook.GetProperty("hookUrl").GetString()!).PathAndQuery, hook.GetProperty("hookToken").GetString()!, hook.GetProperty("id").GetGuid());
        Task<HttpResponseMessage> Post(WebApplicationFactory<Program> f, int n)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(new { n }), Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Hook-Secret", secret);
            return f.CreateClient().SendAsync(request);
        }
        Assert.Contains("started", await (await Post(a, 1)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("queued", await (await Post(b, 2)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // The other replica sees it running too.
        var listed = (await onB.JsonAsync(await onB.GetAsync("/api/tasks"))).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == hookId);
        Assert.True(listed.GetProperty("running").GetBoolean());
        await UntilAsync(async () => await Runs(onA, hookId) is { Count: 2 } runs && runs.All(r => r.GetProperty("status").GetString() == "done"), "both events run", 30,
            async () => string.Join("; ", (await Runs(onA, hookId)).Select(r => r.GetRawText())));
        var both = (await Runs(onA, hookId)).OrderBy(r => r.GetProperty("startedAt").GetDateTimeOffset()).ToList();
        // One after the other, never at once.
        Assert.True(both[1].GetProperty("startedAt").GetDateTimeOffset() >= both[0].GetProperty("finishedAt").GetDateTimeOffset());
        using (var scope = b.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.TaskEvents.Where(e => e.TaskId == hookId).ToListAsync());
            Assert.Null((await db.ScheduledTasks.AsNoTracking().SingleAsync(t => t.Id == hookId)).RunningOn);
        }
    }

    private static async Task<List<JsonElement>> Runs(TestBrowser b, Guid task) =>
        [.. (await b.JsonAsync(await b.GetAsync($"/api/tasks/{task}/runs"))).EnumerateArray()];

    [Fact]
    public async Task A_decision_a_stop_and_answer_now_posted_to_the_other_replica_reach_the_answer()
    {
        var (a, b) = await TwoAsync();
        await using var _a = a;
        await using var _b = b;
        var admin = await new TestBrowser(a).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/calculator", UriKind.Relative),
            new { enabled = true, audience = "Everyone", onByDefault = true, askFirst = true }));
        var (onA, onB, _) = await PersonOnBothAsync(a, b);
        var chat = (await onA.JsonAsync(await onA.PostAsync("/api/chat/conversations", new { useArgus = false, thinking = "xhigh" }))).GetProperty("id").GetGuid();

        // The answer runs on A and waits for a yes, which comes to B.
        var answering = SendAsync(onA, chat, """Sum: [call calculate {"expression":"6*7"}]""");
        await PostUntilAsync(onB, $"/api/chat/conversations/{chat}/tool-calls/call_1", new { allow = true }, HttpStatusCode.NoContent);
        Assert.Contains("\"result\":\"42\"", (await answering).Single(e => e.GetProperty("type").GetString() == "tool_result").GetProperty("text").GetString(), StringComparison.Ordinal);
        // Nothing waits any more, on either.
        await StatusAssert.Is(HttpStatusCode.Conflict, await onB.PostAsync($"/api/chat/conversations/{chat}/tool-calls/call_1", new { allow = true }));

        // Stopped from B.
        answering = SendAsync(onA, chat, "Write a lot [slow]");
        await PostUntilAsync(onB, $"/api/chat/conversations/{chat}/stop", new { }, HttpStatusCode.Accepted);
        Assert.Equal("stopped", (await answering).Last().GetProperty("type").GetString());

        // "Answer now" from B.
        answering = SendAsync(onA, chat, "Think hard [ponder]");
        await PostUntilAsync(onB, $"/api/chat/conversations/{chat}/hurry", new { }, HttpStatusCode.Accepted);
        var events = await answering;
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "thought" && e.TryGetProperty("cutShort", out var cut) && cut.GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Conflict, await onB.PostAsync($"/api/chat/conversations/{chat}/stop", new { }));
    }

    [Fact]
    public async Task A_setting_saved_on_one_replica_applies_on_the_other()
    {
        var (a, b) = await TwoAsync();
        await using var _a = a;
        await using var _b = b;
        var admin = await new TestBrowser(a).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Chat:AnswersPerPerson", value = "3" } } }));
        var chat = b.Services.GetRequiredService<IOptionsMonitor<ChatOptions>>();
        await UntilAsync(() => chat.CurrentValue.AnswersPerPerson == 3, "the setting on the other replica");
    }

    private static AnswerGate Gate(int perPerson, int atOnce) =>
        new(new StaticOptions(new ChatOptions { AnswersPerPerson = perPerson, AnswersAtOnce = atOnce }), TimeProvider.System);

    private sealed class StaticOptions(ChatOptions value) : IOptionsMonitor<ChatOptions>
    {
        public ChatOptions CurrentValue => value;
        public ChatOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ChatOptions, string?> listener) => null;
    }

    [Fact]
    public async Task A_higher_priority_goes_first_and_within_a_priority_the_line_stays_fair()
    {
        var gate = Gate(perPerson: 1, atOnce: 1);
        var (holder, low, high, high2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var lines = new Dictionary<Guid, List<AnswerGate.Line>> { [low] = [], [high] = [], [high2] = [] };
        Func<AnswerGate.Line, Task> Tell(Guid who) => l =>
        {
            lock (lines)
            {
                lines[who].Add(l);
            }
            return Task.CompletedTask;
        };
        var first = await gate.EnterAsync(holder, 0, _ => Task.CompletedTask, default);
        var lowWaits = gate.EnterAsync(low, 0, Tell(low), default);
        await Task.Delay(50);
        var highWaits = gate.EnterAsync(high, 5, Tell(high), default);
        await Task.Delay(50);
        var high2Waits = gate.EnterAsync(high2, 5, Tell(high2), default);
        await Task.Delay(1500);
        lock (lines)
        {
            // The later, higher priority is ahead of the one who came first; within priority 5, first come first.
            Assert.Equal(0, lines[high].Last().Ahead);
            Assert.Equal(1, lines[high2].Last().Ahead);
            Assert.Equal(2, lines[low].Last().Ahead);
        }
        first.Dispose();
        using (await highWaits.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(lowWaits.IsCompleted);
            Assert.False(high2Waits.IsCompleted);
        }
        using (await high2Waits.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(lowWaits.IsCompleted);
        }
        (await lowWaits.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal((0, 0), gate.Now());
    }

    [Fact]
    public async Task The_places_are_shared_out_among_the_replicas()
    {
        var gate = Gate(perPerson: 1, atOnce: 4);
        gate.Replicas = 3;
        // Four places on three replicas: two here (rounded up).
        var one = await gate.EnterAsync(Guid.NewGuid(), _ => Task.CompletedTask, default);
        var two = await gate.EnterAsync(Guid.NewGuid(), _ => Task.CompletedTask, default);
        var three = gate.EnterAsync(Guid.NewGuid(), _ => Task.CompletedTask, default);
        await Task.Delay(100);
        Assert.False(three.IsCompleted);
        gate.Replicas = 1;
        one.Dispose();
        (await three.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        two.Dispose();
    }

    [Fact]
    public async Task A_groups_priority_is_set_by_admins_checked_audited_and_its_members_take_the_highest()
    {
        await using var f = app.Create(app.ConnectionStringFor("prio_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), Settings);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var person = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("id").GetGuid();

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/groups", new { name = "Too high", priority = 11 }));
        var oncall = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "On call", priority = 3 }))).GetProperty("id").GetGuid();
        var batch = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Batch" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/groups/{batch}", UriKind.Relative), new { priority = -2 }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/groups/{batch}", UriKind.Relative), new { priority = -11 }));
        var listed = (await admin.JsonAsync(await admin.GetAsync("/api/admin/groups"))).EnumerateArray().ToDictionary(g => g.GetProperty("name").GetString()!, g => g.GetProperty("priority").GetInt32());
        Assert.Equal(3, listed["On call"]);
        Assert.Equal(-2, listed["Batch"]);
        Assert.Equal(-2, (await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{batch}"))).GetProperty("priority").GetInt32());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() == "group.update").Select(e => e.GetProperty("detail").GetString()).ToList();
        Assert.Contains("priority 0 → -2", audit);

        async Task<int> PriorityAsync()
        {
            using var scope = f.Services.CreateScope();
            var user = await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Llm.Core.Identity.AppUser>>().FindByIdAsync(person.ToString());
            return await scope.ServiceProvider.GetRequiredService<AccessService>().PriorityAsync(user!);
        }
        Assert.Equal(0, await PriorityAsync());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{batch}/members", new { userIds = new[] { person } }));
        Assert.Equal(-2, await PriorityAsync());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{oncall}/members", new { userIds = new[] { person } }));
        Assert.Equal(3, await PriorityAsync());
    }

    [Fact]
    public async Task A_model_on_two_servers_is_a_pool_at_the_gateway_each_copy_within_its_slots()
    {
        app.Remote.Down = false;
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("pool_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?> { ["Engine:Enabled"] = "false" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        object Server(string name, object[] models) => new { name, baseUrl = FakeRemote.Base + "/", apiKey = FakeRemote.Key, models };

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/servers", Server("GPU 1", [new { remote = "big-remote", name = "pooled", parallel = 0 }])));
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/servers", Server("GPU 1", [
            new { remote = "big-remote", name = "pooled", parallel = 4 }, new { remote = "small-remote", name = "solo", parallel = 2 },
        ])));
        // One copy: as before, no pool.
        var alone = Assert.Single(gateway.Managed.Values, m => m.Name == "pooled");
        Assert.Null(alone.Params["max_parallel_requests"]);
        Assert.Null(alone.Info["llm_app_pool"]);

        var second = await admin.PostAsync("/api/admin/servers", Server("GPU 2", [new { remote = "big-remote", name = "pooled", parallel = 2 }]));
        await StatusAssert.Is(HttpStatusCode.Created, second);
        var pool = gateway.Managed.Values.Where(m => m.Name == "pooled").ToList();
        Assert.Equal(2, pool.Count);
        Assert.Equal([2, 4], pool.Select(m => m.Params["max_parallel_requests"]!.GetValue<int>()).Order());
        Assert.Equal([2, 4], pool.Select(m => m.Params["weight"]!.GetValue<int>()).Order());
        Assert.All(pool, m => Assert.Equal(2, m.Info["llm_app_pool"]!.GetValue<int>()));
        var solo = Assert.Single(gateway.Managed.Values, m => m.Name == "solo");
        Assert.Null(solo.Params["max_parallel_requests"]);
        // The slots are kept and shown.
        var listed = (await admin.JsonAsync(await admin.GetAsync("/api/admin/servers"))).EnumerateArray().Single(s => s.GetProperty("name").GetString() == "GPU 2");
        Assert.Equal(2, listed.GetProperty("models")[0].GetProperty("parallel").GetInt32());

        // One server leaves: the other copy is on its own again.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.DeleteAsync(new Uri($"/api/admin/servers/{(await admin.JsonAsync(second)).GetProperty("id").GetGuid()}", UriKind.Relative)));
        alone = Assert.Single(gateway.Managed.Values, m => m.Name == "pooled");
        Assert.Null(alone.Params["max_parallel_requests"]);
    }
}
