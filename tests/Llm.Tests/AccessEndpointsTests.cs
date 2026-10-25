using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Admin → Access: custom roles held through a directory group, rules, the checker and the history.</summary>
[Collection(nameof(AppCollection))]
public sealed class AccessEndpointsTests(AppFixture app)
{
    private static string Unique(string stem) => $"{stem} {Guid.NewGuid().ToString("N")[..6]}";

    [Fact]
    public async Task A_role_made_here_and_held_by_a_directory_group_gives_its_members_what_it_holds_until_it_is_taken()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ac" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = made.GetProperty("id").GetGuid();
        var dn = $"cn=ops-{Guid.NewGuid().ToString("N")[..6]},ou=groups,dc=example,dc=test";
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Users.SingleAsync(u => u.Id == person)).DirectoryGroups = [dn];
            await db.SaveChangesAsync();
        }
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = Unique("Ops"), directory = dn }))).GetProperty("id").GetGuid();
        var them = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/models"));

        // A role of the admin's own: refused with a permission that does not exist, made with one that does.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/access/roles", new { name = "x", permissions = new[] { "models.fly" } }));
        var roleName = Unique("Model watcher");
        var res = await admin.PostAsync("/api/admin/access/roles", new { name = roleName, description = "Sees the models.", permissions = new[] { "models.read" } });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var role = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/access/roles", new { name = roleName }));

        res = await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Group", subjectId = group });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var assignment = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Group", subjectId = group }));
        await StatusAssert.Is(HttpStatusCode.OK, await them.GetAsync("/api/admin/models"));
        // The web shows them the admin pages of what they hold.
        var me = await them.JsonAsync(await them.GetAsync("/api/auth/me"));
        Assert.Equal(["models.read"], me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        // Only what the role holds.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/audit"));

        var check = await admin.JsonAsync(await admin.GetAsync($"/api/admin/access/check?person={person}&permission=models.read"));
        Assert.True(check.GetProperty("allowed").GetBoolean());
        Assert.Contains(check.GetProperty("roles").EnumerateArray(), r => r.GetProperty("name").GetString() == roleName);

        // Held, it cannot be deleted; a built-in role cannot be changed.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.DeleteAsync(new Uri($"/api/admin/access/roles/{role}", UriKind.Relative)));
        var overview = await admin.JsonAsync(await admin.GetAsync("/api/admin/access"));
        var builtIn = overview.GetProperty("roles").EnumerateArray().First(r => r.GetProperty("builtIn").GetBoolean()).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/access/roles/{builtIn}", UriKind.Relative), new { name = "Mine" }));
        Assert.Contains(overview.GetProperty("roles").EnumerateArray(), r => r.GetProperty("id").GetGuid() == role
            && r.GetProperty("holders").EnumerateArray().Any(h => h.GetProperty("subject").GetProperty("id").GetGuid() == group));

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/access/assignments/{assignment}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/models"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/access/roles/{role}", UriKind.Relative)));

        var history = await admin.JsonAsync(await admin.GetAsync("/api/admin/access/history?take=20"));
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("kind").GetString() == "assignment" && h.GetProperty("actor").GetString() == "admin");
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task A_rule_takes_a_feature_from_a_group_and_the_checker_says_why()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ar" + Guid.NewGuid().ToString("N")[..10];
        var person = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("id").GetGuid();
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = Unique("Interns") }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { person } }));

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/access/rules",
            new { effect = "Deny", subjectKind = "Group", subjectId = group, resourceKind = "feature", resource = "feature.flying" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/access/rules",
            new { effect = "Deny", subjectKind = "Group", subjectId = Guid.NewGuid(), resourceKind = "feature", resource = "feature.web_search" }));
        var res = await admin.PostAsync("/api/admin/access/rules",
            new { effect = "Deny", subjectKind = "Group", subjectId = group, resourceKind = "feature", resource = "feature.web_search", note = "Not yet" });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var rule = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();

        var check = await admin.JsonAsync(await admin.GetAsync($"/api/admin/access/check?person={person}&permission=feature.web_search"));
        Assert.False(check.GetProperty("allowed").GetBoolean());
        Assert.Contains(check.GetProperty("because").EnumerateArray(), b => b.GetString()!.Contains("group", StringComparison.Ordinal));
        Assert.True((await admin.JsonAsync(await admin.GetAsync($"/api/admin/access/check?person={person}&permission=feature.chat"))).GetProperty("allowed").GetBoolean());

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/access/rules/{rule}", UriKind.Relative)));
        Assert.True((await admin.JsonAsync(await admin.GetAsync($"/api/admin/access/check?person={person}&permission=feature.web_search"))).GetProperty("allowed").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.Http.DeleteAsync(new Uri($"/api/admin/access/rules/{rule}", UriKind.Relative)));
    }

    [Fact]
    public async Task A_people_admin_for_one_group_reaches_its_members_only()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<(Guid Id, string Name, string Password)> PersonAsync()
        {
            var name = "as" + Guid.NewGuid().ToString("N")[..10];
            var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
            return (made.GetProperty("id").GetGuid(), name, made.GetProperty("password").GetString()!);
        }
        var manager = await PersonAsync();
        var member = await PersonAsync();
        var outsider = await PersonAsync();
        var team = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = Unique("Team") }))).GetProperty("id").GetGuid();
        var other = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = Unique("Other") }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{team}/members", new { userIds = new[] { member.Id } }));
        var overview = await admin.JsonAsync(await admin.GetAsync("/api/admin/access"));
        var peopleAdmin = overview.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "people-admin").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments",
            new { roleId = peopleAdmin, subjectKind = "Person", subjectId = manager.Id, scope = new { groups = new[] { team } } }));

        var them = await new TestBrowser(app.Factory).SignedInAsync(manager.Name, manager.Password);
        var listed = (await them.JsonAsync(await them.GetAsync("/api/admin/people"))).GetProperty("people").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([member.Id], listed);
        Assert.Equal([team], (await them.JsonAsync(await them.GetAsync("/api/admin/groups"))).EnumerateArray().Select(g => g.GetProperty("id").GetGuid()));
        await StatusAssert.Is(HttpStatusCode.OK, await them.GetAsync($"/api/admin/people/{member.Id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await them.GetAsync($"/api/admin/people/{outsider.Id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await them.GetAsync($"/api/admin/groups/{other}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await them.PostAsync($"/api/admin/people/{outsider.Id}/password"));
        // Making people, or putting someone in their group to reach them, is for an admin who manages everyone.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.PostAsync("/api/admin/people", new { userName = "x" + Guid.NewGuid().ToString("N")[..8], email = "x@example.test" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.PostAsync($"/api/admin/groups/{team}/members", new { userIds = new[] { outsider.Id } }));
    }

    [Fact]
    public async Task A_person_asks_for_a_role_an_access_manager_says_yes_and_the_bell_tells_them()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "rq" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var them = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);

        var roles = (await them.JsonAsync(await them.GetAsync("/api/access-requests/roles"))).EnumerateArray().ToList();
        Assert.DoesNotContain(roles, r => r.GetProperty("name").GetString() == "Owner");
        var supervisor = roles.Single(r => r.GetProperty("name").GetString() == "Supervisor").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await them.PostAsync("/api/access-requests", new { roleId = supervisor }));
        await StatusAssert.Is(HttpStatusCode.Created, await them.PostAsync("/api/access-requests", new { roleId = supervisor, reason = "I am on call this month." }));
        await StatusAssert.Is(HttpStatusCode.Created, await them.PostAsync("/api/access-requests", new { resourceKind = "feature", resource = "feature.video", reason = "For the demo." }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/access/requests"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/alerts"));

        var pending = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access/requests?status=pending"))).EnumerateArray()
            .Where(r => r.GetProperty("person").GetProperty("name").GetString() == name).ToList();
        Assert.Equal(2, pending.Count);
        var forRole = pending.Single(r => r.GetProperty("role").GetString() == "Supervisor").GetProperty("id").GetGuid();
        var forVideo = pending.Single(r => r.GetProperty("resource").GetString() == "feature.video").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/access/requests/{forRole}/decide", new { approve = true }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync($"/api/admin/access/requests/{forRole}/decide", new { approve = false }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/access/requests/{forVideo}/decide", new { approve = false, note = "Not this quarter." }));

        var me = await them.JsonAsync(await them.GetAsync("/api/auth/me"));
        Assert.Contains("monitoring.read", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        var mine = (await them.JsonAsync(await them.GetAsync("/api/access-requests"))).EnumerateArray().ToList();
        Assert.Equal(["approved", "denied"], mine.OrderBy(r => r.GetProperty("role").ValueKind == JsonValueKind.Null).Select(r => r.GetProperty("status").GetString()));
        var bell = await them.GetAsync("/api/notifications");
        var text = await bell.Content.ReadAsStringAsync();
        Assert.Contains("You have the role Supervisor now", text, StringComparison.Ordinal);
        Assert.Contains("Not this quarter.", text, StringComparison.Ordinal);
    }
}
