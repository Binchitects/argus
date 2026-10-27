using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The supplier register (Admin → Compliance → Suppliers).</summary>
[Collection(nameof(AppCollection))]
public sealed class SupplierTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("sup_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
        new Dictionary<string, string?> { ["Argus:GitlabUrl"] = "https://gitlab.example.test" });

    private static async Task<string> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "suppliers") is var c ? $"{c.GetProperty("state").GetString()}: {c.GetProperty("detail").GetString()}" : "";

    [Fact]
    [Trait("Control", "GOV-08")]
    public async Task What_Arena_connects_to_is_offered_until_registered_with_its_data_agreements_and_review()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var all = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/suppliers"));
        var suggested = all.GetProperty("suggested").EnumerateArray().Select(s => s.GetProperty("source").GetString()).ToList();
        Assert.Contains("gitlab", suggested);
        Assert.Contains("huggingface", suggested);
        Assert.StartsWith("warn: 0 suppliers in the register; not in it yet: GitLab (gitlab.example.test), Hugging Face", await CheckAsync(admin), StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/suppliers", new { name = "Hugging Face", kind = "model", risk = "extreme" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/suppliers", new { name = "Hugging Face", kind = "model", risk = "low", data = new[] { "secrets" } }));
        var hf = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/suppliers", new { name = "Hugging Face", kind = "model", risk = "low", source = "huggingface", service = "Model downloads" }));
        Assert.InRange((hf.GetProperty("reviewDue").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 728, 731);
        var gl = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/suppliers",
            new { name = "GitLab", kind = "software", risk = "medium", source = "gitlab", data = new[] { "code", "personal" }, location = "EU" }));
        // Personal data and no agreement: flagged.
        Assert.True(gl.GetProperty("agreementMissing").GetBoolean());
        Assert.Contains("no agreement said", await CheckAsync(admin), StringComparison.Ordinal);
        var id = gl.GetProperty("id").GetGuid();
        var agreed = await admin.JsonAsync(await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/compliance/suppliers/{id}", UriKind.Relative),
            new { name = "GitLab", kind = "software", risk = "high", data = new[] { "code", "personal" }, location = "EU", agreements = "DPA 2026-01", certifications = "SOC 2 Type II to 2026-06" }));
        Assert.False(agreed.GetProperty("agreementMissing").GetBoolean());
        // A higher risk is reviewed sooner.
        Assert.InRange((agreed.GetProperty("reviewDue").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 181, 183);
        Assert.StartsWith("ok: 2 suppliers in the register", await CheckAsync(admin), StringComparison.Ordinal);

        // Health data needs a BAA.
        var h = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/suppliers", new { name = "Transcribe Inc", kind = "service", risk = "high", data = new[] { "health" }, agreements = "DPA" }));
        Assert.True(h.GetProperty("agreementMissing").GetBoolean());
        await admin.PostAsync($"/api/admin/compliance/suppliers/{h.GetProperty("id").GetGuid()}/retire");
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/compliance/suppliers/{id}/review", new { note = "" }));
        var reviewed = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/suppliers/{id}/review", new { note = "Their SOC 2 report read; no exceptions" }));
        Assert.Equal("admin", reviewed.GetProperty("reviewedBy").GetString());
        Assert.Empty((await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/suppliers"))).GetProperty("suggested").EnumerateArray());
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "compliance.supplier.review");
    }
}
