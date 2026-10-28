using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Arena's sign-in as Code Arena meets it: a password (and a second factor when asked) with a cookie session, a key of the
/// person's own made and revoked, whose the key is; every state-changing call carries X-Requested-With, as Arena's guard asks.
/// </summary>
internal sealed class FakeArena : FakeServer
{
    public string Password { get; set; } = "right";
    /// <summary>The key the next one made is (null: a new one each time); the gateway's own fake takes only its key.</summary>
    public string? NextKey { get; set; }
    public string? Code { get; set; }
    /// <summary>Keys made, by name, and those revoked.</summary>
    public Dictionary<string, string> Keys { get; } = [];
    public List<string> Revoked { get; } = [];
    public List<string> Calls { get; } = [];
    public bool SignedOut { get; private set; }
    private string? _session;
    private bool _half;

    protected override async Task HandleAsync(HttpListenerContext ctx, string body, CancellationToken ct)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var method = ctx.Request.HttpMethod;
        lock (Calls)
        {
            Calls.Add($"{method} {path}");
        }
        if (method != "GET" && ctx.Request.Headers["X-Requested-With"] is null)
        {
            await WriteJson(ctx, new JsonObject { ["status"] = "csrf", ["error"] = "Missing X-Requested-With header." }, 400);
            return;
        }
        var json = body.Length > 0 ? JsonNode.Parse(body) : null;
        var cookie = ctx.Request.Cookies["arena"]?.Value;
        var bearer = ctx.Request.Headers["Authorization"]?.Replace("Bearer ", "", StringComparison.Ordinal);
        switch (method, path)
        {
            case ("POST", "/api/auth/login"):
                if (json?["password"]?.GetValue<string>() != Password)
                {
                    await WriteJson(ctx, new JsonObject { ["status"] = "invalid", ["error"] = "Wrong username or password." }, 401);
                    return;
                }
                _session = Guid.NewGuid().ToString("N");
                _half = Code is not null;
                ctx.Response.Cookies.Add(new Cookie("arena", _session, "/"));
                await WriteJson(ctx, new JsonObject { ["status"] = _half ? "2fa" : "ok" });
                return;
            case ("POST", "/api/auth/login/2fa"):
                if (cookie != _session || json?["code"]?.GetValue<string>() != Code)
                {
                    await WriteJson(ctx, new JsonObject { ["status"] = "invalid", ["error"] = "Wrong code." }, 401);
                    return;
                }
                _half = false;
                await WriteJson(ctx, new JsonObject { ["status"] = "ok" });
                return;
            case ("POST", "/api/auth/logout"):
                (_session, SignedOut) = (null, true);
                ctx.Response.StatusCode = 204;
                return;
            case ("GET", "/api/code-arena/me"):
                await WriteJson(ctx, Keys.ContainsValue(bearer ?? "") ? new JsonObject { ["userName"] = "mo", ["displayName"] = "Mo", ["managesPeople"] = true } : new JsonObject { ["error"] = "no" }, Keys.ContainsValue(bearer ?? "") ? 200 : 401);
                return;
            case ("POST", "/api/code-arena/sign-out"):
                var name = Keys.FirstOrDefault(k => k.Value == bearer).Key;
                if (name is not null)
                {
                    Keys.Remove(name);
                    Revoked.Add(name);
                }
                ctx.Response.StatusCode = 204;
                return;
        }
        if (cookie is null || cookie != _session || _half)
        {
            await WriteJson(ctx, new JsonObject { ["status"] = "unauthorized", ["error"] = "Sign in." }, 401);
            return;
        }
        switch (method)
        {
            case "GET" when path == "/api/account/keys/own":
                await WriteJson(ctx, new JsonObject { ["keys"] = new JsonArray([.. Keys.Select(k => (JsonNode)new JsonObject { ["id"] = "id-" + k.Key, ["name"] = k.Key, ["kind"] = "own" })]) });
                return;
            case "POST" when path == "/api/account/keys/own":
                var key = NextKey ?? "sk-made-" + Guid.NewGuid().ToString("N")[..8];
                Keys[json!["name"]!.GetValue<string>()] = key;
                await WriteJson(ctx, new JsonObject { ["apiKey"] = key });
                return;
            case "DELETE" when path.StartsWith("/api/account/keys/own/id-", StringComparison.Ordinal):
                var gone = Uri.UnescapeDataString(path["/api/account/keys/own/id-".Length..]);
                Keys.Remove(gone);
                Revoked.Add(gone);
                ctx.Response.StatusCode = 204;
                return;
        }
        ctx.Response.StatusCode = 404;
    }
}

