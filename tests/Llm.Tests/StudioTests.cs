using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Studio;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Agent Studio: workflows checked as kept; a run's steps (a question, JSON, a condition, a loop, a tool, a file) kept as they
/// went; an approval that waits and goes on without running again what ran; webhooks, schedules and a room's word; sharing.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class StudioTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("studio_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());

    private static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(TestBrowser admin, WebApplicationFactory<Program> f, string display)
    {
        var name = "w" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = display }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    /// <summary>Starts a run and waits for its work to end here; the run as it ended.</summary>
    private static async Task<JsonElement> RunAsync(TestBrowser b, WebApplicationFactory<Program> f, Guid workflow, object? body = null)
    {
        var started = await b.JsonAsync(await b.PostAsync($"/api/studio/{workflow}/run", body ?? new { text = "" }));
        return await EndedAsync(b, f, started.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> EndedAsync(TestBrowser b, WebApplicationFactory<Program> f, Guid run)
    {
        await f.Services.GetRequiredService<StudioRuns>().Finished(run).WaitAsync(TimeSpan.FromSeconds(30));
        return await b.JsonAsync(await b.GetAsync($"/api/studio/runs/{run}"));
    }

    private static List<(string Path, string Status, string Output)> Steps(JsonElement run) =>
        [.. run.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("path").GetString()!, s.GetProperty("status").GetString()!, s.GetProperty("output").GetString() ?? ""))];

    [Fact]
    public async Task A_run_asks_reads_JSON_branches_loops_uses_a_tool_and_saves_a_file_each_step_kept()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, _) = await PersonAsync(admin, f, "Ana");

        // Kept only when it makes sense.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync("/api/studio", new { name = "Bad", flow = new { trigger = new { kind = "manual" }, steps = Array.Empty<object>() } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync("/api/studio", new { name = "Bad", flow = new { trigger = new { kind = "schedule", cron = "every day" }, steps = new[] { new { id = "a", kind = "text", text = "x" } } } }));
        var twice = await ana.PostAsync("/api/studio", new { name = "Bad", flow = new { trigger = new { kind = "manual" }, steps = new[] { new { id = "a", kind = "text", text = "x" }, new { id = "a", kind = "text", text = "y" } } } });
        Assert.Contains("its own id", await twice.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var flow = new
        {
            trigger = new { kind = "manual" },
            steps = new object[]
            {
                new { id = "colour", kind = "ask", name = "Ask", prompt = "Name a colour for {{input.text}}" },
                new { id = "check", kind = "condition", left = "{{colour.text}}", op = "contains", right = "colour", then = new object[] { new { id = "yes", kind = "text", text = "It said: {{colour.text}}" } },
                    @else = new object[] { new { id = "no", kind = "text", text = "No colour" } } },
                new { id = "list", kind = "ask", json = true, prompt = """Three fruits [json:["apple", "pear", "fig"]]""" },
                new { id = "each", kind = "loop", over = "{{list.json}}", most = 2, steps = new object[] { new { id = "line", kind = "text", text = "{{index}}: {{item}}" } } },
                new { id = "sum", kind = "tool", tool = "calculator", function = "calculate", arguments = new Dictionary<string, string> { ["expression"] = "6*7" } },
                new { id = "save", kind = "file", fileName = "fruits.md", text = "{{each.text}} / {{sum.text}}" },
            },
        };
        var made = await ana.JsonAsync(await ana.PostAsync("/api/studio", new { name = "Everything", flow }));
        var id = made.GetProperty("workflow").GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, made.GetProperty("secret").ValueKind);

        var run = await RunAsync(ana, f, id, new { text = "the sea" });
        Assert.Equal("done", run.GetProperty("status").GetString());
        var steps = Steps(run);
        Assert.Equal(["colour", "check", "check/yes", "list", "each", "each[0]/line", "each[1]/line", "sum", "save"], steps.Select(s => s.Path));
        Assert.StartsWith("Answer to: Name a colour", steps[0].Output, StringComparison.Ordinal);
        Assert.Equal("yes", steps[1].Output);
        Assert.Equal(["0: apple", "1: pear"], steps.Where(s => s.Path.EndsWith("/line", StringComparison.Ordinal)).Select(s => s.Output));
        Assert.Contains("42", steps[7].Output, StringComparison.Ordinal);
        var file = run.GetProperty("steps").EnumerateArray().Last().GetProperty("link").GetString()!;
        Assert.Equal("0: apple\n1: pear / " + steps[7].Output, await (await ana.GetAsync(file)).Content.ReadAsStringAsync());
        // Its runs, and a notification.
        Assert.Single((await ana.JsonAsync(await ana.GetAsync($"/api/studio/{id}/runs"))).EnumerateArray());
    }

    [Fact]
    public async Task An_approval_waits_and_the_run_goes_on_without_running_again_what_ran_or_ends_when_turned_down()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, _) = await PersonAsync(admin, f, "Ana");
        var flow = new
        {
            trigger = new { kind = "manual" },
            steps = new object[]
            {
                new { id = "draft", kind = "ask", prompt = "Draft a note about {{input.text}}" },
                new { id = "ok", kind = "approval", question = "Send it? {{draft.text}}" },
                new { id = "sent", kind = "text", text = "Sent: {{draft.text}}" },
            },
        };
        var id = (await ana.JsonAsync(await ana.PostAsync("/api/studio", new { name = "Careful", flow }))).GetProperty("workflow").GetProperty("id").GetGuid();
        var run = await RunAsync(ana, f, id, new { text = "lunch" });
        Assert.Equal("waiting", run.GetProperty("status").GetString());
        Assert.Equal([("draft", "done"), ("ok", "waiting")], Steps(run).Select(s => (s.Path, s.Status)));
        var asked = app.Model.Requests.Count(r => r.Body["messages"]!.ToJsonString().Contains("Draft a note about lunch", StringComparison.Ordinal));

        var runId = run.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Accepted, await ana.PostAsync($"/api/studio/runs/{runId}/approve", new { approve = true }));
        var after = await EndedAsync(ana, f, runId);
        Assert.Equal("done", after.GetProperty("status").GetString());
        Assert.StartsWith("Sent: Answer to: Draft a note", Steps(after)[2].Output, StringComparison.Ordinal);
        // The draft was not asked for again.
        Assert.Equal(asked, app.Model.Requests.Count(r => r.Body["messages"]!.ToJsonString().Contains("Draft a note about lunch", StringComparison.Ordinal)));
        await StatusAssert.Is(HttpStatusCode.Conflict, await ana.PostAsync($"/api/studio/runs/{runId}/approve", new { approve = true }));

        var second = await RunAsync(ana, f, id, new { text = "rent" });
        var secondId = second.GetProperty("id").GetGuid();
        await ana.PostAsync($"/api/studio/runs/{secondId}/approve", new { approve = false });
        var turned = await EndedAsync(ana, f, secondId);
        Assert.Equal(("stopped", "Turned down at ok."), (turned.GetProperty("status").GetString(), turned.GetProperty("error").GetString()));
        Assert.DoesNotContain(Steps(turned), s => s.Path == "sent");
    }

    [Fact]
    public async Task Webhooks_schedules_and_a_rooms_word_start_runs()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, _) = await PersonAsync(admin, f, "Ana");
        var runs = f.Services.GetRequiredService<StudioRuns>();

        // A webhook: its secret shown once, the event's JSON its input.
        var hooked = await ana.JsonAsync(await ana.PostAsync("/api/studio", new
        {
            name = "Hook", flow = new { trigger = new { kind = "webhook" }, steps = new[] { new { id = "got", kind = "text", text = "Got {{input.event.order}}" } } },
        }));
        var hookId = hooked.GetProperty("workflow").GetProperty("id").GetGuid();
        var secret = hooked.GetProperty("secret").GetString()!;
        var anonymous = new TestBrowser(f);
        using (var wrong = new HttpRequestMessage(HttpMethod.Post, $"/api/studio/hooks/{hookId}") { Content = JsonContent.Create(new { order = 7 }) })
        {
            wrong.Headers.Add("X-Hook-Secret", "nope");
            await StatusAssert.Is(HttpStatusCode.Unauthorized, await anonymous.Http.SendAsync(wrong));
        }
        using (var right = new HttpRequestMessage(HttpMethod.Post, $"/api/studio/hooks/{hookId}") { Content = JsonContent.Create(new { order = 7 }) })
        {
            right.Headers.Add("X-Hook-Secret", secret);
            var accepted = await anonymous.JsonAsync(await anonymous.Http.SendAsync(right));
            var run = await EndedAsync(ana, f, accepted.GetProperty("run").GetGuid());
            Assert.Equal(("webhook", "Got 7"), (run.GetProperty("trigger").GetString(), Steps(run)[0].Output));
        }

        // A schedule: its next run worked out; when it has come, it runs once.
        var scheduled = (await ana.JsonAsync(await ana.PostAsync("/api/studio", new
        {
            name = "Daily", flow = new { trigger = new { kind = "schedule", cron = "0 9 * * *", timeZone = "UTC" }, steps = new[] { new { id = "hi", kind = "text", text = "Good morning" } } },
        }))).GetProperty("workflow");
        Assert.NotEqual(JsonValueKind.Null, scheduled.GetProperty("nextRunAt").ValueKind);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var w = await db.Workflows.FindAsync(scheduled.GetProperty("id").GetGuid());
            w!.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await runs.DueAsync(CancellationToken.None));
        Assert.Equal(0, await runs.DueAsync(CancellationToken.None));

        // A room's word: a message that starts with it runs the workflow, which answers in the room; its own answer starts nothing.
        var room = (await ana.JsonAsync(await ana.PostAsync("/api/rooms", new { title = "Help", userIds = Array.Empty<Guid>() }))).GetProperty("id").GetGuid();
        var triage = (await ana.JsonAsync(await ana.PostAsync("/api/studio", new
        {
            name = "Triage", flow = new
            {
                trigger = new { kind = "room", roomId = room, keyword = "#triage" },
                steps = new object[] { new { id = "reply", kind = "room", roomId = room, text = "#triage noted for {{input.by}}: {{input.text}}" } },
            },
        }))).GetProperty("workflow").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.OK, await ana.PostAsync($"/api/chat/conversations/{room}/queue", new { content = "#triage the printer is down" }));
        List<JsonElement> list = [];
        for (var i = 0; i < 100 && list.Count == 0; i++)
        {
            await Task.Delay(100);
            list = [.. (await ana.JsonAsync(await ana.GetAsync($"/api/studio/{triage}/runs"))).EnumerateArray()];
        }
        var roomRun = await EndedAsync(ana, f, list.Single().GetProperty("id").GetGuid());
        Assert.Equal(("room", "done"), (roomRun.GetProperty("trigger").GetString(), roomRun.GetProperty("status").GetString()));
        var said = (await ana.JsonAsync(await ana.GetAsync($"/api/chat/conversations/{room}"))).GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()).ToList();
        Assert.Contains("#triage noted for Ana: the printer is down", said);
        await Task.Delay(500);
        Assert.Single((await ana.JsonAsync(await ana.GetAsync($"/api/studio/{triage}/runs"))).EnumerateArray());
    }

    [Fact]
    public async Task A_shared_form_runs_as_its_owner_and_those_who_fill_it_see_how_it_went_not_its_steps()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, _) = await PersonAsync(admin, f, "Ana");
        var (ben, _) = await PersonAsync(admin, f, "Ben");
        var flow = new
        {
            trigger = new { kind = "form", fields = new object[] { new { name = "topic", label = "Topic", kind = "text", required = true }, new { name = "size", label = "Size", kind = "choice", choices = new[] { "S", "L" } } } },
            steps = new object[] { new { id = "secret", kind = "ask", prompt = "Our secret plan for {{input.topic}}" }, new { id = "done", kind = "text", text = "Thanks {{input.by}}" } },
        };
        var id = (await ana.JsonAsync(await ana.PostAsync("/api/studio", new { name = "Requests", flow, reach = "company" }))).GetProperty("workflow").GetProperty("id").GetGuid();

        var seen = await ben.JsonAsync(await ben.GetAsync($"/api/studio/{id}"));
        Assert.False(seen.GetProperty("mine").GetBoolean());
        Assert.False(seen.TryGetProperty("flow", out _));
        Assert.Equal(["topic", "size"], seen.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ben.PostAsync($"/api/studio/{id}/run", new { fields = new { size = "S" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ben.PostAsync($"/api/studio/{id}/run", new { fields = new { topic = "Chairs", size = "XL" } }));
        var run = await RunAsync(ben, f, id, new { fields = new Dictionary<string, string> { ["topic"] = "Chairs", ["size"] = "L" } });
        Assert.Equal(("form", "done"), (run.GetProperty("trigger").GetString(), run.GetProperty("status").GetString()));
        // Ben sees how it went; what it asked and answered is Ana's to read.
        var benSteps = run.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, benSteps[0].GetProperty("input").ValueKind);
        Assert.Equal(JsonValueKind.Null, benSteps[0].GetProperty("output").ValueKind);
        Assert.Equal("Thanks Ben", benSteps[1].GetProperty("output").GetString());
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.Http.PutAsJsonAsync(new Uri($"/api/studio/{id}", UriKind.Relative), new { name = "Mine" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/studio/{id}/runs"));
        Assert.Contains("Our secret plan for Chairs", (await ana.JsonAsync(await ana.GetAsync($"/api/studio/runs/{run.GetProperty("id").GetGuid()}"))).GetProperty("steps")[0].GetProperty("input").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Templates_fill_from_the_runs_values()
    {
        var values = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["input"] = System.Text.Json.Nodes.JsonNode.Parse("""{"text": "hi", "by": "Ana"}"""),
            ["s1"] = System.Text.Json.Nodes.JsonNode.Parse("""{"text": "x", "json": {"items": [{"name": "a"}, {"name": "b"}]}}"""),
        };
        Assert.Equal("hi Ana b {missing}", FlowModel.Fill("{{input.text}} {{ input.by }} {{s1.json.items.1.name}} {missing}{{nope.text}}", values));
        Assert.Equal(2, FlowModel.Items("{{s1.json.items}}", values).Count);
        Assert.Equal(["one", "two"], FlowModel.Items("- one\n- two\n", values).Select(i => i!.GetValue<string>()));
        Assert.True(FlowModel.Test("12", "greater", "3"));
        Assert.False(FlowModel.Test("abc", "equals", "ABD"));
        Assert.All(StudioTemplates.All.Where(t => t.Flow.Trigger.Kind != "room"), t => Assert.Null(FlowModel.Check(t.Flow)));
    }
}
