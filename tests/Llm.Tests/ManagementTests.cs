using System.Net;
using System.Net.Http.Json;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Running the management system (Admin → Compliance → Management).</summary>
[Collection(nameof(AppCollection))]
public sealed class ManagementTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("mgt_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static async Task<string> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "management") is var c ? $"{c.GetProperty("state").GetString()}: {c.GetProperty("detail").GetString()}" : "";

    [Fact]
    [Trait("Control", "GOV-12")]
    public async Task A_review_keeps_its_inputs_as_the_system_stood_and_objectives_are_measured_by_their_checks()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.Contains("no management review within the year", await CheckAsync(admin), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/compliance/owners/IAM-03", UriKind.Relative), new { owner = "it-security" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/compliance/owners/XYZ-99", UriKind.Relative), new { owner = "x" }));
        Assert.Equal("it-security", (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/owners"))).GetProperty("IAM-03").GetString());

        await admin.PostAsync("/api/admin/compliance/findings", new { title = "No restore drill this quarter", kind = "minor" });
        var inputs = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/management-reviews/inputs"))).GetProperty("inputs").GetString()!;
        Assert.Contains("This is the first review.", inputs, StringComparison.Ordinal);
        Assert.Contains("- minor: No restore drill this quarter", inputs, StringComparison.Ordinal);
        Assert.Contains("## Risks and their treatment", inputs, StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/management-reviews", new { heldAt = DateTimeOffset.UtcNow, attendees = "CEO, CISO" }));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync("/api/admin/compliance/management-reviews",
            new { heldAt = DateTimeOffset.UtcNow.AddHours(-2), attendees = "CEO, CISO, CTO", minutes = "Read the inputs.", decisions = "A restore drill each quarter; two-factor for everyone." }));
        var second = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/management-reviews/inputs"))).GetProperty("inputs").GetString()!;
        Assert.Contains("decided: A restore drill each quarter", second, StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/objectives", new { title = "Admins use two factors", check = "nope", target = 95 }));
        var objective = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/objectives", new { title = "Admins use two factors", check = "two-factor", target = 95, owner = "it-security" }));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, objective.GetProperty("achieved").ValueKind);
        // Measured from the checks' daily history: 1 day ok of 2 is 50%, under the target.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ComplianceSnapshots.Add(new Llm.Core.Compliance.ComplianceSnapshot { Day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), Check = "two-factor", State = "ok" });
            db.ComplianceSnapshots.Add(new Llm.Core.Compliance.ComplianceSnapshot { Day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), Check = "two-factor", State = "warn" });
            await db.SaveChangesAsync();
        }
        var measured = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/objectives")))[0];
        Assert.Equal(50, measured.GetProperty("achieved").GetInt32());
        Assert.False(measured.GetProperty("met").GetBoolean());
        Assert.Contains("objectives not met: Admins use two factors", await CheckAsync(admin), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "GOV-12")]
    public async Task An_auditor_reads_and_asks_for_evidence_and_changes_nothing_else()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "extaudit", email = "extaudit@example.test" }));
        var role = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "auditor").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = made.GetProperty("id").GetGuid() }));
        var auditor = await new TestBrowser(f).SignedInAsync("extaudit", made.GetProperty("password").GetString()!);

        var asked = await auditor.JsonAsync(await auditor.PostAsync("/api/admin/compliance/evidence-requests", new { text = "Last quarter's access review, with its decisions", clause = "iso27001 A.5.18" }));
        Assert.Equal("extaudit", asked.GetProperty("requestedBy").GetString());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await auditor.PostAsync($"/api/admin/compliance/evidence-requests/{asked.GetProperty("id").GetGuid()}/answer", new { answer = "Mine to give?" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await auditor.PostAsync("/api/admin/compliance/objectives", new { title = "x", check = "two-factor", target = 90 }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await auditor.PostAsync("/api/admin/compliance/risks", new { title = "x", category = "security", likelihood = 1, impact = 1, treatment = "reduce" }));
        await StatusAssert.Is(HttpStatusCode.OK, await auditor.GetAsync("/api/admin/compliance/readiness"));

        var answered = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/evidence-requests/{asked.GetProperty("id").GetGuid()}/answer",
            new { answer = "evidence.zip, access-reviews.csv: Q3 review, 14 items, 2 revoked" }));
        Assert.Equal("admin", answered.GetProperty("answeredBy").GetString());
        Assert.Contains("Q3 review", (await auditor.JsonAsync(await auditor.GetAsync("/api/admin/compliance/evidence-requests")))[0].GetProperty("answer").GetString(), StringComparison.Ordinal);
    }
}
