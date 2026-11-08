using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>What a review a task or a flow started says when looked at: whether it ended, failed, and what it found in words.</summary>
public sealed record ReviewSeen(bool Done, bool Failed, string? Text);

/// <summary>A review started for someone, and how to look at it: its page in the app, and a look at how it stands.</summary>
public sealed record StartedReview(string Path, Func<CancellationToken, Task<ReviewSeen>> Look);

/// <summary>
/// Starts the reviews scheduled tasks and Agent Studio flows ask for, as their owner (who needs review.run), and reads how
/// they end: a merge request, a whole repository, several (looked at across), one repository's security or several's.
/// No limit of a person's own reviews at once holds them back, as automatic reviews; a review of several waits for the
/// person's running one.
/// </summary>
public sealed class ReviewLauncher(AppDbContext db, ReviewEngine reviews, SecurityReview security, TimeProvider clock)
{
    public const string MergeRequest = "merge-request";
    public const string Repository = "review-repository";
    public const string Repositories = "review-repositories";
    public const string Security = "security";
    public const string SecuritySeveral = "security-several";
    public static readonly string[] Kinds = [MergeRequest, Repository, Repositories, Security, SecuritySeveral];

    private static readonly string[] Severities = ["critical", "high", "medium", "low"];

    /// <summary>How often a started review is looked at while waited for.</summary>
    public static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The fewest and most repositories each kind reads.</summary>
    public static (int Fewest, int Most) Bounds(string kind) => kind switch
    {
        Repositories => (2, CodeReviewBatches.MostRepositories),
        SecuritySeveral => (2, SecurityBatches.MostRepositories),
        _ => (1, 1),
    };

    /// <summary>The review started, or why not (null and the reason): a review of several of the person's is still running.</summary>
    public async Task<(StartedReview? Started, string? Refused)> StartAsync(AppUser owner, string kind, IReadOnlyList<string> repositories, string? at, bool cross, string? title, int? iid,
        string trigger, CancellationToken ct)
    {
        switch (kind)
        {
            case MergeRequest:
            {
                var r = new CodeReview { UserId = owner.Id, Project = repositories[0], Iid = iid, Title = $"!{iid}", Trigger = trigger };
                db.CodeReviews.Add(r);
                await db.SaveChangesAsync(ct);
                reviews.Start(r.Id);
                return (new StartedReview($"/code/reviews/{r.Id}", c => CodeAsync(r.Id, c)), null);
            }
            case Repository:
            {
                var r = new CodeReview { UserId = owner.Id, Project = repositories[0], Scope = CodeReview.Repository, ToRef = at, Title = $"{repositories[0]} @ {at ?? "the default branch"}", Trigger = trigger };
                db.CodeReviews.Add(r);
                await db.SaveChangesAsync(ct);
                reviews.Start(r.Id);
                return (new StartedReview($"/code/reviews/{r.Id}", c => CodeAsync(r.Id, c)), null);
            }
            case Repositories:
            {
                if (await db.CodeReviewBatches.AnyAsync(b => b.UserId == owner.Id && (b.Status == CodeReview.Queued || b.Status == CodeReview.Running), ct))
                {
                    return (null, "A review of several repositories of its owner's was still running.");
                }
                var b = new CodeReviewBatch { UserId = owner.Id, Ref = at, Cross = cross, Title = title };
                db.CodeReviewBatches.Add(b);
                var now = clock.GetUtcNow();
                db.CodeReviews.AddRange(repositories.Select((x, i) => new CodeReview
                {
                    UserId = owner.Id, Project = x, Scope = CodeReview.Repository, ToRef = at, BatchId = b.Id, Title = $"{x} @ {at ?? "the default branch"}", Trigger = trigger, CreatedAt = now.AddMilliseconds(i),
                }));
                await db.SaveChangesAsync(ct);
                reviews.StartBatch(b.Id);
                return (new StartedReview($"/code/reviews/batches/{b.Id}", c => CodeBatchAsync(b.Id, c)), null);
            }
            case Security:
            {
                var sc = new SecurityScan { UserId = owner.Id, Project = repositories[0], Ref = at };
                db.SecurityScans.Add(sc);
                await db.SaveChangesAsync(ct);
                security.Start(sc.Id);
                return (new StartedReview($"/security/scans/{sc.Id}", c => ScanAsync(sc.Id, c)), null);
            }
            default:
            {
                if (await db.SecurityBatches.AnyAsync(x => x.UserId == owner.Id && (x.Status == SecurityBatch.Queued || x.Status == SecurityBatch.Running), ct))
                {
                    return (null, "A security review of several repositories of its owner's was still running.");
                }
                var sb = new SecurityBatch { UserId = owner.Id, Ref = at, Cross = cross, Title = title };
                db.SecurityBatches.Add(sb);
                var now = clock.GetUtcNow();
                db.SecurityScans.AddRange(repositories.Select((x, i) => new SecurityScan { UserId = owner.Id, Project = x, Ref = at, BatchId = sb.Id, CreatedAt = now.AddMilliseconds(i) }));
                await db.SaveChangesAsync(ct);
                security.StartBatch(sb.Id);
                return (new StartedReview($"/security/batches/{sb.Id}", c => SecurityBatchAsync(sb.Id, c)), null);
            }
        }
    }

