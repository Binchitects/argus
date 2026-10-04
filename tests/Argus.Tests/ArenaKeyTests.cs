using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Server;
using Argus.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Argus.Tests;

/// <summary>
/// The platform around Argus on one real socket: the app's key check
/// (/api/authz/key, as Llm.Api answers it) and the GitLab Argus reads with its
/// service token (users and project members).
/// </summary>
public sealed class FakePlatform : IAsyncDisposable
{
    public const string Credential = "chat-secret";
    readonly WebApplication _app;
    public string Url { get; }
    public string KeyCheckUrl => Url + "/api/authz/key";
    /// <summary>Keys the app knows: key -> (email, username).</summary>
    public ConcurrentDictionary<string, (string Email, string Username)> Keys { get; } = new() { ["sk-alice"] = ("alice@corp.example", "alice") };
    /// <summary>Every key check asked: the credential it came with and the key.</summary>
    public ConcurrentQueue<(string Auth, string Key)> Checks { get; } = new();
    /// <summary>The app answers 503 (its gateway down).</summary>
    public bool Down { get; set; }

    public int ChecksOf(string key) => Checks.Count(c => c.Key == key);

    public FakePlatform()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _app = b.Build();
        _app.MapPost("/api/authz/key", async (HttpContext c) =>
        {
            var key = (await JsonNode.ParseAsync(c.Request.Body))?["key"]?.ToString() ?? "";
            var auth = c.Request.Headers.Authorization.ToString();
            Checks.Enqueue((auth, key));
            if (auth != $"Bearer {Credential}") return Results.Json(new { status = "credential", error = "Wrong or missing credential." }, statusCode: 401);
            if (Down) return Results.Json(new { status = "gateway", error = "The gateway could not be asked." }, statusCode: 503);
            return Keys.TryGetValue(key, out var who)
                ? Results.Json(new { email = who.Email, username = who.Username })
                : Results.Json(new { status = "invalid_key", error = "That API key is not valid." }, statusCode: 401);
        });
        // GitLab: alice keeps her email private, so only her username finds her; bob maintains both projects.
        var users = new[]
        {
            new JsonObject { ["id"] = 5, ["username"] = "alice", ["name"] = "Alice", ["state"] = "active" },
            new JsonObject { ["id"] = 7, ["username"] = "bob", ["name"] = "Bob", ["state"] = "active", ["public_email"] = "bob@corp.example" },
        };
        _app.MapGet("/api/v4/users", (string? search, string? username) =>
        {
            var hits = users.Where(u => (search is not null && u["public_email"]?.ToString() == search) || (username is not null && u["username"]!.ToString() == username));
            return Results.Text(new JsonArray([.. hits.Select(u => (JsonNode)u.DeepClone())]).ToJsonString(), "application/json");
        });
        var members = new Dictionary<long, (long Id, string Username, string Name, int Level)[]>
        {
            [11] = [(5, "alice", "Alice", 30), (7, "bob", "Bob", 40)],
            [12] = [(7, "bob", "Bob", 40)],
        };
        _app.MapGet("/api/v4/projects/{id:long}/members/all", (long id, int page) =>
        {
            var list = page == 1 && members.TryGetValue(id, out var m) ? m : [];
            return Results.Text(new JsonArray([.. list.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Id, ["username"] = x.Username, ["name"] = x.Name, ["access_level"] = x.Level, ["state"] = "active",
            })]).ToJsonString(), "application/json");
        });
        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Small MCP client steps over a test server's HttpClient.</summary>
static class McpCalls
{
    public static HttpRequestMessage Request(object body, string? token, string? session = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session is not null) req.Headers.Add("mcp-session-id", session);
        return req;
    }

    public static JsonNode Rpc(string body)
    {
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonNode.Parse(line[6..])!;
        return JsonNode.Parse(body)!;
    }

    public static async Task<HttpResponseMessage> Initialize(HttpClient http, string token) =>
        await http.SendAsync(Request(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } },
        }, token));

    public static async Task<string> Session(HttpClient http, string token)
    {
        var resp = await Initialize(http, token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var session = resp.Headers.GetValues("mcp-session-id").Single();
        await http.SendAsync(Request(new { jsonrpc = "2.0", method = "notifications/initialized" }, token, session));
        return session;
    }

    public static async Task<JsonNode> Call(HttpClient http, string token, string session, string tool, object args)
    {
        var resp = await http.SendAsync(Request(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = tool, arguments = args } }, token, session));
        return Rpc(await resp.Content.ReadAsStringAsync())["result"]!;
    }
}

