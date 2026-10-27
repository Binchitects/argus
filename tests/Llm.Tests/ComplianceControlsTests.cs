using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Llm.Api.Compliance;

namespace Llm.Tests;

/// <summary>
/// The guard of docs/compliance/controls.yaml: what each control names (its tests, documents and checks) exists, the tests that
/// prove a control carry its id and are named by it, and Admin → Compliance runs the checks beside the controls.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed partial class ComplianceControlsTests(AppFixture app)
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppFixture.PluginsPath, ".."));

    private static ControlsFile Repository() => ComplianceControls.Parse(File.ReadAllText(Path.Combine(Root, "docs", "compliance", "controls.yaml")));

    [GeneratedRegex(@"^#{1,6} (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"LIVE_CHECKS = \(([^)]*)\)")]
    private static partial Regex LiveChecks();

    /// <summary>A heading's anchor as GitHub makes it: lower case, punctuation gone, each space a dash.</summary>
    private static string Slug(string heading) =>
        new string([.. heading.Replace("**", "").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')]).Replace(' ', '-');

    /// <summary>This project's test methods, as Llm.Tests/Class.Method, with the controls their traits name.</summary>
    private static readonly Lazy<Dictionary<string, HashSet<string>>> Tests = new(() => typeof(ComplianceControlsTests).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m => (Type: t, Method: m)))
        .Where(x => x.Method.IsDefined(typeof(FactAttribute), inherit: true))
        .ToDictionary(x => $"Llm.Tests/{x.Type.Name}.{x.Method.Name}", x => x.Method.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(TraitAttribute) && a.ConstructorArguments[0].Value as string == ComplianceControls.ControlTrait)
            .Select(a => (string)a.ConstructorArguments[1].Value!).ToHashSet(StringComparer.Ordinal)));

    [Fact]
    [Trait("Control", "GOV-01")]
    public void Every_control_names_tests_documents_and_checks_that_exist()
    {
        var file = Repository();
        var wrong = new List<string>();
        // Every framework has its catalog (frameworks.yaml, and the catalogs it names), and the app's copy is the repository's.
        Assert.Equal(file.Frameworks.Keys.Order(StringComparer.Ordinal), ComplianceControls.Catalogs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(14, file.Frameworks.Count);
        var repo = FrameworkCatalog.Read(name => File.ReadAllText(Path.Combine(Root, "docs", "compliance",
            name == "compliance-frameworks.yaml" ? "frameworks.yaml" : name["compliance-".Length..])));
        Assert.Equal(repo.ToDictionary(kv => kv.Key, kv => kv.Value.Clauses.Count), ComplianceControls.Catalogs.ToDictionary(kv => kv.Key, kv => kv.Value.Clauses.Count));
        // The copy built into the app is this file.
        Assert.Equal(file.Controls.Select(c => c.Id), ComplianceControls.All.Controls.Select(c => c.Id));

        var script = File.ReadAllText(Path.Combine(Root, "deploy", "scripts", "compliance-check.py"));
        var live = Regex.Matches(LiveChecks().Match(script).Groups[1].Value, @"""([a-z-]+)""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(live);
        var app = ComplianceChecks.All.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var anchors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var c in file.Controls)
        {
            var at = c.Id;
            if (!ComplianceControls.IdFormat().IsMatch(c.Id))
            {
                wrong.Add($"{at}: the id is AAA-00");
            }
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Meets) || string.IsNullOrWhiteSpace(c.Operator))
            {
                wrong.Add($"{at}: a title, what meets it and the operator's part are each needed");
            }
            if (c.Frameworks.Count == 0)
            {
                wrong.Add($"{at}: it serves no framework's clause");
            }
            foreach (var (framework, clauses) in c.Frameworks)
            {
                wrong.AddRange(clauses.Where(k => !ComplianceControls.IsClause(framework, k)).Select(k => $"{at}: {framework} has no clause {k}"));
            }
            if (c.Docs.Count == 0 || c.Tests.Count == 0)
            {
                wrong.Add($"{at}: it needs a document and a test");
            }
            foreach (var doc in c.Docs)
            {
                var hash = doc.IndexOf('#', StringComparison.Ordinal);
                var path = Path.Combine(Root, "docs", hash < 0 ? doc : doc[..hash]);
                if (!File.Exists(path))
                {
                    wrong.Add($"{at}: docs/{doc} is not there");
                    continue;
                }
                if (hash >= 0)
                {
                    var known = anchors.TryGetValue(path, out var a) ? a
                        : anchors[path] = [.. Heading().Matches(File.ReadAllText(path)).Select(m => Slug(m.Groups[1].Value))];
                    if (!known.Contains(doc[(hash + 1)..]))
                    {
                        wrong.Add($"{at}: docs/{doc} has no such section");
                    }
                }
            }
            foreach (var test in c.Tests)
            {
                var exists = test.StartsWith("deploy/", StringComparison.Ordinal)
                    ? test.Split("::") is [var py, var name] && File.Exists(Path.Combine(Root, "tests", py))
                        && File.ReadAllText(Path.Combine(Root, "tests", py)).Contains($"def {name}(", StringComparison.Ordinal)
                    : Tests.Value.ContainsKey(test);
                if (!exists)
                {
                    wrong.Add($"{at}: the test {test} is not there");
                }
            }
            foreach (var check in c.Checks)
            {
                var known = check.Split(':') switch
                {
                    ["app", var id] => app.Contains(id),
                    ["live", var id] => live.Contains(id),
                    _ => false,
                };
                if (!known)
                {
                    wrong.Add($"{at}: nothing runs the check {check}");
                }
            }
        }
        Assert.Equal(file.Controls.Count, file.Controls.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        // Every check watches a control.
        var used = file.Controls.SelectMany(c => c.Checks).ToHashSet(StringComparer.Ordinal);
        wrong.AddRange(app.Where(id => !used.Contains("app:" + id)).Select(id => $"the app's check {id} watches no control"));
        wrong.AddRange(live.Where(id => !used.Contains("live:" + id)).Select(id => $"compliance-check.py's {id} watches no control"));
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    [Trait("Control", "GOV-01")]
    public void Every_test_that_proves_a_control_is_named_by_it()
    {
        var named = Repository().Controls.SelectMany(c => c.Tests.Where(t => !t.StartsWith("deploy/", StringComparison.Ordinal)).Select(t => (Test: t, Control: c.Id))).ToHashSet();
        var carried = Tests.Value.SelectMany(t => t.Value.Select(c => (Test: t.Key, Control: c))).ToHashSet();
        var wrong = named.Except(carried).Select(n => $"{n.Test} proves {n.Control} but does not carry [Trait(\"Control\", \"{n.Control}\")]")
            .Concat(carried.Except(named).Select(t => $"{t.Test} carries {t.Control}, which does not name it"))
            .Order(StringComparer.Ordinal).ToList();
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    [Trait("Control", "GOV-01")]
    public async Task The_app_runs_its_checks_and_admins_see_them_with_the_controls()
    {
        var root = Directory.CreateTempSubdirectory("compliance-").FullName;
        try
        {
            // A backup from an hour ago that ended well.
            var backups = Path.Combine(root, "backups");
            var started = DateTimeOffset.UtcNow.AddHours(-1);
            var name = started.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Path.Combine(backups, name));
            File.WriteAllText(Path.Combine(backups, name, "RESULT"), "ok\n");
            File.WriteAllText(Path.Combine(backups, name, "MANIFEST"), $"backup: {name}\nstarted: {started:yyyy-MM-ddTHH:mm:ssZ}\nencrypted: aes-256-cbc, pbkdf2 sha256 200000 rounds; key BACKUP_KEY_FILE, sha256 0123456789abcdef\n");
            await using var f = app.Create(app.ConnectionStringFor("cmp_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
            {
                ["Auth:DataKey"] = "a-data-key-for-compliance-tests",
                ["Retention:Days"] = "365",
                ["Storage:BackupDir"] = backups,
            });
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"));

            var checks = view.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("state").GetString()!);
            Assert.Equal(ComplianceChecks.All.Select(c => c.Id), checks.Keys);
            Assert.All(checks.Values, s => Assert.Contains(s, new[] { "ok", "warn", "fail", "unknown" }));
            Assert.Equal("ok", checks["passwords"]);
            Assert.Equal("ok", checks["sessions"]);
            Assert.Equal("ok", checks["data-key"]);
            Assert.Equal("ok", checks["chat-retention"]);
            Assert.Equal("ok", checks["backups"]);
            // Signing in wrote to the audit log; the first admin has no second factor yet.
            Assert.Equal("ok", checks["audit-log"]);
            Assert.Equal("warn", checks["two-factor"]);
            var twoFactor = view.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "two-factor");
            Assert.Contains("admin", twoFactor.GetProperty("detail").GetString(), StringComparison.Ordinal);

            // Each control as the file has it, with the worst of its app checks.
            var controls = view.GetProperty("controls").EnumerateArray().ToList();
            Assert.Equal(ComplianceControls.All.Controls.Select(c => c.Id), controls.Select(c => c.GetProperty("id").GetString()));
            var iam03 = controls.Single(c => c.GetProperty("id").GetString() == "IAM-03");
            Assert.Equal("warn", iam03.GetProperty("state").GetString());
            Assert.Contains("A.8.5", iam03.GetProperty("frameworks").GetProperty("iso27001").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(JsonValueKind.Null, controls.Single(c => c.GetProperty("id").GetString() == "IAM-04").GetProperty("state").ValueKind);
            Assert.Equal(14, view.GetProperty("frameworks").EnumerateObject().Count());
            // Each framework's catalog, and how much of it the controls serve: 800-53 by its baselines' source, ISO 27001's clauses with Annex A.
            var coverage = view.GetProperty("coverage");
            Assert.Equal(123, coverage.GetProperty("iso27001").GetProperty("clauses").GetInt32());
            Assert.InRange(coverage.GetProperty("nist80053").GetProperty("served").GetInt32(), 40, 1014);

            // A member does not see it.
            var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "cmpmember", email = "cmpmember@example.test" }));
            var member = await new TestBrowser(f).SignedInAsync("cmpmember", made.GetProperty("password").GetString()!);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/compliance")).StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Control", "GOV-01")]
    public async Task The_companys_part_is_attested_and_audited_and_the_evidence_holds_everything_an_auditor_asks_for()
    {
        await using var f = app.Create(app.ConnectionStringFor("cmp_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/compliance/controls/XYZ-99/attest", new { note = "Policy v1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/compliance/controls/IAM-01/attest", new { note = " " })).StatusCode);
        var attested = await admin.JsonAsync(await admin.PostAsync("/api/admin/compliance/controls/IAM-01/attest", new { note = "Password policy v2, in the ISMS folder" }));
        Assert.Equal("admin", attested.GetProperty("actor").GetString());
        var at = attested.GetProperty("at").GetDateTimeOffset();
        Assert.Equal(at.AddDays(365), attested.GetProperty("due").GetDateTimeOffset());

        var view = await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"));
        var iam01 = view.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "IAM-01");
        Assert.Equal("Password policy v2, in the ISMS folder", iam01.GetProperty("attested").GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "IAM-02").GetProperty("attested").ValueKind);

        // A person who may read it but not attest: the Auditor role.
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "cmpauditor", email = "cmpauditor@example.test" }));
        var role = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "auditor").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = made.GetProperty("id").GetGuid() }));
        var auditor = await new TestBrowser(f).SignedInAsync("cmpauditor", made.GetProperty("password").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/admin/compliance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsync("/api/admin/compliance/controls/IAM-01/attest", new { note = "Not mine to say" })).StatusCode);

        var evidence = await admin.GetAsync("/api/admin/compliance/evidence");
        Assert.Equal("application/zip", evidence.Content.Headers.ContentType?.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(await evidence.Content.ReadAsStreamAsync());
        string Read(string name)
        {
            using var r = new StreamReader(zip.GetEntry(name)!.Open());
            return r.ReadToEnd();
        }
        Assert.Equal(["README.txt", "access-reviews.csv", "attestations.csv", "audit-actions.csv", "audit-anchors.jsonl", "audit-chain.json", "compliance.json", "controls.yaml", "incidents.json", "notice.csv", "policies.csv", "policy-acknowledgements.csv", "privileged-access.csv", "risk-history.csv", "risks.csv", "settings.json", "soa-approvals.csv", "soa-iso27001.csv", "soa-iso42001.csv"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(File.ReadAllText(Path.Combine(Root, "docs", "compliance", "controls.yaml")), Read("controls.yaml"));
        Assert.Contains("IAM-01,", Read("attestations.csv"), StringComparison.Ordinal);
        Assert.Contains("Password policy v2, in the ISMS folder", Read("attestations.csv"), StringComparison.Ordinal);
        var privileged = Read("privileged-access.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("person,email,signs in with,second factor", privileged[0], StringComparison.Ordinal);
        Assert.Contains(privileged, l => l.StartsWith("admin,", StringComparison.Ordinal) && l.Contains(",a password here,no,no,", StringComparison.Ordinal));
        Assert.Contains(privileged, l => l.StartsWith("cmpauditor,", StringComparison.Ordinal) && l.Contains("compliance.read", StringComparison.Ordinal));
        Assert.Contains("compliance.attest", Read("audit-actions.csv"), StringComparison.Ordinal);
        Assert.Contains("\"checks\"", Read("compliance.json"), StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(Read("audit-chain.json")).RootElement.GetProperty("holds").GetBoolean());
        // The settings without a secret's value: the APP_KEY-encrypted ones are only "set" or not.
        Assert.DoesNotContain(AppFixture.AdminPassword, Read("settings.json"), StringComparison.Ordinal);

        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "compliance.attest" && e.GetProperty("target").GetString() == "IAM-01");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "compliance.evidence");
    }
}
