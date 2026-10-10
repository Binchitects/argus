using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The bell: credit thresholds and system alerts, each said once.</summary>
[Collection(nameof(AppCollection))]
public sealed class NotificationsTests(AppFixture app)
{
    private Task<TestBrowser> Admin() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<List<JsonElement>> NewsAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task Credit_at_80_percent_and_used_up_is_said_once_each_and_the_admins_hear_of_the_second()
    {
        var admin = await Admin();
        var name = "cr" + Guid.NewGuid().ToString("N")[..8];
        var email = $"{name}@example.test";
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email, credits = new { chat = 1 } }));
        var person = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var watch = app.Factory.Services.GetRequiredService<NewsWatch>();

        await app.SpendAsync(email, 0.85m, DateTimeOffset.UtcNow);
        await watch.CheckCreditAsync(CancellationToken.None);
        await watch.CheckCreditAsync(CancellationToken.None);
        var news = Assert.Single(await NewsAsync(person));
        Assert.Equal("usage", news.GetProperty("kind").GetString());
        Assert.Equal("85% of your chat credit is used", news.GetProperty("title").GetString());
        Assert.Contains("$0.85 of $1.00", news.GetProperty("body").GetString(), StringComparison.Ordinal);

        await app.SpendAsync(email, 0.2m, DateTimeOffset.UtcNow);
        await watch.CheckCreditAsync(CancellationToken.None);
        await watch.CheckCreditAsync(CancellationToken.None);
        var all = await NewsAsync(person);
        Assert.Equal(2, all.Count);
        Assert.Equal("Your chat credit is used up", all[0].GetProperty("title").GetString());
        Assert.Single(await NewsAsync(admin), n => n.GetProperty("title").GetString() == $"{name} has used up their chat credit");

        // Cleared: gone from the bell, one or all, and news said once is not said again.
        await StatusAssert.Is(System.Net.HttpStatusCode.NoContent, await person.Http.DeleteAsync(new Uri($"/api/notifications/{all[1].GetProperty("id").GetString()}", UriKind.Relative)));
        Assert.Single(await NewsAsync(person));
        await StatusAssert.Is(System.Net.HttpStatusCode.NoContent, await person.Http.DeleteAsync(new Uri("/api/notifications", UriKind.Relative)));
        await watch.CheckCreditAsync(CancellationToken.None);
        Assert.Empty(await NewsAsync(person));
        Assert.Equal(0, (await person.JsonAsync(await person.GetAsync("/api/notifications"))).GetProperty("unread").GetInt32());
        await StatusAssert.Is(System.Net.HttpStatusCode.NotFound, await admin.Http.DeleteAsync(new Uri($"/api/notifications/{all[0].GetProperty("id").GetString()}", UriKind.Relative)));
    }

    [Fact]
    public async Task A_system_alert_that_starts_firing_reaches_every_admin_once()
    {
        var admin = await Admin();
        var watch = app.Factory.Services.GetRequiredService<NewsWatch>();
        var started = DateTimeOffset.UtcNow.ToString("O");
        app.Observe.Answers["/api/v2/alerts"] = _ => $$$"""
            [{"labels":{"alertname":"DiskFull","severity":"critical","mount":"/library"},"annotations":{"summary":"Disk almost full","description":"/library is 97% full."},
              "startsAt":"{{{started}}}","status":{"state":"active","silencedBy":[],"inhibitedBy":[]}},
             {"labels":{"alertname":"GpuHot","severity":"warning"},"annotations":{"summary":"GPU hot"},"startsAt":"{{{started}}}",
              "status":{"state":"suppressed","silencedBy":["s1"],"inhibitedBy":[]}}]
            """;
        try
        {
            await watch.CheckAlertsAsync(CancellationToken.None);
            await watch.CheckAlertsAsync(CancellationToken.None);
        }
        finally
        {
            app.Observe.Answers.TryRemove("/api/v2/alerts", out _);
        }
        var alert = Assert.Single(await NewsAsync(admin), n => n.GetProperty("title").GetString() == "Critical: Disk almost full" && n.GetProperty("body").GetString() == "/library is 97% full.");
        Assert.Equal("alert", alert.GetProperty("kind").GetString());
        Assert.Equal("/admin/alerts", alert.GetProperty("link").GetString());
        // A silenced one is not news.
        Assert.DoesNotContain(await NewsAsync(admin), n => n.GetProperty("title").GetString() == "Warning: GPU hot");
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

    private static Task<HttpResponseMessage> SaveAsync(TestBrowser admin, string key, string value) =>
        admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new[] { new { key, value } } });

    [Fact]
    public async Task Credit_and_alert_news_go_by_email_too_and_the_admins_news_to_the_alerts_webhook_once_each()
    {
        await using var smtp = new FakeSmtp();
        await using var f = app.Create(app.ConnectionStringFor("news_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-news-tests",
            ["Mail:Host"] = "127.0.0.1", ["Mail:Port"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Mail:StartTls"] = "false",
            ["Mail:From"] = "Argus Arena <arena@example.test>",
            ["Schedules:WebhookHosts"] = "hooks.example.test",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var watch = f.Services.GetRequiredService<NewsWatch>();

        // The webhook is a secret, and only to a host an admin allowed (as a task's).
        var refused = await SaveAsync(admin, "Notifications:AlertsWebhook", "https://intranet.example.test/hook");
        await StatusAssert.Is(System.Net.HttpStatusCode.BadRequest, refused);
        Assert.Contains("intranet.example.test is not a webhook host", (await admin.JsonAsync(refused)).GetProperty("errors").GetProperty("Notifications:AlertsWebhook").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(System.Net.HttpStatusCode.OK, await SaveAsync(admin, "Notifications:AlertsWebhook", "https://hooks.example.test/alerts/T0/secret"));
        Assert.DoesNotContain("/alerts/T0/secret", await (await admin.GetAsync("/api/admin/config")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        List<(Uri Url, string Body)> Posts() { lock (app.Webhook.Posts) { return [.. app.Webhook.Posts.Where(p => p.Url.AbsolutePath == "/alerts/T0/secret")]; } }

        var name = "nm" + Guid.NewGuid().ToString("N")[..8];
        var email = $"{name}@example.test";
        await admin.PostAsync("/api/admin/people", new { userName = name, email, credits = new { chat = 1 } });

        // 80%: the person, by email too, once; the admins' webhook hears nothing of it.
        await app.SpendAsync(email, 0.85m, DateTimeOffset.UtcNow);
        await watch.CheckCreditAsync(CancellationToken.None);
        await watch.CheckCreditAsync(CancellationToken.None);
        var mail = Assert.Single(smtp.Received);
        Assert.Contains(email, mail.To[0], StringComparison.Ordinal);
        Assert.Contains("You have spent $0.85 of $1.00 this month.", BodyOf(mail.Data), StringComparison.Ordinal);
        Assert.Contains($"Open it: https://{AppFixture.Domain}/", BodyOf(mail.Data), StringComparison.Ordinal);
        Assert.Empty(Posts());

        // Used up: the person and the admin by email, and the webhook, once.
        await app.SpendAsync(email, 0.2m, DateTimeOffset.UtcNow);
        await watch.CheckCreditAsync(CancellationToken.None);
        await watch.CheckCreditAsync(CancellationToken.None);
        Assert.Equal(3, smtp.Received.Count);
        Assert.Single(smtp.Received, m => m.To[0].Contains("admin@llm.test", StringComparison.Ordinal) && BodyOf(m.Data).Contains("The chat's answers are refused until you add credit", StringComparison.Ordinal));
        var post = JsonDocument.Parse(Assert.Single(Posts()).Body).RootElement;
        Assert.Equal($"{name} has used up their chat credit", post.GetProperty("title").GetString());
        Assert.Equal("usage", post.GetProperty("kind").GetString());
        Assert.Equal($"https://{AppFixture.Domain}/admin/people", post.GetProperty("url").GetString());
        Assert.StartsWith($"{name} has used up their chat credit\n\n$1.05 of $1.00 this month.", post.GetProperty("text").GetString(), StringComparison.Ordinal);

        // An alert that starts firing: each admin by email and the webhook, once.
        var started = DateTimeOffset.UtcNow.ToString("O");
        app.Observe.Answers["/api/v2/alerts"] = _ => $$$"""
            [{"labels":{"alertname":"EngineDown","severity":"critical"},"annotations":{"summary":"The engine is down","description":"llamacpp has not answered for 5 minutes."},
              "startsAt":"{{{started}}}","status":{"state":"active","silencedBy":[],"inhibitedBy":[]}}]
            """;
        try
        {
            await watch.CheckAlertsAsync(CancellationToken.None);
            await watch.CheckAlertsAsync(CancellationToken.None);
            Assert.Equal(4, smtp.Received.Count);
            Assert.Contains("llamacpp has not answered for 5 minutes.", BodyOf(smtp.Received[^1].Data), StringComparison.Ordinal);
            Assert.Equal(2, Posts().Count);
            Assert.Equal("Critical: The engine is down", JsonDocument.Parse(Posts()[^1].Body).RootElement.GetProperty("title").GetString());

            // Email turned off: the bell and the webhook still have the next one.
            await StatusAssert.Is(System.Net.HttpStatusCode.OK, await SaveAsync(admin, "Notifications:Email", "false"));
            app.Observe.Answers["/api/v2/alerts"] = _ => $$$"""
                [{"labels":{"alertname":"DiskFull","severity":"warning"},"annotations":{"summary":"Disk filling"},
                  "startsAt":"{{{started}}}","status":{"state":"active","silencedBy":[],"inhibitedBy":[]}}]
                """;
            await watch.CheckAlertsAsync(CancellationToken.None);
            Assert.Equal(4, smtp.Received.Count);
            Assert.Equal(3, Posts().Count);
            Assert.Contains(await NewsAsync(admin), n => n.GetProperty("title").GetString() == "Warning: Disk filling");
        }
        finally
        {
            app.Observe.Answers.TryRemove("/api/v2/alerts", out _);
        }
    }
}
