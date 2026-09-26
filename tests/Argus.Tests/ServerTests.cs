using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Server;
using Argus.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Argus.Tests;

/// <summary>The HTTP surface in-process: authentication, the host guard, MCP, admin, webhook.</summary>
[Collection("process-state")]
public sealed class ServerTests : IDisposable
{
    readonly TestIndex _ix = new();
    readonly EnvScope _env = new(("ARGUS_ADMIN_TOKEN", "admin-secret"), ("ARGUS_WEBHOOK_TOKEN", "hook-secret"),
        ("ARGUS_ACCESS_NOTICES", "0"), ("ARGUS_AUDIT_LOG", "0"), ("ARGUS_INDEX_INTERVAL", "0"));
    readonly WebApplication _app;
    readonly HttpClient _http;
    readonly long _repo;

    public ServerTests()
    {
        _repo = _ix.Repo(11, "grp/alpha");
        var file = _ix.File(_repo, "src/decode.c", "/* Decode one frame. */\nint DecodeFrame(int x) { return x; }\n");
        _ix.Symbol(_repo, file, "DecodeFrame", line: 2, signature: "(int x)", doc: "Decode one frame.");
        var hidden = _ix.Repo(12, "grp/hidden");
        _ix.File(hidden, "h.c", "int Hidden;\n");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Writes.UpsertAclCache(_ix.Conn, Acl.Hash("tok-alice"), 5, "alice", $"[{_repo}]", now);

        _app = ArgusServer.Create(_ix.Config(), configure: b => b.WebHost.UseTestServer());
        _app.StartAsync().GetAwaiter().GetResult();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri("http://localhost:7700");
    }

    public void Dispose()
    {
        _app.StopAsync().GetAwaiter().GetResult();
        ((IDisposable)_app).Dispose();
        _env.Dispose();
        _ix.Dispose();
    }

    HttpRequestMessage Mcp(object body, string? token = "tok-alice", string? session = null)
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

    static JsonNode ReadRpc(string body)
    {
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonNode.Parse(line[6..])!;
        return JsonNode.Parse(body)!;
    }

    async Task<string> Initialize()
    {
        var resp = await _http.SendAsync(Mcp(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } },
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var session = resp.Headers.GetValues("mcp-session-id").Single();
        var init = ReadRpc(await resp.Content.ReadAsStringAsync());
        Assert.StartsWith("This server indexes your organisation's private code", init["result"]!["instructions"]!.GetValue<string>());
        await _http.SendAsync(Mcp(new { jsonrpc = "2.0", method = "notifications/initialized" }, session: session));
        return session;
    }

