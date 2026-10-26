using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>People's data requests (GDPR 15–22): taken in with their month, answered, restriction that stops learning, and erasure with its certificate.</summary>
[Collection(nameof(AppCollection))]
public sealed class DataRequestTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("dsr_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static async Task<(Guid Id, string Name, string Password, TestBrowser Browser)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "ds" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var password = made.GetProperty("password").GetString()!;
        return (made.GetProperty("id").GetGuid(), name, password, await new TestBrowser(f).SignedInAsync(name, password));
    }

    private static async Task<JsonElement> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "data-requests");

    [Fact]
    [Trait("Control", "DAT-06")]
    public async Task A_person_asks_the_admins_hear_it_is_due_in_a_month_and_answering_tells_them()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, name, _, person) = await PersonAsync(f, admin);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/account/data-requests", new { kind = "everything" }));
        var asked = await person.JsonAsync(await person.PostAsync("/api/account/data-requests", new { kind = "access", details = "Everything you hold about me, please." }));
        Assert.Equal("open", asked.GetProperty("status").GetString());
        Assert.Equal(asked.GetProperty("receivedAt").GetDateTimeOffset().AddDays(30), asked.GetProperty("dueAt").GetDateTimeOffset());
        Assert.Equal("themselves", asked.GetProperty("receivedBy").GetString());
        var news = (await admin.JsonAsync(await admin.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray();
        Assert.Contains(news, n => n.GetProperty("title").GetString() == $"{name} asked for a copy of their data");
        Assert.Contains("1 waiting", (await CheckAsync(admin)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        // A member does not see others' requests.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/admin/compliance/data-requests"));

        var id = asked.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/compliance/data-requests/{id}/complete", new { outcome = " " }));
        var done = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/data-requests/{id}/complete", new { outcome = "Their export, from Your account → Your data, and a list of where it is kept." }));
        Assert.Equal("done", done.GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/compliance/data-requests/{id}/refuse", new { outcome = "Too late" }));
        var mine = await person.JsonAsync(await person.GetAsync("/api/account/data-requests"));
        Assert.Equal("done", mine.GetProperty("requests")[0].GetProperty("status").GetString());
        Assert.Contains((await person.JsonAsync(await person.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray(),
            n => n.GetProperty("title").GetString() == "Your request for a copy of their data was answered");

        // At most five waiting at once; one past its month turns the check red.
        for (var i = 0; i < 5; i++)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await person.PostAsync("/api/account/data-requests", new { kind = "rectification", details = $"My name, {i}" }));
        }
        await StatusAssert.Is((HttpStatusCode)429, await person.PostAsync("/api/account/data-requests", new { kind = "objection" }));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.DataRequests.Where(r => r.Status == "open").Take(1).ExecuteUpdateAsync(s => s.SetProperty(r => r.DueAt, DateTimeOffset.UtcNow.AddDays(-1)));
        }
        var check = await CheckAsync(admin);
        Assert.Equal("fail", check.GetProperty("state").GetString());
        Assert.Contains("past its month", check.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("privacy.request", audit);
        Assert.Contains("privacy.done", audit);
    }

    [Fact]
    [Trait("Control", "DAT-06")]
    public async Task Restriction_stops_everything_that_learns_from_their_data_until_it_is_lifted()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (id, _, _, person) = await PersonAsync(f, admin);
        async Task<bool> MemoryOnAsync()
        {
            await using var scope = f.Services.CreateAsyncScope();
            var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id);
            return scope.ServiceProvider.GetRequiredService<Memories>().On(user);
        }
        Assert.True(await MemoryOnAsync());

        var asked = await person.JsonAsync(await person.PostAsync("/api/account/data-requests", new { kind = "objection", details = "Do not learn from my chats." }));
        var answered = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/data-requests/{asked.GetProperty("id").GetGuid()}/restrict"));
        Assert.Contains("nothing learns from it", answered.GetProperty("outcome").GetString(), StringComparison.Ordinal);
        Assert.False(await MemoryOnAsync());
        Assert.NotEqual(JsonValueKind.Null, (await person.JsonAsync(await person.GetAsync("/api/account/data-requests"))).GetProperty("restrictedAt").ValueKind);

        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/compliance/data-requests/restriction/{id}", UriKind.Relative),
            new { restricted = false, why = "they asked to lift it" }));
        Assert.True(await MemoryOnAsync());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("privacy.restrict", audit);
        Assert.Contains("privacy.lift", audit);
    }

    [Fact]
    [Trait("Control", "DAT-06")]
    public async Task Erasure_removes_everything_of_theirs_takes_them_out_of_what_others_keep_and_certifies_what_is_kept()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (id, name, password, person) = await PersonAsync(f, admin);
        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        var sent = await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "My flat is at 12 Elm Street." });
        await sent.Content.ReadAsStringAsync();
        await StatusAssert.Is(HttpStatusCode.Created, await person.PostAsync("/api/account/memories", new { text = "Lives on Elm Street." }));
        // Their words in someone else's room.
        var adminId = (await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        Guid theirs;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var room = new Conversation { UserId = adminId, Title = "Team room" };
            db.Conversations.Add(room);
            var said = new ChatMessage { ConversationId = room.Id, Sequence = 1, Role = "user", Content = "Call me on 0555 123 456.", AuthorId = id };
            db.ChatMessages.Add(said);
            await db.SaveChangesAsync();
            theirs = said.Id;
        }

        // Under legal hold, refused.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/legal-hold", UriKind.Relative), new { hold = true, reason = "Matter 7" }));
        var request = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/data-requests", new { userId = id, kind = "erasure", details = "Asked by email" }));
        Assert.Equal("admin", request.GetProperty("receivedBy").GetString());
        var rid = request.GetProperty("id").GetGuid();
        var held = await admin.PostAsync($"/api/admin/compliance/data-requests/{rid}/erase");
        await StatusAssert.Is(HttpStatusCode.BadRequest, held);
        Assert.Contains("legal hold", (await admin.JsonAsync(held)).GetProperty("error").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/legal-hold", UriKind.Relative), new { hold = false }));

        var erased = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/data-requests/{rid}/erase"));
        Assert.Equal("done", erased.GetProperty("status").GetString());
        Assert.True(erased.GetProperty("certificate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, erased.GetProperty("userId").ValueKind);
        Assert.Equal(name, erased.GetProperty("person").GetString());

        // Gone, and gone from what others keep.
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await new TestBrowser(f).LoginAsync(name, password));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Id == id));
            Assert.False(await db.Conversations.AnyAsync(c => c.UserId == id));
            Assert.False(await db.Memories.AnyAsync(m => m.UserId == id));
            var kept = await db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == theirs);
            Assert.Equal("(erased at their request)", kept.Content);
            Assert.Null(kept.AuthorId);
        }
        var certificate = await (await admin.GetAsync($"/api/admin/compliance/data-requests/{rid}/certificate")).Content.ReadAsStringAsync();
        Assert.StartsWith("# Certificate of erasure", certificate, StringComparison.Ordinal);
        Assert.Contains("chats and rooms of theirs", certificate, StringComparison.Ordinal);
        Assert.Contains("1 memories", certificate, StringComparison.Ordinal);
        Assert.Contains("1 their messages in other people's rooms", certificate, StringComparison.Ordinal);
        Assert.Contains("entries of the audit log that name them", certificate, StringComparison.Ordinal);
        Assert.Contains("## Backups", certificate, StringComparison.Ordinal);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("privacy.erase", audit);
        Assert.Contains("person.delete", audit);
    }
}
