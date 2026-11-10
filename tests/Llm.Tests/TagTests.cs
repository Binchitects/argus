using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Tags;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Tags and summaries on chats, projects and the prompt log (review, security, …): set by hand, or by Summarize and tag
/// (only the known tags); the prompt log searched by tag and reported on; Accounting by tag.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class TagTests(AppFixture app)
{
    private static Task<HttpResponseMessage> PatchAsync(TestBrowser b, string path, object body) => b.Http.PatchAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private static Task<HttpResponseMessage> PutAsync(TestBrowser b, string path, object body) => b.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body);

    [Theory]
    [InlineData("  Security ", "security")]
    [InlineData("#for review", "for-review")]
    [InlineData("a/b!c", "abc")]
    [InlineData("امنیت", "امنیت")]
    [InlineData("  ", null)]
    public void A_tag_is_kept_short_lower_case_and_dashed(string given, string? kept) => Assert.Equal(kept, Tagging.One(given));

    [Fact]
    public void A_summary_keeps_only_the_known_tags()
    {
        var s = TagSummaries.Read("Summary: Fixes the login.\nIt was a bug.\n**Tags:** Bug, nonsense, security", ["bug", "security", "review"]);
        Assert.Equal("Fixes the login. It was a bug.", s!.Brief);
        Assert.Equal(["bug", "security"], s.Tags);
        Assert.Equal([], TagSummaries.Read("Only words.\nTags:", ["bug"])!.Tags);
        Assert.Null(TagSummaries.Read("Tags: bug", ["bug"]));
    }

    [Fact]
    public async Task Chats_and_projects_are_tagged_by_hand_and_summarized_and_the_prompt_log_and_accounting_count_by_tag()
    {
        var cs = app.ConnectionStringFor("tags_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway(), new Dictionary<string, string?> { ["Tags:Known"] = "review\nsecurity, bug" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "tg" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);

        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await (await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Which port does Postgres use?" })).Content.ReadAsStringAsync();

        // By hand: cleaned, once each.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(person, $"/api/chat/conversations/{chat}", new { tags = new[] { "Review", "review", " for later " } }));
        var listed = (await person.JsonAsync(await person.GetAsync("/api/chat/conversations"))).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat);
        Assert.Equal(["review", "for-later"], listed.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        // Summarize and tag: a summary, and the known tags that fit added (not one the model made up).
        var summary = await person.JsonAsync(await person.PostAsync($"/api/chat/conversations/{chat}/summarize", new { }));
        Assert.Equal("Asks which port Postgres uses.", summary.GetProperty("brief").GetString());
        Assert.Equal(["review", "for-later", "security"], summary.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        var detail = await person.JsonAsync(await person.GetAsync($"/api/chat/conversations/{chat}"));
        Assert.Equal("Asks which port Postgres uses.", detail.GetProperty("brief").GetString());

        // The known ones, then the person's own in use.
        var tags = await person.JsonAsync(await person.GetAsync("/api/tags"));
        Assert.True(tags.TryGetProperty("known", out _), tags.ToString());
        Assert.Equal(["review", "security", "bug"], tags.GetProperty("known").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains(tags.GetProperty("used").EnumerateArray(), u => u.GetProperty("tag").GetString() == "for-later");

        // A project: its contributors and owners tag it; a stranger does not.
        var project = (await admin.JsonAsync(await admin.PostAsync("/api/projects", new { name = "Ports", description = "Which ports what uses." }))).GetProperty("id").GetGuid();
        Assert.Equal(["bug"], (await admin.JsonAsync(await PutAsync(admin, $"/api/projects/{project}/tags", new { tags = new[] { "bug" } }))).GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await PutAsync(person, $"/api/projects/{project}/tags", new { tags = new[] { "mine" } }));
        var summarized = await admin.JsonAsync(await admin.PostAsync($"/api/projects/{project}/summarize", new { }));
        Assert.Equal(["bug", "security"], summarized.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["bug", "security"], (await admin.JsonAsync(await admin.GetAsync($"/api/projects/{project}"))).GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        // The prompt log: found by tag, with the tags and the summary; tagged there (a chat's question tags its chat).
        var found = (await admin.JsonAsync(await admin.GetAsync("/api/admin/prompts?tag=security&source=chat"))).GetProperty("items").EnumerateArray().ToList();
        var row = Assert.Single(found);
        Assert.Equal("Asks which port Postgres uses.", row.GetProperty("brief").GetString());
        Assert.Empty((await admin.JsonAsync(await admin.GetAsync("/api/admin/prompts?tag=bug&source=chat"))).GetProperty("items").EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.OK, await PutAsync(admin, $"/api/admin/prompts/chat/{row.GetProperty("id").GetGuid()}/tags", new { tags = new[] { "bug" } }));
        Assert.Single((await admin.JsonAsync(await admin.GetAsync("/api/admin/prompts?tag=bug&source=chat"))).GetProperty("items").EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(person, $"/api/admin/prompts/chat/{row.GetProperty("id").GetGuid()}/tags", new { tags = new[] { "x" } }));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "prompts.tagged");

        // The report: the chat's question counted, under its tag; CSV for the spreadsheet.
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var report = await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts/report?from={from}&to={to}"));
        Assert.Equal(1, report.GetProperty("chat").GetInt64());
        Assert.Contains(report.GetProperty("tags").EnumerateArray(), t => t.GetProperty("name").GetString() == "bug" && t.GetProperty("chat").GetInt64() == 1);
        Assert.Contains(report.GetProperty("people").EnumerateArray(), p => p.GetProperty("chat").GetInt64() == 1);
        var csv = await admin.GetAsync($"/api/admin/prompts/report?from={from}&to={to}&format=csv");
        Assert.StartsWith("section,name,count", await csv.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Accounting by tag: a request counts under each tag of its chat and its project, once each.
        var at = new DateTimeOffset(2023, 7, 1, 10, 0, 0, TimeSpan.Zero);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            foreach (var (conversation, proj) in new (Guid?, Guid?)[] { (chat, project), (chat, null), (null, project), (null, null) })
            {
                await using var insert = new NpgsqlCommand("""
                    insert into gateway.requests (id, started_at, ended_at, duration_ms, surface, call, protocol, credit_kind, person, model, prompt_tokens, cached_tokens,
                      completion_tokens, cost, status, http_status, attempts, usage_estimated, conversation_id, project_id)
                    values (@id, @at, @at, 10, 'chat', 'chat', 'openai', 'chat', @person, 'qwen', 10, 0, 1, 1, 'done', 200, 1, false, @chat, @project)
                    """, conn);
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("at", at);
                insert.Parameters.AddWithValue("person", $"{name}@example.test");
                insert.Parameters.Add(new NpgsqlParameter("chat", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)conversation ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("project", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)proj ?? DBNull.Value });
                await insert.ExecuteNonQueryAsync();
            }
        }
        var accounting = await admin.JsonAsync(await admin.GetAsync($"/api/admin/accounting?from={Uri.EscapeDataString(at.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(at.AddHours(1).ToString("O"))}"));
        var byTag = accounting.GetProperty("tags").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("requests").GetInt64());
        // The chat: bug (set in the prompt log); the project: bug and security.
        Assert.Equal(3, byTag["bug"]);
        Assert.Equal(2, byTag["security"]);
        Assert.Equal(2, byTag.Count);
    }

    [Fact]
    public async Task Chats_projects_and_prompt_log_lines_from_before_have_no_tags_after_the_upgrade()
    {
        var cs = app.ConnectionStringFor("tagsup_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_TagsAndBriefs", StringComparison.Ordinal)) - 1]);
        var (person, chat) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = person, ["UserName"] = "lee", ["NormalizedUserName"] = "LEE" });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = chat, ["UserId"] = person, ["Title"] = "Before" });
        }
        await db.Database.MigrateAsync();
        var kept = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == chat);
        Assert.Equal(("Before", 0, (string?)null), (kept.Title, kept.Tags.Count, kept.Brief));
    }
}
