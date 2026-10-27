using System.Security.Claims;
using System.Text.Json.Nodes;
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

public sealed record NewSecurityScan(string Project, string? Ref = null);

/// <summary>A finding's state: open, fixed, accepted (with why and until when) or wrong (with why).</summary>
public sealed record SecurityFindingChange(string State, string? Why = null, DateTimeOffset? Until = null);

/// <summary>
/// Security reviews of a repository at a ref (Code → Security): started by a person who may run reviews, read as them
/// through Argus (read-only), seen by them and by reviews admins; each finding fixed, accepted as a risk (why and until
/// when) or marked wrong.
/// </summary>
public static class SecurityEndpoints
{
    public static void MapSecurityScans(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code/security").RequireFeature("code-studio").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapPost("", StartAsync).RequirePermission(Core.Access.Permissions.ReviewRun);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapGet("/{id:guid}/sbom", SbomAsync);
        g.MapPut("/{id:guid}/findings/{findingId:guid}", DecideAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object Brief(SecurityScan s) => new
    {
        s.Id, s.Project, s.Ref, s.Status, s.Step, s.Candidates, s.Read, s.Dropped, s.Dependencies, s.AdvisoriesAsOf, s.CreatedAt, s.FinishedAt, s.Error,
        sbom = s.Sbom is not null,
    };

    /// <summary>The person's security reviews (a reviews admin sees everyone's), newest first, with their open findings by severity.</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var all = await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct);
        var list = await db.SecurityScans.AsNoTracking().Where(s => all || s.UserId == me.Id).OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(ct);
        var ids = list.Select(s => s.Id).ToList();
        var open = await db.SecurityFindings.AsNoTracking().Where(f => ids.Contains(f.ScanId) && f.Verified != false && f.State == SecurityFinding.Open)
            .GroupBy(f => new { f.ScanId, f.Severity }).Select(x => new { x.Key.ScanId, x.Key.Severity, n = x.Count() }).ToListAsync(ct);
        return Results.Ok(list.Select(s => new
        {
            scan = Brief(s), mine = s.UserId == me.Id,
            open = open.Where(o => o.ScanId == s.Id).ToDictionary(o => o.Severity, o => o.n),
        }));
    }

    private static async Task<SecurityScan?> SeenAsync(AppDbContext db, AccessService access, AppUser me, Guid id, CancellationToken ct)
    {
        var s = await db.SecurityScans.Include(x => x.Findings).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return null;
        }
        return s.UserId == me.Id || await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct) ? s : null;
    }

    private static async Task<IResult> StartAsync(NewSecurityScan body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, SecurityReview engine,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!argus.Enabled)
        {
            return AuthEndpoints.Problem(503, "argus", "Argus is not set up here: it reads the code for security reviews.");
        }
        var project = (body.Project ?? "").Trim().Trim('/');
        if (project.Length is 0 or > 500)
        {
            return AuthEndpoints.Problem(400, "project", "Name the project by its path, as group/project.");
        }
        var reference = string.IsNullOrWhiteSpace(body.Ref) ? null : body.Ref.Trim();
        if (reference is { Length: > 300 })
        {
            return AuthEndpoints.Problem(400, "ref", "300 characters at most.");
        }
        if (await db.SecurityScans.CountAsync(s => s.UserId == me.Id && (s.Status == SecurityScan.Queued || s.Status == SecurityScan.Running), ct) >= 2)
        {
            return AuthEndpoints.Problem(429, "busy", "Two of your security reviews are running: wait for one to end.");
        }
        var scan = new SecurityScan { UserId = me.Id, Project = project, Ref = reference };
        db.SecurityScans.Add(scan);
        await db.SaveChangesAsync(ct);
        engine.Start(scan.Id);
        await audit.WriteAsync("security.review.start", project + (reference is null ? "" : $"@{reference}"));
        return Results.Accepted($"/api/code/security/{scan.Id}", new { scan.Id });
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct) is not { } s)
        {
            return Results.NotFound();
        }
        var by = await db.Users.AsNoTracking().Where(u => u.Id == s.UserId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        return Results.Ok(new
        {
            scan = Brief(s), s.Summary, s.Model, s.PromptTokens, s.CompletionTokens, by, mine = s.UserId == me.Id,
            findings = s.Findings.OrderBy(f => f.Verified == false).ThenByDescending(f => f.Exploited).ThenBy(f => f.Severity switch { "critical" => 0, "high" => 1, "medium" => 2, _ => 3 }).ThenBy(f => f.Path).ThenBy(f => f.Line)
                .Select(f => new
                {
                    f.Id, f.Kind, f.Exploited, f.Path, f.Line, f.SinkCode, f.Cwe, f.CweTitle, f.Severity, f.Title, f.Source, f.Flow, f.Exploit, f.Fix,
                    pages = JsonNode.Parse(f.Pages), f.Verified, f.VerifyNote, f.State, f.DecidedAt, f.Why, f.Until,
                }),
        });
    }

    /// <summary>The repository's packages at the commit reviewed, as a CycloneDX bill of materials.</summary>
    private static async Task<IResult> SbomAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await SeenAsync(db, access, me, id, ct) is not { Sbom: { } sbom } s)
        {
            return Results.NotFound();
        }
        var name = $"{s.Project.Replace('/', '-')}{(s.Ref is null ? "" : "-" + s.Ref.Replace('/', '-'))}.cdx.json";
        return Results.File(System.Text.Encoding.UTF8.GetBytes(sbom), "application/vnd.cyclonedx+json", name);
    }

    /// <summary>A finding fixed, accepted as a risk (why, and until when it is looked at again), marked wrong (why), or opened again.</summary>
    private static async Task<IResult> DecideAsync(Guid id, Guid findingId, SecurityFindingChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (body.State is not (SecurityFinding.Open or SecurityFinding.Fixed or SecurityFinding.Accepted or SecurityFinding.Wrong))
        {
            return AuthEndpoints.Problem(400, "state", "A finding is open, fixed, accepted or wrong.");
        }
        var why = string.IsNullOrWhiteSpace(body.Why) ? null : body.Why.Trim();
        if (body.State is SecurityFinding.Accepted or SecurityFinding.Wrong && why is null)
        {
            return AuthEndpoints.Problem(400, "why", body.State == SecurityFinding.Accepted ? "Say why the risk is accepted." : "Say why it is not a weakness.");
        }
        if (why is { Length: > 2000 })
        {
            return AuthEndpoints.Problem(400, "why", "2000 characters at most.");
        }
        if (body.State == SecurityFinding.Accepted && (body.Until is null || body.Until <= DateTimeOffset.UtcNow || body.Until > DateTimeOffset.UtcNow.AddYears(1)))
        {
            return AuthEndpoints.Problem(400, "until", "An accepted risk is looked at again: give a date within the year.");
        }
        if (await SeenAsync(db, access, me, id, ct) is not { } s || s.Findings.FirstOrDefault(f => f.Id == findingId) is not { } finding)
        {
            return Results.NotFound();
        }
        var open = body.State == SecurityFinding.Open;
        (finding.State, finding.Why, finding.Until) = (body.State, open ? null : why, body.State == SecurityFinding.Accepted ? body.Until : null);
        finding.DecidedById = open ? null : me.Id;
        finding.DecidedAt = open ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync($"security.finding.{body.State}", $"{s.Project}:{finding.Path}:{finding.Line}", detail: $"{finding.Cwe} {why}".Trim());
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.SecurityScans.Where(s => s.Id == id && s.UserId == me.Id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
    }
}
