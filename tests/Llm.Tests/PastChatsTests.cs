using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Past work, findable: a chat gone quiet gets its digest (and a newer one as it goes on), and search_history finds the
/// person's past chats by what they were about, open_chat reads one, in the chat and from Code Arena alike.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class PastChatsTests(AppFixture app)
{
    private (WebApplicationFactory<Program> App, MovableClock Clock) NewApp(bool embed = false)
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var f = app.Create(app.ConnectionStringFor("past_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-history-tests", ["Modules:embed"] = embed ? "true" : "false" },
            s => s.AddSingleton<TimeProvider>(clock));
        return (f, clock);
    }

    private static async Task<(TestBrowser Browser, string Email, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test", made.GetProperty("apiKey").GetString()!);
    }

    private static async Task<Guid> SendAsync(TestBrowser b, Guid? chat, string text)
    {
        var id = chat ?? (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        return id;
    }

    private string LastResult(string email) =>
        app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email && r.Body["messages"]!.AsArray().Any(m => m!["role"]!.GetValue<string>() == "tool"))
            .Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();

    private static async Task<int> DigestAsync(WebApplicationFactory<Program> f, MovableClock clock)
    {
        clock.Now += ChatDigests.Quiet + TimeSpan.FromMinutes(1);
        return await f.Services.GetRequiredService<ChatDigests>().RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Quiet_chats_get_a_digest_and_the_model_finds_and_reads_them_again_from_the_chat_and_from_Code_Arena()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, email, key) = await PersonAsync(f);
        var vendor = await SendAsync(b, null, "We chose Northwind Traders as the parts vendor, at 4.20 a unit.");
        await SendAsync(b, null, "Plan the team offsite in Lisbon for May.");
        // Not before they have gone quiet.
        Assert.Equal(0, await f.Services.GetRequiredService<ChatDigests>().RunAsync(CancellationToken.None));
        Assert.Equal(2, await DigestAsync(f, clock));
        using (var scope = f.Services.CreateScope())
        {
            var digest = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations.SingleAsync(c => c.Id == vendor)).Digest;
            Assert.Equal("A chat\n- asked: We chose Northwind Traders as the parts vendor, at 4.20 a unit.", digest);
        }
        // Nothing new: nothing written again.
        Assert.Equal(0, await DigestAsync(f, clock));

        // Found by what it was about, and read again.
        var now = await SendAsync(b, null, """[call search_history {"query":"which parts vendor did we choose"}]""");
        var found = LastResult(email);
        Assert.Contains($"[{vendor}]", found, StringComparison.Ordinal);
        Assert.DoesNotContain("Lisbon", found, StringComparison.Ordinal);
        await SendAsync(b, now, $$"""[call open_chat {"chat":"{{vendor}}","query":"Northwind"}]""");
        Assert.Contains("#1 the person", LastResult(email), StringComparison.Ordinal);
        await SendAsync(b, now, $$"""[call open_chat {"chat":"{{vendor}}","from":1,"to":1}]""");
        Assert.Contains("#1 the person: We chose Northwind Traders as the parts vendor, at 4.20 a unit.", LastResult(email), StringComparison.Ordinal);

        // As the chat goes on, its digest is written anew from the one before.
        await SendAsync(b, vendor, "Northwind raised it to 4.60; we stay with them.");
        // That chat, and the one the search was asked in.
        Assert.Equal(2, await DigestAsync(f, clock));
        using (var scope = f.Services.CreateScope())
        {
            var digest = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations.SingleAsync(c => c.Id == vendor)).Digest;
            Assert.EndsWith("4.20 a unit.\n- asked: Northwind raised it to 4.60; we stay with them.", digest, StringComparison.Ordinal);
        }

        // Code Arena asks the same with the person's key; nobody else's chat is read.
        using var arena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        arena.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        arena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        var searched = JsonDocument.Parse(await (await arena.PostAsJsonAsync(new Uri("/api/code-arena/history/search", UriKind.Relative), new { query = "Northwind vendor" })).Content.ReadAsStringAsync()).RootElement;
        Assert.Contains($"[{vendor}]", searched.GetProperty("text").GetString(), StringComparison.Ordinal);
        var (_, _, otherKey) = await PersonAsync(f);
        arena.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherKey);
        var refused = JsonDocument.Parse(await (await arena.PostAsJsonAsync(new Uri("/api/code-arena/history/open", UriKind.Relative), new { chat = vendor.ToString() })).Content.ReadAsStringAsync()).RootElement;
        Assert.True(refused.GetProperty("error").GetBoolean());
    }

    [Fact]
    public async Task With_memory_off_a_persons_chats_get_no_digest()
    {
        var (f, clock) = NewApp();
        await using var _f = f;
        var (b, _, _) = await PersonAsync(f);
        await SendAsync(b, null, "We chose Northwind Traders as the parts vendor.");
        await StatusAssert.Is(System.Net.HttpStatusCode.OK, await b.Http.PutAsJsonAsync(new Uri("/api/account/memories/settings", UriKind.Relative), new { on = false }));
        Assert.Equal(0, await DigestAsync(f, clock));
    }

    [Fact]
    public async Task A_question_about_earlier_work_comes_with_the_past_chats_it_bears_on_by_meaning()
    {
        var (f, clock) = NewApp(embed: true);
        await using var _f = f;
        var (b, email, _) = await PersonAsync(f);
        var vendor = await SendAsync(b, null, "We chose Northwind Traders as the parts vendor, at 4.20 a unit.");
        await SendAsync(b, null, "Plan the team offsite in Lisbon for May.");
        Assert.Equal(2, await DigestAsync(f, clock));

        await SendAsync(b, null, "Which parts vendor did we choose, Northwind Traders?");
        var asked = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray()
            .Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString();
        Assert.Contains("(Past chats of the person's that may bear on this", asked, StringComparison.Ordinal);
        Assert.Contains($"[{vendor}]", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("Lisbon", asked, StringComparison.Ordinal);

        // A question about nothing earlier comes alone.
        await SendAsync(b, null, "Write a haiku about autumn leaves falling.");
        asked = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray()
            .Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString();
        Assert.DoesNotContain("Past chats", asked, StringComparison.Ordinal);
    }
}
