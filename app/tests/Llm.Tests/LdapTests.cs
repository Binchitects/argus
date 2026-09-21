using System.Net;
using System.Net.Http.Json;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>A real OpenLDAP with a small company in it.</summary>
public sealed class LdapServer : IAsyncLifetime
{
    public const string Base = "dc=example,dc=test";
    public const string AdminDn = "cn=admin," + Base;
    public const string AdminPassword = "ldap-admin-pw";

    private const string Seed = """
        dn: ou=people,dc=example,dc=test
        objectClass: organizationalUnit
        ou: people

        dn: ou=groups,dc=example,dc=test
        objectClass: organizationalUnit
        ou: groups

        dn: uid=alice,ou=people,dc=example,dc=test
        objectClass: inetOrgPerson
        uid: alice
        cn: Alice Admin
        sn: Admin
        displayName: Alice Admin
        mail: Alice@Example.test
        userPassword: alice-directory-pw

        dn: uid=bob,ou=people,dc=example,dc=test
        objectClass: inetOrgPerson
        uid: bob
        cn: Bob Member
        sn: Member
        mail: bob@example.test
        userPassword: bob-directory-pw

        dn: uid=carol,ou=people,dc=example,dc=test
        objectClass: inetOrgPerson
        uid: carol
        cn: Carol Nomail
        sn: Nomail
        userPassword: carol-directory-pw

        dn: uid=dave,ou=people,dc=example,dc=test
        objectClass: inetOrgPerson
        uid: dave
        cn: Dave Outsider
        sn: Outsider
        mail: dave@example.test
        userPassword: dave-directory-pw

        dn: uid=erin,ou=people,dc=example,dc=test
        objectClass: inetOrgPerson
        uid: erin
        cn: Erin Directory
        sn: Directory
        mail: erin-directory@example.test
        userPassword: erin-directory-pw

        dn: cn=llm-admins,ou=groups,dc=example,dc=test
        objectClass: groupOfNames
        cn: llm-admins
        member: uid=alice,ou=people,dc=example,dc=test

        dn: cn=llm-users,ou=groups,dc=example,dc=test
        objectClass: groupOfNames
        cn: llm-users
        member: uid=alice,ou=people,dc=example,dc=test
        member: uid=bob,ou=people,dc=example,dc=test
        member: uid=carol,ou=people,dc=example,dc=test
        member: uid=erin,ou=people,dc=example,dc=test

        """;

    private readonly IContainer _ldap = new ContainerBuilder("osixia/openldap:1.5.0")
        .WithEnvironment("LDAP_ORGANISATION", "Example")
        .WithEnvironment("LDAP_DOMAIN", "example.test")
        .WithEnvironment("LDAP_ADMIN_PASSWORD", AdminPassword)
        .WithEnvironment("LDAP_TLS_VERIFY_CLIENT", "never")
        .WithPortBinding(389, true)
        .WithResourceMapping(Encoding.UTF8.GetBytes(Seed), "/tmp/seed.ldif")
        // -ZZ: the image first runs a temporary server to configure itself and adds TLS
        // after; a StartTLS search only succeeds against the final one.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
            "ldapsearch", "-x", "-ZZ", "-H", "ldap://localhost", "-b", Base, "-D", AdminDn, "-w", AdminPassword))
        .Build();

    public string Url => $"ldap://{_ldap.Hostname}:{_ldap.GetMappedPublicPort(389)}";

    /// <summary>The app wired to this directory, created once by the first test that needs it.</summary>
    public WebApplicationFactory<Program>? App { get; set; }
    public FakeGateway Gateway { get; } = new();

    public async Task InitializeAsync()
    {
        await _ldap.StartAsync();
        await RunAsync("ldapadd", "-x", "-D", AdminDn, "-w", AdminPassword, "-f", "/tmp/seed.ldif");
    }

    /// <summary>Applies an LDIF change (ldapmodify) as the directory admin.</summary>
    public async Task ModifyAsync(string ldif)
    {
        var path = $"/tmp/change-{Guid.NewGuid():N}.ldif";
        await _ldap.CopyAsync(Encoding.UTF8.GetBytes(ldif), path);
        await RunAsync("ldapmodify", "-x", "-D", AdminDn, "-w", AdminPassword, "-f", path);
    }

    private async Task RunAsync(params string[] command)
    {
        var r = await _ldap.ExecAsync(command);
        Assert.True(r.ExitCode == 0, $"{string.Join(' ', command[..1])}: {r.Stderr}");
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }
        await _ldap.DisposeAsync();
    }
}

