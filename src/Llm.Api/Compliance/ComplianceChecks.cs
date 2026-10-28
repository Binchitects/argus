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
    PromDatasource prom, IOptions<StackOptions> stack, AuditWatch auditWatch, AuditChainState auditState, IOptionsMonitor<AuditOptions> auditOptions, AuditForwarder forwarder, IOptionsMonitor<ComplianceOptions> compliance, IOptionsMonitor<NoticeOptions> notice, Policies policies, Suppliers suppliers, AiSystems ai, TimeProvider clock)
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
        ("risks", "Risks are assessed, treated and reviewed in time"),
        ("soa", "The Statement of Applicability is approved and current"),
        ("policies", "Policies are approved, reviewed in time and read"),
        ("audits", "Audited each year, findings acted on in time, certificates current"),
        ("suppliers", "Suppliers are registered, agreed with and reviewed"),
        ("ai-systems", "AI systems are registered, classified, assessed and reviewed"),
        ("management", "The management system is reviewed, its objectives met, auditors answered"),
        ("vulnerabilities", "Repositories are reviewed for security, and what is found fixed or accepted in time"),
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
                "risks" => await RisksAsync(ct),
                "soa" => await SoaAsync(ct),
                "policies" => await PoliciesAsync(ct),
                "audits" => await AuditsAsync(ct),
                "suppliers" => await SuppliersAsync(ct),
                "ai-systems" => await AiSystemsAsync(ct),
                "management" => await ManagementAsync(ct),
                "vulnerabilities" => await VulnerabilitiesAsync(ct),
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

    /// <summary>The register is not empty, and no risk is past its review, its acceptance or its actions' dates; a month past is red.</summary>
    private async Task<(string, string)> RisksAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var open = await db.Risks.AsNoTracking().Where(r => r.ClosedAt == null).ToListAsync(ct);
        if (open.Count == 0)
        {
            return (Warn, "The risk register is empty: assess the risks (Admin → Compliance → Risks).");
        }
        var ids = open.Select(r => r.Id).ToList();
        var late = await db.RiskActions.AsNoTracking().Where(a => ids.Contains(a.RiskId) && a.DoneAt == null && a.Due < now).CountAsync(ct);
        var overdue = open.Where(r => r.ReviewDue < now).ToList();
        var expired = open.Count(r => r.Treatment == Llm.Core.Compliance.Risk.Accept && r.AcceptedUntil < now);
        var levels = string.Join(", ", open.GroupBy(r => Risks.Level(r.ResidualLikelihood is { } l && r.ResidualImpact is { } i ? l * i : r.Likelihood * r.Impact))
            .OrderBy(g => Array.IndexOf(["critical", "high", "medium", "low"], g.Key)).Select(g => $"{g.Count()} {g.Key}"));
        var said = $"{open.Count} open risk{(open.Count == 1 ? "" : "s")} ({levels}, as left after treatment where assessed)";
        if (overdue.Count == 0 && expired == 0 && late == 0)
        {
            return (Ok, $"{said}; the next review is due {open.Min(r => r.ReviewDue):yyyy-MM-dd}.");
        }
        var due = string.Join("; ", new[]
        {
            overdue.Count > 0 ? $"{overdue.Count} past their review" : null, expired > 0 ? $"{expired} accepted past the acceptance's end" : null,
            late > 0 ? $"{late} treatment action{(late == 1 ? "" : "s")} past due" : null,
        }.OfType<string>());
        return (overdue.Any(r => r.ReviewDue < now.AddDays(-30)) ? Fail : Warn, $"{said}: {due}.");
    }

    /// <summary>ISO 27001's statement approved within the year and not changed since; ISO 42001's said beside it when there is one.</summary>
    private async Task<(string, string)> SoaAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var said = new List<string>();
        var state = Ok;
        foreach (var fw in Soa.Frameworks)
        {
            var name = fw == "iso27001" ? "ISO 27001's" : "ISO 42001's";
            var latest = await db.SoaApprovals.AsNoTracking().Where(a => a.Framework == fw).OrderByDescending(a => a.Version).FirstOrDefaultAsync(ct);
            var decided = await db.SoaEntries.CountAsync(e => e.Framework == fw, ct);
            if (latest is null)
            {
                // ISO 42001's only when the company is working on one.
                if (fw == "iso27001" || decided > 0)
                {
                    said.Add($"{name} is not approved yet ({decided} of {Soa.AnnexA(fw).Count} controls decided)");
                    state = Warn;
                }
                continue;
            }
            var changed = await db.SoaEntries.AnyAsync(e => e.Framework == fw && e.UpdatedAt > latest.ApprovedAt, ct);
            var stale = latest.ApprovedAt < now.AddDays(-365);
            said.Add($"{name} version {latest.Version}, approved by {latest.ApprovedBy} on {latest.ApprovedAt:yyyy-MM-dd}{(changed ? ", changed since" : "")}{(stale ? ", over a year ago" : "")}");
            if (changed || stale)
            {
                state = Warn;
            }
        }
        return (state, string.Join("; ", said) + (state == Ok ? "." : ": Admin → Compliance → Statement of Applicability."));
    }

    /// <summary>Policies in force, none past its review (red a month past), and each read by those who must within a month of its approval.</summary>
    private async Task<(string, string)> PoliciesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = await policies.ListAsync(ct);
        var inForce = all.Where(p => p.Version is not null).ToList();
        if (inForce.Count == 0)
        {
            return (Warn, "No policy is approved yet: Admin → Compliance → Policies adopts the templates.");
        }
        var overdue = inForce.Where(p => p.ReviewOverdue).ToList();
        var unread = inForce.Where(p => p.Acknowledged < p.Readers && p.ApprovedAt < now.AddDays(-30)).ToList();
        var said = $"{inForce.Count} polic{(inForce.Count == 1 ? "y" : "ies")} in force{(all.Count > inForce.Count ? $", {all.Count - inForce.Count} draft only" : "")}; read: {inForce.Sum(p => p.Acknowledged)} of {inForce.Sum(p => p.Readers)} acknowledgements";
        if (overdue.Count == 0 && unread.Count == 0)
        {
            return (Ok, $"{said}; the next review is due {inForce.Min(p => p.ReviewDue):yyyy-MM-dd}.");
        }
        var due = string.Join("; ", new[]
        {
            overdue.Count > 0 ? $"past their review: {string.Join(", ", overdue.Select(p => p.Title))}" : null,
            unread.Count > 0 ? $"not read by everyone a month after approval: {string.Join(", ", unread.Select(p => $"{p.Title} ({p.Acknowledged} of {p.Readers})"))}" : null,
        }.OfType<string>());
        return (overdue.Any(p => p.ReviewDue < now.AddDays(-30)) ? Fail : Warn, $"{said}: {due}.");
    }

    /// <summary>An internal audit within the year; no finding past its date (a major one is red); no certificate ended (red) or ending within 90 days; no waiver past its end.</summary>
    private async Task<(string, string)> AuditsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var lastInternal = await db.ComplianceAudits.AsNoTracking().Where(a => a.Kind == "internal" && a.Starts <= now).MaxAsync(a => (DateTimeOffset?)a.Starts, ct);
        var late = await db.Findings.AsNoTracking().Where(f => f.Status == Llm.Core.Compliance.Finding.Open && f.Due < now).Select(f => new { f.Title, f.Kind }).ToListAsync(ct);
        var open = await db.Findings.CountAsync(f => f.Status != Llm.Core.Compliance.Finding.Verified, ct);
        var certs = await db.Certificates.AsNoTracking().ToListAsync(ct);
        var waivers = await db.ControlWaivers.CountAsync(w => w.WithdrawnAt == null && w.Until < now, ct);
        var problems = new List<string>();
        var state = Ok;
        void Say(string what, string level)
        {
            problems.Add(what);
            state = level == Fail || state == Fail ? Fail : Warn;
        }
        if (lastInternal is null || lastInternal < now.AddDays(-365))
        {
            Say(lastInternal is null ? "no internal audit yet" : $"no internal audit since {lastInternal:yyyy-MM-dd}", Warn);
        }
        if (late.Count > 0)
        {
            Say($"{late.Count} finding{(late.Count == 1 ? "" : "s")} past due ({string.Join(", ", late.Take(3).Select(f => $"{f.Kind}: {f.Title}"))})", late.Any(f => f.Kind == "major") ? Fail : Warn);
        }
        foreach (var c in certs.Where(c => c.Expires < now.AddDays(90)))
        {
            Say(c.Expires < now ? $"{c.Framework}'s certificate ended {c.Expires:yyyy-MM-dd}" : $"{c.Framework}'s certificate ends {c.Expires:yyyy-MM-dd}", c.Expires < now ? Fail : Warn);
        }
        if (waivers > 0)
        {
            Say($"{waivers} waiver{(waivers == 1 ? "" : "s")} past {(waivers == 1 ? "its" : "their")} end, to decide again", Warn);
        }
        var said = $"last internal audit {(lastInternal is { } d ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "none")}; {open} finding{(open == 1 ? "" : "s")} open; {certs.Count} certificate{(certs.Count == 1 ? "" : "s")}";
        return (state, problems.Count == 0 ? $"{said}." : $"{said}: {string.Join("; ", problems)}.");
    }

    /// <summary>Every service Arena connects to in the register, none past its review, and each that gets personal or health data with its agreement.</summary>
    private async Task<(string, string)> SuppliersAsync(CancellationToken ct)
    {
        var listed = (await suppliers.ListAsync(ct)).Where(s => s.RetiredAt is null).ToList();
        var missing = await suppliers.SuggestAsync(ct);
        var overdue = listed.Where(s => s.ReviewOverdue).Select(s => s.Name).ToList();
        var unagreed = listed.Where(s => s.AgreementMissing).Select(s => s.Name).ToList();
        var said = $"{listed.Count} supplier{(listed.Count == 1 ? "" : "s")} in the register";
        var problems = new[]
        {
            missing.Count > 0 ? $"not in it yet: {string.Join(", ", missing.Select(m => m.Name))}" : null,
            overdue.Count > 0 ? $"past their review: {string.Join(", ", overdue)}" : null,
            unagreed.Count > 0 ? $"personal or health data with no agreement said (a DPA, a BAA): {string.Join(", ", unagreed)}" : null,
        }.OfType<string>().ToList();
        return problems.Count == 0 ? (Ok, $"{said}, every service Arena connects to among them.") : (Warn, $"{said}; {string.Join("; ", problems)}.");
    }

    /// <summary>The shared assistants, workflows, apps and models in the register; none in use with something missing (red) or past its review.</summary>
    private async Task<(string, string)> AiSystemsAsync(CancellationToken ct)
    {
        var listed = (await ai.ListAsync(ct)).Where(s => s.RetiredAt is null).ToList();
        var missing = await ai.SuggestAsync(ct);
        var inUse = listed.Where(s => s.InUseSince is not null).ToList();
        var lacking = inUse.Where(s => s.Missing.Count > 0).Select(s => $"{s.Name} ({string.Join(", ", s.Missing)})").ToList();
        var overdue = listed.Where(s => s.ReviewOverdue).Select(s => s.Name).ToList();
        var classes = string.Join(", ", listed.GroupBy(s => s.Class).Select(g => $"{g.Count()} {g.Key}"));
        var said = $"{listed.Count} AI system{(listed.Count == 1 ? "" : "s")} in the register{(classes.Length > 0 ? $" ({classes})" : "")}, {inUse.Count} in use";
        var problems = new[]
        {
            lacking.Count > 0 ? $"in use without what their class needs: {string.Join("; ", lacking)}" : null,
            missing.Count > 0 ? $"not in it yet: {string.Join(", ", missing.Take(5).Select(m => m.Name))}{(missing.Count > 5 ? $" and {missing.Count - 5} more" : "")}" : null,
            overdue.Count > 0 ? $"past their review: {string.Join(", ", overdue)}" : null,
        }.OfType<string>().ToList();
        return problems.Count == 0 ? (Ok, $"{said}.") : (lacking.Count > 0 ? Fail : Warn, $"{said}; {string.Join("; ", problems)}.");
    }

    /// <summary>A management review within the year, the objectives met over 90 days, and no auditor's request a fortnight unanswered.</summary>
    private async Task<(string, string)> ManagementAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var last = await db.ManagementReviews.AsNoTracking().MaxAsync(r => (DateTimeOffset?)r.HeldAt, ct);
        var unanswered = await db.EvidenceRequests.CountAsync(r => r.AnsweredAt == null && r.RequestedAt < now.AddDays(-14), ct);
        var objectives = await db.SecurityObjectives.AsNoTracking().ToListAsync(ct);
        var owned = await db.ControlOwners.CountAsync(ct);
        var since = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-90));
        var missed = new List<string>();
        foreach (var o in objectives)
        {
            var days = await db.ComplianceSnapshots.Where(s => s.Check == o.Check && s.Day >= since).GroupBy(_ => 1)
                .Select(g => new { Days = g.Count(), Ok = g.Count(s => s.State == Ok) }).FirstOrDefaultAsync(ct);
            if (days is { Days: > 0 } && 100.0 * days.Ok / days.Days < o.Target)
            {
                missed.Add(o.Title);
            }
        }
        var said = $"last management review {(last is { } l ? l.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "none")}; {objectives.Count} objective{(objectives.Count == 1 ? "" : "s")}; {owned} of {ComplianceControls.All.Controls.Count} controls owned";
        var problems = new[]
        {
            last is null || last < now.AddDays(-365) ? "no management review within the year" : null,
            objectives.Count == 0 ? "no security objective set" : null,
            missed.Count > 0 ? $"objectives not met: {string.Join(", ", missed)}" : null,
            unanswered > 0 ? $"{unanswered} auditor's request{(unanswered == 1 ? "" : "s")} unanswered a fortnight" : null,
        }.OfType<string>().ToList();
        return problems.Count == 0 ? (Ok, $"{said}.") : (Warn, $"{said}: {string.Join("; ", problems)}.");
    }

    /// <summary>
    /// Code Studio's security reviews: one within 30 days, against advisories no older than a week; each open finding fixed
    /// within its severity's time (critical 7 days, high 30, medium 90, low 180) or accepted until a date not past. An overdue
    /// critical or high finding fails; the rest warns.
    /// </summary>
    private async Task<(string, string)> VulnerabilitiesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var last = await db.SecurityScans.AsNoTracking().Where(s => s.Status == Llm.Core.Code.SecurityScan.Done).OrderByDescending(s => s.FinishedAt).FirstOrDefaultAsync(ct);
        if (last is null)
        {
            return (Warn, "No repository has been reviewed for security: run one in Code Studio → Security.");
        }
        var open = await db.SecurityFindings.AsNoTracking().Where(f => f.Verified != false && (f.State == Llm.Core.Code.SecurityFinding.Open
                || (f.State == Llm.Core.Code.SecurityFinding.Accepted && f.Until != null && f.Until < now)))
            .Select(f => new { f.Severity, f.State, Found = f.FoundAt })
            .ToListAsync(ct);
        var overdue = open.Where(f => f.State != Llm.Core.Code.SecurityFinding.Open
            || f.Found.AddDays(Code.SecurityReview.FixWithinDays.GetValueOrDefault(f.Severity, 90)) < now).ToList();
        var said = $"last security review {last.FinishedAt:yyyy-MM-dd} ({last.Project}); {open.Count} open finding{(open.Count == 1 ? "" : "s")}";
        var problems = new[]
        {
            last.FinishedAt < now.AddDays(-30) ? "no security review within 30 days" : null,
            last.AdvisoriesAsOf is null ? "its packages were not checked (no advisories pack)"
                : last.AdvisoriesAsOf.Value.ToDateTime(TimeOnly.MinValue) < (last.FinishedAt ?? now).UtcDateTime.AddDays(-Code.SecurityReview.AdvisoriesDue) ? "its advisories were over a week old" : null,
            overdue.Count > 0 ? $"{overdue.Count} past the time to fix ({string.Join(", ", overdue.GroupBy(f => f.Severity).Select(g => $"{g.Count()} {g.Key}"))})" : null,
        }.OfType<string>().ToList();
        if (overdue.Any(f => f.Severity is "critical" or "high"))
        {
            return (Fail, $"{said}: {string.Join("; ", problems)}.");
        }
        return problems.Count == 0 ? (Ok, $"{said}, none past its time.") : (Warn, $"{said}: {string.Join("; ", problems)}.");
    }

    private (string, string) DataKey() => string.IsNullOrEmpty(auth.CurrentValue.DataKey)
        ? (Fail, "APP_KEY is not set: the signing keys and settings' secrets are not encrypted at rest.")
        : (Ok, "The signing keys and settings' secrets are encrypted with APP_KEY.");

    private static string Span(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{t.TotalDays:0} days" : t.TotalHours >= 2 ? $"{t.TotalHours:0} hours" : t == TimeSpan.FromHours(1) ? "1 hour"
        : t.TotalMinutes >= 2 ? $"{t.TotalMinutes:0} minutes" : $"{t.TotalSeconds:0} seconds";
}
