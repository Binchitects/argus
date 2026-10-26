using System.Net;
using System.Net.Http.Json;
using Llm.Api.Identity;
using Llm.Api.Retention;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The records kept for set times: the audit log (only ever the start of its chain), tool calls and the bell; and the Compliance center says so.</summary>
[Collection(nameof(AppCollection))]
public sealed class RecordRetentionTests(AppFixture app)
{
    [Fact]
    [Trait("Control", "DAT-07")]
    public async Task Records_past_their_time_go_the_audit_log_only_from_the_start_of_its_chain_and_the_check_says_how_long_each_is_kept()
    {
        await using var f = app.Create(app.ConnectionStringFor("rec_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var adminId = (await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        for (var i = 0; i < 3; i++)
        {
            await admin.PostAsync("/api/admin/people", new { userName = $"rr{i}{Guid.NewGuid().ToString("N")[..6]}", email = $"rr{i}{Guid.NewGuid().ToString("N")[..6]}@example.test" });
        }
        var old = DateTimeOffset.UtcNow.AddYears(-7);
        long late;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The first two entries are seven years old; one written later is dated as old (a call entered at its start).
            var first = await db.AuditEvents.OrderBy(e => e.Id).Take(2).Select(e => e.Id).ToListAsync();
            await db.AuditEvents.Where(e => first.Contains(e.Id)).ExecuteUpdateAsync(s => s.SetProperty(e => e.At, old));
            await scope.ServiceProvider.GetRequiredService<Audit>().WriteAsync("test.late", at: old);
            late = await db.AuditEvents.Where(e => e.Action == "test.late").Select(e => e.Id).SingleAsync();
            db.ToolCalls.AddRange(new ToolCall { ToolId = "python", Function = "run", At = DateTimeOffset.UtcNow.AddDays(-200) }, new ToolCall { ToolId = "python", Function = "run" });
            db.Notifications.AddRange(new Notification { UserId = adminId, Title = "Old news", CreatedAt = DateTimeOffset.UtcNow.AddDays(-200) }, new Notification { UserId = adminId, Title = "Fresh news" });
            await db.SaveChangesAsync();

            var swept = await scope.ServiceProvider.GetRequiredService<RecordRetention>().SweepAsync(CancellationToken.None);
            Assert.Equal((2, 1, 1), (swept.Audit, swept.ToolCalls, swept.Notifications));
            Assert.False(await db.AuditEvents.AnyAsync(e => first.Contains(e.Id)));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Id == late), "the late entry is kept");
            Assert.Equal(1, await db.ToolCalls.CountAsync());
            Assert.Equal(["Fresh news"], await db.Notifications.Select(n => n.Title).ToListAsync());
            var walked = await AuditChain.VerifyAsync(db, [], DateTimeOffset.UtcNow);
            Assert.True(walked.Holds, $"the chain holds: broken at {walked.BrokenAt} of {walked.FirstId}..{walked.LastId}");
        }
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "retention.records" && e.GetProperty("detail").GetString()!.Contains("2 audit entries", StringComparison.Ordinal));

        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"));
        Assert.Contains(view.GetProperty("retention").EnumerateArray(), r => r.GetProperty("what").GetString() == "The audit log" && r.GetProperty("rule").GetString()!.StartsWith("Kept 6 years", StringComparison.Ordinal));
        var check = view.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "records-retention");
        Assert.Equal("ok", check.GetProperty("state").GetString());

        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Retention:AuditYears", value = "3" }, new { key = "Retention:ToolCallDays", value = "0" } } }));
        check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "records-retention");
        Assert.Equal("warn", check.GetProperty("state").GetString());
        Assert.Contains("HIPAA keeps the audit log six years", check.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "LOG-02")]
    public void Loki_is_set_to_keep_logs_for_ninety_days_in_compose_and_in_the_chart()
    {
        var root = Path.GetFullPath(Path.Combine(AppFixture.PluginsPath, ".."));
        foreach (var file in new[] { "deploy/config/loki.yml", "deploy/helm/argus-arena/files/loki.yml" })
        {
            Assert.Equal("90 days", Llm.Api.Storage.StorageReport.LokiKeeps(File.ReadAllText(Path.Combine(root, file))));
        }
    }
}
