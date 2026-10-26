using System.Globalization;
using Llm.Api.Access;
using Llm.Api.Identity;
using Llm.Core.Access;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Compliance;

/// <summary>Configuration section "Compliance": how often access is reviewed, and when an unused account counts as one.</summary>
public sealed class ComplianceOptions
{
    /// <summary>A review is started by itself when none was started for this many days; 0: only by hand.</summary>
    public int ReviewEveryDays { get; set; } = 90;

    /// <summary>A review is due this many days after it starts.</summary>
    public int ReviewDueDays { get; set; } = 14;

    /// <summary>An account nobody signed in to for this many days is in the review (and, with <see cref="DisableUnused"/>, disabled).</summary>
    public int UnusedDays { get; set; } = 90;

    /// <summary>Disable unused accounts by themselves, every day (the first admin never).</summary>
    public bool DisableUnused { get; set; }
}

public sealed class AccessReviewException(string message) : Exception(message);

/// <summary>
/// Access reviews: who holds a privileged role (directly or through a group, and each member of such a group) and which accounts
/// went unused, each for a reviewer to keep or revoke. Revoking takes the role away, removes the member, or disables the account,
/// at once and audited; what cannot be done here (a directory's or SCIM's group) says where it is done.
/// </summary>
public sealed class AccessReviews(AppDbContext db, AccessService access, PeopleService people, Audit audit, IOptionsMonitor<ComplianceOptions> options,
    IOptions<AuthOptions> auth, Models.KeyAccessWatcher keys, TimeProvider clock)
{
    /// <summary>Starts a review of everything privileged now, and of the accounts unused for long.</summary>
    public async Task<AccessReview> StartAsync(string by, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = clock.GetUtcNow();
        var review = new AccessReview { Title = $"Access review, {now:yyyy-MM-dd}", CreatedBy = by, CreatedAt = now, DueAt = now.AddDays(Math.Max(1, o.ReviewDueDays)) };
        db.AccessReviews.Add(review);
        var snapshot = await access.SnapshotAsync(ct);
        var adminKeys = Permissions.AdminKeys.ToHashSet(StringComparer.Ordinal);
        var privileged = snapshot.Roles.Where(r => r.Permissions.Any(adminKeys.Contains)).ToDictionary(r => r.Id);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct);
        var items = new List<AccessReviewItem>();
        // The first admin is always an Owner (the way back in): listed with what they hold, and an Owner whatever is decided.
        var first = people.FirstOrDefault(u => u.Source == UserSource.Local && string.Equals(u.UserName, auth.Value.AdminUserName, StringComparison.OrdinalIgnoreCase));
        const string Always = "; as the first admin (ADMIN_USERNAME) an Owner always, the way back in";
        foreach (var a in snapshot.Assignments.Where(a => privileged.ContainsKey(a.RoleId) && (a.ExpiresAt is null || a.ExpiresAt > now)))
        {
            var role = privileged[a.RoleId];
            var scope = a.Scope is null ? "" : ", scoped";
            var until = a.ExpiresAt is { } e ? $", until {e:yyyy-MM-dd}" : "";
            var isFirst = first is not null && a.SubjectKind == SubjectKind.Person && a.SubjectId == first.Id;
            items.Add(new AccessReviewItem
            {
                Kind = AccessReviewItem.Role, AssignmentId = a.Id, UserId = a.SubjectKind == SubjectKind.Person ? a.SubjectId : null,
                GroupId = a.SubjectKind == SubjectKind.Group ? a.SubjectId : null, Subject = snapshot.Describe(a.SubjectKind, a.SubjectId),
                What = $"{role.Name}, given {(a.SubjectKind == SubjectKind.Person ? "to them" : a.SubjectKind == SubjectKind.Group ? "to the group" : "to everyone")}{scope}{until}{(isFirst ? Always : "")}",
            });
            if (a.SubjectKind != SubjectKind.Group || a.SubjectId is not { } gid || !groups.TryGetValue(gid, out var group))
            {
                continue;
            }
            // Each member of a group that holds it is reviewed too: a group's membership is how most privilege drifts.
            var members = group.Directory is { } dn
                ? people.Where(u => AccessService.InDirectoryGroup(u.DirectoryGroups, dn)).ToList()
                : await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == gid).Join(db.Users.Where(u => !u.IsDisabled), m => m.UserId, u => u.Id, (_, u) => u).ToListAsync(ct);
            items.AddRange(members.Select(u => new AccessReviewItem
            {
                Kind = AccessReviewItem.Member, GroupId = gid, UserId = u.Id, Subject = u.UserName!,
                What = $"member of {group.Name}{(group.Directory is not null ? " (the directory's)" : group.Scim ? " (SCIM's)" : "")}, which holds {role.Name}",
            }));
        }
        if (first is not null && !items.Any(i => i.UserId == first.Id && i.Kind == AccessReviewItem.Role))
        {
            items.Add(new AccessReviewItem { Kind = AccessReviewItem.Role, UserId = first.Id, Subject = first.UserName!, What = "Owner" + Always });
        }
        var unusedSince = now.AddDays(-Math.Max(1, o.UnusedDays));
        foreach (var u in people.Where(u => (u.LastSignInAt ?? u.CreatedAt) < unusedSince))
        {
            items.Add(new AccessReviewItem
            {
                Kind = AccessReviewItem.Unused, UserId = u.Id, Subject = u.UserName!,
                What = u.LastSignInAt is { } at ? $"last signed in {at:yyyy-MM-dd}" : $"never signed in, made {u.CreatedAt:yyyy-MM-dd}",
            });
        }
        foreach (var i in items)
        {
            i.ReviewId = review.Id;
        }
        db.AccessReviewItems.AddRange(items);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("access_review.start", review.Title, detail: $"{items.Count} to review, due {review.DueAt:yyyy-MM-dd}, by {by}");
        return review;
    }

    /// <summary>Keeps or revokes one item; revoking takes effect at once. Refused once the review is closed.</summary>
    public async Task<AccessReviewItem> DecideAsync(Guid reviewId, Guid itemId, string decision, string? note, AppUser actor, CancellationToken ct)
    {
        if (decision is not (AccessReviewItem.Keep or AccessReviewItem.Revoke))
        {
            throw new AccessReviewException("Keep it or revoke it.");
        }
        var review = await db.AccessReviews.SingleOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new KeyNotFoundException();
        if (review.ClosedAt is not null)
        {
            throw new AccessReviewException("This review is closed: start a new one to change anything.");
        }
        var item = await db.AccessReviewItems.SingleOrDefaultAsync(i => i.Id == itemId && i.ReviewId == reviewId, ct) ?? throw new KeyNotFoundException();
        if (item.Decision == AccessReviewItem.Revoke && item.Outcome is not null && !item.Outcome.StartsWith("Not done", StringComparison.Ordinal))
        {
            throw new AccessReviewException("It was revoked already.");
        }
        item.Outcome = decision == AccessReviewItem.Revoke ? await RevokeAsync(item, actor, ct) : null;
        item.Decision = decision;
        item.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        item.DecidedBy = actor.UserName;
        item.DecidedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("access_review.decide", item.Subject, detail: $"{decision}: {item.What}{(item.Outcome is { } o ? $" ({o})" : "")}");
        return item;
    }

    private async Task<string> RevokeAsync(AccessReviewItem item, AppUser actor, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case AccessReviewItem.Role when item.AssignmentId is { } id:
                try
                {
                    var removed = await access.ChangeAsync(actor, async edit =>
                    {
                        if (await edit.Db.AccessAssignments.SingleOrDefaultAsync(a => a.Id == id, ct) is { } a)
                        {
                            edit.Remove(a);
                        }
                    }, "access.unassign", id.ToString(), ct);
                    return removed > 0 ? "The role was taken away." : "It was taken away already.";
                }
                catch (AccessRefusedException ex)
                {
                    return $"Not done: {ex.Message}";
                }
            case AccessReviewItem.Role:
                return "Not done: the first admin is always an Owner. Change ADMIN_USERNAME's password and keep it as a break-glass secret instead.";
            case AccessReviewItem.Member when item.GroupId is { } gid && item.UserId is { } uid:
                var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gid, ct);
                if (group?.Directory is not null)
                {
                    return $"Not done here: remove them from {group.Directory} in the company's directory.";
                }
                if (group?.Scim == true)
                {
                    return $"Not done here: remove them from {group.Name} at the identity provider (SCIM).";
                }
                if (await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == gid && m.UserId == uid, ct) is not { } member)
                {
                    return "They were not in it any more.";
                }
                db.GroupMembers.Remove(member);
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync("group.remove_member", item.Subject, detail: $"{group?.Name}, by an access review");
                keys.Wake();
                return $"Removed from {group?.Name}.";
            case AccessReviewItem.Unused when item.UserId is { } who:
                if (await db.Users.SingleOrDefaultAsync(u => u.Id == who, ct) is not { } person)
                {
                    return "The account is gone.";
                }
                try
                {
                    await people.SetDisabledAsync(actor, person, disabled: true, reason: "access review");
                    return "The account was disabled.";
                }
                catch (PeopleException ex)
                {
                    return $"Not done: {ex.Message}";
                }
            default:
                return "Not done: nothing to revoke.";
        }
    }

    /// <summary>Closes a review once every item is decided; kept for good.</summary>
    public async Task<AccessReview> CloseAsync(Guid reviewId, AppUser actor, CancellationToken ct)
    {
        var review = await db.AccessReviews.SingleOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw new KeyNotFoundException();
        if (review.ClosedAt is not null)
        {
            return review;
        }
        var open = await db.AccessReviewItems.CountAsync(i => i.ReviewId == reviewId && i.Decision == null, ct);
        if (open > 0)
        {
            throw new AccessReviewException($"{open} {(open == 1 ? "is" : "are")} not decided yet: keep or revoke each.");
        }
        review.ClosedAt = clock.GetUtcNow();
        review.ClosedBy = actor.UserName;
        await db.SaveChangesAsync(ct);
        var counts = await db.AccessReviewItems.Where(i => i.ReviewId == reviewId).GroupBy(i => i.Decision).Select(g => new { g.Key, n = g.Count() }).ToListAsync(ct);
        await audit.WriteAsync("access_review.close", review.Title,
            detail: string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.n} {(c.Key == AccessReviewItem.Keep ? "kept" : "revoked")}")));
        return review;
    }

    /// <summary>The accounts unused for longer than allowed, disabled (when the company says so); the first admin never.</summary>
    public async Task<int> DisableUnusedAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.DisableUnused)
        {
            return 0;
        }
        var since = clock.GetUtcNow().AddDays(-Math.Max(1, o.UnusedDays));
        var unused = await db.Users.Where(u => !u.IsDisabled && (u.LastSignInAt ?? u.CreatedAt) < since).ToListAsync(ct);
        var done = 0;
        foreach (var u in unused.Where(u => !(u.Source == UserSource.Local && string.Equals(u.UserName, auth.Value.AdminUserName, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                await people.SetDisabledAsync(PeopleService.DirectorySync, u, disabled: true,
                    reason: $"unused for {o.UnusedDays.ToString(CultureInfo.InvariantCulture)} days");
                done++;
            }
            catch (PeopleException)
            {
                // The last admin stays enabled.
            }
        }
        return done;
    }
}

