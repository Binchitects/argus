using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that has saved prompts to skills: every prompt kept as a skill (its text its instructions, used by
/// the model when it fits), and who could save prompts makes skills.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class SkillsMigrationTests(AppFixture app)
{
    [Fact]
    public async Task Prompts_become_skills_and_the_permission_follows()
    {
        var cs = app.ConnectionStringFor("skills_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_Skills", StringComparison.Ordinal)) - 1]);

        var (user, prompt, role) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            await V61MigrationTests.InsertAsync(conn, "prompts", new()
            {
                ["Id"] = prompt, ["UserId"] = user, ["Name"] = "standup", ["Title"] = "Stand-up notes", ["Text"] = "Write my stand-up notes for {{team}}.", ["Sharing"] = 0,
            });
            await V61MigrationTests.InsertAsync(conn, "access_roles", new() { ["Id"] = role, ["Key"] = "writers", ["Name"] = "Writers", ["Permissions"] = new[] { "feature.chat", "feature.prompts" } });
            await V61MigrationTests.InsertAsync(conn, "access_rules", new() { ["Effect"] = 1, ["SubjectKind"] = 0, ["SubjectId"] = user, ["ResourceKind"] = "feature", ["Action"] = "feature.prompts" });
        }
        await db.Database.MigrateAsync();

        var skill = await db.Skills.AsNoTracking().SingleAsync(s => s.Id == prompt);
        Assert.Equal(("standup", "Stand-up notes", "Write my stand-up notes for {{team}}."), (skill.Name, skill.Title, skill.Instructions));
        Assert.Equal(("", "[]", true), (skill.Description, skill.Parameters, skill.AutoUse));
        Assert.Equal(["feature.chat", "feature.skills"], (await db.AccessRoles.AsNoTracking().SingleAsync(r => r.Id == role)).Permissions);
        Assert.Contains(await db.AccessRules.AsNoTracking().Select(r => r.Action).ToListAsync(), a => a == "feature.skills");
        Assert.DoesNotContain(await db.AccessRules.AsNoTracking().Select(r => r.Action).ToListAsync(), a => a == "feature.prompts");

        // The person's skill is still theirs: deleting them deletes it.
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "AspNetUsers" WHERE "Id" = {0}""", user);
        Assert.False(await db.Skills.AnyAsync(s => s.Id == prompt));
    }
}
