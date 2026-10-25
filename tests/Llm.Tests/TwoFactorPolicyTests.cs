using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Two-factor sign-in required by the company (of admins or everyone) or by a group: set up at the next sign-in, before anything else.</summary>
[Collection(nameof(AppCollection))]
public sealed class TwoFactorPolicyTests(AppFixture app)
{
    /// <summary>
    /// Its own app and database, the session checked against the account on every request (so a claim that did not survive the
    /// check would show at once).
    /// </summary>
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("tfa_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Auth:SessionRecheck"] = "00:00:00" });

    private static async Task SetPolicyAsync(TestBrowser admin, string policy) =>
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Auth:TwoFactorRequired", value = policy } } }));

    private static async Task<(Guid Id, string Name, string Password)> PersonAsync(TestBrowser admin)
    {
        var name = "tf" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (made.GetProperty("id").GetGuid(), name, made.GetProperty("password").GetString()!);
    }

    private static async Task<bool> MustSetUpAsync(TestBrowser b) =>
        (await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("mustSetUpTwoFactor").GetBoolean();

    [Fact]
    [Trait("Control", "IAM-03")]
    public async Task Required_of_everyone_a_person_without_one_sets_it_up_first_reaches_nothing_else_and_cannot_turn_it_off()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, name, password) = await PersonAsync(admin);
        await SetPolicyAsync(admin, "everyone");
        // A session from before goes on as it was: the policy holds from each person's next sign-in.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.GetAsync("/api/admin/people"));

        var person = new TestBrowser(f);
        Assert.Equal("ok", (await person.JsonAsync(await person.LoginAsync(name, password))).GetProperty("status").GetString());
        Assert.True(await MustSetUpAsync(person));
        for (var i = 0; i < 2; i++)
        {
            var refused = await person.GetAsync("/api/chat/conversations");
            await StatusAssert.Is(HttpStatusCode.Forbidden, refused);
            Assert.Equal("enrol_2fa", (await person.JsonAsync(refused)).GetProperty("status").GetString());
        }
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/account/keys"));
        await StatusAssert.Is(HttpStatusCode.OK, await person.GetAsync("/api/info"));
        var status = await person.JsonAsync(await person.GetAsync("/api/account/2fa"));
        Assert.Equal("the company requires it of everyone", status.GetProperty("required").GetString());

        var key = (await person.JsonAsync(await person.PostAsync("/api/account/2fa/setup"))).GetProperty("sharedKey").GetString()!;
        await StatusAssert.Is(HttpStatusCode.OK, await person.PostAsync("/api/account/2fa/enable", new { code = Totp.Code(key) }));
        Assert.False(await MustSetUpAsync(person));
        await StatusAssert.Is(HttpStatusCode.OK, await person.GetAsync("/api/chat/conversations"));

        var off = await person.PostAsync("/api/account/2fa/disable", new { code = Totp.Code(key) });
        await StatusAssert.Is(HttpStatusCode.Conflict, off);
        Assert.Contains("the company requires it of everyone", (await person.JsonAsync(off)).GetProperty("error").GetString(), StringComparison.Ordinal);

        // From now on a code at every sign-in.
        var next = new TestBrowser(f);
        Assert.Equal("2fa", (await next.JsonAsync(await next.LoginAsync(name, password))).GetProperty("status").GetString());

        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "sign_in" && e.GetProperty("target").GetString() == name
            && e.GetProperty("detail").GetString()!.Contains("to set up two-factor sign-in: the company requires it of everyone", StringComparison.Ordinal));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "account.enable_2fa" && e.GetProperty("target").GetString() == name);
    }

    [Fact]
    [Trait("Control", "IAM-03")]
    public async Task Required_of_admins_or_of_a_groups_members_it_reaches_only_them_and_the_compliance_check_says_who_has_none()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var two = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "two-factor");
        Assert.Contains("It is not required", two.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var (id, name, password) = await PersonAsync(admin);
        await SetPolicyAsync(admin, "admins");
        var member = await new TestBrowser(f).SignedInAsync(name, password);
        Assert.False(await MustSetUpAsync(member));
        var otherAdmin = new TestBrowser(f);
        await otherAdmin.LoginAsync("admin", AppFixture.AdminPassword);
        Assert.True(await MustSetUpAsync(otherAdmin));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await otherAdmin.GetAsync("/api/admin/people"));

        two = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "two-factor");
        Assert.Equal("warn", two.GetProperty("state").GetString());
        Assert.Contains("no second factor: admin", two.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("Required of admins", two.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // A group that requires it: its members set it up at their next sign-in.
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Finance " + Guid.NewGuid().ToString("N")[..6] })));
        var groupId = group.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{groupId}/members", new { userIds = new[] { id } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{groupId}/policies", UriKind.Relative), new { requireTwoFactor = true }));
        var detail = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{groupId}"));
        Assert.True(detail.GetProperty("policies").GetProperty("requireTwoFactor").GetBoolean());
        var again = new TestBrowser(f);
        await again.LoginAsync(name, password);
        Assert.True(await MustSetUpAsync(again));
        Assert.Equal($"the group {group.GetProperty("name").GetString()} requires it",
            (await again.JsonAsync(await again.GetAsync("/api/account/2fa"))).GetProperty("required").GetString());
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "group.policies" && e.GetProperty("detail").GetString()!.Contains("two-factor required", StringComparison.Ordinal));
    }
}
