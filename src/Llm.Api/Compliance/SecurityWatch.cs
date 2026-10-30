using System.Globalization;
using System.Text.Json;
using Llm.Core.Access;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Compliance;

/// <summary>Configuration section "Security": when the app raises a security alert.</summary>
public sealed class SecurityOptions
{
    /// <summary>Watch at all (the tests' apps look only when asked).</summary>
    public bool Watch { get; set; } = true;

    /// <summary>Failed sign-ins in ten minutes, all accounts together, that raise an alert.</summary>
    public int FailedSignIns { get; set; } = 20;

    /// <summary>Failed sign-ins in ten minutes for one account.</summary>
    public int FailedSignInsOneAccount { get; set; } = 10;

    /// <summary>Exports (a person's data, the compliance evidence) by one person in an hour.</summary>
    public int Exports { get; set; } = 3;
}

/// <summary>
/// Watches what the audit log and the access history say, every minute, and raises a security alert (kept, and in every admin's
/// bell, email and the alerts webhook) for: many failed sign-ins, or many on one account; an administrative role given; many
/// exports by one person; a backup that failed, or none for a day and more. Each is raised once; an incident can be opened from it.
/// (A broken audit chain and a certificate near its end are raised where they are found: AuditWatch, the alert rules.)
/// </summary>
public sealed partial class SecurityWatch(IServiceScopeFactory scopes, IOptionsMonitor<SecurityOptions> options, TimeProvider clock, ILogger<SecurityWatch> logger)
    : BackgroundService
{
    private long _changesFrom = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.Watch)
                {
                    await RunAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One look: raises what is new. Returns how many alerts it raised.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var o = options.CurrentValue;
        var now = clock.GetUtcNow();
        var raised = new List<SecurityAlert>();
        var window = now.AddMinutes(-10);
        var slot = $"{now:yyyyMMddHH}{now.Minute / 10}";

        var failures = await db.AuditEvents.AsNoTracking().Where(e => e.Action == "sign_in" && !e.Success && e.At >= window).Select(e => e.Target).ToListAsync(ct);
        if (failures.Count >= o.FailedSignIns)
        {
            raised.Add(new SecurityAlert { Kind = "sign_in_failures", Key = $"sign-in-failures:{slot}", Title = $"{failures.Count} failed sign-ins in ten minutes",
                Detail = $"Across {failures.Distinct().Count()} accounts: password guessing or spraying. The addresses are in the audit log; the sign-in throttle bans them." });
        }
        foreach (var hit in failures.Where(t => t is not null).GroupBy(t => t!).Where(g => g.Count() >= o.FailedSignInsOneAccount))
        {
            raised.Add(new SecurityAlert { Kind = "account_attacked", Key = $"account-attacked:{hit.Key}:{slot}", Title = $"{hit.Count()} failed sign-ins for {hit.Key} in ten minutes",
                Detail = "Someone is guessing this account's password: it locks itself; check who it is with them." });
        }

        // Administrative roles given since the last look (from the first look on: what came before is history).
        if (_changesFrom < 0)
        {
            _changesFrom = await db.AccessChanges.MaxAsync(c => (long?)c.Id, ct) ?? 0;
        }
        var changes = await db.AccessChanges.AsNoTracking().Where(c => c.Id > _changesFrom && c.Kind == "assignment" && c.After != null).OrderBy(c => c.Id).ToListAsync(ct);
        if (changes.Count > 0)
        {
            _changesFrom = changes[^1].Id;
            var adminKeys = Permissions.AdminKeys.ToHashSet(StringComparer.Ordinal);
            var roles = await db.AccessRoles.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
            foreach (var c in changes)
            {
                var given = JsonSerializer.Deserialize<AccessAssignment>(c.After!, JsonSerializerOptions.Web);
                if (given is null || !roles.TryGetValue(given.RoleId, out var role) || !role.Permissions.Any(adminKeys.Contains))
                {
                    continue;
                }
                var to = given.SubjectKind switch
                {
                    SubjectKind.Person => await db.Users.AsNoTracking().Where(u => u.Id == given.SubjectId).Select(u => u.UserName).SingleOrDefaultAsync(ct) ?? "someone",
                    SubjectKind.Group => "the group " + (await db.Groups.AsNoTracking().Where(g => g.Id == given.SubjectId).Select(g => g.Name).SingleOrDefaultAsync(ct) ?? "?"),
                    _ => "everyone",
                };
                var by = c.ActorId is { } actor ? await db.Users.AsNoTracking().Where(u => u.Id == actor).Select(u => u.UserName).SingleOrDefaultAsync(ct) : null;
                raised.Add(new SecurityAlert { Kind = "admin_granted", Key = $"admin-granted:{c.Id}", Title = $"{role.Name} given to {to}",
                    Detail = $"By {by ?? "the directory or the system"}, {c.At:yyyy-MM-dd HH:mm} UTC. Was it meant? Admin → Access shows it, and takes it away." });
            }
        }

        var hour = now.AddHours(-1);
        var exports = await db.AuditEvents.AsNoTracking()
            .Where(e => (e.Action == "person.export" || e.Action == "account.export" || e.Action == "compliance.evidence") && e.At >= hour && e.Actor != null)
            .GroupBy(e => e.Actor!).Select(g => new { Actor = g.Key, Count = g.Count() }).ToListAsync(ct);
        foreach (var e in exports.Where(e => e.Count >= o.Exports))
        {
            raised.Add(new SecurityAlert { Kind = "mass_export", Key = $"mass-export:{e.Actor}:{now:yyyyMMddHH}", Title = $"{e.Actor} exported data {e.Count} times in an hour",
                Detail = "People's data or the compliance evidence, downloaded again and again: is it their job? The audit log names each export." });
        }

        var backups = scope.ServiceProvider.GetRequiredService<Storage.StoragePlaces>().Backups();
        if (backups.State == "ok" && backups.Backups.Count > 0)
        {
            var newest = backups.Backups.Where(b => b.At is not null).MaxBy(b => b.At);
            if (newest is { Result: { } result } && result != "ok" && !result.StartsWith("running", StringComparison.Ordinal))
            {
                raised.Add(new SecurityAlert { Kind = "backup_failed", Key = $"backup-failed:{newest.Name}", Title = $"The backup {newest.Name} failed",
                    Detail = $"It says: {result}. Its backup.log says why; the one before it is the newest that can be restored." });
            }
            var good = backups.Backups.Where(b => b.Result == "ok" && b.At is not null).MaxBy(b => b.At);
            if (good is not null && now - good.At!.Value > ComplianceChecks.BackupDue)
            {
                raised.Add(new SecurityAlert { Kind = "backup_late", Key = $"backup-late:{now:yyyyMMdd}", Title = "No good backup for over a day",
                    Detail = $"The newest that ended well is {good.Name}. Is the daily backup's timer running (sudo ./arenactl backup --install-timer from the release's checkout; on an installed host without one, sudo DIR/deploy/scripts/backup.sh --install-timer)?" });
            }
        }

        var known = await db.SecurityAlerts.AsNoTracking().Where(a => raised.Select(r => r.Key).Contains(a.Key)).Select(a => a.Key).ToListAsync(ct);
        var fresh = raised.Where(r => !known.Contains(r.Key)).DistinctBy(r => r.Key).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }
        foreach (var a in fresh)
        {
            a.At = now;
        }
        db.SecurityAlerts.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        var notifier = scope.ServiceProvider.GetRequiredService<Notifications.Notifier>();
        foreach (var a in fresh)
        {
            await notifier.ToAdminsAsync(new Notifications.News("alert", a.Title, a.Detail, "/admin/compliance?tab=incidents", $"security:{a.Key}"), ct);
            LogRaised(logger, a.Kind, a.Title);
        }
        return fresh.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security alert {Kind}: {Title}")]
    private static partial void LogRaised(ILogger logger, string kind, string title);

    [LoggerMessage(Level = LogLevel.Error, Message = "The security watch failed: {Reason}; trying again in a minute")]
    private static partial void LogFailed(ILogger logger, string reason);
}

