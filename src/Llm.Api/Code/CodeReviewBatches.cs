using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Branding;
namespace Llm.Api.Code;

/// <summary>A code review of several whole repositories: their paths (2 to <see cref="CodeReviewBatches.MostRepositories"/>), one ref for all or none, and whether to look across them.</summary>
public sealed record NewReviewBatch(string[] Projects, string? Ref = null, bool Cross = true, string? Title = null);

/// <summary>
/// Code reviews of several whole repositories at once (Code Studio → Review → Several repositories): each repository
/// reviewed whole as its own review, as the person through code-intel, then looked at across them; seen by them and by
/// reviews admins.
/// </summary>
public static class CodeReviewBatches
{
    public const int MostRepositories = 10;

    public static void MapCodeReviewBatches(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code/reviews/batches").RequireFeature("code-studio").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapPost("", StartAsync).RequirePermission(Core.Access.Permissions.ReviewRun);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object Brief(CodeReviewBatch b) => new { b.Id, b.Title, b.Ref, b.Cross, b.Status, b.Step, b.Model, b.PromptTokens, b.CompletionTokens, b.Error, b.CreatedAt, b.FinishedAt };

    private static int Rank(string severity) => severity switch { "critical" => 0, "major" => 1, "minor" => 2, _ => 3 };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var all = await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct);
        var list = await db.CodeReviewBatches.AsNoTracking().Where(b => all || b.UserId == me.Id).OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
        var ids = list.Select(b => b.Id).ToList();
        var reviews = await db.CodeReviews.AsNoTracking().Where(r => r.BatchId != null && ids.Contains(r.BatchId.Value))
            .Select(r => new { r.BatchId, r.Project, r.Status, r.Verdict }).ToListAsync(ct);
        return Results.Ok(list.Select(b =>
        {
            var theirs = reviews.Where(r => r.BatchId == b.Id).ToList();
            return new
            {
                batch = Brief(b), mine = b.UserId == me.Id, repositories = theirs.Select(r => r.Project).Order(StringComparer.Ordinal), done = theirs.Count(r => r.Status == CodeReview.Done),
                changes = theirs.Count(r => r.Verdict == "changes"),
            };
        }));
    }

    private static async Task<IResult> StartAsync(NewReviewBatch body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CodeIntelMcp codeIntel, ReviewEngine engine,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!codeIntel.Enabled)
        {
            return AuthEndpoints.Problem(503, "code-intel", $"{Brand.CodeIntel} is not set up here: reviews read the code through it.");
        }
        var projects = (body.Projects ?? []).Select(x => (x ?? "").Trim().Trim('/')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (projects.Count < 2 || projects.Count > MostRepositories || projects.Any(x => x.Length > 500))
        {
            return AuthEndpoints.Problem(400, "projects", $"Name 2 to {MostRepositories} repositories, each by its path (group/project).");
        }
        var reference = string.IsNullOrWhiteSpace(body.Ref) ? null : body.Ref.Trim();
        if (reference is { Length: > 300 } || body.Title is { Length: > 200 })
        {
            return AuthEndpoints.Problem(400, "ref", "A ref has at most 300 characters, a title 200.");
        }
        if (await db.CodeReviewBatches.AnyAsync(b => b.UserId == me.Id && (b.Status == CodeReview.Queued || b.Status == CodeReview.Running), ct))
        {
            return AuthEndpoints.Problem(429, "busy", "A review of several repositories of yours is running: wait for it to end.");
        }
        var batch = new CodeReviewBatch { UserId = me.Id, Ref = reference, Cross = body.Cross, Title = string.IsNullOrWhiteSpace(body.Title) ? null : body.Title.Trim() };
        db.CodeReviewBatches.Add(batch);
        var at = DateTimeOffset.UtcNow;
        db.CodeReviews.AddRange(projects.Select((x, i) => new CodeReview
        {
            UserId = me.Id, Project = x, Scope = CodeReview.Repository, ToRef = reference, BatchId = batch.Id, Title = $"{x} @ {reference ?? "the default branch"}", CreatedAt = at.AddMilliseconds(i),
        }));
        await db.SaveChangesAsync(ct);
        engine.StartBatch(batch.Id);
        await audit.WriteAsync("review.start", $"{projects.Count} whole repositories: {string.Join(", ", projects)}" + (reference is null ? "" : $"@{reference}"));
        return Results.Accepted($"/api/code/reviews/batches/{batch.Id}", new { batch.Id });
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeReviewBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b
            || (b.UserId != me.Id && !await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct)))
        {
            return Results.NotFound();
        }
        var reviews = await db.CodeReviews.AsNoTracking().Include(r => r.Findings).Where(r => r.BatchId == id).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var by = await db.Users.AsNoTracking().Where(u => u.Id == b.UserId).Select(u => u.DisplayName == "" ? u.UserName : u.DisplayName).SingleOrDefaultAsync(ct);
        return Results.Ok(new
        {
            batch = Brief(b), b.Report, b.Links, by, mine = b.UserId == me.Id,
            reviews = reviews.Select(r => new
            {
                r.Id, r.Project, r.ToRef, r.HeadSha, r.Status, r.Step, r.Error, r.Verdict, r.Risk, r.Files,
                open = r.Findings.Where(f => f.Verified != false && f.State == ReviewFinding.Open).GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Count()),
            }),
            findings = reviews.SelectMany(r => r.Findings.Where(f => f.Verified != false).Select(f => new { reviewId = r.Id, r.Project, f.Id, f.Path, f.Line, f.Severity, f.Category, f.Title, f.State }))
                .OrderBy(f => Rank(f.Severity)).ThenBy(f => f.Project, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal).Take(2000),
        });
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeReviewBatches.SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b || (b.UserId != me.Id && !await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct)))
        {
            return Results.NotFound();
        }
        if (b.Status is CodeReview.Queued or CodeReview.Running)
        {
            return AuthEndpoints.Problem(409, "running", "It is running: delete it once it ends.");
        }
        db.CodeReviewBatches.Remove(b);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("review.delete", $"several repositories {id}");
        return Results.NoContent();
    }
}
