using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Api.Retention;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>A clock a test moves: retention is measured against it.</summary>
public sealed class MovableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Retention per group, legal hold, and the exports of a person's data.</summary>
[Collection(nameof(AppCollection))]
public sealed class RetentionTests(AppFixture app)
{
    private (WebApplicationFactory<Program> App, MovableClock Clock) NewApp(Dictionary<string, string?>? settings = null)
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var f = app.Create(app.ConnectionStringFor("ret_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings,
            s => s.AddSingleton<TimeProvider>(clock));
        return (f, clock);
    }

    private static async Task<(TestBrowser Browser, Guid Id, string Name)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "rt" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (b, made.GetProperty("id").GetGuid(), name);
    }

    private static async Task<Guid> ChatAsync(TestBrowser b, string text, Guid[]? attachments = null)
    {
        var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text, attachments });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        return id;
    }

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        var res = await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> GroupAsync(TestBrowser admin, string name, object policies, params Guid[] members)
    {
        var id = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{id}/policies", UriKind.Relative), policies));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = members }));
        return id;
    }

    private static async Task<string> AuditAsync(TestBrowser admin) => await (await admin.GetAsync("/api/admin/audit?take=1000")).Content.ReadAsStringAsync();

    /// <summary>The sweep, run as the background job runs it, with the clock moved on and back.</summary>
    private static async Task<Erased> SweepAfterAsync(WebApplicationFactory<Program> f, MovableClock clock, TimeSpan later)
    {
        var then = clock.Now;
        clock.Now = then + later;
        try
        {
            await using var scope = f.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<Retention>().SweepAsync();
        }
        finally
        {
            clock.Now = then;
        }
    }

    [Fact]
    public async Task A_chat_past_its_groups_retention_is_deleted_with_its_files_and_one_under_legal_hold_is_not()
    {
        var (f, clock) = NewApp(new() { ["Retention:Days"] = "365" });
        await using var _ = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annName) = await PersonAsync(f, admin);
        var (ben, benId, benName) = await PersonAsync(f, admin);
        var (cy, _, cyName) = await PersonAsync(f, admin);
        // A longer group does not win: the shortest of a person's groups applies.
        await GroupAsync(admin, "Short " + annName, new { retentionDays = 30 }, annId, benId);
        await GroupAsync(admin, "Long " + annName, new { retentionDays = 3650 }, annId);
        Assert.Equal(30, (await ann.JsonAsync(await ann.GetAsync("/api/account/data"))).GetProperty("retentionDays").GetInt32());
        Assert.Equal(365, (await cy.JsonAsync(await cy.GetAsync("/api/account/data"))).GetProperty("retentionDays").GetInt32());

        var file = await UploadAsync(ann, "notes.txt", "the quarterly numbers");
        var annChat = await ChatAsync(ann, "Summarise my notes", [file]);
        var unsent = await UploadAsync(ann, "draft.txt", "never sent");
        var benChat = await ChatAsync(ben, "Keep this");
        var cyChat = await ChatAsync(cy, "Company default");
        var hold = await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{benId}/legal-hold", UriKind.Relative), new { hold = true, reason = "Matter 42" });
        await StatusAssert.Is(HttpStatusCode.OK, hold);

        // Not yet: 29 days on, nothing goes.
        Assert.Equal(Erased.None, await SweepAfterAsync(f, clock, TimeSpan.FromDays(29)));
        var erased = await SweepAfterAsync(f, clock, TimeSpan.FromDays(31));
        Assert.Equal(1, erased.Chats);
        Assert.Equal(2, erased.Files);

        await StatusAssert.Is(HttpStatusCode.NotFound, await ann.GetAsync($"/api/chat/conversations/{annChat}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ann.GetAsync($"/api/chat/attachments/{file}/content"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ann.GetAsync($"/api/chat/attachments/{unsent}/content"));
        // Under hold: kept. Under the company's 365 days: kept.
        await StatusAssert.Is(HttpStatusCode.OK, await ben.GetAsync($"/api/chat/conversations/{benChat}"));
        await StatusAssert.Is(HttpStatusCode.OK, await cy.GetAsync($"/api/chat/conversations/{cyChat}"));
        var audit = await AuditAsync(admin);
        Assert.Contains("1 chats and 2 files older than 30 days", audit, StringComparison.Ordinal);
        Assert.Contains("Matter 42", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("quarterly", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonDocument.Parse(audit).RootElement.EnumerateArray(),
            e => e.GetProperty("action").GetString() == "retention.delete" && e.GetProperty("target").GetString() == benName);

        // The hold ends: the next sweep deletes Ben's old chat too.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{benId}/legal-hold", UriKind.Relative), new { hold = false }));
        Assert.Equal(1, (await SweepAfterAsync(f, clock, TimeSpan.FromDays(31))).Chats);
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/chat/conversations/{benChat}"));
        Assert.Contains(cyName, (await admin.JsonAsync(await admin.GetAsync("/api/admin/people"))).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_person_on_legal_hold_who_deletes_a_chat_only_hides_it_until_the_hold_ends()
    {
        var (f, _) = NewApp();
        await using var __ = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ben, benId, benName) = await PersonAsync(f, admin);
        var kept = await ChatAsync(ben, "Something to keep");
        var deleted = await ChatAsync(ben, "Something I delete");

        var noReason = await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{benId}/legal-hold", UriKind.Relative), new { hold = true, reason = " " });
        await StatusAssert.Is(HttpStatusCode.BadRequest, noReason);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{benId}/legal-hold", UriKind.Relative), new { hold = true, reason = "Matter 7" }));
        var person = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{benId}"))).GetProperty("person");
        Assert.Equal("Matter 7", person.GetProperty("legalHoldReason").GetString());

        // Gone for Ben, kept for the hold.
        await StatusAssert.Is(HttpStatusCode.NoContent, await ben.Http.DeleteAsync(new Uri($"/api/chat/conversations/{deleted}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/chat/conversations/{deleted}"));
        var list = (await ben.JsonAsync(await ben.GetAsync("/api/chat/conversations"))).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { kept }, list);
        Assert.True(await HiddenAsync(f, deleted));
        // Nor may an admin delete a person on hold.
        var delete = await admin.Http.DeleteAsync(new Uri($"/api/admin/people/{benId}", UriKind.Relative));
        await StatusAssert.Is(HttpStatusCode.BadRequest, delete);
        Assert.Contains("legal hold", await delete.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // eDiscovery has the hidden chat; Ben's own copy does not.
        var ediscovery = await ZipAsync(await admin.GetAsync($"/api/admin/people/{benId}/export"));
        var own = await ZipAsync(await ben.GetAsync("/api/account/export"));
        Assert.Equal(2, ediscovery.Keys.Count(k => k.StartsWith("chats/", StringComparison.Ordinal) && k.EndsWith(".json", StringComparison.Ordinal)));
        Assert.Contains(ediscovery.Values, v => v.Contains("Something I delete", StringComparison.Ordinal) && v.Contains("\"deletedAt\": \"", StringComparison.Ordinal));
        Assert.Single(own.Keys, k => k.StartsWith("chats/", StringComparison.Ordinal) && k.EndsWith(".json", StringComparison.Ordinal));
        Assert.DoesNotContain(own.Values, v => v.Contains("Something I delete", StringComparison.Ordinal));
        var audit = await AuditAsync(admin);
        Assert.Contains("eDiscovery: 2 chats", audit, StringComparison.Ordinal);
        Assert.Contains("account.export", audit, StringComparison.Ordinal);

        // The hold ends: what Ben deleted is erased now.
        var end = await admin.JsonAsync(await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{benId}/legal-hold", UriKind.Relative), new { hold = false }));
        Assert.Equal(1, end.GetProperty("erasedChats").GetInt32());
        Assert.False(await HiddenAsync(f, deleted));
        Assert.Contains("person.legal_hold_end", await AuditAsync(admin), StringComparison.Ordinal);
        Assert.Contains(benName, await AuditAsync(admin), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_persons_export_holds_their_profile_chats_as_json_and_markdown_and_their_files()
    {
        var (f, _) = NewApp();
        await using var __ = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, _, annName) = await PersonAsync(f, admin);
        var file = await UploadAsync(ann, "plan.txt", "step one, step two");
        await ChatAsync(ann, "Read my plan", [file]);
        var res = await ann.GetAsync("/api/account/export");
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"{annName}-data-", res.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.Ordinal);
        var zip = await ZipAsync(res);
        Assert.Contains("README.txt", zip.Keys);
        Assert.Contains($"\"userName\": \"{annName}\"", zip["person.json"], StringComparison.Ordinal);
        Assert.Contains("projects.json", zip.Keys);
        Assert.Contains("tasks.json", zip.Keys);
        var md = zip.Single(e => e.Key.EndsWith(".md", StringComparison.Ordinal)).Value;
        Assert.Contains("## You", md, StringComparison.Ordinal);
        Assert.Contains("Read my plan", md, StringComparison.Ordinal);
        Assert.Contains("## Assistant", md, StringComparison.Ordinal);
        Assert.Equal("step one, step two", zip.Single(e => e.Key.StartsWith("files/", StringComparison.Ordinal) && e.Key.EndsWith("plan.txt", StringComparison.Ordinal)).Value);
    }

    private static async Task<bool> HiddenAsync(WebApplicationFactory<Program> f, Guid chat)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == chat && c.DeletedAt != null);
    }

    private static async Task<Dictionary<string, string>> ZipAsync(HttpResponseMessage res)
    {
        await StatusAssert.Is(HttpStatusCode.OK, res);
        using var zip = new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        var entries = new Dictionary<string, string>();
        foreach (var e in zip.Entries)
        {
            using var reader = new StreamReader(e.Open());
            entries[e.FullName] = await reader.ReadToEndAsync();
        }
        return entries;
    }
}
