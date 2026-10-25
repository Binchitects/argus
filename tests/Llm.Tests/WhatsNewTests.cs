using System.Net;
using System.Net.Http.Json;

namespace Llm.Tests;

/// <summary>"What's new": the release whose tour a person has seen, kept with them so it opens once on every device.</summary>
[Collection(nameof(AppCollection))]
public sealed class WhatsNewTests(AppFixture app)
{
    [Fact]
    public async Task The_release_a_person_has_seen_is_kept_with_them()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "wn" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await b.JsonAsync(await b.GetAsync("/api/account/whats-new"))).GetProperty("seen").ValueKind);
        await StatusAssert.Is(HttpStatusCode.OK, await b.Http.PutAsJsonAsync(new Uri("/api/account/whats-new", UriKind.Relative), new { release = "7.0" }));
        Assert.Equal("7.0", (await b.JsonAsync(await b.GetAsync("/api/account/whats-new"))).GetProperty("seen").GetString());
        // Another sign-in of theirs (another device) has it too.
        var again = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        Assert.Equal("7.0", (await again.JsonAsync(await again.GetAsync("/api/account/whats-new"))).GetProperty("seen").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PutAsJsonAsync(new Uri("/api/account/whats-new", UriKind.Relative), new { release = "" }));
    }
}