    /// <summary>Waits for a started review to end, at most until <paramref name="until"/>; how it stands then.</summary>
    public async Task<ReviewSeen> WaitAsync(StartedReview started, DateTimeOffset until, CancellationToken ct)
    {
        while (true)
        {
            var seen = await started.Look(ct);
            if (seen.Done || clock.GetUtcNow() >= until)
            {
                return seen;
            }
            await Task.Delay(Poll, clock, ct);
        }
    }

    private async Task<ReviewSeen> CodeAsync(Guid id, CancellationToken ct)
    {
        var v = await db.CodeReviews.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Status, x.Summary, x.Error, x.Verdict, x.Title }).SingleAsync(ct);
        var ended = v.Status is CodeReview.Done or CodeReview.Failed;
        return new ReviewSeen(ended, v.Status == CodeReview.Failed, v.Status == CodeReview.Failed ? v.Error : $"{v.Title}. Verdict: {ReviewEngine.VerdictText(v.Verdict)}\n\n{v.Summary}".Trim());
    }

    private async Task<ReviewSeen> CodeBatchAsync(Guid id, CancellationToken ct)
    {
        var v = await db.CodeReviewBatches.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Status, x.Report, x.Error }).SingleAsync(ct);
        var parts = await db.CodeReviews.AsNoTracking().Where(x => x.BatchId == id).OrderBy(x => x.CreatedAt).Select(x => new { x.Project, x.Verdict, x.Status }).ToListAsync(ct);
        var each = string.Join("\n", parts.Select(x => $"- {x.Project}: {(x.Status == CodeReview.Done ? ReviewEngine.VerdictText(x.Verdict) : x.Status)}"));
        var ended = v.Status is CodeReview.Done or CodeReview.Failed;
        return new ReviewSeen(ended, v.Status == CodeReview.Failed, v.Status == CodeReview.Failed ? v.Error : $"{each}\n\n{v.Report}".Trim());
    }

    private async Task<ReviewSeen> ScanAsync(Guid id, CancellationToken ct)
    {
        var v = await db.SecurityScans.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Status, x.Summary, x.Error }).SingleAsync(ct);
        return new ReviewSeen(v.Status is SecurityScan.Done or SecurityScan.Failed, v.Status == SecurityScan.Failed, v.Status == SecurityScan.Failed ? v.Error : v.Summary);
    }

    private async Task<ReviewSeen> SecurityBatchAsync(Guid id, CancellationToken ct)
    {
        var v = await db.SecurityBatches.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Status, x.Report, x.Error }).SingleAsync(ct);
        var ids = await db.SecurityScans.AsNoTracking().Where(x => x.BatchId == id).Select(x => x.Id).ToListAsync(ct);
        var open = await db.SecurityFindings.AsNoTracking().Where(f => ids.Contains(f.ScanId) && f.Verified != false && f.State == SecurityFinding.Open)
            .GroupBy(f => f.Severity).Select(g => new { g.Key, n = g.Count() }).ToListAsync(ct);
        var counts = string.Join(", ", Severities.Select(k => (k, n: open.FirstOrDefault(o => o.Key == k)?.n ?? 0)).Where(x => x.n > 0).Select(x => $"{x.n} {x.k}"));
        var ended = v.Status is SecurityBatch.Done or SecurityBatch.Failed;
        return new ReviewSeen(ended, v.Status == SecurityBatch.Failed, v.Status == SecurityBatch.Failed ? v.Error : $"Open: {(counts.Length > 0 ? counts : "nothing")}.\n\n{v.Report}".Trim());
    }
}
