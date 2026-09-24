using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class IdentityTests(AppFixture app)
{
    private TestBrowser Browser() => new(app.Factory);

    private async Task<TestBrowser> Admin() => await Browser().SignedInAsync("admin", AppFixture.AdminPassword);

    private async Task<(Guid Id, string Password, string Key, string Email)> CreatePersonAsync(TestBrowser admin, bool isAdmin = false, decimal? budget = null)
    {
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var email = $"{name}@example.test";
        var res = await admin.PostAsync("/api/admin/people", new { userName = name, email, displayName = "Person " + name, admin = isAdmin, budget });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var body = await admin.JsonAsync(res);
        return (body.GetProperty("id").GetGuid(), body.GetProperty("password").GetString()!, body.GetProperty("apiKey").GetString()!, email);
    }

    private static async Task<string> NameOf(TestBrowser admin, Guid id) =>
        (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{id}"))).GetProperty("person").GetProperty("userName").GetString()!;

    [Fact]
    public async Task The_first_admin_comes_from_configuration()
    {
        var admin = await Admin();
        var me = await admin.JsonAsync(await admin.GetAsync("/api/auth/me"));
        Assert.Equal("admin", me.GetProperty("userName").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("admin@llm.test", me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Wrong_password_and_unknown_name_get_the_same_answer()
    {
        var wrong = await Browser().LoginAsync("admin", "not the password at all");
        var unknown = await Browser().LoginAsync("nobody-" + Guid.NewGuid(), "not the password at all");
        await StatusAssert.Is(HttpStatusCode.Unauthorized, wrong);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, unknown);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signed_out_there_is_no_me_and_no_admin_api()
    {
        var anon = Browser();
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await anon.GetAsync("/api/auth/me"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await anon.GetAsync("/api/admin/people"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await anon.GetAsync("/api/account/keys"));
    }

    [Fact]
    public async Task State_changes_without_the_csrf_header_are_refused()
    {
        var admin = await Admin();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/admin/people", UriKind.Relative))
        {
            Content = JsonContent.Create(new { userName = "csrf-victim", email = "csrf@example.test" }),
        };
        req.Headers.Remove("X-Requested-With");
        admin.Http.DefaultRequestHeaders.Remove("X-Requested-With");
        var res = await admin.Http.SendAsync(req);
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        Assert.Contains("csrf", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_admin_creates_a_person_who_signs_in_as_a_member()
    {
        var admin = await Admin();
        var (_, password, key, email) = await CreatePersonAsync(admin, budget: 12.5m);
        Assert.Equal(20, password.Length);
        Assert.StartsWith("sk-", key, StringComparison.Ordinal);
        Assert.Contains(app.Gateway.KeysOf(email), k => k.Secret == key);
        Assert.Equal(12.5m, app.Gateway.Budgets[email]);

        var person = await Browser().SignedInAsync(email, password); // by email works too
        var me = await person.JsonAsync(await person.GetAsync("/api/auth/me"));
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/admin/people"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.PostAsync("/api/admin/people", new { userName = "x1", email = "x1@example.test" }));
        var keys = await person.JsonAsync(await person.GetAsync("/api/account/keys"));
        Assert.Equal(1, keys.GetProperty("keys").GetArrayLength());
        Assert.Equal(12.5m, keys.GetProperty("budget").GetDecimal());
    }

    [Theory]
    [InlineData("Bad Name", "ok@example.test")]
    [InlineData("okname", "not-an-email")]
    [InlineData("admin", "someone-else@example.test")]
    public async Task Bad_or_duplicate_people_are_refused(string userName, string email)
    {
        var admin = await Admin();
        var res = await admin.PostAsync("/api/admin/people", new { userName, email });
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
    }

    [Fact]
    public async Task A_person_changes_their_own_password_only_with_the_current_one_and_a_strong_new_one()
    {
        var admin = await Admin();
        var (_, password, _, email) = await CreatePersonAsync(admin);
        var person = await Browser().SignedInAsync(email, password);

        var wrong = await person.PostAsync("/api/account/password", new { current = "not it", next = "violet tractor humming seaweed" });
        Assert.Equal("incorrect", (await person.JsonAsync(wrong)).GetProperty("status").GetString());
        var weak = await person.PostAsync("/api/account/password", new { current = password, next = "Password1!" });
        Assert.Equal("weak", (await person.JsonAsync(weak)).GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.NoContent, await person.PostAsync("/api/account/password", new { current = password, next = "violet tractor humming seaweed" }));
        await StatusAssert.Is(HttpStatusCode.OK, await person.GetAsync("/api/auth/me")); // this session survives

        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Browser().LoginAsync(email, password));
        await StatusAssert.Is(HttpStatusCode.OK, await Browser().LoginAsync(email, "violet tractor humming seaweed"));
    }

    [Fact]
    public async Task A_password_reset_ends_the_persons_sessions_and_only_the_new_password_works()
    {
        var admin = await Admin();
        var (id, password, _, email) = await CreatePersonAsync(admin);
        var person = await Browser().SignedInAsync(email, password);

        var res = await admin.PostAsync($"/api/admin/people/{id}/password");
        var fresh = (await admin.JsonAsync(res)).GetProperty("password").GetString()!;
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await person.GetAsync("/api/auth/me"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Browser().LoginAsync(email, password));
        await StatusAssert.Is(HttpStatusCode.OK, await Browser().LoginAsync(email, fresh));
    }

    [Fact]
    public async Task Disabling_signs_out_and_blocks_keys_and_enabling_undoes_it()
    {
        var admin = await Admin();
        var (id, password, _, email) = await CreatePersonAsync(admin);
        var person = await Browser().SignedInAsync(email, password);

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { disabled = true }));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await person.GetAsync("/api/auth/me"));
        Assert.All(app.Gateway.KeysOf(email), k => Assert.True(k.Blocked));
        var res = await Browser().LoginAsync(email, password);
        Assert.Equal("disabled", (await Browser().JsonAsync(res)).GetProperty("status").GetString());

        await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { disabled = false });
        Assert.All(app.Gateway.KeysOf(email), k => Assert.False(k.Blocked));
        await StatusAssert.Is(HttpStatusCode.OK, await Browser().LoginAsync(email, password));
    }

    [Fact]
    public async Task Admins_cannot_lock_themselves_out()
    {
        var admin = await Admin();
        var me = (await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        foreach (var change in new object[] { new { disabled = true }, new { admin = false } })
        {
            var res = await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{me}", UriKind.Relative), change);
            await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        }
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.DeleteAsync(new Uri($"/api/admin/people/{me}", UriKind.Relative)));
    }

    [Fact]
    public async Task Promotion_and_demotion_take_effect_on_the_persons_next_request()
    {
        var admin = await Admin();
        var (id, password, _, email) = await CreatePersonAsync(admin);
        var person = await Browser().SignedInAsync(email, password);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/admin/people"));

        await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { admin = true });
        // Promotion ends their sessions (so fresh claims reach every app); they sign in again.
        person = await Browser().SignedInAsync(email, password);
        await StatusAssert.Is(HttpStatusCode.OK, await person.GetAsync("/api/admin/people"));

        await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { admin = false });
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await person.GetAsync("/api/admin/people"));
    }

    [Fact]
    public async Task Rotating_a_key_revokes_the_old_one_first()
    {
        var admin = await Admin();
        var (id, password, oldKey, email) = await CreatePersonAsync(admin);
        var res = await admin.PostAsync($"/api/admin/people/{id}/key");
        var newKey = (await admin.JsonAsync(res)).GetProperty("apiKey").GetString()!;
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal([newKey], app.Gateway.KeysOf(email).Select(k => k.Secret));

        // A person may rotate their own key too.
        var person = await Browser().SignedInAsync(email, password);
        var own = (await person.JsonAsync(await person.PostAsync("/api/account/keys/rotate"))).GetProperty("apiKey").GetString()!;
        Assert.Equal([own], app.Gateway.KeysOf(email).Select(k => k.Secret));
    }

    [Fact]
    public async Task Credit_is_set_on_the_gateway_and_can_be_unlimited()
    {
        var admin = await Admin();
        var (id, _, _, email) = await CreatePersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/budget", UriKind.Relative), new { budget = 0 }));
        Assert.Equal(0m, app.Gateway.Budgets[email]);
        await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/budget", UriKind.Relative), new { budget = (decimal?)null });
        Assert.Null(app.Gateway.Budgets[email]);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/budget", UriKind.Relative), new { budget = -1 }));
    }

    [Fact]
    public async Task Deleting_a_person_removes_them_and_their_keys()
    {
        var admin = await Admin();
        var (id, password, _, email) = await CreatePersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative)));
        Assert.Empty(app.Gateway.KeysOf(email));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/admin/people/{id}"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await Browser().LoginAsync(email, password));
    }

    [Fact]
    public async Task A_person_is_created_even_when_the_gateway_is_down_and_is_told_so()
    {
        var admin = await Admin();
        app.Gateway.Down = true;
        try
        {
            var res = await admin.PostAsync("/api/admin/people", new { userName = "gw" + Guid.NewGuid().ToString("N")[..8], email = $"gw{Guid.NewGuid():N}@example.test" });
            await StatusAssert.Is(HttpStatusCode.Created, res);
            var body = await admin.JsonAsync(res);
            Assert.False(string.IsNullOrEmpty(body.GetProperty("password").GetString()));
            Assert.Equal(JsonValueKind.Null, body.GetProperty("apiKey").ValueKind);
            Assert.Contains("gateway", body.GetProperty("warning").GetString(), StringComparison.Ordinal);
            var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/people"));
            Assert.Contains("missing", list.GetProperty("warning").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            app.Gateway.Down = false;
        }
    }

    [Fact]
    public async Task With_the_gateway_down_a_persons_key_page_says_so_instead_of_failing()
    {
        var admin = await Admin();
        var (_, password, _, email) = await CreatePersonAsync(admin);
        var person = await Browser().SignedInAsync(email, password);
        app.Gateway.Down = true;
        try
        {
            foreach (var res in new[] { await person.GetAsync("/api/account/keys"), await person.PostAsync("/api/account/keys/rotate") })
            {
                await StatusAssert.Is(HttpStatusCode.BadGateway, res);
                Assert.Contains("gateway is not reachable", (await person.JsonAsync(res)).GetProperty("error").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            app.Gateway.Down = false;
        }
    }

    [Fact]
    public async Task Two_factor_sign_in_with_an_authenticator_and_with_a_recovery_code()
    {
        var admin = await Admin();
        var (_, password, _, email) = await CreatePersonAsync(admin);
        var person = await Browser().SignedInAsync(email, password);

        var setup = await person.JsonAsync(await person.PostAsync("/api/account/2fa/setup"));
        var key = setup.GetProperty("sharedKey").GetString()!;
        Assert.StartsWith("otpauth://totp/llm.test:", setup.GetProperty("uri").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/account/2fa/enable", new { code = "000000" }));
        var enabled = await person.JsonAsync(await person.PostAsync("/api/account/2fa/enable", new { code = Totp.Code(key) }));
        var recovery = enabled.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recovery.Count);

        var b = Browser();
        var step1 = await b.JsonAsync(await b.LoginAsync(email, password));
        Assert.Equal("2fa", step1.GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await b.GetAsync("/api/auth/me")); // not signed in yet
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await b.PostAsync("/api/auth/login/2fa", new { code = "123456" }));
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync("/api/auth/login/2fa", new { code = Totp.Code(key) }));
        await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync("/api/auth/me"));

        var c = Browser();
        await c.LoginAsync(email, password);
        await StatusAssert.Is(HttpStatusCode.OK, await c.PostAsync("/api/auth/login/2fa", new { code = recovery[0], recovery = true }));
        var d = Browser();
        await d.LoginAsync(email, password);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await d.PostAsync("/api/auth/login/2fa", new { code = recovery[0], recovery = true })); // used up

        // An admin can reset it for someone who lost their phone.
        var id = (await person.JsonAsync(await person.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/people/{id}/2fa/reset"));
        Assert.Equal("ok", (await Browser().JsonAsync(await Browser().LoginAsync(email, password))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Five_failures_ban_that_account_from_that_address_but_not_colleagues_behind_it()
    {
        var admin = await Admin();
        var (_, password, _, email) = await CreatePersonAsync(admin);
        var ip = "203.0.113.77";
        for (var i = 0; i < 5; i++)
        {
            await StatusAssert.Is(HttpStatusCode.Unauthorized, await new TestBrowser(app.Factory, ip).LoginAsync(email, "wrong " + i));
        }
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, await new TestBrowser(app.Factory, ip).LoginAsync(email, password));
        // A colleague behind the same NAT is not affected, nor is the person from elsewhere.
        await StatusAssert.Is(HttpStatusCode.OK, await new TestBrowser(app.Factory, ip).LoginAsync("admin", AppFixture.AdminPassword));
        await StatusAssert.Is(HttpStatusCode.OK, await new TestBrowser(app.Factory, "203.0.113.78").LoginAsync(email, password));
    }

    [Fact]
    public async Task Spraying_many_accounts_from_one_address_bans_the_address()
    {
        var ip = "198.51.100.9";
        for (var i = 0; i < 50; i++)
        {
            await new TestBrowser(app.Factory, ip).LoginAsync($"spray-{i}", "Winter2026!");
        }
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, await new TestBrowser(app.Factory, ip).LoginAsync("admin", AppFixture.AdminPassword));
        await StatusAssert.Is(HttpStatusCode.OK, await new TestBrowser(app.Factory, "198.51.100.10").LoginAsync("admin", AppFixture.AdminPassword));
    }

    [Fact]
    public async Task Ten_failures_lock_the_account_from_any_address()
    {
        var admin = await Admin();
        var (_, password, _, email) = await CreatePersonAsync(admin);
        for (var i = 0; i < 10; i++)
        {
            await Browser().LoginAsync(email, "wrong password " + i);
        }
        var res = await Browser().LoginAsync(email, password);
        Assert.Equal("locked", (await Browser().JsonAsync(res)).GetProperty("status").GetString());
        var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/people"));
        Assert.Contains(list.GetProperty("people").EnumerateArray(), p => p.GetProperty("email").GetString() == email && p.GetProperty("lockedOut").GetBoolean());
    }

    [Fact]
    public async Task Sign_in_returns_only_safe_redirects()
    {
        async Task<string> RedirectFor(string target)
        {
            var b = Browser();
            var res = await b.PostAsync("/api/auth/login", new { userName = "admin", password = AppFixture.AdminPassword, redirect = target });
            return (await b.JsonAsync(res)).GetProperty("redirect").GetString()!;
        }
        Assert.Equal("https://grafana.llm.test/d/x", await RedirectFor("https://grafana.llm.test/d/x"));
        Assert.Equal("/connect/authorize?x=1", await RedirectFor("/connect/authorize?x=1"));
        Assert.Equal("/", await RedirectFor("https://evil.example/"));
        Assert.Equal("/", await RedirectFor("//evil.example/"));
        Assert.Equal("/", await RedirectFor("https://llm.test.evil.example/"));
        Assert.Equal("/", await RedirectFor("http://grafana.llm.test/"));
    }

    [Fact]
    public async Task Everything_is_in_the_audit_log()
    {
        var admin = await Admin();
        var (id, _, _, _) = await CreatePersonAsync(admin);
        await admin.PostAsync($"/api/admin/people/{id}/key");
        await Browser().LoginAsync("admin", "wrong for the audit");
        var name = await NameOf(admin, id);
        var events = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=100"))).EnumerateArray().ToList();
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "person.create" && e.GetProperty("target").GetString() == name && e.GetProperty("actor").GetString() == "admin");
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "person.rotate_key" && e.GetProperty("target").GetString() == name);
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "sign_in" && !e.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task The_directory_for_argus_lists_people_without_passwords()
    {
        var admin = await Admin();
        var (id, _, _, email) = await CreatePersonAsync(admin);
        var name = await NameOf(admin, id);
        var text = await File.ReadAllTextAsync(app.DirectoryPath);
        Assert.Contains($"{name}:", text, StringComparison.Ordinal);
        Assert.Contains($"email: {email}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("password", text.Split('\n').Skip(1).Aggregate("", (a, l) => a + l), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$argon2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AQAAAA", text, StringComparison.Ordinal); // Identity's hash prefix
    }
}

