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

    [Fact]
    [Trait("Control", "GOV-10")]
    public void Generated_sound_and_video_say_in_their_metadata_that_a_model_made_them()
    {
        static int Syncsafe(byte[] b, int at) => (b[at] << 21) | (b[at + 1] << 14) | (b[at + 2] << 7) | b[at + 3];
        static uint Big(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

        // An MP3 with no tag: one of its own before the sound, with a PRIV frame owned by XMP.
        byte[] frames = [0xFF, 0xFB, 0x90, 0x64, 1, 2, 3, 4];
        var mp3 = AiMark.Mark(frames, "audio/mpeg");
        Assert.Equal("ID3", Encoding.ASCII.GetString(mp3, 0, 3));
        Assert.Equal(mp3.Length - frames.Length - 10, Syncsafe(mp3, 6));
        Assert.Equal("PRIV", Encoding.ASCII.GetString(mp3, 10, 4));
        Assert.Equal("XMP\0", Encoding.ASCII.GetString(mp3, 20, 4));
        Assert.Equal(frames, mp3[^frames.Length..]);
        Assert.True(AiMark.IsMarked(mp3));
        Assert.Equal(mp3, AiMark.Mark(mp3, "audio/mpeg"));
        // Its own ID3v2.4 tag: the frame goes first in it (sizes syncsafe), what was there after it.
        byte[] title = [.. "TIT2"u8, 0, 0, 0, 3, 0, 0, 3, (byte)'H', (byte)'i'];
        byte[] tagged = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, (byte)title.Length, .. title, .. frames];
        var v4 = AiMark.Mark(tagged, "audio/mpeg");
        Assert.Equal("PRIV", Encoding.ASCII.GetString(v4, 10, 4));
        var frame = Syncsafe(v4, 14);
        Assert.Equal(title.Length + 10 + frame, Syncsafe(v4, 6));
        Assert.Equal([.. title, .. frames], v4[(20 + frame)..]);
        // Unsynchronised, or not an MP3: left as it is.
        byte[] unsynchronised = [.. "ID3"u8, 4, 0, 0x80, 0, 0, 0, (byte)title.Length, .. title, .. frames];
        Assert.Equal(unsynchronised, AiMark.Mark(unsynchronised, "audio/mpeg"));
        Assert.Equal("text"u8.ToArray(), AiMark.Mark("text"u8.ToArray(), "audio/mpeg"));

        // An MP4: a uuid box with XMP's UUID at its end, everything before it (the chunk offsets) as it was.
        byte[] mp4 = [0, 0, 0, 16, .. "ftypisom"u8, 0, 0, 2, 0, 0, 0, 0, 12, .. "mdat"u8, 1, 2, 3, 4];
        var movie = AiMark.Mark(mp4, "video/mp4");
        Assert.Equal(mp4, movie[..mp4.Length]);
        Assert.Equal((uint)(movie.Length - mp4.Length), Big(movie, mp4.Length));
        Assert.Equal("uuid", Encoding.ASCII.GetString(movie, mp4.Length + 4, 4));
        Assert.Equal("BE7ACFCB97A942E89C71999491E3AFAC", Convert.ToHexString(movie, mp4.Length + 8, 16));
        Assert.True(AiMark.IsMarked(movie));
        // Its last box runs to the end (size 0): what is added would be in it, so it is left as it is.
        byte[] open = [.. mp4[..16], 0, 0, 0, 0, .. "mdat"u8, 1, 2, 3, 4];
        Assert.Equal(open, AiMark.Mark(open, "video/mp4"));

        // A WAV and an AVI: a _PMX chunk at the end (even in length), and the RIFF's size grown by it.
        foreach (var (form, type) in new[] { ("WAVE", "audio/wav"), ("AVI ", "video/x-msvideo") })
        {
            byte[] riff = [.. "RIFF"u8, 16, 0, 0, 0, .. Encoding.ASCII.GetBytes(form), .. "data"u8, 4, 0, 0, 0, 1, 2, 3, 4];
            var marked = AiMark.Mark(riff, type);
            Assert.Equal(riff[8..], marked[8..riff.Length]);
            Assert.Equal((uint)(marked.Length - 8), BitConverter.ToUInt32(marked, 4));
            Assert.Equal("_PMX", Encoding.ASCII.GetString(marked, riff.Length, 4));
            Assert.Equal(0, marked.Length % 2);
            Assert.True(AiMark.IsMarked(marked));
        }

        // A WebM: a Matroska tag at the end of its segment, and the segment's size grown by it; one of unknown size is not resized.
        byte[] header = [0x1A, 0x45, 0xDF, 0xA3, 0x84, 0x42, 0x86, 0x81, 0x01];
        byte[] webm = [.. header, 0x18, 0x53, 0x80, 0x67, 0x01, 0, 0, 0, 0, 0, 0, 5, 0x1F, 0x43, 0xB6, 0x75, 0x80];
        var clip = AiMark.Mark(webm, "video/webm");
        Assert.Equal(webm[..13], clip[..13]);
        var size = 0L;
        for (var i = 1; i < 8; i++)
        {
            size = (size << 8) | clip[13 + i];
        }
        Assert.Equal(0x01, clip[13]);
        Assert.Equal(clip.Length - 21, size);
        Assert.Equal(webm[21..], clip[21..webm.Length]);
        Assert.Equal([0x12, 0x54, 0xC3, 0x67], clip[webm.Length..(webm.Length + 4)]);
        Assert.Contains("DIGITAL_SOURCE_TYPE", Encoding.ASCII.GetString(clip), StringComparison.Ordinal);
        Assert.True(AiMark.IsMarked(clip));
        byte[] live = [.. header, 0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x1F, 0x43, 0xB6, 0x75, 0x80];
        var streamed = AiMark.Mark(live, "video/webm");
        Assert.Equal(live, streamed[..live.Length]);
        Assert.True(AiMark.IsMarked(streamed));
        // Its size says more than the file holds: left as it is.
        byte[] cut = [.. webm[..20], 9, .. webm[21..]];
        Assert.Equal(cut, AiMark.Mark(cut, "video/webm"));
    }

    private static global::System.Text.Json.JsonValueKind System_JsonNull() => global::System.Text.Json.JsonValueKind.Null;
}
