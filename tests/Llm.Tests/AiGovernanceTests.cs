using System.Net;
using System.Net.Http.Json;
using System.Text;
using Llm.Api.Media;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>AI governance: the register of AI systems (Admin → Compliance → AI systems), and generated media marked as such.</summary>
[Collection(nameof(AppCollection))]
public sealed class AiGovernanceTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("ai_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static async Task<string> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "ai-systems") is var c ? $"{c.GetProperty("state").GetString()}: {c.GetProperty("detail").GetString()}" : "";

    private static object System(string cls = "limited", string? area = null, string? oversight = null, string? evaluation = null) => new
    {
        name = "HR screening assistant", kind = "assistant", purpose = "Sorts applications for the recruiters", intendedUse = "A first read, a recruiter decides",
        affected = "Applicants, recruiters", @class = cls, classWhy = "Employment is an Annex III area", highRiskArea = area, role = "deployer", owner = "hr-lead",
        oversight, evaluation,
    };

    [Fact]
    [Trait("Control", "GOV-09")]
    public async Task An_AI_system_is_put_into_use_only_once_its_class_impact_oversight_and_evaluation_are_said()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // A shared assistant is offered for the register.
        var assistant = (await admin.JsonAsync(await admin.PostAsync("/api/assistants", new { name = "Policy helper", instructions = "Answer from the handbook." }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/assistants/{assistant}/sharing", UriKind.Relative), new { reach = "Company" }));
        var all = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance/ai-systems"));
        Assert.Contains(all.GetProperty("suggested").EnumerateArray(), s => s.GetProperty("name").GetString() == "Policy helper" && s.GetProperty("kind").GetString() == "assistant");
        Assert.StartsWith("warn: 0 AI systems in the register", await CheckAsync(admin), StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/ai-systems", System("risky")));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/compliance/ai-systems", System("high", area: "astrology")));
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/ai-systems", System("high", area: "employment and workers management")));
        var id = made.GetProperty("id").GetGuid();
        Assert.InRange((made.GetProperty("reviewDue").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 181, 183);
        Assert.Equal(["its impact assessment", "its human oversight", "its evaluation"], made.GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
        var refused = await admin.PostAsync($"/api/admin/compliance/ai-systems/{id}/in-use");
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("its impact assessment", (await admin.JsonAsync(refused)).GetProperty("error").GetString(), StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/compliance/ai-systems/{id}/assess", new { assessment = " " }));
        var assessed = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/ai-systems/{id}/assess",
            new { assessment = "Applicants may be sorted unfairly: the recruiter reads every one ranked low; no automated rejection." }));
        Assert.Equal("admin", assessed.GetProperty("impactAssessedBy").GetString());
        await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/compliance/ai-systems/{id}", UriKind.Relative),
            System("high", area: "employment and workers management", oversight: "A recruiter decides every application; the assistant can be switched off", evaluation: "docs/measurements/hr-screening.md: 200 past applications, agreement 91%"));
        var inUse = await admin.JsonAsync(await admin.PostAsync($"/api/admin/compliance/ai-systems/{id}/in-use"));
        Assert.NotEqual(System_JsonNull(), inUse.GetProperty("inUseSince").ValueKind);
        Assert.Contains("1 high", await CheckAsync(admin), StringComparison.Ordinal);

        // A prohibited practice is never in use: refused while it is.
        var res = await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/compliance/ai-systems/{id}", UriKind.Relative), System("prohibited"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        Assert.Contains("retire it", (await admin.JsonAsync(res)).GetProperty("error").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/compliance/ai-systems/{id}/review", new { note = "Agreement still 90%; no complaints" }));
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        foreach (var what in new[] { "compliance.ai.register", "compliance.ai.assess", "compliance.ai.in_use", "compliance.ai.review" })
        {
            Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == what);
        }
    }

    [Fact]
    [Trait("Control", "GOV-10")]
    public void A_generated_picture_says_in_its_metadata_that_a_model_made_or_changed_it()
    {
        // A real 1x1 PNG: the mark goes after its header, as a chunk whose CRC holds, and once only.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var marked = AiMark.Mark(png, "image/png");
        Assert.True(AiMark.IsMarked(marked));
        Assert.Equal(marked, AiMark.Mark(marked, "image/png"));
        Assert.Equal(png[..33], marked[..33]);
        var length = (marked[33] << 24) | (marked[34] << 16) | (marked[35] << 8) | marked[36];
        Assert.Equal("iTXt", Encoding.ASCII.GetString(marked, 37, 4));
        var crc = (uint)((marked[41 + length] << 24) | (marked[42 + length] << 16) | (marked[43 + length] << 8) | marked[44 + length]);
        Assert.Equal(AiMark.Crc32(marked.AsSpan(37, 4 + length)), crc);
        Assert.Equal(png[33..], marked[(45 + length)..]);
        Assert.Contains(AiMark.Generated, Encoding.UTF8.GetString(marked), StringComparison.Ordinal);
        // The person's picture changed by a model: composite.
        Assert.Contains(AiMark.Composite, Encoding.UTF8.GetString(AiMark.Mark(png, "image/png", composite: true)), StringComparison.Ordinal);

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x02, 0xFF, 0xD9];
        var j = AiMark.Mark(jpeg, "image/jpeg");
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE1], j[..4]);
        Assert.Equal(j.Length - jpeg.Length - 2, (j[4] << 8) | j[5]);
        Assert.True(AiMark.IsMarked(j));
        // Another format, and a file that is not what it says, stay as they are.
        Assert.Equal(png, AiMark.Mark(png, "image/webp"));
        Assert.Equal(jpeg, AiMark.Mark(jpeg, "image/png"));
    }

    private static global::System.Text.Json.JsonValueKind System_JsonNull() => global::System.Text.Json.JsonValueKind.Null;
}