/// <summary>Every day: starts a review when one is due (and tells the admins), and disables unused accounts when the company says so.</summary>
public sealed partial class AccessReviewSchedule(IServiceScopeFactory scopes, IOptionsMonitor<ComplianceOptions> options, TimeProvider clock,
    ILogger<AccessReviewSchedule> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                await Task.Delay(TimeSpan.FromHours(6), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reviews = scope.ServiceProvider.GetRequiredService<AccessReviews>();
        var every = options.CurrentValue.ReviewEveryDays;
        var newest = await db.AccessReviews.MaxAsync(r => (DateTimeOffset?)r.CreatedAt, ct);
        if (every > 0 && (newest is null || clock.GetUtcNow() - newest > TimeSpan.FromDays(every)))
        {
            var review = await reviews.StartAsync("the schedule", ct);
            await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().ToAdminsAsync(new Notifications.News("compliance",
                "An access review is due", $"Who holds privileged access, and the accounts unused for long: keep or revoke each by {review.DueAt:yyyy-MM-dd}.",
                $"/admin/compliance/reviews/{review.Id}", $"access-review:{review.Id}"), ct);
        }
        if (await reviews.DisableUnusedAsync(ct) is > 0 and var n)
        {
            LogDisabled(logger, n);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} accounts unused for long were disabled")]
    private static partial void LogDisabled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The access review schedule failed: {Reason}; trying again later")]
    private static partial void LogFailed(ILogger logger, string reason);
}
