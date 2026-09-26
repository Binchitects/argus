using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class OidcTests(AppFixture app)
{
    private const string WebUiCallback = "https://chat.llm.test/oauth/oidc/callback";

    private TestBrowser Browser() => new(app.Factory);

    private static string AuthorizeUrl(string state = "s1", string? prompt = null, string redirect = WebUiCallback) =>
        QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "open-webui",
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email groups",
            ["state"] = state,
            ["prompt"] = prompt,
        }.Where(kv => kv.Value is not null));

    /// <summary>Runs the authorization-code flow as Open WebUI would and returns the token response.</summary>
    private async Task<JsonElement> SignInToWebUiAsync(string user, string password)
    {
        var b = Browser();
        var start = await b.GetAsync(AuthorizeUrl());
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var login = start.Headers.Location!.ToString();
        Assert.StartsWith("/login?rd=", login, StringComparison.Ordinal);
        var rd = QueryHelpers.ParseQuery(login.Split('?', 2)[1])["rd"].ToString();

        var res = await b.PostAsync("/api/auth/login", new { userName = user, password, redirect = rd });
        var back = (await b.JsonAsync(res)).GetProperty("redirect").GetString()!;
        Assert.StartsWith("/connect/authorize?", back, StringComparison.Ordinal);

        var authorized = await b.GetAsync(back);
        Assert.Equal(HttpStatusCode.Redirect, authorized.StatusCode);
        var callback = authorized.Headers.Location!;
        Assert.StartsWith(WebUiCallback, callback.ToString(), StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("s1", query["state"].ToString());
        return await ExchangeAsync(query["code"].ToString(), AppFixture.OpenWebUiSecret, HttpStatusCode.OK);
    }

    private async Task<JsonElement> ExchangeAsync(string code, string secret, HttpStatusCode expected)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = WebUiCallback,
            }),
        };
        req.Headers.Authorization = TestBrowser.Basic("open-webui", secret);
        var res = await Browser().Http.SendAsync(req);
        await StatusAssert.Is(expected, res);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static JsonElement Payload(string jwt) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url(jwt.Split('.')[1]))).RootElement;

    private static byte[] Base64Url(string s) =>
        Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));

    private async Task<JsonElement> UserInfoAsync(string accessToken, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/connect/userinfo", UriKind.Relative));
        req.Headers.Authorization = new("Bearer", accessToken);
        var res = await Browser().Http.SendAsync(req);
        await StatusAssert.Is(expected, res);
        return expected == HttpStatusCode.OK ? JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement : default;
    }

    private async Task<(string Email, string Password, Guid Id)> PersonAsync(TestBrowser admin)
    {
        var name = "o" + Guid.NewGuid().ToString("N")[..10];
        var res = await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Olga " + name });
        var body = await admin.JsonAsync(res);
        return ($"{name}@example.test", body.GetProperty("password").GetString()!, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Discovery_names_the_public_issuer_and_endpoints()
    {
        var doc = await Browser().Http.GetFromJsonAsync<JsonElement>(new Uri("/.well-known/openid-configuration", UriKind.Relative));
        Assert.Equal("https://llm.test/", doc.GetProperty("issuer").GetString());
        Assert.Equal("https://llm.test/connect/authorize", doc.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("https://llm.test/connect/token", doc.GetProperty("token_endpoint").GetString());
        Assert.Contains("groups", doc.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()));
        var jwks = await Browser().Http.GetFromJsonAsync<JsonElement>(new Uri(doc.GetProperty("jwks_uri").GetString()!));
        Assert.Contains(jwks.GetProperty("keys").EnumerateArray(), k => k.GetProperty("kty").GetString() == "RSA");
    }

    [Fact]
    public async Task Open_webui_signs_in_an_admin_with_the_admin_group()
    {
        var tokens = await SignInToWebUiAsync("admin", AppFixture.AdminPassword);
        var id = Payload(tokens.GetProperty("id_token").GetString()!);
        Assert.Equal("admin", id.GetProperty("preferred_username").GetString());
        Assert.Equal("admin@llm.test", id.GetProperty("email").GetString());
        Assert.Contains("admins", id.GetProperty("groups").EnumerateArray().Select(g => g.GetString()));
        var info = await UserInfoAsync(tokens.GetProperty("access_token").GetString()!);
        Assert.Equal(["admins", "users"], info.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!).Order().ToArray());
    }

    [Fact]
    public async Task A_member_gets_only_the_users_group()
    {
        var admin = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var (email, password, _) = await PersonAsync(admin);
        var tokens = await SignInToWebUiAsync(email, password);
        var info = await UserInfoAsync(tokens.GetProperty("access_token").GetString()!);
        Assert.Equal(["users"], info.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!).ToArray());
        Assert.Equal(email, info.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_disabled_persons_tokens_stop_working()
    {
        var admin = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var (email, password, id) = await PersonAsync(admin);
        var tokens = await SignInToWebUiAsync(email, password);
        await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { disabled = true });
        await UserInfoAsync(tokens.GetProperty("access_token").GetString()!, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_client_secret_is_refused()
    {
        var b = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await b.GetAsync(AuthorizeUrl());
        var code = QueryHelpers.ParseQuery(res.Headers.Location!.Query)["code"].ToString();
        var error = await ExchangeAsync(code, "not-the-secret", HttpStatusCode.Unauthorized);
        Assert.Equal("invalid_client", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_code_works_once()
    {
        var b = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await b.GetAsync(AuthorizeUrl());
        var code = QueryHelpers.ParseQuery(res.Headers.Location!.Query)["code"].ToString();
        await ExchangeAsync(code, AppFixture.OpenWebUiSecret, HttpStatusCode.OK);
        var again = await ExchangeAsync(code, AppFixture.OpenWebUiSecret, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_grant", again.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unregistered_redirect_is_never_followed()
    {
        var b = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await b.GetAsync(AuthorizeUrl(redirect: "https://evil.example/callback"));
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("redirect_uri", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_none_while_signed_out_answers_login_required()
    {
        var res = await Browser().GetAsync(AuthorizeUrl(prompt: "none"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith(WebUiCallback, res.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("login_required", QueryHelpers.ParseQuery(res.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task Unregistered_clients_do_not_exist()
    {
        // Langfuse has no secret in the test configuration, so it must not be a client at all.
        var b = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await b.GetAsync(AuthorizeUrl().Replace("client_id=open-webui", "client_id=langfuse", StringComparison.Ordinal));
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
    }

    [Fact]
    public async Task A_retired_client_left_by_an_older_version_is_removed_at_start()
    {
        // Grafana signed in through the app until its dashboards moved into the app.
        using (var scope = app.Factory.Services.CreateScope())
        {
            var apps = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
            await apps.CreateAsync(new OpenIddict.Abstractions.OpenIddictApplicationDescriptor
            {
                ClientId = "grafana", ClientSecret = "old-grafana-secret", ClientType = OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Confidential,
                RedirectUris = { new Uri("https://grafana.llm.test/login/generic_oauth") },
            });
            await scope.ServiceProvider.GetRequiredService<Llm.Api.Oidc.OidcClients>().RunAsync();
            Assert.Null(await apps.FindByClientIdAsync("grafana"));
        }
        var b = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await b.GetAsync(AuthorizeUrl(redirect: "https://grafana.llm.test/login/generic_oauth").Replace("client_id=open-webui", "client_id=grafana", StringComparison.Ordinal));
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
    }

    internal static async Task<string> MachineTokenAsync(TestBrowser b, string clientId, string secret, string scope = "api", HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = scope }),
        };
        req.Headers.Authorization = TestBrowser.Basic(clientId, secret);
        var res = await b.Http.SendAsync(req);
        await StatusAssert.Is(expected, res);
        return expected == HttpStatusCode.OK ? (await b.JsonAsync(res)).GetProperty("access_token").GetString()! : "";
    }

    [Fact]
    public async Task Machine_clients_get_short_lived_api_tokens()
    {
        var b = Browser();
        var token = await MachineTokenAsync(b, "api", AppFixture.ApiSecret);
        var claims = Payload(token);
        Assert.Equal("api", claims.GetProperty("sub").GetString());
        Assert.Equal("llm-api", claims.GetProperty("aud").GetString());
        Assert.InRange(claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64(), 3000, 3600);
        await MachineTokenAsync(b, "api", "wrong", expected: HttpStatusCode.Unauthorized);
        await MachineTokenAsync(b, "open-webui", AppFixture.OpenWebUiSecret, expected: HttpStatusCode.BadRequest); // not allowed that grant
    }
}

[Collection(nameof(AppCollection))]
public sealed class ForwardAuthTests(AppFixture app)
{
    private TestBrowser Browser() => new(app.Factory);

    private static async Task<HttpResponseMessage> AskAsync(TestBrowser b, string host, string uri = "/", string accept = "text/html", string? bearer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/authz/forward-auth", UriKind.Relative));
        req.Headers.Add("X-Forwarded-Host", host);
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Uri", uri);
        req.Headers.Add("X-Forwarded-Method", "GET");
        req.Headers.Accept.ParseAdd(accept);
        if (bearer is not null)
        {
            req.Headers.Authorization = new("Bearer", bearer);
        }
        return await b.Http.SendAsync(req);
    }

    private async Task<TestBrowser> MemberAsync()
    {
        var admin = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "f" + Guid.NewGuid().ToString("N")[..10];
        var body = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await Browser().SignedInAsync(name, body.GetProperty("password").GetString()!);
    }

    [Fact]
    public async Task Anonymous_browsers_are_sent_to_sign_in_and_come_back()
    {
        var res = await AskAsync(Browser(), "metrics.llm.test", "/graph?x=1");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("https://llm.test/login?rd=" + Uri.EscapeDataString("https://metrics.llm.test/graph?x=1"), res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Anonymous_programs_get_401()
    {
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await AskAsync(Browser(), "metrics.llm.test", accept: "application/json"));
    }

    [Fact]
    public async Task Admins_reach_infrastructure_with_their_identity_attached()
    {
        var admin = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        foreach (var host in new[] { "metrics", "alerts", "logs", "cadvisor", "node", "gpu", "s3", "admin" })
        {
            var res = await AskAsync(admin, $"{host}.llm.test");
            await StatusAssert.Is(HttpStatusCode.OK, res);
            Assert.Equal("admin", res.Headers.GetValues("Remote-User").Single());
            Assert.Equal("admins,users", res.Headers.GetValues("Remote-Groups").Single());
            Assert.Equal("admin@llm.test", res.Headers.GetValues("Remote-Email").Single());
        }
    }

    [Fact]
    public async Task Members_reach_the_admin_panel_but_not_infrastructure()
    {
        var member = await MemberAsync();
        var panel = await AskAsync(member, "admin.llm.test");
        await StatusAssert.Is(HttpStatusCode.OK, panel);
        Assert.Equal("users", panel.Headers.GetValues("Remote-Groups").Single());
        foreach (var host in new[] { "metrics", "alerts", "logs", "s3" })
        {
            await StatusAssert.Is(HttpStatusCode.Forbidden, await AskAsync(member, $"{host}.llm.test"));
        }
    }

    [Theory]
    [InlineData("unknown.llm.test")]
    [InlineData("metrics.other.example")]
    [InlineData("llm.test.evil.example")]
    public async Task Hosts_without_a_rule_are_denied_even_to_admins(string host)
    {
        var admin = await Browser().SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await AskAsync(admin, host));
    }

    [Fact]
    public async Task The_engine_api_takes_a_machine_token_and_nothing_else_does()
    {
        var b = Browser();
        var token = await OidcTests.MachineTokenAsync(b, "api", AppFixture.ApiSecret);
        var ok = await AskAsync(b, "api.llm.test", "/v1/models", "application/json", token);
        await StatusAssert.Is(HttpStatusCode.OK, ok);
        Assert.Equal("api", ok.Headers.GetValues("Remote-User").Single());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await AskAsync(b, "metrics.llm.test", "/", "application/json", token));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await AskAsync(b, "api.llm.test", "/v1/models", "application/json", "not-a-token"));
    }

    [Fact]
    public async Task A_signed_in_browser_reaches_the_engine_docs()
    {
        var member = await MemberAsync();
        await StatusAssert.Is(HttpStatusCode.OK, await AskAsync(member, "api.llm.test", "/docs"));
    }
}
