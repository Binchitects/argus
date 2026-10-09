using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Llm.Tests;

/// <summary>
/// A real OpenLDAP with a small company in it, a read-only service account whose DN and password
/// are as awkward as real ones get, and a certificate from its own CA (for localhost and 127.0.0.1).
/// </summary>
public sealed class LdapServer : IAsyncLifetime
{
    public const string Base = "dc=example,dc=test";
    public const string AdminDn = "cn=admin," + Base;
    public const string AdminPassword = "ldap-admin-pw";

    /// <summary>A comma inside its CN, spaces in its OU: escaped as \2C, as the server writes it back.</summary>
    public const string ServiceDn = @"cn=Svc Reader\2C LDAP,ou=Service Accounts,dc=example,dc=test";

    /// <summary>Characters that break shells, .env files, JSON and LDIF: $, #, &amp;, quotes, a backslash, é.</summary>
    public const string ServicePassword = "S3rv!ce #pw $HOME &amp; \"q\" \\ é";

    /// <summary>More people than OpenLDAP shows one search (500), below ou=many.</summary>
    public const int Many = 510;

    /// <summary>Where the tests reach the container (an address of the Docker host when they run in a container themselves).</summary>
    private static readonly string Host = new ContainerBuilder("osixia/openldap:1.5.0").Build().Hostname;

    private static readonly (string Ca, string Certificate, string Key) Tls = MakeCertificates();

    /// <summary>The directory's CA, in PEM: what an admin pastes in "Directory's CA".</summary>
    public static string CaPem => Tls.Ca;

