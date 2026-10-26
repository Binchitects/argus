using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>The sign-in notice: public on the sign-in page, accepted in its current version before anything else, each acceptance kept.</summary>
[Collection(nameof(AppCollection))]
public sealed class NoticeTests(AppFixture app)
{
    [Fact]
    [Trait("Control", "GOV-02")]
    public async Task Everyone_accepts_the_current_version_before_anything_else_and_a_new_version_again()
    {
        await using var f = app.Create(app.ConnectionStringFor("notice_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Notice:Title"] = "Acceptable use",
            ["Notice:Text"] = "Use this service for work. No patients' data in prompts.",
            ["Notice:Version"] = "2026-10",
        });
        var visitor = new TestBrowser(f);
        var info = await visitor.JsonAsync(await visitor.GetAsync("/api/info"));
        Assert.Equal("Acceptable use", info.GetProperty("notice").GetProperty("title").GetString());

        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.True((await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("mustAcceptNotice").GetBoolean());
        var refused = await admin.GetAsync("/api/admin/people");
        await StatusAssert.Is(HttpStatusCode.Forbidden, refused);
        Assert.Equal("notice", (await admin.JsonAsync(refused)).GetProperty("status").GetString());
        var shown = await admin.JsonAsync(await admin.GetAsync("/api/account/notice"));
        Assert.Equal("2026-10", shown.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("acceptedVersion").ValueKind);

        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/account/notice/accept", new { version = "2025-01" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync("/api/account/notice/accept", new { version = "2026-10" }));
        Assert.False((await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("mustAcceptNotice").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.GetAsync("/api/admin/people"));
        var check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "notice");
        Assert.Equal("ok", check.GetProperty("state").GetString());
        Assert.Contains("1 of 1 people accepted it", check.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // A new version: accepted again, at once.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Notice:Version", value = "2026-11" } } }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await admin.GetAsync("/api/admin/people"));
        Assert.Equal("2026-10", (await admin.JsonAsync(await admin.GetAsync("/api/account/notice"))).GetProperty("acceptedVersion").GetString());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync("/api/account/notice/accept", new { version = "2026-11" }));

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() == "notice.accept").Select(e => e.GetProperty("detail").GetString()).ToList();
        Assert.Equal(["version 2026-11", "version 2026-10"], audit);
        using var zip = new System.IO.Compression.ZipArchive(await (await admin.GetAsync("/api/admin/compliance/evidence")).Content.ReadAsStreamAsync());
        using var reader = new StreamReader(zip.GetEntry("notice.csv")!.Open());
        var csv = await reader.ReadToEndAsync();
        Assert.Contains("admin,2026-10,", csv, StringComparison.Ordinal);
        Assert.Contains("admin,2026-11,", csv, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "GOV-02")]
    public async Task Without_a_notice_nothing_waits_and_the_check_asks_for_one()
    {
        await using var f = app.Create(app.ConnectionStringFor("nonotice_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        Assert.False((await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("mustAcceptNotice").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await admin.JsonAsync(await admin.GetAsync("/api/info"))).GetProperty("notice").ValueKind);
        var check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "notice");
        Assert.Equal("warn", check.GetProperty("state").GetString());
    }
}
