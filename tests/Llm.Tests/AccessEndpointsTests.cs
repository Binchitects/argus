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
}
