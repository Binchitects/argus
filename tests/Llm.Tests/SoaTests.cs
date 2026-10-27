using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The Statements of Applicability (Admin → Compliance → Statement of Applicability): each line, the approvals, the downloads.</summary>
[Collection(nameof(AppCollection))]
public sealed class SoaTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("soa_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static Task<HttpResponseMessage> Put(TestBrowser b, string path, object body) => b.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private static async Task<string> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "soa") is var c ? $"{c.GetProperty("state").GetString()}: {c.GetProperty("detail").GetString()}" : "";

    [Fact]
    [Trait("Control", "GOV-05")]
    public async Task Each_Annex_A_control_says_whether_it_applies_and_why_how_far_and_what_serves_it()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/soa/iso27001"));
        var lines = view.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(93, lines.Count);
        var a85 = lines.Single(l => l.GetProperty("clause").GetString() == "A.8.5");
        Assert.False(a85.GetProperty("decided").GetBoolean());
        Assert.Contains("IAM-03", a85.GetProperty("controls").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(38, (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/soa/iso42001"))).GetProperty("lines").GetArrayLength());
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync("/api/admin/compliance/soa/soc2"));

        // A risk that IAM-03 treats shows on the controls it serves.
        await admin.PostAsync("/api/admin/compliance/risks", new { title = "An account is taken over", category = "security", likelihood = 3, impact = 4, treatment = "reduce", controls = new[] { "IAM-03" } });
        a85 = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/soa/iso27001"))).GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("clause").GetString() == "A.8.5");
        Assert.Equal("An account is taken over", a85.GetProperty("risks")[0].GetProperty("title").GetString());

        await StatusAssert.Is(HttpStatusCode.BadRequest, await Put(admin, "/api/admin/compliance/soa/iso27001/A.7.1", new { applicable = false }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await Put(admin, "/api/admin/compliance/soa/iso27001/A.8.5", new { applicable = true, status = "mostly" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await Put(admin, "/api/admin/compliance/soa/iso27001/A.9.9", new { applicable = true, status = "implemented" }));
        var excluded = await admin.JsonAsync(await Put(admin, "/api/admin/compliance/soa/iso27001/A.7.1", new { applicable = false, justification = "No premises of our own: the host is in a provider's data centre." }));
        Assert.Equal("not_implemented", excluded.GetProperty("status").GetString());
        var line = await admin.JsonAsync(await Put(admin, "/api/admin/compliance/soa/iso27001/A.8.5", new { applicable = true, status = "partial", justification = "Two-factor for admins only." }));
        Assert.True(line.GetProperty("decided").GetBoolean());
        Assert.Equal("admin", line.GetProperty("updatedBy").GetString());

        var csv = await (await admin.GetAsync("/api/admin/compliance/soa/iso27001/csv")).Content.ReadAsStringAsync();
        Assert.StartsWith("control,title,applies,how far,why,Arena's controls,risks", csv, StringComparison.Ordinal);
        Assert.Contains("A.7.1,Physical security perimeters,no,not_implemented,No premises of our own: the host is in a provider's data centre.", csv, StringComparison.Ordinal);
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "compliance.soa.set" && e.GetProperty("target").GetString() == "iso27001 A.7.1");
    }

    [Fact]
    [Trait("Control", "GOV-05")]
    public async Task It_is_approved_once_every_control_is_decided_each_approval_a_version_kept_as_it_stood()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.StartsWith("warn: ISO 27001's is not approved yet (0 of 93", await CheckAsync(admin), StringComparison.Ordinal);
        var refused = await admin.PostAsync("/api/admin/compliance/soa/iso27001/approve", new { note = "First" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("93 of its 93 controls are not decided yet", (await admin.JsonAsync(refused)).GetProperty("error").GetString(), StringComparison.Ordinal);

        var clauses = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/soa/iso27001"))).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("clause").GetString()!).ToList();
        foreach (var clause in clauses)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await Put(admin, $"/api/admin/compliance/soa/iso27001/{clause}", new { applicable = true, status = "implemented" }));
        }
        var v1 = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/soa/iso27001/approve", new { note = "After the 2026 risk assessment" }));
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.StartsWith("ok: ISO 27001's version 1, approved by admin", await CheckAsync(admin), StringComparison.Ordinal);

        // Changed after it: amber until approved again, and version 1 stays as it was.
        await Put(admin, "/api/admin/compliance/soa/iso27001/A.5.1", new { applicable = true, status = "partial", justification = "The policy is being rewritten." });
        Assert.Contains("changed since", await CheckAsync(admin), StringComparison.Ordinal);
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/soa/iso27001"));
        Assert.True(view.GetProperty("changedSinceApproval").GetBoolean());
        var first = await (await admin.GetAsync("/api/admin/compliance/soa/iso27001/csv?version=1")).Content.ReadAsStringAsync();
        Assert.Contains("A.5.1,Policies for information security,yes,implemented,", first, StringComparison.Ordinal);
        Assert.Equal(2, (await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/soa/iso27001/approve", new { }))).GetProperty("version").GetInt32());
        Assert.Equal([2, 1], (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/soa/iso27001"))).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync("/api/admin/compliance/soa/iso27001/csv?version=9"));
    }
}
