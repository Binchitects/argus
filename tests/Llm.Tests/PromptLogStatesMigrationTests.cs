using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack whose prompt log already has API requests to the log of every state: each one kept, as done (only
/// answered requests were kept before), with no error and no time taken known.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class PromptLogStatesMigrationTests(AppFixture app)
{
    [Fact]
    public async Task Requests_logged_before_the_states_are_kept_as_done()
    {
        var cs = app.ConnectionStringFor("plstates_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_PromptLogStates", StringComparison.Ordinal)) - 1]);

        var (user, line) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            await V61MigrationTests.InsertAsync(conn, "prompt_log", new()
            {
                ["Id"] = line, ["UserId"] = user, ["Model"] = "Qwen3-4B", ["Prompt"] = "Name a prime.", ["Answer"] = "7", ["Status"] = 200,
                ["At"] = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero),
            });
        }
        await db.Database.MigrateAsync();

        var kept = await db.PromptLog.AsNoTracking().SingleAsync(p => p.Id == line);
        Assert.Equal((PromptLogEntry.Done, null, null, "7"), (kept.State, kept.Error, kept.DurationMs, kept.Answer));
    }
}
