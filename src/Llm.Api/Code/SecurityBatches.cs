using System.Globalization;
using System.Security.Claims;
using System.Text;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>A review of several repositories: their paths (2 to <see cref="SecurityBatches.MostRepositories"/>), one ref for all or none, and whether to look across them.</summary>
public sealed record NewSecurityBatch(string[] Projects, string? Ref = null, bool Cross = true, string? Title = null);

/// <summary>A weakness (CWE) found in more than one of the repositories: a cause they share.</summary>
public sealed record SharedWeakness(string Cwe, string Title, string Worst, int Findings, IReadOnlyList<string> Repositories);

/// <summary>A vulnerable package in more than one of the repositories: one upgrade policy for all of them.</summary>
public sealed record SharedPackage(string Package, string Advisories, string Worst, IReadOnlyList<string> Repositories);

/// <summary>What the repositories' reviews have in common, from their findings alone.</summary>
public sealed record Across(IReadOnlyList<SharedWeakness> Weaknesses, IReadOnlyList<SharedPackage> Packages, IReadOnlyList<string> WithSecrets);

/// <summary>
/// The look across several repositories' security reviews: the weaknesses and the vulnerable packages they share (from
/// their findings), and the model's report of what one's weakness means for the others and what to fix first.
/// </summary>
public static class SecurityCross
{
    /// <summary>A finding counted across: not refuted by its check, not marked wrong.</summary>
    public static bool Counts(SecurityFinding f) => f.Verified != false && f.State != SecurityFinding.Wrong;

    public static int Rank(string severity) => severity switch { "critical" => 0, "high" => 1, "medium" => 2, _ => 3 };

    private static string Name(SecurityScan s) => s.Ref is { Length: > 0 } r ? $"{s.Project}@{r}" : s.Project;

    public static Across Of(IReadOnlyList<SecurityScan> scans)
    {
        var all = scans.SelectMany(s => s.Findings.Where(Counts).Select(f => (Scan: s, Finding: f))).ToList();
        var weaknesses = all.Where(x => x.Finding.Kind != SecurityFinding.Dependency && x.Finding.Cwe.Length > 0)
            .GroupBy(x => x.Finding.Cwe)
            .Select(g => new SharedWeakness(g.Key, g.Select(x => x.Finding.CweTitle).FirstOrDefault(t => t.Length > 0) ?? g.First().Finding.Title,
                g.Select(x => x.Finding.Severity).MinBy(Rank)!, g.Count(), [.. g.Select(x => Name(x.Scan)).Distinct().Order(StringComparer.Ordinal)]))
            .Where(w => w.Repositories.Count >= 2).OrderBy(w => Rank(w.Worst)).ThenByDescending(w => w.Repositories.Count).ThenByDescending(w => w.Findings).ToList();
        var packages = all.Where(x => x.Finding.Kind == SecurityFinding.Dependency)
            // A package finding's line is "Package 1.2.3 (Ecosystem)"; its source, the advisory and its aliases.
            .GroupBy(x => x.Finding.SinkCode.Split(' ')[0])
            .Select(g => new SharedPackage(g.Key, string.Join(", ", g.Select(x => (x.Finding.Source ?? "").Split(',')[0].Trim()).Where(a => a.Length > 0).Distinct().Order(StringComparer.Ordinal).Take(6)),
                g.Select(x => x.Finding.Severity).MinBy(Rank)!, [.. g.Select(x => Name(x.Scan)).Distinct().Order(StringComparer.Ordinal)]))
            .Where(p => p.Repositories.Count >= 2).OrderBy(p => Rank(p.Worst)).ThenByDescending(p => p.Repositories.Count).ToList();
        var secrets = all.Where(x => x.Finding.Kind == SecurityFinding.Secret).Select(x => Name(x.Scan)).Distinct().Order(StringComparer.Ordinal).ToList();
        return new Across(weaknesses, packages, secrets);
    }

    public const string Prompt =
        """
        You are a security reviewer looking across several repositories of one organisation, from what each one's security review found. Write in Markdown, plainly, with these sections:
        ## Across the repositories
        Two to four sentences: how exposed they are together, and the worst of it.
        ## Paths across repositories
        Where a weakness in one repository could reach, or be reached through, another (a service that trusts another's input, a shared secret or package, a client of a vulnerable endpoint). Only what the findings support: name each repository, file and line. If nothing crosses, say so in one sentence.
        ## Shared causes
        The weaknesses and packages several repositories share, and the one cause behind each (a missing library, a pattern copied around).
        ## Fix first
        A numbered list, at most ten: the repository, the finding (file:line or package), and why it comes first.
        ## To standardise
        At most five changes for every repository at once (a shared library, a check in CI, a policy).
        Never invent a finding, a file or a line that is not in what you are given.
        """;

