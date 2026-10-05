using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using Llm.Api.Company;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The fake SAML identity provider and an app that trusts it by its metadata address, made by the first test that needs it.</summary>
public sealed class SamlIdpFixture : IAsyncLifetime
{
    public FakeSamlIdp Idp { get; } = new();
    public FakeGateway Gateway { get; } = new();
    public WebApplicationFactory<Program>? App { get; set; }
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
        ["CompanySignIn:Protocol"] = "saml",
        ["CompanySignIn:SamlMetadataUrl"] = FakeSamlIdp.MetadataUrl,
        ["CompanySignIn:AdminGroup"] = "llm-admins",
        ["CompanySignIn:RequiredGroup"] = "llm-users",
        ["CompanySignIn:ButtonLabel"] = "Example SAML",
    };

    /// <summary>An app whose calls to the identity provider (its metadata) reach this fake one.</summary>
    public static WebApplicationFactory<Program> WithIdp(WebApplicationFactory<Program> factory, FakeSamlIdp idp) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddHttpClient(CompanyIdp.Client).ConfigurePrimaryHttpMessageHandler(() => idp)));

    public async Task<WebApplicationFactory<Program>> AppAsync(AppFixture app)
    {
        await Gate.WaitAsync();
        try
        {
            App ??= WithIdp(app.Create(app.ConnectionStringFor("saml_" + Guid.NewGuid().ToString("N")[..8]), Gateway, Settings()), Idp);
            return App;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Posts the provider's answer as its page does: a form, without the X-Requested-With an app page sends.</summary>
    public static async Task<HttpResponseMessage> PostAsync(TestBrowser b, string samlResponse, string? relayState, string? cookie = null)
    {
        var form = new Dictionary<string, string> { ["SAMLResponse"] = samlResponse };
        if (relayState is not null)
        {
            form["RelayState"] = relayState;
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/company/saml/acs", UriKind.Relative)) { Content = new FormUrlEncodedContent(form) };
        if (cookie is not null)
        {
            req.Headers.Add("Cookie", cookie);
        }
        b.Http.DefaultRequestHeaders.Remove("X-Requested-With");
        try
        {
            return await b.Http.SendAsync(req);
        }
        finally
        {
            b.Http.DefaultRequestHeaders.Add("X-Requested-With", "test");
        }
    }

    /// <summary>A browser that starts company sign-in, the person signing in at the provider, and the app's answer to the posted Response.</summary>
    public async Task<(TestBrowser Browser, HttpResponseMessage Answer)> SignInAsync(WebApplicationFactory<Program> f, FakeSamlIdp.Person person, FakeSamlIdp.Forgery? forgery = null, string rd = "/chat")
    {
        var b = new TestBrowser(f);
        var start = await b.GetAsync("/api/auth/company/start?rd=" + Uri.EscapeDataString(rd));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var (response, relayState) = Idp.Approve(start.Headers.Location!, person, forgery);
        return (b, await PostAsync(b, response, relayState));
    }
}

[Collection(nameof(AppCollection))]
public sealed class SamlSignInTests(AppFixture app, SamlIdpFixture saml) : IClassFixture<SamlIdpFixture>
{
    private Task<WebApplicationFactory<Program>> AppAsync() => saml.AppAsync(app);

    private static string Location(HttpResponseMessage res) => res.Headers.Location!.ToString();

    private static async Task<List<JsonElement>> AuditAsync(TestBrowser admin) =>
        [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray()];

    [Fact]
    public async Task Saml_sign_in_creates_the_person_with_their_groups_their_role_and_a_key()
    {
        var f = await AppAsync();
        var b = new TestBrowser(f);
        var start = await b.GetAsync("/api/auth/company/start?rd=%2Fchat");
        // The request's ID and RelayState wait in a protected cookie that the provider's cross-site POST carries back.
        var cookie = start.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CompanyEndpoints.SamlFlowCookie + "=", StringComparison.Ordinal));
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        var (response, relayState) = saml.Idp.Approve(start.Headers.Location!,
            new("Ada@Example.test", "Ada@Example.test", ["llm-users", "llm-admins", "research"], "Ada Lovelace"));
        Assert.Equal("/chat", Location(await SamlIdpFixture.PostAsync(b, response, relayState)));

        var me = await b.JsonAsync(await b.GetAsync("/api/auth/me"));
        Assert.Equal("oidc", me.GetProperty("source").GetString()); // a company account, whichever the protocol
        Assert.Equal("ada", me.GetProperty("userName").GetString()); // the NameID's part before the @
        Assert.Equal("ada@example.test", me.GetProperty("email").GetString());
        Assert.Equal("Ada Lovelace", me.GetProperty("displayName").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.NotEmpty(saml.Gateway.KeysOf("ada@example.test"));

        // The provider's groups are kept: a directory group in the app has them as members.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var made = await admin.PostAsync("/api/admin/groups", new { name = "Research", directory = "research" });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var group = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{(await admin.JsonAsync(made)).GetProperty("id").GetGuid()}"));
        Assert.Contains("ada", group.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userName").GetString()));

        // A second sign-in is the same person, found by the NameID; a signature on the Response instead of the assertion does as well.
        var (again, answer) = await saml.SignInAsync(f, new("Ada@Example.test", "ada@example.test", ["llm-users"], "Ada King"), new(SignResponse: true));
        Assert.Equal("/chat", Location(answer));
        var second = await again.JsonAsync(await again.GetAsync("/api/auth/me"));
        Assert.Equal(me.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal("Ada King", second.GetProperty("displayName").GetString());
        Assert.False(second.GetProperty("isAdmin").GetBoolean()); // left the admin group
        var audit = await AuditAsync(admin);
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "sign_in" && e.GetProperty("target").GetString() == "ada"
            && e.GetProperty("detail").GetString() == "company sign-in (SAML)");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "person.remove_admin" && e.GetProperty("target").GetString() == "ada");
    }

    [Theory]
    [InlineData("unsigned", "not signed")]
    [InlineData("other-key", "does not match the identity provider's certificate")]
    [InlineData("other-key-response", "does not match the identity provider's certificate")]
    [InlineData("wrapped", "exactly one assertion")]
    [InlineData("wrapped-signature", "exactly one assertion")]
    [InlineData("wrapped-same-id", "exactly one assertion")]
    [InlineData("audience", "another service provider")]
    [InlineData("issuer", "not https://saml-idp.test/realms/staff")]
    [InlineData("recipient", "subject confirmation is for https://evil.test/acs")]
    [InlineData("expired", "out of date")]
    [InlineData("encrypted", "encrypted")]
    [InlineData("sha1", "signed with rsa-sha1")]
    [InlineData("denied", "answered AuthnFailed: The person cancelled")]
    public async Task A_forged_or_misdirected_answer_is_refused(string flaw, string reason)
    {
        var f = await AppAsync();
        // The attacker signs in as themselves and tries to come out as an admin.
        var mallory = new FakeSamlIdp.Person($"mallory-{flaw}@example.test", $"mallory-{flaw}@example.test", ["llm-users"]);
        var boss = new FakeSamlIdp.Person($"boss-{flaw}@example.test", $"boss-{flaw}@example.test", ["llm-users", "llm-admins"]);
        var forgery = flaw switch
        {
            "unsigned" => new FakeSamlIdp.Forgery(NoSignature: true),
            "other-key" => new FakeSamlIdp.Forgery(OtherKey: true),
            "other-key-response" => new FakeSamlIdp.Forgery(OtherKey: true, SignResponse: true),
            "wrapped" => new FakeSamlIdp.Forgery(Wrap: "wrapped", Forged: boss),
            "wrapped-signature" => new FakeSamlIdp.Forgery(Wrap: "signature", Forged: boss),
            "wrapped-same-id" => new FakeSamlIdp.Forgery(Wrap: "same-id", Forged: boss),
            "audience" => new FakeSamlIdp.Forgery(Audience: "https://another-app.test"),
            "issuer" => new FakeSamlIdp.Forgery(Issuer: "https://evil.test"),
            "recipient" => new FakeSamlIdp.Forgery(Recipient: "https://evil.test/acs"),
            "expired" => new FakeSamlIdp.Forgery(Expired: true),
            "sha1" => new FakeSamlIdp.Forgery(Sha1: true),
            "denied" => new FakeSamlIdp.Forgery(Denied: true),
            _ => new FakeSamlIdp.Forgery(Encrypted: true),
        };
        var (b, answer) = await saml.SignInAsync(f, flaw.StartsWith("wrapped", StringComparison.Ordinal) ? mallory : boss, forgery);
        Assert.Equal("/login?error=company_failed&rd=%2Fchat", Location(answer));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await b.GetAsync("/api/auth/me"));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains(await AuditAsync(admin), e => e.GetProperty("action").GetString() == "sign_in" && !e.GetProperty("success").GetBoolean()
            && e.GetProperty("detail").GetString()!.StartsWith("company sign-in (SAML): ", StringComparison.Ordinal)
            && e.GetProperty("detail").GetString()!.Contains(reason, StringComparison.Ordinal));
        Assert.Empty(saml.Gateway.KeysOf(boss.Email!));
        Assert.Empty(saml.Gateway.KeysOf(mallory.Email!));
    }

    [Fact]
    public async Task A_comment_slipped_into_a_signed_NameID_does_not_shorten_it()
    {
        var f = await AppAsync();
        // Signed as carl@example.test.evil.test; a reader of the first text node alone would see carl@example.test.
        var (b, answer) = await saml.SignInAsync(f, new("carl@example.test.evil.test", null, ["llm-users"]), new(Comment: "carl@example.test".Length));
        Assert.Equal("/chat", Location(answer));
        var me = await b.JsonAsync(await b.GetAsync("/api/auth/me"));
        Assert.Equal("carl@example.test.evil.test", me.GetProperty("email").GetString());
        Assert.Empty(saml.Gateway.KeysOf("carl@example.test"));
    }

    [Fact]
    public async Task A_replayed_assertion_signs_nobody_in()
    {
        var f = await AppAsync();
        var b = new TestBrowser(f);
        var start = await b.GetAsync("/api/auth/company/start");
        var cookie = start.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CompanyEndpoints.SamlFlowCookie + "=", StringComparison.Ordinal)).Split(';')[0];
        var (response, relayState) = saml.Idp.Approve(start.Headers.Location!, new("rhea@example.test", "rhea@example.test", ["llm-users"]));
        Assert.Equal("/", Location(await SamlIdpFixture.PostAsync(b, response, relayState)));
        await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync("/api/auth/me"));

        // Someone who captured the whole exchange, the browser's cookie included, posts it again: the assertion was used.
        var thief = new TestBrowser(f);
        Assert.Equal("/login?error=company_failed", Location(await SamlIdpFixture.PostAsync(thief, response, relayState, cookie)));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await thief.GetAsync("/api/auth/me"));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains(await AuditAsync(admin), e => e.GetProperty("action").GetString() == "sign_in" && !e.GetProperty("success").GetBoolean()
            && e.GetProperty("detail").GetString()!.Contains("a replay", StringComparison.Ordinal));
        // The same browser posting it back (the back button) has no sign-in waiting any more.
        Assert.Equal("/login?error=company_failed", Location(await SamlIdpFixture.PostAsync(b, response, relayState)));
    }

    [Fact]
    public async Task An_answer_to_no_request_of_ours_is_refused_unless_sign_in_at_the_provider_is_allowed()
    {
        var f = await AppAsync();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // Unsolicited, as from the provider's portal: refused by default.
        var stranger = new TestBrowser(f);
        var unsolicited = saml.Idp.Respond(new("una@example.test", "una@example.test", ["llm-users"]), inResponseTo: null);
        Assert.Equal("/login?error=company_failed", Location(await SamlIdpFixture.PostAsync(stranger, unsolicited, "/usage")));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await stranger.GetAsync("/api/auth/me"));
        Assert.Contains(await AuditAsync(admin), e => e.GetProperty("detail").GetString()?.Contains("answers no sign-in request of the app", StringComparison.Ordinal) == true);

        // Another browser's answer slipped into this one (login CSRF): it answers a request this browser never made.
        var victim = new TestBrowser(f);
        var start = await new TestBrowser(f).GetAsync("/api/auth/company/start");
        var (theirs, theirRelay) = saml.Idp.Approve(start.Headers.Location!, new("mal@example.test", "mal@example.test", ["llm-users"]));
        Assert.Equal("/login?error=company_failed", Location(await SamlIdpFixture.PostAsync(victim, theirs, theirRelay)));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await victim.GetAsync("/api/auth/me"));
        Assert.Contains(await AuditAsync(admin), e => e.GetProperty("detail").GetString()?.Contains("a sign-in request this browser did not make", StringComparison.Ordinal) == true);

        // With sign-in at the provider allowed, the portal's answer signs in, once, and RelayState says where to.
        var idp = new FakeSamlIdp();
        var settings = SamlIdpFixture.Settings();
        settings["CompanySignIn:SamlAllowIdpInitiated"] = "true";
        await using var portal = SamlIdpFixture.WithIdp(app.Create(app.ConnectionStringFor("samlportal_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings), idp);
        var una = new TestBrowser(portal);
        var answer = idp.Respond(new("una@example.test", "una@example.test", ["llm-users"]), inResponseTo: null);
        Assert.Equal("/usage", Location(await SamlIdpFixture.PostAsync(una, answer, "/usage")));
        Assert.Equal("una", (await una.JsonAsync(await una.GetAsync("/api/auth/me"))).GetProperty("userName").GetString());
        Assert.Equal("/login?error=company_failed&rd=%2Fusage", Location(await SamlIdpFixture.PostAsync(new TestBrowser(portal), answer, "/usage")));
        // A RelayState to another site is not followed.
        Assert.Equal("/", Location(await SamlIdpFixture.PostAsync(new TestBrowser(portal), idp.Respond(new("una@example.test", "una@example.test", ["llm-users"]), null), "https://evil.test/")));
    }

    [Fact]
    public async Task Someone_outside_the_required_group_is_refused_and_a_local_admin_is_never_taken_over()
    {
        var f = await AppAsync();
        var bea = new FakeSamlIdp.Person("bea@example.test", "bea@example.test", ["llm-users"]);
        var (session, _) = await saml.SignInAsync(f, bea);
        await StatusAssert.Is(HttpStatusCode.OK, await session.GetAsync("/api/auth/me"));

        var (refused, answer) = await saml.SignInAsync(f, bea with { Groups = ["contractors"] });
        Assert.Equal("/login?error=company_not_allowed&rd=%2Fchat", Location(answer));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await refused.GetAsync("/api/auth/me"));
        // The provider said no: their other sessions end and their keys stop, until they are back in the group.
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await session.GetAsync("/api/auth/me"));
        Assert.All(saml.Gateway.KeysOf("bea@example.test"), k => Assert.True(k.Blocked));
        var (back, welcome) = await saml.SignInAsync(f, bea);
        Assert.Equal("/chat", Location(welcome));
        await StatusAssert.Is(HttpStatusCode.OK, await back.GetAsync("/api/auth/me"));

        // The local admin's email at the provider: refused, and the admin stays a way in.
        var (_, takeover) = await saml.SignInAsync(f, new("boss@example.test", "admin@llm.test", ["llm-users", "llm-admins"]));
        Assert.StartsWith("/login?error=company_refused", Location(takeover), StringComparison.Ordinal);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains(await AuditAsync(admin), e => e.GetProperty("detail").GetString()?.StartsWith("company sign-in (SAML): admin is a local admin", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Saml_is_set_up_in_the_Settings_page_tested_first_and_offered_on_the_sign_in_page()
    {
        var idp = new FakeSamlIdp();
        await using var f = SamlIdpFixture.WithIdp(app.Create(app.ConnectionStringFor("samlset_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway()), idp);
        var visitor = new TestBrowser(f);
        Assert.Equal(JsonValueKind.Null, (await visitor.JsonAsync(await visitor.GetAsync("/api/auth/company"))).GetProperty("label").ValueKind);

        // This app's metadata, for the provider, is public from the start.
        var metadata = await visitor.GetAsync("/api/auth/company/saml/metadata");
        await StatusAssert.Is(HttpStatusCode.OK, metadata);
        var sp = new XmlDocument();
        sp.LoadXml(await metadata.Content.ReadAsStringAsync());
        Assert.Equal(FakeSamlIdp.SpEntityId, sp.DocumentElement!.GetAttribute("entityID"));
        var acs = (XmlElement)sp.GetElementsByTagName("AssertionConsumerService", "urn:oasis:names:tc:SAML:2.0:metadata")[0]!;
        Assert.Equal(FakeSamlIdp.Acs, acs.GetAttribute("Location"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", acs.GetAttribute("Binding"));
        Assert.Contains("urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress", sp.GetElementsByTagName("NameIDFormat", "urn:oasis:names:tc:SAML:2.0:metadata").OfType<XmlElement>().Select(e => e.InnerText));

        // Tested before saving: the pasted metadata, then values by hand that do not add up.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var tested = await admin.JsonAsync(await admin.PostAsync("/api/admin/company-sign-in/test", new Dictionary<string, string>
        {
            ["CompanySignIn:Protocol"] = "saml",
            ["CompanySignIn:SamlMetadata"] = idp.Metadata(),
        }));
        Assert.True(tested.GetProperty("ok").GetBoolean(), tested.GetProperty("message").GetString());
        Assert.StartsWith($"Found {FakeSamlIdp.EntityId}: people sign in at {FakeSamlIdp.SsoUrl}, and it signs with 1 certificate", tested.GetProperty("message").GetString(), StringComparison.Ordinal);
        var wrong = await admin.JsonAsync(await admin.PostAsync("/api/admin/company-sign-in/test", new Dictionary<string, string>
        {
            ["CompanySignIn:Protocol"] = "saml",
            ["CompanySignIn:SamlSsoUrl"] = FakeSamlIdp.SsoUrl,
            ["CompanySignIn:SamlIdpEntityId"] = FakeSamlIdp.EntityId,
            ["CompanySignIn:SamlCertificate"] = "not a certificate",
        }));
        Assert.False(wrong.GetProperty("ok").GetBoolean());
        Assert.Contains("is not a certificate", wrong.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The settings say which protocol they belong to, and the metadata gets a box of several lines and room.
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/config"));
        var all = view.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()).ToDictionary(s => s.GetProperty("key").GetString()!);
        Assert.Equal("CompanySignIn:Protocol=saml", all["CompanySignIn:SamlMetadata"].GetProperty("shownWhen").GetString());
        Assert.Equal(6, all["CompanySignIn:SamlMetadata"].GetProperty("lines").GetInt32());
        Assert.Equal("CompanySignIn:Protocol=oidc", all["CompanySignIn:Issuer"].GetProperty("shownWhen").GetString());
        Assert.Equal(JsonValueKind.Null, all["CompanySignIn:AdminGroup"].GetProperty("shownWhen").ValueKind);

        // Pasted metadata longer than an ordinary setting is taken.
        var changes = new[]
        {
            new { key = "CompanySignIn:Protocol", value = "saml" },
            new { key = "CompanySignIn:SamlMetadata", value = idp.Metadata().Replace("<md:NameIDFormat>", new string(' ', 5000) + "<md:NameIDFormat>", StringComparison.Ordinal) },
            new { key = "CompanySignIn:ButtonLabel", value = "Okta" },
        };
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes }));
        var offered = await visitor.JsonAsync(await visitor.GetAsync("/api/auth/company"));
        Assert.Equal("Okta", offered.GetProperty("label").GetString());
        Assert.Equal("saml", offered.GetProperty("protocol").GetString());
        var status = await admin.JsonAsync(await admin.GetAsync("/api/admin/company-sign-in"));
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("saml", status.GetProperty("protocol").GetString());
        Assert.Equal(FakeSamlIdp.EntityId, status.GetProperty("issuer").GetString());
        Assert.Equal(FakeSamlIdp.SpEntityId, status.GetProperty("saml").GetProperty("entityId").GetString());
        Assert.Equal(FakeSamlIdp.Acs, status.GetProperty("saml").GetProperty("acsUrl").GetString());
        Assert.Equal($"https://{AppFixture.Domain}/api/auth/company/saml/metadata", status.GetProperty("saml").GetProperty("metadataUrl").GetString());

        // No restart: the next sign-in uses it. No required group here: anyone the provider lets through.
        var b = new TestBrowser(f);
        var start = await b.GetAsync("/api/auth/company/start");
        var (response, relayState) = idp.Approve(start.Headers.Location!, new("eve@example.test", "eve@example.test", []));
        Assert.Equal("/", Location(await SamlIdpFixture.PostAsync(b, response, relayState)));
        Assert.Equal("eve", (await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("userName").GetString());

        // By hand instead (a PEM certificate, another entity ID for this app): wins over the metadata.
        var byHand = new[]
        {
            new { key = "CompanySignIn:SamlSsoUrl", value = FakeSamlIdp.SsoUrl },
            new { key = "CompanySignIn:SamlIdpEntityId", value = FakeSamlIdp.EntityId },
            new { key = "CompanySignIn:SamlCertificate", value = idp.CertificatePem },
            new { key = "CompanySignIn:SamlEntityId", value = "urn:arena:test" },
        };
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = byHand }));
        var hand = new TestBrowser(f);
        var handStart = await hand.GetAsync("/api/auth/company/start");
        var (handResponse, handRelay) = idp.Approve(handStart.Headers.Location!, new("fay@example.test", "fay@example.test", []), sp: "urn:arena:test");
        Assert.Equal("/", Location(await SamlIdpFixture.PostAsync(hand, handResponse, handRelay)));
        await StatusAssert.Is(HttpStatusCode.OK, await hand.GetAsync("/api/auth/me"));
    }

    [Fact]
    public async Task An_unreachable_identity_provider_is_reported_as_such_and_local_accounts_still_work()
    {
        var idp = new FakeSamlIdp { Down = true };
        await using var f = SamlIdpFixture.WithIdp(app.Create(app.ConnectionStringFor("samldown_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), SamlIdpFixture.Settings()), idp);
        Assert.Equal("/login?error=company_unavailable&rd=%2Fusage", Location(await new TestBrowser(f).GetAsync("/api/auth/company/start?rd=/usage")));
        await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
    }

    [Fact]
    public void Metadata_gives_the_providers_entity_ID_its_redirect_sign_in_address_and_its_signing_certificates()
    {
        var idp = new FakeSamlIdp();
        var m = SamlIdp.ParseMetadata(idp.Metadata());
        Assert.Equal(FakeSamlIdp.EntityId, m.EntityId);
        Assert.Equal(FakeSamlIdp.SsoUrl, m.SsoUrl); // the HTTP-Redirect one, not the POST one
        Assert.Equal(idp.CertificateBase64, Convert.ToBase64String(Assert.Single(m.Certificates).RawData)); // signing only, not the encryption key
        Assert.False(m.WantsSignedRequests);
        Assert.Null(SamlIdp.ParseMetadata(idp.Metadata(sso: null)).SsoUrl);

        // Several providers in one file (a federation's): the entity ID picks one.
        var other = idp.Metadata().Replace(FakeSamlIdp.EntityId, "https://other-idp.test", StringComparison.Ordinal);
        var federation = $"""<md:EntitiesDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata">{idp.Metadata()}{other}</md:EntitiesDescriptor>""";
        Assert.Contains("2 identity providers", Assert.Throws<CompanyIdpException>(() => SamlIdp.ParseMetadata(federation)).Message, StringComparison.Ordinal);
        Assert.Equal("https://other-idp.test", SamlIdp.ParseMetadata(federation, "https://other-idp.test").EntityId);

        // A service provider's metadata, a DTD and plain junk are refused.
        Assert.Contains("no SAML 2.0 identity provider", Assert.Throws<CompanyIdpException>(() =>
            SamlIdp.ParseMetadata(SamlIdp.Metadata("https://sp.test", "https://sp.test/acs"))).Message, StringComparison.Ordinal);
        Assert.Contains("not XML", Assert.Throws<CompanyIdpException>(() =>
            SamlIdp.ParseMetadata("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>")).Message, StringComparison.Ordinal);
        Assert.Contains("not SAML metadata", Assert.Throws<CompanyIdpException>(() => SamlIdp.ParseMetadata("<html/>")).Message, StringComparison.Ordinal);

        // A certificate by hand: PEM, or its bare base64.
        Assert.Equal(idp.CertificateBase64, Convert.ToBase64String(Assert.Single(SamlIdp.Certificates(idp.CertificatePem)).RawData));
        Assert.Single(SamlIdp.Certificates(idp.CertificateBase64));
    }
}
