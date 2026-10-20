using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that has data to v6.1: the safeguard switches (the company's and each group's) become rules that do what
/// they did, and the tables that gained columns keep their rows.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class V61MigrationTests(AppFixture app)
{
    /// <summary>A row with the given values, every other required column filled with its type's empty value.</summary>
    private static async Task InsertAsync(NpgsqlConnection conn, string table, Dictionary<string, object> values)
    {
        await using var columns = new NpgsqlCommand(
            "select column_name, data_type, udt_name from information_schema.columns where table_name = @t and is_nullable = 'NO' and column_default is null", conn);
        columns.Parameters.AddWithValue("t", table);
        var all = new Dictionary<string, object>(values);
        await using (var r = await columns.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var (name, type, udt) = (r.GetString(0), r.GetString(1), r.GetString(2));
                if (all.ContainsKey(name))
                {
                    continue;
                }
                all[name] = type switch
                {
                    "uuid" => Guid.NewGuid(),
                    "boolean" => false,
                    "integer" or "smallint" => 0,
                    "bigint" => 0L,
                    "numeric" => 0m,
                    "double precision" => 0d,
                    "timestamp with time zone" => DateTimeOffset.UtcNow,
                    "ARRAY" => udt == "_uuid" ? Array.Empty<Guid>() : Array.Empty<string>(),
                    _ => "",
                };
            }
        }
        var names = all.Keys.ToList();
        await using var insert = new NpgsqlCommand($"insert into {table} ({string.Join(", ", names.Select(n => $"\"{n}\""))}) values ({string.Join(", ", names.Select((_, i) => $"@p{i}"))})", conn);
        for (var i = 0; i < names.Count; i++)
        {
            insert.Parameters.AddWithValue($"p{i}", all[names[i]]);
        }
        await insert.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_switches_become_rules_that_do_what_they_did_and_tables_keep_their_rows()
    {
        var cs = app.ConnectionStringFor("v61_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SafeguardRules", StringComparison.Ordinal)) - 1]);

        var security = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            foreach (var (key, value) in new[] { ("SecretScanning", "mask"), ("BlockedPatterns", "project falcon"), ("Moderation", "check"), ("RedactPii", "off") })
            {
                await InsertAsync(conn, "settings", new() { ["key"] = $"config:Safeguards:{key}", ["value"] = value });
            }
            await InsertAsync(conn, "groups", new() { ["Id"] = security, ["Name"] = "Security", ["SecretScanning"] = "off", ["RedactPii"] = "mask" });
            await InsertAsync(conn, "tool_settings", new() { ["ToolId"] = "web", ["Enabled"] = true });
            await InsertAsync(conn, "mcp_servers", new() { ["Name"] = "Tracker", ["Url"] = "https://tracker.test/mcp" });
        }
        await db.Database.MigrateAsync();

        var rules = await db.SafeguardRules.AsNoTracking().OrderBy(r => r.Order).ToListAsync();
        var secrets = rules.Single(r => r.Name == "Secrets in what people send");
        Assert.True(secrets.Enabled);
        Assert.Equal("mask", secrets.Action);
        var words = rules.Single(r => r.Name == "Blocked words");
        Assert.Equal((true, "project falcon"), (words.Enabled, words.Patterns));
        Assert.True(rules.Single(r => r.Name == "Harmful requests").Enabled);
        Assert.False(rules.Single(r => r.Name == "Personal data to the model").Enabled);
        // The group's own come first: its secrets allowed, its personal data masked.
        var mine = rules.Where(r => r.Groups.Contains(security)).ToList();
        Assert.Equal([("secrets", "allow"), ("pii", "mask")], mine.Select(r => (r.Detector, r.Action)));
        Assert.True(mine.All(r => r.Order < secrets.Order));
        Assert.Empty(await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("config:Safeguards:")).ToListAsync());

        var web = await db.ToolSettings.AsNoTracking().SingleAsync(s => s.ToolId == "web");
        Assert.Empty(web.DisabledFunctions);
        Assert.Empty((await db.McpServers.AsNoTracking().SingleAsync()).PluginPermissions);
    }
}