    /// <summary>What the model is given: each repository's summary and findings (the worst first, at most 15 each), and what they share.</summary>
    public static string Question(IReadOnlyList<SecurityScan> scans, Across across)
    {
        var sb = new StringBuilder();
        foreach (var s in scans.OrderBy(s => s.Project, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"# {Name(s)}\n");
            if (s.Summary is { Length: > 0 } summary)
            {
                sb.Append(summary.Length > 800 ? summary[..800] : summary).Append('\n');
            }
            var findings = s.Findings.Where(Counts).OrderBy(f => Rank(f.Severity)).ThenByDescending(f => f.Exploited).Take(15).ToList();
            if (findings.Count == 0)
            {
                sb.Append("No findings.\n");
            }
            foreach (var f in findings)
            {
                var where = f.Kind == SecurityFinding.Dependency ? $"{f.SinkCode}, {f.Source}" : $"{f.Path}:{f.Line}";
                sb.Append(CultureInfo.InvariantCulture, $"- [{f.Severity}] {f.Cwe} {f.Title} — {where}");
                if (f.Source is { Length: > 0 } source && f.Kind == SecurityFinding.Code)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" (from {Cut(source, 120)})");
                }
                if (f.Flow.Length > 0)
                {
                    sb.Append(": ").Append(Cut(f.Flow, 200));
                }
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        if (across.Weaknesses.Count > 0)
        {
            sb.Append("# Weaknesses in more than one repository\n");
            foreach (var w in across.Weaknesses)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- {w.Cwe} {w.Title}: {w.Findings} findings, worst {w.Worst}, in {string.Join(", ", w.Repositories)}\n");
            }
        }
        if (across.Packages.Count > 0)
        {
            sb.Append("# Vulnerable packages in more than one repository\n");
            foreach (var p in across.Packages)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- {p.Package} ({p.Advisories}), worst {p.Worst}, in {string.Join(", ", p.Repositories)}\n");
            }
        }
        if (across.WithSecrets.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"# Secrets in the code of {string.Join(", ", across.WithSecrets)}\n");
        }
        return sb.ToString();
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Security reviews of several repositories at once (Security review → Several repositories): started by a person who may
/// run reviews, each repository read as them through Argus; seen by them and by reviews admins.
/// </summary>
public static class SecurityBatches
{
    public const int MostRepositories = 20;

    public static void MapSecurityBatches(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code/security/batches").RequireFeature("code-studio").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapPost("", StartAsync).RequirePermission(Core.Access.Permissions.ReviewRun);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapGet("/{id:guid}/findings.csv", CsvAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object Brief(SecurityBatch b) => new { b.Id, b.Title, b.Ref, b.Cross, b.Status, b.Step, b.Model, b.PromptTokens, b.CompletionTokens, b.Error, b.CreatedAt, b.FinishedAt };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var all = await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct);
        var list = await db.SecurityBatches.AsNoTracking().Where(b => all || b.UserId == me.Id).OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
        var ids = list.Select(b => b.Id).ToList();
        var scans = await db.SecurityScans.AsNoTracking().Where(s => s.BatchId != null && ids.Contains(s.BatchId.Value))
            .Select(s => new { s.BatchId, s.Id, s.Project, s.Status }).ToListAsync(ct);
        var scanIds = scans.Select(s => s.Id).ToList();
        var open = await db.SecurityFindings.AsNoTracking().Where(f => scanIds.Contains(f.ScanId) && f.Verified != false && f.State == SecurityFinding.Open)
            .GroupBy(f => new { f.ScanId, f.Severity }).Select(x => new { x.Key.ScanId, x.Key.Severity, n = x.Count() }).ToListAsync(ct);
        return Results.Ok(list.Select(b =>
        {
            var mine = scans.Where(s => s.BatchId == b.Id).ToList();
            var theirs = mine.Select(s => s.Id).ToHashSet();
            return new
            {
                batch = Brief(b), mine = b.UserId == me.Id, repositories = mine.Select(s => s.Project).Order(StringComparer.Ordinal), done = mine.Count(s => s.Status == SecurityScan.Done),
                open = open.Where(o => theirs.Contains(o.ScanId)).GroupBy(o => o.Severity).ToDictionary(g => g.Key, g => g.Sum(x => x.n)),
            };
        }));
    }

