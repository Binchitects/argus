using Llm.Api.Access;
using Llm.Api.Operations;
using Llm.Core.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>
/// Admin → Security: every repository's latest security review at a glance (what is open by severity, the known-exploited,
/// what is past its time to fix, what is new this week and what was fixed), the risks accepted (by whom, why and until
/// when), and how old the advisories are. And the alerts: a new critical or known-exploited finding reaches the security
/// team (whoever may manage every review) as well as the review's person.
/// </summary>
public static class SecurityOverview
{
    /// <summary>"This week": the last seven days.</summary>
    public const int WeekDays = 7;
    /// <summary>The most findings each list carries (the most severe and the newest first).</summary>
    public const int MostListed = 50;

    public static void MapSecurityOverview(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/admin/security", OverviewAsync).RequireFeature("code-studio").RequirePermission(Core.Access.Permissions.ReviewsAdmin);

    private static int Rank(string severity) => severity switch { "critical" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4 };

    private static async Task<IResult> OverviewAsync(AppDbContext db, ArgusAdmin argus, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var week = now.AddDays(-WeekDays);
        var scans = await db.SecurityScans.AsNoTracking().Where(s => s.Status == SecurityScan.Done)
            .Select(s => new { s.Id, s.Project, s.Ref, s.Commit, s.FinishedAt, s.AdvisoriesAsOf, s.RecheckedAt, Sbom = s.Sbom != null, s.UserId }).ToListAsync(ct);
        // A repository and ref's latest review speaks for it; the ones before it are history.
        var latest = scans.GroupBy(s => (s.Project, s.Ref)).Select(g => g.OrderByDescending(s => s.FinishedAt).First()).ToList();
        var ids = latest.Select(s => s.Id).ToList();
        var findings = await db.SecurityFindings.AsNoTracking().Where(f => ids.Contains(f.ScanId) && f.Verified != false)
            .Select(f => new Found(f.Id, f.ScanId, f.Kind, f.Severity, f.Exploited, f.Title, f.Path, f.Line, f.Cwe, f.State, f.FoundAt, f.DecidedAt, f.DecidedById, f.Why,
                f.Until, f.FoundBy)).ToListAsync(ct);
        var people = await db.Users.AsNoTracking().Select(u => new { u.Id, Name = u.DisplayName == "" ? u.UserName : u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var scanOf = latest.ToDictionary(s => s.Id);
        bool Overdue(string severity, DateTimeOffset found) => found.AddDays(SecurityReview.FixWithinDays.GetValueOrDefault(severity, 90)) < now;
        var open = findings.Where(f => f.State == SecurityFinding.Open).ToList();
        object Row(Found f) => new
        {
            f.Id, f.ScanId, project = scanOf[f.ScanId].Project, @ref = scanOf[f.ScanId].Ref, f.Kind, f.Severity, f.Exploited, f.Title, f.Path, f.Line, f.Cwe, f.FoundAt,
            f.FoundBy, overdue = f.State == SecurityFinding.Open && Overdue(f.Severity, f.FoundAt),
        };
        var repositories = latest.Select(s =>
        {
            var mine = open.Where(f => f.ScanId == s.Id).ToList();
            return new
            {
                scanId = s.Id, s.Project, s.Ref, s.Commit, s.FinishedAt, s.AdvisoriesAsOf, s.RecheckedAt, s.Sbom, by = people.GetValueOrDefault(s.UserId),
                open = severities.ToDictionary(v => v, v => mine.Count(f => f.Severity == v)),
                exploited = mine.Count(f => f.Exploited), overdue = mine.Count(f => Overdue(f.Severity, f.FoundAt)), fresh = mine.Count(f => f.FoundAt >= week),
            };
        }).OrderBy(r => r.exploited > 0 ? 0 : 1).ThenBy(r => severities.FindIndex(v => r.open[v] > 0) is var i && i >= 0 ? i : 9)
            .ThenByDescending(r => r.open.Values.Sum()).ThenBy(r => r.Project, StringComparer.Ordinal).ToList();
        var packs = argus.Enabled ? await AdvisoriesDayAsync(argus, ct) : null;
        return Results.Ok(new
        {
            repositories,
            totals = new
            {
                open = severities.ToDictionary(v => v, v => open.Count(f => f.Severity == v)),
                exploited = open.Count(f => f.Exploited), overdue = open.Count(f => Overdue(f.Severity, f.FoundAt)),
                fresh = open.Count(f => f.FoundAt >= week), fixedThisWeek = findings.Count(f => f.State == SecurityFinding.Fixed && f.DecidedAt >= week),
            },
            fresh = open.Where(f => f.FoundAt >= week).OrderBy(f => f.Exploited ? 0 : 1).ThenBy(f => Rank(f.Severity)).ThenByDescending(f => f.FoundAt).Take(MostListed).Select(f => Row(f)),
            exploited = open.Where(f => f.Exploited).OrderBy(f => Rank(f.Severity)).Take(MostListed).Select(f => Row(f)),
            overdue = open.Where(f => Overdue(f.Severity, f.FoundAt)).OrderBy(f => Rank(f.Severity)).ThenBy(f => f.FoundAt).Take(MostListed).Select(f => Row(f)),
            fixedThisWeek = findings.Where(f => f.State == SecurityFinding.Fixed && f.DecidedAt >= week).OrderByDescending(f => f.DecidedAt).Take(MostListed)
                .Select(f => new { f.Id, f.ScanId, project = scanOf[f.ScanId].Project, f.Kind, f.Severity, f.Title, f.DecidedAt, by = f.DecidedById is { } d ? people.GetValueOrDefault(d) : null }),
            accepted = findings.Where(f => f.State == SecurityFinding.Accepted).OrderBy(f => f.Until).Take(MostListed)
                .Select(f => new
                {
                    f.Id, f.ScanId, project = scanOf[f.ScanId].Project, f.Kind, f.Severity, f.Title, f.Why, f.Until, f.DecidedAt,
                    by = f.DecidedById is { } d ? people.GetValueOrDefault(d) : null, lapsed = f.Until < now,
                }),
            advisories = packs is { } day ? new { asOf = day, daysOld = (int)(now.UtcDateTime.Date - day.ToDateTime(TimeOnly.MinValue)).TotalDays } : null,
        });
    }

    private static readonly List<string> severities = ["critical", "high", "medium", "low"];

    private sealed record Found(Guid Id, Guid ScanId, string Kind, string Severity, bool Exploited, string Title, string Path, int Line, string Cwe, string State,
        DateTimeOffset FoundAt, DateTimeOffset? DecidedAt, Guid? DecidedById, string? Why, DateTimeOffset? Until, string[] FoundBy);

    private static async Task<DateOnly?> AdvisoriesDayAsync(ArgusAdmin argus, CancellationToken ct)
    {
        try
        {
            return SecurityRecheck.AdvisoriesDay(await argus.GetAsync("packs", ct));
        }
        catch (Exception ex) when (ex is ArgusException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The security team told of new findings that cannot wait: critical, or on CISA's known-exploited list. The team is
    /// whoever may manage every review (reviews.admin); the review's person is told by the review itself.
    /// </summary>
    public static async Task AlertAsync(IServiceProvider services, SecurityScan scan, IReadOnlyCollection<SecurityFinding> found, CancellationToken ct)
    {
        var urgent = found.Where(f => f.Verified != false && f.State == SecurityFinding.Open && (f.Severity == "critical" || f.Exploited)).ToList();
        if (urgent.Count == 0)
        {
            return;
        }
        var db = services.GetRequiredService<AppDbContext>();
        var access = services.GetRequiredService<AccessService>();
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled && u.Id != scan.UserId).ToListAsync(ct);
        var memberships = await access.MembershipsAsync(people, ct);
        var team = people.Where(p => memberships[p.Id].Has(Core.Access.Permissions.ReviewsAdmin)).ToList();
        var exploited = urgent.Count(f => f.Exploited);
        var what = string.Join(", ", new[]
        {
            urgent.Count(f => f.Severity == "critical") is > 0 and var c ? $"{c} critical" : null,
            exploited > 0 ? $"{exploited} known to be exploited" : null,
        }.OfType<string>());
        var notifier = services.GetRequiredService<Notifications.Notifier>();
        foreach (var person in team)
        {
            await notifier.SendAsync(person.Id, new Notifications.News("review", $"Urgent security findings in {scan.Project}",
                $"{urgent.Count} finding(s): {what}. First: {urgent.OrderBy(f => f.Exploited ? 0 : 1).ThenBy(f => Rank(f.Severity)).First().Title}",
                $"/code/security/{scan.Id}"), ct);
        }
    }
}