    private static (string Ca, string Certificate, string Key) MakeCertificates()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Example Test Directory CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        if (!IPAddress.TryParse(Host, out _))
        {
            names.AddDnsName(Host);
        }
        // Every address of this machine: Testcontainers may name the Docker host by one of them
        // (its bridge's gateway, when the tests run in a container on the host's network).
        foreach (var address in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Append(IPAddress.Loopback).Distinct())
        {
            names.AddIpAddress(address);
        }
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), RandomNumberGenerator.GetBytes(16));
        return (ca.ExportCertificatePem(), certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

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

    /// <summary>
    /// The service account; a person whose uid has a space, which no username here can have; a group of
    /// the kind the image's memberOf overlay keeps (groupOfUniqueNames, unlike the groupOfNames above); and many people.
    /// </summary>
    private static string Extra()
    {
        var sb = new StringBuilder($"""
            dn: ou=Service Accounts,dc=example,dc=test
            objectClass: organizationalUnit
            ou: Service Accounts

            dn: {ServiceDn}
            objectClass: inetOrgPerson
            cn: Svc Reader, LDAP
            sn: Reader
            userPassword:: {Convert.ToBase64String(Encoding.UTF8.GetBytes(ServicePassword))}

            dn: uid=gina space,ou=people,dc=example,dc=test
            objectClass: inetOrgPerson
            uid: gina space
            cn: Gina Space
            sn: Space
            mail: gina@example.test
            userPassword: gina-directory-pw

            dn: cn=llm-unique,ou=groups,dc=example,dc=test
            objectClass: groupOfUniqueNames
            cn: llm-unique
            uniqueMember: uid=alice,ou=people,dc=example,dc=test

            dn: ou=many,dc=example,dc=test
            objectClass: organizationalUnit
            ou: many


            """);
        for (var i = 0; i < Many; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"dn: uid=m{i:D4},ou=many,dc=example,dc=test\nobjectClass: inetOrgPerson\nuid: m{i:D4}\ncn: Many {i}\nsn: Many\n\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Service accounts may read the directory (the image's rules let only its admin), and anonymous sign-ins
    /// are refused, as in most companies. An anonymous look sees which service accounts exist and nothing
    /// else, as on many OpenLDAP servers: the test then tells a DN with no entry from a wrong password.
    /// </summary>
    private const string ReadAccess = """
        dn: olcDatabase={1}mdb,cn=config
        changetype: modify
        add: olcAccess
        olcAccess: {2}to dn.subtree="ou=Service Accounts,dc=example,dc=test" attrs=entry,objectClass by anonymous read by * break
        olcAccess: {3}to * by dn.subtree="ou=Service Accounts,dc=example,dc=test" read by * break

        dn: cn=config
        changetype: modify
        add: olcDisallows
        olcDisallows: bind_anon

        """;

    private const string Certificates = "/container/service/slapd/assets/certs/";

    private readonly IContainer _ldap = new ContainerBuilder("osixia/openldap:1.5.0")
        .WithEnvironment("LDAP_ORGANISATION", "Example")
        .WithEnvironment("LDAP_DOMAIN", "example.test")
        .WithEnvironment("LDAP_ADMIN_PASSWORD", AdminPassword)
        .WithEnvironment("LDAP_TLS_VERIFY_CLIENT", "never")
        .WithPortBinding(389, true)
        .WithPortBinding(636, true)
        .WithResourceMapping(Encoding.UTF8.GetBytes(Seed), "/tmp/seed.ldif")
        .WithResourceMapping(Encoding.UTF8.GetBytes(Extra()), "/tmp/extra.ldif")
        .WithResourceMapping(Encoding.UTF8.GetBytes(ReadAccess), "/tmp/access.ldif")
        // Its own CA's certificate in place of the one the image would make (which has expired).
        .WithResourceMapping(Encoding.UTF8.GetBytes(Tls.Certificate), Certificates + "ldap.crt")
        .WithResourceMapping(Encoding.UTF8.GetBytes(Tls.Key), Certificates + "ldap.key")
        .WithResourceMapping(Encoding.UTF8.GetBytes(Tls.Ca), Certificates + "ca.crt")
        // -ZZ: the image first runs a temporary server to configure itself and adds TLS
        // after; a StartTLS search only succeeds against the final one.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
            "ldapsearch", "-x", "-ZZ", "-H", "ldap://localhost", "-b", Base, "-D", AdminDn, "-w", AdminPassword))
        .Build();

    public string Url => $"ldap://{_ldap.Hostname}:{_ldap.GetMappedPublicPort(389)}";

    public string LdapsUrl => $"ldaps://{_ldap.Hostname}:{_ldap.GetMappedPublicPort(636)}";

    /// <summary>The app wired to this directory, created once by the first test that needs it.</summary>
    public WebApplicationFactory<Program>? App { get; set; }
    public FakeGateway Gateway { get; } = new();

    public async Task InitializeAsync()
    {
        await _ldap.StartAsync();
        await RunAsync("ldapadd", "-x", "-D", AdminDn, "-w", AdminPassword, "-f", "/tmp/seed.ldif");
        await RunAsync("ldapadd", "-x", "-D", AdminDn, "-w", AdminPassword, "-f", "/tmp/extra.ldif");
        await RunAsync("ldapmodify", "-Y", "EXTERNAL", "-H", "ldapi:///", "-f", "/tmp/access.ldif");
    }

    /// <summary>Applies an LDIF change (ldapmodify) as the directory admin.</summary>
    public async Task ModifyAsync(string ldif)
    {
        var path = $"/tmp/change-{Guid.NewGuid():N}.ldif";
        await _ldap.CopyAsync(Encoding.UTF8.GetBytes(ldif), path);
        await RunAsync("ldapmodify", "-x", "-D", AdminDn, "-w", AdminPassword, "-f", path);
    }

    /// <summary>Applies an LDIF change to the server's own configuration (cn=config), as root in the container.</summary>
    public async Task ConfigureAsync(string ldif)
    {
        var path = $"/tmp/config-{Guid.NewGuid():N}.ldif";
        await _ldap.CopyAsync(Encoding.UTF8.GetBytes(ldif), path);
        await RunAsync("ldapmodify", "-Y", "EXTERNAL", "-H", "ldapi:///", "-f", path);
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
        // The read-only service account, with its awkward DN and password, through every path.
        ["Ldap:BindDn"] = LdapServer.ServiceDn,
        ["Ldap:BindPassword"] = LdapServer.ServicePassword,
        ["Ldap:UserBaseDn"] = "ou=people," + LdapServer.Base,
        ["Ldap:GroupBaseDn"] = "ou=groups," + LdapServer.Base,
        ["Ldap:AdminGroup"] = "llm-admins",
        ["Ldap:RequiredGroup"] = "llm-users",
    };

    [Fact]
    public async Task The_directory_can_be_set_up_in_the_Settings_page_tested_first_and_used_at_once()
    {
        // An app started with no directory at all.
        await using var fresh = app.Create(app.ConnectionStringFor("ldapset_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "ldap-settings-data-key" });
        var admin = await new TestBrowser(fresh).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new TestBrowser(fresh).LoginAsync("alice", "alice-directory-pw")).StatusCode);

        var form = Settings(ldap.Url).Where(kv => kv.Key.StartsWith("Ldap:", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var wrong = new Dictionary<string, string?>(form) { ["Ldap:BindPassword"] = "not-the-password" };
        var refused = await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-test", wrong));
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("service account", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
        var tested = await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-test", form));
        Assert.True(tested.GetProperty("ok").GetBoolean(), tested.GetProperty("message").GetString());

        var changes = form.Select(kv => new { key = kv.Key, value = kv.Value }).ToArray();
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes }));
        // No restart: the next sign-in uses the directory.
        var alice = await new TestBrowser(fresh).SignedInAsync("alice", "alice-directory-pw");
        var me = await alice.JsonAsync(await alice.GetAsync("/api/auth/me"));
        Assert.Equal("ldap", me.GetProperty("source").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
    }

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
        var me = await b.JsonAsync(await b.GetAsync("/api/auth/me"));
        Assert.False(me.GetProperty("isAdmin").GetBoolean());

        // The directory's groups are kept, so a directory group in the app has them as members.
        var admin = await (await Browser()).SignedInAsync("admin", AppFixture.AdminPassword);
        var seen = await admin.JsonAsync(await admin.GetAsync("/api/admin/groups/directory"));
        Assert.Contains("llm-users", seen.EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        var made = await admin.PostAsync("/api/admin/groups", new { name = "Directory users " + Guid.NewGuid().ToString("N")[..6], directory = "llm-users" });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var group = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{(await admin.JsonAsync(made)).GetProperty("id").GetGuid()}"));
        Assert.Contains("bob", group.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userName").GetString()));
        var person = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{me.GetProperty("id").GetGuid()}"));
        Assert.Contains(group.GetProperty("name").GetString(), person.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()));
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
        // The test server's certificate comes from its own CA, which nothing here trusts: refused by
        // default, accepted with the directory's CA given, or with the explicit test switch.
        await using (var strict = app.Create(app.ConnectionStringFor("tls1_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), Settings(ldap.Url, startTls: true)))
        {
            await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await new TestBrowser(strict).LoginAsync("bob", "bob-directory-pw"));
        }
        await using (var ca = app.Create(app.ConnectionStringFor("tls3_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?>(Settings(ldap.Url, startTls: true)) { ["Ldap:CaCertificate"] = LdapServer.CaPem }))
        {
            await new TestBrowser(ca).SignedInAsync("bob", "bob-directory-pw");
        }
        await using (var lax = app.Create(app.ConnectionStringFor("tls2_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), Settings(ldap.Url, startTls: true, ignoreCerts: true)))
        {
            await new TestBrowser(lax).SignedInAsync("bob", "bob-directory-pw");
        }
    }

    /// <summary>An app with no directory and a key for secrets, as an admin sets one up in the Settings page.</summary>
    private (WebApplicationFactory<Program> App, string Db) Fresh(string dataKey = "ldap-settings-data-key", string? db = null, IDictionary<string, string?>? more = null)
    {
        db ??= app.ConnectionStringFor("ldapfresh_" + Guid.NewGuid().ToString("N")[..8]);
        var settings = new Dictionary<string, string?> { ["Auth:DataKey"] = dataKey };
        foreach (var (k, v) in more ?? new Dictionary<string, string?>())
        {
            settings[k] = v;
        }
        return (app.Create(db, new FakeGateway(), settings), db);
    }

    private Dictionary<string, string?> Form(string? url = null, params (string Key, string? Value)[] changes)
    {
        var form = Settings(url ?? ldap.Url);
        foreach (var (k, v) in changes)
        {
            form["Ldap:" + k] = v;
        }
        return form;
    }

    private static async Task<JsonElement> TestAsync(TestBrowser admin, Dictionary<string, string?> form) =>
        await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-test", form));

    private static async Task SaveAsync(TestBrowser admin, Dictionary<string, string?> form) =>
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = form.Select(kv => new { key = kv.Key, value = kv.Value }).ToArray() }));

    private static string Says(JsonElement result) =>
        result.GetProperty("message").GetString() + " | " + string.Join(" | ", result.GetProperty("steps").EnumerateArray().Select(s => $"{s.GetProperty("state").GetString()}: {s.GetProperty("text").GetString()}"));

    [Fact]
    public async Task The_test_uses_the_saved_password_when_the_field_is_blank_at_once_and_after_a_restart()
    {
        var (first, db) = Fresh();
        await using (first)
        {
            var admin = await new TestBrowser(first).SignedInAsync("admin", AppFixture.AdminPassword);
            // Typed and tested before saving.
            var typed = await TestAsync(admin, Form());
            Assert.True(typed.GetProperty("ok").GetBoolean(), Says(typed));
            Assert.Contains("with the password typed above", Says(typed), StringComparison.Ordinal);
            // What the server says of itself, read before signing in.
            Assert.Contains("It is OpenLDAP, holding \"dc=example,dc=test\"", Says(typed), StringComparison.Ordinal);
            Assert.Contains("This server offers StartTLS", Says(typed), StringComparison.Ordinal);

            // Saved: the field shows blank, and the test uses the saved (encrypted, then decrypted) one at once.
            await SaveAsync(admin, Form());
            var blank = Form(null, ("BindPassword", ""));
            var saved = await TestAsync(admin, blank);
            Assert.True(saved.GetProperty("ok").GetBoolean(), Says(saved));
            Assert.Contains("with the saved password", Says(saved), StringComparison.Ordinal);
            var refused = await TestAsync(admin, Form(null, ("BindPassword", "not-the-password")));
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Contains("with the password typed above: the password is wrong", Says(refused), StringComparison.Ordinal);

            // A wrong password saved, then the right one: each in effect at once, no restart.
            await SaveAsync(admin, new() { ["Ldap:BindPassword"] = "a-wrong-one" });
            Assert.False((await TestAsync(admin, blank)).GetProperty("ok").GetBoolean());
            await SaveAsync(admin, new() { ["Ldap:BindPassword"] = LdapServer.ServicePassword });
            Assert.True((await TestAsync(admin, blank)).GetProperty("ok").GetBoolean());

            // A password is tried and saved exactly as typed: a space at an end is part of it (before,
            // saving cut it off, so a test that passed before saving failed after). One pasted with a
            // stray space is said, not fixed silently.
            var spaced = await TestAsync(admin, Form(null, ("BindPassword", LdapServer.ServicePassword + " ")));
            Assert.False(spaced.GetProperty("ok").GetBoolean(), Says(spaced));
            Assert.Contains("starts or ends with a space", Says(spaced), StringComparison.Ordinal);
            await ldap.ModifyAsync($"""
                dn: {LdapServer.ServiceDn}
                changetype: modify
                replace: userPassword
                userPassword:: {Convert.ToBase64String(Encoding.UTF8.GetBytes(LdapServer.ServicePassword + " "))}

                """);
            try
            {
                var typedSpace = await TestAsync(admin, Form(null, ("BindPassword", LdapServer.ServicePassword + " ")));
                Assert.True(typedSpace.GetProperty("ok").GetBoolean(), Says(typedSpace));
                await SaveAsync(admin, new() { ["Ldap:BindPassword"] = LdapServer.ServicePassword + " " });
                var savedSpace = await TestAsync(admin, blank);
                Assert.True(savedSpace.GetProperty("ok").GetBoolean(), Says(savedSpace));
            }
            finally
            {
                await ldap.ModifyAsync($"""
                    dn: {LdapServer.ServiceDn}
                    changetype: modify
                    replace: userPassword
                    userPassword:: {Convert.ToBase64String(Encoding.UTF8.GetBytes(LdapServer.ServicePassword))}

                    """);
            }
            await SaveAsync(admin, new() { ["Ldap:BindPassword"] = LdapServer.ServicePassword });
        }
        // After a restart: the same database, read again at start.
        var (second, _) = Fresh(db: db);
        await using (second)
        {
            var admin = await new TestBrowser(second).SignedInAsync("admin", AppFixture.AdminPassword);
            var afterRestart = await TestAsync(admin, new());
            Assert.True(afterRestart.GetProperty("ok").GetBoolean(), Says(afterRestart));
            await new TestBrowser(second).SignedInAsync("bob", "bob-directory-pw");
        }
        // A lost APP_KEY, recovered as docs/authentication.md says (the key ring and the sign-in keys
        // removed, a new key): the saved password no longer reads, and the page and the test say so.
        await using (var conn = new Npgsql.NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("""delete from "DataProtectionKeys"; delete from settings where key like 'oidc.%'""", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var (rekeyed, _) = Fresh("another-data-key", db);
        await using (rekeyed)
        {
            var admin = await new TestBrowser(rekeyed).SignedInAsync("admin", AppFixture.AdminPassword);
            var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/config"));
            var row = view.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray())
                .Single(s => s.GetProperty("key").GetString() == "Ldap:BindPassword");
            Assert.Contains("APP_KEY", row.GetProperty("warning").GetString(), StringComparison.Ordinal);
            var unreadable = await TestAsync(admin, new());
            Assert.False(unreadable.GetProperty("ok").GetBoolean());
            Assert.Contains("no longer reads", unreadable.GetProperty("message").GetString(), StringComparison.Ordinal);
            // Sign-in says the directory cannot be used, rather than binding anonymously.
            await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await new TestBrowser(rekeyed).LoginAsync("bob", "bob-directory-pw"));
        }
    }

    [Fact]
    public async Task The_test_says_exactly_what_is_wrong()
    {
        var (f, _) = Fresh();
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<string> Fails(Dictionary<string, string?> form)
        {
            var r = await TestAsync(admin, form);
            Assert.False(r.GetProperty("ok").GetBoolean(), Says(r));
            return r.GetProperty("message").GetString()!;
        }

        // OpenLDAP refuses a DN with no entry as it refuses a wrong password; an anonymous look tells them apart where it may.
        var wrong = await Fails(Form(null, ("BindPassword", "wrong")));
        Assert.Contains("the password is wrong. The DN is right (an entry has it)", wrong, StringComparison.Ordinal);
        var misspelt = await Fails(Form(null, ("BindDn", @"cn=Svc Raeder\2C LDAP,ou=Service Accounts,dc=example,dc=test")));
        Assert.Contains("no entry has this DN. The part of it that exists is \"ou=Service Accounts,dc=example,dc=test\"", misspelt, StringComparison.Ordinal);
        Assert.DoesNotContain("password", misspelt, StringComparison.Ordinal);
        // Where an anonymous look sees nothing (here, the rest of the domain), the test says it cannot tell.
        var hidden = await Fails(Form(null, ("BindDn", @"cn=Svc Reader\2C LDAP,ou=Service Acounts,dc=example,dc=test")));
        Assert.Contains("the DN or the password is wrong. The server says the same for both", hidden, StringComparison.Ordinal);
        Assert.Contains("cannot tell which", hidden, StringComparison.Ordinal);
        Assert.Contains("this server holds \"dc=example,dc=test\"", await Fails(Form(null, ("BindDn", "cn=reader,dc=example,dc=com"))), StringComparison.Ordinal);
        Assert.Contains("works only with Active Directory", await Fails(Form(null, ("BindDn", "reader@example.test"))), StringComparison.Ordinal);
        Assert.Contains("works only with Active Directory", await Fails(Form(null, ("BindDn", "EXAMPLE\\reader"))), StringComparison.Ordinal);
        Assert.Contains("is not a valid DN", await Fails(Form(null, ("BindDn", "cn=Svc Reader, LDAP,ou=Service Accounts,dc=example,dc=test"))), StringComparison.Ordinal);
        Assert.Contains("ou=people,dc=example,dc=test", await Fails(Form(null, ("UserBaseDn", ""))), StringComparison.Ordinal);
        Assert.Contains("The part of it that exists is \"dc=example,dc=test\"", await Fails(Form(null, ("UserBaseDn", "ou=staff,dc=example,dc=test"))), StringComparison.Ordinal);
        Assert.Contains("dc=example,dc=test", await Fails(Form(null, ("UserBaseDn", "example.test"))), StringComparison.Ordinal);
        Assert.Contains("Which entries are people", await Fails(Form(null, ("UserFilter", "(uid=bob)"))), StringComparison.Ordinal);
        Assert.Contains("no group \"llm-userz\"", await Fails(Form(null, ("RequiredGroup", "llm-userz"))), StringComparison.Ordinal);
        // groupOfNames groups are not in the image's memberOf: without "Where groups are", nobody could sign in.
        Assert.Contains("Where groups are", await Fails(Form(null, ("GroupBaseDn", ""))), StringComparison.Ordinal);
        Assert.Contains("nothing answers at 127.0.0.1:9", await Fails(Form("ldap://127.0.0.1:9")), StringComparison.Ordinal);
        Assert.Contains("ldaps://", await Fails(Form(ldap.LdapsUrl.Replace("ldaps://", "ldap://", StringComparison.Ordinal))), StringComparison.Ordinal);
        // No service account: anonymous, never a saved password with no name (which servers refuse as
        // wrong credentials: "refused the service account" for settings that were right).
        await SaveAsync(admin, new() { ["Ldap:BindPassword"] = "left behind" });
        var anonymous = await Fails(Form(null, ("BindDn", ""), ("BindPassword", "")));
        Assert.Contains("does not let anonymous connections in", anonymous, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", anonymous, StringComparison.Ordinal);

        // groupOfUniqueNames is in memberOf: then "Where groups are" may stay empty.
        var unique = await TestAsync(admin, Form(null, ("GroupBaseDn", ""), ("AdminGroup", "llm-unique"), ("RequiredGroup", "")));
        Assert.True(unique.GetProperty("ok").GetBoolean(), Says(unique));
    }

    [Fact]
    public async Task A_directory_past_the_servers_search_limit_passes_the_test()
    {
        var (f, _) = Fresh();
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // More than OpenLDAP's 500 below the whole domain: before, the count failed with "Size Limit Exceeded".
        var r = await TestAsync(admin, Form(null, ("UserBaseDn", LdapServer.Base)));
        Assert.True(r.GetProperty("ok").GetBoolean(), Says(r));
        Assert.Contains("or more", r.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ldaps_is_checked_against_the_directorys_CA_and_a_refused_certificate_is_explained_not_a_500()
    {
        var (f, _) = Fresh(more: new Dictionary<string, string?> { ["Replicas:Enabled"] = "false" });
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var strict = await TestAsync(admin, Form(ldap.LdapsUrl));
        Assert.False(strict.GetProperty("ok").GetBoolean());
        Assert.Contains("certificate is not trusted", strict.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("Example Test Directory CA", strict.GetProperty("message").GetString(), StringComparison.Ordinal);
        var withCa = await TestAsync(admin, Form(ldap.LdapsUrl, ("CaCertificate", LdapServer.CaPem)));
        Assert.True(withCa.GetProperty("ok").GetBoolean(), Says(withCa));
        var garbage = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new[] { new { key = "Ldap:CaCertificate", value = "not a certificate" } } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, garbage);

        // Someone from the directory signs in, then the directory moves to ldaps:// with no CA given.
        await SaveAsync(admin, Form());
        await new TestBrowser(f).SignedInAsync("bob", "bob-directory-pw");
        await SaveAsync(admin, new() { ["Ldap:Url"] = ldap.LdapsUrl });
        // Sign-in: "cannot be reached", never a 500; the check: the reason in words.
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await new TestBrowser(f).LoginAsync("bob", "bob-directory-pw"));
        var sync = await admin.PostAsync("/api/admin/ldap/sync");
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, sync);
        Assert.Contains("certificate is not trusted", (await admin.JsonAsync(sync)).GetProperty("error").GetString(), StringComparison.Ordinal);
        // The background check ran with the new settings (a change wakes it): the app is still up.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(f.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.GetAsync("/api/auth/me"));
        // With its CA, it works again.
        await SaveAsync(admin, new() { ["Ldap:CaCertificate"] = LdapServer.CaPem });
        await new TestBrowser(f).SignedInAsync("bob", "bob-directory-pw");
    }

    [Fact]
    public async Task A_directory_that_demands_a_client_certificate_is_said_to()
    {
        // osixia/openldap's default (LDAP_TLS_VERIFY_CLIENT=demand): TLS ends at once for a client without a certificate.
        var (f, _) = Fresh();
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await ldap.ConfigureAsync(VerifyClient("demand"));
        try
        {
            foreach (var form in new[] { Form(null, ("StartTls", "true"), ("CaCertificate", LdapServer.CaPem)), Form(ldap.LdapsUrl, ("CaCertificate", LdapServer.CaPem)) })
            {
                var r = await TestAsync(admin, form);
                Assert.False(r.GetProperty("ok").GetBoolean());
                Assert.Contains("asked this app for a client certificate", r.GetProperty("message").GetString(), StringComparison.Ordinal);
                Assert.Contains("TLSVerifyClient", r.GetProperty("message").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            await ldap.ConfigureAsync(VerifyClient("never"));
        }
        Assert.True((await TestAsync(admin, Form(ldap.LdapsUrl, ("CaCertificate", LdapServer.CaPem)))).GetProperty("ok").GetBoolean());
    }

    private static string VerifyClient(string how) => $"""
        dn: cn=config
        changetype: modify
        replace: olcTLSVerifyClient
        olcTLSVerifyClient: {how}

        """;

    [Fact]
    public async Task A_persons_sign_in_can_be_tried_with_unsaved_settings_and_nothing_is_kept()
    {
        var b = await Browser();
        var admin = await b.SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<JsonElement> Try(string login, string password, Dictionary<string, string?>? settings = null) =>
            await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-try", new { settings = settings ?? new(), login, password }));

        var alice = await Try("alice", "alice-directory-pw");
        Assert.True(alice.GetProperty("ok").GetBoolean(), Says(alice));
        Assert.Contains("as an admin", alice.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True((await Try("EXAMPLE\\bob", "bob-directory-pw")).GetProperty("ok").GetBoolean());
        Assert.Contains("the password is wrong", (await Try("bob", "not-it")).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("is called \"zed\"", (await Try("zed", "whatever")).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("not in the required group", (await Try("dave", "dave-directory-pw")).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("no email", (await Try("carol", "carol-directory-pw")).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("a local account named erin", (await Try("erin", "erin-directory-pw")).GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("an empty one is never tried", (await Try("bob", "")).GetProperty("message").GetString(), StringComparison.Ordinal);
        // With unsaved settings: no required group, so dave would come in.
        Assert.True((await Try("dave", "dave-directory-pw", new() { ["Ldap:RequiredGroup"] = "" })).GetProperty("ok").GetBoolean());
        // The directory lets gina in, but her account could not be made: the try says so, as signing in would refuse her.
        var gina = await Try("gina space", "gina-directory-pw", new() { ["Ldap:RequiredGroup"] = "" });
        Assert.False(gina.GetProperty("ok").GetBoolean(), Says(gina));
        Assert.Contains("their account cannot be made", gina.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Audited as a try, with the outcome; the password is nowhere.
        var audit = await admin.GetAsync("/api/admin/audit?take=50");
        var text = await audit.Content.ReadAsStringAsync();
        Assert.Contains("settings.ldap_try", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice-directory-pw", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dave-directory-pw", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenLdaps_memberOf_counts_without_where_groups_are_and_people_sign_in_with_a_domain_in_front()
    {
        // memberOf is an operational attribute in OpenLDAP: sent only when asked for by name.
        var (f, _) = Fresh(more: Form(null, ("GroupBaseDn", ""), ("AdminGroup", "llm-unique"), ("RequiredGroup", "")));
        await using var _f = f;
        var alice = await new TestBrowser(f).SignedInAsync("EXAMPLE\\alice", "alice-directory-pw");
        var me = await alice.JsonAsync(await alice.GetAsync("/api/auth/me"));
        Assert.Equal("alice", me.GetProperty("userName").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await new TestBrowser(f).LoginAsync("EXAMPLE\\bob", "not-bobs"));
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        // The audit log has the directory's reason, under the name without its domain (one name for the
        // throttle and the lockout however the domain is typed); the person was told only "wrong username or password".
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "sign_in" && e.GetProperty("target").GetString() == "bob"
            && e.GetProperty("detail").GetString() == "directory refused: the password is wrong");
    }

    [Fact]
    public async Task A_wrong_where_groups_are_changes_nobody_and_says_so()
    {
        // Before, the sync took "Where groups are" missing for every person gone: one typo, saved, disabled them all.
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("ldapgroups_" + Guid.NewGuid().ToString("N")[..8]), gateway,
            new Dictionary<string, string?>(Settings(ldap.Url)) { ["Auth:DataKey"] = "ldap-settings-data-key" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var bob = await new TestBrowser(f).SignedInAsync("bob", "bob-directory-pw");

        // Saving wakes the background check at once, as well as the one run here.
        await SaveAsync(admin, new() { ["Ldap:GroupBaseDn"] = "ou=grups," + LdapServer.Base });
        var sync = await admin.PostAsync("/api/admin/ldap/sync");
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, sync);
        Assert.Contains("\"ou=grups,dc=example,dc=test\" (where groups are) does not exist", (await admin.JsonAsync(sync)).GetProperty("error").GetString(), StringComparison.Ordinal);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var people = await admin.JsonAsync(await admin.GetAsync("/api/admin/people"));
        Assert.False(people.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("userName").GetString() == "bob").GetProperty("disabled").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.OK, await bob.GetAsync("/api/auth/me"));
        Assert.All(gateway.KeysOf("bob@example.test"), k => Assert.False(k.Blocked));

        // Signing in says the directory cannot be used; the test and the try say why.
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, await new TestBrowser(f).LoginAsync("bob", "bob-directory-pw"));
        var test = await TestAsync(admin, new());
        Assert.Contains("\"ou=grups,dc=example,dc=test\" (where groups are) does not exist", test.GetProperty("message").GetString(), StringComparison.Ordinal);
        var tried = await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-try", new { settings = new Dictionary<string, string?>(), login = "bob", password = "bob-directory-pw" }));
        Assert.Contains("Their groups cannot be read", tried.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Fixed: the check runs again. Someone whose own entry is gone from the directory still leaves.
        await SaveAsync(admin, new() { ["Ldap:GroupBaseDn"] = "ou=groups," + LdapServer.Base });
        await ldap.ModifyAsync("""
            dn: uid=hank,ou=people,dc=example,dc=test
            changetype: add
            objectClass: inetOrgPerson
            uid: hank
            cn: Hank Gone
            sn: Gone
            mail: hank@example.test
            userPassword: hank-directory-pw

            dn: cn=llm-users,ou=groups,dc=example,dc=test
            changetype: modify
            add: member
            member: uid=hank,ou=people,dc=example,dc=test

            """);
        var hank = await new TestBrowser(f).SignedInAsync("hank", "hank-directory-pw");
        await ldap.ModifyAsync("""
            dn: cn=llm-users,ou=groups,dc=example,dc=test
            changetype: modify
            delete: member
            member: uid=hank,ou=people,dc=example,dc=test

            dn: uid=hank,ou=people,dc=example,dc=test
            changetype: delete

            """);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync("/api/admin/ldap/sync"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await hank.GetAsync("/api/auth/me"));
        var after = await admin.JsonAsync(await admin.GetAsync("/api/admin/people"));
        Assert.Equal("ldap", after.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("userName").GetString() == "hank").GetProperty("disabledReason").GetString());
        await StatusAssert.Is(HttpStatusCode.OK, await bob.GetAsync("/api/auth/me"));
    }

    [Fact]
    public async Task The_saved_password_goes_only_to_the_saved_server_and_account()
    {
        var (f, _) = Fresh();
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await SaveAsync(admin, Form());

        // Another server in the form, the password field blank: nothing connects there, and it says why.
        using var elsewhere = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        elsewhere.Start();
        var other = $"ldap://127.0.0.1:{((IPEndPoint)elsewhere.LocalEndpoint).Port}";
        var test = await TestAsync(admin, Form(other, ("BindPassword", "")));
        Assert.False(test.GetProperty("ok").GetBoolean());
        Assert.Contains("the saved one is sent only to the saved server, as the saved service account", test.GetProperty("message").GetString(), StringComparison.Ordinal);
        var tried = await admin.JsonAsync(await admin.PostAsync("/api/admin/config/ldap-try",
            new { settings = Form(other, ("BindPassword", "")), login = "bob", password = "bob-directory-pw" }));
        Assert.False(tried.GetProperty("ok").GetBoolean());
        Assert.False(elsewhere.Pending());
        // Another service account on the saved server: the same.
        var account = await TestAsync(admin, Form(null, ("BindDn", "cn=admin," + LdapServer.Base), ("BindPassword", "")));
        Assert.Contains("the saved one is sent only to the saved server", account.GetProperty("message").GetString(), StringComparison.Ordinal);
        // The saved server and account, written a little differently: the saved password.
        var same = await TestAsync(admin, Form(ldap.Url.ToUpperInvariant().Replace("LDAP://", "ldap://", StringComparison.Ordinal), ("BindDn", LdapServer.ServiceDn.ToUpperInvariant()), ("BindPassword", "")));
        Assert.True(same.GetProperty("ok").GetBoolean(), Says(same));
        Assert.Contains("with the saved password", Says(same), StringComparison.Ordinal);

        // Each check is audited with the server it went to.
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "settings.ldap_test" && e.GetProperty("target").GetString() == other
            && !e.GetProperty("success").GetBoolean());
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "settings.ldap_try" && e.GetProperty("target").GetString() == "bob"
            && e.GetProperty("detail").GetString()!.StartsWith(other + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public void Active_Directorys_reasons_and_escaped_names_read_in_plain_words()
    {
        static Novell.Directory.Ldap.LdapException Refused(string data) => new("Invalid Credentials", Novell.Directory.Ldap.LdapException.InvalidCredentials,
            $"80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data {data}, v4563\0");
        Assert.Equal("the name or the password is wrong", Llm.Api.Ldap.LdapErrors.AdReason(Refused("52e")));
        Assert.Contains("must be changed", Llm.Api.Ldap.LdapErrors.AdReason(Refused("773")), StringComparison.Ordinal);
        Assert.Equal("its password has expired", Llm.Api.Ldap.LdapErrors.AdReason(Refused("532")));
        Assert.Equal("the account is disabled", Llm.Api.Ldap.LdapErrors.AdReason(Refused("533")));
        Assert.Equal("the account has expired", Llm.Api.Ldap.LdapErrors.AdReason(Refused("701")));
        Assert.Equal("the account is locked out after too many wrong passwords", Llm.Api.Ldap.LdapErrors.PersonRefused(Refused("775")));
        Assert.Null(Llm.Api.Ldap.LdapErrors.AdReason(new("Invalid Credentials", Novell.Directory.Ldap.LdapException.InvalidCredentials, "")));
        Assert.Equal("Smith, Jane", Llm.Api.Ldap.LdapDirectory.CommonName(@"CN=Smith\, Jane,OU=Staff,DC=corp,DC=example,DC=com"));
        Assert.Equal("Svc Reader, LDAP", Llm.Api.Ldap.LdapDirectory.CommonName(LdapServer.ServiceDn));
        Assert.Equal("Zoë", Llm.Api.Ldap.LdapDirectory.CommonName(@"cn=Zo\C3\AB,ou=people,dc=example,dc=test"));
        Assert.Equal("llm-admins", Llm.Api.Ldap.LdapDirectory.CommonName("cn=llm-admins,ou=groups,dc=example,dc=test"));
    }

    [Fact]
    public void A_refused_certificate_says_what_mends_it()
    {
        // As osixia/openldap 1.5.0's own: a certificate in date from a CA that expired (on 2026-01-15). Pasting that CA mends nothing.
        var now = DateTimeOffset.UtcNow;
        using var oldKey = RSA.Create(2048);
        using var oldCa = Ca("Old Directory CA", oldKey, now.AddYears(-3), now.AddDays(-30));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=ldap.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("ldap.example.test");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        // Signed past its CA's own end, as that image's are: CertificateRequest.Create(issuer) would refuse to.
        using var leaf = request.Create(oldCa.SubjectName, X509SignatureGenerator.CreateForRSA(oldKey, RSASignaturePadding.Pkcs1), now.AddDays(-1), now.AddYears(1), RandomNumberGenerator.GetBytes(16));
        var expired = Llm.Api.Chat.Tools.ServerTls.Problems(Llm.Core.Chat.TlsCheck.OwnCa, [oldCa], leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, "ldap.example.test");
        Assert.Contains(expired, r => r.Contains("Old Directory CA, a CA in its chain, expired on", StringComparison.Ordinal));
        var fix = Llm.Api.Ldap.LdapErrors.CertificateFix(expired, "ldap.example.test");
        Assert.Contains("a certificate that is in date, from a CA that is in date", fix, StringComparison.Ordinal);
        Assert.DoesNotContain("Paste the CA", fix, StringComparison.Ordinal);
        // That image sends its CA along, so without it given the CA is also untrusted: one fix for both.
        fix = Llm.Api.Ldap.LdapErrors.CertificateFix([.. expired, "It was issued by Old Directory CA, a CA this server does not trust."], "ldap.example.test");
        Assert.StartsWith("Give the directory a certificate that is in date, from a CA that is in date, and paste that CA in \"Directory's CA\"", fix, StringComparison.Ordinal);
        Assert.DoesNotContain("Paste the CA", fix, StringComparison.Ordinal);

        // Another name than the one it is for: that name, not a CA.
        var otherName = Llm.Api.Chat.Tools.ServerTls.Problems(Llm.Core.Chat.TlsCheck.OwnCa, [oldCa], leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, "10.0.0.5");
        Assert.Contains("It is for ldap.example.test, not 10.0.0.5.", otherName);
        fix = Llm.Api.Ldap.LdapErrors.CertificateFix(["It is for ldap.example.test, not 10.0.0.5."], "10.0.0.5");
        Assert.StartsWith("Write \"Directory server\" with a name the certificate is for, or give the directory a certificate for 10.0.0.5.", fix, StringComparison.Ordinal);
        Assert.DoesNotContain("Paste the CA", fix, StringComparison.Ordinal);

        // Issued by a CA not given: that CA is what is missing. Each ends with the way round it, for tests only.
        using var otherKey = RSA.Create(2048);
        using var otherCa = Ca("Another CA", otherKey, now.AddDays(-1), now.AddYears(1));
        var untrusted = Llm.Api.Chat.Tools.ServerTls.Problems(Llm.Core.Chat.TlsCheck.OwnCa, [otherCa], leaf, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, "ldap.example.test");
        fix = Llm.Api.Ldap.LdapErrors.CertificateFix(untrusted, "ldap.example.test");
        Assert.Equal("Paste the CA that issued it in \"Directory's CA\". For a test server only, \"Accept any certificate\" turns the check off.", fix);

        static X509Certificate2 Ca(string name, RSA key, DateTimeOffset from, DateTimeOffset to)
        {
            var ca = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            ca.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            ca.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            return ca.CreateSelfSigned(from, to);
        }
    }
}
