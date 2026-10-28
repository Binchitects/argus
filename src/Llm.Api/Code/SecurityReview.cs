using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>A dangerous call untrusted data may reach, by language: what weakness it would be, and git grep's pattern for it.</summary>
public sealed record Sink(string Kind, string Cwe, string What, string[] Globs, string Pattern);

/// <summary>
/// A security review of a repository at a ref, as the person who asked (Argus reads it with their read-only access):
/// <list type="number">
/// <item>the sinks, found with git grep (<see cref="Sinks"/>): queries, commands, paths, fetches, deserialisers, HTML,
/// XML parsers, redirects and code run from text, tests and vendored code left out;</item>
/// <item>each, the most dangerous first, read with the function around it and that function's callers, and judged by
/// the model: does data from outside reach it without what the weakness needs;</item>
/// <item>a suspicion is kept only when it cites the sink's line as the file has it and names a CWE the security pack
/// knows; the pack's pages on that weakness (its CWE, the cheat sheet, CodeQL's query for the language) go with it;</item>
/// <item>each kept finding is put to a pass told to refute it, with the CWE's page in hand.</item>
/// </list>
/// The model's requests are the person's, tagged "security". Nothing is written to the code host.
/// </summary>
public sealed partial class SecurityReview(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<SecurityReview> logger) : IHostedService
{
    public const string Tag = "security";
    /// <summary>The most sinks a review reads (the most dangerous first, at most three a file).</summary>
    public const int MostRead = 30;
    public const int MostPerFile = 3;
    /// <summary>The most findings put to the second pass (the most severe first).</summary>
    public const int MostVerified = 25;
    /// <summary>How long an open finding of each severity may stay open before the vulnerabilities check counts it overdue (days).</summary>
    public static readonly IReadOnlyDictionary<string, int> FixWithinDays = new Dictionary<string, int> { ["critical"] = 7, ["high"] = 30, ["medium"] = 90, ["low"] = 180 };
    /// <summary>Advisories older than this when a review ran make the vulnerabilities check warn (days).</summary>
    public const int AdvisoriesDue = 7;

    private static readonly string[] Cs = ["*.cs"];
    private static readonly string[] Java = ["*.java", "*.kt"];
    private static readonly string[] Py = ["*.py"];
    private static readonly string[] Js = ["*.[jt]s", "*.[jt]sx", "*.vue"];
    private static readonly string[] Go = ["*.go"];
    private static readonly string[] Php = ["*.php"];
    private static readonly string[] Rb = ["*.rb"];

    /// <summary>The sinks looked for, the most dangerous kinds first.</summary>
    public static readonly IReadOnlyList<Sink> Sinks =
    [
        new("sql", "CWE-89", "an SQL query", Cs, @"(SqlCommand\(|\.CommandText *=|ExecuteSqlRaw|FromSqlRaw|SqlQueryRaw)"),
        new("sql", "CWE-89", "an SQL query", Java, @"(createStatement\(|\.executeQuery\(|\.executeUpdate\(|\.addBatch\(|createNativeQuery\(|createQuery\(.*\+)"),
        new("sql", "CWE-89", "an SQL query", Py, @"(\.execute(many)?\(|\.raw\(|text\(f[""'])"),
        new("sql", "CWE-89", "an SQL query", Js, @"(\.query\(|\.raw\(|\$queryRawUnsafe|\$executeRawUnsafe)"),
        new("sql", "CWE-89", "an SQL query", Go, @"\.(Query|Exec|QueryRow)(Context)?\("),
        new("sql", "CWE-89", "an SQL query", Php, @"(mysqli_query|->query\(|->exec\()"),
        new("sql", "CWE-89", "an SQL query", Rb, @"(find_by_sql|\.execute\(|where\("".*#\{)"),
        new("command", "CWE-78", "an OS command", Cs, @"(Process\.Start\(|new ProcessStartInfo)"),
        new("command", "CWE-78", "an OS command", Java, @"(Runtime\.getRuntime\(\)\.exec|new ProcessBuilder\()"),
        new("command", "CWE-78", "an OS command", Py, @"(subprocess\.|os\.system\(|os\.popen\()"),
        new("command", "CWE-78", "an OS command", Js, @"(child_process|execSync\(|spawnSync\(|exec\(|spawn\()"),
        new("command", "CWE-78", "an OS command", Go, @"exec\.Command"),
        new("command", "CWE-78", "an OS command", Php, @"(shell_exec|passthru|proc_open|popen\(|system\()"),
        new("command", "CWE-78", "an OS command", Rb, @"(system\(|Open3\.|%x\{|IO\.popen)"),
        new("deserialization", "CWE-502", "a deserialiser", Cs, @"(BinaryFormatter|TypeNameHandling\.(All|Auto|Objects)|LosFormatter|NetDataContractSerializer|SoapFormatter)"),
        new("deserialization", "CWE-502", "a deserialiser", Java, @"(ObjectInputStream|\.readObject\(|XMLDecoder|enableDefaultTyping)"),
        new("deserialization", "CWE-502", "a deserialiser", Py, @"(pickle\.loads?\(|yaml\.load\(|marshal\.loads|jsonpickle\.decode|shelve\.open)"),
        new("deserialization", "CWE-502", "a deserialiser", Js, @"(node-serialize|unserialize\(|yaml\.load\()"),
        new("deserialization", "CWE-502", "a deserialiser", Php, @"unserialize\("),
        new("deserialization", "CWE-502", "a deserialiser", Rb, @"(Marshal\.load|YAML\.load\()"),
        new("code", "CWE-94", "code run from text", Cs, @"(CSharpScript\.|Assembly\.Load\()"),
        new("code", "CWE-94", "code run from text", Java, @"(ScriptEngine|SpelExpressionParser|OgnlUtil)"),
        new("code", "CWE-94", "code run from text", Py, @"(eval\(|exec\()"),
        new("code", "CWE-94", "code run from text", Js, @"(eval\(|new Function\(|vm\.runIn)"),
        new("code", "CWE-94", "code run from text", Php, @"eval\("),
        new("code", "CWE-94", "code run from text", Rb, @"(eval\(|instance_eval|class_eval)"),
        new("xxe", "CWE-611", "an XML parser", Cs, @"(DtdProcessing\.Parse|XmlResolver *= *new|XmlUrlResolver)"),
        new("xxe", "CWE-611", "an XML parser", Java, @"(DocumentBuilderFactory|SAXParserFactory|XMLInputFactory|TransformerFactory)"),
        new("xxe", "CWE-611", "an XML parser", Py, @"(etree\.(parse|fromstring|XMLParser)|xml\.sax|minidom\.parse)"),
        new("path", "CWE-22", "a file path", Cs, @"(File\.(ReadAll|WriteAll|Open|Delete|Copy|Move)|new FileStream\(|PhysicalFile\()"),
        new("path", "CWE-22", "a file path", Java, @"(new File\(|Paths\.get\(|Files\.(read|write|newInputStream|newOutputStream))"),
        new("path", "CWE-22", "a file path", Py, @"(send_file\(|send_from_directory\(|FileResponse\(|open\()"),
        new("path", "CWE-22", "a file path", Js, @"(fs\.(readFile|writeFile|createReadStream|unlink)|res\.sendFile\(|res\.download\()"),
        new("path", "CWE-22", "a file path", Go, @"(os\.(Open|ReadFile|Create|Remove)\(|http\.ServeFile\()"),
        new("path", "CWE-22", "a file path", Php, @"((include|require)(_once)? *\(?\$|file_get_contents\(\$|fopen\(\$)"),
        new("xss", "CWE-79", "HTML written", Cs, @"(Html\.Raw\(|MarkupString\)|new HtmlString\()"),
        new("xss", "CWE-79", "HTML written", Java, @"getWriter\(\)\.(print|write)"),
        new("xss", "CWE-79", "HTML written", Py, @"(Markup\(|mark_safe\(|\|safe|render_template_string\()"),
        new("xss", "CWE-79", "HTML written", Js, @"(innerHTML|outerHTML|dangerouslySetInnerHTML|document\.write\(|v-html|insertAdjacentHTML)"),
        new("xss", "CWE-79", "HTML written", Go, @"template\.HTML\("),
        new("xss", "CWE-79", "HTML written", Php, @"echo .*\$_(GET|POST|REQUEST|COOKIE)"),
        new("ssrf", "CWE-918", "a URL fetched by the server", Cs, @"(\.GetAsync\(|\.GetStringAsync\(|\.PostAsync\(|WebRequest\.Create\()"),
        new("ssrf", "CWE-918", "a URL fetched by the server", Java, @"(new URL\(|RestTemplate|WebClient\.create)"),
        new("ssrf", "CWE-918", "a URL fetched by the server", Py, @"(requests\.(get|post|put|request)\(|urlopen\(|httpx\.(get|post))"),
        new("ssrf", "CWE-918", "a URL fetched by the server", Js, @"(axios\.(get|post|request)\(|http\.get\(|got\()"),
        new("ssrf", "CWE-918", "a URL fetched by the server", Go, @"http\.(Get|Post|NewRequest)\("),
        new("redirect", "CWE-601", "a redirect", Cs, @"Redirect\("),
        new("redirect", "CWE-601", "a redirect", Java, @"sendRedirect\("),
        new("redirect", "CWE-601", "a redirect", Py, @"redirect\("),
        new("redirect", "CWE-601", "a redirect", Js, @"res\.redirect\("),
        new("redirect", "CWE-601", "a redirect", Go, @"http\.Redirect\("),
    ];