    private static async Task<IResult> StartAsync(NewSecurityBatch body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, SecurityReview engine,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!argus.Enabled)
        {
            return AuthEndpoints.Problem(503, "argus", "Argus is not set up here: it reads the code for security reviews.");
        }
        var projects = (body.Projects ?? []).Select(x => (x ?? "").Trim().Trim('/')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (projects.Count < 2 || projects.Count > MostRepositories)
        {
            return AuthEndpoints.Problem(400, "projects", $"Name 2 to {MostRepositories} repositories, each by its path (group/project).");
        }
        if (projects.Any(x => x.Length > 500))
        {
            return AuthEndpoints.Problem(400, "projects", "A repository's path has at most 500 characters.");
        }
        var reference = string.IsNullOrWhiteSpace(body.Ref) ? null : body.Ref.Trim();
        if (reference is { Length: > 300 } || body.Title is { Length: > 200 })
        {
            return AuthEndpoints.Problem(400, "ref", "A ref has at most 300 characters, a title 200.");
        }
        if (await db.SecurityBatches.AnyAsync(b => b.UserId == me.Id && (b.Status == SecurityBatch.Queued || b.Status == SecurityBatch.Running), ct))
        {
            return AuthEndpoints.Problem(429, "busy", "A review of several repositories of yours is running: wait for it to end.");
        }
        var batch = new SecurityBatch { UserId = me.Id, Ref = reference, Cross = body.Cross, Title = string.IsNullOrWhiteSpace(body.Title) ? null : body.Title.Trim() };
        db.SecurityBatches.Add(batch);
        var at = DateTimeOffset.UtcNow;
        // In the order asked: the batch reviews them one after the other.
        db.SecurityScans.AddRange(projects.Select((x, i) => new SecurityScan { UserId = me.Id, Project = x, Ref = reference, BatchId = batch.Id, CreatedAt = at.AddMilliseconds(i) }));
        await db.SaveChangesAsync(ct);
        engine.StartBatch(batch.Id);
        await audit.WriteAsync("security.review.start", $"{projects.Count} repositories: {string.Join(", ", projects)}" + (reference is null ? "" : $"@{reference}"));
        return Results.Accepted($"/api/code/security/batches/{batch.Id}", new { batch.Id });
    }

    private static async Task<(SecurityBatch Batch, List<SecurityScan> Scans)?> SeenAsync(AppDbContext db, AccessService access, AppUser me, Guid id, CancellationToken ct)
    {
        if (await db.SecurityBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b)
        {
            return null;
        }
        if (b.UserId != me.Id && !await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct))
        {
            return null;
        }
        var scans = await db.SecurityScans.AsNoTracking().Include(s => s.Findings).Where(s => s.BatchId == id).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return (b, scans);
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct) is not var (b, scans))
        {
            return Results.NotFound();
        }
        var done = scans.Where(s => s.Status == SecurityScan.Done).ToList();
        var across = SecurityCross.Of(done);
        var by = await db.Users.AsNoTracking().Where(u => u.Id == b.UserId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        return Results.Ok(new
        {
            batch = Brief(b), b.Report, by, mine = b.UserId == me.Id,
            scans = scans.Select(s => new
            {
                s.Id, s.Project, s.Ref, s.Commit, s.Status, s.Step, s.Error, s.CreatedAt, s.FinishedAt,
                open = s.Findings.Where(f => f.Verified != false && f.State == SecurityFinding.Open).GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Count()),
            }),
            across.Weaknesses, across.Packages, across.WithSecrets,
            findings = done.SelectMany(s => s.Findings.Where(SecurityCross.Counts).Select(f => new
            {
                scanId = s.Id, s.Project, f.Id, f.Kind, f.Path, f.Line, f.Cwe, f.CweTitle, f.Severity, f.Title, f.Exploited, f.State, f.FoundBy, f.Source,
            })).OrderBy(f => SecurityCross.Rank(f.Severity)).ThenByDescending(f => f.Exploited).ThenBy(f => f.Project, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal).Take(2000),
        });
    }

    /// <summary>Every repository's findings in one CSV, the worst first.</summary>
    private static async Task<IResult> CsvAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct) is not var (_, scans))
        {
            return Results.NotFound();
        }
        var csv = new StringBuilder("repository,ref,severity,kind,cwe,title,path,line,state,exploited,found_by\n");
        foreach (var (s, f) in scans.SelectMany(s => s.Findings.Where(SecurityCross.Counts).Select(f => (s, f))).OrderBy(x => SecurityCross.Rank(x.f.Severity)).ThenBy(x => x.s.Project, StringComparer.Ordinal))
        {
            csv.AppendJoin(',', new[]
            {
                Operations.OperationsEndpoints.Csv(s.Project), Operations.OperationsEndpoints.Csv(s.Ref), f.Severity, f.Kind, f.Cwe, Operations.OperationsEndpoints.Csv(f.Title),
                Operations.OperationsEndpoints.Csv(f.Kind == SecurityFinding.Dependency ? f.SinkCode : f.Path), f.Line.ToString(CultureInfo.InvariantCulture), f.State,
                f.Exploited ? "yes" : "no", Operations.OperationsEndpoints.Csv(string.Join(" ", f.FoundBy.Length == 0 ? [SecurityFinding.Model] : f.FoundBy)),
            }).Append('\n');
        }
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"security-review-{id.ToString()[..8]}.csv");
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.SecurityBatches.SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b || (b.UserId != me.Id && !await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct)))
        {
            return Results.NotFound();
        }
        if (b.Status is SecurityBatch.Queued or SecurityBatch.Running)
        {
            return AuthEndpoints.Problem(409, "running", "It is running: delete it once it ends.");
        }
        // Its repositories' reviews go with it (the foreign key cascades).
        db.SecurityBatches.Remove(b);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("security.review.delete", $"several repositories {id}");
        return Results.NoContent();
    }
}
