using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Platform;
using Argus.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Argus.Tests;

/// <summary>
/// A stand-in for LiteLLM on a real socket: users, keys, models, and a
/// streaming chat that asks for one tool call before it answers.
/// </summary>
public sealed class FakeGateway : IAsyncDisposable
{
    readonly WebApplication _app;
    public string Url { get; }
    public ConcurrentDictionary<string, JsonObject> Users { get; } = new();
    public ConcurrentDictionary<string, string> Keys { get; } = new();      // key -> user_id
    public ConcurrentQueue<(string? Auth, JsonObject Body)> ChatRequests { get; } = new();
    public const string Master = "sk-master";

    public FakeGateway()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _app = b.Build();
        _app.MapGet("/health/liveliness", () => "I'm alive!");
        _app.MapPost("/user/new", async (HttpContext c) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            var id = body["user_id"]!.ToString();
            if (Users.ContainsKey(id)) return Results.Json(new { error = new { message = $"User {id} already exists" } }, statusCode: 400);
            Users[id] = new JsonObject { ["user_id"] = id, ["spend"] = 0.0, ["max_budget"] = body["max_budget"]?.DeepClone() };
            return Results.Json(new { user_id = id });
        });
        _app.MapPost("/user/update", async (HttpContext c) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            Users[body["user_id"]!.ToString()]["max_budget"] = body["max_budget"]?.DeepClone();
            return Results.Json(new { ok = true });
        });
        _app.MapPost("/user/delete", async (HttpContext c) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            foreach (var id in (JsonArray)body["user_ids"]!) Users.TryRemove(id!.ToString(), out _);
            return Results.Json(new { ok = true });
        });
        _app.MapGet("/user/info", (string user_id) =>
        {
            var keys = new JsonArray(Keys.Where(k => k.Value == user_id)
                .Select(k => (JsonNode?)new JsonObject { ["token"] = "tok-" + k.Key, ["key_alias"] = AliasOf(k.Key), ["key_name"] = "sk-..." + k.Key[^4..] }).ToArray());
            return Results.Text(new JsonObject { ["user_info"] = Users.TryGetValue(user_id, out var u) ? u.DeepClone() : null, ["keys"] = keys }.ToJsonString(), "application/json");
        });
        _app.MapGet("/user/list", () => Results.Text(new JsonObject { ["users"] = new JsonArray(Users.Values.Select(u => (JsonNode?)u.DeepClone()).ToArray()) }.ToJsonString(), "application/json"));
        _app.MapPost("/key/generate", async (HttpContext c) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            var key = "sk-" + Guid.NewGuid().ToString("n");
            Keys[key] = body["user_id"]!.ToString();
            _aliases[key] = body["key_alias"]?.ToString() ?? "";
            return Results.Json(new { key, token = "tok-" + key });
        });
        _app.MapPost("/key/delete", async (HttpContext c) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            foreach (var t in (JsonArray)body["keys"]!) Keys.TryRemove(t!.ToString().Replace("tok-", ""), out _);
            return Results.Json(new { ok = true });
        });
        _app.MapGet("/v1/models", () => Results.Json(new { data = new[] { new { id = "qwen-test" } } }));
        _app.MapPost("/v1/chat/completions", async (HttpContext c) =>
        {
            var body = (JsonObject)(await JsonNode.ParseAsync(c.Request.Body))!;
            ChatRequests.Enqueue((c.Request.Headers.Authorization.ToString(), body));
            var messages = (JsonArray)body["messages"]!;
            var last = (JsonObject)messages[^1]!;
            c.Response.ContentType = "text/event-stream";
            async Task Send(object chunk) => await c.Response.WriteAsync("data: " + System.Text.Json.JsonSerializer.Serialize(chunk) + "\n\n");
            var text = last["content"]?.ToString() ?? "";
            if (last["role"]!.ToString() == "user" && text.Contains("budget"))
            {
                c.Response.StatusCode = 400;
                await c.Response.WriteAsync("{\"error\":{\"message\":\"Budget has been exceeded! Current cost: 51.0, Max budget: 50.0\"}}");
                return;
            }
            if (last["role"]!.ToString() == "user" && text.StartsWith("find ", StringComparison.Ordinal) && body["tools"] is not null)
            {
                await Send(new { choices = new[] { new { index = 0, delta = new { reasoning_content = "I should look it up." } } } });
                await Send(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "find_symbol", arguments = "{\"name\":" } } } } } } });
                await Send(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"" + text[5..] + "\"}" } } } }, finish_reason = "tool_calls" } } });
            }
            else
            {
                var said = last["role"]!.ToString() == "tool" ? "The tool said: " + Util.PyStr.Prefix(last["content"]!.ToString(), 40) : "Hello there.";
                foreach (var part in new[] { said[..(said.Length / 2)], said[(said.Length / 2)..] })
                    await Send(new { choices = new[] { new { index = 0, delta = new { content = part } } } });
                await Send(new { choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } });
            }
            await c.Response.WriteAsync("data: [DONE]\n\n");
        });
        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Urls.First();
    }

    readonly ConcurrentDictionary<string, string> _aliases = new();
    string AliasOf(string key) => _aliases.GetValueOrDefault(key, "");

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public class PasswordAndAccountTests
{
    [Fact]
    public void A_password_hash_verifies_and_resists_tampering()
    {
        var h = Passwords.Hash("correct horse battery");
        Assert.StartsWith("pbkdf2_sha256$600000$", h);
        Assert.True(Passwords.Verify("correct horse battery", h));
        Assert.False(Passwords.Verify("correct horse batterY", h));
        Assert.False(Passwords.Verify("correct horse battery", h[..^4] + "AAAA"));
        Assert.False(Passwords.Verify("x", "md5$1$abc$def"));
        Assert.NotEqual(h, Passwords.Hash("correct horse battery"));
    }

    [Fact]
    public void Accounts_validate_and_the_last_administrator_is_protected()
    {
        using var dir = new TempDir();
        using var conn = AppDb.Open(Path.Combine(dir.Path, "app.db"));
        var admin = Users.Create(conn, "Alice", "Alice@Example.invalid", "a-long-password", "admin");
        Assert.Equal("alice", admin.Username);
        Assert.Equal("alice@example.invalid", admin.Email);
        Assert.Throws<AccountError>(() => Users.Create(conn, "bob", "bob@example.invalid", "short"));
        Assert.Throws<AccountError>(() => Users.Create(conn, "b o b", "bob@example.invalid", "a-long-password"));
        Assert.Equal(409, Assert.Throws<AccountError>(() => Users.Create(conn, "ALICE", "x@example.invalid", "a-long-password")).Status);
        Assert.Equal(409, Assert.Throws<AccountError>(() => Users.Update(conn, admin.Id, role: "user")).Status);
        Assert.Equal(409, Assert.Throws<AccountError>(() => Users.Delete(conn, admin.Id)).Status);

        var bob = Users.Create(conn, "bob", "bob@example.invalid", "b-long-password");
        Assert.Equal(bob.Id, Users.CheckPassword(conn, "BOB@example.invalid", "b-long-password")!.Id);
        Assert.Null(Users.CheckPassword(conn, "bob", "wrong-password"));
        Assert.Null(Users.CheckPassword(conn, "nobody", "b-long-password"));

        var token = Users.StartSession(conn, bob.Id, "test");
        Assert.Equal(bob.Id, Users.FromSession(conn, token)!.Id);
        var (key, _) = Users.CreateApiKey(conn, bob.Id, "laptop");
        Assert.StartsWith("ak_", key);
        Assert.Equal(bob.Id, Users.FromApiKey(conn, key)!.Id);

        Users.Update(conn, bob.Id, disabled: true);
        Assert.Null(Users.FromSession(conn, token));
        Assert.Null(Users.FromApiKey(conn, key));
        Assert.Null(Users.CheckPassword(conn, "bob", "b-long-password"));

        Users.Update(conn, bob.Id, disabled: false);
        Users.SetPassword(conn, bob.Id, "a-new-long-password");
        Assert.Null(Users.FromSession(conn, Users.StartSession(conn, bob.Id, "t") + "x"));
    }
}