    private static readonly Dictionary<string, int> Danger = new()
    {
        ["sql"] = 0, ["command"] = 1, ["deserialization"] = 2, ["code"] = 3, ["xxe"] = 4, ["path"] = 5, ["xss"] = 6, ["ssrf"] = 7, ["redirect"] = 8,
    };

    /// <summary>The most scanner findings a review keeps (the most severe first); the rest are counted.</summary>
    public const int MostScanned = 500;

    /// <summary>
    /// A weakness a scanner found that is about data reaching a sink, as the sink's kind: the model traces those as it
    /// traces git grep's. A scanner's other findings (TLS checks off, weak crypto, a debugger on) need no trace.
    /// </summary>
    private static readonly Dictionary<string, string> TracedCwes = new()
    {
        ["CWE-89"] = "sql", ["CWE-78"] = "command", ["CWE-77"] = "command", ["CWE-502"] = "deserialization", ["CWE-94"] = "code", ["CWE-95"] = "code",
        ["CWE-1336"] = "code", ["CWE-611"] = "xxe", ["CWE-22"] = "path", ["CWE-98"] = "path", ["CWE-79"] = "xss", ["CWE-80"] = "xss", ["CWE-918"] = "ssrf",
        ["CWE-601"] = "redirect",
    };

    /// <summary>What a scanner found, as Argus's scan_at gives it.</summary>
    public sealed record ScannerHit(string Kind, string[] Tools, string[] Rules, string Path, int Line, string Cwe, string Severity, string Confidence,
        string Title, string Message, string Code, string? Commit);

    /// <summary>A place where a sink is called: the file, the line and its text, and the sink it matched.</summary>
    public sealed record Candidate(string Path, int Line, string Text, Sink Sink);

    public void Start(Guid scanId) => _ = Task.Run(() => RunAsync(scanId, lifetime.ApplicationStopping));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Scans cut short by a restart say so.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SecurityScans.Where(s => s.Status == SecurityScan.Running || s.Status == SecurityScan.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SecurityScan.Failed).SetProperty(x => x.Error, "The app restarted while it ran: run it again."), cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            LogFailed(logger, Guid.Empty, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Tests, examples and vendored code: not where an attacker's data arrives.</summary>
    [GeneratedRegex(@"(^|/)(tests?|spec|specs|__tests__|testdata|fixtures?|examples?|samples?|node_modules|vendor|third_party|dist|build|e2e|migrations)/|\.(test|spec)\.[a-z]+$|_test\.go$|Tests?\.cs$|test_[^/]*\.py$", RegexOptions.IgnoreCase)]
    private static partial Regex NotProduct();

    [GeneratedRegex(@"^\s*(//|#|\*|/\*|--|<!--)")]
    private static partial Regex Comment();

    /// <summary>
    /// The sinks found, as git grep and the scanners gave them: deduplicated, without tests, vendored code or comments, what
    /// a scanner flagged first, then the most dangerous, at most three a file.
    /// </summary>
    public static List<Candidate> Order(IEnumerable<Candidate> found, IReadOnlySet<(string Path, int Line)>? flagged = null)
    {
        int Flag(Candidate c) => flagged?.Contains((c.Path, c.Line)) == true ? 0 : 1;
        return [.. found.Where(c => !NotProduct().IsMatch(c.Path) && !Comment().IsMatch(c.Text))
            .GroupBy(c => (c.Path, c.Line)).Select(g => g.OrderBy(c => Danger[c.Sink.Kind]).First())
            .OrderBy(Flag).ThenBy(c => Danger[c.Sink.Kind]).ThenBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Line)
            .GroupBy(c => c.Path).SelectMany(g => g.Take(MostPerFile).Select((c, i) => (c, i)))
            .OrderBy(x => Flag(x.c)).ThenBy(x => x.i).ThenBy(x => Danger[x.c.Sink.Kind]).Select(x => x.c)];
    }