/// <summary>Argus in the platform (ARGUS_KEY_CHECK_URL set): coding agents bring the person's Arena API key.</summary>
[Collection("process-state")]
public sealed class ArenaKeyServerTests : IAsyncLifetime
{
    readonly FakePlatform _platform = new();
    readonly TestIndex _ix = new();
    readonly StringWriter _log = new();
    EnvScope _env = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _env = new EnvScope(("ARGUS_KEY_CHECK_URL", _platform.KeyCheckUrl), ("ARGUS_CHAT_CLIENT_TOKEN", FakePlatform.Credential),
            ("ARGUS_APP", "off"), ("ARGUS_ACCESS_NOTICES", "1"), ("ARGUS_AUDIT_LOG", "1"), ("ARGUS_INDEX_INTERVAL", "0"), ("ARGUS_USERS_FILE", null));
        AuditLog.Writer = _log;
        var alpha = _ix.Repo(11, "grp/alpha");
        _ix.Symbol(alpha, _ix.File(alpha, "src/decode.c", "int DecodeFrame(int x) { return x; }\n"), "DecodeFrame");
        var hidden = _ix.Repo(12, "grp/hidden");
        _ix.Symbol(hidden, _ix.File(hidden, "h.c", "int Hidden(void);\n"), "Hidden");
        // A GitLab token that would work in a standalone Argus: its access is cached already.
        Writes.UpsertAclCache(_ix.Conn, Acl.Hash("glpat-alice"), 5, "alice", $"[{alpha}]", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var cfg = _ix.Config() with { GitLab = GitLabConfig.Create(_platform.Url, "svc") };
        _app = ArgusServer.Create(cfg, configure: b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri("http://localhost:7700");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        AuditLog.Writer = null;
        _env.Dispose();
        _ix.Dispose();
        await _platform.DisposeAsync();
    }

    List<JsonObject> Lines(string @event) =>
        [.. _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l) as JsonObject).OfType<JsonObject>()
            .Where(o => o["event"]?.ToString() == @event)];

    [Fact]
    public async Task An_api_key_is_checked_with_the_app_and_argus_answers_as_that_persons_gitlab_user()
    {
        var session = await McpCalls.Session(_http, "sk-alice");
        var found = await McpCalls.Call(_http, "sk-alice", session, "find_symbol", new { name = "DecodeFrame" });
        Assert.False(found["isError"]!.GetValue<bool>());
        Assert.Equal("DecodeFrame", found["structuredContent"]!["result"]![0]!["name"]!.GetValue<string>());

        // Asked once, with the chat client's credential; the next requests came from the cache.
        var check = Assert.Single(_platform.Checks);
        Assert.Equal(($"Bearer {FakePlatform.Credential}", "sk-alice"), check);

        using (var audit = Db.ConnectAudit(_ix.DbPath))
            Assert.Equal("alice", Util.Sql.Scalar(audit, "SELECT username FROM audit WHERE tool = 'find_symbol'"));
        var line = Lines("tool_call").Single();
        Assert.Equal(("alice", "api_key"), (line["user"]!.ToString(), line["via"]!.ToString()));
        Assert.DoesNotContain("sk-alice", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_person_cannot_read_still_names_its_gitlab_maintainers()
    {
        var session = await McpCalls.Session(_http, "sk-alice");
        var notice = await McpCalls.Call(_http, "sk-alice", session, "find_symbol", new { name = "Hidden" });
        Assert.True(notice["isError"]!.GetValue<bool>());
        var text = notice["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Nothing you have access to matches this", text, StringComparison.Ordinal);
        Assert.Contains("- grp/hidden (1 match) -- maintainers: @bob (Bob)", text, StringComparison.Ordinal);
        Assert.Contains("ask a maintainer listed above to add them in GitLab", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_key_is_401_and_the_app_is_not_asked_again_at_once()
    {
        for (var i = 0; i < 2; i++)
        {
            var resp = await McpCalls.Initialize(_http, "sk-nobody");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("Bearer", resp.Headers.WwwAuthenticate.Single().Scheme);
            Assert.Equal(ArenaKeys.Refused, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        }
        Assert.Equal(1, _platform.ChecksOf("sk-nobody"));
        var denied = Lines("denied").Last();
        Assert.Equal(("token_rejected", "api_key"), (denied["reason"]!.ToString(), denied["via"]!.ToString()));
    }

    [Fact]
    public async Task GitLab_tokens_are_refused_with_how_to_connect_instead()
    {
        var resp = await McpCalls.Initialize(_http, "glpat-alice");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var error = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["error"]!.GetValue<string>();
        Assert.StartsWith("Connect with your Arena API key (Your account → API key) as 'Authorization: Bearer sk-...'", error, StringComparison.Ordinal);
        // A GitLab token is never sent to the app either.
        Assert.Empty(_platform.Checks);
    }
}

/// <summary>The key check itself: what is cached, for how long, and what never blames the key.</summary>
public sealed class ArenaKeyCheckTests : IAsyncLifetime
{
    readonly FakePlatform _platform = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _platform.DisposeAsync();

    [Fact]
    public async Task A_good_key_is_remembered_five_minutes_and_a_refused_one_thirty_seconds()
    {
        double now = 1000;
        var keys = new ArenaKeys(_platform.KeyCheckUrl, FakePlatform.Credential, now: () => now);
        Assert.Equal(new ArenaKeys.Person("alice@corp.example", "alice"), await keys.CheckAsync("sk-alice"));
        now += 299;
        await keys.CheckAsync("sk-alice");
        Assert.Equal(1, _platform.ChecksOf("sk-alice"));
        now += 2;
        await keys.CheckAsync("sk-alice");
        Assert.Equal(2, _platform.ChecksOf("sk-alice"));

        var refused = await Assert.ThrowsAsync<AclDenied>(() => keys.CheckAsync("sk-bob"));
        Assert.Equal(ArenaKeys.Refused, refused.Message);
        // Made a moment later: still refused from memory, then taken.
        _platform.Keys["sk-bob"] = ("bob@corp.example", "bob");
        now += 29;
        await Assert.ThrowsAsync<AclDenied>(() => keys.CheckAsync("sk-bob"));
        Assert.Equal(1, _platform.ChecksOf("sk-bob"));
        now += 2;
        Assert.Equal("bob", (await keys.CheckAsync("sk-bob")).Username);
    }

    [Fact]
    public async Task An_app_that_cannot_answer_is_not_remembered_and_never_blames_the_key()
    {
        var keys = new ArenaKeys(_platform.KeyCheckUrl, FakePlatform.Credential);
        _platform.Down = true;
        Assert.Equal(ArenaKeys.CannotCheck, (await Assert.ThrowsAsync<AclDenied>(() => keys.CheckAsync("sk-alice"))).Message);
        _platform.Down = false;
        Assert.Equal("alice", (await keys.CheckAsync("sk-alice")).Username);

        // Argus with the wrong credential: the operator's mistake, not the key's.
        var misconfigured = new ArenaKeys(_platform.KeyCheckUrl, "not-the-credential");
        Assert.Equal(ArenaKeys.CannotCheck, (await Assert.ThrowsAsync<AclDenied>(() => misconfigured.CheckAsync("sk-alice"))).Message);
        var unreachable = new ArenaKeys("http://127.0.0.1:9/api/authz/key", FakePlatform.Credential);
        Assert.Equal(ArenaKeys.CannotCheck, (await Assert.ThrowsAsync<AclDenied>(() => unreachable.CheckAsync("sk-alice"))).Message);
    }
}

/// <summary>A standalone Argus (no ARGUS_KEY_CHECK_URL): GitLab tokens, as before.</summary>
[Collection("process-state")]
public sealed class StandaloneTokenTests : IDisposable
{
    readonly TestIndex _ix = new();
    readonly StringWriter _log = new();
    readonly EnvScope _env = new(("ARGUS_KEY_CHECK_URL", null), ("ARGUS_APP", "off"), ("ARGUS_ACCESS_NOTICES", "0"), ("ARGUS_AUDIT_LOG", "1"),
        ("ARGUS_INDEX_INTERVAL", "0"));
    readonly WebApplication _app;
    readonly HttpClient _http;

    public StandaloneTokenTests()
    {
        AuditLog.Writer = _log;
        var repo = _ix.Repo(11, "grp/alpha");
        _ix.Symbol(repo, _ix.File(repo, "src/decode.c", "int DecodeFrame(int x) { return x; }\n"), "DecodeFrame");
        Writes.UpsertAclCache(_ix.Conn, Acl.Hash("glpat-alice"), 5, "alice", $"[{repo}]", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _app = ArgusServer.Create(_ix.Config(), configure: b => b.WebHost.UseTestServer());
        _app.StartAsync().GetAwaiter().GetResult();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri("http://localhost:7700");
    }

    public void Dispose()
    {
        _app.StopAsync().GetAwaiter().GetResult();
        ((IDisposable)_app).Dispose();
        AuditLog.Writer = null;
        _env.Dispose();
        _ix.Dispose();
    }

    [Fact]
    public async Task A_gitlab_token_works_as_before_and_an_sk_key_is_just_another_gitlab_token()
    {
        var session = await McpCalls.Session(_http, "glpat-alice");
        Assert.False((await McpCalls.Call(_http, "glpat-alice", session, "find_symbol", new { name = "DecodeFrame" }))["isError"]!.GetValue<bool>());
        Assert.Contains("\"via\": \"gitlab_token\"", _log.ToString(), StringComparison.Ordinal);

        // No app to ask: GitLab is asked (unreachable here, nothing cached), not anyone else.
        var resp = await McpCalls.Initialize(_http, "sk-alice");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Cannot verify your GitLab access right now", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