[Collection(nameof(AppCollection))]
public sealed class SessionCapTests(AppFixture app)
{
    [Fact]
    public async Task Refreshing_a_session_does_not_restart_the_twelve_hour_cap()
    {
        var b = new TestBrowser(app.Factory);
        await b.SignedInAsync("admin", AppFixture.AdminPassword);
        var before = (await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("signedInAt").GetInt64();
        await Task.Delay(1500);
        await StatusAssert.Is(System.Net.HttpStatusCode.OK, await b.PostAsync("/api/account/2fa/setup")); // refreshes the session
        var after = await b.JsonAsync(await b.GetAsync("/api/auth/me"));
        Assert.Equal(before, after.GetProperty("signedInAt").GetInt64());
        Assert.True(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - before >= 1);
    }
}

[Collection(nameof(AppCollection))]
public sealed class ConcurrentSignInTests(AppFixture app)
{
    [Fact]
    public async Task Many_simultaneous_sign_ins_of_one_person_all_succeed()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => new TestBrowser(app.Factory).LoginAsync("admin", AppFixture.AdminPassword)));
        Assert.All(results, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task Parallel_wrong_passwords_still_reach_the_lockout()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "par" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var password = made.GetProperty("password").GetString()!;
        // Ten guesses at once, each from its own address: the count must not lose any of them.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => new TestBrowser(app.Factory).LoginAsync(name, "wrong guess " + i)));
        var res = await new TestBrowser(app.Factory).LoginAsync(name, password);
        Assert.Equal(System.Net.HttpStatusCode.Locked, res.StatusCode);
    }

    [Fact]
    public async Task Many_simultaneous_wrong_passwords_are_all_refused_cleanly()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new TestBrowser(app.Factory).LoginAsync("admin", "wrong in parallel " + i)));
        Assert.All(results, r => Assert.Equal(System.Net.HttpStatusCode.Unauthorized, r.StatusCode));
    }
}

[Collection(nameof(AppCollection))]
public sealed class FloodGuardTests(AppFixture app)
{
    [Fact]
    public async Task A_flood_of_sign_in_requests_gets_a_readable_429()
    {
        var ip = "192.0.2.200";
        HttpResponseMessage last = null!;
        for (var i = 0; i < 125; i++)
        {
            // Unknown names, each once: the per-account throttle never engages, only the flood guard.
            last = await new TestBrowser(app.Factory, ip).LoginAsync($"flood-{i}", "x");
        }
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Equal("60", last.Headers.RetryAfter?.ToString());
        Assert.Contains("Wait a minute", await last.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
