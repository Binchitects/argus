using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Changes to many people at once: a dry run says what would change, the real run does exactly that, and nobody is locked out.</summary>
[Collection(nameof(AppCollection))]
public sealed class AccessBulkTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("bulk_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());

    [Fact]
    public async Task A_dry_run_says_what_would_change_and_the_real_run_changes_exactly_that()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<Guid> PersonAsync(string name)
        {
            var user = "bk" + Guid.NewGuid().ToString("N")[..8];
            return (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = name }))).GetProperty("id").GetGuid();
        }
        var (ana, ben, cy) = (await PersonAsync("Ana"), await PersonAsync("Ben"), await PersonAsync("Cy"));
        var ops = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Ops" }))).GetProperty("id").GetGuid();
        var finance = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Finance" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{ops}/members", new { userIds = new[] { ben, cy } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{finance}/members", new { userIds = new[] { cy } }));
        var overview = await admin.JsonAsync(await admin.GetAsync("/api/admin/access"));
        Guid Role(string key) => overview.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("id").GetGuid();
        var steps = new object[]
        {
            new { kind = "role.give", roleId = Role("supervisor") },
            new { kind = "group.join", groupId = finance },
            new { kind = "feature.deny", feature = "feature.web_search" },
        };

        // Ana by name, Ben and Cy through Ops: Cy is in Finance already.
        var dry = await admin.JsonAsync(await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { ana }, groups = new[] { ops }, steps }));
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(3, dry.GetProperty("people").GetInt32());
        Assert.Equal(8, dry.GetProperty("changes").GetInt32());
        var cyLine = dry.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Cy");
        Assert.Equal(["is in Finance already"], cyLine.GetProperty("unchanged").EnumerateArray().Select(x => x.GetString()));
        async Task<bool> MayAsync(Guid person, string permission) =>
            (await admin.JsonAsync(await admin.GetAsync($"/api/admin/access/check?person={person}&permission={permission}"))).GetProperty("allowed").GetBoolean();
        Assert.False(await MayAsync(ana, "monitoring.read"));

        var done = await admin.JsonAsync(await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { ana }, groups = new[] { ops }, steps, dryRun = false }));
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.True(await MayAsync(ana, "monitoring.read"));
        Assert.False(await MayAsync(ben, "feature.web_search"));
        var members = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{finance}"))).GetProperty("members").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToHashSet();
        Assert.True(members.SetEquals([ana, ben, cy]));

        // The same again changes nothing.
        var again = await admin.JsonAsync(await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { ana }, groups = new[] { ops }, steps, dryRun = false }));
        Assert.Equal(0, again.GetProperty("changes").GetInt32());

        // Nobody locks themselves out: an Owner given by a bulk change cannot take their own access away with one.
        var user = "bo" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test" }));
        var dee = made.GetProperty("id").GetGuid();
        await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { dee }, steps = new[] { new { kind = "role.give", roleId = Role("owner") } }, dryRun = false });
        var deeBrowser = await new TestBrowser(f).SignedInAsync(user, made.GetProperty("password").GetString()!);
        var lockout = await deeBrowser.PostAsync("/api/admin/access/bulk", new { people = new[] { dee }, steps = new[] { new { kind = "role.take", roleId = Role("owner") } }, dryRun = false });
        await StatusAssert.Is(HttpStatusCode.Conflict, lockout);
        Assert.True(await MayAsync(dee, "access.manage"));

        // Accounts: never one's own.
        var adminId = (await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        var self = await admin.JsonAsync(await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { adminId, ana }, steps = new[] { new { kind = "account.disable" } }, dryRun = false }));
        Assert.Equal(1, self.GetProperty("changes").GetInt32());
        Assert.True((await admin.JsonAsync(await admin.GetAsync($"/api/admin/access/check?person={ana}&permission=feature.chat"))).GetProperty("isDisabled").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { ana }, steps = new[] { new { kind = "role.paint" } } }));
    }
}
