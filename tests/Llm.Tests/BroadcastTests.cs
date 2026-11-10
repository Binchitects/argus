using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Broadcasts: an admin's message to everyone or some groups, in their bell, above the Arena's pages and in Code Arena, until dismissed or ended.</summary>
[Collection(nameof(AppCollection))]
public sealed class BroadcastTests(AppFixture app)
{
    private static async Task<(TestBrowser Browser, string Key, Guid Id)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "bc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!, made.GetProperty("id").GetGuid());
    }

    private static List<JsonElement> List(JsonElement e) => [.. e.EnumerateArray()];

    [Fact]
    public async Task A_broadcast_reaches_its_people_bell_banner_and_code_arena_until_dismissed_and_a_group_ones_only_its_members()
    {
        var f = app.Factory;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annKey, annId) = await PersonAsync(f, admin);
        var (bob, _, _) = await PersonAsync(f, admin);

        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.PostAsync("/api/admin/broadcasts", new { title = "Not mine to send" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/broadcasts", new { title = " " }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/broadcasts", new { title = "x", level = "shout" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/broadcasts", new { title = "x", until = DateTimeOffset.UtcNow.AddMinutes(-1) }));

        var title = "Maintenance tonight " + Guid.NewGuid().ToString("N")[..6];
        var sent = await admin.JsonAsync(await admin.PostAsync("/api/admin/broadcasts", new { title, body = "The Arena restarts at 22:00.", level = "warning" }));
        var id = sent.GetProperty("id").GetGuid();
        Assert.True(sent.GetProperty("sent").GetInt32() >= 2);
        // In each one's bell, and on their pages.
        var bell = (await ann.JsonAsync(await ann.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray();
        Assert.Contains(bell, n => n.GetProperty("kind").GetString() == "broadcast" && n.GetProperty("title").GetString() == title);
        Assert.Contains(List(await ann.JsonAsync(await ann.GetAsync("/api/broadcasts"))), b => b.GetProperty("id").GetGuid() == id && b.GetProperty("level").GetString() == "warning");

        // Code Arena, with the person's key: the same, and dismissed there it is dismissed everywhere.
        var codeArena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        codeArena.DefaultRequestHeaders.Authorization = new("Bearer", annKey);
        codeArena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        Assert.Contains(List(JsonDocument.Parse(await codeArena.GetStringAsync("/api/code-arena/broadcasts")).RootElement), b => b.GetProperty("id").GetGuid() == id);
        await StatusAssert.Is(HttpStatusCode.NoContent, await codeArena.PostAsync($"/api/code-arena/broadcasts/{id}/dismiss", null));
        Assert.DoesNotContain(List(await ann.JsonAsync(await ann.GetAsync("/api/broadcasts"))), b => b.GetProperty("id").GetGuid() == id);
        // Bob still has it, until it is ended.
        Assert.Contains(List(await bob.JsonAsync(await bob.GetAsync("/api/broadcasts"))), b => b.GetProperty("id").GetGuid() == id);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/broadcasts/{id}/end", new { }));
        Assert.DoesNotContain(List(await bob.JsonAsync(await bob.GetAsync("/api/broadcasts"))), b => b.GetProperty("id").GetGuid() == id);
        var listed = List(await admin.JsonAsync(await admin.GetAsync("/api/admin/broadcasts"))).Single(b => b.GetProperty("id").GetGuid() == id);
        Assert.Equal((false, 1), (listed.GetProperty("on").GetBoolean(), listed.GetProperty("dismissed").GetInt32()));

        // A group's: its members only; a critical one is not dismissed while it lasts.
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "bcg" + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { annId } }));
        var critical = (await admin.JsonAsync(await admin.PostAsync("/api/admin/broadcasts", new { title = "Only the group", level = "critical", groups = new[] { group } }))).GetProperty("id").GetGuid();
        Assert.Contains(List(await ann.JsonAsync(await ann.GetAsync("/api/broadcasts"))), b => b.GetProperty("id").GetGuid() == critical && !b.GetProperty("dismissable").GetBoolean());
        Assert.DoesNotContain(List(await bob.JsonAsync(await bob.GetAsync("/api/broadcasts"))), b => b.GetProperty("id").GetGuid() == critical);
        await StatusAssert.Is(HttpStatusCode.Conflict, await ann.PostAsync($"/api/broadcasts/{critical}/dismiss", new { }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await bob.PostAsync($"/api/broadcasts/{critical}/dismiss", new { }));
        await admin.PostAsync($"/api/admin/broadcasts/{critical}/end", new { });
    }
}
