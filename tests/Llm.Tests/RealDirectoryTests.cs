using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit.Abstractions;

namespace Llm.Tests;

/// <summary>
/// The Settings page's directory flows against a real directory of your own (OpenLDAP, Active
/// Directory), run only when LDAP_TEST_URL names one. Passwords come from files in data/ (which git
/// ignores), so no shell ever sees them: <c>tools/dn test tests/Llm.Tests --filter RealDirectory -e LDAP_TEST_URL=ldaps://dc1.corp.example.com
/// -e LDAP_TEST_BIND_DN=reader@corp.example.com -e LDAP_TEST_BIND_PASSWORD_FILE=/repo/data/reader-pw
/// -e LDAP_TEST_USER_BASE_DN=DC=corp,DC=example,DC=com -e LDAP_TEST_USER=jsmith -e LDAP_TEST_USER_PASSWORD_FILE=/repo/data/jsmith-pw</c>.
/// Also LDAP_TEST_START_TLS, LDAP_TEST_CA_FILE, LDAP_TEST_IGNORE_CERTIFICATE, LDAP_TEST_GROUP_BASE_DN,
/// LDAP_TEST_ADMIN_GROUP, LDAP_TEST_REQUIRED_GROUP and LDAP_TEST_USER_FILTER.
/// </summary>
public sealed class RealDirectoryFactAttribute : FactAttribute
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("LDAP_TEST_URL") is { Length: > 0 } url ? url : null;

    public RealDirectoryFactAttribute()
    {
        if (Url is null)
        {
            Skip = "No directory to test: set LDAP_TEST_URL (and the others in RealDirectoryTests) to run it.";
        }
    }
}

[Collection(nameof(AppCollection))]
public sealed class RealDirectoryTests(AppFixture app, ITestOutputHelper output)
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>A secret from NAME_FILE (read as it is, without its last line break) or NAME.</summary>
    private static string? Secret(string name) =>
        Env(name + "_FILE") is { } path ? File.ReadAllText(path).TrimEnd('\r', '\n') : Env(name);

    private static Dictionary<string, string?> Form()
    {
        var form = new Dictionary<string, string?>
        {
            ["Ldap:Url"] = RealDirectoryFactAttribute.Url,
            ["Ldap:StartTls"] = Env("LDAP_TEST_START_TLS") ?? "false",
            ["Ldap:IgnoreCertificateErrors"] = Env("LDAP_TEST_IGNORE_CERTIFICATE") ?? "false",
            ["Ldap:CaCertificate"] = Env("LDAP_TEST_CA_FILE") is { } ca ? File.ReadAllText(ca) : "",
            ["Ldap:BindDn"] = Env("LDAP_TEST_BIND_DN") ?? "",
            ["Ldap:BindPassword"] = Secret("LDAP_TEST_BIND_PASSWORD") ?? "",
            ["Ldap:UserBaseDn"] = Env("LDAP_TEST_USER_BASE_DN") ?? "",
            ["Ldap:GroupBaseDn"] = Env("LDAP_TEST_GROUP_BASE_DN") ?? "",
            ["Ldap:AdminGroup"] = Env("LDAP_TEST_ADMIN_GROUP") ?? "",
            ["Ldap:RequiredGroup"] = Env("LDAP_TEST_REQUIRED_GROUP") ?? "",
        };
        if (Env("LDAP_TEST_USER_FILTER") is { } filter)
        {
            form["Ldap:UserFilter"] = filter;
        }
        return form;
    }

    private async Task<JsonElement> CheckAsync(TestBrowser admin, string path, object body, string what)
    {
        var res = await admin.PostAsync(path, body);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var r = await admin.JsonAsync(res);
        output.WriteLine($"{what}: {(r.GetProperty("ok").GetBoolean() ? "works" : "fails")}: {r.GetProperty("message").GetString()}");
        foreach (var step in r.GetProperty("steps").EnumerateArray())
        {
            output.WriteLine($"    [{step.GetProperty("state").GetString()}] {step.GetProperty("text").GetString()}");
        }
        return r;
    }

    [RealDirectoryFact]
    public async Task The_settings_page_flows_work_against_a_real_directory()
    {
        var db = app.ConnectionStringFor("realldap_" + Guid.NewGuid().ToString("N")[..8]);
        var settings = new Dictionary<string, string?> { ["Auth:DataKey"] = "real-directory-data-key" };
        var form = Form();
        var blank = new Dictionary<string, string?>(form) { ["Ldap:BindPassword"] = "" };
        var hasAccount = !string.IsNullOrEmpty(form["Ldap:BindDn"]);
        await using (var first = app.Create(db, new FakeGateway(), settings))
        {
            var admin = await new TestBrowser(first).SignedInAsync("admin", AppFixture.AdminPassword);
            // 1. Typed, tested before saving.
            var typed = await CheckAsync(admin, "/api/admin/config/ldap-test", form, "Test, typed");
            Assert.True(typed.GetProperty("ok").GetBoolean(), typed.GetProperty("message").GetString());
            if (hasAccount)
            {
                // A wrong password is refused, in words.
                var wrong = await CheckAsync(admin, "/api/admin/config/ldap-test", new Dictionary<string, string?>(form) { ["Ldap:BindPassword"] = "surely-not-the-password" }, "Test, a wrong password");
                Assert.False(wrong.GetProperty("ok").GetBoolean());
            }
            // 2. Saved; the field left blank: the saved password, at once.
            await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
                new { changes = form.Where(kv => !string.IsNullOrEmpty(kv.Value) || kv.Key != "Ldap:BindPassword").Select(kv => new { key = kv.Key, value = kv.Value }).ToArray() }));
            var saved = await CheckAsync(admin, "/api/admin/config/ldap-test", blank, "Test, saved");
            Assert.True(saved.GetProperty("ok").GetBoolean(), saved.GetProperty("message").GetString());
        }
        // 3. After a restart.
        await using var second = app.Create(db, new FakeGateway(), settings);
        var again = await new TestBrowser(second).SignedInAsync("admin", AppFixture.AdminPassword);
        var restarted = await CheckAsync(again, "/api/admin/config/ldap-test", new Dictionary<string, string?>(), "Test, after a restart");
        Assert.True(restarted.GetProperty("ok").GetBoolean(), restarted.GetProperty("message").GetString());

        // 4. A person: tried, then signed in for real; the two agree.
        if (Env("LDAP_TEST_USER") is { } user && Secret("LDAP_TEST_USER_PASSWORD") is { } password)
        {
            var tried = await CheckAsync(again, "/api/admin/config/ldap-try", new { settings = new Dictionary<string, string?>(), login = user, password }, $"Try {user}");
            var signIn = await new TestBrowser(second).LoginAsync(user, password);
            output.WriteLine($"Sign-in of {user}: {(int)signIn.StatusCode}");
            Assert.Equal(tried.GetProperty("ok").GetBoolean(), signIn.StatusCode == HttpStatusCode.OK);
            var wrong = await CheckAsync(again, "/api/admin/config/ldap-try", new { settings = new Dictionary<string, string?>(), login = user, password = password + "-not" }, $"Try {user}, a wrong password");
            Assert.False(wrong.GetProperty("ok").GetBoolean());
        }
    }
}