/// <summary>The application API in-process, with a fake gateway on a real socket.</summary>
[Collection("process-state")]
public sealed class PlatformHttpTests : IAsyncLifetime
{
    readonly TestIndex _ix = new();
    readonly FakeGateway _gateway = new();
    readonly TempDir _web = new();
    EnvScope? _env;
    WebApplication? _app;
    long _repo;

    public async Task InitializeAsync()
    {
        _web.File("index.html", "<!doctype html><title>Argus</title><div id=root></div>");
        _web.File("assets/app-abc123.js", "console.log(1)");
        _env = new EnvScope(("ARGUS_GATEWAY_URL", _gateway.Url), ("LITELLM_MASTER_KEY", FakeGateway.Master),
            ("ARGUS_ADMIN_USERNAME", "root"), ("ARGUS_ADMIN_EMAIL", "root@example.invalid"), ("ARGUS_ADMIN_PASSWORD", "root-long-password"),
            ("ARGUS_WEB_ROOT", _web.Path), ("ARGUS_INDEX_INTERVAL", "0"), ("ARGUS_AUDIT_LOG", "0"), ("ARGUS_ADMIN_TOKEN", null),
            ("ARGUS_APP_DB", null), ("ARGUS_LLAMACPP_HEALTH_URL", null), ("ARGUS_EMBED_URL", "http://127.0.0.1:9"));
        _repo = _ix.Repo(11, "grp/alpha");
        var file = _ix.File(_repo, "src/decode.c", "int DecodeFrame(int x) { return x; }\n");
        _ix.Symbol(_repo, file, "DecodeFrame", signature: "(int x)", doc: "Decode one frame.");
        _app = ArgusServer.Create(_ix.Config(), configure: b => b.WebHost.UseTestServer());
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app!.StopAsync();
        await _app.DisposeAsync();
        await _gateway.DisposeAsync();
        _env!.Dispose();
        _ix.Dispose();
        _web.Dispose();
    }

