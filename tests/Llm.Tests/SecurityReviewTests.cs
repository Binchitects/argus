using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Code;
using Llm.Core.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The security review: sinks found by git grep through Argus as the person, traced to their callers, judged by the model,
/// kept only when the sink's line is cited as the file has it and the CWE is one the security pack knows, then refuted or
/// not by a second pass; each finding fixed, accepted (why, until when) or marked wrong.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class SecurityReviewTests(AppFixture app)
{
    private static readonly Dictionary<string, string[]> Files = new()
    {
        ["src/Orders.cs"] = ["using System.Data.SqlClient;", "public class Orders {", "  public object Find(string id) {",
            "    var cmd = new SqlCommand(\"SELECT * FROM orders WHERE id = '\" + id + \"'\", conn); // [vuln]", "    return cmd.ExecuteReader();", "  }", "}"],
        ["src/Report.cs"] = ["class Report {", "  int Count() {", "    var cmd = new SqlCommand(\"SELECT count(*) FROM orders\", conn); // [harmless]", "  }", "}"],
        ["src/Search.cs"] = ["class Search {", "  void Run(string q) {", "    var cmd = new SqlCommand(\"SELECT 1 WHERE x = \" + q, conn); // [miscite]", "  }", "}"],
        ["src/Audit.cs"] = ["class Audit {", "  void Log(string who) {", "    var cmd = new SqlCommand(\"INSERT INTO log VALUES ('\" + who + \"')\", conn); // [badcwe]", "  }", "}"],
        ["src/Controller.cs"] = ["class Controller {", "  object Get() {", "", "", "", "", "", "", "    return orders.Find(Request.Query[\"id\"]);", "  }", "}"],
        ["src/Export.cs"] = ["class Export {", "  string Read(string name) {", "    return File.ReadAllText(Path.Combine(Root, name));", "  }", "}"],
    };

    /// <summary>What the scanners find: two of the model's sinks, a path the model clears, a weak hash, a secret in the history, a Dockerfile's root user.</summary>
    private const string Scanned = """
        {"project": "g/shop", "ref": "main", "sha": "abc", "history": true,
         "tools": [{"name": "opengrep", "version": "1.31.0", "ran": true, "seconds": 2.0, "findings": 4, "error": null},
                   {"name": "bandit", "version": "1.9.4", "ran": true, "seconds": 0.1, "findings": 1, "error": null},
                   {"name": "gitleaks", "version": "8.30.1", "ran": true, "seconds": 0.5, "findings": 1, "error": null},
                   {"name": "trivy", "version": "0.75.0", "ran": true, "seconds": 0.4, "findings": 1, "error": null}],
         "findings": [
           {"tools": ["opengrep", "bandit"], "rules": ["csharp-sql-built-from-strings", "B608"], "kind": "code", "path": "src/Orders.cs", "line": 4, "cwe": "CWE-89", "severity": "high", "confidence": "high", "title": "A query built from strings", "message": "Add the values as parameters.", "code": "var cmd = ..."},
           {"tools": ["opengrep"], "rules": ["csharp-sql-built-from-strings"], "kind": "code", "path": "src/Report.cs", "line": 3, "cwe": "CWE-89", "severity": "high", "confidence": "medium", "title": "A query built from strings", "message": "Add the values as parameters.", "code": "var cmd = ..."},
           {"tools": ["opengrep"], "rules": ["csharp-path-from-data"], "kind": "code", "path": "src/Export.cs", "line": 3, "cwe": "CWE-22", "severity": "high", "confidence": "medium", "title": "A file path from data", "message": "Take only a file's name.", "code": "return File.ReadAllText(Path.Combine(Root, name));"},
           {"tools": ["opengrep"], "rules": ["csharp-weak-crypto"], "kind": "code", "path": "src/Crypto.cs", "line": 2, "cwe": "CWE-327", "severity": "low", "confidence": "medium", "title": "A broken algorithm", "message": "Use SHA-256 or better.", "code": "using var md5 = MD5.Create();"},
           {"tools": ["gitleaks"], "rules": ["aws-access-token"], "kind": "secret", "path": "config/app.env", "line": 2, "cwe": "CWE-798", "severity": "high", "confidence": "medium", "title": "A secret (aws-access-token) in the history, commit c0ffee000000", "message": "Revoke it and make a new one.", "code": "", "commit": "c0ffee0000000000000000000000000000000000"},
           {"tools": ["trivy"], "rules": ["AVD-DS-0002 Image user should not be 'root'"], "kind": "config", "path": "Dockerfile", "line": 1, "cwe": "", "severity": "high", "confidence": "high", "title": "Specify at least 1 USER command in Dockerfile with non-root user as argument", "message": "Add 'USER <non root user name>' line to the Dockerfile", "code": "FROM mcr.microsoft.com/dotnet/aspnet:10.0"}],
         "more": 0}
        """;

    private void Argus()
    {
        app.Argus.Handlers["grep_at"] = args =>
        {
            var pattern = args.GetProperty("pattern").GetString()!;
            var glob = args.TryGetProperty("path", out var g) ? g.GetString() : null;
            object[] matches = pattern.Contains("SqlCommand", StringComparison.Ordinal) && glob == "*.cs"
                ? [.. Files.Where(f => f.Value.Any(l => l.Contains("SqlCommand(", StringComparison.Ordinal))).Select(f => new { path = f.Key, line = Array.FindIndex(f.Value, l => l.Contains("SqlCommand(", StringComparison.Ordinal)) + 1, text = f.Value.First(l => l.Contains("SqlCommand(", StringComparison.Ordinal)) }),
                   new { path = "tests/OrdersTests.cs", line = 5, text = "var cmd = new SqlCommand(sql, conn); // [vuln]" }]
                : pattern.Contains("Find", StringComparison.Ordinal) ? [new { path = "src/Controller.cs", line = 9, text = Files["src/Controller.cs"][8] }] : [];
            return JsonSerializer.Serialize(new { project = "g/shop", @ref = "main", matches, more = false });
        };
        app.Argus.Handlers["file_at"] = args =>
        {
            var lines = Files[args.GetProperty("path").GetString()!];
            var start = Math.Max(1, args.TryGetProperty("start_line", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 1);
            var end = Math.Min(lines.Length, args.TryGetProperty("end_line", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : lines.Length);
            return JsonSerializer.Serialize(new { start_line = start, end_line = end, total_lines = lines.Length, content = string.Join('\n', lines[(start - 1)..end]) + "\n" });
        };
        app.Argus.Handlers["docs_lookup"] = args => args.GetProperty("name").GetString() == "CWE-89"
            ? """{"result": [{"name": "CWE-89", "kind": "weakness", "namespace": "CWE", "signature": "CWE-89: SQL Injection (Base)", "doc_path": "cwe/top25/CWE-89.md", "url": "https://cwe.mitre.org/data/definitions/89.html"}]}"""
            : """{"result": []}""";
        app.Argus.Handlers["docs_search"] = args => args.GetProperty("lang").GetString() == "cheatsheets"
            ? """{"result": [{"title": "SQL Injection Prevention Cheat Sheet", "doc_path": "cheatsheets/SQL.md", "url": "https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html"}]}"""
            : """{"result": [{"title": "CodeQL cs/sql-injection: SQL query built from user-controlled sources", "doc_path": "codeql/csharp/cs__sql-injection.md", "url": "https://codeql.github.com/codeql-query-help/csharp/cs-sql-injection/"}]}""";
        app.Argus.Handlers["docs_get"] = _ => """{"result": {"title": "CWE-89", "text": "Use prepared statements and parameters."}}""";
        app.Argus.Handlers["deps_check"] = _ => """
            {"project": "g/shop", "ref": "main", "sha": "abc", "dependencies": 42, "pinned": 40, "versions_not_compared": 0,
             "advisories": [{"pack": "advisories", "as_of": "2026-10-27", "days_old": 0}],
             "findings": [{"ecosystem": "NuGet", "package": "Newtonsoft.Json", "version": "12.0.3", "direct": false, "via": "Some.Lib", "file": "api/packages.lock.json", "line": 7,
                           "advisory": "GHSA-5crp-9r3c-p9vr", "aliases": ["CVE-2024-21907"], "severity": "high", "cwes": ["CWE-755"], "exploited": true,
                           "summary": "Improper Handling of Exceptional Conditions in Newtonsoft.Json", "fixed_in": "13.0.1", "upgrade_to": "13.0.1",
                           "url": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"}]}
            """;
        app.Argus.Handlers["sbom"] = _ => """{"bomFormat": "CycloneDX", "specVersion": "1.5", "components": [{"type": "library", "name": "Newtonsoft.Json", "version": "12.0.3", "purl": "pkg:nuget/Newtonsoft.Json@12.0.3"}]}""";
        app.Argus.Handlers["scan_at"] = _ => Scanned;
    }

    private static async Task<JsonElement> DoneAsync(TestBrowser b, Guid id)
    {
        for (var i = 0; i < 300; i++)
        {
            var r = await b.JsonAsync(await b.GetAsync($"/api/code/security/{id}"));
            if (r.GetProperty("scan").GetProperty("status").GetString() is "done" or "failed")
            {
                return r;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("the security review did not end");
    }

    [Fact]
    [Trait("Control", "VUL-01")]
    public async Task A_repository_is_reviewed_as_its_person_and_only_cited_weaknesses_the_pack_knows_are_kept()
    {
        Argus();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var user = "sr" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = "Sam" }));
        var me = await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!);
        var before = app.Model.Requests.Count;

        var res = await me.PostAsync("/api/code/security", new { project = "g/shop", @ref = "main" });
        await StatusAssert.Is(HttpStatusCode.Accepted, res);
        var id = (await me.JsonAsync(res)).GetProperty("id").GetGuid();
        var done = await DoneAsync(me, id);
        var scan = done.GetProperty("scan");
        Assert.True(scan.GetProperty("status").GetString() == "done", scan.GetProperty("error").ToString());
        // The test file is left out; the scanners' path sink joins git grep's; one suspicion cites a line the file does not have, and is dropped.
        Assert.Equal((5, 5, 1), (scan.GetProperty("candidates").GetInt32(), scan.GetProperty("read").GetInt32(), scan.GetProperty("dropped").GetInt32()));
        var all = done.GetProperty("findings").EnumerateArray().ToList();
        var findings = all.Where(f => f.GetProperty("kind").GetString() == "code" && f.GetProperty("foundBy").EnumerateArray().Any(t => t.GetString() == "model")).ToList();
        Assert.Equal(["src/Audit.cs", "src/Orders.cs", "src/Report.cs"], findings.Select(f => f.GetProperty("path").GetString()).Order());
        // Its packages too: the known-exploited one first, with how it comes in and the upgrade that fixes it; and its bill of materials.
        var package = all[0];
        Assert.Equal(("dependency", true, "api/packages.lock.json", "CWE-755"),
            (package.GetProperty("kind").GetString(), package.GetProperty("exploited").GetBoolean(), package.GetProperty("path").GetString(), package.GetProperty("cwe").GetString()));
        Assert.Equal("Newtonsoft.Json comes in through Some.Lib", package.GetProperty("flow").GetString());
        Assert.StartsWith("Upgrade Newtonsoft.Json to 13.0.1", package.GetProperty("fix").GetString(), StringComparison.Ordinal);
        Assert.Equal((42, "2026-10-27", true), (scan.GetProperty("dependencies").GetInt32(), scan.GetProperty("advisoriesAsOf").GetString(), scan.GetProperty("sbom").GetBoolean()));
        Assert.StartsWith("42 package(s) checked against the advisories of 2026-10-27: 1 known vulnerabilit(ies), 1 known to be exploited.", done.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var bom = await me.GetAsync($"/api/code/security/{id}/sbom");
        Assert.Equal("application/vnd.cyclonedx+json", bom.Content.Headers.ContentType?.MediaType);
        Assert.Contains("pkg:nuget/Newtonsoft.Json@12.0.3", await bom.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var orders = findings.Single(f => f.GetProperty("path").GetString() == "src/Orders.cs");
        Assert.Equal((4, "CWE-89", "high", true), (orders.GetProperty("line").GetInt32(), orders.GetProperty("cwe").GetString(), orders.GetProperty("severity").GetString(), orders.GetProperty("verified").GetBoolean()));
        Assert.StartsWith("var cmd = new SqlCommand(\"SELECT * FROM orders WHERE id", orders.GetProperty("sinkCode").GetString(), StringComparison.Ordinal);
        Assert.Contains("Orders.Find(id)", orders.GetProperty("flow").GetString(), StringComparison.Ordinal);
        Assert.Equal(["CWE-89: SQL Injection (Base)", "SQL Injection Prevention Cheat Sheet", "CodeQL cs/sql-injection: SQL query built from user-controlled sources"],
            orders.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("title").GetString()));
        // A CWE the pack does not know falls back to the sink's own; a refuted one is kept apart, with why.
        Assert.Equal("CWE-89", findings.Single(f => f.GetProperty("path").GetString() == "src/Audit.cs").GetProperty("cwe").GetString());
        var report = findings.Single(f => f.GetProperty("path").GetString() == "src/Report.cs");
        Assert.Equal((false, "The value is a constant."), (report.GetProperty("verified").GetBoolean(), report.GetProperty("verifyNote").GetString()));
        Assert.Contains("1 suspicion(s) dropped", done.GetProperty("summary").GetString(), StringComparison.Ordinal);

        // The scanners: what the model proved too names them both; what it read and cleared is kept apart with why; the rest as they found it.
        static string[] FoundBy(JsonElement f) => [.. f.GetProperty("foundBy").EnumerateArray().Select(t => t.GetString()!)];
        Assert.Equal(["model", "opengrep", "bandit"], FoundBy(orders));
        Assert.Equal("csharp-sql-built-from-strings, B608", orders.GetProperty("rule").GetString());
        Assert.Equal(["model", "opengrep"], FoundBy(report));
        var export = all.Single(f => f.GetProperty("path").GetString() == "src/Export.cs");
        Assert.Equal(["opengrep"], FoundBy(export));
        Assert.Equal((false, "CWE-22"), (export.GetProperty("verified").GetBoolean(), export.GetProperty("cwe").GetString()));
        Assert.Equal("The model read it with its callers and found no outside data reaching it. To check: where id comes from", export.GetProperty("verifyNote").GetString());
        var crypto = all.Single(f => f.GetProperty("path").GetString() == "src/Crypto.cs");
        Assert.Equal((JsonValueKind.Null, "low", "Use SHA-256 or better."), (crypto.GetProperty("verified").ValueKind, crypto.GetProperty("severity").GetString(), crypto.GetProperty("fix").GetString()));
        var secret = all.Single(f => f.GetProperty("kind").GetString() == "secret");
        Assert.Equal(("config/app.env", "", "c0ffee0000000000000000000000000000000000", "CWE-798"),
            (secret.GetProperty("path").GetString(), secret.GetProperty("sinkCode").GetString(), secret.GetProperty("commit").GetString(), secret.GetProperty("cwe").GetString()));
        var config = all.Single(f => f.GetProperty("kind").GetString() == "config");
        Assert.Equal("Dockerfile", config.GetProperty("path").GetString());
        Assert.Equal(["trivy"], FoundBy(config));
        Assert.Equal(("abc", 4), (scan.GetProperty("commit").GetString(), scan.GetProperty("scanners").GetProperty("tools").GetArrayLength()));
        Assert.Contains("Scanners (opengrep, bandit, gitleaks, trivy): 6 finding(s), 2 traced by the model too, 1 the model read and cleared (kept apart), 1 secret(s), 1 in configuration.",
            done.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var scanCall = app.Argus.McpCalls.Last(c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "scan_at" && c.Email == $"{user}@example.test");
        Assert.True(scanCall.Params!.Value.GetProperty("arguments").GetProperty("history").GetBoolean());

        // Read as the person, and asked as them, tagged security.
        Assert.All(app.Argus.McpCalls.Where(c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "grep_at"
            && c.Params?.GetProperty("arguments").GetProperty("project").GetString() == "g/shop" && c.Email == $"{user}@example.test"), c => Assert.Equal("main", c.Params!.Value.GetProperty("arguments").GetProperty("ref").GetString()));
        var asked = app.Model.Requests.Skip(before).Select(r => r.Body).Where(r => r["messages"]![0]!["content"]!.ToString().StartsWith("You are an application security engineer", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, asked.Count);
        Assert.All(asked, r => Assert.Equal("security", r["metadata"]!["tags"]![0]!.ToString()));
        Assert.Contains(asked, r => r["messages"]![1]!["content"]!.ToString().Contains("orders.Find(Request.Query[\"id\"])", StringComparison.Ordinal));
        // What a scanner matched is told to the model as a pattern, not a proof; the scanners' sinks are read first.
        Assert.Contains("Scanners matched a pattern here (a pattern is not proof: judge the code):\n- line 4, opengrep, bandit (csharp-sql-built-from-strings, B608)",
            asked[0]["messages"]![1]!["content"]!.ToString(), StringComparison.Ordinal);

        // Triage: an accepted risk needs why and until when; a wrong one why; others do not see it.
        var path = $"/api/code/security/{id}/findings/{orders.GetProperty("id").GetGuid()}";
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { state = "accepted", why = "Internal tool only" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { state = "accepted", why = "Internal tool only", until = DateTimeOffset.UtcNow.AddMonths(3) }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { state = "wrong" }));
        var list = await me.JsonAsync(await me.GetAsync("/api/code/security"));
        var mine = list.EnumerateArray().Single(s => s.GetProperty("scan").GetProperty("id").GetGuid() == id);
        // Audit.cs's weakness, the package, the secret and the Dockerfile are still open; Orders.cs's is accepted, Export.cs's cleared.
        Assert.Equal((4, 1), (mine.GetProperty("open").GetProperty("high").GetInt32(), mine.GetProperty("open").GetProperty("low").GetInt32()));

        // As SARIF, for a code host's security view: every finding, what found it, the commit read, the accepted one suppressed with why.
        var sarif = JsonDocument.Parse(await (await me.GetAsync($"/api/code/security/{id}/sarif")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("2.1.0", sarif.GetProperty("version").GetString());
        var run = sarif.GetProperty("runs")[0];
        Assert.Equal("abc", run.GetProperty("versionControlProvenance")[0].GetProperty("revisionId").GetString());
        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(all.Count, results.Count);
        var accepted = results.Single(r => r.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString() == "src/Orders.cs");
        Assert.Equal(("csharp-sql-built-from-strings", "error"), (accepted.GetProperty("ruleId").GetString(), accepted.GetProperty("level").GetString()));
        Assert.Equal("Risk accepted: Internal tool only", accepted.GetProperty("suppressions")[0].GetProperty("justification").GetString());
        Assert.Contains(run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray(), r => r.GetProperty("id").GetString() == "GHSA-5crp-9r3c-p9vr");
        var other = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.OK, await other.GetAsync($"/api/code/security/{id}"));
        // The known-exploited package reached the security team (who may manage every review) at once.
        var news = (await other.JsonAsync(await other.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray();
        Assert.Contains(news, n => n.GetProperty("title").GetString() == "Urgent security findings in g/shop" && n.GetProperty("link").GetString() == $"/code/security/{id}"
            && n.GetProperty("body").GetString()!.Contains("known to be exploited", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Control", "VUL-01")]
    public async Task Admin_security_shows_each_repositorys_latest_review_what_cannot_wait_this_week_and_the_risks_accepted()
    {
        await using var f = app.Create(app.ConnectionStringFor("sec_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var adminId = (await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        var now = DateTimeOffset.UtcNow;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SecurityFinding Found(string title, string severity, int daysAgo, string state = SecurityFinding.Open, bool exploited = false, string kind = SecurityFinding.Code) =>
                new() { Kind = kind, Path = "src/a.cs", Line = 1, Cwe = "CWE-89", Severity = severity, Title = title, Verified = true, State = state, Exploited = exploited, FoundAt = now.AddDays(-daysAgo) };
            // An old review of g/shop is history: the latest speaks for it.
            var before = new SecurityScan { UserId = adminId, Project = "g/shop", Ref = "main", Status = SecurityScan.Done, FinishedAt = now.AddDays(-60) };
            before.Findings.Add(Found("Gone since", "critical", 60));
            var shop = new SecurityScan { UserId = adminId, Project = "g/shop", Ref = "main", Status = SecurityScan.Done, FinishedAt = now.AddDays(-40), Sbom = "{}" };
            var accepted = Found("Runs as root", "medium", 40, SecurityFinding.Accepted, kind: SecurityFinding.Config);
            (accepted.Why, accepted.Until, accepted.DecidedById, accepted.DecidedAt) = ("Until the base image moves", now.AddMonths(2), adminId, now.AddDays(-30));
            var fixedOne = Found("A secret", "high", 40, SecurityFinding.Fixed, kind: SecurityFinding.Secret);
            (fixedOne.DecidedAt, fixedOne.DecidedById) = (now.AddDays(-2), adminId);
            shop.Findings.AddRange([Found("SQL built from the request", "high", 40), Found("Log4Shell", "critical", 1, exploited: true, kind: SecurityFinding.Dependency),
                Found("Weak hash", "low", 40), new SecurityFinding { Path = "x", Cwe = "CWE-89", Severity = "critical", Title = "Refuted", Verified = false, FoundAt = now }, accepted, fixedOne]);
            var blog = new SecurityScan { UserId = adminId, Project = "g/blog", Status = SecurityScan.Done, FinishedAt = now.AddDays(-3) };
            blog.Findings.Add(Found("Open redirect", "medium", 3));
            db.SecurityScans.AddRange(before, shop, blog);
            await db.SaveChangesAsync();
        }
        var d = await admin.JsonAsync(await admin.GetAsync("/api/admin/security"));
        var repos = d.GetProperty("repositories").EnumerateArray().ToList();
        // The known-exploited first; the old review's critical is not counted, nor the refuted one.
        Assert.Equal(["g/shop", "g/blog"], repos.Select(r => r.GetProperty("project").GetString()));
        var shopRow = repos[0];
        Assert.Equal((1, 1, 1, 1, true), (shopRow.GetProperty("open").GetProperty("critical").GetInt32(), shopRow.GetProperty("open").GetProperty("high").GetInt32(),
            shopRow.GetProperty("exploited").GetInt32(), shopRow.GetProperty("overdue").GetInt32(), shopRow.GetProperty("sbom").GetBoolean()));
        var totals = d.GetProperty("totals");
        Assert.Equal((1, 1, 2, 1), (totals.GetProperty("exploited").GetInt32(), totals.GetProperty("overdue").GetInt32(), totals.GetProperty("fresh").GetInt32(), totals.GetProperty("fixedThisWeek").GetInt32()));
        Assert.Equal("Log4Shell", d.GetProperty("exploited")[0].GetProperty("title").GetString());
        // The high one is 40 days old, past its 30; the low one has 180.
        Assert.Equal(["SQL built from the request"], d.GetProperty("overdue").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        Assert.Equal(["Log4Shell", "Open redirect"], d.GetProperty("fresh").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        var risk = d.GetProperty("accepted")[0];
        Assert.Equal(("Runs as root", "Until the base image moves", false), (risk.GetProperty("title").GetString(), risk.GetProperty("why").GetString(), risk.GetProperty("lapsed").GetBoolean()));
        Assert.Equal("A secret", d.GetProperty("fixedThisWeek")[0].GetProperty("title").GetString());

        // Only those who may manage every review.
        var name = "s" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Sid" }));
        var someone = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await someone.GetAsync("/api/admin/security"));
    }

    [Fact]
    [Trait("Control", "VUL-01")]
    public async Task What_people_decided_is_remembered_for_the_repositorys_next_review_and_a_fixed_one_found_again_is_open()
    {
        Argus();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var user = "rm" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = "Ola" }));
        var me = await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!);
        var project = "g/remember" + Guid.NewGuid().ToString("N")[..6];
        async Task<JsonElement> ReviewAsync()
        {
            var res = await me.PostAsync("/api/code/security", new { project, @ref = "main" });
            return await DoneAsync(me, (await me.JsonAsync(res)).GetProperty("id").GetGuid());
        }
        JsonElement At(JsonElement review, string path) => review.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("path").GetString() == path);
        var first = await ReviewAsync();
        var firstId = first.GetProperty("scan").GetProperty("id").GetGuid();
        async Task Decide(string path, object body) => await StatusAssert.Is(HttpStatusCode.NoContent,
            await me.Http.PutAsJsonAsync(new Uri($"/api/code/security/{firstId}/findings/{At(first, path).GetProperty("id").GetGuid()}", UriKind.Relative), body));
        var until = DateTimeOffset.UtcNow.AddMonths(2);
        await Decide("src/Audit.cs", new { state = "wrong", why = "who comes from our own audit table" });
        await Decide("src/Orders.cs", new { state = "accepted", why = "Internal tool only", until });
        await Decide("Dockerfile", new { state = "fixed" });

        var second = await ReviewAsync();
        Assert.Equal(2, second.GetProperty("scan").GetProperty("remembered").GetInt32());
        var audit = At(second, "src/Audit.cs");
        Assert.Equal(("wrong", "who comes from our own audit table"), (audit.GetProperty("state").GetString(), audit.GetProperty("why").GetString()));
        var orders = At(second, "src/Orders.cs");
        Assert.Equal("accepted", orders.GetProperty("state").GetString());
        Assert.Equal(until.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture),
            orders.GetProperty("until").GetDateTimeOffset().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture));
        // Marked fixed, found again: open again.
        Assert.Equal("open", At(second, "Dockerfile").GetProperty("state").GetString());
        Assert.Contains("2 decided in an earlier review of the repository", second.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "VUL-01")]
    public async Task When_the_advisories_are_renewed_a_reviews_packages_are_checked_again_at_the_commit_it_read()
    {
        Argus();
        app.Argus.PacksAnswer = null;
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var user = "rc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = "Rae" }));
        var me = await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!);
        var project = "g/recheck" + Guid.NewGuid().ToString("N")[..6];
        var res = await me.PostAsync("/api/code/security", new { project, @ref = "main" });
        var id = (await me.JsonAsync(res)).GetProperty("id").GetGuid();
        var done = await DoneAsync(me, id);
        var finished = done.GetProperty("scan").GetProperty("finishedAt").GetDateTimeOffset();
        var recheck = app.Factory.Services.GetRequiredService<SecurityRecheck>();
        try
        {
            // The advisories of the review's day: nothing to check again.
            app.Argus.PacksAnswer = """{"packs": [{"name": "advisories", "version": "2026.10.27"}], "job": {"state": "idle"}}""";
            var calls = app.Argus.McpCalls.Count;
            await recheck.RecheckAsync(CancellationToken.None);
            Assert.DoesNotContain(app.Argus.McpCalls.Skip(calls), c => c.Params?.GetProperty("arguments").TryGetProperty("project", out var p) == true && p.GetString() == project);

            // Renewed two days on, with an advisory for a package the review saw as clean.
            app.Argus.PacksAnswer = """{"packs": [{"name": "advisories", "version": "2026.10.29"}], "job": {"state": "idle"}}""";
            app.Argus.Handlers["deps_check"] = _ => """
                {"project": "g/shop", "ref": "abc", "sha": "abc", "dependencies": 42, "advisories": [{"pack": "advisories", "as_of": "2026-10-29", "days_old": 0}],
                 "findings": [{"ecosystem": "NuGet", "package": "Newtonsoft.Json", "version": "12.0.3", "direct": false, "via": "Some.Lib", "file": "api/packages.lock.json", "line": 7,
                               "advisory": "GHSA-5crp-9r3c-p9vr", "aliases": ["CVE-2024-21907"], "severity": "high", "cwes": ["CWE-755"], "exploited": true,
                               "summary": "Improper Handling of Exceptional Conditions in Newtonsoft.Json", "fixed_in": "13.0.1", "upgrade_to": "13.0.1", "url": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"},
                              {"ecosystem": "npm", "package": "qs", "version": "6.10.1", "direct": true, "via": null, "file": "web/package-lock.json", "line": 12,
                               "advisory": "GHSA-hrpp-h998-j3pp", "aliases": ["CVE-2022-24999"], "severity": "high", "cwes": ["CWE-1321"], "exploited": false,
                               "summary": "qs vulnerable to Prototype Pollution", "fixed_in": "6.10.3", "upgrade_to": "6.10.3", "url": "https://github.com/advisories/GHSA-hrpp-h998-j3pp"}]}
                """;
            Assert.True(await recheck.RecheckAsync(CancellationToken.None) >= 1);
            var call = app.Argus.McpCalls.Last(c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "deps_check"
                && c.Params?.GetProperty("arguments").GetProperty("project").GetString() == project);
            // At the commit the review read, as its person.
            Assert.Equal(("abc", $"{user}@example.test"), (call.Params!.Value.GetProperty("arguments").GetProperty("ref").GetString(), call.Email));
            var after = await me.JsonAsync(await me.GetAsync($"/api/code/security/{id}"));
            Assert.Equal("2026-10-29", after.GetProperty("scan").GetProperty("advisoriesAsOf").GetString());
            Assert.NotEqual(JsonValueKind.Null, after.GetProperty("scan").GetProperty("recheckedAt").ValueKind);
            var packages = after.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("kind").GetString() == "dependency").ToList();
            Assert.Equal(2, packages.Count);
            var qs = packages.Single(f => f.GetProperty("sinkCode").GetString()!.StartsWith("qs ", StringComparison.Ordinal));
            Assert.True(qs.GetProperty("foundAt").GetDateTimeOffset() > finished);
            var news = (await me.JsonAsync(await me.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray();
            Assert.Contains(news, n => n.GetProperty("title").GetString() == $"New known vulnerabilities in {project}" && n.GetProperty("link").GetString() == $"/code/security/{id}");

            // Checked again once: the next look finds nothing due.
            calls = app.Argus.McpCalls.Count;
            await recheck.RecheckAsync(CancellationToken.None);
            Assert.DoesNotContain(app.Argus.McpCalls.Skip(calls), c => c.Params?.GetProperty("arguments").TryGetProperty("project", out var p) == true && p.GetString() == project);
        }
        finally
        {
            app.Argus.PacksAnswer = null;
        }
    }

    [Fact]
    [Trait("Control", "VUL-01")]
    public async Task The_vulnerabilities_check_counts_what_is_past_its_time_to_fix()
    {
        await using var f = app.Create(app.ConnectionStringFor("vul_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<(string State, string Detail)> Check()
        {
            var c = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "vulnerabilities");
            return (c.GetProperty("state").GetString()!, c.GetProperty("detail").GetString()!);
        }
        Assert.Equal("warn", (await Check()).State);

        var adminId = (await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("id").GetGuid();
        Guid high;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var old = DateTimeOffset.UtcNow.AddDays(-40);
            var scan = new SecurityScan { UserId = adminId, Project = "g/shop", Status = SecurityScan.Done, CreatedAt = old, FinishedAt = old, AdvisoriesAsOf = DateOnly.FromDateTime(old.UtcDateTime.AddDays(-10)) };
            var finding = new SecurityFinding { Path = "src/Orders.cs", Line = 4, Cwe = "CWE-89", Severity = "high", Title = "SQL", Verified = true, FoundAt = old };
            var low = new SecurityFinding { Path = "src/Util.cs", Line = 9, Cwe = "CWE-601", Severity = "low", Title = "Redirect", Verified = true, FoundAt = old };
            scan.Findings.AddRange([finding, low]);
            db.SecurityScans.Add(scan);
            await db.SaveChangesAsync();
            high = finding.Id;
        }
        // A high finding 40 days old is past its 30: red, with the review's age and its stale advisories said too.
        var (state, detail) = await Check();
        Assert.Equal("fail", state);
        Assert.Contains("no security review within 30 days", detail, StringComparison.Ordinal);
        Assert.Contains("its advisories were over a week old", detail, StringComparison.Ordinal);
        Assert.Contains("1 past the time to fix (1 high)", detail, StringComparison.Ordinal);

        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SecurityFindings.Where(x => x.Id == high)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, SecurityFinding.Fixed));
        }
        // One found today, by a check of the packages against renewed advisories: its 30 days count from now.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var scanId = await db.SecurityFindings.Where(x => x.Id == high).Select(x => x.ScanId).SingleAsync();
            db.SecurityFindings.Add(new SecurityFinding { ScanId = scanId, Kind = SecurityFinding.Dependency, Path = "web/package-lock.json", Cwe = "CWE-1321", Severity = "high", Title = "qs", Verified = true });
            await db.SaveChangesAsync();
        }

        // Fixed: amber now only for the old review; the low one has 180 days, the new one its own 30.
        (state, detail) = await Check();
        Assert.Equal("warn", state);
        Assert.DoesNotContain("past the time to fix", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("    public object Find(string id) {", "Find")]
    [InlineData("def find_order(order_id):", "find_order")]
    [InlineData("func (s *Store) Lookup(id string) error {", "Lookup")]
    [InlineData("export async function load(req, res) {", "load")]
    [InlineData("const handler = async (req) => {", "handler")]
    [InlineData("  if (x) {", null)]
    public void The_function_a_sink_is_in_is_read_from_the_lines_above_it(string line, string? name) =>
        Assert.Equal(name, SecurityReview.EnclosingFunction(["// a file", line, "  call(x);"]));

    [Fact]
    public void Sinks_come_most_dangerous_first_without_tests_or_comments_and_at_most_three_a_file()
    {
        var sql = SecurityReview.Sinks.First(s => s.Kind == "sql");
        var redirect = SecurityReview.Sinks.First(s => s.Kind == "redirect");
        var found = new List<SecurityReview.Candidate>
        {
            new("src/A.cs", 9, "return Redirect(url);", redirect),
            new("src/A.cs", 1, "q(1)", sql), new("src/A.cs", 2, "q(2)", sql), new("src/A.cs", 3, "q(3)", sql), new("src/A.cs", 4, "q(4)", sql),
            new("src/A.cs", 2, "q(2)", redirect), new("tests/ATests.cs", 1, "q()", sql), new("src/B.cs", 5, "// new SqlCommand(", sql), new("src/C.cs", 7, "q()", sql),
        };
        Assert.Equal(["src/A.cs:1", "src/C.cs:7", "src/A.cs:2", "src/A.cs:3"], SecurityReview.Order(found).Select(c => $"{c.Path}:{c.Line}"));
        Assert.Equal("CWE-89", SecurityReview.Cwe("cwe 89"));
        Assert.Null(SecurityReview.Cwe("SQL injection"));
    }
}
