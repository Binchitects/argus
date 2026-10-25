using System.Net;
using System.Text.Json;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The audit log as a hash chain: what changing, removing or slipping in an entry, or rewriting its end, shows; and anchors kept apart.</summary>
[Collection(nameof(AppCollection))]
public sealed class AuditChainTests(AppFixture app)
{
    private sealed record Setup(WebApplicationFactory<Program> App, string Anchors) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Directory.Delete(Anchors, recursive: true);
        }

        public async Task<int> SqlAsync(FormattableString sql)
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(sql);
        }

        public async Task<List<AuditEvent>> EntriesAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        }
    }

    private Setup NewApp()
    {
        var anchors = Directory.CreateTempSubdirectory("anchors-").FullName;
        return new Setup(app.Create(app.ConnectionStringFor("chain_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Audit:AnchorDir"] = anchors }), anchors);
    }

    private static async Task<JsonElement> ReportAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/audit-chain"))).GetProperty("report");

    private static async Task<JsonElement> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "audit-chain");

    [Fact]
    [Trait("Control", "LOG-03")]
    public async Task An_entry_changed_removed_or_slipped_in_breaks_the_chain_there_and_every_admin_is_told()
    {
        await using var s = NewApp();
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        for (var i = 0; i < 4; i++)
        {
            await admin.PostAsync("/api/admin/people", new { userName = $"ch{i}{Guid.NewGuid().ToString("N")[..6]}", email = $"ch{i}{Guid.NewGuid().ToString("N")[..6]}@example.test" });
        }
        var entries = await s.EntriesAsync();
        Assert.All(entries, e => Assert.Matches("^[0-9a-f]{64}$", e.Hash!));
        var report = await ReportAsync(admin);
        Assert.True(report.GetProperty("holds").GetBoolean());
        Assert.Equal(entries.Count, report.GetProperty("entries").GetInt64());
        await admin.PostAsync("/api/admin/compliance/audit-chain/anchor");
        Assert.Equal("ok", (await CheckAsync(admin)).GetProperty("state").GetString());

        // Changed: the chain breaks at that entry; put back, it holds again.
        var victim = entries[^3];
        await s.SqlAsync($"UPDATE audit_events SET \"Detail\" = 'nothing to see' WHERE \"Id\" = {victim.Id}");
        report = await ReportAsync(admin);
        Assert.False(report.GetProperty("holds").GetBoolean());
        Assert.Equal(victim.Id, report.GetProperty("brokenAt").GetInt64());
        var check = await CheckAsync(admin);
        Assert.Equal("fail", check.GetProperty("state").GetString());
        Assert.Contains($"broken at entry {victim.Id}", check.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var news = (await admin.JsonAsync(await admin.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(news, n => n.GetProperty("title").GetString() == "The audit log was changed");
        await s.SqlAsync($"UPDATE audit_events SET \"Detail\" = {victim.Detail} WHERE \"Id\" = {victim.Id}");
        Assert.True((await ReportAsync(admin)).GetProperty("holds").GetBoolean());

        // Removed: the one after it no longer follows.
        var next = entries[^2];
        await s.SqlAsync($"DELETE FROM audit_events WHERE \"Id\" = {victim.Id}");
        Assert.Equal(next.Id, (await ReportAsync(admin)).GetProperty("brokenAt").GetInt64());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=100"))).EnumerateArray();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "audit.chain_broken" && !e.GetProperty("success").GetBoolean());
    }

    [Fact]
    [Trait("Control", "LOG-03")]
    public async Task An_anchor_kept_apart_shows_the_end_of_the_chain_rewritten_and_entries_from_before_the_chain_are_sealed()
    {
        await using var s = NewApp();
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        await admin.PostAsync("/api/admin/people", new { userName = "anc" + Guid.NewGuid().ToString("N")[..6], email = $"anc{Guid.NewGuid().ToString("N")[..6]}@example.test" });
        var anchored = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/audit-chain/anchor"));
        var anchor = anchored.GetProperty("anchor");
        var id = anchor.GetProperty("id").GetInt64();
        // The newest line of the file apart from the database (one made as the app started may be before it).
        var line = File.ReadAllLines(Path.Combine(s.Anchors, AuditChain.AnchorFile))[^1];
        Assert.Contains(anchor.GetProperty("hash").GetString()!, line, StringComparison.Ordinal);
        var check = await CheckAsync(admin);
        Assert.Equal("ok", check.GetProperty("state").GetString());
        Assert.Contains("anchors kept apart", check.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // The end rewritten from the anchored entry on: the chain still follows, but the anchor no longer matches.
        await s.SqlAsync($"DELETE FROM audit_events WHERE \"Id\" >= {id}");
        await using (var scope = s.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditEvents.Add(new AuditEvent { Action = "person.create", Target = "someone-else", At = AuditChain.Stored(DateTimeOffset.UtcNow) });
            await db.SaveChangesAsync();
            await AuditChain.SealAsync(db);
        }
        var report = await ReportAsync(admin);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("brokenAt").ValueKind);
        Assert.Equal([id], report.GetProperty("anchorsWrong").EnumerateArray().Select(a => a.GetInt64()));
        Assert.Contains("no longer match", (await CheckAsync(admin)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Entries written before the chain existed (an upgrade) are sealed when the app starts, oldest first.
        await s.SqlAsync($"UPDATE audit_events SET \"Hash\" = NULL");
        await using (var again = s.App.WithWebHostBuilder(_ => { }))
        {
            await using var scope = again.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 50 && await db.AuditEvents.AnyAsync(e => e.Hash == null); i++)
            {
                await Task.Delay(100);
            }
            Assert.False(await db.AuditEvents.AnyAsync(e => e.Hash == null));
            var walked = await AuditChain.VerifyAsync(db, [], DateTimeOffset.UtcNow);
            Assert.True(walked.Holds);
        }
    }

    [Fact]
    [Trait("Control", "LOG-03")]
    public async Task Many_writers_at_once_still_make_one_chain()
    {
        await using var s = NewApp();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            await using var scope = s.App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Audit>().WriteAsync("test.parallel", $"writer {i}");
        }));
        await using var check = s.App.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(40, await db.AuditEvents.CountAsync(e => e.Action == "test.parallel"));
        var report = await AuditChain.VerifyAsync(db, [], DateTimeOffset.UtcNow);
        Assert.True(report.Holds, $"broken at {report.BrokenAt}");
        Assert.Equal(await db.AuditEvents.CountAsync(), report.Entries);
    }
}
