using System.Net;
using System.Text.Json;
using Llm.Api.Compliance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Security alerts raised from what the app sees, and the incident register with the law's clocks.</summary>
[Collection(nameof(AppCollection))]
public sealed class IncidentTests(AppFixture app)
{
    [Fact]
    [Trait("Control", "MON-02")]
    public async Task Failed_sign_ins_an_admin_made_many_exports_and_a_failed_backup_each_raise_one_alert_to_every_admin()
    {
        var root = Directory.CreateTempSubdirectory("security-").FullName;
        try
        {
            var backups = Path.Combine(root, "backups");
            var name = DateTimeOffset.UtcNow.AddHours(-2).ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Path.Combine(backups, name));
            File.WriteAllText(Path.Combine(backups, name, "RESULT"), "FAILED (2 problem(s))\n");
            File.WriteAllText(Path.Combine(backups, name, "MANIFEST"), $"backup: {name}\nstarted: {DateTimeOffset.UtcNow.AddHours(-2):yyyy-MM-ddTHH:mm:ssZ}\n");
            await using var f = app.Create(app.ConnectionStringFor("sec_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
            {
                ["Security:FailedSignIns"] = "6", ["Security:FailedSignInsOneAccount"] = "4", ["Security:Exports"] = "2", ["Storage:BackupDir"] = backups,
            });
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            var watch = f.Services.GetRequiredService<SecurityWatch>();
            Assert.Equal(1, await watch.RunAsync(CancellationToken.None));

            var person = "secp" + Guid.NewGuid().ToString("N")[..6];
            var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = person, email = $"{person}@example.test" }));
            for (var i = 0; i < 4; i++)
            {
                await new TestBrowser(f).LoginAsync("admin", "not the password " + i);
            }
            for (var i = 0; i < 2; i++)
            {
                await new TestBrowser(f).LoginAsync("someone-else", "wrong");
            }
            var role = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "people-admin").GetProperty("id").GetGuid();
            await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = made.GetProperty("id").GetGuid() }));
            for (var i = 0; i < 2; i++)
            {
                (await admin.GetAsync("/api/account/export")).Dispose();
            }

            var raised = await watch.RunAsync(CancellationToken.None);
            var said = string.Join("; ", (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/incidents"))).GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
            Assert.True(raised == 4, $"{raised} raised; all: {said}");
            Assert.Equal(0, await watch.RunAsync(CancellationToken.None));
            var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/incidents"));
            var kinds = view.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).Order().ToList();
            Assert.Equal(["account_attacked", "admin_granted", "backup_failed", "mass_export", "sign_in_failures"], kinds);
            var granted = view.GetProperty("alerts").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "admin_granted");
            Assert.Equal($"People admin given to {person}", granted.GetProperty("title").GetString());
            var news = (await admin.JsonAsync(await admin.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
            Assert.Contains($"The backup {name} failed", news);
            Assert.Contains("4 failed sign-ins for admin in ten minutes", news);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Control", "MON-02")]
    public async Task An_incident_runs_from_its_alert_to_closure_with_the_GDPRs_72_hours_and_HIPAAs_60_days()
    {
        await using var f = app.Create(app.ConnectionStringFor("inc_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<JsonElement> CheckAsync() =>
            (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "incidents");
        Assert.Equal("ok", (await CheckAsync()).GetProperty("state").GetString());

        var detected = DateTimeOffset.UtcNow.AddDays(-4);
        var opened = await admin.PostAsync("/api/admin/compliance/incidents", new
        {
            title = "Laptop with an export stolen", summary = "An admin's laptop with a person's export.", severity = "high", personalData = true, healthData = true, detectedAt = detected,
        });
        await StatusAssert.Is(HttpStatusCode.Created, opened);
        var incident = await admin.JsonAsync(opened);
        var id = incident.GetProperty("id").GetGuid();
        Assert.Equal(incident.GetProperty("detectedAt").GetDateTimeOffset().AddHours(72), incident.GetProperty("authorityDue").GetDateTimeOffset());
        Assert.Equal(incident.GetProperty("detectedAt").GetDateTimeOffset().AddDays(60), incident.GetProperty("peopleDue").GetDateTimeOffset());
        var late = await CheckAsync();
        Assert.Equal("fail", late.GetProperty("state").GetString());
        Assert.Contains("Laptop with an export stolen", late.GetProperty("detail").GetString(), StringComparison.Ordinal);

        async Task<HttpResponseMessage> StepAsync(string kind, string? text = null) => await admin.PostAsync($"/api/admin/compliance/incidents/{id}/steps", new { kind, text });
        await StatusAssert.Is(HttpStatusCode.BadRequest, await StepAsync("note"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await StepAsync("closed", "Encrypt laptops."));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await StepAsync("authority_not_required"));
        await StatusAssert.Is(HttpStatusCode.OK, await StepAsync("contained", "The laptop was wiped remotely."));
        await StatusAssert.Is(HttpStatusCode.OK, await StepAsync("authority_notified", "Reported to the supervisory authority, case 2026-118."));
        await StatusAssert.Is(HttpStatusCode.OK, await StepAsync("people_notified", "The person was told by letter."));
        Assert.Equal("warn", (await CheckAsync()).GetProperty("state").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await StepAsync("closed"));
        var closed = await admin.JsonAsync(await StepAsync("closed", "Laptops' disks are encrypted now, and exports expire after a day."));
        Assert.Equal("closed", closed.GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await StepAsync("note", "Too late"));
        Assert.Equal("ok", (await CheckAsync()).GetProperty("state").GetString());

        var detail = await admin.JsonAsync(await admin.GetAsync($"/api/admin/compliance/incidents/{id}"));
        Assert.Equal(["opened", "contained", "authority_notified", "people_notified", "closed"], detail.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("incident.open", audit);
        Assert.Contains("incident.closed", audit);
    }
}
