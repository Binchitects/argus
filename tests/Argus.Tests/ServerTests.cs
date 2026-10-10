using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Indexing;
using Argus.Server;
using Argus.Store;
using Argus.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Tests;

/// <summary>The HTTP surface in-process: authentication, the host guard, MCP, admin, webhook.</summary>
[Collection("process-state")]
public sealed class ServerTests : IDisposable
{
    readonly TestIndex _ix = new();
    readonly EnvScope _env = new(("ARGUS_ADMIN_TOKEN", "admin-secret"), ("ARGUS_WEBHOOK_TOKEN", "hook-secret"), ("ARGUS_CHAT_CLIENT_TOKEN", "chat-secret"),
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
    public async Task The_chat_client_credential_works_only_inside_the_network_and_only_for_someone()
    {
        // Through the proxy, anyone could claim any email with the shared credential.
        var viaProxy = Mcp(new { jsonrpc = "2.0", id = 1, method = "ping" }, token: "chat-secret");
        viaProxy.Headers.Add("x-forwarded-for", "203.0.113.9");
        viaProxy.Headers.Add(ArgusServer.ChatEmailHeader, "alice@example.com");
        var resp = await _http.SendAsync(viaProxy);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("only from inside the stack's network", await resp.Content.ReadAsStringAsync());

        resp = await _http.SendAsync(Mcp(new { jsonrpc = "2.0", id = 1, method = "ping" }, token: "chat-secret"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("did not say who is asking", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_users_file_maps_an_email_to_its_sign_in_name()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "users:\n  alice:\n    displayname: Alice\n    email: Alice@Example.com\n  bob:\n    email: bob@example.com\n");
            Assert.Equal("alice", People.UsernameForEmail(path, " alice@example.com "));
            Assert.Equal("bob", People.UsernameForEmail(path, "BOB@example.com"));
            Assert.Null(People.UsernameForEmail(path, "carol@example.com"));
            Assert.Null(People.UsernameForEmail(path + ".missing", "alice@example.com"));
            Assert.Null(People.UsernameForEmail(null, "alice@example.com"));
        }
        finally
        {
            File.Delete(path);
        }
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
        Assert.Equal(20, tools.Count);
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
    public async Task Storage_says_what_argus_keeps_on_its_disk_and_a_loaded_pack_is_not_counted_twice()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync("/admin/storage")).StatusCode);
        var data = _ix.Config().Index.DataDir;
        Directory.CreateDirectory(Path.Combine(data, "mirrors", "grp", "alpha.git"));
        File.WriteAllBytes(Path.Combine(data, "mirrors", "grp", "alpha.git", "pack"), new byte[3000]);
        Directory.CreateDirectory(Path.Combine(data, "trees"));
        File.WriteAllBytes(Path.Combine(data, "trees", "a.c"), new byte[200]);
        Directory.CreateDirectory(Path.Combine(data, "packs"));
        File.WriteAllBytes(Path.Combine(data, "packs", "own.arguspack"), new byte[1000]);
        // A pack loaded from the library is a link to it: its bytes are the library's.
        var library = Path.Combine(data, "..", Path.GetFileName(data) + "-library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "big.arguspack"), new byte[50_000]);
        File.CreateSymbolicLink(Path.Combine(data, "packs", "big.arguspack"), Path.Combine(library, "big.arguspack"));
        try
        {
            var (status, body) = await AdminGet("/admin/storage?fresh");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(3000, body["mirrors_bytes"]!.GetValue<long>());
            Assert.Equal(200, body["trees_bytes"]!.GetValue<long>());
            Assert.Equal(1000, body["packs_bytes"]!.GetValue<long>());
            Assert.True(body["index_bytes"]!.GetValue<long>() > 0);
            Assert.True(body["disk"]!["size_bytes"]!.GetValue<long>() >= body["disk"]!["free_bytes"]!.GetValue<long>());
            Assert.Equal(data, body["data_dir"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }

    async Task<(HttpStatusCode Status, JsonNode Body)> AdminGet(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("x-argus-admin-token", "admin-secret");
        var resp = await _http.SendAsync(req);
        return (resp.StatusCode, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!);
    }

    async Task<(HttpStatusCode Status, JsonNode Body)> AdminSend(HttpMethod method, string path, object body)
    {
        var req = new HttpRequestMessage(method, path) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-argus-admin-token", "admin-secret");
        var resp = await _http.SendAsync(req);
        return (resp.StatusCode, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task Repositories_are_listed_with_their_branches_chosen_in_and_out_and_new_ones_follow_the_policy()
    {
        Choices.Record(_ix.Conn, [new Project(11, "grp/alpha", "main", "http://x/a.git"), new Project(12, "grp/hidden", "main", "http://x/h.git")], 100);
        Writes.SetCommitInfo(_ix.Conn, _repo, "Decode frames faster", 1_700_000_000);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync("/admin/repos")).StatusCode);

        var (status, list) = await AdminGet("/admin/repos");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("include", list["new_repos"]!.GetValue<string>());
        var alpha = list["repos"]!.AsArray().Single(r => r!["repo"]!.GetValue<string>() == "grp/alpha")!;
        Assert.True(alpha["included"]!.GetValue<bool>());
        var main = alpha["indexed"]![0]!;
        Assert.Equal("main", main["branch"]!.GetValue<string>());
        Assert.Equal("Decode frames faster", main["message"]!.GetValue<string>());
        Assert.Equal(1, main["files"]!.GetValue<int>());
        Assert.Equal(1, main["symbols"]!.GetValue<int>());

        // Out: it leaves the index at once (no pass is running).
        var (saved, outcome) = await AdminSend(HttpMethod.Patch, "/admin/repos/12", new { included = false });
        Assert.Equal(HttpStatusCode.OK, saved);
        Assert.Equal(1, outcome["removed"]!.GetValue<int>());
        var hidden = (await AdminGet("/admin/repos")).Body["repos"]!.AsArray().Single(r => r!["gitlab_id"]!.GetValue<long>() == 12)!;
        Assert.False(hidden["included"]!.GetValue<bool>());
        Assert.Empty(hidden["indexed"]!.AsArray());

        // Its own branches, besides the default.
        Assert.Equal(HttpStatusCode.OK, (await AdminSend(HttpMethod.Patch, "/admin/repos/11", new { branches = new[] { "develop", "release/*" } })).Status);
        alpha = (await AdminGet("/admin/repos")).Body["repos"]!.AsArray().Single(r => r!["gitlab_id"]!.GetValue<long>() == 11)!;
        Assert.Equal(["develop", "release/*"], alpha["branches"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await AdminSend(HttpMethod.Patch, "/admin/repos/999", new { included = true })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminSend(HttpMethod.Put, "/admin/repos/settings", new { new_repos = "maybe" })).Status);

        // Its index removed and kept in (built anew next time), or removed and left out.
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsync("/admin/repos/11/index/remove", new StringContent("{}"))).StatusCode);
        var (gone, dropped) = await AdminSend(HttpMethod.Post, "/admin/repos/11/index/remove", new { leave_out = false });
        Assert.Equal(HttpStatusCode.OK, gone);
        Assert.True(dropped["removed"]!.GetValue<int>() >= 1);
        alpha = (await AdminGet("/admin/repos")).Body["repos"]!.AsArray().Single(r => r!["gitlab_id"]!.GetValue<long>() == 11)!;
        Assert.True(alpha["included"]!.GetValue<bool>());
        Assert.Empty(alpha["indexed"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await AdminSend(HttpMethod.Post, "/admin/repos/11/index/remove", new { leave_out = true })).Status);
        Assert.False((await AdminGet("/admin/repos")).Body["repos"]!.AsArray().Single(r => r!["gitlab_id"]!.GetValue<long>() == 11)!["included"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await AdminSend(HttpMethod.Post, "/admin/repos/999/index/remove", new { })).Status);

        // New repositories left out until chosen.
        Assert.Equal(HttpStatusCode.OK, (await AdminSend(HttpMethod.Put, "/admin/repos/settings", new { new_repos = "exclude" })).Status);
        Choices.Record(_ix.Conn, [new Project(13, "grp/new", "main", "http://x/n.git")], 200);
        Assert.False((await AdminGet("/admin/repos")).Body["repos"]!.AsArray().Single(r => r!["gitlab_id"]!.GetValue<long>() == 13)!["included"]!.GetValue<bool>());
    }

    static JsonNode Repo(JsonNode list, long id) => list["repos"]!.AsArray().Single(r => r!["gitlab_id"]!.GetValue<long>() == id)!;

    static Dictionary<long, (bool Ok, string Message)> Results(JsonNode batch) =>
        batch["results"]!.AsArray().ToDictionary(r => r!["gitlab_id"]!.GetValue<long>(), r => (r!["ok"]!.GetValue<bool>(), r["message"]!.GetValue<string>()));

    [Fact]
    public async Task Repositories_say_their_state_and_schedule_and_change_many_at_once_with_an_outcome_each()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Project P(long id, string path) => new(id, path, "main", $"http://x/{path}.git");
        Choices.Record(_ix.Conn, [P(11, "grp/alpha"), P(12, "grp/hidden"), P(13, "other/never")], now);
        Writes.SetLastIndexed(_ix.Conn, _repo, "abc12345", now);
        Writes.RecordRunState(_ix.Conn, _repo, false, false, now);
        var hiddenRow = Sql.One(_ix.Conn, "SELECT id FROM repos WHERE gitlab_id = 12")!.Long("id");
        Writes.RecordRunState(_ix.Conn, hiddenRow, false, false, now, "git fetch failed: 403");
        var jobs = _app.Services.GetRequiredService<Jobs>();
        var runs = new List<IReadOnlyList<string>>();
        jobs.Runner = (argv, _) => { lock (runs) runs.Add(argv); return 0; };

        var list = (await AdminGet("/admin/repos")).Body;
        var alpha = Repo(list, 11);
        Assert.Equal("alpha", alpha["name"]!.GetValue<string>());
        Assert.Equal("grp", alpha["group"]!.GetValue<string>());
        Assert.Equal("C", alpha["language"]!.GetValue<string>());
        Assert.Equal("indexed", alpha["state"]!.GetValue<string>());
        Assert.True(alpha["listed"]!.GetValue<bool>());
        Assert.Equal("With each scheduled pass", alpha["schedule_words"]!.GetValue<string>());
        Assert.Null(alpha["next_run_at"]);
        Assert.Equal(now, alpha["last_run_at"]!.GetValue<long>());
        Assert.Equal("failed", Repo(list, 12)["state"]!.GetValue<string>());
        Assert.Equal("git fetch failed: 403", Repo(list, 12)["problem"]!.GetValue<string>());
        Assert.Equal("never", Repo(list, 13)["state"]!.GetValue<string>());
        Assert.Equal("pass", list["schedule"]!["default"]!.GetValue<string>());

        // A schedule of its own, and the default for every other one, in a time zone.
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminSend(HttpMethod.Patch, "/admin/repos/11", new { schedule = "every day" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await AdminSend(HttpMethod.Patch, "/admin/repos/11", new { schedule = "daily:02:30" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminSend(HttpMethod.Put, "/admin/repos/settings", new { schedule = "hours:6", schedule_tz = "Mars/Base" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminSend(HttpMethod.Put, "/admin/repos/settings", new { schedule = "" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await AdminSend(HttpMethod.Put, "/admin/repos/settings", new { schedule = "hours:6", schedule_tz = "Europe/Berlin" })).Status);
        list = (await AdminGet("/admin/repos")).Body;
        Assert.Equal("Every day at 02:30", Repo(list, 11)["schedule_words"]!.GetValue<string>());
        Assert.True(Repo(list, 11)["next_run_at"]!.GetValue<long>() > now);
        Assert.Equal("Every 6 hours", Repo(list, 13)["schedule_words"]!.GetValue<string>());
        Assert.Equal("", Repo(list, 13)["schedule"]!.GetValue<string>());
        Assert.Equal("Europe/Berlin", list["schedule"]!["time_zone"]!.GetValue<string>());

        // Many at once, each with its outcome.
        var (status, batch) = await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "schedule", ids = new long[] { 12, 13, 999 }, schedule = "weekly:1:03:00" });
        Assert.Equal(HttpStatusCode.OK, status);
        var outcome = Results(batch);
        Assert.Equal((true, "Schedule: Mondays at 03:00."), outcome[12]);
        Assert.False(outcome[999].Ok);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "bogus", ids = new long[] { 11 } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "reindex", ids = Array.Empty<long>() })).Status);

        outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "exclude", ids = new long[] { 13 } })).Body);
        Assert.True(outcome[13].Ok);
        outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "reindex", ids = new long[] { 11, 13 } })).Body);
        Assert.Equal((true, "Updating now."), outcome[11]);
        Assert.Equal((false, "Not indexed: choose it for the index first."), outcome[13]);
        Assert.True(SpinWait.SpinUntil(() => { lock (runs) return runs.Count == 1; }, TimeSpan.FromSeconds(10)));
        Assert.Equal(["--repo", "grp/alpha", "--trigger", "manual"], runs[0].SkipWhile(a => a != "--repo").Take(4));
        Assert.True(SpinWait.SpinUntil(() => jobs.IndexJobSnapshot()["state"]?.ToString() == "idle", TimeSpan.FromSeconds(10)));

        outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "add_branches", ids = new long[] { 11 }, branches = new[] { "release/*", "" } })).Body);
        Assert.Equal((true, "Also indexes release/* from the next run."), outcome[11]);
        outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "remove", ids = new long[] { 12 } })).Body);
        Assert.Equal((true, "Left out. Its index (1 branch) is being removed."), outcome[12]);
        Assert.True(SpinWait.SpinUntil(() => Convert.ToInt64(Sql.Scalar(_ix.Conn, "SELECT COUNT(*) FROM repos WHERE gitlab_id = 12")) == 0, TimeSpan.FromSeconds(10)));
        list = (await AdminGet("/admin/repos")).Body;
        Assert.Equal(["release/*"], Repo(list, 11)["branches"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.False(Repo(list, 12)["included"]!.GetValue<bool>());
        Assert.Empty(Repo(list, 12)["indexed"]!.AsArray());
        Assert.Equal("off", Repo(list, 12)["state"]!.GetValue<string>());

        // One GitLab no longer lists (a token that cannot see it, a repository deleted) is forgotten when removed.
        Choices.Record(_ix.Conn, [P(11, "grp/alpha"), P(12, "grp/hidden")], now + 10);
        Assert.False(Repo((await AdminGet("/admin/repos")).Body, 13)["listed"]!.GetValue<bool>());
        outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "remove", ids = new long[] { 13 } })).Body);
        Assert.StartsWith("Forgotten: GitLab no longer lists it", outcome[13].Message);
        Assert.DoesNotContain((await AdminGet("/admin/repos")).Body["repos"]!.AsArray(), r => r!["gitlab_id"]!.GetValue<long>() == 13);

        // Its log, in sentences: what admins did to it.
        var (logStatus, log) = await AdminGet("/admin/repos/11/log");
        Assert.Equal(HttpStatusCode.OK, logStatus);
        var lines = log["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()).ToList();
        Assert.Contains("An admin set its schedule: every day at 02:30.", lines);
        Assert.Contains("An admin added branches to index: release/*.", lines);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminGet("/admin/repos/999/log")).Status);
    }

    [Fact]
    public async Task Leaving_many_out_answers_at_once_and_their_index_goes_in_the_background_once_the_run_going_ends()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Project P(long id, string path) => new(id, path, "main", $"http://x/{path}.git");
        var gone = _ix.Repo(14, "grp/gone");
        _ix.File(gone, "g.c", "int GoneForGood;\n");
        Choices.Record(_ix.Conn, [P(11, "grp/alpha"), P(12, "grp/hidden"), P(14, "grp/gone")], now);
        // GitLab no longer lists grp/gone.
        Choices.Record(_ix.Conn, [P(11, "grp/alpha"), P(12, "grp/hidden")], now + 10);
        long Rows(long id) => Convert.ToInt64(Sql.Scalar(_ix.Conn, "SELECT COUNT(*) FROM repos WHERE gitlab_id = ?", id));
        // Its schedule never runs one GitLab no longer lists (it cannot be fetched), so no next run is shown for it.
        Choices.SetSchedule(_ix.Conn, 12, "hours:1", now);
        Choices.SetSchedule(_ix.Conn, 14, "hours:1", now);
        var before = (await AdminGet("/admin/repos")).Body;
        Assert.NotNull(Repo(before, 12)["next_run_at"]);
        Assert.Null(Repo(before, 14)["next_run_at"]);
        var jobs = _app.Services.GetRequiredService<Jobs>();
        using var release = new ManualResetEventSlim();
        jobs.Runner = (_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(20));
            return 0;
        };
        Assert.Equal("started", jobs.EnqueueRepos(["grp/alpha"], "manual")["grp/alpha"]);

        // While a run writes: the choices are made, and each says when its index goes.
        var outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "exclude", ids = new long[] { 11, 12 } })).Body);
        Assert.Equal((true, "Left out. Its index goes when the run going now ends."), outcome[11]);
        outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "remove", ids = new long[] { 14 } })).Body);
        Assert.Equal((true, "Forgotten: GitLab no longer lists it. Its index goes when the run going now ends."), outcome[14]);
        var list = (await AdminGet("/admin/repos")).Body;
        Assert.False(Repo(list, 11)["included"]!.GetValue<bool>());
        // Nothing is removed under the run.
        Assert.Equal(0, jobs.RemoveLeftOutNow());
        Assert.Equal(1L, Rows(11));
        Assert.Equal(1L, Rows(14));

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => Rows(11) + Rows(12) + Rows(14) == 0, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => Choices.Find(_ix.Conn, 14) is null, TimeSpan.FromSeconds(10)));
        Assert.Empty(Queries.FindSymbol([_repo], _ix.Conn, "DecodeFrame"));
        var log = (await AdminGet("/admin/repos/12/log")).Body["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["An admin left it out of the index.", "Its files and symbols were removed from the index."], log);

        // With no run going, a batch answers before the index goes, and it goes at once.
        Choices.Set(_ix.Conn, 11, true, null, now);
        var again = _ix.Repo(11, "grp/alpha");
        _ix.File(again, "a.c", "int Again;\n");
        outcome = Results((await AdminSend(HttpMethod.Post, "/admin/repos/batch", new { action = "exclude", ids = new long[] { 11 } })).Body);
        Assert.Equal((true, "Left out. Its index (1 branch) is being removed."), outcome[11]);
        Assert.True(SpinWait.SpinUntil(() => Rows(11) == 0, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Explore_finds_references_and_code_in_every_repository_and_opens_a_file()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync("/admin/explore/references?name=DecodeFrame")).StatusCode);

        var (status, refs) = await AdminGet("/admin/explore/references?name=DecodeFrame");
        Assert.Equal(HttpStatusCode.OK, status);
        var hit = refs["rows"]!.AsArray().Single()!;
        Assert.Equal("src/decode.c", hit["path"]!.GetValue<string>());
        Assert.Equal(2, hit["line"]!.GetValue<long>());
        Assert.True(hit["is_definition"]!.GetValue<bool>());
        // The operator sees every repository, not only what one person may.
        Assert.Equal("grp/hidden", (await AdminGet("/admin/explore/references?name=Hidden")).Body["rows"]![0]!["repo"]!.GetValue<string>());
        Assert.Empty((await AdminGet("/admin/explore/references?name=Hidden&repo=grp/alpha")).Body["rows"]!.AsArray());

        var code = (await AdminGet("/admin/explore/code?q=frame")).Body["rows"]!.AsArray();
        Assert.Equal("src/decode.c", code.Single()!["path"]!.GetValue<string>());
        var (bad, why) = await AdminGet("/admin/explore/code?q=%22unclosed");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.StartsWith("That search syntax is not valid", why["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("regex", why["error"]!.GetValue<string>(), StringComparison.Ordinal);

        var (found, file) = await AdminGet($"/admin/explore/file?repo_id={_repo}&path=src/decode.c");
        Assert.Equal(HttpStatusCode.OK, found);
        Assert.Contains("int DecodeFrame", file["content"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await AdminGet($"/admin/explore/file?repo_id={_repo}&path=nope.c")).Status);

        // No packs: said plainly, as a request that cannot run, not a failure.
        var (noPacks, docs) = await AdminGet("/admin/explore/docs?q=frame");
        Assert.Equal(HttpStatusCode.BadRequest, noPacks);
        Assert.Contains("No documentation packs are loaded", docs["error"]!.GetValue<string>());
        // Without a library, loading says what to set; the listing has an empty one.
        var load = new HttpRequestMessage(HttpMethod.Post, "/admin/packs/load") { Content = new StringContent("""{"file":"x.arguspack"}""", Encoding.UTF8, "application/json") };
        load.Headers.Add("x-argus-admin-token", "admin-secret");
        var loaded = await _http.SendAsync(load);
        Assert.Equal(HttpStatusCode.BadRequest, loaded.StatusCode);
        Assert.Contains("ARGUS_PACK_LIBRARY", await loaded.Content.ReadAsStringAsync());
        Assert.Empty((await AdminGet("/admin/packs")).Body["library"]!.AsArray());
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

/// <summary>ARGUS_APP=off, as the platform runs it: MCP and the operator surface, no app of its own.</summary>
[Collection("process-state")]
public sealed class ServerWithoutAppTests : IDisposable
{
    readonly TestIndex _ix = new();
    readonly EnvScope _env = new(("ARGUS_APP", "off"), ("ARGUS_ADMIN_TOKEN", "admin-secret"),
        ("ARGUS_ADMIN_USERNAME", "admin"), ("ARGUS_ADMIN_EMAIL", "admin@example.com"), ("ARGUS_ADMIN_PASSWORD", "a-long-password-here"),
        ("ARGUS_ACCESS_NOTICES", "0"), ("ARGUS_AUDIT_LOG", "0"), ("ARGUS_INDEX_INTERVAL", "0"));
    readonly WebApplication _app;
    readonly HttpClient _http;

    public ServerWithoutAppTests()
    {
        var repo = _ix.Repo(11, "grp/alpha");
        Writes.UpsertAclCache(_ix.Conn, Acl.Hash("tok-alice"), 5, "alice", $"[{repo}]", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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

    [Fact]
    public async Task No_app_no_api_no_accounts_but_mcp_and_the_operator_surface_answer()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/auth/me")).StatusCode);
        // The admin credentials in the environment create nobody: there is no one to sign in as.
        var appDb = Argus.Platform.AppDb.PathFor(_ix.Config().Index.DataDir);
        if (File.Exists(appDb))
        {
            using var conn = Argus.Platform.AppDb.Open(appDb);
            Assert.Equal(0, Argus.Platform.Users.Count(conn));
        }

        var init = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } },
            }), Encoding.UTF8, "application/json"),
        };
        init.Headers.Accept.ParseAdd("application/json");
        init.Headers.Accept.ParseAdd("text/event-stream");
        init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok-alice");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(init)).StatusCode);

        var admin = new HttpRequestMessage(HttpMethod.Get, "/admin/index/status");
        admin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-secret");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(admin)).StatusCode);
    }

    [Fact]
    public async Task The_webhook_is_off_until_an_admin_gives_its_hash_and_takes_merges_as_well_as_pushes()
    {
        async Task<(HttpStatusCode, JsonNode)> Admin(HttpMethod method, object? body = null)
        {
            var req = new HttpRequestMessage(method, "/admin/webhook");
            if (body is not null) req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            req.Headers.Add("x-argus-admin-token", "admin-secret");
            var resp = await _http.SendAsync(req);
            return (resp.StatusCode, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!);
        }
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
        static string Sha(string s) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)));

        // No secret in the environment, none given: the address answers as if it were not there.
        var (_, off) = await Admin(HttpMethod.Get);
        Assert.False(off["enabled"]!.GetValue<bool>());
        Assert.False(off["from_env"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await Hook("anything", new { object_kind = "push" })).Item1);

        // Only a SHA-256 is accepted, and only it is kept.
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Put, new { token_sha256 = "the-secret-itself" })).Item1);
        var (_, on) = await Admin(HttpMethod.Put, new { token_sha256 = Sha("hook-secret") });
        Assert.True(on["enabled"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook("wrong", new { object_kind = "push" })).Item1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook(Sha("hook-secret"), new { object_kind = "push" })).Item1);
        // A merge request opened or updated changes no branch; one merged does.
        var (_, opened) = await Hook("hook-secret", new { object_kind = "merge_request", object_attributes = new { action = "open" }, project = new { path_with_namespace = "grp/alpha" } });
        Assert.Equal("ignored", opened["status"]!.GetValue<string>());
        var (merged, noProject) = await Hook("hook-secret", new { object_kind = "merge_request", object_attributes = new { action = "merge" } });
        Assert.Equal(HttpStatusCode.BadRequest, merged);
        Assert.Equal("no project.path_with_namespace", noProject["error"]!.GetValue<string>());

        var deliveries = (await Admin(HttpMethod.Get)).Item2["deliveries"]!.AsArray();
        Assert.Contains(deliveries, d => d!["outcome"]!.GetValue<string>() == "ignored: open" && d["repo"]!.GetValue<string>() == "grp/alpha");
        Assert.Contains(deliveries, d => d!["outcome"]!.GetValue<string>() == "refused: wrong secret");

        // Cleared: off again.
        Assert.False((await Admin(HttpMethod.Put, new { token_sha256 = "" })).Item2["enabled"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await Hook("hook-secret", new { object_kind = "push" })).Item1);
    }
}
