using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Company;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The fake identity provider and an app that trusts it, made by the first test that needs it.</summary>
public sealed class CompanyIdpFixture : IAsyncLifetime
{
    public FakeIdp Idp { get; } = new();
    public FakeGateway Gateway { get; } = new();
    public WebApplicationFactory<Program>? App { get; set; }
    public string? ScimToken { get; set; }
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }
    }

    public static Dictionary<string, string?> Settings() => new()
    {
        ["CompanySignIn:Issuer"] = FakeIdp.Issuer,
        ["CompanySignIn:ClientId"] = FakeIdp.ClientId,
        ["CompanySignIn:ClientSecret"] = FakeIdp.ClientSecret,
        ["CompanySignIn:AdminGroup"] = "llm-admins",
        ["CompanySignIn:RequiredGroup"] = "llm-users",
        ["CompanySignIn:ButtonLabel"] = "Example SSO",
    };

    /// <summary>An app whose calls to the identity provider reach this fake one.</summary>
    public static WebApplicationFactory<Program> WithIdp(WebApplicationFactory<Program> factory, FakeIdp idp) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddHttpClient(CompanyIdp.Client).ConfigurePrimaryHttpMessageHandler(() => idp)));

    public async Task<WebApplicationFactory<Program>> AppAsync(AppFixture app)
    {
        await Gate.WaitAsync();
        try
        {
            App ??= WithIdp(app.Create(app.ConnectionStringFor("company_" + Guid.NewGuid().ToString("N")[..8]), Gateway, Settings()), Idp);
            return App;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A browser that starts company sign-in, the person approving at the provider; the app's answer to the provider's redirect.</summary>
    public async Task<(TestBrowser Browser, HttpResponseMessage Answer)> SignInAsync(WebApplicationFactory<Program> f, FakeIdp.Person person, FakeIdp.Forgery? forgery = null, string rd = "/chat")
    {
        var b = new TestBrowser(f);
        var start = await b.GetAsync("/api/auth/company/start?rd=" + Uri.EscapeDataString(rd));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var answer = await b.GetAsync(Idp.Approve(start.Headers.Location!, person, forgery));
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return (b, answer);
    }
}

[Collection(nameof(AppCollection))]
public sealed class CompanySignInTests(AppFixture app, CompanyIdpFixture company) : IClassFixture<CompanyIdpFixture>
{
    private Task<WebApplicationFactory<Program>> AppAsync() => company.AppAsync(app);

    private static string Location(HttpResponseMessage res) => res.Headers.Location!.ToString();

    private static async Task<List<JsonElement>> AuditAsync(TestBrowser admin) =>
        [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray()];

    [Fact]
    public async Task Company_sign_in_creates_the_person_with_their_groups_their_role_and_a_key()
    {
        var f = await AppAsync();
        var (b, answer) = await company.SignInAsync(f, new("sub-ada", "Ada@Example.test", "Ada@Example.test", ["llm-users", "llm-admins", "research"], "Ada Lovelace"));
        Assert.Equal("/chat", Location(answer));

        var me = await b.JsonAsync(await b.GetAsync("/api/auth/me"));
        Assert.Equal("oidc", me.GetProperty("source").GetString());
        Assert.Equal("ada", me.GetProperty("userName").GetString()); // the part before the @
        Assert.Equal("ada@example.test", me.GetProperty("email").GetString());
        Assert.Equal("Ada Lovelace", me.GetProperty("displayName").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.False(me.GetProperty("twoFactorEnabled").GetBoolean());
        Assert.NotEmpty(company.Gateway.KeysOf("ada@example.test"));
        // Two-factor sign-in is the provider's, and so is the password.
        Assert.Equal("oidc", (await b.JsonAsync(await b.PostAsync("/api/account/2fa/setup"))).GetProperty("status").GetString());
        Assert.Equal("oidc", (await b.JsonAsync(await b.PostAsync("/api/account/password", new { current = "x", next = "violet tractor humming seaweed" }))).GetProperty("status").GetString());

        // The provider's groups are kept: a directory group in the app has them as members.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var made = await admin.PostAsync("/api/admin/groups", new { name = "Research", directory = "research" });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var group = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{(await admin.JsonAsync(made)).GetProperty("id").GetGuid()}"));
        Assert.Contains("ada", group.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userName").GetString()));

        // A second sign-in is the same person, found by the provider's subject.
        var (again, _) = await company.SignInAsync(f, new("sub-ada", "ada@example.test", "ada@example.test", ["llm-users"], "Ada King"));
        var second = await again.JsonAsync(await again.GetAsync("/api/auth/me"));
        Assert.Equal(me.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal("Ada King", second.GetProperty("displayName").GetString());
        Assert.False(second.GetProperty("isAdmin").GetBoolean()); // left the admin group
        var audit = await AuditAsync(admin);
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "sign_in" && e.GetProperty("target").GetString() == "ada"
            && e.GetProperty("detail").GetString() == "company sign-in");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "person.remove_admin" && e.GetProperty("target").GetString() == "ada");
    }

    [Fact]
    public async Task Someone_outside_the_required_group_is_refused_and_disabled_until_they_are_back()
    {
        var f = await AppAsync();
        var bea = new FakeIdp.Person("sub-bea", "bea", "bea@example.test", ["llm-users"]);
        var (session, _) = await company.SignInAsync(f, bea);
        await StatusAssert.Is(HttpStatusCode.OK, await session.GetAsync("/api/auth/me"));

        var (refused, answer) = await company.SignInAsync(f, bea with { Groups = ["contractors"] });
        Assert.Equal("/login?error=company_not_allowed&rd=%2Fchat", Location(answer));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await refused.GetAsync("/api/auth/me"));
        // The provider said no: their other sessions end and their keys stop.
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await session.GetAsync("/api/auth/me"));
        Assert.All(company.Gateway.KeysOf("bea@example.test"), k => Assert.True(k.Blocked));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/people"))).GetProperty("people").EnumerateArray().Single(p => p.GetProperty("userName").GetString() == "bea");
        Assert.True(row.GetProperty("disabled").GetBoolean());
        Assert.Equal("oidc", row.GetProperty("disabledReason").GetString());

        var (back, welcome) = await company.SignInAsync(f, bea);
        Assert.Equal("/chat", Location(welcome));
        await StatusAssert.Is(HttpStatusCode.OK, await back.GetAsync("/api/auth/me"));
        Assert.All(company.Gateway.KeysOf("bea@example.test"), k => Assert.False(k.Blocked));

        // Someone the app never saw is not made at all.
        var (_, stranger) = await company.SignInAsync(f, new("sub-stranger", "stranger", "stranger@example.test", []));
        Assert.StartsWith("/login?error=company_not_allowed", Location(stranger), StringComparison.Ordinal);
        Assert.DoesNotContain((await admin.JsonAsync(await admin.GetAsync("/api/admin/people"))).GetProperty("people").EnumerateArray(),
            p => p.GetProperty("userName").GetString() == "stranger");
    }

    [Theory]
    [InlineData("signature", "signature")]
    [InlineData("nonce", "nonce")]
    [InlineData("issuer", "another issuer")]
    [InlineData("audience", "another client")]
    [InlineData("expired", "out of date")]
    public async Task An_identity_token_that_is_forged_or_not_for_this_sign_in_is_refused(string flaw, string reason)
    {
        var f = await AppAsync();
        var forgery = flaw switch
        {
            "signature" => new FakeIdp.Forgery(OtherKey: true),
            "nonce" => new FakeIdp.Forgery(Nonce: "nonce-of-another-sign-in"),
            "issuer" => new FakeIdp.Forgery(Issuer: "https://evil.test"),
            "audience" => new FakeIdp.Forgery(Audience: "another-app"),
            _ => new FakeIdp.Forgery(Expired: true),
        };
        var (b, answer) = await company.SignInAsync(f, new("sub-forged-" + flaw, "forged-" + flaw, $"forged-{flaw}@example.test", ["llm-users", "llm-admins"]), forgery);
        Assert.Equal("/login?error=company_failed&rd=%2Fchat", Location(answer));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await b.GetAsync("/api/auth/me"));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains(await AuditAsync(admin), e => e.GetProperty("action").GetString() == "sign_in" && !e.GetProperty("success").GetBoolean()
            && e.GetProperty("detail").GetString()!.Contains(reason, StringComparison.Ordinal));
        Assert.Empty(company.Gateway.KeysOf($"forged-{flaw}@example.test"));
    }

    [Fact]
    public async Task An_answer_for_a_sign_in_this_browser_did_not_start_is_refused()
    {
        var f = await AppAsync();
        var victim = new TestBrowser(f);
        var start = await victim.GetAsync("/api/auth/company/start");
        // Someone else's code and state, delivered to a browser that has no sign-in waiting: refused.
        var callback = company.Idp.Approve(start.Headers.Location!, new("sub-mallory", "mallory", "mallory@example.test", ["llm-users"]));
        var stranger = new TestBrowser(f);
        Assert.Equal("/login?error=company_failed", Location(await stranger.GetAsync(callback)));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await stranger.GetAsync("/api/auth/me"));

        // A state that is not the one this browser's sign-in sent: refused too.
        var tampered = QueryHelpers.AddQueryString("/api/auth/company/callback", new Dictionary<string, string?> { ["code"] = "x", ["state"] = "not-the-state" });
        Assert.Equal("/login?error=company_failed", Location(await victim.GetAsync(tampered)));

        // The provider saying no (the person cancelled) is worded as such.
        var again = new TestBrowser(f);
        var second = QueryHelpers.ParseQuery((await again.GetAsync("/api/auth/company/start?rd=/usage")).Headers.Location!.Query);
        var denied = QueryHelpers.AddQueryString("/api/auth/company/callback", new Dictionary<string, string?> { ["error"] = "access_denied", ["state"] = second["state"].ToString() });
        Assert.Equal("/login?error=company_cancelled&rd=%2Fusage", Location(await again.GetAsync(denied)));
    }

    [Fact]
    public async Task Company_sign_in_matches_someone_already_here_by_email_but_never_takes_over_a_local_admin()
    {
        var f = await AppAsync();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var created = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "cleo", email = "cleo@example.test", displayName = "Cleo" }));
        var id = created.GetProperty("id").GetGuid();

        var (b, answer) = await company.SignInAsync(f, new("sub-cleo", "cleo", "CLEO@example.test", ["llm-users"], "Cleo Company"));
        Assert.Equal("/chat", Location(answer));
        var me = await b.JsonAsync(await b.GetAsync("/api/auth/me"));
        Assert.Equal(id, me.GetProperty("id").GetGuid());
        Assert.Equal("oidc", me.GetProperty("source").GetString());
        // From now on the provider is in charge: the old password no longer signs in.
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await new TestBrowser(f).LoginAsync("cleo", created.GetProperty("password").GetString()!));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { admin = true }));

        // The local admin's email at the provider: refused, and the admin stays a way in.
        var (_, takeover) = await company.SignInAsync(f, new("sub-boss", "boss", "admin@llm.test", ["llm-users", "llm-admins"]));
        Assert.StartsWith("/login?error=company_refused", Location(takeover), StringComparison.Ordinal);
        await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        // An email the provider has not verified matches nobody and makes nobody.
        var (_, unverified) = await company.SignInAsync(f, new("sub-dan", "dan", "cleo@example.test", ["llm-users"], EmailVerified: false));
        Assert.StartsWith("/login?error=company_refused", Location(unverified), StringComparison.Ordinal);
        Assert.Contains(await AuditAsync(admin), e => e.GetProperty("detail").GetString()?.Contains("not verified", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Groups_that_only_the_userinfo_endpoint_sends_count_as_GitLab_sends_them()
    {
        var f = await AppAsync();
        var (b, answer) = await company.SignInAsync(f, new("sub-gil", "gil", "gil@example.test", ["llm-users", "llm-admins"], GroupsInUserInfo: true));
        Assert.Equal("/chat", Location(answer));
        Assert.True((await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Company_sign_in_is_set_up_in_the_Settings_page_tested_first_and_offered_on_the_sign_in_page()
    {
        var idp = new FakeIdp();
        await using var f = CompanyIdpFixture.WithIdp(app.Create(app.ConnectionStringFor("companyset_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "company-settings-data-key" }), idp);
        var visitor = new TestBrowser(f);
        Assert.Equal(JsonValueKind.Null, (await visitor.JsonAsync(await visitor.GetAsync("/api/auth/company"))).GetProperty("label").ValueKind);
        Assert.Equal("/login?error=company_off", Location(await visitor.GetAsync("/api/auth/company/start")));

        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var wrong = await admin.JsonAsync(await admin.PostAsync("/api/admin/company-sign-in/test", new Dictionary<string, string> { ["CompanySignIn:Issuer"] = "https://login.idp.test" }));
        Assert.False(wrong.GetProperty("ok").GetBoolean());
        Assert.Contains("is for https://idp.test", wrong.GetProperty("message").GetString(), StringComparison.Ordinal);
        var tested = await admin.JsonAsync(await admin.PostAsync("/api/admin/company-sign-in/test", new Dictionary<string, string> { ["CompanySignIn:Issuer"] = FakeIdp.Issuer }));
        Assert.True(tested.GetProperty("ok").GetBoolean(), tested.GetProperty("message").GetString());

        var changes = new[]
        {
            new { key = "CompanySignIn:Issuer", value = FakeIdp.Issuer },
            new { key = "CompanySignIn:ClientId", value = FakeIdp.ClientId },
            new { key = "CompanySignIn:ClientSecret", value = FakeIdp.ClientSecret },
            new { key = "CompanySignIn:ButtonLabel", value = "Okta" },
        };
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes }));
        Assert.Equal("Okta", (await visitor.JsonAsync(await visitor.GetAsync("/api/auth/company"))).GetProperty("label").GetString());
        var status = await admin.JsonAsync(await admin.GetAsync("/api/admin/company-sign-in"));
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal($"https://{AppFixture.Domain}/api/auth/company/callback", status.GetProperty("redirectUri").GetString());
        Assert.Equal($"https://{AppFixture.Domain}/scim/v2", status.GetProperty("scim").GetProperty("url").GetString());

        // No restart: the next sign-in uses it. No required group here: anyone the provider lets through.
        var b = new TestBrowser(f);
        var start = await b.GetAsync("/api/auth/company/start");
        Assert.Equal("/", Location(await b.GetAsync(idp.Approve(start.Headers.Location!, new("sub-eve", "eve", "eve@example.test", [])))));
        Assert.Equal("oidc", (await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("source").GetString());
        // The secret is stored encrypted and never shown.
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/config"));
        var secret = view.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()).Single(s => s.GetProperty("key").GetString() == "CompanySignIn:ClientSecret");
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);
        Assert.True(secret.GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task An_unreachable_identity_provider_is_reported_as_such_and_local_accounts_still_work()
    {
        var idp = new FakeIdp { Down = true };
        await using var f = CompanyIdpFixture.WithIdp(app.Create(app.ConnectionStringFor("companydown_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            CompanyIdpFixture.Settings()), idp);
        var b = new TestBrowser(f);
        Assert.Equal("/login?error=company_unavailable&rd=%2Fusage", Location(await b.GetAsync("/api/auth/company/start?rd=/usage")));
        await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
    }
}
