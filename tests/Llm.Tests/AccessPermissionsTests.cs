using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Core.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The finer permissions: company-wide and any-group sharing by permission, not by being Owner; rules on a shared
/// assistant or skill; the people list kept to people.read; a person's effective permissions with why; role templates.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class AccessPermissionsTests(AppFixture app)
{
    private async Task<(Guid Id, TestBrowser Browser)> PersonAsync(TestBrowser admin, string stem)
    {
        var name = stem + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!));
    }

    private async Task<Guid> RoleAsync(TestBrowser admin, Guid person, params string[] permissions)
    {
        var role = (await admin.JsonAsync(await admin.PostAsync("/api/admin/access/roles", new { name = "Role " + Guid.NewGuid().ToString("N")[..8], permissions }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = person }));
        return role;
    }

    private static Task<HttpResponseMessage> PutAsync(TestBrowser b, string path, object body) => b.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body);

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task The_people_list_with_emails_and_spend_needs_people_read_not_monitoring()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (id, watcher) = await PersonAsync(admin, "pc");
        await RoleAsync(admin, id, Permissions.MonitoringRead);
        await StatusAssert.Is(HttpStatusCode.OK, await watcher.GetAsync("/api/admin/overview"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await watcher.GetAsync("/api/admin/people.csv"));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.GetAsync("/api/admin/people.csv"));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Sharing_with_the_company_is_a_permission_and_a_rule_keeps_a_shared_assistant_from_a_group()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ownerId, owner) = await PersonAsync(admin, "so");
        var (_, other) = await PersonAsync(admin, "sx");
        var res = await owner.PostAsync("/api/assistants", new { name = "Release notes", instructions = "Write release notes." });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var assistant = (await owner.JsonAsync(res)).GetProperty("id").GetGuid();

        // Not Owner, and without sharing.company: refused; with it, shared.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(owner, $"/api/assistants/{assistant}/sharing", new { reach = "Company" }));
        var role = await RoleAsync(admin, ownerId, Permissions.SharingCompany);
        await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(owner, $"/api/assistants/{assistant}/sharing", new { reach = "Company" }));
        var seen = await other.JsonAsync(await other.GetAsync("/api/assistants"));
        Assert.Contains(seen.EnumerateArray(), a => a.GetProperty("id").GetGuid() == assistant);

        // A deny on the assistant for everyone: the others neither see nor open it; its owner still does.
        res = await admin.PostAsync("/api/admin/access/rules", new { effect = "Deny", subjectKind = "Everyone", resourceKind = ResourceKinds.Assistant, resource = assistant.ToString(), note = "Not yet" });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var rule = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        seen = await other.JsonAsync(await other.GetAsync("/api/assistants"));
        Assert.DoesNotContain(seen.EnumerateArray(), a => a.GetProperty("id").GetGuid() == assistant);
        Assert.NotEqual(HttpStatusCode.OK, (await other.GetAsync($"/api/assistants/{assistant}")).StatusCode);
        Assert.Contains((await owner.JsonAsync(await owner.GetAsync("/api/assistants"))).EnumerateArray(), a => a.GetProperty("id").GetGuid() == assistant);

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/access/rules/{rule}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.OK, await other.GetAsync($"/api/assistants/{assistant}"));
        await using var scope = app.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AccessService>().ChangeAsync(null, async edit =>
            edit.Remove(await edit.Db.AccessAssignments.SingleAsync(a => a.RoleId == role)));
    }

    [Fact]
    public async Task Skills_go_to_groups_with_their_feature_and_to_the_company_with_sharing_company()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (id, person) = await PersonAsync(admin, "sk");
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Skills " + Guid.NewGuid().ToString("N")[..8] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { id } }));
        var library = await person.JsonAsync(await person.GetAsync("/api/skills"));
        Assert.True(library.GetProperty("mayShareGroups").GetBoolean());
        Assert.False(library.GetProperty("mayShareCompany").GetBoolean());
        Assert.False(library.GetProperty("mayTurnBuiltIn").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.PostAsync("/api/skills", new { name = "co" + Guid.NewGuid().ToString("N")[..6], title = "T", instructions = "Do it.", sharing = "Company" }));

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Llm.Core.Identity.AppUser>>();
            await access.SetFeatureAsync(null, (await users.FindByIdAsync(id.ToString()))!, Permissions.SkillsShare, on: false);
        }
        Assert.False((await person.JsonAsync(await person.GetAsync("/api/skills"))).GetProperty("mayShareGroups").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.PostAsync("/api/skills", new { name = "gr" + Guid.NewGuid().ToString("N")[..6], title = "T", instructions = "Do it.", sharing = "Groups", groups = new[] { group } }));
        await StatusAssert.Is(HttpStatusCode.Created, await person.PostAsync("/api/skills", new { name = "me" + Guid.NewGuid().ToString("N")[..6], title = "T", instructions = "Do it." }));
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task A_persons_effective_permissions_say_why_each_is_held_or_not()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (id, _) = await PersonAsync(admin, "ef");
        var role = await RoleAsync(admin, id, Permissions.PeoplePasswords);
        var res = await admin.PostAsync("/api/admin/access/rules", new { effect = "Deny", subjectKind = "Person", subjectId = id, resourceKind = "feature", resource = Permissions.Pictures, note = "Budget" });
        await StatusAssert.Is(HttpStatusCode.Created, res);

        var e = await admin.JsonAsync(await admin.GetAsync($"/api/admin/access/effective?person={id}"));
        JsonElement Of(string key) => e.GetProperty("permissions").EnumerateArray().Single(p => p.GetProperty("key").GetString() == key);
        Assert.True(Of(Permissions.PeoplePasswords).GetProperty("allowed").GetBoolean());
        // Acting in an area reads it.
        Assert.True(Of(Permissions.PeopleRead).GetProperty("allowed").GetBoolean());
        Assert.False(Of(Permissions.PeopleDelete).GetProperty("allowed").GetBoolean());
        Assert.False(Of(Permissions.Pictures).GetProperty("allowed").GetBoolean());
        Assert.Contains("Budget", Of(Permissions.Pictures).GetProperty("because")[0].GetString());
        Assert.True(Of(Permissions.Chat).GetProperty("allowed").GetBoolean());
        Assert.Contains(e.GetProperty("roles").EnumerateArray(), r => r.GetProperty("id").GetGuid() == role);
        Assert.Contains(e.GetProperty("rules").EnumerateArray(), r => r.GetProperty("resource").GetString() == Permissions.Pictures);
        Assert.Equal(Permissions.All.Count, e.GetProperty("permissions").GetArrayLength());
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/admin/access/effective?person={Guid.NewGuid()}"));
    }

    [Fact]
    public async Task The_overview_has_the_matrix_and_templates_that_make_working_roles()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var o = await admin.JsonAsync(await admin.GetAsync("/api/admin/access"));
        var operatorRole = o.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == BuiltInRoles.Operator);
        var gives = operatorRole.GetProperty("gives").EnumerateArray().Select(x => x.GetString()).ToList();
        // Its parts and the reading of its areas, not only what it names.
        Assert.Contains(Permissions.StorageDelete, gives);
        Assert.Contains(Permissions.StorageRead, gives);
        Assert.DoesNotContain(Permissions.PeopleRead, gives);
        var templates = o.GetProperty("templates").EnumerateArray().ToList();
        Assert.Equal(RoleTemplates.All.Count, templates.Count);
        foreach (var t in RoleTemplates.All)
        {
            Assert.All(t.Permissions, p => Assert.True(Permissions.IsKnown(p), $"{t.Key}: {p}"));
        }
        var res = await admin.PostAsync("/api/admin/access/roles", new { name = "Help desk " + Guid.NewGuid().ToString("N")[..6], permissions = RoleTemplates.All[0].Permissions });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/access/roles/{(await admin.JsonAsync(res)).GetProperty("id").GetGuid()}", UriKind.Relative)));
        var history = await admin.JsonAsync(await admin.GetAsync("/api/admin/access/history?kind=role&take=5"));
        Assert.All(history.EnumerateArray(), h => Assert.Equal("role", h.GetProperty("kind").GetString()));
        Assert.NotEqual(0, history.GetArrayLength());
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Code_Arenas_memory_needs_feature_memory_as_the_chats_does()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "cm" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var id = made.GetProperty("id").GetGuid();
        var arena = app.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        arena.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", made.GetProperty("apiKey").GetString());
        arena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        await StatusAssert.Is(HttpStatusCode.OK, await arena.GetAsync(new Uri("/api/code-arena/memories", UriKind.Relative)));

        var res = await admin.PostAsync("/api/admin/access/rules", new { effect = "Deny", subjectKind = "Person", subjectId = id, resourceKind = ResourceKinds.Feature, resource = Permissions.Memory });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.GetAsync(new Uri("/api/code-arena/memories", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.PostAsJsonAsync("/api/code-arena/memories", new { text = "Not kept." }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.PostAsJsonAsync("/api/code-arena/memories/recall", new { query = "how to build" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.PostAsJsonAsync("/api/code-arena/memories/import", new { memories = new[] { new { text = "Not brought in." } } }));
        // What is theirs they may still forget, as on the web: no such memory, not refused.
        await StatusAssert.Is(HttpStatusCode.NotFound, await arena.DeleteAsync(new Uri($"/api/code-arena/memories/{Guid.NewGuid()}", UriKind.Relative)));
    }
}