public sealed class IncidentException(string message) : Exception(message);

/// <summary>The incident register: opened (from an alert or by hand), each step on its timeline, the clocks the law sets, closed with lessons.</summary>
public sealed class Incidents(AppDbContext db, Identity.Audit audit, TimeProvider clock)
{
    public static readonly string[] Severities = ["low", "medium", "high", "critical"];

    public static readonly string[] Steps = ["note", "contained", "resolved", "authority_notified", "authority_not_required", "people_notified", "closed"];

    /// <summary>The GDPR's time to tell the supervisory authority of a personal data breach (Article 33(1)).</summary>
    public static readonly TimeSpan AuthorityWithin = TimeSpan.FromHours(72);

    /// <summary>HIPAA's longest time to tell the people whose health data was breached (164.404(b)).</summary>
    public static readonly TimeSpan PeopleWithin = TimeSpan.FromDays(60);

    public async Task<Incident> OpenAsync(string title, string? summary, string severity, bool personal, bool health, Guid? alertId, DateTimeOffset? detectedAt, string by, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new IncidentException("Name it.");
        }
        if (!Severities.Contains(severity))
        {
            throw new IncidentException("Its severity is low, medium, high or critical.");
        }
        var now = clock.GetUtcNow();
        var incident = new Incident
        {
            Title = title.Trim(), Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(), Severity = severity, PersonalData = personal || health, HealthData = health,
            DetectedAt = detectedAt is { } at && at <= now ? at : now, OpenedBy = by,
        };
        db.Incidents.Add(incident);
        db.IncidentEvents.Add(new IncidentEvent { IncidentId = incident.Id, At = now, By = by, Kind = "opened", Text = incident.Summary });
        if (alertId is { } id && await db.SecurityAlerts.SingleOrDefaultAsync(a => a.Id == id, ct) is { } alert)
        {
            alert.IncidentId = incident.Id;
            incident.DetectedAt = detectedAt ?? alert.At;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("incident.open", incident.Title, detail: $"{severity}{(incident.PersonalData ? ", personal data" : "")}{(health ? ", health data" : "")}");
        return incident;
    }

