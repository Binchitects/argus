using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that has data to v8.0: what was stored under the code-intelligence service's brand name (its chat
/// tool id, its permissions, its settings) is stored under the neutral names, and what was not is left as it was.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class V80MigrationTests(AppFixture app)
{
    [Fact]
    public async Task What_was_stored_under_the_brand_name_is_stored_under_the_neutral_one_and_nothing_else_changes()
    {
        var cs = app.ConnectionStringFor("v80_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var migrator = db.GetService<IMigrator>();
        var before = all[all.FindIndex(m => m.EndsWith("_NeutralNames", StringComparison.Ordinal)) - 1];
        await migrator.MigrateAsync(before);

        var (chat, role, task, rule) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            var person = Guid.NewGuid();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = person, ["UserName"] = "kim", ["NormalizedUserName"] = "KIM" });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = chat, ["UserId"] = person, ["Tools"] = new[] { "argus", "web", "argus-like" } });
            await V61MigrationTests.InsertAsync(conn, "scheduled_tasks", new() { ["Id"] = task, ["UserId"] = person, ["Tools"] = new[] { "web", "argus" } });
            await V61MigrationTests.InsertAsync(conn, "tool_settings", new() { ["ToolId"] = "argus", ["Enabled"] = false });
            await V61MigrationTests.InsertAsync(conn, "tool_settings", new() { ["ToolId"] = "web", ["Enabled"] = true });
            await V61MigrationTests.InsertAsync(conn, "access_roles", new()
            {
                ["Id"] = role, ["Key"] = "code-readers", ["Name"] = "Code readers",
                ["Permissions"] = new[] { "chat.use", "argus.read", "argus.manage", "settings.argus", "settings.branding" },
            });
            await V61MigrationTests.InsertAsync(conn, "access_rules", new() { ["Id"] = rule, ["ResourceKind"] = "tool", ["Resource"] = "argus" });
            await V61MigrationTests.InsertAsync(conn, "settings", new() { ["key"] = "config:ArgusIndex:Schedule", ["value"] = "0 3 * * *" });
            await V61MigrationTests.InsertAsync(conn, "settings", new() { ["key"] = "config:Chat:MaxToolRounds", ["value"] = "8" });
        }
        await db.Database.MigrateAsync();

        Assert.Equal(["code-intel", "web", "argus-like"], (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == chat)).Tools);
        Assert.Equal(["web", "code-intel"], (await db.ScheduledTasks.AsNoTracking().SingleAsync(t => t.Id == task)).Tools);
        Assert.Equal(["code-intel", "web"], (await db.ToolSettings.AsNoTracking().OrderBy(t => t.ToolId).ToListAsync()).Select(t => t.ToolId));
        Assert.False((await db.ToolSettings.AsNoTracking().SingleAsync(t => t.ToolId == "code-intel")).Enabled);
        // In their order, the rest as they were.
        Assert.Equal(["chat.use", "code-intel.read", "code-intel.manage", "settings.code-intel", "settings.branding"],
            (await db.AccessRoles.AsNoTracking().SingleAsync(r => r.Id == role)).Permissions);
        Assert.Equal("code-intel", (await db.AccessRules.AsNoTracking().SingleAsync(r => r.Id == rule)).Resource);
        Assert.Equal(["config:Chat:MaxToolRounds", "config:CodeIntelIndex:Schedule"],
            (await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("config:")).OrderBy(s => s.Key).ToListAsync()).Select(s => s.Key));

        // And back, as a migration to the release before does.
        await migrator.MigrateAsync(before);
        await using var again = new NpgsqlConnection(cs);
        await again.OpenAsync();
        await using var read = new NpgsqlCommand("select \"Tools\" from conversations where \"Id\" = @id", again);
        read.Parameters.AddWithValue("id", chat);
        Assert.Equal(["argus", "web", "argus-like"], (string[])(await read.ExecuteScalarAsync())!);
    }
}
