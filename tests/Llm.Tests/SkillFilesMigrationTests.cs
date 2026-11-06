using System.IO.Compression;
using Llm.Api.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>Upgrading a stack with skills to skills with files: each kept as it was, with no files beside its SKILL.md, and exported so.</summary>
[Collection(nameof(AppCollection))]
public sealed class SkillFilesMigrationTests(AppFixture app)
{
    [Fact]
    public async Task A_skill_saved_before_has_no_files_and_exports_as_its_SKILL_md()
    {
        var cs = app.ConnectionStringFor("skillfiles_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SkillFiles", StringComparison.Ordinal)) - 1]);

        var (user, id) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            await V61MigrationTests.InsertAsync(conn, "skills", new() { ["Id"] = id, ["UserId"] = user, ["Name"] = "standup", ["Title"] = "Stand-up", ["Instructions"] = "Write my stand-up." });
        }
        await db.Database.MigrateAsync();

        var skill = await db.Skills.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal("{}", skill.Files);
        Assert.Empty(SkillTransfer.FilesOf(skill));
        using var zip = new ZipArchive(new MemoryStream(SkillTransfer.Zip([skill])));
        Assert.Equal(["standup/SKILL.md"], zip.Entries.Select(e => e.FullName));
    }
}
