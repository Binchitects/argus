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
    PromDatasource prom, IOptions<StackOptions> stack, TimeProvider clock)
{
    public const string Ok = "ok", Warn = "warn", Fail = "fail", Unknown = "unknown";

    /// <summary>Every check, by id, with what it looks at.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> All =
    [
        ("passwords", "Strong passwords and lockout"),
        ("sessions", "Sessions end on their own"),
        ("two-factor", "Admins sign in with two factors"),
        ("audit-log", "The audit log is written"),
        ("chat-retention", "Chats are kept for a set time"),
        ("log-retention", "Logs are kept for a set time"),
        ("backups", "A recent backup that ended well"),
        ("certificate", "The site's certificate"),
        ("data-key", "Secrets encrypted at rest"),
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
                "chat-retention" => await ChatRetentionAsync(ct),
                "log-retention" => await LogRetentionAsync(ct),
                "backups" => Backups(),
                "certificate" => await CertificateAsync(ct),
                "data-key" => DataKey(),
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

    private async Task<(string, string)> ChatRetentionAsync(CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().CountAsync(g => g.RetentionDays != null, ct);
        var ofGroups = groups == 0 ? "" : $" {groups} {(groups == 1 ? "group sets its" : "groups set their")} own.";
        return retention.CurrentValue.Days is > 0 and var days
            ? (Ok, $"Chats are deleted {days} days after their last message.{ofGroups}")
            : (Warn, $"Chats are kept forever unless a group sets a time.{ofGroups} Set Settings → Data retention → Keep chats for (the GDPR's storage limitation).");
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
        var said = $"The newest that ended well is {good.Name}, {Span(age)} old; {report.Backups.Count} kept.";
        return age <= BackupDue ? (Ok, said) : (age <= BackupStale ? Warn : Fail, $"{said} Backups are expected every day.");
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

    private (string, string) DataKey() => string.IsNullOrEmpty(auth.CurrentValue.DataKey)
        ? (Fail, "APP_KEY is not set: the signing keys and settings' secrets are not encrypted at rest.")
        : (Ok, "The signing keys and settings' secrets are encrypted with APP_KEY.");

    private static string Span(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{t.TotalDays:0} days" : t.TotalHours >= 2 ? $"{t.TotalHours:0} hours" : t == TimeSpan.FromHours(1) ? "1 hour"
        : t.TotalMinutes >= 2 ? $"{t.TotalMinutes:0} minutes" : $"{t.TotalSeconds:0} seconds";
}
