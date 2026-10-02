using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Schedules;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Questions asked on a schedule: as their owner, landing as chats, a notification, an email and a webhook post.</summary>
[Collection(nameof(AppCollection))]
public sealed class ScheduledTasksTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeSmtp smtp) =>
        app.Create(app.ConnectionStringFor("tasks_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-task-tests",
            ["Mail:Host"] = "127.0.0.1", ["Mail:Port"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Mail:StartTls"] = "false",
            ["Mail:From"] = "LLM Service <llm@example.test>",
            ["Schedules:WebhookHosts"] = "hooks.example.test",
        });

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "s" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    /// <summary>An email's text, its transfer encoding undone.</summary>
    private static string BodyOf(string data)
    {
        var split = data.IndexOf("\n\n", StringComparison.Ordinal);
        var (headers, body) = (data[..split], data[(split + 2)..]);
        return headers.Contains("base64", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(body.Split('\n', StringSplitOptions.TrimEntries))))
            : body;
    }

    private static async Task<JsonElement> TaskAsync(TestBrowser b, Guid id) =>
        (await b.JsonAsync(await b.GetAsync("/api/tasks"))).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == id);

    private static async Task<List<JsonElement>> RunsAsync(TestBrowser b, Guid id) =>
        [.. (await b.JsonAsync(await b.GetAsync($"/api/tasks/{id}/runs"))).EnumerateArray()];

    private static async Task<List<JsonElement>> FinishedAsync(TestBrowser b, Guid id, int count)
    {
        for (var i = 0; i < 200; i++)
        {
            var runs = await RunsAsync(b, id);
            if (runs.Count(r => r.GetProperty("status").GetString() != "running") >= count)
            {
                return runs;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Not {count} runs finished.");
    }

    [Fact]
    public async Task A_task_runs_as_its_owner_lands_as_a_chat_and_is_delivered_everywhere_asked()
    {
        await using var smtp = new FakeSmtp();
        await using var f = NewApp(smtp);
        var (b, email) = await PersonAsync(f);
        var task = new
        {
            name = "Morning digest", prompt = "Summarize what changed yesterday.", cron = "0 9 * * 1-5", timeZone = "Europe/Berlin",
            email = true, webhook = "https://hooks.example.test/T000/B000/secret",
        };

        // Refused: too often, nowhere real to post to, no such zone, no schedule.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/tasks", task with { cron = "*/5 * * * *" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/tasks", task with { webhook = "https://intranet.example.test/hook" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/tasks", task with { webhook = "http://hooks.example.test/T000" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/tasks", task with { timeZone = "Mars/Olympus_Mons" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/tasks", task with { cron = "0 9 * *" }));

        var made = await b.PostAsync("/api/tasks", task);
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await b.JsonAsync(made)).GetProperty("id").GetGuid();
        var listed = await TaskAsync(b, id);
        Assert.Equal(3, listed.GetProperty("nextRuns").GetArrayLength());
        Assert.True(listed.GetProperty("webhookSet").GetBoolean());
        // The webhook URL is a secret: never sent back.
        Assert.DoesNotContain("secret", await (await b.GetAsync("/api/tasks")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Run now: a new chat with the question and its answer, a notification, an email, a post.
        await StatusAssert.Is(HttpStatusCode.Accepted, await b.PostAsync($"/api/tasks/{id}/run"));
        var run = (await FinishedAsync(b, id, 1))[0];
        Assert.Equal("done", run.GetProperty("status").GetString());
        Assert.True(run.GetProperty("manual").GetBoolean());
        Assert.Equal("email sent; webhook posted", run.GetProperty("delivery").GetString());
        var chatId = run.GetProperty("conversationId").GetGuid();
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chatId}"));
        Assert.StartsWith("Morning digest · ", chat.GetProperty("title").GetString(), StringComparison.Ordinal);
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("Summarize what changed yesterday.", messages[0].GetProperty("content").GetString());
        Assert.Equal("Answer to: Summarize what chang", messages[^1].GetProperty("content").GetString());

        var notes = await b.JsonAsync(await b.GetAsync("/api/notifications"));
        Assert.Equal(1, notes.GetProperty("unread").GetInt32());
        Assert.Equal($"/chat/{chatId}", notes.GetProperty("items")[0].GetProperty("link").GetString());
        Assert.Equal("Morning digest", notes.GetProperty("items")[0].GetProperty("title").GetString());

        var mail = Assert.Single(smtp.Received);
        Assert.Contains(email, mail.To[0], StringComparison.Ordinal);
        Assert.Contains("Answer to: Summarize what chang", BodyOf(mail.Data), StringComparison.Ordinal);
        Assert.Contains($"/chat/{chatId}", BodyOf(mail.Data), StringComparison.Ordinal);

        var post = Assert.Single(app.Webhook.Posts, p => p.Url.AbsolutePath == "/T000/B000/secret");
        var posted = JsonDocument.Parse(post.Body).RootElement;
        Assert.Contains("Answer to: Summarize what chang", posted.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("Morning digest", posted.GetProperty("task").GetString());
        Assert.Equal("done", posted.GetProperty("status").GetString());

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync("/api/notifications/read"));
        Assert.Equal(0, (await b.JsonAsync(await b.GetAsync("/api/notifications"))).GetProperty("unread").GetInt32());

        // Someone else neither sees it nor changes it.
        var (other, _) = await PersonAsync(f);
        Assert.Empty((await other.JsonAsync(await other.GetAsync("/api/tasks"))).GetProperty("tasks").EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.PatchAsJsonAsync(new Uri($"/api/tasks/{id}", UriKind.Relative), new { enabled = false }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/tasks/{id}/run"));
    }

    [Fact]
    public async Task Due_by_the_clock_it_runs_once_and_its_next_time_moves_on_carrying_on_one_chat_if_asked()
    {
        await using var smtp = new FakeSmtp();
        await using var f = NewApp(smtp);
        var (b, _) = await PersonAsync(f);
        var made = await b.PostAsync("/api/tasks", new { name = "Weekly", prompt = "What is new this week?", cron = "0 8 * * MON", timeZone = "UTC", sameChat = true });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await b.JsonAsync(made)).GetProperty("id").GetGuid();

        async Task DueNowAsync()
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = await db.ScheduledTasks.SingleAsync(x => x.Id == id);
            t.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            await f.Services.GetRequiredService<Scheduler>().RunDueAsync(CancellationToken.None);
        }

        await DueNowAsync();
        var first = (await FinishedAsync(b, id, 1))[0];
        Assert.False(first.GetProperty("manual").GetBoolean());
        var next = (await TaskAsync(b, id)).GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.True(next > DateTimeOffset.UtcNow);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);

        // The second run carries on the same chat: it reads the first.
        await DueNowAsync();
        var runs = await FinishedAsync(b, id, 2);
        Assert.Equal(runs[0].GetProperty("conversationId").GetGuid(), runs[1].GetProperty("conversationId").GetGuid());
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{runs[0].GetProperty("conversationId").GetGuid()}"));
        Assert.Equal("Weekly", chat.GetProperty("title").GetString());
        Assert.Equal(2, chat.GetProperty("messages").EnumerateArray().Count(m => m.GetProperty("role").GetString() == "user"));

        // Off: not due any more. Removed: its chats stay.
        await StatusAssert.Is(HttpStatusCode.OK, await b.Http.PatchAsJsonAsync(new Uri($"/api/tasks/{id}", UriKind.Relative), new { enabled = false }));
        Assert.Equal(JsonValueKind.Null, (await TaskAsync(b, id)).GetProperty("nextRunAt").ValueKind);
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/tasks/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync($"/api/chat/conversations/{runs[0].GetProperty("conversationId").GetGuid()}"));
    }

    [Fact]
    public async Task Email_needs_a_mail_server_and_tasks_can_be_switched_off_for_everyone()
    {
        await using var f = app.Create(app.ConnectionStringFor("tasks_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Schedules:PerPerson"] = "1" });
        var (b, _) = await PersonAsync(f);
        var task = new { name = "Digest", prompt = "Hi", cron = "0 9 * * *", timeZone = "UTC", email = true };
        var refused = await b.PostAsync("/api/tasks", task);
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("Settings → Email", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await b.JsonAsync(await b.GetAsync("/api/tasks"))).GetProperty("email").ValueKind);
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/tasks", task with { email = false }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await b.PostAsync("/api/tasks", task with { email = false, name = "Second" }));
    }
}
