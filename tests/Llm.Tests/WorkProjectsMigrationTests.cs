using Llm.Core.Data;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading to projects: the chats and the ledger's requests kept before are in no project, as they were, and a chat of
/// before goes in a project made after.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class WorkProjectsMigrationTests(AppFixture app)
{
    [Fact]
    [Trait("Control", "DAT-09")]
    public async Task Chats_and_requests_kept_before_are_in_no_project_and_may_go_in_one()
    {
        var cs = app.ConnectionStringFor("projects_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_WorkProjects", StringComparison.Ordinal)) - 1]);

        var (user, chat, request) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = user, ["UserName"] = "ana", ["Email"] = "ana@corp.test" });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = chat, ["UserId"] = user, ["Title"] = "Before", ["CreatedAt"] = DateTimeOffset.UtcNow, ["UpdatedAt"] = DateTimeOffset.UtcNow });
            await using var insert = new NpgsqlCommand("""
                insert into gateway.requests (id, started_at, ended_at, duration_ms, surface, call, protocol, credit_kind, user_id, person, model, prompt_tokens, cached_tokens,
                  completion_tokens, cost, status, http_status, attempts, usage_estimated)
                values (@id, now(), now(), 10, 'chat', 'chat', 'openai', 'chat', @user, 'ana@corp.test', 'qwen', 10, 0, 1, 0.01, 'done', 200, 1, false)
                """, conn);
            insert.Parameters.AddWithValue("id", request);
            insert.Parameters.AddWithValue("user", user);
            await insert.ExecuteNonQueryAsync();
        }
        await db.Database.MigrateAsync();

        var kept = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == chat);
        Assert.Equal(("Before", (Guid?)null), (kept.Title, kept.WorkProjectId));
        var row = await db.GatewayRequests.AsNoTracking().SingleAsync(r => r.Id == request);
        Assert.Equal((0.01m, (Guid?)null, (Guid?)null), (row.Cost, row.ProjectId, row.ConversationId));

        var project = new Project { Name = "After", OwnerId = user };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        Assert.Equal((80, Project.Active, false), (project.AlertPercent, project.Status, project.Global));
        await db.Conversations.Where(c => c.Id == chat).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkProjectId, project.Id));
        // A member is a person or a group, never both nor neither.
        db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, Role = ProjectMember.Viewer });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