    /// <summary>The name of the function a line is in, from the lines above it (C#, Java, Kotlin, Python, JavaScript, TypeScript, Go, PHP, Ruby).</summary>
    public static string? EnclosingFunction(IReadOnlyList<string> above)
    {
        for (var i = above.Count - 1; i >= 0; i--)
        {
            var m = Function().Match(above[i]);
            if (m.Success)
            {
                var name = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success && g.Value.Length > 0)?.Value;
                if (name is not null && !Keywords.Contains(name))
                {
                    return name;
                }
            }
        }
        return null;
    }

    private static readonly HashSet<string> Keywords = ["if", "for", "while", "switch", "catch", "using", "return", "foreach", "lock", "new", "else", "elif", "with", "function", "await"];

    [GeneratedRegex(@"^\s*(?:def|async def)\s+(\w+)\s*\(|^\s*func\s+(?:\([^)]*\)\s*)?(\w+)\s*\(|^\s*(?:export\s+)?(?:async\s+)?function\s*\*?\s*(\w+)\s*\(|^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*=>|^\s*(?:public|private|protected|internal|static|async|override|virtual|final|synchronized|suspend|fun|\s)+[\w<>\[\],.? ]*?\s(\w+)\s*\([^;]*$|^\s*def\s+(?:self\.)?(\w+)")]
    private static partial Regex Function();

    private async Task RunAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        if (await db.SecurityScans.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } scan)
        {
            return;
        }
        try
        {
            var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(scan.UserId.ToString())
                ?? throw new ReviewException("Its person is gone.");
            scan.Status = SecurityScan.Running;
            scan.Model = await ModelAsync(services, user, ct);
            var session = await services.GetRequiredService<ArgusMcp>().ConnectAsync(user.Id, user.Email!, ct);
            await StepAsync(db, scan, "Checking its packages against known vulnerabilities", ct);
            var packages = await DependenciesAsync(session, scan, ct);
            await StepAsync(db, scan, "Running the security scanners", ct);
            var scanned = await ScannersAsync(session, scan, ct);
            await StepAsync(db, scan, "Finding where data reaches dangerous calls", ct);
            var found = new List<Candidate>();
            var traced = scanned.Where(h => h.Kind == SecurityFinding.Code && h.Commit is null && TracedCwes.ContainsKey(h.Cwe)).ToList();
            found.AddRange(traced.Select(h => new Candidate(h.Path, h.Line, h.Code, new Sink(TracedCwes[h.Cwe], h.Cwe, h.Title, [], ""))));
            foreach (var sink in Sinks)
            {
                foreach (var glob in sink.Globs)
                {
                    var hits = await CallAsync(session, "grep_at", Args(scan, new JsonObject { ["pattern"] = sink.Pattern, ["path"] = glob, ["limit"] = 60 }), ct);
                    foreach (var h in (hits["matches"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (h["path"]?.ToString() is { Length: > 0 } path && h["line"] is JsonValue l && l.TryGetValue<int>(out var line))
                        {
                            found.Add(new Candidate(path, line, h["text"]?.ToString() ?? "", sink));
                        }
                    }
                }
            }
            var ordered = Order(found, traced.Select(h => (h.Path, h.Line)).ToHashSet());
            scan.Candidates = ordered.Count;
            var findings = new List<SecurityFinding>();
            var cweCache = new Dictionary<string, JsonObject?>();
            // What the model read and found no outside data reaching, and why: a scanner's finding there is kept apart.
            var cleared = new Dictionary<(string Path, int Line), string>();
            for (var i = 0; i < ordered.Count && i < MostRead; i++)
            {
                var c = ordered[i];
                await StepAsync(db, scan, $"Tracing {c.Path}:{c.Line} ({i + 1} of {Math.Min(ordered.Count, MostRead)})", ct);
                scan.Read++;
                var (context, lines) = await ContextAsync(session, scan, c, ct);
                var callers = await CallersAsync(session, scan, c, lines, ct);
                var flags = scanned.Where(h => h.Path == c.Path && Math.Abs(h.Line - c.Line) <= 3 && h.Kind == SecurityFinding.Code).ToList();
                var answer = Json(await AskAsync(services, scan, user.Email!, AnalyzePrompt, Question(c, context, callers, flags), 1_500, ct));
                if (answer?["vulnerable"] is not JsonValue v || !v.TryGetValue<bool>(out var vulnerable) || !vulnerable)
                {
                    cleared[(c.Path, c.Line)] = Cut(answer?["check"]?.ToString() ?? "", 600);
                    continue;
                }
                if (await GroundAsync(session, scan, c, answer, lines, cweCache, ct) is { } finding)
                {
                    var near = flags.Where(f => Math.Abs(f.Line - finding.Line) <= 3).ToList();
                    finding.FoundBy = [SecurityFinding.Model, .. near.SelectMany(f => f.Tools).Distinct()];
                    finding.Rule = near.Count == 0 ? null : Cut(string.Join(", ", near.SelectMany(f => f.Rules).Distinct()), 500);
                    findings.Add(finding);
                }
                else
                {
                    scan.Dropped++;
                }
            }
            var checkedFirst = findings.OrderBy(f => Rank(f.Severity)).ToList();
            for (var i = 0; i < checkedFirst.Count && i < MostVerified; i++)
            {
                await StepAsync(db, scan, $"Checking finding {i + 1} of {Math.Min(checkedFirst.Count, MostVerified)}", ct);
                await VerifyAsync(services, session, scan, user.Email!, checkedFirst[i], cweCache, ct);
            }
            scan.Findings.AddRange([.. packages, .. checkedFirst, .. await FromScannersAsync(session, scanned, checkedFirst, cleared, cweCache, ct)]);
            await RememberAsync(db, scan, ct);
            db.SecurityFindings.AddRange(scan.Findings.Select(f => { f.ScanId = scan.Id; return f; }));
            scan.Summary = Summary(scan, ordered, scan.Findings);
            scan.Status = SecurityScan.Done;
            scan.Step = null;
            scan.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await NotifyAsync(services, scan, ct);
            await SecurityOverview.AlertAsync(services, scan, scan.Findings, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailed(logger, id, ex);
            scan.Status = SecurityScan.Failed;
            scan.Error = ex is ReviewException or ChatGatewayException ? ex.Message : "The security review failed on the server. Try again, and tell an admin if it keeps failing.";
            scan.Step = null;
            scan.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(services, scan, CancellationToken.None);
        }
    }

    /// <summary>
    /// The repository's packages matched against the advisories (Argus's deps_check, no model) and its bill of materials:
    /// a finding per package version an advisory affects. Without an advisories pack, or a tool that cannot read the
    /// repository's lock files, the review goes on with its code alone and says so.
    /// </summary>
    private static async Task<List<SecurityFinding>> DependenciesAsync(McpSession session, SecurityScan scan, CancellationToken ct)
    {
        JsonObject check;
        try
        {
            check = await CallAsync(session, "deps_check", Args(scan, []), ct);
            var bom = await CallAnyAsync(session, "sbom", Args(scan, []), ct);
            scan.Sbom = bom?.ToJsonString() is { Length: <= 5_000_000 } json ? json : null;
        }
        catch (ReviewException)
        {
            return [];
        }
        scan.Commit ??= check["sha"]?.ToString() is { Length: > 0 and <= 64 } sha ? sha : null;
        scan.Dependencies = check["dependencies"] is JsonValue n && n.TryGetValue<int>(out var count) ? count : 0;
        scan.AdvisoriesAsOf = Rows(check["advisories"]).Select(a => DateOnly.TryParse(a["as_of"]?.ToString(), CultureInfo.InvariantCulture, out var day) ? day : (DateOnly?)null)
            .FirstOrDefault(d => d is not null);
        return [.. Rows(check["findings"]).Select(PackageFinding)];
    }

    /// <summary>
    /// The security scanners on the commit, its history too (Argus's scan_at): what they found, the most severe first.
    /// Without the scanners feature, or when they cannot scan, the review goes on without them and says why.
    /// </summary>
    private static async Task<List<ScannerHit>> ScannersAsync(McpSession session, SecurityScan scan, CancellationToken ct)
    {
        JsonObject result;
        try
        {
            result = await CallAsync(session, "scan_at", Args(scan, new JsonObject { ["history"] = true }), ct);
        }
        catch (ReviewException ex)
        {
            scan.Scanners = new JsonObject { ["tools"] = new JsonArray(), ["error"] = Cut(ex.Message, 500) }.ToJsonString();
            return [];
        }
        static string[] Strings(JsonNode? n) => [.. (n as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>()];
        var hits = Rows(result["findings"]).Select(f => new ScannerHit(
            f["kind"]?.ToString() is SecurityFinding.Secret or SecurityFinding.Config ? f["kind"]!.ToString() : SecurityFinding.Code,
            Strings(f["tools"]), Strings(f["rules"]), Cut(f["path"]?.ToString() ?? "", 1_000),
            f["line"] is JsonValue l && l.TryGetValue<int>(out var line) ? line : 0, Cwe(f["cwe"]?.ToString()) ?? "",
            f["severity"]?.ToString() is "critical" or "high" or "medium" or "low" ? f["severity"]!.ToString() : "medium",
            f["confidence"]?.ToString() ?? "medium", Cut(f["title"]?.ToString() ?? "", 300), Cut(f["message"]?.ToString() ?? "", 4_000),
            Cut(f["code"]?.ToString() ?? "", 1_000), f["commit"]?.ToString() is { Length: > 0 } commit ? Cut(commit, 64) : null))
            .Where(h => h.Path.Length > 0).ToList();
        scan.Commit ??= result["sha"]?.ToString() is { Length: > 0 and <= 64 } sha ? sha : null;
        var more = result["more"] is JsonValue m && m.TryGetValue<int>(out var n) ? n : 0;
        scan.Scanners = new JsonObject
        {
            ["tools"] = result["tools"]?.DeepClone() ?? new JsonArray(), ["history"] = result["history"]?.DeepClone(), ["found"] = hits.Count + more,
        }.ToJsonString();
        return hits;
    }

    /// <summary>
    /// The scanners' findings as the review's: one the model proved too is that finding (both named); one the model read and
    /// found no outside data reaching is kept apart with why; the others as the scanners found them, each with the security
    /// pack's page for its CWE where the pack has one.
    /// </summary>
    private static async Task<List<SecurityFinding>> FromScannersAsync(McpSession session, List<ScannerHit> hits, List<SecurityFinding> proven,
        Dictionary<(string Path, int Line), string> cleared, Dictionary<string, JsonObject?> cweCache, CancellationToken ct)
    {
        var found = new List<SecurityFinding>();
        foreach (var h in hits.Where(h => !proven.Any(p => p.Path == h.Path && Math.Abs(p.Line - h.Line) <= 3 && h.Kind == SecurityFinding.Code))
                     .OrderBy(h => Rank(h.Severity)).Take(MostScanned))
        {
            var page = h.Cwe.Length > 0 ? await CweAsync(session, h.Cwe, cweCache, ct) : null;
            var why = cleared.FirstOrDefault(c => c.Key.Path == h.Path && Math.Abs(c.Key.Line - h.Line) <= 3);
            found.Add(new SecurityFinding
            {
                Kind = h.Kind, FoundBy = h.Tools, Rule = Cut(string.Join(", ", h.Rules), 500), Path = h.Path, Line = h.Line, SinkCode = h.Code,
                Cwe = Cut(h.Cwe, 16), CweTitle = Cut(page?["signature"]?.ToString() ?? h.Cwe, 300), Severity = h.Severity, Title = h.Title.Length > 0 ? h.Title : h.Rules.FirstOrDefault() ?? "",
                Fix = h.Message, Commit = h.Commit,
                Pages = (page is null ? new JsonArray() : new JsonArray(new JsonObject { ["title"] = page["signature"]?.ToString(), ["url"] = page["url"]?.ToString() })).ToJsonString(),
                Verified = why.Key.Path is null ? null : false,
                VerifyNote = why.Key.Path is null ? null
                    : $"The model read it with its callers and found no outside data reaching it.{(why.Value.Length > 0 ? " To check: " + why.Value : "")}",
            });
        }
        return found;
    }

    /// <summary>A package version an advisory affects, as deps_check gave it, made a finding.</summary>
    public static SecurityFinding PackageFinding(JsonObject f)
    {
        var package = f["package"]?.ToString() ?? "";
        var version = f["version"]?.ToString() ?? "";
        var cwes = (f["cwes"] as JsonArray ?? []).Select(c => c?.ToString()).OfType<string>().ToList();
        var aliases = (f["aliases"] as JsonArray ?? []).Select(c => c?.ToString()).OfType<string>().ToList();
        var exploited = f["exploited"] is JsonValue e && e.TryGetValue<bool>(out var x) && x;
        var upgrade = f["upgrade_to"]?.ToString();
        return new SecurityFinding
        {
            Kind = SecurityFinding.Dependency, Exploited = exploited, Path = Cut(f["file"]?.ToString() ?? "", 1_000),
            Line = f["line"] is JsonValue l && l.TryGetValue<int>(out var line) ? line : 0, SinkCode = Cut($"{package} {version} ({f["ecosystem"]})", 1_000),
            Cwe = Cut(cwes.FirstOrDefault() ?? "CWE-1395", 16), CweTitle = cwes.Count > 0 ? string.Join(", ", cwes) : "CWE-1395: Dependency on Vulnerable Third-Party Component",
            Severity = f["severity"]?.ToString() is "critical" or "high" or "medium" or "low" ? f["severity"]!.ToString() : "medium",
            Title = Cut(f["summary"]?.ToString() is { Length: > 0 } summary ? summary : $"{package} {version} has a known vulnerability", 300),
            Source = Cut(string.Join(", ", [f["advisory"]?.ToString(), .. aliases]), 1_000),
            Flow = f["direct"] is JsonValue dv && dv.TryGetValue<bool>(out var direct) && direct ? $"{package} is asked for directly" : $"{package} comes in through {f["via"]?.ToString() ?? "another package"}",
            Exploit = exploited ? "On CISA's list of known exploited vulnerabilities: attackers use it." : "",
            Fix = upgrade is not null ? $"Upgrade {package} to {upgrade} or later (the smallest version that fixes all of its advisories here)." : $"No fixed version of {package} is known: replace it, or keep what reaches it away from the vulnerable part.",
            Pages = new JsonArray(new JsonObject { ["title"] = $"{f["advisory"]}: {Cut(f["summary"]?.ToString() ?? "", 120)}", ["url"] = f["url"]?.ToString() }).ToJsonString(),
            Verified = true,
        };
    }

    /// <summary>What makes a finding the same one in another review: its kind and file, its weakness or rule, and its line's text (lines move).</summary>
    public static string Sameness(SecurityFinding f) => f.Kind == SecurityFinding.Dependency
        ? $"{f.Kind}|{f.Source?.Split(',')[0].Trim()}|{f.SinkCode}"
        : $"{f.Kind}|{f.Path}|{(f.Kind == SecurityFinding.Code ? f.Cwe : f.Rule ?? f.Cwe)}|{Squash(f.SinkCode)}";

    /// <summary>
    /// What people decided about this repository's findings before, kept: one marked not a weakness stays so (with who said
    /// it and why), a risk accepted stays accepted until its date. One marked fixed is not kept: found again, it is open again.
    /// </summary>
    private static async Task RememberAsync(AppDbContext db, SecurityScan scan, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var decided = await db.SecurityFindings.AsNoTracking()
            .Where(f => (f.State == SecurityFinding.Wrong || (f.State == SecurityFinding.Accepted && f.Until > now))
                && db.SecurityScans.Any(s => s.Id == f.ScanId && s.Project == scan.Project && s.Id != scan.Id))
            .OrderByDescending(f => f.DecidedAt).ToListAsync(ct);
        var known = decided.GroupBy(Sameness).ToDictionary(g => g.Key, g => g.First());
        foreach (var f in scan.Findings)
        {
            if (known.GetValueOrDefault(Sameness(f)) is not { } before)
            {
                continue;
            }
            (f.State, f.Why, f.Until, f.DecidedById, f.DecidedAt) = (before.State, before.Why, before.Until, before.DecidedById, before.DecidedAt);
            scan.Remembered++;
        }
    }

    private static JsonObject Args(SecurityScan scan, JsonObject args)
    {
        args["project"] = scan.Project;
        if (scan.Ref is { Length: > 0 } r)
        {
            args["ref"] = r;
        }
        return args;
    }

    /// <summary>The sink's function, from up to 60 lines above it to 15 below, numbered; and those lines by number.</summary>
    private static async Task<(string Text, Dictionary<int, string> Lines)> ContextAsync(McpSession session, SecurityScan scan, Candidate c, CancellationToken ct)
    {
        var file = await CallAsync(session, "file_at", Args(scan, new JsonObject { ["path"] = c.Path, ["start_line"] = Math.Max(1, c.Line - 60), ["end_line"] = c.Line + 15 }), ct);
        var start = file["start_line"] is JsonValue s && s.TryGetValue<int>(out var n) ? n : 1;
        var lines = new Dictionary<int, string>();
        var sb = new StringBuilder();
        foreach (var (text, i) in (file["content"]?.ToString() ?? "").Split('\n').Select((t, i) => (t, i)))
        {
            lines[start + i] = text;
            sb.Append((start + i).ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(start + i == c.Line ? " >" : "  ").Append(text).Append('\n');
        }
        return (sb.ToString(), lines);
    }

    /// <summary>Who calls the sink's function: up to four places, each with the lines around it.</summary>
    private static async Task<string> CallersAsync(McpSession session, SecurityScan scan, Candidate c, Dictionary<int, string> lines, CancellationToken ct)
    {
        var name = EnclosingFunction([.. lines.Where(l => l.Key <= c.Line).OrderBy(l => l.Key).Select(l => l.Value)]);
        if (name is null)
        {
            return "";
        }
        var hits = await CallAsync(session, "grep_at", Args(scan, new JsonObject { ["pattern"] = $@"[^A-Za-z0-9_]{Regex.Escape(name)} *\(", ["limit"] = 20 }), ct);
        var sb = new StringBuilder($"The sink is in `{name}`. Its callers:\n");
        var n = 0;
        foreach (var h in (hits["matches"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var path = h["path"]?.ToString() ?? "";
            var line = h["line"] is JsonValue l && l.TryGetValue<int>(out var x) ? x : 0;
            var text = h["text"]?.ToString() ?? "";
            if (n >= 4 || line == 0 || (path == c.Path && lines.ContainsKey(line)) || NotProduct().IsMatch(path) || Function().IsMatch(text))
            {
                continue;
            }
            var around = await CallAsync(session, "file_at", Args(scan, new JsonObject { ["path"] = path, ["start_line"] = Math.Max(1, line - 12), ["end_line"] = line + 6 }), ct);
            var start = around["start_line"] is JsonValue s && s.TryGetValue<int>(out var first) ? first : 1;
            sb.Append(CultureInfo.InvariantCulture, $"\n{path}:\n");
            foreach (var (t, i) in (around["content"]?.ToString() ?? "").Split('\n').Select((t, i) => (t, i)))
            {
                sb.Append((start + i).ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(start + i == line ? " >" : "  ").Append(t).Append('\n');
            }
            n++;
        }
        return n == 0 ? $"The sink is in `{name}`; no caller was found outside it (it may be an entry point itself, or called by name from elsewhere)." : sb.ToString();
    }

    public const string AnalyzePrompt =
        "You are an application security engineer reviewing one place in a repository where a dangerous call is made. You get the code " +
        "around it, its lines numbered (the call's line marked >), and the places that call its function. Decide whether data from " +
        "outside the program (an HTTP request, a message, an uploaded file, command-line arguments, a webhook) can reach the call " +
        "without what the weakness needs (parameters for a query, an allow-list for a path or URL, encoding for HTML, a safe parser " +
        "setting). Say vulnerable only when the code shown proves the path, or a caller shown clearly passes outside data; when it " +
        "depends on code not shown, say not vulnerable and what to check. Constant strings, configuration and data the program made " +
        "itself are not outside data.\n\n" +
        "Answer with JSON only: {\"vulnerable\": true|false, \"cwe\": \"CWE-89\", \"severity\": \"critical|high|medium|low\", " +
        "\"title\": \"short\", \"sink_line\": 123, \"sink_code\": \"the sink's line, copied exactly as shown\", " +
        "\"source\": \"where the outside data comes in: path:line and what\", \"flow\": [\"each step from the source to the sink\"], " +
        "\"exploit\": \"how an attacker would use it\", \"fix\": \"the change that removes it\", \"check\": \"what to look at when not proven\"}.";

    public const string VerifyPrompt =
        "You check another engineer's security finding as a skeptic: try to REFUTE it. It is real only if the code shown proves that " +
        "outside data reaches the sink without what the weakness (its CWE page is given) needs, and nothing shown (validation, a " +
        "parameterised call, an allow-list, encoding, a framework's protection, the data being the program's own) prevents it. When " +
        "unsure, it is not real. Answer with JSON only: {\"real\": true|false, \"why\": \"one or two sentences\"}.";

    public static string Question(Candidate c, string context, string callers, List<ScannerHit> flags) =>
        $"Repository file {c.Path}, the call at line {c.Line} is {c.Sink.What} (as {c.Sink.Cwe} would be): `{Cut(c.Text.Trim(), 300)}`\n\n" +
        $"```\n{Cut(context, 12_000)}```\n\n{Cut(callers, 10_000)}" +
        (flags.Count == 0 ? "" : "\n\nScanners matched a pattern here (a pattern is not proof: judge the code):\n" +
            string.Join('\n', flags.Select(f => $"- line {f.Line}, {string.Join(", ", f.Tools)} ({string.Join(", ", f.Rules)}): {f.Title}")));

    /// <summary>
    /// A suspicion made a finding only when it holds to what can be cited: the sink's line as the file has it (on the line said,
    /// or within three of it), and a CWE the security pack knows (the model's, or the sink's own). The pack's pages go with it.
    /// </summary>
    private static async Task<SecurityFinding?> GroundAsync(McpSession session, SecurityScan scan, Candidate c, JsonObject answer, Dictionary<int, string> lines,
        Dictionary<string, JsonObject?> cweCache, CancellationToken ct)
    {
        var cited = Squash(answer["sink_code"]?.ToString() ?? "");
        var said = answer["sink_line"] is JsonValue sl && sl.TryGetValue<int>(out var n) ? n : c.Line;
        if (cited.Length < 6)
        {
            return null;
        }
        var line = Enumerable.Range(said - 3, 7).OrderBy(x => Math.Abs(x - said))
            .FirstOrDefault(x => lines.TryGetValue(x, out var text) && Squash(text) is { Length: > 0 } t && (t.Contains(cited, StringComparison.Ordinal) || cited.Contains(t, StringComparison.Ordinal) && t.Length >= 6));
        if (line == 0)
        {
            return null;
        }
        var cwe = Cwe(answer["cwe"]?.ToString()) ?? c.Sink.Cwe;
        var page = await CweAsync(session, cwe, cweCache, ct);
        if (page is null && cwe != c.Sink.Cwe)
        {
            cwe = c.Sink.Cwe;
            page = await CweAsync(session, cwe, cweCache, ct);
        }
        if (page is null)
        {
            return null;
        }
        var pages = new JsonArray(new JsonObject { ["title"] = page["signature"]?.ToString() ?? cwe, ["url"] = page["url"]?.ToString() });
        var title = page["signature"]?.ToString() ?? cwe;
        foreach (var (lang, query) in new[] { ("cheatsheets", title), (CodeQlFacet(c.Path), title) })
        {
            if (lang is null)
            {
                continue;
            }
            try
            {
                var hits = await CallAnyAsync(session, "docs_search", new JsonObject { ["query"] = query, ["lang"] = lang }, ct);
                if (Rows(hits).FirstOrDefault() is { } top)
                {
                    pages.Add(new JsonObject { ["title"] = top["title"]?.ToString(), ["url"] = top["url"]?.ToString() });
                }
            }
            catch (ReviewException)
            {
                // A page to read is a help, not a condition.
            }
        }
        var flow = answer["flow"] is JsonArray steps ? string.Join('\n', steps.Select(s => s?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s))) : answer["flow"]?.ToString() ?? "";
        return new SecurityFinding
        {
            Path = c.Path, Line = line, SinkCode = Cut(lines[line].Trim(), 1_000), Cwe = cwe, CweTitle = Cut(page["signature"]?.ToString() ?? cwe, 300),
            Severity = answer["severity"]?.ToString() is "critical" or "high" or "medium" or "low" ? answer["severity"]!.ToString() : "medium",
            Title = Cut(answer["title"]?.ToString() is { Length: > 0 } t2 ? t2 : $"{c.Sink.What} from outside data", 300),
            Source = answer["source"]?.ToString() is { Length: > 0 } src ? Cut(src, 1_000) : null, Flow = Cut(flow, 4_000),
            Exploit = Cut(answer["exploit"]?.ToString() ?? "", 4_000), Fix = Cut(answer["fix"]?.ToString() ?? "", 4_000), Pages = pages.ToJsonString(),
        };
    }

    /// <summary>A CWE's id as the pack names it ("cwe-89", "CWE 89" and "89" are CWE-89); null when it is not one.</summary>
    public static string? Cwe(string? said) =>
        said is not null && CweId().Match(said) is { Success: true } m ? $"CWE-{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)}" : null;

    [GeneratedRegex(@"^\s*(?:CWE)?[\s-]*(\d{1,5})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CweId();

    /// <summary>The CWE's entry in the security pack, or null when the pack does not know it.</summary>
    private static async Task<JsonObject?> CweAsync(McpSession session, string cwe, Dictionary<string, JsonObject?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(cwe, out var known))
        {
            return known;
        }
        JsonObject? page;
        try
        {
            var found = await CallAnyAsync(session, "docs_lookup", new JsonObject { ["name"] = cwe, ["lang"] = "cwe" }, ct);
            page = Rows(found).FirstOrDefault(r => string.Equals(r["name"]?.ToString(), cwe, StringComparison.OrdinalIgnoreCase));
        }
        catch (ReviewException)
        {
            page = null;
        }
        cache[cwe] = page;
        return page;
    }

    private static string? CodeQlFacet(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "codeql-csharp",
        ".java" or ".kt" => "codeql-java",
        ".py" => "codeql-python",
        ".js" or ".ts" or ".jsx" or ".tsx" or ".vue" => "codeql-javascript",
        ".go" => "codeql-go",
        ".rb" => "codeql-ruby",
        _ => null,
    };

    private static async Task VerifyAsync(IServiceProvider services, McpSession session, SecurityScan scan, string email, SecurityFinding f, Dictionary<string, JsonObject?> cache,
        CancellationToken ct)
    {
        var page = cache.GetValueOrDefault(f.Cwe);
        var cweText = "";
        if (page?["doc_path"]?.ToString() is { Length: > 0 } docPath)
        {
            try
            {
                var doc = await CallAnyAsync(session, "docs_get", new JsonObject { ["doc_path"] = docPath, ["source"] = "security" }, ct);
                cweText = Cut(((doc as JsonObject)?["result"] as JsonObject ?? doc as JsonObject)?["text"]?.ToString() ?? "", 6_000);
            }
            catch (ReviewException)
            {
                cweText = f.CweTitle;
            }
        }
        var file = await CallAsync(session, "file_at", Args(scan, new JsonObject { ["path"] = f.Path, ["start_line"] = Math.Max(1, f.Line - 40), ["end_line"] = f.Line + 15 }), ct);
        var ask = $"The weakness, as the security pack has it:\n{(cweText.Length > 0 ? cweText : f.CweTitle)}\n\nThe file {f.Path}, from line {file["start_line"]}:\n```\n{Cut(file["content"]?.ToString() ?? "", 10_000)}```\n\n" +
            $"The finding ({f.Cwe}, {f.Severity}) at line {f.Line}: {f.Title}\nSource: {f.Source}\nFlow:\n{f.Flow}\nExploit: {f.Exploit}";
        if (Json(await AskAsync(services, scan, email, VerifyPrompt, ask, 400, ct)) is { } verdict && verdict["real"] is JsonValue real && real.TryGetValue<bool>(out var isReal))
        {
            f.Verified = isReal;
            f.VerifyNote = Cut(verdict["why"]?.ToString() ?? "", 1_000);
        }
    }

    /// <summary>What was done and found, written from the counts (no model).</summary>
    public static string Summary(SecurityScan scan, List<Candidate> candidates, IEnumerable<SecurityFinding> findings)
    {
        var all = findings.ToList();
        var packages = all.Where(f => f.Kind == SecurityFinding.Dependency).ToList();
        // The model's own findings (a scanner may have found them too); the scanners' alone are counted after.
        var code = all.Where(f => f.Kind == SecurityFinding.Code && (f.FoundBy.Length == 0 || f.FoundBy.Contains(SecurityFinding.Model))).ToList();
        var kept = code.Where(f => f.Verified != false).ToList();
        var sb = new StringBuilder();
        if (scan.AdvisoriesAsOf is { } asOf)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{scan.Dependencies} package(s) checked against the advisories of {asOf:yyyy-MM-dd}: ");
            sb.Append(packages.Count == 0 ? "none is known to be vulnerable. " : $"{packages.Count} known vulnerabilit(ies)" +
                (packages.Count(f => f.Exploited) is > 0 and var k ? $", {k} known to be exploited" : "") + ". ");
        }
        else
        {
            sb.Append("Packages not checked: no advisories pack is loaded (Admin → Packs). ");
        }
        sb.Append(CultureInfo.InvariantCulture, $"{candidates.Count} place(s) where data reaches a dangerous call; the {scan.Read} most dangerous read with their callers. ");
        sb.Append(kept.Count == 0 ? "No weakness was proven in the code." : $"{kept.Count} weakness(es) in the code: " +
            string.Join(", ", kept.GroupBy(f => f.Severity).OrderBy(g => Rank(g.Key)).Select(g => $"{g.Count()} {g.Key}")) + ".");
        if (code.Count > kept.Count)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {code.Count - kept.Count} refuted by the second pass (kept apart).");
        }
        if (scan.Remembered > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {scan.Remembered} decided in an earlier review of the repository (not a weakness, or a risk accepted until its date), and kept so.");
        }
        if (scan.Dropped > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {scan.Dropped} suspicion(s) dropped: the sink's line could not be cited, or the weakness is not a CWE the security pack knows.");
        }
        var kinds = candidates.GroupBy(c => c.Sink.Kind).OrderBy(g => Danger[g.Key]).Select(g => $"{g.Key} {g.Count()}");
        sb.Append("\n\nFound by kind: ").Append(string.Join(", ", kinds)).Append('.');
        if (candidates.Count > scan.Read)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {candidates.Count - scan.Read} were not read (past the {MostRead} most dangerous): run it on a narrower branch, or look at them by kind.");
        }
        var scanners = scan.Scanners is { } json ? JsonNode.Parse(json) as JsonObject : null;
        var ran = Rows(scanners?["tools"]).Where(t => t["ran"] is JsonValue r && r.TryGetValue<bool>(out var yes) && yes).Select(t => t["name"]?.ToString()).ToList();
        if (ran.Count > 0)
        {
            var scanned = all.Where(f => f.FoundBy.Any(t => t != SecurityFinding.Model)).ToList();
            var both = scanned.Count(f => f.FoundBy.Contains(SecurityFinding.Model));
            sb.Append(CultureInfo.InvariantCulture, $"\n\nScanners ({string.Join(", ", ran)}): {scanned.Count} finding(s)");
            sb.Append(both > 0 ? $", {both} traced by the model too" : "");
            sb.Append(scanned.Count(f => f.Verified == false && f.FoundBy.All(t => t != SecurityFinding.Model)) is > 0 and var c ? $", {c} the model read and cleared (kept apart)" : "");
            sb.Append(scanned.Count(f => f.Kind == SecurityFinding.Secret) is > 0 and var secrets ? $", {secrets} secret(s)" : "");
            sb.Append(scanned.Count(f => f.Kind == SecurityFinding.Config) is > 0 and var config ? $", {config} in configuration" : "");
            sb.Append('.');
            foreach (var t in Rows(scanners?["tools"]).Where(t => t["error"]?.ToString() is { Length: > 0 }))
            {
                sb.Append(CultureInfo.InvariantCulture, $" {t["name"]} did not run: {t["error"]}.");
            }
            sb.Append("\n\nNot covered: authorisation and access checks, and business logic.");
        }
        else
        {
            sb.Append(scanners?["error"]?.ToString() is { Length: > 0 } error
                ? $"\n\nThe scanners did not run ({error}), so secrets and configuration were not looked at."
                : "\n\nNot covered: authorisation and access checks, secrets and configuration (the scanners feature looks at those).");
        }
        return sb.ToString();
    }

    private static async Task StepAsync(AppDbContext db, SecurityScan scan, string step, CancellationToken ct)
    {
        scan.Step = step;
        await db.SaveChangesAsync(ct);
    }

    private static async Task NotifyAsync(IServiceProvider services, SecurityScan scan, CancellationToken ct)
    {
        var kept = scan.Findings.Count(f => f.Verified != false);
        await services.GetRequiredService<Notifications.Notifier>().SendAsync(scan.UserId, new Notifications.News("review",
            scan.Status == SecurityScan.Done ? $"Security review ready: {scan.Project}" : $"Security review failed: {scan.Project}",
            scan.Status == SecurityScan.Done ? $"{kept} weakness(es) found" : scan.Error, $"/code/security/{scan.Id}"), ct);
    }

    private static int Rank(string severity) => severity switch { "critical" => 0, "high" => 1, "medium" => 2, _ => 3 };

    private static async Task<string> ModelAsync(IServiceProvider services, AppUser user, CancellationToken ct)
    {
        var policy = services.GetRequiredService<ModelPolicy>();
        var models = services.GetRequiredService<ChatModels>();
        var name = (await policy.ForAsync(user, await models.ListAsync(ct), ct)).Default?.Name ?? throw new ReviewException("No model is there for you to review with.");
        if (await policy.RefusalAsync(user, name, ct) is { } refused)
        {
            throw new ReviewException(refused);
        }
        return name;
    }

    private static async Task<JsonObject> CallAsync(McpSession session, string tool, JsonObject args, CancellationToken ct) =>
        await CallAnyAsync(session, tool, args, ct) as JsonObject ?? throw new ReviewException($"Argus's {tool} answered something else.");

    /// <summary>A tool's answer as JSON: an object, or a list (a list tool's rows, as the client unwraps them).</summary>
    private static async Task<JsonNode?> CallAnyAsync(McpSession session, string tool, JsonObject args, CancellationToken ct)
    {
        var (text, isError) = await session.CallAsync(tool, args, null, ct);
        if (isError)
        {
            throw new ReviewException(text.Length > 500 ? text[..500] : text);
        }
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ReviewException($"Argus's {tool} answered something else.");
        }
    }

    /// <summary>A list tool's rows, whether they came as the list or as {"result": [...]}.</summary>
    private static IEnumerable<JsonObject> Rows(JsonNode? node) => (node as JsonArray ?? (node as JsonObject)?["result"] as JsonArray ?? []).OfType<JsonObject>();

    /// <summary>One request to the model, as the person, tagged "security"; its tokens counted on the scan.</summary>
    private static async Task<string> AskAsync(IServiceProvider services, SecurityScan scan, string email, string system, string ask, int maxTokens, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = scan.Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = ask }),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.1,
            ["user"] = email,
            ["metadata"] = new JsonObject { ["tags"] = new JsonArray(Tag) },
            ["chat_template_kwargs"] = ThinkingPresets.TemplateKwargs("off"),
        };
        var text = new StringBuilder();
        await foreach (var e in services.GetRequiredService<GatewayChat>().StreamAsync(request, email, ct))
        {
            if (e is ContentDelta c)
            {
                text.Append(c.Text);
            }
            else if (e is UsageReport u)
            {
                scan.PromptTokens += u.Prompt;
                scan.CompletionTokens += u.Completion;
            }
        }
        return text.ToString();
    }

    private static JsonObject? Json(string text) => ReviewEngine.Json(text);

    /// <summary>A line with its spaces squashed, to compare a cited line with the file's.</summary>
    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security review {Id} failed")]
    private static partial void LogFailed(ILogger logger, Guid id, Exception ex);
}
