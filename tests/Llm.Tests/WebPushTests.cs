using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Api.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The installable app: its manifest, and the bell's news pushed to each device (Web Push, RFC 8291 and RFC 8292).</summary>
[Collection(nameof(AppCollection))]
public sealed class WebPushTests(AppFixture app) : IDisposable
{
    private readonly FakeChatPlatforms _push = new();

    private WebApplicationFactory<Program> NewApp(string? dataKey = "a-data-key-for-push-tests") =>
        app.Create(app.ConnectionStringFor("push_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Auth:DataKey"] = dataKey })
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddHttpClient(WebPush.Client).ConfigurePrimaryHttpMessageHandler(() => _push)));

    public void Dispose() => _push.Dispose();

    private static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "p" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (b, (await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid());
    }

    /// <summary>A browser's subscription: its own key pair and auth secret, as pushManager.subscribe() makes them.</summary>
    private sealed class Browser : IDisposable
    {
        public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        public byte[] Auth { get; } = RandomNumberGenerator.GetBytes(16);

        public byte[] Public
        {
            get
            {
                var q = Key.ExportParameters(false).Q;
                return [4, .. q.X!, .. q.Y!];
            }
        }

        public object Subscription(string endpoint) => new { endpoint, keys = new { p256dh = WebPush.Base64Url(Public), auth = WebPush.Base64Url(Auth) } };

        /// <summary>Reads a push as the browser does (RFC 8291): the server's key from the header, the shared secret, then AES-128-GCM.</summary>
        public string Decrypt(byte[] body)
        {
            var salt = body[..16];
            Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
            var idLength = body[20];
            var serverPublic = body[21..(21 + idLength)];
            using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] } });
            var shared = Key.DeriveRawSecretAgreement(server.PublicKey);
            var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Auth, [.. "WebPush: info"u8, 0, .. Public, .. serverPublic]);
            var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, [.. "Content-Encoding: aes128gcm"u8, 0]);
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, [.. "Content-Encoding: nonce"u8, 0]);
            var record = body[(21 + idLength)..];
            var plain = new byte[record.Length - 16];
            using (var aes = new AesGcm(cek, 16))
            {
                aes.Decrypt(nonce, record[..^16], record[^16..], plain);
            }
            Assert.Equal(2, plain[^1]);
            return Encoding.UTF8.GetString(plain[..^1]);
        }

        public void Dispose() => Key.Dispose();
    }

    [Fact]
    public void The_encryption_matches_RFC_8291s_test_vector()
    {
        static byte[] B(string s) => WebPush.FromBase64Url(s);
        // RFC 8291, section 5: the application server's key pair, the browser's, its auth secret and the salt.
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = B("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint
            {
                X = B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")[1..33],
                Y = B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8")[33..65],
            },
        });
        var body = WebPush.Encrypt("When I grow up, I want to be a watermelon"u8.ToArray(),
            B("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"), B("BTBZMqHH6r4Tts7J_aSIgg"), server, B("DGv6ra1nlYgDCS1FRnbzlw"));
        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            WebPush.Base64Url(body));
    }

    [Fact]
    public async Task A_device_subscribes_gets_the_bells_news_pushed_signed_and_encrypted_and_unsubscribes()
    {
        await using var f = NewApp();
        var (b, id) = await PersonAsync(f);
        using var browser = new Browser();
        var push = await b.JsonAsync(await b.GetAsync("/api/push"));
        Assert.True(push.GetProperty("available").GetBoolean());
        var vapid = WebPush.FromBase64Url(push.GetProperty("publicKey").GetString()!);
        Assert.Equal(65, vapid.Length);
        // The key is kept: asked again (or by another instance), it is the same.
        Assert.Equal(push.GetProperty("publicKey").GetString(), (await b.JsonAsync(await b.GetAsync("/api/push"))).GetProperty("publicKey").GetString());

        // Only the browsers' push services, and only a browser's keys.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/push/subscriptions", browser.Subscription("https://internal.example.test/push")));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/push/subscriptions", browser.Subscription("http://fcm.googleapis.com/fcm/send/x")));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/push/subscriptions",
            new { endpoint = "https://fcm.googleapis.com/fcm/send/x", keys = new { p256dh = WebPush.Base64Url(new byte[65]), auth = WebPush.Base64Url(browser.Auth) } }));
        b.Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36");
        var device = await b.JsonAsync(await b.PostAsync("/api/push/subscriptions", browser.Subscription("https://fcm.googleapis.com/fcm/send/device-1")));
        Assert.Equal("Chrome on Android", device.GetProperty("device").GetString());

        // News in the bell (here: an answer that ended while away) is pushed to the device.
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Notifier>().SendAsync(id, new News("answer", "Answer ready: Parser", "It splits the input.", "/chat/123"));
        }
        var sent = await _push.WaitAsync("fcm.googleapis.com", "/fcm/send/device-1");
        Assert.Equal("aes128gcm", sent.Headers["Content-Encoding"]);
        Assert.Equal("86400", sent.Headers["TTL"]);
        var news = JsonDocument.Parse(browser.Decrypt(sent.Bytes)).RootElement;
        Assert.Equal("Answer ready: Parser", news.GetProperty("title").GetString());
        Assert.Equal("It splits the input.", news.GetProperty("body").GetString());
        Assert.Equal("/chat/123", news.GetProperty("link").GetString());
        Assert.Equal("answer", news.GetProperty("kind").GetString());

        // Signed with the app's VAPID key, for the push service's origin.
        var authorization = sent.Headers["Authorization"];
        Assert.StartsWith("vapid t=", authorization, StringComparison.Ordinal);
        var parts = authorization["vapid ".Length..].Split(", ").ToDictionary(p => p[..1], p => p[2..]);
        Assert.Equal(push.GetProperty("publicKey").GetString(), parts["k"]);
        var jwt = parts["t"].Split('.');
        var claims = JsonDocument.Parse(WebPush.FromBase64Url(jwt[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.Equal("https://llm.test/", claims.GetProperty("sub").GetString());
        using (var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = vapid[1..33], Y = vapid[33..65] } }))
        {
            Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), WebPush.FromBase64Url(jwt[2]), HashAlgorithmName.SHA256));
        }

        // A test push reaches each device; the list says when it last did.
        Assert.Equal(1, (await b.JsonAsync(await b.PostAsync("/api/push/test"))).GetProperty("sent").GetInt32());
        var listed = Assert.Single((await b.JsonAsync(await b.GetAsync("/api/push"))).GetProperty("devices").EnumerateArray());
        Assert.Equal(JsonValueKind.String, listed.GetProperty("lastSentAt").ValueKind);

        // Someone else cannot remove it; its owner can.
        var (other, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.DeleteAsync(new Uri($"/api/push/subscriptions/{device.GetProperty("id").GetString()}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/push/subscriptions/{device.GetProperty("id").GetString()}", UriKind.Relative)));
        Assert.Empty((await b.JsonAsync(await b.GetAsync("/api/push"))).GetProperty("devices").EnumerateArray());
    }

    [Fact]
    public async Task A_device_the_push_service_forgot_is_removed_and_without_a_data_key_there_are_no_pushes()
    {
        await using var f = NewApp();
        var (b, _) = await PersonAsync(f);
        using var browser = new Browser();
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync("/api/push/subscriptions", browser.Subscription("https://updates.push.services.mozilla.com/wpush/v2/gone")));
        _push.PushAnswer = HttpStatusCode.Gone;
        var result = await b.JsonAsync(await b.PostAsync("/api/push/test"));
        Assert.Equal(0, result.GetProperty("sent").GetInt32());
        Assert.Equal(1, result.GetProperty("failed").GetInt32());
        Assert.Empty((await b.JsonAsync(await b.GetAsync("/api/push"))).GetProperty("devices").EnumerateArray());

        await using var keyless = NewApp(dataKey: null);
        var (k, _) = await PersonAsync(keyless);
        var view = await k.JsonAsync(await k.GetAsync("/api/push"));
        Assert.False(view.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("publicKey").ValueKind);
        await StatusAssert.Is(HttpStatusCode.Conflict, await k.PostAsync("/api/push/subscriptions", browser.Subscription("https://fcm.googleapis.com/fcm/send/x")));
    }

    [Fact]
    public async Task The_web_manifest_names_the_product_and_its_icons()
    {
        var res = await app.Factory.CreateClient().GetAsync(new Uri("/api/app/manifest.webmanifest", UriKind.Relative));
        Assert.Equal("application/manifest+json", res.Content.Headers.ContentType?.MediaType);
        var manifest = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Argus Arena", manifest.GetProperty("name").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        Assert.Equal("/", manifest.GetProperty("start_url").GetString());
        var icons = manifest.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "512x512" && i.GetProperty("purpose").GetString() == "maskable");
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "192x192");
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36 Edg/129.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox on macOS")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", "Safari on iOS")]
    [InlineData("curl/8", "A browser")]
    public void A_device_is_named_as_people_call_it(string userAgent, string name) => Assert.Equal(name, PushEndpoints.DeviceName(userAgent));
}
