using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.CodeHosts;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Server;
using Argus.Store;
using Argus.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Argus.Tests;

/// <summary>
/// The merge request and commit tools against the fake GitLab, through MCP and the app's /review routes:
/// what each answers, that a person reads only projects they are a member of at Reporter or above (indexed
/// or not), and that Argus never writes to GitLab.
/// </summary>
[Collection("process-state")]
public sealed class ReviewToolsTests : IAsyncLifetime
{
    const string Chat = "chat-secret";
    const string Alpha = "dev_alpha@argus.test";
    readonly TestIndex _ix = new();
    readonly StringWriter _log = new();
    FakeGitLabMr _gitlab = null!;
    EnvScope _env = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _env = new EnvScope(("ARGUS_KEY_CHECK_URL", null), ("ARGUS_CHAT_CLIENT_TOKEN", Chat), ("ARGUS_APP", "off"), ("ARGUS_ACCESS_NOTICES", "0"),
            ("ARGUS_AUDIT_LOG", "1"), ("ARGUS_INDEX_INTERVAL", "0"), ("ARGUS_USERS_FILE", null));
        AuditLog.Writer = _log;
        _gitlab = ReviewFixture.Build();
        // GitLab lists both projects. eal-core is left out of the index (it can still be reviewed); driver-shim is indexed.
        Choices.Record(_ix.Conn,
        [
            new Project(ReviewFixture.EalCore, "root/eal-core", "main", _gitlab[ReviewFixture.EalCore].Bare),
            new Project(ReviewFixture.DriverShim, "root/driver-shim", "main", _gitlab[ReviewFixture.DriverShim].Bare),
        ], 1000);
        Sql.Exec(_ix.Conn, "UPDATE repo_choices SET included = 0 WHERE gitlab_id = ?", ReviewFixture.EalCore);
        var shim = _ix.Repo(ReviewFixture.DriverShim, "root/driver-shim");
        _ix.Symbol(shim, _ix.File(shim, "src/shim.c", "int ShimEntry(const char* b, int n);\n"), "ShimEntry");

