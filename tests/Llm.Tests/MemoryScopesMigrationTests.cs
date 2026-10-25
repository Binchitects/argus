using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>Upgrading a stack with memories kept before scopes and kinds: each is the person's own fact, typed, not pinned, used by nobody yet.</summary>
[Collection(nameof(AppCollection))]
public sealed class MemoryScopesMigrationTests(AppFixture app)
{
    [Fact]
    public async Task Memories_kept_before_are_a_persons_typed_facts()
    {
        var cs = app.ConnectionStringFor("memscope_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_MemoryScopes", StringComparison.Ordinal)) - 1]);

        var (user, memory) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            await V61MigrationTests.InsertAsync(conn, "memories", new()
            {
                ["Id"] = memory, ["UserId"] = user, ["Text"] = "Deploys with Podman.", ["CreatedAt"] = DateTimeOffset.UtcNow, ["UpdatedAt"] = DateTimeOffset.UtcNow,
            });
        }
        await db.Database.MigrateAsync();

        var kept = await db.Memories.AsNoTracking().SingleAsync(m => m.Id == memory);
        Assert.Equal(("Deploys with Podman.", Memory.Person, Memory.Fact, Memory.Typed), (kept.Text, kept.Scope, kept.Kind, kept.Source));
        Assert.Equal((false, 0, (DateTimeOffset?)null, (Guid?)null, (string?)null, (float[]?)null), (kept.Pinned, kept.Uses, kept.UsedAt, kept.ReplacedBy, kept.ProjectKey, kept.Embedding));
    }
}
