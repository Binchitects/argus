using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Notifications;

/// <summary>Configuration section "Push": Web Push to the installed app and the browsers.</summary>
public sealed class PushOptions
{
    /// <summary>Push services the app posts to (the browsers' own), comma separated; *.example.com for a domain and its subdomains.</summary>
    public string Hosts { get; set; } = "fcm.googleapis.com, *.push.services.mozilla.com, *.push.apple.com, *.notify.windows.com";

    /// <summary>Devices one person may have.</summary>
    public int PerPerson { get; set; } = 20;
}

/// <summary>What a push says: the bell's news, as the service worker shows it.</summary>
public sealed record PushMessage(string Title, string? Body, string? Link, string Kind, string Tag);

/// <summary>
/// Web Push: the bell's news on people's devices, the installed app's and the browsers'
/// (RFC 8030), even with no page open. The app signs each push with its own VAPID key
/// (RFC 8292: made on first use, kept encrypted under APP_DATA_KEY), and encrypts it for
/// the device (RFC 8291, aes128gcm): the push service carries it without reading it. Only
/// the browsers' push services are posted to (Push:Hosts), so a subscription cannot make
/// the app reach into the network. A device the push service no longer knows is removed.
/// </summary>
public sealed partial class WebPush(IServiceScopeFactory scopes, IHttpClientFactory http, IOptions<AuthOptions> auth, IOptionsMonitor<PushOptions> options,
    TimeProvider clock, ILogger<WebPush> logger) : IDisposable
{
    public const string Client = "webpush";

    /// <summary>The settings row that keeps the VAPID key (PKCS#8, encrypted); not a setting of the Settings page.</summary>
    public const string KeyRow = "webpush:vapid";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ECDsa? _key;

    /// <summary>Whether pushes can be signed: the key is kept encrypted, so APP_DATA_KEY must be set.</summary>
    public bool Available => !string.IsNullOrEmpty(auth.Value.DataKey);

    /// <summary>The app's VAPID public key (an uncompressed P-256 point, base64url), as browsers take it; null when pushes are not available.</summary>
    public async Task<string?> PublicKeyAsync(CancellationToken ct) => await KeyAsync(ct) is { } key ? Base64Url(Point(key.ExportParameters(false).Q)) : null;

    private async Task<ECDsa?> KeyAsync(CancellationToken ct)
    {
        if (_key is not null || !Available)
        {
            return _key;
        }
        await _lock.WaitAsync(ct);
        try
        {
            if (_key is not null)
            {
                return _key;
            }
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == KeyRow, ct) is { } row)
                {
                    if (SettingsCrypto.Decrypt(row.Value, auth.Value.DataKey) is not { } pkcs8)
                    {
                        // APP_DATA_KEY changed: the key is lost, and so are the subscriptions made with it.
                        LogKeyLost(logger);
                        return null;
                    }
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8), out _);
                    return _key = key;
                }
                using var made = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                db.Settings.Add(new Setting { Key = KeyRow, Value = SettingsCrypto.Encrypt(Convert.ToBase64String(made.ExportPkcs8PrivateKey()), auth.Value.DataKey!) });
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Another instance made one at the same moment: theirs is read on the next round.
                    db.ChangeTracker.Clear();
                }
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Why the app will not push to this address, or null.</summary>
    public string? Refusal(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            return "A push subscription's endpoint is an https:// address.";
        }
        return WebGuard.Allowed(u.Host, options.CurrentValue.Hosts) ? null : $"{u.Host} is not a push service the app posts to (Push:Hosts).";
    }

    /// <summary>Why these are not a browser's keys (a P-256 point and a 16-byte secret, base64url), or null.</summary>
    public static string? KeysRefusal(string? p256dh, string? authSecret)
    {
        try
        {
            var point = FromBase64Url(p256dh ?? "");
            if (point.Length != 65 || point[0] != 4 || FromBase64Url(authSecret ?? "").Length != 16)
            {
                return "The subscription's keys are not a browser's (p256dh: 65 bytes, auth: 16).";
            }
            using var _ = ECDiffieHellman.Create(PublicKey(point));
            return null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return "The subscription's keys are not a browser's (p256dh: 65 bytes, auth: 16).";
        }
    }

    /// <summary>Pushes to each of the person's devices in the background; nothing happens for a person without any.</summary>
    public void Queue(Guid userId, PushMessage message)
    {
        if (!Available)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(userId, message, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A push is a courtesy: the bell has the news anyway.
                LogFailed(logger, ex.Message);
            }
        });
    }

    /// <summary>Pushes to each of the person's devices. Returns how many took it and how many did not.</summary>
    public async Task<(int Sent, int Failed)> SendAsync(Guid userId, PushMessage message, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var devices = await db.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
        if (devices.Count == 0 || await KeyAsync(ct) is not { } key)
        {
            return (0, devices.Count);
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = Cut(message.Title, 200), body = message.Body is null ? null : Cut(message.Body, 600), link = message.Link, kind = message.Kind, tag = message.Tag,
        });
        var publicKey = Base64Url(Point(key.ExportParameters(false).Q));
        var subject = $"{auth.Value.Origin}/";
        var now = clock.GetUtcNow();
        var (sent, failed) = (0, 0);
        foreach (var device in devices)
        {
            if (Refusal(device.Endpoint) is { } refusal)
            {
                device.LastError = Cut(refusal, 500);
                failed++;
                continue;
            }
            try
            {
                var endpoint = new Uri(device.Endpoint);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new ByteArrayContent(Encrypt(payload, FromBase64Url(device.P256dh), FromBase64Url(device.Auth))),
                };
                request.Content.Headers.ContentType = new("application/octet-stream");
                request.Content.Headers.ContentEncoding.Add("aes128gcm");
                request.Headers.Add("TTL", "86400");
                request.Headers.Add("Urgency", message.Kind is "alert" or "answer" ? "high" : "normal");
                request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={Vapid(key, endpoint.GetLeftPart(UriPartial.Authority), subject, now)}, k={publicKey}");
                using var response = await http.CreateClient(Client).SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    // The browser unsubscribed, or the push service forgot it: it is gone for good.
                    db.PushSubscriptions.Remove(device);
                    failed++;
                }
                else if (!response.IsSuccessStatusCode)
                {
                    device.LastError = Cut($"The push service said {(int)response.StatusCode} {response.ReasonPhrase}", 500);
                    failed++;
                }
                else
                {
                    device.LastSentAt = now;
                    device.LastError = null;
                    sent++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or CryptographicException)
            {
                device.LastError = Cut(ex.Message, 500);
                failed++;
            }
        }
        await db.SaveChangesAsync(ct);
        return (sent, failed);
    }

    /// <summary>
    /// The push message for one device, per RFC 8291 and RFC 8188 (aes128gcm, one record): a
    /// new key pair of the app's (<paramref name="serverKey"/> in tests) agrees a secret with
    /// the browser's key, which with the browser's auth secret and a random salt derives the
    /// content key and nonce.
    /// </summary>
    public static byte[] Encrypt(byte[] payload, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? serverKey = null, byte[]? salt = null)
    {
        using var made = serverKey is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var server = serverKey ?? made!;
        var asPublic = Point(server.ExportParameters(false).Q);
        using var browser = ECDiffieHellman.Create(PublicKey(uaPublic));
        var shared = server.DeriveRawSecretAgreement(browser.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, [.. "WebPush: info"u8, 0, .. uaPublic, .. asPublic]);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, [.. "Content-Encoding: aes128gcm"u8, 0]);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, [.. "Content-Encoding: nonce"u8, 0]);
        // One record, the last: the payload and its delimiter 0x02, no padding.
        byte[] plain = [.. payload, 2];
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        var header = new byte[16 + 4 + 1];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 4096);
        header[20] = (byte)asPublic.Length;
        return [.. header, .. asPublic, .. cipher, .. tag];
    }

    /// <summary>The VAPID token for a push service (RFC 8292): an ES256 JWT for its origin, valid twelve hours.</summary>
    public static string Vapid(ECDsa key, string audience, string subject, DateTimeOffset now)
    {
        var header = Base64Url("""{"typ":"JWT","alg":"ES256"}"""u8.ToArray());
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = now.AddHours(12).ToUnixTimeSeconds(), sub = subject }));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{Base64Url(signature)}";
    }

    private static ECParameters PublicKey(byte[] point) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] },
    };

    private static byte[] Point(ECPoint q) => [4, .. q.X!, .. q.Y!];

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var b = s.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _lock.Dispose();
        _key?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Web Push: a push could not be sent: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Web Push: the VAPID key cannot be decrypted (APP_DATA_KEY changed); pushes are off until the webpush:vapid row is removed and people subscribe again")]
    private static partial void LogKeyLost(ILogger logger);
}
