using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The company's policies (Admin → Compliance → Policies): templates, versions, approval, review and acknowledgement.</summary>
[Collection(nameof(AppCollection))]
public sealed class PolicyTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("pol_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static Task<HttpResponseMessage> Put(TestBrowser b, string path, object body) => b.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private static async Task<string> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "policies") is var c ? $"{c.GetProperty("state").GetString()}: {c.GetProperty("detail").GetString()}" : "";

    [Fact]
    [Trait("Control", "GOV-06")]
    public async Task A_policy_from_a_template_is_in_force_once_approved_and_each_reader_acknowledges_its_version()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.StartsWith("warn: No policy is approved yet", await CheckAsync(admin), StringComparison.Ordinal);
        var templates = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/policies/templates"));
        Assert.Contains(templates.EnumerateArray(), t => t.GetProperty("key").GetString() == "acceptable-use" && t.GetProperty("title").GetString() == "Acceptable use policy");
        Assert.Contains(templates.EnumerateArray(), t => t.GetProperty("key").GetString() == "isms");

        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/policies", new { template = "acceptable-use" }));
        var id = made.GetProperty("id").GetGuid();
        Assert.Equal("acceptable-use", made.GetProperty("key").GetString());
        Assert.Equal(1, made.GetProperty("draft").GetInt32());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/policies", new { template = "acceptable-use" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/policies", new { template = "nope" }));
        var text = await admin.JsonAsync(await admin.GetAsync($"/api/admin/compliance/policies/{id}/versions/1"));
        Assert.StartsWith("# Acceptable use policy", text.GetProperty("text").GetString(), StringComparison.Ordinal);

        // A person who must read it: told when it is in force, reads it and says so.
        var p = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "polreader", email = "polreader@example.test" }));
        var reader = await new TestBrowser(f).SignedInAsync("polreader", p.GetProperty("password").GetString()!);
        Assert.Equal(0, (await reader.JsonAsync(await reader.GetAsync("/api/account/policies"))).GetArrayLength());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/compliance/policies/{id}/approve"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/compliance/policies/{id}/approve"));
        var toRead = await reader.JsonAsync(await reader.GetAsync("/api/account/policies"));
        var version = toRead[0].GetProperty("versionId").GetGuid();
        Assert.Equal(JsonValueKindNull(), toRead[0].GetProperty("acknowledgedAt").ValueKind);
        var bell = await reader.JsonAsync(await reader.GetAsync("/api/notifications"));
        Assert.Contains("A policy to read: Acceptable use policy", bell.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("# Acceptable use policy", (await reader.JsonAsync(await reader.GetAsync($"/api/account/policies/{version}"))).GetProperty("text").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.OK, await reader.PostAsync($"/api/account/policies/{version}/acknowledge"));
        Assert.NotEqual(JsonValueKindNull(), (await reader.JsonAsync(await reader.GetAsync("/api/account/policies")))[0].GetProperty("acknowledgedAt").ValueKind);

        var one = await admin.JsonAsync(await admin.GetAsync($"/api/admin/compliance/policies/{id}"));
        Assert.Equal(1, one.GetProperty("policy").GetProperty("version").GetInt32());
        // Everyone must read it: the admin (who approved it, and has not said so) and the reader.
        Assert.Equal(1, one.GetProperty("policy").GetProperty("acknowledged").GetInt32());
        Assert.Equal(["admin"], one.GetProperty("pending").EnumerateArray().Select(e => e.GetString()));
        Assert.StartsWith("ok: 1 policy in force; read: 1 of 2", await CheckAsync(admin), StringComparison.Ordinal);

        // Only the admins must read it: the reader no longer sees it.
        await StatusAssert.Is(HttpStatusCode.OK, await Put(admin, $"/api/admin/compliance/policies/{id}", new { title = "Acceptable use policy", owner = "ciso", audience = "Admins", reviewMonths = 12 }));
        Assert.Equal(0, (await reader.JsonAsync(await reader.GetAsync("/api/account/policies"))).GetArrayLength());
        await StatusAssert.Is(HttpStatusCode.NotFound, await reader.GetAsync($"/api/account/policies/{version}"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await Put(admin, $"/api/admin/compliance/policies/{id}", new { title = "Acceptable use policy", audience = "Groups", reviewMonths = 12 }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await Put(admin, $"/api/admin/compliance/policies/{id}", new { title = "Acceptable use policy", audience = "Everyone", reviewMonths = 60 }));
    }

    [Fact]
    [Trait("Control", "GOV-06")]
    public async Task A_new_version_says_what_changed_is_read_again_and_a_review_keeps_it_in_force()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var id = (await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/policies", new { title = "Remote work" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.OK, await Put(admin, $"/api/admin/compliance/policies/{id}/draft", new { text = "# Remote work\n\nFrom home, on the VPN." }));
        await admin.PostAsync($"/api/admin/compliance/policies/{id}/approve");
        var v1 = (await admin.JsonAsync(await admin.GetAsync("/api/account/policies"))).EnumerateArray().Single().GetProperty("versionId").GetGuid();
        await admin.PostAsync($"/api/account/policies/{v1}/acknowledge");

        await StatusAssert.Is(HttpStatusCode.OK, await Put(admin, $"/api/admin/compliance/policies/{id}/draft", new { text = "# Remote work\n\nFrom home or abroad, on the VPN." }));
        var refused = await admin.PostAsync($"/api/admin/compliance/policies/{id}/approve");
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("Say what changed", (await admin.JsonAsync(refused)).GetProperty("error").GetString(), StringComparison.Ordinal);
        await Put(admin, $"/api/admin/compliance/policies/{id}/draft", new { text = "# Remote work\n\nFrom home or abroad, on the VPN.", changes = "Abroad too." });
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/compliance/policies/{id}/approve"));
        // Version 2 is to be read again; version 1 stays as it was.
        var mine = (await admin.JsonAsync(await admin.GetAsync("/api/account/policies"))).EnumerateArray().Single();
        Assert.Equal(2, mine.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKindNull(), mine.GetProperty("acknowledgedAt").ValueKind);
        Assert.Equal("# Remote work\n\nFrom home, on the VPN.", (await admin.JsonAsync(await admin.GetAsync($"/api/admin/compliance/policies/{id}/versions/1"))).GetProperty("text").GetString());

        var reviewed = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/policies/{id}/review"));
        Assert.Equal("admin", reviewed.GetProperty("reviewedBy").GetString());
        Assert.InRange((reviewed.GetProperty("reviewDue").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 360, 367);
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=30"));
        foreach (var what in new[] { "compliance.policy.create", "compliance.policy.draft", "compliance.policy.approve", "compliance.policy.review", "compliance.policy.acknowledge" })
        {
            Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == what);
        }
    }

    private static System.Text.Json.JsonValueKind JsonValueKindNull() => System.Text.Json.JsonValueKind.Null;
}