    /// <summary>A browser: keeps its cookie, and says X-Argus-Request on changes unless told not to.</summary>
    sealed class Browser(HttpClient http)
    {
        string? _cookie;

        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, bool csrf = true)
        {
            var req = new HttpRequestMessage(method, path);
            if (body is not null) req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (_cookie is not null) req.Headers.Add("Cookie", _cookie);
            if (csrf && method != HttpMethod.Get) req.Headers.Add(PlatformApi.CsrfHeader, "1");
            var resp = await http.SendAsync(req);
            if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var c in cookies)
                    if (c.StartsWith(PlatformApi.SessionCookie + "=", StringComparison.Ordinal))
                        _cookie = c.Split(';')[0].EndsWith('=') ? null : c.Split(';')[0];
            return resp;
        }

        public async Task<JsonNode> Json(HttpMethod method, string path, object? body = null, HttpStatusCode expect = HttpStatusCode.OK)
        {
            var resp = await Send(method, path, body);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == expect, $"{method} {path}: {(int)resp.StatusCode} {text}");
            return JsonNode.Parse(text)!;
        }

        public Task<JsonNode> Login(string user, string password) =>
            Json(HttpMethod.Post, "/api/auth/login", new { username = user, password });
    }

    Browser NewBrowser() => new(_app!.GetTestClient());

    [Fact]
    public async Task Signing_in_sets_a_hardened_cookie_and_signing_out_ends_the_session()
    {
        var b = NewBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Send(HttpMethod.Get, "/api/me")).StatusCode);
        var bad = await b.Send(HttpMethod.Post, "/api/auth/login", new { username = "root", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var resp = await b.Send(HttpMethod.Post, "/api/auth/login", new { username = "root", password = "root-long-password" });
        var cookie = resp.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("argus_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("admin", (await b.Json(HttpMethod.Get, "/api/me"))["user"]!["role"]!.GetValue<string>());

        await b.Json(HttpMethod.Post, "/api/auth/logout");
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Send(HttpMethod.Get, "/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_change_without_the_request_header_is_refused_as_cross_site()
    {
        var b = NewBrowser();
        await b.Login("root", "root-long-password");
        var resp = await b.Send(HttpMethod.Post, "/api/conversations", new { }, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await b.Send(HttpMethod.Post, "/api/conversations", new { })).StatusCode);
    }

    [Fact]
    public async Task Failed_sign_ins_are_throttled_but_successful_ones_are_not()
    {
        var b = NewBrowser();
        for (int i = 0; i < 15; i++) Assert.Equal(HttpStatusCode.OK, (await b.Send(HttpMethod.Post, "/api/auth/login", new { username = "root", password = "root-long-password" })).StatusCode);
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 11; i++)
            codes.Add((await b.Send(HttpMethod.Post, "/api/auth/login", new { username = "root", password = "nope" })).StatusCode);
        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, 10).Append(HttpStatusCode.TooManyRequests), codes);
        var locked = await b.Send(HttpMethod.Post, "/api/auth/login", new { username = "root", password = "root-long-password" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter is not null);
        Assert.Contains("Too many failed sign-ins", await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public void The_throttle_window_slides_and_success_clears_the_name()
    {
        var clock = DateTimeOffset.UnixEpoch;
        var t = new LoginThrottle(perName: 3, perAddress: 5, window: TimeSpan.FromMinutes(10), now: () => clock);
        for (int i = 0; i < 3; i++) t.Failed("Bob", "10.0.0.1");
        Assert.Equal(TimeSpan.FromMinutes(10), t.RetryAfter("bob", "10.0.0.9"));
        clock += TimeSpan.FromMinutes(4);
        Assert.Equal(TimeSpan.FromMinutes(6), t.RetryAfter("bob", "10.0.0.9"));
        t.Succeeded("BOB");
        Assert.Null(t.RetryAfter("bob", "10.0.0.9"));
        t.Failed("carol", "10.0.0.1");
        t.Failed("dave", "10.0.0.1");
        Assert.NotNull(t.RetryAfter("erin", "10.0.0.1"));      // five failures from one address, across names
        clock += TimeSpan.FromMinutes(11);
        Assert.Null(t.RetryAfter("erin", "10.0.0.1"));
    }

    [Fact]
    public async Task Administration_creates_people_in_the_gateway_and_is_closed_to_everyone_else()
    {
        var admin = NewBrowser();
        await admin.Login("root", "root-long-password");
        var created = await admin.Json(HttpMethod.Post, "/api/admin/users",
            new { username = "dana", email = "dana@example.invalid", max_budget = 25.0 }, HttpStatusCode.Created);
        var password = created["password"]!.GetValue<string>();
        Assert.Equal(20, password.Length);
        Assert.Equal(25.0, _gateway.Users["dana@example.invalid"]["max_budget"]!.GetValue<double>());
        var id = created["user"]!["id"]!.GetValue<long>();

        var users = await admin.Json(HttpMethod.Get, "/api/admin/users");
        Assert.Contains(users["users"]!.AsArray(), u => u!["username"]!.GetValue<string>() == "dana" && u["max_budget"]!.GetValue<double>() == 25.0);

        await admin.Json(HttpMethod.Patch, $"/api/admin/users/{id}", new { max_budget = 5.0, gitlab_username = "dana.gl" });
        Assert.Equal(5.0, _gateway.Users["dana@example.invalid"]["max_budget"]!.GetValue<double>());

        var dana = NewBrowser();
        await dana.Login("dana", password);
        Assert.Equal(HttpStatusCode.Forbidden, (await dana.Send(HttpMethod.Get, "/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dana.Send(HttpMethod.Get, "/admin/index/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Send(HttpMethod.Get, "/admin/index/status")).StatusCode);

        var reset = await admin.Json(HttpMethod.Post, $"/api/admin/users/{id}/reset-password");
        Assert.Equal(HttpStatusCode.Unauthorized, (await dana.Send(HttpMethod.Get, "/api/me")).StatusCode);   // sessions ended
        await dana.Login("dana", reset["password"]!.GetValue<string>());

        var self = (await admin.Json(HttpMethod.Get, "/api/me"))["user"]!["id"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Send(HttpMethod.Delete, $"/api/admin/users/{self}")).StatusCode);
        await admin.Json(HttpMethod.Delete, $"/api/admin/users/{id}");
        Assert.False(_gateway.Users.ContainsKey("dana@example.invalid"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await dana.Send(HttpMethod.Get, "/api/me")).StatusCode);
    }

    [Fact]
    public async Task Personal_keys_work_for_the_api_and_model_keys_come_from_the_gateway()
    {
        var b = NewBrowser();
        await b.Login("root", "root-long-password");
        var created = await b.Json(HttpMethod.Post, "/api/me/keys", new { name = "laptop" }, HttpStatusCode.Created);
        var key = created["key"]!.GetValue<string>();
        var http = _app!.GetTestClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Authorization = new("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(req)).StatusCode);
        var listed = await b.Json(HttpMethod.Get, "/api/me/keys");
        Assert.Equal("laptop", listed[0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(key, listed.ToJsonString());
        await b.Json(HttpMethod.Delete, $"/api/me/keys/{created["id"]}");
        req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Authorization = new("Bearer", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(req)).StatusCode);

        var model = await b.Json(HttpMethod.Post, "/api/me/model-keys", new { name = "editor" }, HttpStatusCode.Created);
        Assert.StartsWith("sk-", model["key"]!.GetValue<string>());
        var models = await b.Json(HttpMethod.Get, "/api/me/model-keys");
        Assert.Equal("editor", models[0]!["alias"]!.GetValue<string>());
        await b.Json(HttpMethod.Delete, $"/api/me/model-keys/{models[0]!["token"]}");
        Assert.Empty((await b.Json(HttpMethod.Get, "/api/me/model-keys")).AsArray());
    }

    static List<JsonObject> Events(string sse) =>
        sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(e => e.StartsWith("data: ", StringComparison.Ordinal))
            .Select(e => (JsonObject)JsonNode.Parse(e[6..])!).ToList();

    [Fact]
    public async Task A_chat_turn_streams_runs_the_tool_and_answers_with_the_persons_own_key()
    {
        var b = NewBrowser();
        await b.Login("root", "root-long-password");
        Assert.Equal("qwen-test", (await b.Json(HttpMethod.Get, "/api/models"))[0]!.GetValue<string>());
        var conv = await b.Json(HttpMethod.Post, "/api/conversations", new { }, HttpStatusCode.Created);
        var id = conv["id"]!.GetValue<string>();

        var resp = await b.Send(HttpMethod.Post, $"/api/conversations/{id}/messages", new { content = "find DecodeFrame" });
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        var events = Events(await resp.Content.ReadAsStringAsync());
        var types = events.Select(e => e["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["start", "reasoning", "tool_call", "tool_result", "content", "content", "done"], types);
        var call = events.Single(e => e["type"]!.GetValue<string>() == "tool_call");
        Assert.Equal("find_symbol", call["name"]!.GetValue<string>());
        Assert.Equal("{\"name\":\"DecodeFrame\"}", call["arguments"]!.GetValue<string>());
        // No GitLab here, so the code index refuses -- as a tool result the model reads, not a failed turn.
        var result = events.Single(e => e["type"]!.GetValue<string>() == "tool_result");
        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.StartsWith("Error executing tool find_symbol:", result["content"]!.GetValue<string>());

        // Both model rounds went out with the person's own gateway key, and the tools.
        var sent = _gateway.ChatRequests.ToList();
        Assert.Equal(2, sent.Count);
        var key = _gateway.Keys.Single(k => k.Value == "root@example.invalid").Key;
        Assert.All(sent, s => Assert.Equal("Bearer " + key, s.Auth));
        Assert.Equal(17, sent[0].Body["tools"]!.AsArray().Count);
        Assert.Equal("tool", sent[1].Body["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>());

        var stored = await b.Json(HttpMethod.Get, $"/api/conversations/{id}");
        Assert.Equal("find DecodeFrame", stored["title"]!.GetValue<string>());
        Assert.Equal(["user", "assistant", "tool", "assistant"], stored["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("I should look it up.", stored["messages"]![1]!["reasoning"]!.GetValue<string>());
        Assert.StartsWith("The tool said: Error executing tool", stored["messages"]![3]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_gateway_refusal_reaches_the_person_as_an_error_event()
    {
        var b = NewBrowser();
        await b.Login("root", "root-long-password");
        var id = (await b.Json(HttpMethod.Post, "/api/conversations", new { }, HttpStatusCode.Created))["id"]!.GetValue<string>();
        var events = Events(await (await b.Send(HttpMethod.Post, $"/api/conversations/{id}/messages", new { content = "over budget" })).Content.ReadAsStringAsync());
        Assert.Contains("Budget has been exceeded", events[^1]["message"]!.GetValue<string>());
        Assert.Equal("error", events[^1]["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Conversations_belong_to_their_owner()
    {
        var admin = NewBrowser();
        await admin.Login("root", "root-long-password");
        var pw = (await admin.Json(HttpMethod.Post, "/api/admin/users", new { username = "eve", email = "eve@example.invalid" }, HttpStatusCode.Created))["password"]!.GetValue<string>();
        var id = (await admin.Json(HttpMethod.Post, "/api/conversations", new { title = "mine" }, HttpStatusCode.Created))["id"]!.GetValue<string>();
        var eve = NewBrowser();
        await eve.Login("eve", pw);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.Send(HttpMethod.Get, $"/api/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eve.Send(HttpMethod.Delete, $"/api/conversations/{id}")).StatusCode);
        Assert.Empty((await eve.Json(HttpMethod.Get, "/api/conversations")).AsArray());
        var events = Events(await (await eve.Send(HttpMethod.Post, $"/api/conversations/{id}/messages", new { content = "hi" })).Content.ReadAsStringAsync());
        Assert.Equal("No such conversation.", events.Single()["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_app_shell_is_served_for_client_routes_but_not_for_api_paths()
    {
        var http = _app!.GetTestClient();
        foreach (var route in new[] { "/manage/packs", "/manage/people", "/settings" })
            Assert.Contains("<div id=root>", await (await http.GetAsync(route)).Content.ReadAsStringAsync());
        var shell = await http.GetAsync("/chat/abc");
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Contains("<div id=root>", await shell.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'self'", shell.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", shell.Headers.GetValues("X-Content-Type-Options").Single());
        var asset = await http.GetAsync("/assets/app-abc123.js");
        Assert.Contains("immutable", asset.Headers.CacheControl!.ToString());
        var apiMiss = await http.GetAsync("/api/nothing-here");
        Assert.NotEqual(HttpStatusCode.OK, apiMiss.StatusCode);
        Assert.DoesNotContain("<div id=root>", await apiMiss.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_tool_runs_as_the_person_and_sees_only_their_repositories()
    {
        var tools = new Tools(_ix.Config());
        var alice = new AppUser(1, "alice", "alice@example.invalid", "", "user", null, false, 0, null);
        var chat = new ChatService(tools, null, _ => new Identity(5, "alice", [_repo]), Path.Combine(_ix.Root, "chat-app.db"));
        var (text, isError) = chat.RunTool("find_symbol", "{\"name\":\"DecodeFrame\"}", alice);
        Assert.False(isError, text);
        Assert.Contains("\"doc\":\"Decode one frame.\"", text);
        var blind = new ChatService(tools, null, _ => new Identity(6, "bob", []), Path.Combine(_ix.Root, "chat-app.db"));
        Assert.True(blind.RunTool("find_symbol", "{\"name\":\"DecodeFrame\"}", alice).IsError);
        Assert.Equal(("Unknown tool: rm_rf", true), chat.RunTool("rm_rf", "{}", alice));
        Assert.StartsWith("Error executing tool find_symbol: tool arguments are not valid JSON", chat.RunTool("find_symbol", "{nope", alice).Text);
        var denied = new ChatService(tools, null, _ => throw new AclDenied("No GitLab account matches."), Path.Combine(_ix.Root, "chat-app.db"));
        Assert.Equal(("Error executing tool find_symbol: No GitLab account matches.", true), denied.RunTool("find_symbol", "{\"name\":\"DecodeFrame\"}", alice));
    }
}
