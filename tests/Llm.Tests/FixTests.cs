using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Code.Fixes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Fix it: a person's own GitLab token linked; the branch read with it, the agent's change kept as a diff; the merge request
/// opened as them; what to fix read from a review finding, an issue or a failed pipeline.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class FixTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("fixes_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-fix-tests", ["GitLab:Url"] = "https://gitlab.test",
        });

    private static async Task<TestBrowser> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "f" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Kim" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    private static async Task<JsonElement> DoneAsync(TestBrowser b, WebApplicationFactory<Program> f, Guid fix)
    {
        await f.Services.GetRequiredService<FixRuns>().Finished(fix).WaitAsync(TimeSpan.FromSeconds(30));
        return await b.JsonAsync(await b.GetAsync($"/api/fixes/{fix}"));
    }

    private const string Script = """[script [[{"name":"search","arguments":{"text":"def add"}}],[{"name":"read_file","arguments":{"path":"calc.py"}}],[{"name":"replace","arguments":{"path":"calc.py","old":"return a - b","new":"return a + b"}}],[{"name":"run_tests","arguments":{"command":"python -m unittest"}}],[{"name":"finish","arguments":{"summary":"add() adds now."}}]]]""";

    [Fact]
    public async Task With_the_persons_own_token_the_agent_changes_the_branch_and_the_merge_request_is_opened_as_them()
    {
        await using var f = NewApp();
        var kim = await PersonAsync(f);
        // Nothing without one's own token.
        await StatusAssert.Is(HttpStatusCode.Conflict, await kim.PostAsync("/api/fixes", new { project = "group/app", ask = "Fix add" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await kim.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "not-a-good-one" }));
        var linked = await kim.JsonAsync(await kim.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "person-token" }));
        Assert.Equal("kim", linked.GetProperty("account").GetString());
        Assert.DoesNotContain("person-token", await (await kim.GetAsync("/api/fixes/token")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await kim.PostAsync("/api/fixes", new { project = "app", ask = "Fix add" }));
        var started = await kim.JsonAsync(await kim.PostAsync("/api/fixes", new { project = "group/app", ask = "add() subtracts: fix it. " + Script }));
        var fix = await DoneAsync(kim, f, started.GetProperty("id").GetGuid());
        Assert.Equal(("ready", "main", "sha1"), (fix.GetProperty("status").GetString(), fix.GetProperty("branch").GetString(), fix.GetProperty("baseSha").GetString()));
        Assert.Equal("add() adds now.", fix.GetProperty("summary").GetString());
        var diff = fix.GetProperty("diff").GetString()!;
        Assert.Contains("--- a/calc.py\n+++ b/calc.py\n@@ -1,2 +1,2 @@\n def add(a, b):\n-    return a - b\n+    return a + b\n", diff, StringComparison.Ordinal);
        Assert.Equal(["search", "read_file", "replace", "run_tests", "finish"], fix.GetProperty("log").EnumerateArray().Select(s => s.GetProperty("tool").GetString()));
        // The sandbox is not running here: the tests say so, and the fix is still ready.
        Assert.StartsWith("Tests cannot run here", fix.GetProperty("tests").GetProperty("output").GetString(), StringComparison.Ordinal);

        // The merge request, as Kim: a new branch from the commit it read, into main.
        var mr = await kim.JsonAsync(await kim.PostAsync($"/api/fixes/{fix.GetProperty("id").GetGuid()}/merge-request", new { title = "Make add() add" }));
        Assert.Equal("https://gitlab.test/group/app/-/merge_requests/12", mr.GetProperty("url").GetString());
        var commit = app.Mcp.GitLabCalls.Last(c => c.PathAndQuery == "/api/v4/projects/group%2Fapp/repository/commits");
        var sent = JsonDocument.Parse(commit.Body!).RootElement;
        Assert.Equal(("sha1", "update", "calc.py"), (sent.GetProperty("start_sha").GetString(), sent.GetProperty("actions")[0].GetProperty("action").GetString(), sent.GetProperty("actions")[0].GetProperty("file_path").GetString()));
        Assert.Equal("def add(a, b):\n    return a + b\n", sent.GetProperty("actions")[0].GetProperty("content").GetString());
        var opened = JsonDocument.Parse(app.Mcp.GitLabCalls.Last(c => c.PathAndQuery == "/api/v4/projects/group%2Fapp/merge_requests").Body!).RootElement;
        Assert.Equal(("main", "Make add() add"), (opened.GetProperty("target_branch").GetString(), opened.GetProperty("title").GetString()));
        Assert.StartsWith("fix/", opened.GetProperty("source_branch").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Conflict, await kim.PostAsync($"/api/fixes/{fix.GetProperty("id").GetGuid()}/merge-request"));
    }

    [Fact]
    public async Task What_to_fix_is_read_from_an_issue_or_a_failed_pipelines_logs()
    {
        await using var f = NewApp();
        var kim = await PersonAsync(f);
        await kim.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "person-token" });

        var issue = await kim.JsonAsync(await kim.PostAsync("/api/fixes", new { project = "group/app", issue = 5, ask = Script }));
        var fromIssue = await DoneAsync(kim, f, issue.GetProperty("id").GetGuid());
        Assert.Equal(("issue", "add() subtracts"), (fromIssue.GetProperty("source").GetString(), fromIssue.GetProperty("title").GetString()));
        Assert.Contains("calc.add(2, 3) gives -1.", fromIssue.GetProperty("ask").GetString(), StringComparison.Ordinal);
        Assert.Equal("ready", fromIssue.GetProperty("status").GetString());

        var pipeline = await kim.JsonAsync(await kim.PostAsync("/api/fixes", new { project = "group/app", pipeline = 9 }));
        var fromPipeline = await DoneAsync(kim, f, pipeline.GetProperty("id").GetGuid());
        Assert.Contains("AssertionError: -1 != 5", fromPipeline.GetProperty("ask").GetString(), StringComparison.Ordinal);
        // The model here says words without a change: it ends so.
        Assert.Equal("failed", fromPipeline.GetProperty("status").GetString());

        Assert.Equal(2, (await kim.JsonAsync(await kim.GetAsync("/api/fixes"))).GetArrayLength());
    }

    [Fact]
    [Trait("Control", "VUL-01")]
    public async Task A_security_findings_fix_starts_from_the_branch_reviewed_with_the_weakness_its_references_and_a_test_first()
    {
        await using var f = NewApp();
        var kim = await PersonAsync(f);
        await kim.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "person-token" });
        var me = (await kim.JsonAsync(await kim.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        Guid code, package, secret, others;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var scan = new Llm.Core.Code.SecurityScan { UserId = me, Project = "group/app", Ref = "main", Commit = "abc1234567890", Status = "done", FinishedAt = DateTimeOffset.UtcNow };
            var weakness = new Llm.Core.Code.SecurityFinding
            {
                Path = "app/views.py", Line = 25, SinkCode = "cursor.execute(f\"SELECT * FROM users WHERE name = '{name}'\")", Cwe = "CWE-89", CweTitle = "CWE-89: SQL Injection",
                Severity = "high", Title = "SQL built from the request", Source = "views.py:20 request.args['name']", Flow = "request.args['name']\nname\ncursor.execute(f\"...\")",
                Exploit = "name = ' OR '1'='1", Fix = "Pass name as a parameter.", Verified = true,
                Pages = """[{"title": "CWE-89: SQL Injection", "url": "https://cwe.mitre.org/data/definitions/89.html"}, {"title": "SQL Injection Prevention Cheat Sheet", "url": "https://cheatsheetseries.owasp.org/x.html"}]""",
            };
            var vulnerable = new Llm.Core.Code.SecurityFinding
            {
                Kind = Llm.Core.Code.SecurityFinding.Dependency, Path = "web/package-lock.json", Line = 12, SinkCode = "qs 6.10.1 (npm)", Cwe = "CWE-1321", Severity = "high",
                Title = "qs vulnerable to Prototype Pollution", Source = "GHSA-hrpp-h998-j3pp, CVE-2022-24999", Flow = "qs comes in through express", Fix = "Upgrade qs to 6.10.3 or later.", Verified = true,
            };
            var key = new Llm.Core.Code.SecurityFinding { Kind = Llm.Core.Code.SecurityFinding.Secret, Path = "config/app.env", Line = 2, Cwe = "CWE-798", Severity = "high", Title = "A secret", Rule = "aws-access-token", Commit = "c0ffee000000000" };
            scan.Findings.AddRange([weakness, vulnerable, key]);
            var theirs = new Llm.Core.Code.SecurityScan { UserId = Guid.NewGuid(), Project = "group/other", Status = "done" };
            var notMine = new Llm.Core.Code.SecurityFinding { Path = "x.py", Cwe = "CWE-89", Severity = "high", Title = "x" };
            theirs.Findings.Add(notMine);
            db.Users.Add(new Llm.Core.Identity.AppUser { Id = theirs.UserId, UserName = "someone" + Guid.NewGuid().ToString("N")[..6] });
            db.SecurityScans.AddRange(scan, theirs);
            await db.SaveChangesAsync();
            (code, package, secret, others) = (weakness.Id, vulnerable.Id, key.Id, notMine.Id);
        }

        var started = await kim.JsonAsync(await kim.PostAsync("/api/fixes", new { securityFindingId = code }));
        var fix = await kim.JsonAsync(await kim.GetAsync($"/api/fixes/{started.GetProperty("id").GetGuid()}"));
        Assert.Equal(("security", "group/app", "main", "CWE-89 SQL built from the request"),
            (fix.GetProperty("source").GetString(), fix.GetProperty("project").GetString(), fix.GetProperty("branch").GetString(), fix.GetProperty("title").GetString()));
        var ask = fix.GetProperty("ask").GetString()!;
        Assert.Contains("Fix this security weakness in app/views.py at line 25: CWE-89 (CWE-89: SQL Injection), high.", ask, StringComparison.Ordinal);
        Assert.Contains("Where outside data comes in: views.py:20 request.args['name']", ask, StringComparison.Ordinal);
        Assert.Contains("First write a test that shows the weakness", ask, StringComparison.Ordinal);
        Assert.Contains("- SQL Injection Prevention Cheat Sheet: https://cheatsheetseries.owasp.org/x.html", ask, StringComparison.Ordinal);
        Assert.Contains("read commit abc123456789", ask, StringComparison.Ordinal);

        var upgrade = await kim.JsonAsync(await kim.PostAsync("/api/fixes", new { securityFindingId = package }));
        Assert.Equal("Upgrade qs: qs vulnerable to Prototype Pollution", upgrade.GetProperty("title").GetString());
        await f.Services.GetRequiredService<FixRuns>().Finished(started.GetProperty("id").GetGuid()).WaitAsync(TimeSpan.FromSeconds(30));
        await f.Services.GetRequiredService<FixRuns>().Finished(upgrade.GetProperty("id").GetGuid()).WaitAsync(TimeSpan.FromSeconds(30));
        var secretAsk = (await kim.JsonAsync(await kim.GetAsync($"/api/fixes/{(await kim.JsonAsync(await kim.PostAsync("/api/fixes", new { securityFindingId = secret }))).GetProperty("id").GetGuid()}"))).GetProperty("ask").GetString()!;
        Assert.Contains("in the history at commit c0ffee000000", secretAsk, StringComparison.Ordinal);
        Assert.Contains("must revoke it", secretAsk, StringComparison.Ordinal);
        // Another person's review is not theirs to fix from.
        await StatusAssert.Is(HttpStatusCode.NotFound, await kim.PostAsync("/api/fixes", new { securityFindingId = others }));
    }

    [Fact]
    public async Task The_workspace_reads_changes_and_shows_a_unified_diff()
    {
        var w = Workspace.FromArchive(FakeMcp.Archive(new Dictionary<string, string> { ["a.txt"] = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", ["b.txt"] = "x\n" }));
        Assert.Equal(["a.txt", "b.txt"], w.Paths);
        w.Write("a.txt", "1\n2\nthree\n4\n5\n6\n7\n8\n9\nten\n");
        w.Write("c.txt", "new\n");
        w.Delete("b.txt");
        Assert.Throws<WorkspaceException>(() => w.Read("b.txt"));
        Assert.Throws<WorkspaceException>(() => w.Write("../etc/passwd", "x"));
        var diff = w.Diff();
        // Changes six lines apart are one hunk, as diff makes them.
        Assert.Contains("@@ -1,10 +1,10 @@\n 1\n 2\n-3\n+three\n 4\n 5\n 6\n 7\n 8\n 9\n-10\n+ten\n", diff, StringComparison.Ordinal);
        Assert.Contains("--- a/b.txt\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-x\n", diff, StringComparison.Ordinal);
        Assert.Contains("--- /dev/null\n+++ b/c.txt\n@@ -0,0 +1,1 @@\n+new\n", diff, StringComparison.Ordinal);
        // Written back as it was: no change.
        w.Write("a.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        Assert.DoesNotContain("a.txt", w.Changes.Keys);
        Assert.Equal(["a.txt:2: 2"], w.Search("2", null));
    }
}