    async Task<JsonNode> Call(string session, string tool, object args)
    {
        var resp = await _http.SendAsync(Mcp(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = tool, arguments = args } }, session: session));
        return ReadRpc(await resp.Content.ReadAsStringAsync())["result"]!;
    }

    [Fact]
    public async Task Healthz_needs_no_credential()
    {
        var resp = await _http.GetAsync("/healthz");
        Assert.Equal("{\"status\":\"ok\"}", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_missing_bearer_is_401_with_a_challenge()
    {
        var resp = await _http.SendAsync(Mcp(new { jsonrpc = "2.0", id = 1, method = "ping" }, token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("Bearer", resp.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Contains("Missing or malformed Authorization header", await resp.Content.ReadAsStringAsync());
        var audit = Db.ConnectAudit(_ix.DbPath);
        Assert.Equal("<auth_denied>", Util.Sql.Scalar(audit, "SELECT tool FROM audit ORDER BY id DESC LIMIT 1"));
        audit.Dispose();
    }

    [Fact]
    public async Task A_foreign_host_header_is_421_after_authentication()
    {
        var req = Mcp(new { jsonrpc = "2.0", id = 1, method = "ping" });
        req.Headers.Host = "evil.example";
        var resp = await _http.SendAsync(req);
        Assert.Equal((HttpStatusCode)421, resp.StatusCode);
        Assert.Equal("Invalid Host header", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tools_are_listed_with_the_python_catalogue()
    {
        var session = await Initialize();
        var resp = await _http.SendAsync(Mcp(new { jsonrpc = "2.0", id = 3, method = "tools/list" }, session: session));
        var tools = ReadRpc(await resp.Content.ReadAsStringAsync())["result"]!["tools"]!.AsArray();
        Assert.Equal(17, tools.Count);
        Assert.Equal(ToolCatalog.Specs.Select(s => s.Name), tools.Select(t => t!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_tool_call_is_scoped_shaped_and_audited()
    {
        var session = await Initialize();
        var result = await Call(session, "find_symbol", new { name = "DecodeFrame" });
        Assert.False(result["isError"]!.GetValue<bool>());
        var rows = result["structuredContent"]!["result"]!.AsArray();
        Assert.Single(rows);
        Assert.Equal("Decode one frame.", rows[0]!["doc"]!.GetValue<string>());
        Assert.StartsWith("{\n  \"repo_id\": ", result["content"]![0]!["text"]!.GetValue<string>());

        var denied = await Call(session, "get_file", new { repo_id = 99, path = "h.c" });
        Assert.True(denied["isError"]!.GetValue<bool>());
        Assert.StartsWith("Error executing tool get_file: No file at repo_id=99", denied["content"]![0]!["text"]!.GetValue<string>());

        var invalid = await Call(session, "find_symbol", new { });
        Assert.StartsWith("Error executing tool find_symbol: 1 validation error for find_symbolArguments\nname\n  Field required",
            invalid["content"]![0]!["text"]!.GetValue<string>());

        using var audit = Db.ConnectAudit(_ix.DbPath);
        var row = Util.Sql.One(audit, "SELECT username, tool, args_json, repo_ids_json FROM audit WHERE tool = 'find_symbol'")!;
        Assert.Equal("alice", row.Str("username"));
        Assert.Equal("{\"name\": \"DecodeFrame\", \"kind\": null, \"branch\": null}", row.Str("args_json"));
        Assert.Equal($"[{_repo}]", row.Str("repo_ids_json"));
    }

    [Fact]
    public async Task Docs_tools_say_plainly_when_no_packs_are_installed()
    {
        var session = await Initialize();
        var result = await Call(session, "docs_lookup", new { name = "x" });
        Assert.Contains("No documentation packs are installed on this server", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Admin_endpoints_need_the_admin_token()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync("/admin/explore")).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, "/admin/explore?q=Decode");
        req.Headers.Add("x-argus-admin-token", "admin-secret");
        var body = JsonNode.Parse(await (await _http.SendAsync(req)).Content.ReadAsStringAsync())!;
        Assert.Equal(2, body["repos"]!.AsArray().Count);
        Assert.Equal("DecodeFrame", body["symbols"]!["rows"]![0]!["name"]!.GetValue<string>());

        var metrics = new HttpRequestMessage(HttpMethod.Get, "/admin/metrics");
        metrics.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-secret");
        var text = await (await _http.SendAsync(metrics)).Content.ReadAsStringAsync();
        Assert.Contains("argus_index_scrape_ok 1", text);
        Assert.Contains("argus_index_repos 2", text);
        // Each family's HELP appears exactly once, and its samples are contiguous.
        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("# HELP argus_index_files ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_webhook_ignores_what_it_should_and_refuses_a_bad_token()
    {
        async Task<(HttpStatusCode, JsonNode)> Hook(string token, object body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/hook/gitlab")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-gitlab-token", token);
            var resp = await _http.SendAsync(req);
            return (resp.StatusCode, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook("wrong", new { })).Item1);
        var (_, ignored) = await Hook("hook-secret", new { object_kind = "tag_push" });
        Assert.Equal("ignored", ignored["status"]!.GetValue<string>());
        var (_, deleted) = await Hook("hook-secret", new { object_kind = "push", after = "0000000", project = new { path_with_namespace = "grp/alpha" } });
        Assert.Equal("ref deleted", deleted["reason"]!.GetValue<string>());
        var (status, missing) = await Hook("hook-secret", new { object_kind = "push" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("no project.path_with_namespace", missing["error"]!.GetValue<string>());
    }
}
