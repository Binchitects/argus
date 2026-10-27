using Llm.Core.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that already has security reviews to the scanners: each finding kept, found by nothing named (the
/// model's review before the scanners came), and each review with no scanners' run and no commit said.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class SecurityScannersMigrationTests(AppFixture app)
{
    [Fact]
    public async Task Findings_of_reviews_made_before_the_scanners_are_kept()
    {
        var cs = app.ConnectionStringFor("scanners_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SecurityScanners", StringComparison.Ordinal)) - 1]);

        var (user, scan, finding) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            await V61MigrationTests.InsertAsync(conn, "security_scans", new()
            {
                ["Id"] = scan, ["UserId"] = user, ["Project"] = "g/shop", ["Status"] = "done", ["Candidates"] = 1, ["Read"] = 1, ["Dropped"] = 0, ["Dependencies"] = 0,
                ["PromptTokens"] = 0, ["CompletionTokens"] = 0, ["CreatedAt"] = DateTimeOffset.UtcNow,
            });
            await V61MigrationTests.InsertAsync(conn, "security_findings", new()
            {
                ["Id"] = finding, ["ScanId"] = scan, ["Path"] = "src/Orders.cs", ["Line"] = 4, ["SinkCode"] = "new SqlCommand(...)", ["Cwe"] = "CWE-89",
                ["CweTitle"] = "CWE-89: SQL Injection", ["Severity"] = "high", ["Title"] = "SQL built from the request", ["Flow"] = "", ["Exploit"] = "", ["Fix"] = "",
                ["Pages"] = "[]", ["State"] = "open", ["Exploited"] = false,
            });
        }
        await db.Database.MigrateAsync();

        var kept = await db.SecurityFindings.AsNoTracking().SingleAsync(f => f.Id == finding);
        Assert.Equal((SecurityFinding.Code, 0, null, null), (kept.Kind, kept.FoundBy.Length, kept.Rule, kept.Commit));
        var reviewed = await db.SecurityScans.AsNoTracking().SingleAsync(s => s.Id == scan);
        Assert.Equal((null, null), (reviewed.Scanners, reviewed.Commit));
    }
}
