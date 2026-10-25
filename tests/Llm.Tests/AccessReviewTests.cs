using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Compliance;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Access reviews: privileged roles, their groups' members and unused accounts, each kept or revoked; the schedule; unused accounts disabled.</summary>
[Collection(nameof(AppCollection))]
public sealed class AccessReviewTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? settings = null) =>
        app.Create(app.ConnectionStringFor("rev_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings);

    private static async Task<(Guid Id, string Name, TestBrowser Browser)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "rv" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (made.GetProperty("id").GetGuid(), name, b);
    }

    private static async Task<Guid> RoleAsync(TestBrowser admin, string key) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("id").GetGuid();

    private static async Task SqlAsync(WebApplicationFactory<Program> f, FormattableString sql)
    {
        await using var scope = f.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(sql);
    }

    [Fact]
    [Trait("Control", "IAM-07")]
    public async Task A_review_lists_privileged_roles_their_groups_members_and_unused_accounts_and_revoking_takes_effect_at_once()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (peopleAdminId, peopleAdmin, peopleAdminB) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = await RoleAsync(admin, "people-admin"), subjectKind = "Person", subjectId = peopleAdminId }));
        var (memberId, member, memberB) = await PersonAsync(f, admin);
        var group = await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Auditors " + Guid.NewGuid().ToString("N")[..6] }));
        var groupId = group.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{groupId}/members", new { userIds = new[] { memberId } }));
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = await RoleAsync(admin, "auditor"), subjectKind = "Group", subjectId = groupId }));
        var (idleId, idle, _) = await PersonAsync(f, admin);
        await SqlAsync(f, $"UPDATE \"AspNetUsers\" SET \"LastSignInAt\" = now() - interval '200 days', \"CreatedAt\" = now() - interval '300 days' WHERE \"Id\" = {idleId}");
        await StatusAssert.Is(HttpStatusCode.OK, await peopleAdminB.GetAsync("/api/admin/people"));
        await StatusAssert.Is(HttpStatusCode.OK, await memberB.GetAsync("/api/admin/audit"));

        var started = await admin.PostAsync("/api/admin/compliance/reviews");
        await StatusAssert.Is(HttpStatusCode.Created, started);
        var id = (await admin.JsonAsync(started)).GetProperty("id").GetGuid();
        var items = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/compliance/reviews/{id}"))).GetProperty("items").EnumerateArray().ToList();
        JsonElement Item(string kind, string subject) =>
            items.Where(i => i.GetProperty("kind").GetString() == kind && i.GetProperty("subject").GetString() == subject).ToList() is [var one] ? one
            : throw new Xunit.Sdk.XunitException($"Not one {kind} for {subject} in: {string.Join("; ", items.Select(i => $"{i.GetProperty("kind")} {i.GetProperty("subject")}: {i.GetProperty("what")}"))}");
        Assert.Contains("first admin", Item("role", "admin").GetProperty("what").GetString(), StringComparison.Ordinal);
        Assert.Equal("People admin, given to them", Item("role", $"person {peopleAdmin}").GetProperty("what").GetString());
        Assert.StartsWith("Auditor, given to the group", Item("role", $"group {group.GetProperty("name").GetString()}").GetProperty("what").GetString(), StringComparison.Ordinal);
        Assert.Equal($"member of {group.GetProperty("name").GetString()}, which holds Auditor", Item("member", member).GetProperty("what").GetString());
        Assert.StartsWith("last signed in ", Item("unused", idle).GetProperty("what").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, items.Count);

        async Task<JsonElement> DecideAsync(JsonElement item, string decision)
        {
            var res = await admin.PostAsync($"/api/admin/compliance/reviews/{id}/items/{item.GetProperty("id").GetGuid()}", new { decision });
            await StatusAssert.Is(HttpStatusCode.OK, res);
            return await admin.JsonAsync(res);
        }
        Assert.Equal("The role was taken away.", (await DecideAsync(Item("role", $"person {peopleAdmin}"), "revoke")).GetProperty("outcome").GetString());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await peopleAdminB.GetAsync("/api/admin/people"));
        Assert.StartsWith("Removed from", (await DecideAsync(Item("member", member), "revoke")).GetProperty("outcome").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await memberB.GetAsync("/api/admin/audit"));
        Assert.Equal("The account was disabled.", (await DecideAsync(Item("unused", idle), "revoke")).GetProperty("outcome").GetString());

        // Closed only once each is decided; then nothing changes.
        var early = await admin.PostAsync($"/api/admin/compliance/reviews/{id}/close");
        await StatusAssert.Is(HttpStatusCode.BadRequest, early);
        Assert.Contains("not decided yet", (await admin.JsonAsync(early)).GetProperty("error").GetString(), StringComparison.Ordinal);
        await DecideAsync(Item("role", "admin"), "keep");
        await DecideAsync(Item("role", $"group {group.GetProperty("name").GetString()}"), "keep");
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/compliance/reviews/{id}/close"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/compliance/reviews/{id}/items/{Item("role", "admin").GetProperty("id").GetGuid()}", new { decision = "revoke" }));

        var list = Assert.Single((await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/reviews"))).EnumerateArray());
        Assert.Equal((5, 2, 3), (list.GetProperty("items").GetInt32(), list.GetProperty("kept").GetInt32(), list.GetProperty("revoked").GetInt32()));
        // The first admin, kept, is an Owner as ever.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.GetAsync("/api/admin/people"));
        var check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "access-review");
        Assert.Equal("ok", check.GetProperty("state").GetString());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=100"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("access_review.start", audit);
        Assert.Equal(5, audit.Count(a => a == "access_review.decide"));
        Assert.Contains("access_review.close", audit);
        Assert.Contains("person.disable", audit);

        using var zip = new System.IO.Compression.ZipArchive(await (await admin.GetAsync("/api/admin/compliance/evidence")).Content.ReadAsStreamAsync());
        using var reader = new StreamReader(zip.GetEntry("access-reviews.csv")!.Open());
        var csv = await reader.ReadToEndAsync();
        Assert.Contains($"person {peopleAdmin},", csv, StringComparison.Ordinal);
        Assert.Contains("The role was taken away.", csv, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "IAM-07")]
    public async Task The_schedule_starts_a_review_when_one_is_due_tells_the_admins_and_disables_unused_accounts_when_asked()
    {
        await using var f = NewApp(new Dictionary<string, string?> { ["Compliance:DisableUnused"] = "true", ["Compliance:UnusedDays"] = "30" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (idleId, _, _) = await PersonAsync(f, admin);
        var (busyId, _, _) = await PersonAsync(f, admin);
        await SqlAsync(f, $"UPDATE \"AspNetUsers\" SET \"LastSignInAt\" = now() - interval '45 days', \"CreatedAt\" = now() - interval '60 days' WHERE \"Id\" = {idleId}");
        // The first admin, unused as long, is never disabled: the way back in.
        await SqlAsync(f, $"UPDATE \"AspNetUsers\" SET \"LastSignInAt\" = now() - interval '45 days' WHERE \"UserName\" = 'admin'");
        var schedule = ActivatorUtilities.CreateInstance<AccessReviewSchedule>(f.Services);

        // Reviews only by hand (the tests' setting): the unused account is disabled, no review starts.
        await schedule.RunAsync(CancellationToken.None);
        var people = (await admin.JsonAsync(await admin.GetAsync("/api/admin/people"))).GetProperty("people").EnumerateArray().ToList();
        bool Disabled(Guid who) => people.Single(p => p.GetProperty("id").GetGuid() == who).GetProperty("disabled").GetBoolean();
        Assert.True(Disabled(idleId));
        Assert.False(Disabled(busyId));
        Assert.False(people.Single(p => p.GetProperty("userName").GetString() == "admin").GetProperty("disabled").GetBoolean());
        Assert.Empty((await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/reviews"))).EnumerateArray());

        // Every 90 days: one starts now, and the admins hear of it; run again, none more.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Compliance:ReviewEveryDays", value = "90" } } }));
        await schedule.RunAsync(CancellationToken.None);
        await schedule.RunAsync(CancellationToken.None);
        var review = Assert.Single((await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/reviews"))).EnumerateArray());
        Assert.Equal("the schedule", review.GetProperty("createdBy").GetString());
        var news = (await admin.JsonAsync(await admin.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray();
        Assert.Contains(news, n => n.GetProperty("title").GetString() == "An access review is due"
            && n.GetProperty("link").GetString() == $"/admin/compliance/reviews/{review.GetProperty("id").GetGuid()}");
        var check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "access-review");
        Assert.Equal("warn", check.GetProperty("state").GetString());
        Assert.Contains("is under way", check.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
