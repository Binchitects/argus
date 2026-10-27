using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Compliance;

/// <summary>One thing a certification is not ready on: what, and where it is put right.</summary>
public sealed record ReadinessGap(string What, string Where);

/// <param name="Score">0 to 100: Arena's controls holding, their operator's parts attested, and the management system's checks.</param>
public sealed record FrameworkReadiness(string Framework, string Title, int Score, int Controls, int Holding, int Attested, IReadOnlyList<ReadinessGap> Gaps);

/// <summary>
/// How ready the company is for each framework's certification or audit (Admin → Compliance): Arena's controls that serve it
/// holding now, their operator's parts attested within the year, and the checks of the management system it needs (risks, the
/// Statement of Applicability, policies, audits, suppliers, AI systems...), each with what is missing and where; and, for each,
/// the package its auditor reads.
/// </summary>
public sealed class Readiness(AppDbContext db, ComplianceChecks checks, IServiceProvider services, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The management system's checks each framework's audit reads, beside its controls.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Governance = new Dictionary<string, string[]>
    {
        ["iso27001"] = ["risks", "soa", "policies", "audits", "suppliers", "access-review", "incidents"],
        ["iso42001"] = ["risks", "soa", "policies", "audits", "suppliers", "ai-systems"],
        ["iso22301"] = ["risks", "policies", "audits", "backups"],
        ["soc2"] = ["risks", "policies", "audits", "suppliers", "access-review", "incidents"],
        ["hipaa"] = ["risks", "policies", "access-review", "incidents", "suppliers"],
        ["gdpr"] = ["policies", "suppliers", "data-requests", "incidents"],
        ["pcidss"] = ["risks", "policies", "audits", "suppliers", "access-review"],
        ["csaccm"] = ["risks", "policies", "audits", "suppliers"],
        ["nistcsf"] = ["risks", "policies", "audits", "suppliers", "incidents"],
        ["nist80053"] = ["risks", "policies", "audits", "suppliers", "incidents", "access-review"],
        ["nist800171"] = ["risks", "policies", "audits", "incidents", "access-review"],
        ["airmf"] = ["ai-systems", "risks", "policies"],
        ["euaiact"] = ["ai-systems", "risks", "policies", "incidents"],
        ["commoncriteria"] = [],
    };

    private static readonly Dictionary<string, string> Tabs = new()
    {
        ["risks"] = "Admin → Compliance → Risks", ["soa"] = "Admin → Compliance → Statement of Applicability", ["policies"] = "Admin → Compliance → Policies",
        ["audits"] = "Admin → Compliance → Audits", ["suppliers"] = "Admin → Compliance → Suppliers", ["ai-systems"] = "Admin → Compliance → AI systems",
        ["access-review"] = "Admin → Compliance → Access reviews", ["incidents"] = "Admin → Compliance → Incidents", ["data-requests"] = "Admin → Compliance → Data requests",
        ["backups"] = "scripts/backup.sh and its restore drill",
    };

    public async Task<IReadOnlyList<FrameworkReadiness>> AllAsync(CancellationToken ct)
    {
        var results = await checks.RunAsync(ct);
        var attested = (await db.ComplianceAttestations.AsNoTracking().ToListAsync(ct)).GroupBy(a => a.ControlId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.At)!);
        var view = ComplianceEndpoints.View(results, attested, clock.GetUtcNow());
        var byCheck = results.ToDictionary(r => r.Id);
        return [.. ComplianceControls.Catalogs.Keys.Select(fw => Of(fw, view, byCheck))];
    }

    private FrameworkReadiness Of(string fw, ComplianceView view, Dictionary<string, CheckResult> byCheck)
    {
        static double Value(string? state) => state switch { ComplianceChecks.Fail => 0, ComplianceChecks.Warn or ComplianceChecks.Unknown => 0.5, _ => 1 };
        var now = clock.GetUtcNow();
        var serving = view.Controls.Where(c => c.Frameworks.TryGetValue(fw, out var cl) && cl.Count > 0).ToList();
        var gaps = new List<ReadinessGap>();
        foreach (var c in serving.Where(c => c.State is ComplianceChecks.Fail or ComplianceChecks.Warn))
        {
            gaps.Add(new($"{c.Id} ({c.Title}) does not hold now", "Admin → Compliance → Frameworks and controls"));
        }
        var unattested = serving.Where(c => c.Attested is null || c.Attested.Due < now).Select(c => c.Id).ToList();
        if (unattested.Count > 0)
        {
            gaps.Add(new($"the company's part not attested within the year: {string.Join(" ", unattested)}", "each control's Attest, Admin → Compliance"));
        }
        var parts = new List<double>();
        if (serving.Count > 0)
        {
            parts.Add(serving.Average(c => Value(c.State)));
            parts.Add((serving.Count - unattested.Count) / (double)serving.Count);
        }
        foreach (var id in Governance.GetValueOrDefault(fw) ?? [])
        {
            if (byCheck.TryGetValue(id, out var r))
            {
                parts.Add(Value(r.State));
                if (r.State != ComplianceChecks.Ok)
                {
                    gaps.Add(new($"{r.Title}: {r.Detail}", Tabs.GetValueOrDefault(id, "Admin → Compliance")));
                }
            }
        }
        var score = parts.Count == 0 ? 0 : (int)Math.Round(parts.Average() * 100);
        return new FrameworkReadiness(fw, ComplianceControls.Catalogs[fw].Title, score, serving.Count, serving.Count(c => Value(c.State) == 1), serving.Count - unattested.Count, gaps);
    }

    /// <summary>
    /// What a framework's auditor reads, in one zip: its clauses with Arena's controls, their state and attestations; the checks'
    /// daily history over the period asked (a year by default); the records of the management system its audit reads; and the
    /// drafts its scheme asks for (a Common Criteria Security Target, the AI Act's Annex IV for each high-risk system, a CSF profile).
    /// </summary>
    public async Task<byte[]> PackageAsync(string fw, DateOnly? from, DateOnly? to, string by, CancellationToken ct)
    {
        var catalog = ComplianceControls.Catalogs.GetValueOrDefault(fw) ?? throw new KeyNotFoundException();
        var now = clock.GetUtcNow();
        var until = to ?? DateOnly.FromDateTime(now.UtcDateTime);
        var since = from ?? until.AddYears(-1);
        var results = await checks.RunAsync(ct);
        var attested = (await db.ComplianceAttestations.AsNoTracking().ToListAsync(ct)).GroupBy(a => a.ControlId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.At)!);
        var view = ComplianceEndpoints.View(results, attested, now);
        var readiness = Of(fw, view, results.ToDictionary(r => r.Id));
        var serving = new Dictionary<string, List<ControlView>>(StringComparer.Ordinal);
        foreach (var c in view.Controls)
        {
            foreach (var clause in c.Frameworks.GetValueOrDefault(fw) ?? [])
            {
                if (catalog.Entry(clause) is { } e)
                {
                    (serving.TryGetValue(e, out var l) ? l : serving[e] = []).Add(c);
                }
            }
        }
        var controlChecks = view.Controls.Where(c => c.Frameworks.ContainsKey(fw)).SelectMany(c => c.Checks).Where(k => k.StartsWith("app:", StringComparison.Ordinal))
            .Select(k => k[4..]).Concat(Governance.GetValueOrDefault(fw) ?? []).ToHashSet(StringComparer.Ordinal);
        var history = await db.ComplianceSnapshots.AsNoTracking().Where(s => s.Day >= since && s.Day <= until && controlChecks.Contains(s.Check))
            .OrderBy(s => s.Day).ThenBy(s => s.Check).ToListAsync(ct);
        var documents = Documents();

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            string Csv(IEnumerable<IEnumerable<object?>> rows) => string.Join("\n", rows.Select(r => string.Join(",", r.Select(Cell)))) + "\n";
            static string? U(DateTimeOffset? d) => d?.ToString("u", CultureInfo.InvariantCulture);

            var files = new List<(string Name, string What)>
            {
                ("clauses.csv", "every clause: its title, the company's document, Arena's controls that serve it, their state now and their attestation"),
                ("readiness.json", $"how ready, {readiness.Score} of 100, and what is missing and where"),
                ("check-history.csv", $"each check of its controls and its management system, day by day, {since:yyyy-MM-dd} to {until:yyyy-MM-dd}"),
                ("controls.yaml", "Arena's controls as this release ships them: what proves each"),
            };
            Text("clauses.csv", Csv([["clause", "title", "the company's document", "Arena's controls", "state now", "attested", "attestation due"],
                .. catalog.Clauses.Select(c =>
                {
                    var cs = serving.GetValueOrDefault(c.Id) ?? [];
                    return new object?[]
                    {
                        c.Id, c.Title, documents.GetValueOrDefault(c.Document, c.Document), string.Join(" ", cs.Select(x => x.Id)),
                        string.Join(" ", cs.Select(x => $"{x.Id}:{x.State ?? "outside"}")), string.Join(" ", cs.Where(x => x.Attested is not null).Select(x => $"{x.Id}:{x.Attested!.At:yyyy-MM-dd}")),
                        string.Join(" ", cs.Where(x => x.Attested is not null).Select(x => $"{x.Id}:{x.Attested!.Due:yyyy-MM-dd}")),
                    };
                })]));
            Text("readiness.json", JsonSerializer.Serialize(readiness, Indented));
            Text("check-history.csv", Csv([["day", "check", "state", "detail"], .. history.Select(h => new object?[] { h.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), h.Check, h.State, h.Detail })]));
            Text("controls.yaml", ComplianceControls.Text);

            var governance = Governance.GetValueOrDefault(fw) ?? [];
            if (governance.Contains("risks"))
            {
                var risks = await services.GetRequiredService<Risks>().ListAsync(ct);
                Text("risks.csv", Csv([["title", "category", "level", "left", "owner", "treatment", "controls", "review due"],
                    .. risks.Select(r => new object?[] { r.Title, r.Category, r.Level, r.ResidualLevel, r.Owner, r.Treatment, string.Join(" ", r.Controls), U(r.ReviewDue) })]));
                files.Add(("risks.csv", "the risk register"));
            }
            if (governance.Contains("soa") && Soa.Frameworks.Contains(fw))
            {
                Text("statement-of-applicability.csv", Soa.Csv(await services.GetRequiredService<Soa>().GetAsync(fw, ct)));
                files.Add(("statement-of-applicability.csv", "the Statement of Applicability as it stands (its approved versions: Admin → Compliance)"));
            }
            if (governance.Contains("policies"))
            {
                var policies = await services.GetRequiredService<Policies>().ListAsync(ct);
                Text("policies.csv", Csv([["policy", "version", "approved by", "approved at", "review due", "read by", "must read"],
                    .. policies.Select(p => new object?[] { p.Title, p.Version, p.ApprovedBy, U(p.ApprovedAt), U(p.ReviewDue), p.Acknowledged, p.Readers })]));
                files.Add(("policies.csv", "the policies, their versions in force, reviews and acknowledgements"));
            }
            if (governance.Contains("audits"))
            {
                var audits = await services.GetRequiredService<Audits>().AllAsync(ct);
                Text("audits.csv", Csv([["title", "kind", "framework", "starts", "reported", "conclusion"], .. audits.Audits.Select(a => new object?[] { a.Title, a.Kind, a.Framework, U(a.Starts), U(a.ReportedAt), a.Conclusion })]));
                Text("findings.csv", Csv([["title", "kind", "clause", "status", "root cause", "corrective action", "verified by", "verification"],
                    .. audits.Findings.Select(f => new object?[] { f.Title, f.Kind, f.Clause, f.Status, f.RootCause, f.CorrectiveAction, f.VerifiedBy, f.Verification })]));
                files.Add(("audits.csv, findings.csv", "the audits and their findings"));
            }
            if (governance.Contains("suppliers"))
            {
                var suppliers = await services.GetRequiredService<Suppliers>().ListAsync(ct);
                Text("suppliers.csv", Csv([["name", "kind", "data", "location", "certifications", "agreements", "risk", "review due"],
                    .. suppliers.Select(s => new object?[] { s.Name, s.Kind, string.Join(" ", s.Data), s.Location, s.Certifications, s.Agreements, s.Risk, U(s.ReviewDue) })]));
                files.Add(("suppliers.csv", "the supplier register"));
            }
            if (governance.Contains("ai-systems"))
            {
                var systems = await services.GetRequiredService<AiSystems>().ListAsync(ct);
                Text("ai-systems.csv", Csv([["name", "kind", "class", "area", "role", "in use since", "impact assessed by", "oversight", "evaluation"],
                    .. systems.Select(s => new object?[] { s.Name, s.Kind, s.Class, s.HighRiskArea, s.Role, U(s.InUseSince), s.ImpactAssessedBy, s.Oversight, s.Evaluation })]));
                files.Add(("ai-systems.csv", "the register of AI systems"));
                if (fw == "euaiact")
                {
                    foreach (var s in systems.Where(s => s.Class == "high" && s.RetiredAt is null))
                    {
                        Text($"annex-iv/{Slug(s.Name)}.md", AnnexIv(s));
                    }
                    files.Add(("annex-iv/", "a draft of Annex IV's technical documentation for each high-risk system, its gaps marked"));
                }
            }
            if (fw == "commoncriteria")
            {
                Text("security-target.md", SecurityTarget(catalog, serving));
                files.Add(("security-target.md", "a Security Target drafted from Arena's controls, for the laboratory to complete"));
            }
            if (fw == "nistcsf")
            {
                Text("profile.csv", Csv([["subcategory", "outcome", "current (Arena's part)", "target (the company's)"],
                    .. catalog.Clauses.Where(c => c.Id.Contains('-')).Select(c => new object?[]
                    {
                        c.Id, c.Title, serving.TryGetValue(c.Id, out var cs) ? (cs.All(x => x.State is null or ComplianceChecks.Ok) ? "achieved by Arena's controls" : "partly") : "the company's own", "",
                    })]));
                files.Add(("profile.csv", "a current profile from Arena's controls, with the target for the company to fill in"));
            }
            Text("README.txt", $"""
                {catalog.Title}: what its auditor reads, from Argus Arena {AppInfo.Current.Version}, made {now:yyyy-MM-dd HH:mm} UTC by {by}.
                Readiness: {readiness.Score} of 100. A framework is met by the company that runs Arena: Arena's controls are its technical part.

                {string.Join("\n", files.Select(f => $"{f.Name,-34}{f.What}"))}

                """);
        }
        return buffer.ToArray();
    }

    private static Dictionary<string, string> Documents()
    {
        using var stream = typeof(Readiness).Assembly.GetManifestResourceStream("compliance-frameworks.yaml")!;
        using var reader = new StreamReader(stream);
        var file = new YamlDotNet.Serialization.DeserializerBuilder().IgnoreUnmatchedProperties().Build().Deserialize<DocumentsFile>(reader.ReadToEnd());
        return file.documents.ToDictionary(kv => kv.Key, kv => kv.Value.Count > 1 ? kv.Value[1] : kv.Key);
    }

    private sealed class DocumentsFile
    {
#pragma warning disable IDE1006, SA1300 // The YAML's own key.
        public Dictionary<string, List<string>> documents { get; set; } = [];
#pragma warning restore IDE1006, SA1300
    }

    private static string AnnexIv(AiSystemView s)
    {
        static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? "**To write.**" : v;
        return $"""
            # {s.Name}: technical documentation (AI Act, Annex IV), a draft

            Drafted from Admin → Compliance → AI systems; the parts marked "To write" are the provider's to complete.

            ## 1. General description
            - Intended purpose: {Or(s.Purpose)}
            - Intended use, and how not: {Or(s.IntendedUse)}
            - Whom it affects: {Or(s.Affected)}
            - Its Annex III area: {Or(s.HighRiskArea)}; the company's role: {s.Role}.

            ## 2. Its elements and its development
            **To write**: the models it uses, their versions and where they come from, how it was built and its data.

            ## 3. Monitoring, functioning and control
            - Human oversight (Article 14): {Or(s.Oversight)}
            - Records (Article 12): Arena keeps the audit log and each answer's trace.

            ## 4. Performance and its measurement
            {Or(s.Evaluation)}

            ## 5. Risk management (Article 9)
            The risk register (Admin → Compliance → Risks) holds its risks; its impact assessment, by {s.ImpactAssessedBy ?? "nobody yet"}:

            {Or(s.ImpactAssessment)}

            ## 6. Changes over its life
            **To write.**

            ## 7. Standards applied
            **To write** (ISO/IEC 42001 where the company is certified).

            ## 8. Post-market monitoring (Article 72)
            **To write.**
            """;
    }

    private static string SecurityTarget(FrameworkCatalog catalog, Dictionary<string, List<ControlView>> serving)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Argus Arena: Security Target, a draft (Common Criteria CC:2022)").AppendLine();
        sb.AppendLine("Drafted from Arena's controls (docs/compliance/product-evaluation.md says what it rests on). The laboratory completes it with the").AppendLine("evaluated release's configuration, the assurance package chosen and the vulnerability analysis.").AppendLine();
        sb.AppendLine("## TOE").AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Argus Arena {AppInfo.Current.Version}: the app, the web and the gateway as the release ships them; the hosts, the container engine, Postgres and the models are its environment.").AppendLine();
        sb.AppendLine("## Security functional requirements, and what meets each").AppendLine().AppendLine("| Family | Name | Arena's controls | Their state now |").AppendLine("|---|---|---|---|");
        foreach (var c in catalog.Clauses.Where(c => c.Id.StartsWith('F') && serving.ContainsKey(c.Id)))
        {
            var cs = serving[c.Id];
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {c.Id} | {c.Title} | {string.Join(", ", cs.Select(x => x.Id))} | {string.Join(", ", cs.Select(x => x.State ?? "watched outside"))} |");
        }
        sb.AppendLine().AppendLine("## Assurance: the evidence there is").AppendLine();
        foreach (var c in catalog.Clauses.Where(c => c.Id.StartsWith('A') && serving.ContainsKey(c.Id)))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {c.Id} ({c.Title}): {string.Join(", ", serving[c.Id].Select(x => $"{x.Id}, {x.Title}"))}");
        }
        return sb.ToString();
    }

    private static string Slug(string s) => string.Concat(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-') is { Length: > 0 } x ? x : "system";

    private static string Cell(object? v)
    {
        var s = v switch { null => "", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }
}

/// <summary>
/// Each day once, every compliance check's result kept as it was (for an audit over a period: SOC 2 Type II), on whichever
/// replica gets there first; kept six years.
/// </summary>
public sealed partial class ComplianceSnapshots(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ComplianceSnapshots> logger) : BackgroundService
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(6 * 365);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not as the app starts: its checks ask Prometheus and Loki, which may be starting too.
            await Task.Delay(TimeSpan.FromMinutes(10), clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Today's results, unless kept already; and those past six years taken out.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await db.ComplianceSnapshots.Where(s => s.Day < DateOnly.FromDateTime((now - KeptFor).UtcDateTime)).ExecuteDeleteAsync(ct);
        if (await db.ComplianceSnapshots.AnyAsync(s => s.Day == today, ct))
        {
            return 0;
        }
        var results = await scope.ServiceProvider.GetRequiredService<ComplianceChecks>().RunAsync(ct);
        db.ComplianceSnapshots.AddRange(results.Select(r => new ComplianceSnapshot { Day = today, Check = r.Id, State = r.State, Detail = r.Detail.Length > 4000 ? r.Detail[..4000] : r.Detail, At = now }));
        try
        {
            await db.SaveChangesAsync(ct);
            return results.Count;
        }
        catch (DbUpdateException)
        {
            // Another replica kept today's first.
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keeping the compliance checks' daily results failed: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);
}