public sealed class SignInTests : IDisposable
{
    private readonly FakeArena _arena = new();

    public void Dispose() => _arena.Dispose();

    [Fact]
    public async Task Arena_s_sign_in_makes_a_key_for_this_machine_replacing_the_one_before_and_ends_its_session()
    {
        using (var first = new ArenaSignIn(new Uri(_arena.BaseUrl + "/"), null))
        {
            Assert.False(await first.PasswordAsync("mo", "right", CancellationToken.None));
            var key = await first.KeyAsync(CancellationToken.None);
            Assert.Equal(key, _arena.Keys[ArenaSignIn.KeyName]);
        }
        Assert.True(_arena.SignedOut);
        // Again on the same machine: the key before is revoked, a new one made.
        using (var again = new ArenaSignIn(new Uri(_arena.BaseUrl + "/"), null))
        {
            await again.PasswordAsync("mo", "right", CancellationToken.None);
            var second = await again.KeyAsync(CancellationToken.None);
            Assert.Equal([ArenaSignIn.KeyName], _arena.Revoked);
            Assert.Equal(second, Assert.Single(_arena.Keys).Value);
        }
        // A wrong password: Arena's words.
        using var wrong = new ArenaSignIn(new Uri(_arena.BaseUrl + "/"), null);
        var refused = await Assert.ThrowsAsync<ArenaSignInException>(() => wrong.PasswordAsync("mo", "wrong", CancellationToken.None));
        Assert.Equal("Wrong username or password.", refused.Message);
    }

    [Fact]
    public async Task The_ide_signs_in_with_a_password_and_second_factor_then_starts_again_with_the_key_and_signs_out()
    {
        (_arena.Code, _arena.NextKey) = ("123456", FakeGateway.Key);
        // Its Arena is this one; its gateway is the fake's own (kept: the same Arena).
        using var gateway = new FakeGateway();
        using var h = new Harness(gateway, config: c => c["url"] = _arena.BaseUrl);
        await using var web = await WebRun.StartAsync(h);
        var config = () => JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!;

        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/account/sign-in", new JsonObject { ["userName"] = "mo" })).StatusCode);
        var wrong = await web.PostAsync("/api/account/sign-in", new JsonObject { ["userName"] = "mo", ["password"] = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("Wrong username or password.", await wrong.Content.ReadAsStringAsync());

        var half = await web.PostAsync("/api/account/sign-in", new JsonObject { ["userName"] = "mo", ["password"] = "right" });
        Assert.Equal("2fa", JsonNode.Parse(await half.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
        // A wrong code may be typed again.
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.PostAsync("/api/account/sign-in/code", new JsonObject { ["code"] = "000000" })).StatusCode);
        var done = await web.PostAsync("/api/account/sign-in/code", new JsonObject { ["code"] = "123456" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var key = _arena.Keys[ArenaSignIn.KeyName];
        Assert.Equal(key, config()["apiKey"]!.GetValue<string>());
        Assert.Equal(_arena.BaseUrl, config()["url"]!.GetValue<string>().TrimEnd('/'));
        Assert.Equal(gateway.Url, config()["gateway"]!.GetValue<string>());

        // It starts again with the key: Arena says whose it is.
        JsonNode? account = null;
        for (var waited = 0; waited < 20_000 && account?["account"]?["userName"] is null; waited += 200)
        {
            await Task.Delay(200);
            try
            {
                account = await web.GetJsonAsync("/api/account");
            }
            catch (Exception e) when (e is HttpRequestException or Xunit.Sdk.XunitException)
            {
                // Between the two runs.
            }
        }
        Assert.Equal("mo", account!["account"]!["userName"]!.GetValue<string>());
        Assert.True(account["account"]!["managesPeople"]!.GetValue<bool>());

        // Signed out: the key revoked at Arena and forgotten here.
        var signedOut = JsonNode.Parse(await (await web.PostAsync("/api/account/sign-out", new JsonObject())).Content.ReadAsStringAsync())!;
        Assert.True(signedOut["revoked"]!.GetValue<bool>());
        Assert.Contains(ArenaSignIn.KeyName, _arena.Revoked);
        Assert.Null(config()["apiKey"]);
    }
}
