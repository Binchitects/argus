using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Skills that learn: a chat that worked out a procedure proposes a skill (kept by the person, then used); a skill used there
/// that went wrong gets a better version (applied, and undone); one the person may not edit gets better as theirs; an answer
/// marked bad is read as a lesson.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class SkillLearningTests(AppFixture app)
{
    private (WebApplicationFactory<Program> App, MovableClock Clock) NewApp()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var f = app.Create(app.ConnectionStringFor("skilllearn_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), null, s => s.AddSingleton<TimeProvider>(clock));
        return (f, clock);
    }

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "sl" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> SendAsync(TestBrowser b, Guid? chat, object body)
    {
        var id = chat ?? (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", body);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        return id;
    }

    private static Task<int> ReflectAsync(WebApplicationFactory<Program> f, MovableClock clock)
    {
        clock.Now += MemoryReflection.Quiet + TimeSpan.FromMinutes(1);
        return f.Services.GetRequiredService<MemoryReflection>().RunAsync(CancellationToken.None);
    }

    private static async Task<JsonElement> LibraryAsync(TestBrowser b) => await b.JsonAsync(await b.GetAsync("/api/skills"));

    private const string Steps = "1. Read the merged changes since the last tag. 2. Group them by what they change for people. 3. Write one line each, newest first.";

    [Fact]
    public async Task A_chat_that_worked_out_a_procedure_proposes_a_skill_kept_by_the_person_then_used()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, email) = await PersonAsync(f);
        await SendAsync(b, null, new
        {
            content = $$"""Let us write the release notes the way we always do. [skill {"name":"weekly-notes","title":"Weekly release notes","description":"When the person asks for this week's notes","instructions":"{{Steps}}"}]""",
        });
        Assert.Equal(1, await ReflectAsync(f, clock));

        var library = await LibraryAsync(b);
        var learned = library.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "weekly-notes");
        Assert.True(learned.GetProperty("proposed").GetBoolean());
        Assert.True(learned.GetProperty("learned").GetBoolean());
        var change = library.GetProperty("changes").EnumerateArray().Single();
        Assert.Equal(("learned", true), (change.GetProperty("action").GetString(), change.GetProperty("proposed").GetBoolean()));
        var news = (await b.JsonAsync(await b.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "skill");
        Assert.Equal("/skills", news.GetProperty("link").GetString());

        // Not used until kept: the model is not offered it.
        await SendAsync(b, null, new { content = "Hello" });
        Assert.DoesNotContain("weekly-notes", app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email).Body.ToJsonString(), StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/skills/changes/{change.GetProperty("id").GetGuid()}/apply", new { }));
        Assert.False((await LibraryAsync(b)).GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "weekly-notes").GetProperty("proposed").GetBoolean());
        await SendAsync(b, null, new { content = "Hello again" });
        Assert.Contains("weekly-notes", app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email).Body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_skill_that_went_wrong_gets_a_better_version_applied_and_undone_and_one_not_theirs_gets_better_as_theirs()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, email) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/skills", new
        {
            name = "release-notes", title = "Release notes", description = "Release notes", instructions = "Write the release notes from the commits, as a list of everything.",
            parameters = Array.Empty<object>(), autoUse = true, sharing = "Personal",
        }));
        var better = Steps + " 4. Leave out internal refactors.";
        var chat = await SendAsync(b, null, new
        {
            content = $$"""Too long: leave out the refactors. [improve {"skill":"release-notes","instructions":"{{better}}","why":"It listed internal refactors the person did not want"}]""",
            skill = new { name = "release-notes", args = new Dictionary<string, string>() },
        });
        // The answer marked bad, with why: read with the conversation.
        var answer = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "assistant");
        await StatusAssert.Is(HttpStatusCode.OK, await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/messages/{answer.GetProperty("id").GetString()}/feedback", UriKind.Relative),
            new { up = false, comment = "listed the refactors again" }));
        Assert.Equal(1, await ReflectAsync(f, clock));
        var asked = app.Model.Requests.Last(r => r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == MemoryReflection.Tag && r.Body["user"]?.GetValue<string>() == email)
            .Body["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("The skill /release-notes used here, its instructions:\nWrite the release notes from the commits", asked, StringComparison.Ordinal);
        Assert.Contains("(The person marked this answer bad: listed the refactors again.)", asked, StringComparison.Ordinal);

        var library = await LibraryAsync(b);
        var change = library.GetProperty("changes").EnumerateArray().Single();
        Assert.Equal(("improved", "It listed internal refactors the person did not want"), (change.GetProperty("action").GetString(), change.GetProperty("reason").GetString()));
        Assert.Equal(better, change.GetProperty("after").GetString());
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/skills/changes/{change.GetProperty("id").GetGuid()}/apply", new { }));
        var skill = (await LibraryAsync(b)).GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "release-notes");
        Assert.Equal((better, 2), (skill.GetProperty("instructions").GetString(), skill.GetProperty("version").GetInt32()));

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/skills/changes/{change.GetProperty("id").GetGuid()}/undo", new { }));
        skill = (await LibraryAsync(b)).GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "release-notes");
        Assert.Equal(("Write the release notes from the commits, as a list of everything.", 3), (skill.GetProperty("instructions").GetString(), skill.GetProperty("version").GetInt32()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.PostAsync($"/api/skills/changes/{change.GetProperty("id").GetGuid()}/undo", new { }));

        // The company's: a better version becomes the person's own of that name.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/skills", new
        {
            name = "standup", title = "Stand-up summary", description = "The stand-up", instructions = "Summarize what each person did yesterday and will do today.",
            parameters = Array.Empty<object>(), autoUse = true, sharing = "Company",
        }));
        var stand = $$"""Group it by team instead. [improve {"skill":"standup","instructions":"Summarize what each team did yesterday and will do today, by team, then the blockers.","why":"The person wanted it by team"}]""";
        await SendAsync(b, null, new { content = stand, skill = new { name = "standup", args = new Dictionary<string, string>() } });
        Assert.Equal(1, await ReflectAsync(f, clock));
        var mine = (await LibraryAsync(b)).GetProperty("skills").EnumerateArray().Where(s => s.GetProperty("name").GetString() == "standup").ToList();
        Assert.Equal(2, mine.Count);
        var own = mine.Single(s => s.GetProperty("source").GetString() == "mine");
        Assert.True(own.GetProperty("proposed").GetBoolean());
        Assert.StartsWith("Summarize what each team did", own.GetProperty("instructions").GetString(), StringComparison.Ordinal);
        // Not now: it goes, and the company's stays.
        var proposal = (await LibraryAsync(b)).GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("proposed").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/skills/changes/{proposal.GetProperty("id").GetGuid()}/dismiss", new { }));
        Assert.Single((await LibraryAsync(b)).GetProperty("skills").EnumerateArray(), s => s.GetProperty("name").GetString() == "standup");
    }

    [Fact]
    public async Task Code_Arena_keeps_what_it_worked_out_as_the_persons_skill_and_a_better_version_of_one_they_have()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ca" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        using var arena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        arena.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", made.GetProperty("apiKey").GetString());
        arena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        async Task<JsonElement> LearnAsync(object body)
        {
            var res = await arena.PostAsJsonAsync(new Uri("/api/code-arena/skills/learn", UriKind.Relative), body);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        var first = await LearnAsync(new { name = "debug-flaky-test", description = "When a test fails now and then", instructions = Steps, why = "Fixed a flake together" });
        Assert.Contains("waits for the person on the Skills page", first.GetProperty("text").GetString(), StringComparison.Ordinal);
        var library = await LibraryAsync(b);
        var learned = library.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "debug-flaky-test");
        Assert.True(learned.GetProperty("proposed").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/skills/changes/{library.GetProperty("changes")[0].GetProperty("id").GetGuid()}/apply", new { }));

        // Again with that name: a better version of it, waiting.
        var again = await LearnAsync(new { name = "debug-flaky-test", description = "When a test fails now and then", instructions = Steps + " 4. Prove it with ten runs in a row.", why = "Ten runs prove it" });
        Assert.Contains("A better /debug-flaky-test waits", again.GetProperty("text").GetString(), StringComparison.Ordinal);
        var change = (await LibraryAsync(b)).GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("proposed").GetBoolean());
        Assert.Equal(("improved", "Ten runs prove it"), (change.GetProperty("action").GetString(), change.GetProperty("reason").GetString()));

        // Not a skill: refused, with why.
        var bad = await LearnAsync(new { name = "Not A Name", description = "x", instructions = "too short" });
        Assert.True(bad.GetProperty("error").GetBoolean());
    }
}
