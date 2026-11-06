using System.Net;
using System.Net.Http.Json;
using Llm.Api.Company;
using Llm.Api.Identity;
using Llm.Api.Ldap;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>Admin → Access → Groups: the groups new people join at their first sign-in, made by an admin, from the directory or company sign-in.</summary>
[Collection(nameof(AppCollection))]
public sealed class DefaultGroupsTests(AppFixture app)
{
    private static string Unique(string stem) => $"{stem} {Guid.NewGuid().ToString("N")[..6]}";

    private static Task<HttpResponseMessage> PutAsync(TestBrowser b, string path, object body) => b.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private async Task<Guid[]> GroupsOfAsync(Guid person)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        return [.. await scope.ServiceProvider.GetRequiredService<AppDbContext>().GroupMembers.Where(m => m.UserId == person).Select(m => m.GroupId).ToListAsync()];
    }

    [Fact]
    [Trait("Control", "IAM-04")]
    public async Task New_people_join_the_default_groups_however_they_first_sign_in_and_only_then()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var staff = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = Unique("Staff") }))).GetProperty("id").GetGuid();
        var onboarding = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = Unique("Onboarding") }))).GetProperty("id").GetGuid();
        var directory = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = Unique("Ops"), directory = "cn=ops,ou=groups,dc=example,dc=test" }))).GetProperty("id").GetGuid();

        // A directory group's members come from the directory: it cannot be one.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PutAsync(admin, "/api/admin/access/defaults", new { groups = new[] { staff, directory } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PutAsync(admin, "/api/admin/access/defaults", new { groups = new[] { Guid.NewGuid() } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(admin, "/api/admin/access/defaults", new { groups = new[] { staff, onboarding } }));
        var overview = await admin.JsonAsync(await admin.GetAsync("/api/admin/access/groups"));
        Assert.True(overview.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == staff).GetProperty("joinsNewPeople").GetBoolean());
        Assert.False(overview.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == directory).GetProperty("canBeDefault").GetBoolean());
        var history = await admin.JsonAsync(await admin.GetAsync("/api/admin/access/history?kind=defaults&take=1"));
        Assert.Contains("Onboarding", history[0].GetProperty("after").GetString());

        try
        {
            // An account an admin makes.
            var name = "dg" + Guid.NewGuid().ToString("N")[..10];
            var local = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("id").GetGuid();
            Assert.Equivalent(new[] { staff, onboarding }, await GroupsOfAsync(local));
            overview = await admin.JsonAsync(await admin.GetAsync("/api/admin/access/groups"));
            Assert.Contains(overview.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == staff).GetProperty("members").EnumerateArray(), m => m.GetProperty("id").GetGuid() == local);

            // From the directory at the first sign-in; taken out, the next sign-in leaves them out.
            Guid fromDirectory;
            var ldapName = "dl" + Guid.NewGuid().ToString("N")[..10];
            var person = new LdapPerson($"uid={ldapName},ou=people,dc=example,dc=test", ldapName, $"{ldapName}@example.test", "Lee", []);
            await using (var scope = app.Factory.Services.CreateAsyncScope())
            {
                var (user, refusal) = await scope.ServiceProvider.GetRequiredService<SignInService>().SyncFromDirectoryAsync(person);
                Assert.True(user is not null, refusal);
                fromDirectory = user.Id;
            }
            Assert.Equivalent(new[] { staff, onboarding }, await GroupsOfAsync(fromDirectory));
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{onboarding}/members/{fromDirectory}", UriKind.Relative)));
            await using (var scope = app.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SignInService>().SyncFromDirectoryAsync(person);
            }
            Assert.Equal([staff], await GroupsOfAsync(fromDirectory));

            // From company sign-in (OIDC and SAML alike) and SCIM, which make people through the same place.
            var oidcName = "do" + Guid.NewGuid().ToString("N")[..10];
            await using (var scope = app.Factory.Services.CreateAsyncScope())
            {
                var made = await scope.ServiceProvider.GetRequiredService<CompanyPeople>().CreateAsync(oidcName, $"{oidcName}@example.test", "Oli", "from company sign-in");
                Assert.Equivalent(new[] { staff, onboarding }, await GroupsOfAsync(made.Id));
            }
        }
        finally
        {
            await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(admin, "/api/admin/access/defaults", new { groups = Array.Empty<Guid>() }));
        }
        // None set: a new person joins nothing.
        var last = "dn" + Guid.NewGuid().ToString("N")[..10];
        var nobody = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = last, email = $"{last}@example.test" }))).GetProperty("id").GetGuid();
        Assert.Empty(await GroupsOfAsync(nobody));
    }

    [Fact]
    public async Task Choosing_the_default_groups_needs_access_defaults()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "dp" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var them = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(them, "/api/admin/access/defaults", new { groups = Array.Empty<Guid>() }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/access/groups"));
    }

    [Fact]
    public async Task Groups_from_before_default_groups_are_kept_and_none_is_a_default()
    {
        var cs = app.ConnectionStringFor("defaultgroups_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_DefaultGroups", StringComparison.Ordinal)) - 1]);

        var group = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "groups", new() { ["Id"] = group, ["Name"] = "Engineering", ["Priority"] = 0, ["CreatedAt"] = DateTimeOffset.UtcNow });
        }
        await db.Database.MigrateAsync();

        var kept = await db.Groups.AsNoTracking().SingleAsync(g => g.Id == group);
        Assert.Equal(("Engineering", false), (kept.Name, kept.JoinsNewPeople));
    }
}