[Collection(nameof(AppCollection))]
public sealed class LdapTests(AppFixture app, LdapServer ldap) : IClassFixture<LdapServer>
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private Dictionary<string, string?> Settings(string url, bool startTls = false, bool ignoreCerts = false) => new()
    {
        ["Ldap:Url"] = url,
        ["Ldap:StartTls"] = startTls.ToString(),
        ["Ldap:IgnoreCertificateErrors"] = ignoreCerts.ToString(),
        ["Ldap:BindDn"] = LdapServer.AdminDn,
        ["Ldap:BindPassword"] = LdapServer.AdminPassword,
        ["Ldap:UserBaseDn"] = "ou=people," + LdapServer.Base,
        ["Ldap:GroupBaseDn"] = "ou=groups," + LdapServer.Base,
        ["Ldap:AdminGroup"] = "llm-admins",
        ["Ldap:RequiredGroup"] = "llm-users",
    };

    private async Task<WebApplicationFactory<Program>> AppAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (ldap.App is null)
            {
                ldap.App = app.Create(app.ConnectionStringFor("ldap_" + Guid.NewGuid().ToString("N")[..8]), ldap.Gateway, Settings(ldap.Url));
                // A local account that shares a name with a directory entry.
                var admin = await new TestBrowser(ldap.App).SignedInAsync("admin", AppFixture.AdminPassword);
                await admin.PostAsync("/api/admin/people", new { userName = "erin", email = "erin-local@example.test" });
            }
            return ldap.App;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<TestBrowser> Browser() => new(await AppAsync());

    [Fact]
    public async Task A_directory_admin_signs_in_and_is_an_admin_here_with_a_key()
    {
        var b = await (await Browser()).SignedInAsync("alice", "alice-directory-pw");
        var me = await b.JsonAsync(await b.GetAsync("/api/auth/me"));
        Assert.Equal("ldap", me.GetProperty("source").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("alice@example.test", me.GetProperty("email").GetString());
        Assert.Equal("Alice Admin", me.GetProperty("displayName").GetString());
        Assert.NotEmpty(ldap.Gateway.KeysOf("alice@example.test"));
    }

    [Fact]
    public async Task A_directory_member_is_a_member_and_can_sign_in_by_email()
    {
        var b = await (await Browser()).SignedInAsync("bob@example.test", "bob-directory-pw");
        Assert.False((await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("isAdmin").GetBoolean());
    }

    [Theory]
    [InlineData("bob", "wrong-pw")]
    [InlineData("bob", "")]                         // an empty password is an anonymous bind on many servers
    [InlineData("*", "bob-directory-pw")]           // a wildcard must not match anyone
    [InlineData("bob)(uid=*", "bob-directory-pw")] // nor may a name rewrite the filter
    [InlineData("nobody", "whatever-pw")]
    public async Task Wrong_empty_or_crafted_credentials_are_refused(string user, string password)
    {
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await (await Browser()).LoginAsync(user, password));
    }

    [Fact]
    public async Task Someone_outside_the_sign_in_group_is_refused()
    {
        var b = await Browser();
        var res = await b.LoginAsync("dave", "dave-directory-pw");
        await StatusAssert.Is(HttpStatusCode.Forbidden, res);
        Assert.Equal("not_allowed", (await b.JsonAsync(res)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_entry_without_an_email_cannot_be_a_person_here()
    {
        await StatusAssert.Is(HttpStatusCode.Forbidden, await (await Browser()).LoginAsync("carol", "carol-directory-pw"));
    }

    [Fact]
    public async Task A_directory_entry_cannot_take_over_a_local_account()
    {
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await (await Browser()).LoginAsync("erin", "erin-directory-pw"));
    }

    [Fact]
    public async Task Directory_people_manage_their_password_in_the_directory()
    {
        var b = await (await Browser()).SignedInAsync("bob", "bob-directory-pw");
        var res = await b.PostAsync("/api/account/password", new { current = "bob-directory-pw", next = "violet tractor humming seaweed" });
        Assert.Equal("ldap", (await b.JsonAsync(res)).GetProperty("status").GetString());

        var admin = await (await Browser()).SignedInAsync("admin", AppFixture.AdminPassword);
        var id = (await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/people/{id}/password"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { admin = true }));
    }

    [Fact]
    public async Task The_sync_disables_people_who_leave_and_restores_them_when_they_return()
    {
        var sync = "/api/admin/ldap/sync";
        var admin = await (await Browser()).SignedInAsync("admin", AppFixture.AdminPassword);
        await ldap.ModifyAsync("""
            dn: uid=frank,ou=people,dc=example,dc=test
            changetype: add
            objectClass: inetOrgPerson
            uid: frank
            cn: Frank Leaver
            sn: Leaver
            mail: frank@example.test
            userPassword: frank-directory-pw

            dn: cn=llm-users,ou=groups,dc=example,dc=test
            changetype: modify
            add: member
            member: uid=frank,ou=people,dc=example,dc=test

            """);
        var frank = await (await Browser()).SignedInAsync("frank", "frank-directory-pw");

        await ldap.ModifyAsync("""
            dn: cn=llm-users,ou=groups,dc=example,dc=test
            changetype: modify
            delete: member
            member: uid=frank,ou=people,dc=example,dc=test

            """);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync(sync));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await frank.GetAsync("/api/auth/me"));
        Assert.All(ldap.Gateway.KeysOf("frank@example.test"), k => Assert.True(k.Blocked));
        var people = await admin.JsonAsync(await admin.GetAsync("/api/admin/people"));
        var row = people.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("userName").GetString() == "frank");
        Assert.True(row.GetProperty("disabled").GetBoolean());
        Assert.Equal("ldap", row.GetProperty("disabledReason").GetString());

        await ldap.ModifyAsync("""
            dn: cn=llm-users,ou=groups,dc=example,dc=test
            changetype: modify
            add: member
            member: uid=frank,ou=people,dc=example,dc=test

            """);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync(sync));
        Assert.All(ldap.Gateway.KeysOf("frank@example.test"), k => Assert.False(k.Blocked));
        await (await Browser()).SignedInAsync("frank", "frank-directory-pw");
    }

    [Fact]
    public async Task Joining_the_admin_group_in_the_directory_makes_someone_an_admin()
    {
        await ldap.ModifyAsync("""
            dn: uid=gus,ou=people,dc=example,dc=test
            changetype: add
            objectClass: inetOrgPerson
            uid: gus
            cn: Gus Promoted
            sn: Promoted
            mail: gus@example.test
            userPassword: gus-directory-pw

            dn: cn=llm-users,ou=groups,dc=example,dc=test
            changetype: modify
            add: member
            member: uid=gus,ou=people,dc=example,dc=test

            """);
        var before = await (await Browser()).SignedInAsync("gus", "gus-directory-pw");
        Assert.False((await before.JsonAsync(await before.GetAsync("/api/auth/me"))).GetProperty("isAdmin").GetBoolean());
        await ldap.ModifyAsync("""
            dn: cn=llm-admins,ou=groups,dc=example,dc=test
            changetype: modify
            add: member
            member: uid=gus,ou=people,dc=example,dc=test

            """);
        var after = await (await Browser()).SignedInAsync("gus", "gus-directory-pw");
        Assert.True((await after.JsonAsync(await after.GetAsync("/api/auth/me"))).GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task An_unreachable_directory_is_reported_as_such()
    {
        await using var factory = app.Create(app.ConnectionStringFor("ldapdown_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            Settings("ldap://127.0.0.1:1"));
        var b = new TestBrowser(factory);
        var res = await b.LoginAsync("bob", "bob-directory-pw");
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, res);
        await new TestBrowser(factory).SignedInAsync("admin", AppFixture.AdminPassword); // local accounts still work
    }

    [Fact]
    public async Task StartTls_checks_the_certificate_unless_told_not_to()
    {
        // The test server's certificate is self-signed: refused by default, accepted only with the explicit test switch.
        await using (var strict = app.Create(app.ConnectionStringFor("tls1_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), Settings(ldap.Url, startTls: true)))
        {
            await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await new TestBrowser(strict).LoginAsync("bob", "bob-directory-pw"));
        }
        await using (var lax = app.Create(app.ConnectionStringFor("tls2_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), Settings(ldap.Url, startTls: true, ignoreCerts: true)))
        {
            await new TestBrowser(lax).SignedInAsync("bob", "bob-directory-pw");
        }
    }
}