    /// <summary>A step: a note, contained, resolved, the authority told (or not, with why), the people told, closed (with what was learned).</summary>
    public async Task<Incident> StepAsync(Guid id, string kind, string? text, string by, CancellationToken ct)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException();
        if (incident.Status == Incident.Closed)
        {
            throw new IncidentException("It is closed.");
        }
        if (!Steps.Contains(kind))
        {
            throw new IncidentException($"A step is one of: {string.Join(", ", Steps)}.");
        }
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var now = clock.GetUtcNow();
        switch (kind)
        {
            case "note" when text is null:
                throw new IncidentException("Say what happened.");
            case "contained":
                incident.Status = Incident.Contained;
                break;
            case "resolved":
                incident.Status = Incident.Resolved;
                break;
            case "authority_notified":
                incident.AuthorityDecidedAt = now;
                incident.AuthorityNotified = true;
                break;
            case "authority_not_required" when text is null:
                throw new IncidentException("Say why it need not be told (unlikely to result in a risk to people's rights and freedoms: Article 33(1)).");
            case "authority_not_required":
                incident.AuthorityDecidedAt = now;
                incident.AuthorityNotified = false;
                break;
            case "people_notified":
                incident.PeopleNotifiedAt = now;
                break;
            case "closed" when text is null:
                throw new IncidentException("Say what was learned, and what changes so it does not happen again.");
            case "closed" when incident.PersonalData && incident.AuthorityDecidedAt is null:
                throw new IncidentException("Personal data was involved: record whether the supervisory authority was told first.");
            case "closed":
                incident.Status = Incident.Closed;
                incident.ClosedAt = now;
                incident.Lessons = text;
                break;
        }
        db.IncidentEvents.Add(new IncidentEvent { IncidentId = id, At = now, By = by, Kind = kind, Text = text });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("incident." + kind, incident.Title, detail: text is null ? null : text.Length > 200 ? text[..200] + "…" : text);
        return incident;
    }

    /// <summary>When the authority must be told by, and when the people must be: the law's clocks, from detection.</summary>
    public static (DateTimeOffset? Authority, DateTimeOffset? People) Clocks(Incident i) =>
        (i.PersonalData ? i.DetectedAt + AuthorityWithin : null, i.HealthData ? i.DetectedAt + PeopleWithin : null);

    public static string When(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
