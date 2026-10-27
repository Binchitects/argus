using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Audits, findings, certificates and waivers (Admin → Compliance → Audits).</summary>
[Collection(nameof(AppCollection))]
public sealed class AuditTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("aud_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static async Task<string> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "audits") is var c ? $"{c.GetProperty("state").GetString()}: {c.GetProperty("detail").GetString()}" : "";

    [Fact]
    [Trait("Control", "GOV-07")]
    public async Task A_finding_is_acted_on_with_its_root_cause_and_closed_only_when_its_action_is_checked()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/audits", new { title = "Q3 internal audit", kind = "casual", starts = DateTimeOffset.UtcNow }));
        var auditId = (await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/audits",
            new { title = "Q3 internal audit", kind = "internal", framework = "iso27001", scope = "Annex A.5 and A.8", auditor = "Internal audit", starts = DateTimeOffset.UtcNow.AddDays(-2) }))).GetProperty("id").GetGuid();

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/findings", new { auditId, title = "No 2FA", kind = "minor", clause = "A.8.5" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/findings", new { auditId, title = "No 2FA", kind = "minor", control = "XYZ-99" }));
        var finding = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/findings",
            new { auditId, title = "Two admins without a second factor", kind = "minor", clause = "iso27001 A.8.5(1)", control = "IAM-03", owner = "it", due = DateTimeOffset.UtcNow.AddDays(30) }));
        var id = finding.GetProperty("id").GetGuid();
        // A clause written as part of one counts for it.
        Assert.Equal("iso27001 A.8.5(1)", finding.GetProperty("clause").GetString());
        Assert.Equal("Q3 internal audit", finding.GetProperty("audit").GetString());

        var refused = await admin.PostAsync($"/api/admin/compliance/findings/{id}/done");
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("root cause", (await admin.JsonAsync(refused)).GetProperty("error").GetString(), StringComparison.Ordinal);
        await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/compliance/findings/{id}", UriKind.Relative), new
        {
            auditId, title = "Two admins without a second factor", kind = "minor", clause = "iso27001 A.8.5", control = "IAM-03", owner = "it", due = DateTimeOffset.UtcNow.AddDays(30),
            correction = "Both enrolled", rootCause = "Two-factor was optional for admins", correctiveAction = "Required of admins (Settings → Sign-in)",
        });
        Assert.Equal("done", (await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/findings/{id}/done"))).GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/compliance/findings/{id}/verify", new { worked = true, note = " " }));
        // Not effective: open again, with why.
        var again = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/findings/{id}/verify", new { worked = false, note = "A new admin has none" }));
        Assert.Equal("open", again.GetProperty("status").GetString());
        Assert.Contains("Not effective", again.GetProperty("verification").GetString(), StringComparison.Ordinal);
        await admin.PostAsync($"/api/admin/compliance/findings/{id}/done");
        var closed = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/findings/{id}/verify", new { worked = true, note = "Every admin has two factors; the policy holds" }));
        Assert.Equal("verified", closed.GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/compliance/findings/{id}", UriKind.Relative), new { title = "x", kind = "minor" }));

        var reported = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/audits/{auditId}/report", new { conclusion = "One minor nonconformity, closed." }));
        Assert.NotEqual(JsonValueKind.Null, reported.GetProperty("reportedAt").ValueKind);
        Assert.Equal(1, reported.GetProperty("findings").GetInt32());
        Assert.Equal(0, reported.GetProperty("openFindings").GetInt32());
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=30"));
        foreach (var what in new[] { "compliance.audit.plan", "compliance.finding.raise", "compliance.finding.done", "compliance.finding.reopen", "compliance.finding.verify", "compliance.audit.report" })
        {
            Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == what);
        }
    }

    [Fact]
    [Trait("Control", "GOV-07")]
    public async Task The_check_wants_an_internal_audit_each_year_findings_in_time_certificates_current_and_waivers_decided()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.StartsWith("warn: last internal audit none", await CheckAsync(admin), StringComparison.Ordinal);
        await admin.PostAsync("/api/admin/compliance/audits", new { title = "Internal audit", kind = "internal", starts = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.StartsWith("ok:", await CheckAsync(admin), StringComparison.Ordinal);

        var major = (await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/findings", new { title = "No restore drill", kind = "major", due = DateTimeOffset.UtcNow.AddDays(-1) }))).GetProperty("id").GetGuid();
        Assert.StartsWith("fail:", await CheckAsync(admin), StringComparison.Ordinal);
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Findings.Where(x => x.Id == major).ExecuteUpdateAsync(s => s.SetProperty(x => x.Due, DateTimeOffset.UtcNow.AddDays(10)));
        }

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/certificates", new { framework = "iso27001", body = "BSI", issued = DateTimeOffset.UtcNow, expires = DateTimeOffset.UtcNow.AddDays(-1) }));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync("/api/admin/compliance/certificates", new { framework = "iso27001", body = "BSI", number = "IS 123", issued = DateTimeOffset.UtcNow.AddYears(-3), expires = DateTimeOffset.UtcNow.AddDays(60) }));
        Assert.Contains("iso27001's certificate ends", await CheckAsync(admin), StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/waivers", new { control = "XYZ-99", reason = "x", until = DateTimeOffset.UtcNow.AddDays(30) }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/waivers", new { control = "IAM-03", reason = "x", until = DateTimeOffset.UtcNow.AddYears(2) }));
        var waiver = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/waivers",
            new { control = "iso27001 A.7.1", reason = "The data centre is the provider's", compensating = "Its SOC 2 report", until = DateTimeOffset.UtcNow.AddDays(30) }));
        Assert.Equal("admin", waiver.GetProperty("approvedBy").GetString());
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().ControlWaivers.ExecuteUpdateAsync(s => s.SetProperty(x => x.Until, DateTimeOffset.UtcNow.AddDays(-1)));
        }
        Assert.Contains("1 waiver past its end", await CheckAsync(admin), StringComparison.Ordinal);
        await admin.PostAsync($"/api/admin/compliance/waivers/{waiver.GetProperty("id").GetGuid()}/withdraw");
        Assert.DoesNotContain("waiver", await CheckAsync(admin), StringComparison.Ordinal);
    }
}
