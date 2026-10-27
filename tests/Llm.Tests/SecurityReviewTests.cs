using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Code;

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
    };

    private void Argus()
    {
        app.Argus.Handlers["grep_at"] = args =>
        {
            var pattern = args.GetProperty("pattern").GetString()!;
            var glob = args.TryGetProperty("path", out var g) ? g.GetString() : null;
            object[] matches = pattern.Contains("SqlCommand", StringComparison.Ordinal) && glob == "*.cs"
                ? [.. Files.Where(f => f.Key != "src/Controller.cs").Select(f => new { path = f.Key, line = Array.FindIndex(f.Value, l => l.Contains("SqlCommand(", StringComparison.Ordinal)) + 1, text = f.Value.First(l => l.Contains("SqlCommand(", StringComparison.Ordinal)) }),
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
        Assert.Equal("done", scan.GetProperty("status").GetString());
        // The test file is left out; one suspicion cites a line the file does not have, and is dropped.
        Assert.Equal((4, 4, 1), (scan.GetProperty("candidates").GetInt32(), scan.GetProperty("read").GetInt32(), scan.GetProperty("dropped").GetInt32()));
        var findings = done.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(["src/Audit.cs", "src/Orders.cs", "src/Report.cs"], findings.Select(f => f.GetProperty("path").GetString()).Order());
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

        // Read as the person, and asked as them, tagged security.
        Assert.All(app.Argus.McpCalls.Where(c => c.Method == "tools/call" && c.Params?.GetProperty("name").GetString() == "grep_at"
            && c.Params?.GetProperty("arguments").GetProperty("project").GetString() == "g/shop" && c.Email == $"{user}@example.test"), c => Assert.Equal("main", c.Params!.Value.GetProperty("arguments").GetProperty("ref").GetString()));
        var asked = app.Model.Requests.Skip(before).Select(r => r.Body).Where(r => r["messages"]![0]!["content"]!.ToString().StartsWith("You are an application security engineer", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, asked.Count);
        Assert.All(asked, r => Assert.Equal("security", r["metadata"]!["tags"]![0]!.ToString()));
        Assert.Contains(asked, r => r["messages"]![1]!["content"]!.ToString().Contains("orders.Find(Request.Query[\"id\"])", StringComparison.Ordinal));

        // Triage: an accepted risk needs why and until when; a wrong one why; others do not see it.
        var path = $"/api/code/security/{id}/findings/{orders.GetProperty("id").GetGuid()}";
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { state = "accepted", why = "Internal tool only" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await me.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { state = "accepted", why = "Internal tool only", until = DateTimeOffset.UtcNow.AddMonths(3) }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await me.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { state = "wrong" }));
        var list = await me.JsonAsync(await me.GetAsync("/api/code/security"));
        var mine = list.EnumerateArray().Single(s => s.GetProperty("scan").GetProperty("id").GetGuid() == id);
        Assert.Equal(1, mine.GetProperty("open").GetProperty("high").GetInt32());
        var other = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.OK, await other.GetAsync($"/api/code/security/{id}"));
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