        var cfg = _ix.Config() with { GitLab = GitLabConfig.Create(_gitlab.Url, FakeGitLabMr.ServiceToken) };
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
        await _gitlab.DisposeAsync();
    }

    // --- calling the tools ------------------------------------------------------------

    HttpRequestMessage Mcp(object body, string email, string? session = null)
    {
        var req = McpCalls.Request(body, Chat, session);
        req.Headers.Add(ArgusServer.ChatEmailHeader, email);
        return req;
    }

    async Task<string> Session(string email)
    {
        var resp = await _http.SendAsync(Mcp(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } },
        }, email));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var session = resp.Headers.GetValues("mcp-session-id").Single();
        await _http.SendAsync(Mcp(new { jsonrpc = "2.0", method = "notifications/initialized" }, email, session));
        return session;
    }

    async Task<JsonNode> Call(string tool, object args, string email = Alpha)
    {
        var session = await Session(email);
        var resp = await _http.SendAsync(Mcp(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = tool, arguments = args } }, email, session));
        return McpCalls.Rpc(await resp.Content.ReadAsStringAsync())["result"]!;
    }

    /// <summary>A successful call's structured answer (a list's items for the list tools).</summary>
    async Task<JsonNode> Answer(string tool, object args, string email = Alpha)
    {
        var result = await Call(tool, args, email);
        Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());
        var structured = result["structuredContent"]!;
        return structured is JsonObject o && o.Count == 1 && o["result"] is JsonArray list ? list : structured;
    }

    [Fact]
    public async Task Issues_and_the_wiki_are_read_with_their_comments_and_pages_and_only_ever_read()
    {
        var eal = _gitlab[ReviewFixture.EalCore];
        JsonObject Issue(long iid, string title, string state, string updated, string description, params string[] labels) => new()
        {
            ["iid"] = iid, ["title"] = title, ["state"] = state, ["description"] = description, ["author"] = new JsonObject { ["username"] = "bob" },
            ["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)l)]), ["milestone"] = new JsonObject { ["title"] = "Q4" }, ["user_notes_count"] = 2,
            ["created_at"] = "2026-09-01T10:00:00Z", ["updated_at"] = updated, ["web_url"] = $"https://gitlab.test/root/eal-core/-/issues/{iid}",
        };
        eal.Issues[1] = Issue(1, "Refunds round half up", "closed", "2026-10-01T10:00:00Z", "Finance asks refunds to round half up, as invoices do.", "finance");
        eal.Issues[2] = Issue(2, "Free shipping over 50", "opened", "2026-10-05T10:00:00Z", "Orders of 50 or more ship free.", "pricing");
        eal.IssueNotes[1] =
        [
            new JsonObject { ["id"] = 10, ["body"] = "Agreed with finance on 3 Sept.", ["author"] = new JsonObject { ["username"] = "alice" }, ["created_at"] = "2026-09-03T10:00:00Z", ["system"] = false },
            new JsonObject { ["id"] = 11, ["body"] = "changed the label", ["author"] = new JsonObject { ["username"] = "alice" }, ["created_at"] = "2026-09-04T10:00:00Z", ["system"] = true },
        ];
        eal.Wiki["glossary"] = ("Glossary", "**Basket**: what a customer is about to buy.");
        eal.Wiki["pricing"] = ("Pricing", new string('x', Tools.WikiBudget + 10));

        var open = (await Answer("issue_list", new { project = "root/eal-core" })).AsArray();
        Assert.Equal([2L], open.Select(i => i!["iid"]!.GetValue<long>()));
        Assert.Equal(("Free shipping over 50", "Q4", 2), (open[0]!["title"]!.GetValue<string>(), open[0]!["milestone"]!.GetValue<string>(), open[0]!["comments"]!.GetValue<int>()));
        Assert.Equal([2L, 1L], (await Answer("issue_list", new { project = "root/eal-core", state = "all" })).AsArray().Select(i => i!["iid"]!.GetValue<long>()));
        Assert.Equal([1L], (await Answer("issue_list", new { project = "root/eal-core", state = "all", search = "refunds" })).AsArray().Select(i => i!["iid"]!.GetValue<long>()));
        Assert.Equal([2L], (await Answer("issue_list", new { project = "root/eal-core", state = "all", labels = "pricing" })).AsArray().Select(i => i!["iid"]!.GetValue<long>()));
        Assert.True((await Call("issue_list", new { project = "root/eal-core", state = "merged" }))["isError"]!.GetValue<bool>());

        var refunds = await Answer("issue_get", new { project = "root/eal-core", iid = 1 });
        Assert.Equal(("Finance asks refunds to round half up, as invoices do.", "root/eal-core"), (refunds["description"]!.GetValue<string>(), refunds["project"]!.GetValue<string>()));
        // The people's comments, not GitLab's own notes.
        Assert.Equal(["Agreed with finance on 3 Sept."], refunds["notes"]!.AsArray().Select(n => n!["body"]!.GetValue<string>()));
        Assert.True((await Call("issue_get", new { project = "root/eal-core", iid = 9 }))["isError"]!.GetValue<bool>());

        Assert.Equal(["glossary", "pricing"], (await Answer("wiki_list", new { project = "root/eal-core" }))["pages"]!.AsArray().Select(p => p!["slug"]!.GetValue<string>()));
        Assert.Equal("**Basket**: what a customer is about to buy.", (await Answer("wiki_get", new { project = "root/eal-core", slug = "glossary" }))["content"]!.GetValue<string>());
        var long_ = await Answer("wiki_get", new { project = "root/eal-core", slug = "pricing" });
        Assert.Equal((Tools.WikiBudget, true), (long_["content"]!.GetValue<string>().Length, long_["content_truncated"]!.GetValue<bool>()));
        // Read only: Argus never wrote to GitLab.
        Assert.All(_gitlab.Requests, r => Assert.Equal("GET", r.Method));
    }

    async Task<HttpResponseMessage> Review(string pathAndQuery, string? email = Alpha, string token = Chat)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (email is not null) req.Headers.Add(ArgusServer.ChatEmailHeader, email);
        return await _http.SendAsync(req);
    }

    // --- what each tool answers ---------------------------------------------------------------

    [Fact]
    public async Task Merge_requests_are_listed_and_read_in_a_project_left_out_of_the_index()
    {
        var list = (await Answer("mr_list", new { project = "root/eal-core" })).AsArray();
        var mr = Assert.Single(list)!;
        Assert.Equal((1L, "Sum a frame's bytes", "dev_alpha", "frame-sum"),
            (mr["iid"]!.GetValue<long>(), mr["title"]!.GetValue<string>(), mr["author"]!.GetValue<string>(), mr["source_branch"]!.GetValue<string>()));
        Assert.Empty((await Answer("mr_list", new { project = "root/eal-core", state = "merged" })).AsArray());

        var eal = _gitlab[ReviewFixture.EalCore];
        var got = await Answer("mr_get", new { project = "ROOT/EAL-CORE", iid = 1 });
        Assert.Equal(eal.Sha("refs/heads/frame-sum"), got["diff_refs"]!["head_sha"]!.GetValue<string>());
        Assert.Equal(2, got["versions"]!.AsArray().Count);
        Assert.Equal(["bob"], got["reviewers"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal("failed", got["head_pipeline"]!["status"]!.GetValue<string>());
        Assert.Contains("Adds SumFrame", got["description"]!.GetValue<string>(), StringComparison.Ordinal);

        // By its GitLab id, too.
        Assert.Equal("root/eal-core", (await Answer("mr_get", new { project = "11", iid = 1 }))["project"]!.GetValue<string>());
        foreach (var tool in new[] { "mr_get", "mr_diff", "mr_commits", "mr_discussions", "ci_status" })
        {
            var missing = await Call(tool, new { project = "root/eal-core", iid = 99 });
            Assert.True(missing["isError"]!.GetValue<bool>(), tool);
            Assert.Contains("GitLab has no merge request !99 in root/eal-core", missing["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        _gitlab.AssertOnlyReads();
    }

    [Fact]
    public async Task A_diff_whole_by_path_at_a_version_and_since_a_commit()
    {
        var diff = await Answer("mr_diff", new { project = "root/eal-core", iid = 1 });
        var files = diff["files"]!.AsArray();
        Assert.Equal(["config/upload.env", "src/decoder.c"], files.Select(f => f!["new_path"]!.GetValue<string>()).Order());
        var decoder = files.Single(f => f!["new_path"]!.GetValue<string>() == "src/decoder.c")!;
        Assert.Contains("+    for (int i = 1; i <= len; i++) total += buf[i];", decoder["diff"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(7, decoder["additions"]!.GetValue<int>());

        Assert.Single((await Answer("mr_diff", new { project = "root/eal-core", iid = 1, path = "config/*" }))["files"]!.AsArray());

        var versions = (await Answer("mr_get", new { project = "root/eal-core", iid = 1 }))["versions"]!.AsArray();
        var first = versions[^1]!;
        var atFirst = await Answer("mr_diff", new { project = "root/eal-core", iid = 1, version = first["id"]!.GetValue<long>() });
        Assert.Equal(["src/decoder.c"], atFirst["files"]!.AsArray().Select(f => f!["new_path"]!.GetValue<string>()));
        Assert.Equal(first["head_sha"]!.GetValue<string>(), atFirst["diff_refs"]!["head_sha"]!.GetValue<string>());

        // What the second push changed: the upload settings alone.
        var since = await Answer("mr_diff", new { project = "root/eal-core", iid = 1, since_sha = first["head_sha"]!.GetValue<string>() });
        Assert.Equal(["config/upload.env"], since["files"]!.AsArray().Select(f => f!["new_path"]!.GetValue<string>()));

        var both = await Call("mr_diff", new { project = "root/eal-core", iid = 1, version = 1, since_sha = "abcdef1" });
        Assert.Contains("not both", both["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_diff_past_the_answers_size_lists_the_rest_and_one_file_alone_still_shows()
    {
        var eal = _gitlab[ReviewFixture.EalCore];
        var big = string.Concat(Enumerable.Range(0, 4000).Select(i => $"line {i:0000} of a generated table, long enough to count\n"));
        eal.Commit("big-table", new() { ["data/a-table.txt"] = big, ["data/b-small.txt"] = "small\n" }, "A big table");
        eal.OpenMr(2, "big-table", "A big table");

        var diff = await Answer("mr_diff", new { project = "root/eal-core", iid = 2 });
        var files = diff["files"]!.AsArray();
        var table = files.Single(f => f!["new_path"]!.GetValue<string>() == "data/a-table.txt")!;
        Assert.True(table["truncated"]!.GetValue<bool>());
        Assert.Equal(Tools.DiffBudget, table["diff"]!.GetValue<string>().Length);
        var small = files.Single(f => f!["new_path"]!.GetValue<string>() == "data/b-small.txt")!;
        Assert.Null(small["diff"]);
        Assert.StartsWith("over this answer's size", small["omitted"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, diff["omitted_files"]!.GetValue<int>());
        Assert.Equal("@@ -0,0 +1 @@\n+small\n",
            (await Answer("mr_diff", new { project = "root/eal-core", iid = 2, path = "data/b-small.txt" }))["files"]![0]!["diff"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_older_gitlab_without_paged_diffs_answers_through_changes()
    {
        _gitlab.NoDiffsEndpoint = true;
        var files = (await Answer("mr_diff", new { project = "root/eal-core", iid = 1 }))["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        Assert.Contains(_gitlab.Requests, r => r.Path.Contains("/merge_requests/1/changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commits_discussions_and_ci_with_its_log_masked()
    {
        var commits = (await Answer("mr_commits", new { project = "root/eal-core", iid = 1 })).AsArray();
        Assert.Equal(["Upload settings", "Add SumFrame"], commits.Select(c => c!["title"]!.GetValue<string>()));

        var threads = (await Answer("mr_discussions", new { project = "root/eal-core", iid = 1 })).AsArray();
        var thread = Assert.Single(threads)!;
        var note = thread["notes"]![0]!;
        Assert.Equal(("bob", "src/decoder.c", 9L), (note["author"]!.GetValue<string>(), note["position"]!["new_path"]!.GetValue<string>(),
            note["position"]!["new_line"]!.GetValue<long>()));
        Assert.False(thread["resolved"]!.GetValue<bool>());
        Assert.Equal(2, (await Answer("mr_discussions", new { project = "root/eal-core", iid = 1, include_system = true })).AsArray().Count);

        var ci = await Answer("ci_status", new { project = "root/eal-core", iid = 1 });
        Assert.Equal("failed", ci["pipeline"]!["status"]!.GetValue<string>());
        var failed = Assert.Single(ci["failed_jobs"]!.AsArray())!;
        Assert.Equal("test", failed["name"]!.GetValue<string>());
        var bySha = await Answer("ci_status", new { project = "root/eal-core", sha = ci["pipeline"]!["sha"]!.GetValue<string>() });
        Assert.Equal(ci["pipeline"]!["id"]!.GetValue<long>(), bySha["pipeline"]!["id"]!.GetValue<long>());

        var log = await Answer("job_log", new { project = "root/eal-core", job_id = failed["id"]!.GetValue<long>() });
        var text = log["log"]!.GetValue<string>();
        Assert.DoesNotContain(ReviewFixture.FakeSecret, text, StringComparison.Ordinal);
        Assert.Contains("export ARCHIVE_TOKEN=[removed: GitLab token]", text, StringComparison.Ordinal);
        Assert.Contains("FAIL SumFrame reads past the end", text, StringComparison.Ordinal);
        Assert.Contains("progress 100%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("progress 10%\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("section_start", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        _gitlab.AssertOnlyReads();
    }

    [Fact]
    public async Task Compare_and_code_at_a_merge_requests_head_a_branch_nobody_indexed_and_a_commit()
    {
        var compared = await Answer("compare_refs", new { project = "root/eal-core", from = "main", to = "frame-sum" });
        Assert.Equal(2, compared["commits"]!.AsArray().Count);
        Assert.Equal(2, compared["files"]!.AsArray().Count);

        // The merge request's head, fetched into a mirror made for it (the project is not indexed).
        var file = await Answer("file_at", new { project = "root/eal-core", iid = 1, path = "src/decoder.c", start_line = 7, end_line = 8 });
        Assert.Equal("int SumFrame(const char* buf, int len) {\n    int total = buf[0];\n", file["content"]!.GetValue<string>());
        Assert.Equal((7, 8, 12), (file["start_line"]!.GetValue<int>(), file["end_line"]!.GetValue<int>(), file["total_lines"]!.GetValue<int>()));
        var mirror = Mirror.MirrorPath(_ix.Config().Index, ReviewFixture.EalCore);
        Assert.Equal(file["sha"]!.GetValue<string>(), CodeAt.Resolve(mirror, "refs/arena/mr/1"));
        Assert.Contains(1L, CodeAt.Kept(_ix.DbPath, ReviewFixture.EalCore));

        // The default branch has no SumFrame; a branch nobody indexed is read too; and a commit by its id.
        var main = await Answer("file_at", new { project = "root/eal-core", path = "src/decoder.c" });
        Assert.DoesNotContain("SumFrame", main["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("int Hotfix(void) { return 1; }\n", (await Answer("file_at", new { project = "root/eal-core", @ref = "hotfix", path = "src/hotfix.c" }))["content"]!.GetValue<string>());
        _gitlab.AssertOnlyReads();
    }

    [Fact]
    public async Task Code_at_a_commit_says_what_is_not_there_and_refuses_what_is_not_a_ref_or_a_path()
    {
        var first = _gitlab[ReviewFixture.EalCore].Mrs[1].Versions[0].Head;
        await Answer("file_at", new { project = "root/eal-core", iid = 1, path = "README.md" });
        var gone = await Call("file_at", new { project = "root/eal-core", @ref = first[..10], path = "config/upload.env" });
        Assert.True(gone["isError"]!.GetValue<bool>());
        Assert.Contains("has no file 'config/upload.env'", gone["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("SumFrame", (await Answer("file_at", new { project = "root/eal-core", @ref = first, path = "src/decoder.c" }))["content"]!.GetValue<string>(),
            StringComparison.Ordinal);

        foreach (var bad in new[] { "--upload-pack=touch /tmp/x", "main..frame-sum", "HEAD@{1}", "refs/arena/mr/1", "a b" })
        {
            var refused = await Call("file_at", new { project = "root/eal-core", @ref = bad, path = "README.md" });
            Assert.True(refused["isError"]!.GetValue<bool>(), bad);
        }
        var directory = await Call("file_at", new { project = "root/eal-core", path = "src" });
        Assert.Contains("has no file 'src'", directory["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var escape = await Call("file_at", new { project = "root/eal-core", path = "../../etc/passwd" });
        Assert.Contains("is not a file path", escape["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var nothing = await Call("file_at", new { project = "root/eal-core", @ref = "no-such-branch", path = "README.md" });
        Assert.Contains("has no branch, tag or commit no-such-branch", nothing["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        _gitlab.AssertOnlyReads();
    }

    [Fact]
    public async Task Grep_at_a_merge_requests_head_and_at_the_default_branch()
    {
        var hits = await Answer("grep_at", new { project = "root/eal-core", iid = 1, pattern = "Sum[A-Z][a-z]+\\(" });
        var hit = Assert.Single(hits["matches"]!.AsArray())!;
        Assert.Equal(("src/decoder.c", 7L), (hit["path"]!.GetValue<string>(), hit["line"]!.GetValue<long>()));
        Assert.Empty((await Answer("grep_at", new { project = "root/eal-core", pattern = "SumFrame" }))["matches"]!.AsArray());

        var limited = await Answer("grep_at", new { project = "root/eal-core", iid = 1, pattern = "buf", limit = 2 });
        Assert.Equal(2, limited["matches"]!.AsArray().Count);
        Assert.True(limited["more"]!.GetValue<bool>());
        Assert.Single((await Answer("grep_at", new { project = "root/eal-core", iid = 1, pattern = "archive_token", ignore_case = true, path = "config" }))["matches"]!.AsArray());
        // A pattern is never an option of git's.
        Assert.Empty((await Answer("grep_at", new { project = "root/eal-core", pattern = "--open-files-in-pager=sh" }))["matches"]!.AsArray());
    }

    [Fact]
    public async Task Deps_check_matches_a_branchs_locked_packages_against_the_advisories_and_sbom_lists_them()
    {
        // Three real advisories (lodash, requests, Newtonsoft.Json), as an installed advisories pack.
        var packs = Path.Combine(_ix.Root, "packs");
        Directory.CreateDirectory(packs);
        Packs.PackBuilder.BuildPack(new Packs.Sources.AdvisoriesTexts(), Path.Combine(AppContext.BaseDirectory, "packfixtures", "advisories"),
            Path.Combine(packs, "advisories.arguspack"), "1", FakeEmbedder.Embed, log: TextWriter.Null);
        _gitlab[ReviewFixture.EalCore].Commit("deps", new()
        {
            ["web/package-lock.json"] = """{"lockfileVersion": 3, "packages": {"": {"dependencies": {"lodash": "^4"}}, "node_modules/lodash": {"version": "4.17.20"}, "node_modules/ms": {"version": "2.1.3"}}}""",
            ["svc/requirements.txt"] = "requests==2.30.0\nflask>=2\n",
            ["api/packages.lock.json"] = """{"version": 1, "dependencies": {"net8.0": {"Some.Lib": {"type": "Direct", "resolved": "1.0.0", "dependencies": {"Newtonsoft.Json": "12.0.3"}}, "Newtonsoft.Json": {"type": "Transitive", "resolved": "12.0.3"}}}}""",
            ["web/node_modules/old/package-lock.json"] = """{"lockfileVersion": 3, "packages": {"node_modules/lodash": {"version": "3.0.0"}}}""",
        }, "Dependencies");

        var check = await Answer("deps_check", new { project = "root/eal-core", @ref = "deps" });
        Assert.Equal(("deps", 6), (check["ref"]!.GetValue<string>(), check["dependencies"]!.GetValue<int>()));
        var findings = check["findings"]!.AsArray().Select(f => f!.AsObject()).ToList();
        Assert.Equal(["Newtonsoft.Json", "lodash", "requests"], findings.Select(f => f["package"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var lodash = findings.Single(f => f["package"]!.GetValue<string>() == "lodash");
        Assert.Equal(("4.17.20", true, "web/package-lock.json", "4.17.21", "GHSA-35jh-r3h4-6jhm", "high"),
            (lodash["version"]!.GetValue<string>(), lodash["direct"]!.GetValue<bool>(), lodash["file"]!.GetValue<string>(), lodash["upgrade_to"]!.GetValue<string>(),
             lodash["advisory"]!.GetValue<string>(), lodash["severity"]!.GetValue<string>()));
        Assert.Contains("CVE-2021-23337", lodash["aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        var json = findings.Single(f => f["package"]!.GetValue<string>() == "Newtonsoft.Json");
        Assert.Equal((false, "Some.Lib", "13.0.1"), (json["direct"]!.GetValue<bool>(), json["via"]!.GetValue<string>(), json["upgrade_to"]!.GetValue<string>()));
        Assert.Equal("2026-10-27", check["advisories"]![0]!["as_of"]!.GetValue<string>());
        // Nothing found on the default branch, which has no lock files; vendored lock files are never read.
        Assert.Empty((await Answer("deps_check", new { project = "root/eal-core" }))["findings"]!.AsArray());

        var bom = await Answer("sbom", new { project = "root/eal-core", @ref = "deps" });
        Assert.Equal("CycloneDX", bom["bomFormat"]!.GetValue<string>());
        Assert.Contains("pkg:nuget/Newtonsoft.Json@12.0.3", bom["components"]!.AsArray().Select(c => c!["purl"]!.GetValue<string>()));
        Assert.DoesNotContain(bom["components"]!.AsArray(), c => c!["version"]!.GetValue<string>() == "3.0.0");
        _gitlab.AssertOnlyReads();
    }

    [Fact]
    public async Task A_merged_or_closed_merge_requests_head_is_dropped_and_one_unused_for_thirty_days_too()
    {
        await Answer("file_at", new { project = "root/eal-core", iid = 1, path = "README.md" });
        var mirror = Mirror.MirrorPath(_ix.Config().Index, ReviewFixture.EalCore);
        Assert.NotNull(CodeAt.Resolve(mirror, "refs/arena/mr/1"));

        _gitlab[ReviewFixture.EalCore].Mrs[1].State = "merged";
        await Answer("mr_get", new { project = "root/eal-core", iid = 1 });
        Assert.Null(CodeAt.Resolve(mirror, "refs/arena/mr/1"));
        Assert.DoesNotContain(1L, CodeAt.Kept(_ix.DbPath, ReviewFixture.EalCore));

        _gitlab[ReviewFixture.EalCore].Mrs[1].State = "opened";
        await Answer("file_at", new { project = "root/eal-core", iid = 1, path = "README.md" });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        CodeAt.Prune(_ix.DbPath, mirror, ReviewFixture.EalCore, now + (CodeAt.UnusedDays - 1) * 86400L);
        Assert.NotNull(CodeAt.Resolve(mirror, "refs/arena/mr/1"));
        CodeAt.Prune(_ix.DbPath, mirror, ReviewFixture.EalCore, now + (CodeAt.UnusedDays + 1) * 86400L);
        Assert.Null(CodeAt.Resolve(mirror, "refs/arena/mr/1"));
    }

    // --- who may read what ------------------------------------------------------------------

    /// <summary>Every merge request and commit tool, asked about driver-shim; a new tool fails here until it is added.</summary>
    static readonly Dictionary<string, object> AboutShim = new()
    {
        ["mr_list"] = new { project = "root/driver-shim" },
        ["mr_get"] = new { project = "root/driver-shim", iid = 1 },
        ["mr_diff"] = new { project = "root/driver-shim", iid = 1 },
        ["mr_commits"] = new { project = "root/driver-shim", iid = 1 },
        ["mr_discussions"] = new { project = "root/driver-shim", iid = 1 },
        ["ci_status"] = new { project = "root/driver-shim", iid = 1 },
        ["job_log"] = new { project = "root/driver-shim", job_id = 1 },
        ["compare_refs"] = new { project = "root/driver-shim", from = "main", to = "shim-secret" },
        ["file_at"] = new { project = "root/driver-shim", @ref = "shim-secret", path = "src/shim.c" },
        ["grep_at"] = new { project = "root/driver-shim", @ref = "shim-secret", pattern = "SHIM_ONLY" },
        ["deps_check"] = new { project = "root/driver-shim", @ref = "shim-secret" },
        ["sbom"] = new { project = "root/driver-shim", @ref = "shim-secret" },
        ["issue_list"] = new { project = "root/driver-shim" },
        ["issue_get"] = new { project = "root/driver-shim", iid = 1 },
        ["wiki_list"] = new { project = "root/driver-shim" },
        ["wiki_get"] = new { project = "root/driver-shim", slug = "pricing" },
    };

    [Fact]
    public async Task Dev_alpha_never_sees_driver_shim_in_any_tool_by_path_or_id_through_mcp_or_the_routes()
    {
        Assert.Equal(Tools.ReviewTools.Order(), AboutShim.Keys.Order());
        var mirror = Mirror.MirrorPath(_ix.Config().Index, ReviewFixture.DriverShim);
        foreach (var (tool, args) in AboutShim)
        {
            foreach (var asked in new object[] { args, WithProject(args, ReviewFixture.DriverShim.ToString()) })
            {
                var result = await Call(tool, asked);
                Assert.True(result["isError"]!.GetValue<bool>(), $"{tool}: {result.ToJsonString()}");
                var text = result["content"]![0]!["text"]!.GetValue<string>();
                Assert.Contains("that you can read is known to Argus", text, StringComparison.Ordinal);
                Assert.DoesNotContain("SHIM_ONLY", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Shim-only", text, StringComparison.Ordinal);
            }
            // The same through the app's route: as a project that does not exist.
            var query = string.Join("&", JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject()
                .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!.ToString())}"));
            var resp = await Review($"/review/{tool}?{query}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.DoesNotContain("SHIM_ONLY", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        // GitLab was asked who its members are, and nothing else about it; no mirror was made for it.
        Assert.DoesNotContain(_gitlab.Requests, r => r.Path.StartsWith("/api/v4/projects/12/", StringComparison.Ordinal) && !r.Path.Contains("/members/all", StringComparison.Ordinal));
        Assert.False(Directory.Exists(mirror));

        // Bob, a Maintainer there, reads it.
        var shim = await Answer("grep_at", new { project = "root/driver-shim", @ref = "shim-secret", pattern = "SHIM_ONLY" }, "bob@corp.example");
        Assert.Single(shim["matches"]!.AsArray());
        _gitlab.AssertOnlyReads();
    }

    static object WithProject(object args, string project)
    {
        var o = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
        o["project"] = project;
        return o;
    }

    [Fact]
    public async Task A_guest_is_not_a_reader_and_an_unknown_project_answers_as_one_you_cannot_read()
    {
        var guest = await Call("mr_list", new { project = "root/eal-core" }, "gary@argus.test");
        Assert.True(guest["isError"]!.GetValue<bool>());
        Assert.Contains("that you can read is known to Argus", guest["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var unknown = await Call("mr_list", new { project = "root/no-such-project" });
        Assert.Equal(guest["content"]![0]!["text"]!.GetValue<string>().Replace("root/eal-core", "X"),
            unknown["content"]![0]!["text"]!.GetValue<string>().Replace("root/no-such-project", "X"));
    }

    [Fact]
    public async Task The_routes_take_only_the_chat_credential_with_an_email_and_answer_as_the_tools()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Review("/review/mr_list?project=root/eal-core", token: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Review("/review/mr_list?project=root/eal-core", email: null)).StatusCode);
        var viaProxy = new HttpRequestMessage(HttpMethod.Get, "/review/mr_list?project=root/eal-core");
        viaProxy.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Chat);
        viaProxy.Headers.Add(ArgusServer.ChatEmailHeader, Alpha);
        viaProxy.Headers.Add("x-forwarded-for", "203.0.113.9");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(viaProxy)).StatusCode);

        var list = await Review("/review/mr_list?project=root/eal-core");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(1, JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray()[0]!["iid"]!.GetValue<int>());
        var file = await Review("/review/file_at?project=root/eal-core&iid=1&path=src/decoder.c&start_line=7&end_line=7");
        Assert.Equal("int SumFrame(const char* buf, int len) {\n", JsonNode.Parse(await file.Content.ReadAsStringAsync())!["content"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await Review("/review/mr_get?project=root/eal-core&iid=one")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Review("/review/mr_get?project=root/eal-core&iid=99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Review("/review/find_symbol?name=x")).StatusCode);

        // Whether a person may read a project: the one asking, or another the app names.
        async Task<bool> May(string query)
        {
            var resp = await Review("/review/access?" + query);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["may_read"]!.GetValue<bool>();
        }
        Assert.True(await May("project=root/eal-core"));
        Assert.False(await May("project=root/driver-shim"));
        Assert.True(await May("project=root/driver-shim&email=bob@corp.example"));
        Assert.False(await May("project=root/eal-core&email=gary@argus.test"));
        Assert.False(await May("project=root/eal-core&email=nobody@corp.example"));

        // Audited as the tool calls are, the person named.
        Assert.Contains("\"tool\": \"mr_list\"", _log.ToString(), StringComparison.Ordinal);
        _gitlab.AssertOnlyReads();
    }

    [Fact]
    public async Task With_gitlab_down_a_review_tool_says_so_and_not_that_the_project_is_missing()
    {
        await Answer("mr_list", new { project = "root/eal-core" });
        _gitlab.Down = true;
        var down = await Call("mr_get", new { project = "root/eal-core", iid = 1 });
        Assert.True(down["isError"]!.GetValue<bool>());
        Assert.Contains("GitLab could not answer", down["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadGateway, (await Review("/review/mr_get?project=root/eal-core&iid=1")).StatusCode);
    }
}

/// <summary>Access notices on: a project the person may not read is named, with whom to ask, and still not read.</summary>
[Collection("process-state")]
public sealed class ReviewAccessNoticeTests : IAsyncLifetime
{
    readonly TestIndex _ix = new();
    FakeGitLabMr _gitlab = null!;

    public Task InitializeAsync()
    {
        _gitlab = ReviewFixture.Build();
        Choices.Record(_ix.Conn, [new Project(ReviewFixture.DriverShim, "root/driver-shim", "main", _gitlab[ReviewFixture.DriverShim].Bare)], 1000);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _ix.Dispose();
        await _gitlab.DisposeAsync();
    }

    [Fact]
    public void The_notice_names_the_maintainers_and_nothing_of_the_project()
    {
        using var env = new EnvScope(("ARGUS_ACCESS_NOTICES", "1"));
        var cfg = _ix.Config() with { GitLab = GitLabConfig.Create(_gitlab.Url, FakeGitLabMr.ServiceToken) };
        var tools = new Tools(cfg);
        var access = PersonAccess.For(_ix.DbPath, new Lazy<MemberDirectory>(() => new MemberDirectory(cfg.GitLab)));
        var alpha = access.Resolve("dev_alpha@argus.test", null);
        var notice = Assert.Throws<AccessNotice>(() => tools.MrGet(alpha, "root/driver-shim", 1));
        Assert.StartsWith("You cannot read root/driver-shim in GitLab", notice.Message, StringComparison.Ordinal);
        Assert.Contains("Its maintainers: @bob (Bob).", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_gitlab.Requests, r => r.Path.Contains("/merge_requests", StringComparison.Ordinal));
    }
}

/// <summary>Who may read a GitLab project that is not indexed: asked once, kept with the rest.</summary>
public sealed class ProjectRightsTests
{
    [Fact]
    public void A_project_left_out_of_the_index_is_checked_when_first_asked_and_kept()
    {
        var asked = new List<string>();
        var access = new PersonAccess((_, _, projects) =>
        {
            lock (asked) asked.Add(string.Join(",", projects.Order()));
            return new Grant(5, "alice", projects.ToHashSet(), projects.Where(p => p is 100 or 300).ToHashSet());
        }, () => [(1, 100)], () => 1000);
        var alice = access.Resolve("alice@corp.example", "alice");
        Assert.True(alice.MayRead(100));
        Assert.True(alice.MayRead(300));
        Assert.False(alice.MayRead(400));
        Assert.True(alice.MayRead(300));
        // The indexed project with the person's first resolution; each other one once, when first asked.
        Assert.Equal(["100", "300", "400"], asked);
        Assert.Equal([1L], alice.AllowedRepoIds);
    }

    [Fact]
    public void GitLab_unwell_for_a_project_not_checked_yet_says_so_instead_of_no()
    {
        var down = false;
        var access = new PersonAccess((_, _, projects) => down
            ? throw new AclDenied("Cannot verify", new GitLabUnavailable("503"))
            : new Grant(5, "alice", projects.ToHashSet(), projects.ToHashSet()), () => [(1, 100)], () => 1000);
        var alice = access.Resolve("alice@corp.example", null);
        down = true;
        Assert.True(alice.MayRead(100));
        Assert.Throws<AclDenied>(() => alice.MayRead(300));
        down = false;
        Assert.True(alice.MayRead(300));
    }

    [Fact]
    public void A_gitlab_tokens_projects_are_kept_with_its_rows_and_rows_kept_before_read_none()
    {
        using var ix = new TestIndex();
        var repo = ix.Repo(11, "grp/alpha");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Writes.UpsertAclCache(ix.Conn, Acl.Hash("glpat-alice"), 5, "alice", $"[{repo}]", now, "[11, 40]");
        var cfg = GitLabConfig.Create("http://127.0.0.1:9", "svc");
        var alice = Acl.Resolve(ix.Conn, cfg, "glpat-alice", now: () => now);
        Assert.Equal([repo], alice.AllowedRepoIds);
        Assert.True(alice.MayRead(11));
        Assert.True(alice.MayRead(40));
        Assert.False(alice.MayRead(12));

        Writes.UpsertAclCache(ix.Conn, Acl.Hash("glpat-old"), 6, "old", $"[{repo}]", now);
        Assert.False(Acl.Resolve(ix.Conn, cfg, "glpat-old", now: () => now).MayRead(11));
    }
}

/// <summary>What leaves Argus from a job's log.</summary>
public sealed class SecretMaskTests
{
    [Theory]
    [InlineData("token glpat-abcdefghijklmnopqrstu0 used", "token [removed: GitLab token] used")]
    [InlineData("AKIAABCDEFGHIJKLMNOP", "[removed: AWS access key]")]
    [InlineData("git clone https://ci:s3cr3tpass@gitlab.corp/x.git", "git clone https://ci:[removed: password]@gitlab.corp/x.git")]
    [InlineData("PASSWORD=hunter2hunter2", "PASSWORD=[removed: password]")]
    [InlineData("password=$CI_PASSWORD", "password=$CI_PASSWORD")]
    [InlineData("no secrets here", "no secrets here")]
    public void Secrets_are_taken_out_and_placeholders_kept(string text, string masked) => Assert.Equal(masked, SecretMask.Mask(text));

    [Fact]
    public void A_logs_colours_sections_and_overwritten_lines_are_taken_out()
    {
        var log = "section_start:1:prepare\r\u001b[0K\u001b[32;1mPreparing\u001b[0;m\nstep 1\rstep 2\r\nsection_end:1:prepare\r\u001b[0K";
        Assert.Equal("Preparing\nstep 2\n", Tools.CleanLog(log));
    }
}
