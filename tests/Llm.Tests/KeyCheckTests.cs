using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Argus asks the app whose an API key is (/api/authz/key): from inside the network, with its credential.</summary>
[Collection(nameof(AppCollection))]
public sealed class KeyCheckTests(AppFixture app)
{
    /// <summary>Argus calling http://app:8080 directly: no proxy in between, so no forwarded headers.</summary>
    private HttpClient Inside() => app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://app:8080"), AllowAutoRedirect = false });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> AskAsync(HttpClient http, object? body, string? credential = FakeArgus.ChatToken,
        IEnumerable<(string, string)>? headers = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/authz/key", UriKind.Relative)) { Content = JsonContent.Create(body) };
        if (credential is not null)
        {
            req.Headers.Authorization = new("Bearer", credential);
        }
        foreach (var (name, value) in headers ?? [])
        {
            req.Headers.Add(name, value);
        }
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    /// <summary>A new person and the API key they were given.</summary>
    private async Task<(Guid Id, string Name, string Key)> PersonAsync()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (made.GetProperty("id").GetGuid(), name, made.GetProperty("apiKey").GetString()!);
    }

    [Fact]
    public async Task Argus_learns_whose_an_api_key_is_and_the_gateway_is_asked_once_a_minute()
    {
        var (_, name, key) = await PersonAsync();
        var http = Inside();

        var (status, body) = await AskAsync(http, new { key });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($"{name}@example.test", body.GetProperty("email").GetString());
        Assert.Equal(name, body.GetProperty("username").GetString());

        // ARGUS_KEY is Argus's credential too; the second answer comes from the app's short memory.
        (status, body) = await AskAsync(http, new { key }, FakeArgus.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(name, body.GetProperty("username").GetString());
        Assert.Equal(1, app.Gateway.KeyLookups.Count(k => k == key));
    }

    [Fact]
    public async Task An_unknown_blocked_or_expired_key_is_refused()
    {
        var http = Inside();
        var (status, body) = await AskAsync(http, new { key = "sk-not-a-key-anyone-has" });
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("invalid_key", body.GetProperty("status").GetString());
        Assert.Contains("not valid", body.GetProperty("error").GetString(), StringComparison.Ordinal);

        var (_, _, blocked) = await PersonAsync();
        app.Gateway.Keys.Values.Single(k => k.Secret == blocked).Blocked = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(http, new { key = blocked })).Status);

        var (_, _, expired) = await PersonAsync();
        app.Gateway.Keys.Values.Single(k => k.Secret == expired).Expires = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(http, new { key = expired })).Status);
    }

    [Fact]
    public async Task A_disabled_person_is_refused_at_once_even_while_the_gateway_still_takes_the_key()
    {
        var (id, name, key) = await PersonAsync();
        var http = Inside();
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(http, new { key })).Status);

        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/people/{id}", UriKind.Relative), new { disabled = true });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        // Disabling blocks the key at the gateway; were that to fail, the person is still refused.
        app.Gateway.KeysOf($"{name}@example.test").Single().Blocked = false;
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(http, new { key })).Status);
    }

    [Fact]
    public async Task The_check_answers_only_argus_inside_the_network()
    {
        var (_, _, key) = await PersonAsync();
        var http = Inside();

        // Through Traefik, with the right credential even: refused.
        (string, string)[] traefik = [("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Host", "llm.test"), ("X-Forwarded-Proto", "https"),
            ("X-Forwarded-Port", "443"), ("X-Forwarded-Server", "traefik"), ("X-Real-Ip", "203.0.113.9")];
        var (status, body) = await AskAsync(http, new { key }, headers: traefik);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("not through the proxy", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        // Any one of them is enough, including what the forwarded-headers step leaves (X-Original-*).
        Assert.Equal(HttpStatusCode.Forbidden, (await AskAsync(http, new { key }, headers: [("X-Forwarded-Host", "llm.test")])).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await AskAsync(http, new { key }, headers: [("X-Real-Ip", "203.0.113.9")])).Status);

        // A wrong credential says so, apart from a refused key: Argus must not take it as the key's fault.
        (status, body) = await AskAsync(http, new { key }, credential: "not-the-credential");
        Assert.Equal((HttpStatusCode.Unauthorized, "credential"), (status, body.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(http, new { key }, credential: null)).Status);
        // The key itself is no credential here.
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(http, new { key }, credential: key)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AskAsync(http, new { }, FakeArgus.ChatToken)).Status);
    }
}
