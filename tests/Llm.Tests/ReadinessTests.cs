using System.IO.Compression;
using System.Net;
using Llm.Api.Compliance;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Readiness for each framework, its auditor's package, and the checks' daily history.</summary>
[Collection(nameof(AppCollection))]
public sealed class ReadinessTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("rdy_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static async Task<Dictionary<string, string>> PackageAsync(TestBrowser admin, string framework, string query = "")
    {
        var res = await admin.GetAsync($"/api/admin/compliance/readiness/{framework}/package{query}");
        await StatusAssert.Is(HttpStatusCode.OK, res);
        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync());
        return zip.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
    }

    [Fact]
    [Trait("Control", "GOV-11")]
    public async Task Each_framework_says_how_ready_and_what_is_missing_and_its_package_holds_what_its_auditor_reads()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var all = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/readiness"));
        Assert.Equal(14, all.GetArrayLength());
        var iso = all.EnumerateArray().Single(r => r.GetProperty("framework").GetString() == "iso27001");
        Assert.InRange(iso.GetProperty("score").GetInt32(), 1, 99);
        var gaps = iso.GetProperty("gaps").EnumerateArray().Select(g => g.GetProperty("what").GetString()!).ToList();
        Assert.Contains(gaps, g => g.StartsWith("The Statement of Applicability is approved and current", StringComparison.Ordinal));
        Assert.Contains(gaps, g => g.StartsWith("the company's part not attested within the year", StringComparison.Ordinal));
        // Attested, a control's part counts.
        await admin.PostAsync("/api/admin/compliance/controls/IAM-01/attest", new { note = "Password policy v2" });
        var after = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/readiness"))).EnumerateArray().Single(r => r.GetProperty("framework").GetString() == "iso27001");
        Assert.Equal(iso.GetProperty("attested").GetInt32() + 1, after.GetProperty("attested").GetInt32());

        var p = await PackageAsync(admin, "iso27001");
        foreach (var name in new[] { "README.txt", "clauses.csv", "readiness.json", "check-history.csv", "controls.yaml", "risks.csv", "statement-of-applicability.csv", "policies.csv", "audits.csv", "findings.csv", "suppliers.csv" })
        {
            Assert.True(p.ContainsKey(name), name);
        }
        Assert.Contains("A.8.5,Secure authentication,", p["clauses.csv"], StringComparison.Ordinal);
        Assert.Contains("IAM-01:", p["clauses.csv"], StringComparison.Ordinal);
        Assert.DoesNotContain("ai-systems.csv", p.Keys);

        // The scheme's drafts.
        Assert.StartsWith("# Argus Arena: Security Target", (await PackageAsync(admin, "commoncriteria"))["security-target.md"], StringComparison.Ordinal);
        Assert.StartsWith("subcategory,outcome,", (await PackageAsync(admin, "nistcsf"))["profile.csv"], StringComparison.Ordinal);
        await admin.PostAsync("/api/admin/compliance/ai-systems", new { name = "CV screener", kind = "assistant", @class = "high", highRiskArea = "employment and workers management", role = "deployer", purpose = "Reads CVs" });
        var act = await PackageAsync(admin, "euaiact");
        Assert.Contains("**To write.**", act["annex-iv/cv-screener.md"], StringComparison.Ordinal);
        Assert.Contains("employment and workers management", act["annex-iv/cv-screener.md"], StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync("/api/admin/compliance/readiness/nope/package"));
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "compliance.package" && e.GetProperty("target").GetString() == "euaiact");
    }

    [Fact]
    [Trait("Control", "GOV-11")]
    public async Task The_checks_are_kept_once_a_day_and_the_package_reads_them_over_the_period()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var job = f.Services.GetRequiredService<ComplianceSnapshots>();
        // Today's once, however often it runs and on however many replicas (the app ran it as it started).
        await job.RunAsync(CancellationToken.None);
        Assert.Equal(0, await job.RunAsync(CancellationToken.None));
        var first = ComplianceChecks.All.Count;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(first, await db.ComplianceSnapshots.CountAsync());
            // A day long past the six years is taken out at the next run.
            db.ComplianceSnapshots.Add(new Llm.Core.Compliance.ComplianceSnapshot { Day = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-7)), Check = "passwords", State = "ok" });
            db.ComplianceSnapshots.Add(new Llm.Core.Compliance.ComplianceSnapshot { Day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-40)), Check = "passwords", State = "warn", Detail = "then" });
            await db.SaveChangesAsync();
        }
        await job.RunAsync(CancellationToken.None);
        using (var scope = f.Services.CreateScope())
        {
            Assert.Equal(first + 1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ComplianceSnapshots.CountAsync());
        }
        var p = await PackageAsync(admin, "soc2");
        var lines = p["check-history.csv"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.Contains(",passwords,warn,then", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(",passwords,ok,", StringComparison.Ordinal));
        // A shorter period leaves out what is before it.
        var lastWeek = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain(",passwords,warn,then", (await PackageAsync(admin, "soc2", $"?from={lastWeek}"))["check-history.csv"], StringComparison.Ordinal);
    }
}
