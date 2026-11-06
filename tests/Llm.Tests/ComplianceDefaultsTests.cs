using System.Net;
using System.Text.Json;
using Llm.Api.Compliance;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// What a new installation shows in Admin → Compliance: what Arena can hold by itself holds from the start (the starter's
/// rules, retention and register, the first anchor, the first access review), and each check left is the company's step,
/// said with why, with a button where one click does it.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ComplianceDefaultsTests(AppFixture app)
{
    private sealed record Setup(WebApplicationFactory<Program> App, string Root) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }

        public async Task SqlAsync(FormattableString sql)
        {
            await using var scope = App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(sql);
        }

        public async Task<Dictionary<string, CheckResult>> ChecksAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ComplianceChecks>().RunAsync(CancellationToken.None)).ToDictionary(c => c.Id);
        }
    }

    /// <summary>A new installation as the stack makes one: APP_KEY, the anchors' volume, the backups' folder (none yet), access reviewed every 90 days.</summary>
    private Setup NewInstall(bool starter = true, int reviewEveryDays = 90)
    {
        var root = Directory.CreateTempSubdirectory("cmpdef-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "anchors"));
        Directory.CreateDirectory(Path.Combine(root, "backups"));
        return new Setup(app.Create(app.ConnectionStringFor("cmpdef_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-compliance-defaults",
            ["Audit:AnchorDir"] = Path.Combine(root, "anchors"),
            ["Storage:BackupDir"] = Path.Combine(root, "backups"),
            ["Compliance:ReviewEveryDays"] = reviewEveryDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Compliance:Starter"] = starter ? "true" : "false",
        }), root);
    }

    /// <summary>
    /// The bounds: nothing fails; everything Arena does by itself holds; what is left (in part) is only what the company does or
    /// decides, each saying what and why. Loki and Prometheus are not here: in the stack, the logs' retention and the
    /// certificate are checked there and hold with the installer's own CA.
    /// </summary>
    [Fact]
    [Trait("Control", "GOV-11")]
    [Trait("Control", "DAT-08")]
    [Trait("Control", "DAT-01")]
    public async Task A_new_installation_holds_what_Arena_does_by_itself_and_each_check_left_is_the_companys_step_said_with_why()
    {
        await using var s = NewInstall();
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        // The schedule starts the first access review as the installation begins (here run now, rather than wait on its service).
        await ActivatorUtilities.CreateInstance<AccessReviewSchedule>(s.App.Services).RunAsync(CancellationToken.None);
        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"));
        var checks = view.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        string State(string id) => checks[id].GetProperty("state").GetString()!;
        var byState = checks.Keys.GroupBy(State).ToDictionary(g => g.Key, g => g.Order(StringComparer.Ordinal).ToList());

        Assert.False(byState.ContainsKey("fail"), string.Join("\n", byState.GetValueOrDefault("fail", []).Select(id => $"{id}: {checks[id].GetProperty("detail").GetString()}")));
        Assert.Equal(["certificate", "log-retention"], byState["unknown"]);
        Assert.Equal(["access-review", "ai-systems", "audit-chain", "audit-log", "chat-retention", "data-key", "data-requests", "files-sealed", "incidents",
            "passwords", "personal-data", "records-retention", "sessions", "suppliers"], byState["ok"]);
        // In part: the company's own steps, at most these ten, and each says what to do.
        Assert.Equal(["audit-forwarding", "audits", "backups", "management", "notice", "policies", "risks", "soa", "two-factor", "vulnerabilities"], byState["warn"]);
        foreach (var id in byState["warn"])
        {
            Assert.Matches(@"Settings →|Admin →|Code Studio|arenactl", checks[id].GetProperty("detail").GetString()!);
        }
        // The first backup is due, not missed; the policies wait for a person's approval; the steps one click does have their button.
        Assert.Contains("the first is due within a day", checks["backups"].GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains($"{Policies.TemplateList.Count} drafts wait for a person", checks["policies"].GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(["management", "notice", "risks", "two-factor"],
            checks.Values.Where(c => c.TryGetProperty("fix", out var x) && x.ValueKind == JsonValueKind.Object).Select(c => c.GetProperty("id").GetString()!).Order(StringComparer.Ordinal));
        // The first anchor is kept apart already.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(s.Root, "anchors")));

        // What the starter did: the two rules enforced after those every database starts with, the templates as drafts, chats
        // kept a year, Hugging Face in the register.
        async Task<List<JsonElement>> RulesAsync() => [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/safeguards/rules"))).GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("rule"))];
        var rules = await RulesAsync();
        Assert.Equal(ComplianceDefaults.Rules, rules.TakeLast(2).Select(r => r.GetProperty("name").GetString()!));
        Assert.All(rules.TakeLast(2), r => Assert.True(r.GetProperty("enabled").GetBoolean() && !r.GetProperty("monitor").GetBoolean()));
        var policies = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/policies"))).EnumerateArray().ToList();
        Assert.Equal(Policies.TemplateList.Count, policies.Count);
        Assert.All(policies, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("version").ValueKind));
        Assert.Contains("Chats are deleted 365 days after their last message.", checks["chat-retention"].GetProperty("detail").GetString(), StringComparison.Ordinal);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=200"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("compliance.starter", audit);

        // Started again, the starter is not: it is a new installation's alone.
        using (var scope = s.App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Llm.Api.Identity.IdentityBootstrap>().RunAsync();
        }
        Assert.Equal(rules.Count, (await RulesAsync()).Count);
    }

    [Fact]
    [Trait("Control", "GOV-11")]
    public async Task Each_step_is_one_click_audited_and_needs_the_permission_of_what_it_changes()
    {
        // Reviews by hand here: the schedule would start one as the app starts.
        await using var s = NewInstall(starter: false, reviewEveryDays: 0);
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var offered = (await s.ChecksAsync()).Values.Where(c => c.Fix is not null).Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["access-review", "chat-retention", "management", "notice", "personal-data", "policies", "risks", "suppliers", "two-factor"], offered);

        // A compliance officer takes the company's steps, not those that change a setting or a rule.
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "cmpofficer", email = "cmpofficer@example.test" }));
        var role = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "compliance-officer").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = made.GetProperty("id").GetGuid() }));
        var officer = await new TestBrowser(s.App).SignedInAsync("cmpofficer", made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await officer.PostAsync("/api/admin/compliance/fixes/personal-data"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await officer.PostAsync("/api/admin/compliance/fixes/chat-retention"));
        await StatusAssert.Is(HttpStatusCode.OK, await officer.PostAsync("/api/admin/compliance/fixes/risks"));
        // A member, none.
        var member = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "cmpmember2", email = "cmpmember2@example.test" }));
        var nobody = await new TestBrowser(s.App).SignedInAsync("cmpmember2", member.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await nobody.PostAsync("/api/admin/compliance/fixes/policies"));

        foreach (var step in new[] { "personal-data", "chat-retention", "policies", "suppliers", "management", "access-review" })
        {
            await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/compliance/fixes/{step}"));
        }
        // Done, a step is offered no more.
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync("/api/admin/compliance/fixes/risks"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync("/api/admin/compliance/fixes/nonsense"));
        var checks = await s.ChecksAsync();
        foreach (var id in new[] { "personal-data", "chat-retention", "suppliers", "risks", "access-review" })
        {
            Assert.True(checks[id].State == ComplianceChecks.Ok, $"{id}: {checks[id].Detail}");
        }
        // What a person decides stays theirs: the policies are drafts, the management review is to hold.
        Assert.Equal(ComplianceChecks.Warn, checks["policies"].State);
        Assert.Null(checks["policies"].Fix);
        Assert.Contains("no management review within the year", checks["management"].Detail, StringComparison.Ordinal);
        Assert.Contains($"{ComplianceDefaults.Objectives.Count} objectives", checks["management"].Detail, StringComparison.Ordinal);

        // The notice and two-factor last: each holds back the admin's next step until it is accepted or set up.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync("/api/admin/compliance/fixes/notice"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await admin.GetAsync("/api/admin/compliance"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync("/api/account/notice/accept", new { version = "1" }));
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync("/api/admin/compliance/fixes/two-factor"));
        checks = await s.ChecksAsync();
        Assert.Equal(ComplianceChecks.Ok, checks["notice"].State);
        Assert.Contains("Required of admins", checks["two-factor"].Detail, StringComparison.Ordinal);
        Assert.Null(checks["two-factor"].Fix);

        await using var scope = s.App.Services.CreateAsyncScope();
        var audited = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == "compliance.fix").Select(e => e.Target).ToListAsync();
        Assert.Equal(["access-review", "chat-retention", "management", "notice", "personal-data", "policies", "risks", "suppliers", "two-factor"], audited.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Control", "OPS-01")]
    public async Task A_new_installation_without_a_backup_yet_is_due_and_a_day_on_it_fails()
    {
        await using var s = NewInstall(starter: false);
        await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var backups = (await s.ChecksAsync())["backups"];
        Assert.Equal(ComplianceChecks.Warn, backups.State);
        Assert.Contains("No backup yet: this installation is", backups.Detail, StringComparison.Ordinal);
        // Installed two days ago and still none: missed.
        await s.SqlAsync($"UPDATE \"AspNetUsers\" SET \"CreatedAt\" = now() - interval '2 days'");
        backups = (await s.ChecksAsync())["backups"];
        Assert.Equal(ComplianceChecks.Fail, backups.State);
        Assert.StartsWith("No backup yet: run ./arenactl backup on a schedule", backups.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "LOG-03")]
    public async Task The_first_anchor_is_kept_with_the_logs_first_entries_not_a_day_after_the_start()
    {
        await using var s = NewInstall(starter: false);
        // The app's walk at its start found the log empty; its next is a day later. Signing in writes the first entries.
        await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var chain = (await s.ChecksAsync())["audit-chain"];
        Assert.True(chain.State == ComplianceChecks.Ok, chain.Detail);
        Assert.Contains("1 anchors kept apart", chain.Detail, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(s.Root, "anchors")));
    }
}
