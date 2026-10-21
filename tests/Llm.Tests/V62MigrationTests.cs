using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that has data from v6.1 to v6.2: the rooms, reviews and plugins it has keep their rows, and what v6.2
/// adds to them (invite links, automatic reviews, a group's plugin settings, plugin tasks) starts empty beside them.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class V62MigrationTests(AppFixture app)
{
    [Fact]
    public async Task Rooms_reviews_and_plugins_keep_their_rows_and_what_v62_adds_starts_empty()
    {
        var cs = app.ConnectionStringFor("v62_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_RoomInvites", StringComparison.Ordinal)) - 1]);

        var (user, room, review, server) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "rae", ["NormalizedUserName"] = "RAE", ["Email"] = "rae@example.test" });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = room, ["UserId"] = user, ["Title"] = "Team", ["Kind"] = "room" });
            await V61MigrationTests.InsertAsync(conn, "code_reviews", new() { ["Id"] = review, ["UserId"] = user, ["Project"] = "g/app", ["Status"] = "done", ["RoomId"] = room });
            await V61MigrationTests.InsertAsync(conn, "mcp_servers", new() { ["Id"] = server, ["Name"] = "Pets", ["Url"] = "https://pets.test/v1", ["Plugin"] = "pets" });
        }
        await db.Database.MigrateAsync();

        var kept = await db.CodeReviews.AsNoTracking().SingleAsync(r => r.Id == review);
        Assert.Equal(("g/app", room, (Guid?)null, (string?)null), (kept.Project, kept.RoomId, kept.RuleId, kept.Trigger));
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == room));
        Assert.True(await db.McpServers.AnyAsync(s => s.Id == server));
        Assert.False(await db.RoomInvites.AnyAsync());
        Assert.False(await db.ReviewRules.AnyAsync());
        Assert.False(await db.PluginGroupSettings.AnyAsync());
        Assert.False(await db.TaskTemplates.AnyAsync());

        // An automatic review of the project, and one of its reviews, side by side with what was there.
        db.ReviewRules.Add(new Llm.Core.Code.ReviewRule { UserId = user, Project = "g/app", RoomId = room });
        await db.SaveChangesAsync();
        Assert.Empty((await db.ReviewRules.AsNoTracking().SingleAsync()).TargetBranches);
    }
}
