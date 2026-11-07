using System.Net;
using System.Net.Http.Json;
using Llm.Api.Access;
using Llm.Api.Settings;
using Llm.Core.Access;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Nobody gives more than they hold: a person's role changes need the access permission that governs roles, nobody grants a
/// role (by an assignment, a role's edit, a group, a request's yes or a bulk change) holding a permission they do not hold
/// themselves, only an Owner makes, unmakes, disables or deletes an Owner, and who resets credentials does it only for people
/// who hold nothing beyond them.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class PeopleEscalationTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private async Task<(Guid Id, TestBrowser Browser)> PersonAsync(TestBrowser admin)
    {
        var name = "esc" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!));
    }

    private static async Task<Guid> RoleAsync(TestBrowser admin, params string[] permissions)
    {
        var res = await admin.PostAsync("/api/admin/access/roles", new { name = "Esc " + Guid.NewGuid().ToString("N")[..8], permissions });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task GiveAsync(TestBrowser admin, Guid role, Guid person) =>
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = person }));

    /// <summary>A person who holds a role with just these permissions (and Member, as everyone does).</summary>
    private async Task<(Guid Id, TestBrowser Browser)> HolderAsync(TestBrowser admin, params string[] permissions)
    {
        var (id, browser) = await PersonAsync(admin);
        await GiveAsync(admin, await RoleAsync(admin, permissions), id);
        return (id, browser);
    }

    private static Task<HttpResponseMessage> PatchAsync(TestBrowser b, string path, object body) =>
        b.Http.PatchAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private static async Task<Guid> GroupAsync(TestBrowser admin, string name)
    {
        var res = await admin.PostAsync("/api/admin/groups", new { name = name + " " + Guid.NewGuid().ToString("N")[..6] });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task JoinAsync(TestBrowser admin, Guid group, Guid person) =>
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { person } }));

    /// <summary>A role given to a person or a group for some groups only.</summary>
    private static async Task ScopedAsync(TestBrowser admin, Guid role, string kind, Guid subject, params Guid[] groups) =>
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments",
            new { roleId = role, subjectKind = kind, subjectId = subject, scope = new { groups } }));

    private static async Task<Guid> RuleAsync(TestBrowser admin, string effect, string kind, Guid subject, string feature)
    {
        var res = await admin.PostAsync("/api/admin/access/rules", new { effect, subjectKind = kind, subjectId = subject, resourceKind = ResourceKinds.Feature, resource = feature });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private async Task<bool> IsOwnerAsync(Guid id)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(id.ToString());
        return await scope.ServiceProvider.GetRequiredService<AccessService>().IsOwnerAsync(user!);
    }

    private async Task<Guid> OwnerRoleAsync()
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>().AccessRoles.Where(r => r.Key == BuiltInRoles.Owner).Select(r => r.Id).SingleAsync();
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task People_edit_alone_renames_but_makes_nobody_Owner()
    {
        var admin = await AdminAsync();
        var (editorId, editor) = await HolderAsync(admin, Permissions.PeopleEdit);
        var (otherId, _) = await PersonAsync(admin);

        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(editor, $"/api/admin/people/{editorId}", new { admin = true }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(editor, $"/api/admin/people/{otherId}", new { admin = true }));
        Assert.False(await IsOwnerAsync(editorId));
        Assert.False(await IsOwnerAsync(otherId));
        // Disabling is people.disable's, not people.edit's.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(editor, $"/api/admin/people/{otherId}", new { disabled = true }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(editor, $"/api/admin/people/{otherId}", new { displayName = "Renamed by people.edit" }));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Only_an_Owner_makes_an_Owner_even_with_access_assign()
    {
        var admin = await AdminAsync();
        var (assignerId, assigner) = await HolderAsync(admin, Permissions.PeopleEdit, Permissions.AccessAssign);
        var (otherId, _) = await PersonAsync(admin);

        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(assigner, $"/api/admin/people/{assignerId}", new { admin = true }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(assigner, $"/api/admin/people/{otherId}", new { admin = true }));
        var owner = await OwnerRoleAsync();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await assigner.PostAsync("/api/admin/access/assignments", new { roleId = owner, subjectKind = "Person", subjectId = assignerId }));
        Assert.False(await IsOwnerAsync(assignerId));
        Assert.False(await IsOwnerAsync(otherId));
        // A new person is no Owner either, unless an Owner makes them one.
        var (creatorId, creator) = await HolderAsync(admin, Permissions.PeopleCreate);
        var name = "esc" + Guid.NewGuid().ToString("N")[..10];
        await StatusAssert.Is(HttpStatusCode.Forbidden, await creator.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", admin = true }));
        Assert.False(await IsOwnerAsync(creatorId));

        // An Owner does it, and undoes it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, $"/api/admin/people/{otherId}", new { admin = true }));
        Assert.True(await IsOwnerAsync(otherId));
        // Who is not an Owner neither takes it away nor disables nor deletes one.
        var (_, manager) = await HolderAsync(admin, Permissions.PeopleManage, Permissions.AccessAssign);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(manager, $"/api/admin/people/{otherId}", new { admin = false }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(manager, $"/api/admin/people/{otherId}", new { disabled = true }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await manager.Http.DeleteAsync(new Uri($"/api/admin/people/{otherId}", UriKind.Relative)));
        Assert.True(await IsOwnerAsync(otherId));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, $"/api/admin/people/{otherId}", new { admin = false }));
        Assert.False(await IsOwnerAsync(otherId));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task People_disable_alone_disables_and_enables()
    {
        var admin = await AdminAsync();
        var (_, disabler) = await HolderAsync(admin, Permissions.PeopleDisable);
        var (otherId, _) = await PersonAsync(admin);

        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(disabler, $"/api/admin/people/{otherId}", new { disabled = true }));
        var person = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{otherId}"));
        Assert.True(person.GetProperty("person").GetProperty("disabled").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(disabler, $"/api/admin/people/{otherId}", new { disabled = false }));
        // Nothing else: no name, no role.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(disabler, $"/api/admin/people/{otherId}", new { displayName = "Not theirs to change" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(disabler, $"/api/admin/people/{otherId}", new { admin = true }));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Nobody_gives_a_role_holding_what_they_do_not_hold()
    {
        var admin = await AdminAsync();
        var (assignerId, assigner) = await HolderAsync(admin, Permissions.AccessAssign, Permissions.AccessRoles, Permissions.PeopleRead);
        var (otherId, _) = await PersonAsync(admin);
        var broad = await RoleAsync(admin, Permissions.SettingsManage);
        var narrow = await RoleAsync(admin, Permissions.PeopleRead);

        // An assignment: of what they hold, yes; beyond it, no, to themselves or to anyone.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await assigner.PostAsync("/api/admin/access/assignments", new { roleId = broad, subjectKind = "Person", subjectId = assignerId }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await assigner.PostAsync("/api/admin/access/assignments", new { roleId = broad, subjectKind = "Person", subjectId = otherId }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await assigner.PostAsync("/api/admin/access/assignments", new { roleId = broad, subjectKind = "Everyone" }));
        await StatusAssert.Is(HttpStatusCode.Created, await assigner.PostAsync("/api/admin/access/assignments", new { roleId = narrow, subjectKind = "Person", subjectId = otherId }));

        // A role's edit: no more in a role someone holds than the one editing it holds.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PatchAsync(assigner, $"/api/admin/access/roles/{narrow}", new { permissions = new[] { Permissions.PeopleRead, Permissions.SettingsManage } }));
        // A bulk change is refused whole.
        var (_, bulker) = await HolderAsync(admin, Permissions.AccessManage);
        await StatusAssert.Is(HttpStatusCode.Conflict, await bulker.PostAsync("/api/admin/access/bulk", new { steps = new[] { new { kind = "role.give", roleId = await OwnerRoleAsync() } }, people = new[] { otherId }, dryRun = false }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await bulker.PostAsync("/api/admin/access/bulk", new { steps = new[] { new { kind = "role.give", roleId = broad } }, people = new[] { otherId }, dryRun = false }));

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        Assert.False(await db.AccessAssignments.AnyAsync(a => a.RoleId == broad));
        Assert.Equal([Permissions.PeopleRead], (await db.AccessRoles.SingleAsync(r => r.Id == narrow)).Permissions);
        Assert.False(await IsOwnerAsync(otherId));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task A_request_is_granted_only_by_who_holds_what_it_gives()
    {
        var admin = await AdminAsync();
        var (_, decider) = await HolderAsync(admin, Permissions.AccessRequests);
        var (_, asker) = await PersonAsync(admin);
        var broad = await RoleAsync(admin, Permissions.SettingsManage);
        var res = await asker.PostAsync("/api/access-requests", new { roleId = broad, reason = "To change the settings." });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var request = (await asker.JsonAsync(res)).GetProperty("id").GetGuid();

        await StatusAssert.Is(HttpStatusCode.BadRequest, await decider.PostAsync($"/api/admin/access/requests/{request}/decide", new { approve = true }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/access/requests/{request}/decide", new { approve = true }));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Joining_a_group_gives_nobody_what_the_one_adding_does_not_hold()
    {
        var admin = await AdminAsync();
        var (memberId, members) = await HolderAsync(admin, Permissions.GroupsMembers);
        var res = await admin.PostAsync("/api/admin/groups", new { name = "Owners " + Guid.NewGuid().ToString("N")[..6] });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var owners = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = await OwnerRoleAsync(), subjectKind = "Group", subjectId = owners }));
        res = await admin.PostAsync("/api/admin/groups", new { name = "Plain " + Guid.NewGuid().ToString("N")[..6] });
        var plain = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();

        await StatusAssert.Is(HttpStatusCode.Forbidden, await members.PostAsync($"/api/admin/groups/{owners}/members", new { userIds = new[] { memberId } }));
        Assert.False(await IsOwnerAsync(memberId));
        await StatusAssert.Is(HttpStatusCode.NoContent, await members.PostAsync($"/api/admin/groups/{plain}/members", new { userIds = new[] { memberId } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{owners}/members", new { userIds = new[] { memberId } }));
        Assert.True(await IsOwnerAsync(memberId));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{owners}", UriKind.Relative)));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task A_permission_held_for_some_groups_is_given_for_those_groups_only()
    {
        var admin = await AdminAsync();
        var a = await GroupAsync(admin, "Team A");
        var b = await GroupAsync(admin, "Team B");
        var edit = await RoleAsync(admin, Permissions.PeopleEdit);
        // People edit for team A, and giving roles for everyone.
        var (assignerId, assigner) = await HolderAsync(admin, Permissions.AccessAssign, Permissions.PeopleRead);
        await ScopedAsync(admin, edit, "Person", assignerId, a);
        var (otherId, _) = await PersonAsync(admin);

        foreach (var groups in new[] { new[] { b }, new[] { a, b } })
        {
            await StatusAssert.Is(HttpStatusCode.BadRequest, await assigner.PostAsync("/api/admin/access/assignments",
                new { roleId = edit, subjectKind = "Person", subjectId = otherId, scope = new { groups } }));
        }
        await StatusAssert.Is(HttpStatusCode.BadRequest, await assigner.PostAsync("/api/admin/access/assignments", new { roleId = edit, subjectKind = "Person", subjectId = otherId }));
        await StatusAssert.Is(HttpStatusCode.Created, await assigner.PostAsync("/api/admin/access/assignments",
            new { roleId = edit, subjectKind = "Person", subjectId = otherId, scope = new { groups = new[] { a } } }));

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        var held = await db.AccessAssignments.Where(x => x.RoleId == edit && x.SubjectId == otherId).ToListAsync();
        Assert.Equal([a], Assert.Single(held).Scope!.Groups);
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Joining_a_group_or_a_bulk_change_gives_a_scoped_role_only_within_the_groups_held()
    {
        var admin = await AdminAsync();
        var a = await GroupAsync(admin, "Team A");
        var b = await GroupAsync(admin, "Team B");
        var edit = await RoleAsync(admin, Permissions.PeopleEdit);
        // Two groups whose members edit people: one of team A's, one of team B's.
        var forA = await GroupAsync(admin, "Editors of A");
        var forB = await GroupAsync(admin, "Editors of B");
        await ScopedAsync(admin, edit, "Group", forA, a);
        await ScopedAsync(admin, edit, "Group", forB, b);
        var (memberId, members) = await HolderAsync(admin, Permissions.GroupsMembers, Permissions.AccessManage);
        await ScopedAsync(admin, edit, "Person", memberId, a);
        var (otherId, _) = await PersonAsync(admin);
        var (thirdId, _) = await PersonAsync(admin);

        await StatusAssert.Is(HttpStatusCode.Forbidden, await members.PostAsync($"/api/admin/groups/{forB}/members", new { userIds = new[] { otherId } }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await members.PostAsync("/api/admin/access/bulk",
            new { steps = new[] { new { kind = "group.join", groupId = forB } }, people = new[] { thirdId }, dryRun = false }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await members.PostAsync($"/api/admin/groups/{forA}/members", new { userIds = new[] { otherId } }));
        await StatusAssert.Is(HttpStatusCode.OK, await members.PostAsync("/api/admin/access/bulk",
            new { steps = new[] { new { kind = "group.join", groupId = forA } }, people = new[] { thirdId }, dryRun = false }));

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        Assert.False(await db.GroupMembers.AnyAsync(m => m.GroupId == forB));
        Assert.Equal(2, await db.GroupMembers.CountAsync(m => m.GroupId == forA));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task A_feature_held_through_a_role_for_some_groups_is_given_by_a_rule_to_their_people_only()
    {
        var admin = await AdminAsync();
        var a = await GroupAsync(admin, "Team A");
        var b = await GroupAsync(admin, "Team B");
        var without = await GroupAsync(admin, "No video");
        // The rule writer holds video only through a role for team A: their group's deny overrides Member's.
        var (writerId, writer) = await HolderAsync(admin, Permissions.AccessRules, Permissions.PeopleRead);
        await JoinAsync(admin, without, writerId);
        await RuleAsync(admin, "Deny", "Group", without, Permissions.Video);
        await ScopedAsync(admin, await RoleAsync(admin, Permissions.Video), "Person", writerId, a);
        var (inAId, _) = await PersonAsync(admin);
        var (inBId, _) = await PersonAsync(admin);
        await JoinAsync(admin, a, inAId);
        await JoinAsync(admin, b, inBId);
        var denyA = await RuleAsync(admin, "Deny", "Person", inAId, Permissions.Video);
        var denyB = await RuleAsync(admin, "Deny", "Person", inBId, Permissions.Video);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await writer.Http.DeleteAsync(new Uri($"/api/admin/access/rules/{denyB}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await writer.Http.DeleteAsync(new Uri($"/api/admin/access/rules/{denyA}", UriKind.Relative)));

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        Assert.True(await db.AccessRules.AnyAsync(r => r.Id == denyB));
        Assert.False(await db.AccessRules.AnyAsync(r => r.Id == denyA));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Enabling_again_is_only_for_who_holds_nothing_beyond_the_one_enabling()
    {
        var admin = await AdminAsync();
        var (_, disabler) = await HolderAsync(admin, Permissions.PeopleDisable, Permissions.AccessManage);
        var (adminId, _) = await HolderAsync(admin, Permissions.SettingsManage);
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, $"/api/admin/people/{adminId}", new { disabled = true }));

        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(disabler, $"/api/admin/people/{adminId}", new { disabled = false }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await disabler.PostAsync("/api/admin/access/bulk",
            new { steps = new[] { new { kind = "account.enable" } }, people = new[] { adminId }, dryRun = false }));
        var person = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{adminId}"));
        Assert.True(person.GetProperty("person").GetProperty("disabled").GetBoolean());
        // Disabling them is still theirs; an Owner enables them again.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, $"/api/admin/people/{adminId}", new { disabled = false }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(disabler, $"/api/admin/people/{adminId}", new { disabled = true }));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Credentials_are_reset_only_for_who_holds_nothing_beyond_the_one_resetting()
    {
        var admin = await AdminAsync();
        var (_, resetter) = await HolderAsync(admin, Permissions.PeoplePasswords, Permissions.PeopleKeys);
        var (memberId, _) = await PersonAsync(admin);
        var (adminId, _) = await HolderAsync(admin, Permissions.SettingsManage);
        var (owner, _) = await PersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(admin, $"/api/admin/people/{owner}", new { admin = true }));

        foreach (var target in new[] { owner, adminId })
        {
            await StatusAssert.Is(HttpStatusCode.Forbidden, await resetter.PostAsync($"/api/admin/people/{target}/password"));
            await StatusAssert.Is(HttpStatusCode.Forbidden, await resetter.PostAsync($"/api/admin/people/{target}/2fa/reset"));
            await StatusAssert.Is(HttpStatusCode.Forbidden, await resetter.PostAsync($"/api/admin/people/{target}/key"));
        }
        await StatusAssert.Is(HttpStatusCode.OK, await resetter.PostAsync($"/api/admin/people/{memberId}/password"));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Who_is_an_admin_through_sign_in_and_SCIM_is_set_by_an_Owner()
    {
        var admin = await AdminAsync();
        var directory = SettingsCatalog.PermissionOf(SettingsCatalog.ByKey["Ldap:AdminGroup"]);
        var sso = SettingsCatalog.PermissionOf(SettingsCatalog.ByKey["CompanySignIn:AdminGroup"]);
        var (_, signIn) = await HolderAsync(admin, directory, sso, Permissions.SettingsRead);

        await StatusAssert.Is(HttpStatusCode.Forbidden, await signIn.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Ldap:AdminGroup", value = "mine" } } }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await signIn.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "CompanySignIn:AdminGroup", value = "mine" } } }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await signIn.PostAsync("/api/admin/company-sign-in/scim-token"));
    }
}
