using Llm.Api.Dashboards;
using Llm.Api.Identity;
using Llm.Api.Operations;
using Llm.Api.Storage;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Compliance;

/// <summary>A check's result.</summary>
/// <param name="State">ok (met), warn (in part, or a choice to make knowingly), fail (not met), unknown (could not be told: what it reads did not answer).</param>
/// <param name="Detail">What it found, and when it is not ok, what to change.</param>
public sealed record CheckResult(string Id, string Title, string State, string Detail);

/// <summary>
/// The checks the app runs on itself for Admin → Compliance (their ids are app:ID in controls.yaml): what it is set to, and what
/// it sees of the stack. deploy/scripts/compliance-check.py prints them beside its own, from outside.
/// </summary>
public sealed class ComplianceChecks(AppDbContext db, UserManager<AppUser> users, Access.AccessService access, IOptionsMonitor<AuthOptions> auth,
    IOptions<IdentityOptions> identity, IOptionsMonitor<Retention.RetentionOptions> retention, StoragePlaces places, LokiDatasource loki,
    PromDatasource prom, IOptions<StackOptions> stack, AuditWatch auditWatch, AuditChainState auditState, IOptionsMonitor<AuditOptions> auditOptions, AuditForwarder forwarder, IOptionsMonitor<ComplianceOptions> compliance, IOptionsMonitor<NoticeOptions> notice, TimeProvider clock)
{
    public const string Ok = "ok", Warn = "warn", Fail = "fail", Unknown = "unknown";

    /// <summary>Every check, by id, with what it looks at.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> All =
    [
        ("passwords", "Strong passwords and lockout"),
        ("sessions", "Sessions end on their own"),
        ("two-factor", "Admins sign in with two factors"),
        ("audit-log", "The audit log is written"),
        ("audit-chain", "Nobody changed the audit log unseen"),
        ("audit-forwarding", "The audit log is sent on to the SIEM"),
        ("chat-retention", "Chats are kept for a set time"),
        ("log-retention", "Logs are kept for a set time"),
        ("records-retention", "Records are kept for set times"),
        ("backups", "A recent backup that ended well"),
        ("certificate", "The site's certificate"),
        ("data-key", "Secrets encrypted at rest"),
        ("files-sealed", "People's files encrypted at rest"),
        ("personal-data", "Personal and health data kept from cloud models"),
        ("notice", "Everyone accepted the sign-in notice"),
        ("incidents", "Incidents handled within the law's time"),
        ("access-review", "Privileged access was reviewed"),
        ("data-requests", "People's data requests answered in time"),
    ];

    /// <summary>A newest backup older than this is amber; one older than <see cref="BackupStale"/> is red.</summary>
    public static readonly TimeSpan BackupDue = TimeSpan.FromHours(26), BackupStale = TimeSpan.FromDays(7);

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var results = new List<CheckResult>();
        foreach (var (id, title) in All)
        {
            var (state, detail) = id switch
            {
                "passwords" => Passwords(),
                "sessions" => Sessions(),
                "two-factor" => await TwoFactorAsync(ct),
                "audit-log" => await AuditLogAsync(ct),
                "audit-chain" => await AuditChainAsync(ct),
                "audit-forwarding" => await AuditForwardingAsync(ct),
                "chat-retention" => await ChatRetentionAsync(ct),
                "log-retention" => await LogRetentionAsync(ct),
                "records-retention" => RecordsRetention(),
                "backups" => Backups(),
                "certificate" => await CertificateAsync(ct),
                "data-key" => DataKey(),
                "files-sealed" => await FilesSealedAsync(ct),
                "personal-data" => await PersonalDataAsync(ct),
                "notice" => await NoticeAsync(ct),
                "incidents" => await IncidentsAsync(ct),
                "access-review" => await AccessReviewAsync(ct),
                "data-requests" => await DataRequestsAsync(ct),
                _ => (Unknown, "No such check."),
            };
            results.Add(new CheckResult(id, title, state, detail));
        }
        return results;
    }

    private (string, string) Passwords()
    {
        var lockout = identity.Value.Lockout;
        var strong = users.PasswordValidators.OfType<StrongPasswordValidator>().Any();
        if (!strong)
        {
            return (Fail, "Passwords are not scored for strength.");
        }
        return lockout.AllowedForNewUsers && lockout.MaxFailedAccessAttempts <= 10
            ? (Ok, $"Passwords score 3 or more on zxcvbn; {lockout.MaxFailedAccessAttempts} failures lock an account for {lockout.DefaultLockoutTimeSpan.TotalMinutes:0} minutes, and guessing from one address is banned sooner.")
            : (Warn, $"An account locks only after {lockout.MaxFailedAccessAttempts} failures: 10 or fewer is expected.");
    }

    private (string, string) Sessions()
    {
        var a = auth.CurrentValue;
        var kept = $"Idle {Span(a.SessionIdle)}, at most {Span(a.SessionMax)}; \"keep me signed in\" {Span(a.RememberMe)}.";
        return a.SessionIdle <= TimeSpan.FromHours(1) && a.SessionMax <= TimeSpan.FromHours(12)
            ? (Ok, kept)
            : (Warn, $"{kept} Expected: idle 1 hour or less and 12 hours at most (Settings → Sign-in).");
    }

    /// <summary>People who hold an admin permission and sign in here with a password alone (company sign-in brings the provider's own factors).</summary>
    private async Task<(string, string)> TwoFactorAsync(CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct);
        var memberships = await access.MembershipsAsync(people, ct);
        var adminKeys = Permissions.All.Where(p => p.Admin).Select(p => p.Key).ToList();
        var admins = people.Where(p => adminKeys.Any(memberships[p.Id].Has)).ToList();
        var here = admins.Where(p => p.Source != UserSource.Oidc).ToList();
        var without = here.Where(p => !p.TwoFactorEnabled).Select(p => p.UserName ?? "").Order(StringComparer.Ordinal).ToList();
        var everyone = people.Count(p => p.Source != UserSource.Oidc);
        var enrolled = people.Count(p => p.Source != UserSource.Oidc && p.TwoFactorEnabled);
        var share = $"{enrolled} of {everyone} {(everyone == 1 ? "person" : "people")} who sign in here {(enrolled == 1 ? "has" : "have")} a second factor.";
        var policy = auth.CurrentValue.TwoFactorRequired;
        var required = policy == TwoFactorPolicy.Off ? "It is not required: set Settings → Sign-in → Require two-factor sign-in to admins or everyone."
            : $"Required of {policy}: who has none sets it up at their next sign-in.";
        if (without.Count == 0)
        {
            return (policy == TwoFactorPolicy.Off ? Warn : Ok, $"Every admin who signs in here has a second factor. {share} {required}");
        }
        var named = string.Join(", ", without.Take(5)) + (without.Count > 5 ? $" and {without.Count - 5} more" : "");
        return (Warn, $"{without.Count} of {here.Count} admins who sign in here {(without.Count == 1 ? "has" : "have")} no second factor: {named}. {share} {required}");
    }

    private async Task<(string, string)> AuditLogAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var day = now.AddDays(-1);
        var latest = await db.AuditEvents.AsNoTracking().MaxAsync(e => (DateTimeOffset?)e.At, ct);
        if (latest is null)
        {
            return (Warn, "The audit log is empty.");
        }
        var oldest = await db.AuditEvents.AsNoTracking().MinAsync(e => e.At, ct);
        var lastDay = await db.AuditEvents.AsNoTracking().CountAsync(e => e.At >= day, ct);
        return (Ok, $"{lastDay} entries in the last 24 hours; the oldest is from {oldest:yyyy-MM-dd}.");
    }

    /// <summary>The whole chain walked now, and whether its anchors are kept apart from the database.</summary>
    private async Task<(string, string)> AuditChainAsync(CancellationToken ct)
    {
        var r = await auditWatch.WalkAsync(anchor: false, ct);
        if (!r.Holds)
        {
            return (Fail, r.BrokenAt is { } at
                ? $"The hash chain is broken at entry {at}: {r.Why}."
                : $"The anchors of entries {string.Join(", ", r.AnchorsWrong)} no longer match: the chain was rewritten after them.");
        }
        var walked = $"{r.Entries} entries, each sealed onto the one before, walked now and whole.";
        if (string.IsNullOrWhiteSpace(auditOptions.CurrentValue.AnchorDir))
        {
            return (Warn, $"{walked} Its daily anchors are only logged: give them a folder of their own (Audit:AnchorDir).");
        }
        return auditState.LastAnchor is { } a
            ? (Ok, $"{walked} {r.Anchors} anchors kept apart, the newest from {a.At:yyyy-MM-dd} (entry {a.Id}).")
            : (Warn, $"{walked} No anchor is kept yet: the first is made within a day of the app starting.");
    }

    private async Task<(string, string)> AuditForwardingAsync(CancellationToken ct)
    {
        if (!forwarder.On)
        {
            return (Warn, "The audit log is kept only here: send it on to the company's SIEM as well (Settings → Audit log: syslog or a webhook).");
        }
        var s = await forwarder.StateAsync(db, ct);
        var behind = s.Newest - s.Sent;
        if (s.Problem is { } problem)
        {
            return (Fail, $"{behind} entries wait to be sent, and the last try failed: {problem}");
        }
        // What was written in the last seconds is on its way.
        return behind <= 20
            ? (Ok, $"Sent on as it is written: entry {s.Sent} of {s.Newest}.")
            : (Warn, $"{behind} entries wait to be sent (entry {s.Sent} of {s.Newest}).");
    }

    private async Task<(string, string)> ChatRetentionAsync(CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().CountAsync(g => g.RetentionDays != null, ct);
        var ofGroups = groups == 0 ? "" : $" {groups} {(groups == 1 ? "group sets its" : "groups set their")} own.";
        return retention.CurrentValue.Days is > 0 and var days
            ? (Ok, $"Chats are deleted {days} days after their last message.{ofGroups}")
            : (Warn, $"Chats are kept forever unless a group sets a time.{ofGroups} Set Settings → Data retention → Keep chats for (the GDPR's storage limitation).");
    }

    /// <summary>The audit log at least six years (or for good), and the other records for a set time.</summary>
    private (string, string) RecordsRetention()
    {
        var r = retention.CurrentValue;
        var forever = new[] { ("the gateway's request log", r.GatewayLogDays), ("the record of tool calls", r.ToolCallDays), ("news in the bell", r.NotificationDays) }
            .Where(x => x.Item2 <= 0).Select(x => x.Item1).ToList();
        var said = $"The audit log {(r.AuditYears > 0 ? $"{r.AuditYears} years" : "forever")}; the gateway's request log {Days(r.GatewayLogDays)}, tool calls {Days(r.ToolCallDays)}, the bell {Days(r.NotificationDays)}.";
        if (r.AuditYears is > 0 and < 6)
        {
            return (Warn, $"{said} HIPAA keeps the audit log six years: set Settings → Data retention → Keep the audit log for to 6.");
        }
        return forever.Count > 0 ? (Warn, $"{said} Kept forever: {string.Join(", ", forever)} (Settings → Data retention).") : (Ok, said);

        static string Days(int d) => d > 0 ? $"{d} days" : "forever";
    }

    private async Task<(string, string)> LogRetentionAsync(CancellationToken ct)
    {
        string? keeps;
        try
        {
            keeps = StorageReport.LokiKeeps(await loki.TextAsync("/config", ct));
        }
        catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (Unknown, "Loki did not answer.");
        }
        return keeps switch
        {
            null => (Unknown, "Loki's configuration does not say how long it keeps logs."),
            "forever" => (Warn, "Loki keeps logs forever: set its retention (config/loki.yml)."),
            _ => (Ok, $"Loki keeps logs {keeps}."),
        };
    }

    private (string, string) Backups()
    {
        var report = places.Backups();
        if (report.State != "ok")
        {
            return (Unknown, report.State == "missing"
                ? $"The app does not see the backups: {report.Dir} is not mounted (BACKUP_DIR)."
                : $"The app may not read the backups in {report.Dir}.");
        }
        var good = report.Backups.Where(b => b.Result == "ok" && b.At is not null).MaxBy(b => b.At);
        if (good is null)
        {
            return (Fail, report.Backups.Count == 0 ? "No backup yet: run scripts/backup.sh on a schedule." : "No backup ended well: see the newest one's log.");
        }
        var age = clock.GetUtcNow() - good.At!.Value;
        var said = $"The newest that ended well is {good.Name}, {Span(age)} old{(good.Encrypted ? ", encrypted" : "")}; {report.Backups.Count} kept.";
        if (age > BackupDue)
        {
            return (age <= BackupStale ? Warn : Fail, $"{said} Backups are expected every day.");
        }
        return good.Encrypted ? (Ok, said) : (Warn, $"{said} It is not encrypted, and it holds every secret of the stack: set BACKUP_KEY_FILE (scripts/backup.sh --help).");
    }

    private async Task<(string, string)> CertificateAsync(CancellationToken ct)
    {
        var cert = await Certificates.ReadAsync(prom, stack.Value, clock.GetUtcNow(), ct);
        if (cert is null)
        {
            return (Unknown, "Prometheus does not know the certificate Traefik serves.");
        }
        if (cert.Issuer == "traefik")
        {
            return (Fail, "Traefik serves its own default certificate: browsers warn. Give it one (deployment.md, Certificates).");
        }
        var said = $"{cert.Name}, {(cert.Issuer == "letsencrypt" ? "from Let's Encrypt" : "your own")}, expires {cert.ExpiresAt:yyyy-MM-dd}";
        return cert.Days switch
        {
            < 0 => (Fail, $"{said}: it has expired."),
            <= 30 => (Warn, $"{said}, in {cert.Days} days: renew it."),
            _ => (Ok, $"{said}, in {cert.Days} days."),
        };
    }

    /// <summary>A review closed within the period (and a little), or one under way and not late.</summary>
    private async Task<(string, string)> AccessReviewAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var every = Math.Max(1, compliance.CurrentValue.ReviewEveryDays is > 0 and var d ? d : 90);
        var closed = await db.AccessReviews.AsNoTracking().Where(r => r.ClosedAt != null).MaxAsync(r => r.ClosedAt, ct);
        var open = await db.AccessReviews.AsNoTracking().Where(r => r.ClosedAt == null).OrderBy(r => r.DueAt).FirstOrDefaultAsync(ct);
        if (open is not null && open.DueAt < now)
        {
            return (Warn, $"\"{open.Title}\" was due {open.DueAt:yyyy-MM-dd} and is still open: keep or revoke what is left, then close it.");
        }
        if (closed is { } c && now - c <= TimeSpan.FromDays(every + 14))
        {
            return (Ok, $"The last review was closed {c:yyyy-MM-dd}{(open is null ? "" : $"; \"{open.Title}\" is under way, due {open.DueAt:yyyy-MM-dd}")}.");
        }
        return open is not null
            ? (Warn, $"\"{open.Title}\" is under way, due {open.DueAt:yyyy-MM-dd}; none was closed in the last {every} days.")
            : (Warn, closed is null ? "Access was never reviewed: start a review under Admin → Compliance." : $"The last review was closed {closed:yyyy-MM-dd}, over {every} days ago.");
    }

    /// <summary>None open past its month (GDPR 12(3)); amber when one is due within a week.</summary>
    private async Task<(string, string)> DataRequestsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var open = await db.DataRequests.AsNoTracking().Where(r => r.Status == Llm.Core.Compliance.DataRequest.Open).OrderBy(r => r.DueAt).ToListAsync(ct);
        var answered = await db.DataRequests.CountAsync(r => r.Status != Llm.Core.Compliance.DataRequest.Open, ct);
        var late = open.Where(r => r.DueAt < now).ToList();
        if (late.Count > 0)
        {
            return (Fail, $"{late.Count} past {(late.Count == 1 ? "its" : "their")} month: {string.Join(", ", late.Take(5).Select(r => $"{r.Person}'s {r.Kind} (due {r.DueAt:yyyy-MM-dd})"))}.");
        }
        var soon = open.Where(r => r.DueAt < now.AddDays(7)).ToList();
        if (soon.Count > 0)
        {
            return (Warn, $"{soon.Count} due within a week, the first {soon[0].Person}'s {soon[0].Kind} by {soon[0].DueAt:yyyy-MM-dd}.");
        }
        return (Ok, open.Count == 0 ? $"None waiting; {answered} answered." : $"{open.Count} waiting, the first due {open[0].DueAt:yyyy-MM-dd}; {answered} answered.");
    }

    /// <summary>Every file's bytes sealed with the key from APP_KEY (the sealing job does those stored before).</summary>
    private async Task<(string, string)> FilesSealedAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.CurrentValue.DataKey))
        {
            return (Fail, "APP_KEY is not set: people's files are stored as they are.");
        }
        var left = await FileSealingJob.UnsealedAsync(db, ct);
        return left == 0
            ? (Ok, "Every file, its sound and its drawn pages are encrypted with AES-256-GCM under a key from APP_KEY.")
            : (Warn, $"{left} files stored before are not sealed yet: the sealing job seals them within the hour.");
    }

    /// <summary>A safeguard rule that masks or blocks personal data in questions before models on other providers, the health identifiers among it.</summary>
    private async Task<(string, string)> PersonalDataAsync(CancellationToken ct)
    {
        var rules = await db.SafeguardRules.AsNoTracking()
            .Where(r => r.Enabled && !r.Monitor && r.Detector == "pii" && (r.Action == "mask" || r.Action == "block")).ToListAsync(ct);
        var toCloud = rules.Where(r => r.Stages.Contains("question") && (r.Models.Count == 0 || r.Models.Contains("@external"))).ToList();
        if (toCloud.Count == 0)
        {
            return (Warn, "No safeguard rule keeps personal data from models on other providers: add \"Personal data to cloud models\" and \"Health and identity data to cloud models\" (Admin → Safeguards → Start from a template).");
        }
        var health = Safeguards.Pii.HealthKinds.Where(k => !toCloud.Any(r => r.Kinds.Count == 0 || r.Kinds.Contains(k))).ToList();
        return health.Count == 0
            ? (Ok, $"{string.Join(", ", toCloud.Select(r => $"\"{r.Name}\" ({r.Action})"))} keep personal and health data from models on other providers.")
            : (Warn, $"{string.Join(", ", toCloud.Select(r => $"\"{r.Name}\""))} leave out {string.Join(", ", health.Select(Safeguards.Pii.Named))}: add \"Health and identity data to cloud models\".");
    }

    /// <summary>A notice is set, and who of the people still here accepted its current version.</summary>
    private async Task<(string, string)> NoticeAsync(CancellationToken ct)
    {
        var o = notice.CurrentValue;
        if (!o.On)
        {
            return (Warn, "No sign-in notice: set acceptable use and the privacy notice under Settings → Sign-in notice, for everyone to accept.");
        }
        var people = await db.Users.CountAsync(u => !u.IsDisabled, ct);
        var accepted = await db.NoticeAcceptances.Where(a => a.Version == o.Version).Join(db.Users.Where(u => !u.IsDisabled), a => a.UserId, u => u.Id, (a, _) => a.UserId).Distinct().CountAsync(ct);
        return (Ok, $"Version {o.Version}: {accepted} of {people} people accepted it; the others do before their next step in the app.");
    }

    /// <summary>No breach past the GDPR's 72 hours without the authority question answered, or past HIPAA's 60 days without the people told.</summary>
    private async Task<(string, string)> IncidentsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var open = await db.Incidents.AsNoTracking().Where(i => i.Status != Llm.Core.Compliance.Incident.Closed).ToListAsync(ct);
        var late = open.Where(i => (i.PersonalData && i.AuthorityDecidedAt is null && now > i.DetectedAt + Incidents.AuthorityWithin)
            || (i.HealthData && i.PeopleNotifiedAt is null && now > i.DetectedAt + Incidents.PeopleWithin)).ToList();
        var alerts = await db.SecurityAlerts.CountAsync(a => a.At >= now.AddDays(-30), ct);
        if (late.Count > 0)
        {
            return (Fail, $"Past the law's time: {string.Join("; ", late.Select(i => $"\"{i.Title}\" (found {i.DetectedAt:yyyy-MM-dd HH:mm} UTC)"))}. Tell the supervisory authority (GDPR 33) or record why not, and the people (HIPAA 164.404).");
        }
        return open.Count > 0
            ? (Warn, $"{open.Count} incident{(open.Count == 1 ? "" : "s")} open; {alerts} security alerts in 30 days.")
            : (Ok, $"No incident open; {alerts} security alerts in 30 days.");
    }

    private (string, string) DataKey() => string.IsNullOrEmpty(auth.CurrentValue.DataKey)
        ? (Fail, "APP_KEY is not set: the signing keys and settings' secrets are not encrypted at rest.")
        : (Ok, "The signing keys and settings' secrets are encrypted with APP_KEY.");

    private static string Span(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{t.TotalDays:0} days" : t.TotalHours >= 2 ? $"{t.TotalHours:0} hours" : t == TimeSpan.FromHours(1) ? "1 hour"
        : t.TotalMinutes >= 2 ? $"{t.TotalMinutes:0} minutes" : $"{t.TotalSeconds:0} seconds";
}
