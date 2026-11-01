using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>Upgrading a stack with people to colour themes: each person kept, with Arena's own colours (no theme chosen).</summary>
[Collection(nameof(AppCollection))]
public sealed class ColorThemeMigrationTests(AppFixture app)
{
    [Fact]
    public async Task People_from_before_colour_themes_keep_arena_s_own()
    {
        var cs = app.ConnectionStringFor("colortheme_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_ColorTheme", StringComparison.Ordinal)) - 1]);

        var user = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test", ["AnswerLength"] = "short" });
        }
        await db.Database.MigrateAsync();

        var kept = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user);
        Assert.Equal(("ana", "short", null), (kept.UserName, kept.AnswerLength, kept.ColorTheme));
    }
}
