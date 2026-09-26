using System.Net;
using System.Net.Http.Json;
using Llm.Api.Access;
using Llm.Core.Access;
using Llm.Core.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Groups for access rules: app groups chosen in the app, directory groups the directory fills.</summary>
[Collection(nameof(AppCollection))]
public sealed class GroupsTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<(Guid Id, string Name, string Password)> PersonAsync(TestBrowser admin)
    {
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (made.GetProperty("id").GetGuid(), name, made.GetProperty("password").GetString()!);
    }

    private static async Task<Guid> GroupAsync(TestBrowser admin, object body)
    {
        var res = await admin.PostAsync("/api/admin/groups", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private async Task<Membership> MembershipAsync(Guid userId)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<Llm.Core.Identity.AppUser>>().FindByIdAsync(userId.ToString());
        return await scope.ServiceProvider.GetRequiredService<AccessService>().MembershipAsync(user!);
    }

    [Fact]
    public async Task An_app_group_has_the_people_an_admin_puts_in_it()
    {
        var admin = await AdminAsync();
        var (ann, annName, annPassword) = await PersonAsync(admin);
        var (ben, _, _) = await PersonAsync(admin);
        var name = Unique("Data science");
        var id = await GroupAsync(admin, new { name, description = "Notebook people" });
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/groups", new { name = name.ToUpperInvariant() }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/groups", new { name = " " }));

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = new[] { ann, ben, ann } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = new[] { ann } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = new[] { Guid.NewGuid() } }));
        var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/groups"));
        Assert.Equal(2, list.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == id).GetProperty("members").GetInt32());
        Assert.Contains(id, (await MembershipAsync(ann)).Groups);
        var detail = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{ann}"));
        Assert.Equal([name], detail.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()));

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{id}/members/{ann}", UriKind.Relative)));
        Assert.DoesNotContain(id, (await MembershipAsync(ann)).Groups);
        Assert.Contains(id, (await MembershipAsync(ben)).Groups);

        // Only admins manage groups; every change is audited.
        var member = await new TestBrowser(app.Factory).SignedInAsync(annName, annPassword);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.GetAsync("/api/admin/groups"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/admin/groups/{id}"));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("group.create", audit);
        Assert.Contains("group.add_member", audit);
        Assert.Contains("group.remove_member", audit);
        Assert.Contains("group.delete", audit);
    }

    [Fact]
    public async Task A_directory_group_has_whoever_the_directory_puts_in_it()
    {
        var admin = await AdminAsync();
        var (dana, _, _) = await PersonAsync(admin);
        var (eli, _, _) = await PersonAsync(admin);
        var dn = $"cn=platform-{Guid.NewGuid().ToString("N")[..6]},ou=groups,dc=example,dc=test";
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == dana);
            user.DirectoryGroups = [dn.ToUpperInvariant()];
            await db.SaveChangesAsync();
        }
        var cn = dn[3..dn.IndexOf(',', StringComparison.Ordinal)];
        var byName = await GroupAsync(admin, new { name = Unique("Platform"), directory = cn });
        var byDn = await GroupAsync(admin, new { name = Unique("Platform by DN"), directory = dn });

        var seen = await admin.JsonAsync(await admin.GetAsync("/api/admin/groups/directory"));
        Assert.Contains(cn, seen.EnumerateArray().Select(g => g.GetProperty("name").GetString()), StringComparer.OrdinalIgnoreCase);
        var group = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{byName}"));
        Assert.Equal([dana], group.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()));
        var membership = await MembershipAsync(dana);
        Assert.Contains(byName, membership.Groups);
        Assert.Contains(byDn, membership.Groups);
        Assert.DoesNotContain(byName, (await MembershipAsync(eli)).Groups);

        // The directory decides who is in it, and it stays a directory group.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/groups/{byName}/members", new { userIds = new[] { eli } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsync(new Uri($"/api/admin/groups/{byName}", UriKind.Relative),
            JsonContent.Create(new { directory = "" })));
    }

    [Fact]
    public void Who_may_use_something_admins_always_everyone_or_members()
    {
        var group = Guid.NewGuid();
        var member = new Membership(false, new HashSet<Guid> { group });
        var outsider = new Membership(false, new HashSet<Guid>());
        var admin = new Membership(true, new HashSet<Guid>());
        Assert.True(outsider.May(Audience.Everyone, null));
        Assert.False(member.May(Audience.Admins, null));
        Assert.True(admin.May(Audience.Admins, null));
        Assert.True(member.May(Audience.Groups, [group]));
        Assert.False(outsider.May(Audience.Groups, [group]));
        Assert.True(admin.May(Audience.Groups, [Guid.NewGuid()]));
    }
}
