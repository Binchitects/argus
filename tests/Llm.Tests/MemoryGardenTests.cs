using System.Net;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The memory garden: near-duplicates merged into one, what was replaced long ago or never used let go (never a pinned
/// one), a long one tightened; each change listed under History, and undone from there.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class MemoryGardenTests(AppFixture app)
{
    private (WebApplicationFactory<Program> App, MovableClock Clock) NewApp()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var f = app.Create(app.ConnectionStringFor("garden_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), null, s => s.AddSingleton<TimeProvider>(clock));
        return (f, clock);
    }

    [Fact]
    public async Task Tidying_merges_lets_go_and_tightens_and_each_change_is_undone_from_the_history()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "gd" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var me = made.GetProperty("id").GetGuid();
        var now = clock.Now;
        Memory M(string text, Action<Memory>? more = null)
        {
            var m = new Memory { UserId = me, Text = text, CreatedAt = now.AddDays(-10), UpdatedAt = now.AddDays(-10), Uses = 1, UsedAt = now.AddDays(-2) };
            more?.Invoke(m);
            return m;
        }
        var first = M("Deploys the payment service with Podman on the build server.", m => m.Uses = 5);
        var second = M("Deploys payment service with Podman on build server.", m => (m.CreatedAt, m.UpdatedAt) = (now.AddDays(-5), now.AddDays(-1)));
        var newer = M("Hosts the docs on Netlify.");
        var replaced = M("Hosts the docs on GitHub Pages.", m => (m.ReplacedBy, m.UpdatedAt) = (newer.Id, now.AddDays(-40)));
        var stale = M("Likes the office plants watered on Fridays.", m => (m.Uses, m.UsedAt, m.CreatedAt) = (0, null, now.AddDays(-200)));
        var pinnedStale = M("Our fiscal year starts in April.", m => (m.Uses, m.UsedAt, m.CreatedAt, m.Pinned) = (0, null, now.AddDays(-200), true));
        var long1 = M("Prefers answers that start with the conclusion " + new string('x', 320) + " and then the details.");
        var other = M("Works on the payments team.");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Memories.AddRange(first, second, newer, replaced, stale, pinnedStale, long1, other);
            await db.SaveChangesAsync();
        }

        var tidied = await b.JsonAsync(await b.PostAsync("/api/account/memories/tidy", new { }));
        Assert.Equal(4, tidied.GetProperty("changes").GetInt32());
        var texts = (await b.JsonAsync(await b.GetAsync("/api/account/memories"))).GetProperty("memories").EnumerateArray()
            .ToDictionary(m => m.GetProperty("id").GetGuid(), m => m.GetProperty("text").GetString());
        // Merged into the one used most, as the model wrote it; the other gone.
        Assert.Equal("Deploys payment service with Podman on build server. (merged)", texts[first.Id]);
        Assert.False(texts.ContainsKey(second.Id));
        // Let go: replaced long ago, and never used in six months; the pinned one stays.
        Assert.False(texts.ContainsKey(replaced.Id));
        Assert.False(texts.ContainsKey(stale.Id));
        Assert.True(texts.ContainsKey(pinnedStale.Id));
        // Tightened.
        Assert.Equal(150, texts[long1.Id]!.Length);
        Assert.Equal("Works on the payments team.", texts[other.Id]);

        var history = (await b.JsonAsync(await b.GetAsync("/api/account/memories/history"))).GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(["let_go", "let_go", "merged", "tightened"], history.Select(c => c.GetProperty("action").GetString()).Order());
        var merge = history.Single(c => c.GetProperty("action").GetString() == "merged");
        Assert.Equal(2, merge.GetProperty("before").GetArrayLength());

        // Undone: both as they were.
        var undo = await b.PostAsync($"/api/account/memories/history/{merge.GetProperty("id").GetGuid()}/undo", new { });
        await StatusAssert.Is(HttpStatusCode.OK, undo);
        texts = (await b.JsonAsync(await b.GetAsync("/api/account/memories"))).GetProperty("memories").EnumerateArray()
            .ToDictionary(m => m.GetProperty("id").GetGuid(), m => m.GetProperty("text").GetString());
        Assert.Equal("Deploys the payment service with Podman on the build server.", texts[first.Id]);
        Assert.Equal("Deploys payment service with Podman on build server.", texts[second.Id]);
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.PostAsync($"/api/account/memories/history/{merge.GetProperty("id").GetGuid()}/undo", new { }));
        var stillLetGo = history.First(c => c.GetProperty("action").GetString() == "let_go").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync($"/api/account/memories/history/{stillLetGo}/undo", new { }));

        // Tidied today: not again in the daily pass; tomorrow, again.
        var garden = f.Services.GetRequiredService<MemoryGarden>();
        Assert.Equal(0, await garden.RunAsync(CancellationToken.None));
        clock.Now += TimeSpan.FromDays(1.1);
        Assert.True(await garden.RunAsync(CancellationToken.None) >= 1);

        // Nobody else's change is undone.
        var other2 = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other2.PostAsync($"/api/account/memories/history/{stillLetGo}/undo", new { }));
    }

    [Fact]
    public async Task Memories_go_out_as_Markdown_and_come_back_in_with_their_kinds_projects_and_pins()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "md" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/account/memories", new { text = "Prefers short answers.", kind = "preference", pinned = true }));
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/account/memories", new { text = "Works on the payments team." }));
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/account/memories", new { text = "Builds with make, tests with make check.", kind = "howto", scope = "project", projectKey = "gitlab.corp/payments/api" }));

        var md = await (await b.GetAsync("/api/account/memories/export")).Content.ReadAsStringAsync();
        Assert.Equal(
            "# Memory\n\n## About you\n\n### Preferences\n\n- Prefers short answers. (pinned)\n\n### Facts\n\n- Works on the payments team.\n" +
            "\n## Project gitlab.corp/payments/api\n\n### How to\n\n- Builds with make, tests with make check.\n", md);

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri("/api/account/memories", UriKind.Relative)));
        var brought = await b.JsonAsync(await b.PostAsync("/api/account/memories/import", new { markdown = md + "\n- A line with no heading is a fact.\n" }));
        Assert.Equal((4, 0), (brought.GetProperty("kept").GetInt32(), brought.GetProperty("skipped").GetInt32()));
        var all = (await b.JsonAsync(await b.GetAsync("/api/account/memories"))).GetProperty("memories").EnumerateArray().ToList();
        var shortAnswers = all.Single(m => m.GetProperty("text").GetString() == "Prefers short answers.");
        Assert.Equal(("preference", true, "import"), (shortAnswers.GetProperty("kind").GetString(), shortAnswers.GetProperty("pinned").GetBoolean(), shortAnswers.GetProperty("source").GetString()));
        var build = all.Single(m => m.GetProperty("text").GetString()!.StartsWith("Builds with make", StringComparison.Ordinal));
        Assert.Equal(("project", "howto"), (build.GetProperty("scope").GetString(), build.GetProperty("kind").GetString()));
        // Said already: left as they are.
        var again = await b.JsonAsync(await b.PostAsync("/api/account/memories/import", new { markdown = md }));
        Assert.Equal((0, 3), (again.GetProperty("kept").GetInt32(), again.GetProperty("skipped").GetInt32()));
    }
}
