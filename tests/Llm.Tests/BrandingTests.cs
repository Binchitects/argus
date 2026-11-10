using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The app's icon an admin chooses beside its name (Settings → Branding).</summary>
[Collection(nameof(AppCollection))]
public sealed class BrandingTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("branding_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());

    /// <summary>The smallest PNG there is: one transparent pixel.</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent File(byte[] data, string name) => new() { { new ByteArrayContent(data), "file", name } };

    [Fact]
    public async Task An_admin_chooses_the_apps_icon_everyone_sees_it_by_an_address_that_changes_with_it_and_can_go_back_to_the_apps_own()
    {
        await using var f = NewApp();
        var anyone = f.CreateClient();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        // None chosen: the app's own.
        var info = JsonDocument.Parse(await anyone.GetStringAsync("/api/info")).RootElement;
        Assert.Equal(JsonValueKind.Null, info.GetProperty("icon").ValueKind);
        await StatusAssert.Is(HttpStatusCode.NotFound, await anyone.GetAsync("/api/branding/icon"));

        // What is not a picture, or is too big, is refused, whatever it is called.
        using (var form = File(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "icon.svg"))
        {
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsync(new Uri("/api/admin/branding/icon", UriKind.Relative), form));
        }
        using (var form = File([.. Png, .. new byte[Llm.Api.Settings.BrandingEndpoints.MostIconBytes]], "big.png"))
        {
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsync(new Uri("/api/admin/branding/icon", UriKind.Relative), form));
        }

        JsonElement saved;
        using (var form = File(Png, "logo.png"))
        {
            saved = await admin.JsonAsync(await admin.Http.PutAsync(new Uri("/api/admin/branding/icon", UriKind.Relative), form));
        }
        Assert.Equal("image/png", saved.GetProperty("type").GetString());
        var url = saved.GetProperty("url").GetString()!;
        info = JsonDocument.Parse(await anyone.GetStringAsync("/api/info")).RootElement;
        Assert.Equal(url, info.GetProperty("icon").GetString());
        var got = await anyone.GetAsync(url);
        Assert.Equal("image/png", got.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await got.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", got.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        Assert.Contains("sandbox", got.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        // The home screen's icon too: the chosen one first.
        var manifest = JsonDocument.Parse(await anyone.GetStringAsync("/api/app/manifest.webmanifest")).RootElement;
        Assert.Equal(url, manifest.GetProperty("icons")[0].GetProperty("src").GetString());

        // An SVG is taken, by what it is; a new icon, a new address.
        using (var form = File("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><rect width=\"1\" height=\"1\"/></svg>"u8.ToArray(), "logo"))
        {
            saved = await admin.JsonAsync(await admin.Http.PutAsync(new Uri("/api/admin/branding/icon", UriKind.Relative), form));
        }
        Assert.Equal("image/svg+xml", saved.GetProperty("type").GetString());
        Assert.NotEqual(url, saved.GetProperty("url").GetString());

        // A person who may not change the branding cannot change it.
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Kim" }));
        var kim = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        using (var form = File(Png, "mine.png"))
        {
            await StatusAssert.Is(HttpStatusCode.Forbidden, await kim.Http.PutAsync(new Uri("/api/admin/branding/icon", UriKind.Relative), form));
        }
        await StatusAssert.Is(HttpStatusCode.Forbidden, await kim.Http.DeleteAsync(new Uri("/api/admin/branding/icon", UriKind.Relative)));

        // Back to the app's own, and said in the audit log.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri("/api/admin/branding/icon", UriKind.Relative)));
        info = JsonDocument.Parse(await anyone.GetStringAsync("/api/info")).RootElement;
        Assert.Equal(JsonValueKind.Null, info.GetProperty("icon").ValueKind);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("branding.icon", audit);
        Assert.Contains("branding.icon.reset", audit);
    }
}
