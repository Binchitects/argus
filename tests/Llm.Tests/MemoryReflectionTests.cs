using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Memories that form themselves: a chat gone quiet is read, and what is worth keeping is proposed (kept when the person
/// says so, or at once when they keep them on their own); a Code Arena session's project memories are kept and said.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class MemoryReflectionTests(AppFixture app)
{
    private (WebApplicationFactory<Program> App, MovableClock Clock) NewApp()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var f = app.Create(app.ConnectionStringFor("reflect_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), null, s => s.AddSingleton<TimeProvider>(clock));
        return (f, clock);
    }

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "rf" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<Guid> ChatAsync(TestBrowser b, string text)
    {
        var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        return id;
    }

    private static async Task<List<JsonElement>> MemoriesAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/account/memories"))).GetProperty("memories").EnumerateArray()];

    private string SystemSentFor(string email) =>
        app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email && r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() != MemoryReflection.Tag)
            .Body["messages"]![0]!["content"]!.GetValue<string>();

    private static Task<int> ReflectAsync(WebApplicationFactory<Program> f, MovableClock clock)
    {
        clock.Now += MemoryReflection.Quiet + TimeSpan.FromMinutes(1);
        return f.Services.GetRequiredService<MemoryReflection>().RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_quiet_chat_proposes_what_is_worth_keeping_and_the_person_keeps_it()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, _, email) = await PersonAsync(f);
        await ChatAsync(b, """I always deploy with Podman, never Docker. [propose {"text":"Deploys with Podman, never Docker.","kind":"howto","scope":"person"}]""");
        // Not before it has gone quiet.
        Assert.Equal(0, await f.Services.GetRequiredService<MemoryReflection>().RunAsync(CancellationToken.None));
        Assert.Equal(1, await ReflectAsync(f, clock));

        var proposed = Assert.Single(await MemoriesAsync(b));
        Assert.Equal((true, "howto", "reflected"), (proposed.GetProperty("proposed").GetBoolean(), proposed.GetProperty("kind").GetString(), proposed.GetProperty("source").GetString()));
        var news = (await b.JsonAsync(await b.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("memory", "/memory"), (news.GetProperty("kind").GetString(), news.GetProperty("link").GetString()));
        Assert.StartsWith("Remember this from", news.GetProperty("title").GetString(), StringComparison.Ordinal);
        // Not read until kept.
        await ChatAsync(b, "Hello");
        Assert.DoesNotContain("Podman", SystemSentFor(email), StringComparison.Ordinal);

        var id = proposed.GetProperty("id").GetString();
        var kept = await b.JsonAsync(await b.PostAsync($"/api/account/memories/{id}/keep"));
        Assert.False(kept.GetProperty("proposed").GetBoolean());
        await ChatAsync(b, "Hello again");
        Assert.Contains("Deploys with Podman, never Docker. (how to)", SystemSentFor(email), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.PostAsync($"/api/account/memories/{id}/keep"));

        // Read once: nothing new, nothing proposed again (the chats said "Hello" with nothing to keep).
        await ReflectAsync(f, clock);
        Assert.Single(await MemoriesAsync(b));
    }

    [Fact]
    public async Task A_chat_with_a_skill_call_cut_short_is_still_read()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, _, _) = await PersonAsync(f);
        var id = await ChatAsync(b, """I always deploy with Podman, never Docker. [propose {"text":"Deploys with Podman, never Docker.","kind":"howto","scope":"person"}]""");
        // An answer whose use_skill call the model wrote cut short: its arguments are not JSON.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var last = await db.ChatMessages.Where(m => m.ConversationId == id).OrderByDescending(m => m.Sequence).FirstAsync();
            last.ToolCallsJson = $$$"""[{"id":"c1","type":"function","function":{"name":"{{{Llm.Api.Chat.Tools.SkillTool.Function}}}","arguments":"{\"name\": \"rel"}}]""";
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await ReflectAsync(f, clock));
        Assert.Single(await MemoriesAsync(b));
    }

    [Fact]
    public async Task Kept_on_its_own_a_proposal_that_says_otherwise_replaces_the_old_memory()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, _, email) = await PersonAsync(f);
        var auto = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri("/api/account/memories/settings", UriKind.Relative), new { on = true, auto = true }));
        Assert.True(auto.GetProperty("auto").GetBoolean());
        var old = await b.JsonAsync(await b.PostAsync("/api/account/memories", new { text = "Publishes the docs site with GitHub Pages." }));
        await ChatAsync(b, """We moved the docs site from GitHub Pages to Netlify. [propose {"text":"Publishes the docs site with Netlify.","kind":"decision","scope":"person","replaces":"FIRST"}]""");
        Assert.Equal(1, await ReflectAsync(f, clock));

        var listed = await MemoriesAsync(b);
        var now = Assert.Single(listed);
        Assert.Equal(("Publishes the docs site with Netlify.", false), (now.GetProperty("text").GetString(), now.GetProperty("proposed").GetBoolean()));
        var replaced = (await b.JsonAsync(await b.GetAsync("/api/account/memories?replaced=true"))).GetProperty("memories").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == old.GetProperty("id").GetString());
        Assert.Equal(now.GetProperty("id").GetString(), replaced.GetProperty("replacedBy").GetString());
        await ChatAsync(b, "Where is the docs site?");
        Assert.DoesNotContain("GitHub Pages", SystemSentFor(email), StringComparison.Ordinal);
        Assert.StartsWith("Remembered from", (await b.JsonAsync(await b.GetAsync("/api/notifications"))).GetProperty("items")[0].GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Code_Arena_sessions_project_memories_are_kept_and_rooms_and_tasks_are_not_read()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, me, _) = await PersonAsync(f);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var session = new Conversation { UserId = me, Origin = CodeArenaSync.Origin, OriginRef = "s1", ProjectKey = "gitlab.corp/payments/api", Title = "Fix the build" };
            var room = new Conversation { UserId = me, Kind = RoomMember.Room, Title = "A room" };
            db.Conversations.AddRange(session, room);
            db.ChatMessages.AddRange(
                new ChatMessage { ConversationId = session.Id, Role = "user", Sequence = 1, Content = """The tests need tools/dn, not dotnet. [propose {"text":"Build and test with tools/dn.","kind":"howto","scope":"project"}]""" },
                new ChatMessage { ConversationId = session.Id, Role = "assistant", Sequence = 2, Content = "Done: I ran tools/dn test." },
                new ChatMessage { ConversationId = room.Id, Role = "user", Sequence = 1, Content = """[propose {"text":"Likes rooms.","kind":"fact","scope":"person"}]""" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await ReflectAsync(f, clock));
        var kept = Assert.Single(await MemoriesAsync(b));
        Assert.Equal(("Build and test with tools/dn.", "project", "gitlab.corp/payments/api", false),
            (kept.GetProperty("text").GetString(), kept.GetProperty("scope").GetString(), kept.GetProperty("project").GetString(), kept.GetProperty("proposed").GetBoolean()));
        Assert.StartsWith("Remembered from", (await b.JsonAsync(await b.GetAsync("/api/notifications"))).GetProperty("items")[0].GetProperty("title").GetString(), StringComparison.Ordinal);
    }
}
