using Llm.Core.Data;
using Llm.Core.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// The gateway's own tables on a stack that has people already: its ledger and keys, the rate windows (not written to the
/// WAL), and the two views the dashboards read; and back down again.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class GatewayTablesMigrationTests(AppFixture app)
{
    [Fact]
    public async Task The_ledger_keys_rate_windows_and_views_come_on_a_stack_with_people_and_go_again()
    {
        var cs = app.ConnectionStringFor("gwtables_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_GatewayTables", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);

        var user = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "Ana@Corp.test" });
        }
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        db.ApiKeys.Add(new ApiKey { Hash = new string('a', 64), UserId = user, Kind = ApiKey.Main, Alias = "app-ana", Preview = "sk-…abcd", Spend = 1.5m });
        db.GatewayRequests.AddRange(
            new GatewayRequest { StartedAt = now, EndedAt = now, Surface = "api", Call = "chat", CreditKind = "api", Person = "ana@corp.test", UserId = user, Model = "Qwen3-4B", Status = GatewayRequest.Done, PromptTokens = 100, CompletionTokens = 20, Cost = 0.25m },
            new GatewayRequest { StartedAt = now, EndedAt = now, Surface = "chat", Call = "chat", CreditKind = "chat", Person = "ana@corp.test", UserId = user, Model = "Qwen3-4B", Status = GatewayRequest.Done, Cost = 0.125m },
            new GatewayRequest { StartedAt = now.AddMonths(-2), EndedAt = now.AddMonths(-2), Surface = "api", Call = "chat", CreditKind = "api", Person = "ana@corp.test", Model = "Qwen3-4B", Status = GatewayRequest.Done, Cost = 9m });
        await db.SaveChangesAsync();
        // The total is the database's own.
        Assert.Equal(120, (await db.GatewayRequests.AsNoTracking().OrderBy(r => r.Cost).LastAsync(r => r.StartedAt > now.AddDays(-1))).TotalTokens);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            async Task<List<string>> Rows(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await using var r = await cmd.ExecuteReaderAsync();
                var rows = new List<string>();
                while (await r.ReadAsync())
                {
                    rows.Add(string.Join('|', Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
                }
                return rows;
            }
            Assert.Equal(["app-ana|main|ana@corp.test|1.500000000"], await Rows("SELECT alias, kind, email, spend FROM gateway.key_owners"));
            // This month only, by credit.
            Assert.Equal(["ana@corp.test|api|0.250000000", "ana@corp.test|chat|0.125000000"], await Rows("SELECT person, credit_kind, spend FROM gateway.month_spend ORDER BY credit_kind"));
            Assert.Equal(["u"], await Rows("SELECT relpersistence FROM pg_class WHERE oid = 'gateway.rate_windows'::regclass"));
            await using (var insert = new NpgsqlCommand($"INSERT INTO gateway.rate_windows (key_hash, minute, requests, tokens) VALUES ('{new string('a', 64)}', date_trunc('minute', now()), 1, 120)", conn))
            {
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
        }

        // Back down: the schema's tables and views go, the people stay.
        await db.GetService<IMigrator>().MigrateAsync(before);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'gateway' AND c.relkind IN ('r', 'v')", conn);
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
            await using var people = new NpgsqlCommand("SELECT count(*) FROM \"AspNetUsers\"", conn);
            Assert.Equal(1L, await people.ExecuteScalarAsync());
        }
    }
}
