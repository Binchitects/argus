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
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email, budget = 1 }));
        var person = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var watch = app.Factory.Services.GetRequiredService<NewsWatch>();

        await app.SpendAsync(email, 0.85m);
        await watch.CheckCreditAsync(CancellationToken.None);
        await watch.CheckCreditAsync(CancellationToken.None);
        var news = Assert.Single(await NewsAsync(person));
        Assert.Equal("usage", news.GetProperty("kind").GetString());
        Assert.Equal("85% of your credit is used", news.GetProperty("title").GetString());
        Assert.Contains("$0.85 of $1.00", news.GetProperty("body").GetString(), StringComparison.Ordinal);

        await app.SpendAsync(email, 0.2m);
        await watch.CheckCreditAsync(CancellationToken.None);
        await watch.CheckCreditAsync(CancellationToken.None);
        var all = await NewsAsync(person);
        Assert.Equal(2, all.Count);
        Assert.Equal("Your credit is used up", all[0].GetProperty("title").GetString());
        Assert.Single(await NewsAsync(admin), n => n.GetProperty("title").GetString() == $"{name} has used up their credit");
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
}
