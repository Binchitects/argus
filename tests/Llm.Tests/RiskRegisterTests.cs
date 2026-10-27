using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The risk register (Admin → Compliance → Risks): levels, treatment, acceptance, actions, reviews, its check and its record.</summary>
[Collection(nameof(AppCollection))]
public sealed class RiskRegisterTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("risk_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static object Input(string title = "An admin account is taken over", int l = 3, int i = 5, string treatment = "reduce", string[]? controls = null,
        int? rl = 1, int? ri = 5, string category = "security") => new
    {
        title, description = "A phished password on an admin account.", category, assets = new[] { "Arena's database (Postgres)", "The audit log" },
        threat = "Phishing", likelihood = l, impact = i, owner = "ciso", treatment, controls = controls ?? ["IAM-01", "IAM-03"],
        plan = "Two-factor for every admin.", residualLikelihood = rl, residualImpact = ri,
    };

    private static async Task<string> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "risks") is var c ? $"{c.GetProperty("state").GetString()}: {c.GetProperty("detail").GetString()}" : "";

    [Fact]
    [Trait("Control", "GOV-04")]
    public async Task A_risk_has_its_levels_before_and_after_treatment_its_controls_and_a_review_its_level_sets()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.StartsWith("warn: The risk register is empty", await CheckAsync(admin), StringComparison.Ordinal);

        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/risks", Input()));
        Assert.Equal(15, made.GetProperty("score").GetInt32());
        Assert.Equal("high", made.GetProperty("level").GetString());
        Assert.Equal(5, made.GetProperty("residualScore").GetInt32());
        Assert.Equal("medium", made.GetProperty("residualLevel").GetString());
        // Reviewed again in a year: its level after treatment is medium.
        var due = made.GetProperty("reviewDue").GetDateTimeOffset();
        Assert.InRange((due - DateTimeOffset.UtcNow).TotalDays, 364, 366);
        Assert.StartsWith("ok: 1 open risk (1 medium", await CheckAsync(admin), StringComparison.Ordinal);

        // Not yet treated, a critical one is looked at again within three months.
        var critical = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/risks", Input("Backups are lost", 4, 5, rl: null, ri: null, category: "continuity", controls: ["OPS-01"])));
        Assert.Equal("critical", critical.GetProperty("level").GetString());
        Assert.InRange((critical.GetProperty("reviewDue").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 90, 92);

        foreach (var (body, message) in new (object, string)[]
        {
            (Input(l: 6), "Likelihood and impact are each 1 to 5."),
            (Input(rl: 2, ri: null), "or neither yet"),
            (Input(controls: ["XYZ-99"]), "No control XYZ-99"),
            (Input(treatment: "ignore"), "Its treatment is one of"),
            (Input(" "), "Name the risk."),
        })
        {
            var res = await admin.PostAsync("/api/admin/compliance/risks", body);
            await StatusAssert.Is(HttpStatusCode.BadRequest, res);
            Assert.Contains(message, (await admin.JsonAsync(res)).GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/risks"));
        Assert.Equal(["Backups are lost", "An admin account is taken over"], list.EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        var assets = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/risks/assets"));
        Assert.Contains(assets.EnumerateArray(), a => a.GetProperty("name").GetString() == "Arena's database (Postgres)");
    }

    [Fact]
    [Trait("Control", "GOV-04")]
    public async Task An_acceptance_names_who_why_and_until_when_and_a_treatment_has_its_actions_and_history()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var id = (await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/risks", Input()))).GetProperty("id").GetGuid();
        var at = $"/api/admin/compliance/risks/{id}";

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"{at}/accept", new { why = " " }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"{at}/accept", new { why = "Cheaper than a token", until = DateTimeOffset.UtcNow.AddYears(2) }));
        var until = DateTimeOffset.UtcNow.AddDays(90);
        var accepted = await admin.JsonAsync(await admin.PostAsync($"{at}/accept", new { why = "Two-factor for everyone in the next quarter", until }));
        Assert.Equal("accept", accepted.GetProperty("treatment").GetString());
        Assert.Equal("admin", accepted.GetProperty("acceptedBy").GetString());
        // Decided again when the acceptance ends.
        Assert.True(accepted.GetProperty("reviewDue").GetDateTimeOffset() <= until.AddSeconds(1));

        // Treated another way: the acceptance no longer holds.
        var changed = await admin.JsonAsync(await admin.Http.PutAsJsonAsync(new Uri(at, UriKind.Relative), Input(treatment: "reduce")));
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("acceptedBy").ValueKind);

        var action = await admin.JsonAsync(await admin.PostAsync($"{at}/actions", new { text = "Require two-factor for admins", owner = "it", due = DateTimeOffset.UtcNow.AddDays(-1) }));
        Assert.StartsWith("warn: 1 open risk", await CheckAsync(admin), StringComparison.Ordinal);
        Assert.Contains("1 treatment action past due", await CheckAsync(admin), StringComparison.Ordinal);
        await admin.PostAsync($"{at}/actions/{action.GetProperty("id").GetGuid()}/done");
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"{at}/review", new { note = " " }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"{at}/review", new { note = "Still holds", next = DateTimeOffset.UtcNow.AddYears(3) }));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"{at}/review", new { note = "Two-factor is required of admins now" }));

        // A review long past: red.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Risks.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewDue, DateTimeOffset.UtcNow.AddDays(-40)));
        }
        Assert.StartsWith("fail: 1 open risk", await CheckAsync(admin), StringComparison.Ordinal);
        Assert.True((await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/risks"))).EnumerateArray().Single().GetProperty("reviewOverdue").GetBoolean());

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"{at}/close", new { why = "" }));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"{at}/close", new { why = "The admin accounts moved to company sign-in" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"{at}/actions", new { text = "Too late" }));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"{at}/reopen", new { why = "Local admins are back" }));

        var one = await admin.JsonAsync(await admin.GetAsync(at));
        Assert.Equal(["created", "accepted", "changed", "action", "action_done", "reviewed", "closed", "reopened"],
            one.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal("admin", one.GetProperty("actions")[0].GetProperty("doneBy").GetString());

        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"));
        foreach (var what in new[] { "compliance.risk.create", "compliance.risk.accept", "compliance.risk.change", "compliance.risk.review", "compliance.risk.close" })
        {
            Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == what);
        }
    }

    [Fact]
    [Trait("Control", "GOV-04")]
    public async Task Only_those_who_manage_compliance_change_the_register_and_an_auditor_reads_it()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await admin.PostAsync("/api/admin/compliance/risks", Input());
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "riskaudit", email = "riskaudit@example.test" }));
        var role = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "auditor").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = made.GetProperty("id").GetGuid() }));
        var auditor = await new TestBrowser(f).SignedInAsync("riskaudit", made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.OK, await auditor.GetAsync("/api/admin/compliance/risks"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await auditor.PostAsync("/api/admin/compliance/risks", Input("Not mine to add")));

        var member = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "riskmember", email = "riskmember@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync("riskmember", member.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/admin/compliance/risks"));
    }
}
