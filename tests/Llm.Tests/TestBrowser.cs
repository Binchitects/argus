using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>A browser against the app: its own cookies and its own client address, no auto-redirects.</summary>
public sealed class TestBrowser
{
    private static int _next = 10;

    public HttpClient Http { get; }
    public string Ip { get; }

    public TestBrowser(WebApplicationFactory<Program> factory, string? ip = null)
    {
        var n = Interlocked.Increment(ref _next); // one read: parallel browsers must never share an address
        Ip = ip ?? $"10.{n / 62500 % 250}.{n / 250 % 250}.{n % 250 + 1}";
        Http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"https://{AppFixture.Domain}"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        Http.DefaultRequestHeaders.Add("X-Requested-With", "test");
        Http.DefaultRequestHeaders.Add("X-Forwarded-For", Ip);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) =>
        Http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body ?? new { });

    public Task<HttpResponseMessage> GetAsync(string path) => Http.GetAsync(new Uri(path, UriKind.Relative));

    public async Task<JsonElement> JsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    public async Task<HttpResponseMessage> LoginAsync(string user, string password, bool remember = false) =>
        await PostAsync("/api/auth/login", new { userName = user, password, remember });

    public async Task<TestBrowser> SignedInAsync(string user, string password)
    {
        var res = await LoginAsync(user, password);
        Assert.True(res.IsSuccessStatusCode, $"sign-in as {user}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return this;
    }

    public static AuthenticationHeaderValue Basic(string id, string secret) =>
        new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(id)}:{Uri.EscapeDataString(secret)}")));
}

public static class Totp
{
    /// <summary>RFC 6238 code for a base32 key, as an authenticator app would show it.</summary>
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32(base32Key.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant());
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(msg);
        }
        var hash = HMACSHA1.HashData(key, msg);
        var o = hash[^1] & 0xf;
        var bin = ((hash[o] & 0x7f) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in s.TrimEnd('='))
        {
            value = (value << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. output];
    }
}

public static class StatusAssert
{
    public static async Task Is(HttpStatusCode expected, HttpResponseMessage res) =>
        Assert.True(res.StatusCode == expected, $"expected {(int)expected}, got {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
}
