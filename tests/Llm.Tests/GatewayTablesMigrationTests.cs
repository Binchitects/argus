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

    [Fact]
    public async Task Refusals_the_first_mirror_kept_as_failures_become_refusals_with_why_and_failures_stay()
    {
        var cs = app.ConnectionStringFor("gwrefusals_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_LedgerRefusals", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);

        var now = DateTimeOffset.UtcNow;
        (string Error, string? Source)[] rows =
        [
            ("You have used all your API credit for this month ($0.00). Ask an admin to raise it.", "a"),
            ("Your API access is off. Ask an admin to turn it on (Admin → People).", "b"),
            ("The request was refused: it holds a credit card number. Remove it and send it again.", "c"),
            ("Authentication Error, Invalid proxy server token passed. Received API Key = sk-...", "d"),
            ("key not allowed to access model. This key can only access models=['a']", "e"),
            ("Key is blocked. Update via `/key/unblock` if you're an admin.", "f"),
            ("litellm.BadRequestError: OpenAIException - model is not loaded.", "g"),
            // The app's own row (no source in LiteLLM's log) is the app's to say.
            ("You have used all your API credit for this month ($0.00).", null),
        ];
        db.GatewayRequests.AddRange(rows.Select(r => new GatewayRequest
        {
            StartedAt = now, EndedAt = now, Surface = "api", Call = "chat", CreditKind = "api", Model = "qwen", Status = GatewayRequest.Failed, Error = r.Error, SourceId = r.Source,
        }));
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();

        var after = await db.GatewayRequests.AsNoTracking().OrderBy(r => r.SourceId).Select(r => new { r.SourceId, r.Status, r.Refusal }).ToListAsync();
        Assert.Equal(
            ["a|refused|credit", "b|refused|api_off", "c|refused|safeguards", "d|refused|key", "e|refused|model", "f|refused|blocked", "g|failed|", "|failed|"],
            after.Select(r => $"{r.SourceId}|{r.Status}|{r.Refusal}"));
    }

    [Fact]
    public async Task Own_keys_join_the_apps_keys_with_their_names_and_limits_and_their_table_goes()
    {
        var cs = app.ConnectionStringFor("gwownkeys_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_ApiKeysOwn", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);

        var user = Guid.NewGuid();
        var (mirrored, notYet) = (new string('b', 64), new string('c', 64));
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            // One the mirror of LiteLLM's keys brought over already (as the main key, its alias), one it has not yet.
            await using var seed = new NpgsqlCommand($"""
                insert into gateway.api_keys (hash, user_id, kind, alias, preview, created_at, spend) values ('{mirrored}', '{user}', 'main', 'app-ana-own-1a2b3c4d', 'sk-...mirr', now(), 1.5);
                insert into own_keys ("Token", "UserId", "Name", "RequestsPerMinute", "TokensPerMinute", "Parallel", "MonthlyBudget", "CreatedAt") values
                  ('{mirrored}', '{user}', 'CI', 5, null, 2, 3.5, '2026-10-01T00:00:00Z'),
                  ('{notYet}', '{user}', 'laptop', null, 1000, null, null, '2026-10-02T00:00:00Z');
                """, conn);
            await seed.ExecuteNonQueryAsync();
        }
        await db.Database.MigrateAsync();

        var keys = await db.ApiKeys.AsNoTracking().OrderBy(k => k.Hash).ToListAsync();
        Assert.Equal(
            [$"{mirrored}|own|app-ana-own-1a2b3c4d|CI|5||2|3.5000|1.5", $"{notYet}|own|own-cccccccccccccccc|laptop||1000|||0"],
            keys.Select(k => $"{k.Hash}|{k.Kind}|{k.Alias}|{k.Name}|{k.RequestsPerMinute}|{k.TokensPerMinute}|{k.Parallel}|{k.MonthlyBudget}|{k.Spend:0.#########}"));
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var gone = new NpgsqlCommand("select count(*) from pg_class where relname = 'own_keys'", conn);
            Assert.Equal(0L, await gone.ExecuteScalarAsync());
        }

        // Back down: the own keys' table again, from the app's own keys.
        await db.GetService<IMigrator>().MigrateAsync(before);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var back = new NpgsqlCommand("""select string_agg("Name", ',' order by "Name") from own_keys""", conn);
            Assert.Equal("CI,laptop", await back.ExecuteScalarAsync());
        }
    }
}
