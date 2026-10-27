using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Compliance;

public sealed class AuditException(string message) : Exception(message);

public sealed record AuditInput(string? Title, string? Kind, string? Framework, string? Scope, string? Auditor, DateTimeOffset Starts, DateTimeOffset? Ends);

public sealed record FindingInput(Guid? AuditId, string? Title, string? Kind, string? Description, string? Clause, string? Control, string? Owner, DateTimeOffset? Due,
    string? Correction, string? RootCause, string? CorrectiveAction);

public sealed record CertificateInput(string? Framework, string? Body, string? Number, string? Scope, DateTimeOffset Issued, DateTimeOffset Expires);

public sealed record WaiverInput(string? Control, string? Reason, string? Compensating, DateTimeOffset Until);

public sealed record AuditView(Guid Id, string Title, string Kind, string? Framework, string? Scope, string? Auditor, DateTimeOffset Starts, DateTimeOffset? Ends,
    string? Conclusion, DateTimeOffset? ReportedAt, string CreatedBy, int Findings, int OpenFindings);

public sealed record FindingView(Guid Id, Guid? AuditId, string? Audit, string Title, string Kind, string? Description, string? Clause, string? Control, string? Owner,
    DateTimeOffset? Due, bool Late, string? Correction, string? RootCause, string? CorrectiveAction, string Status, DateTimeOffset? DoneAt, string? DoneBy,
    DateTimeOffset? VerifiedAt, string? VerifiedBy, string? Verification, string CreatedBy, DateTimeOffset CreatedAt);

public sealed record CertificateView(Guid Id, string Framework, string Body, string? Number, string? Scope, DateTimeOffset Issued, DateTimeOffset Expires, int DaysLeft);

public sealed record WaiverView(Guid Id, string Control, string Reason, string? Compensating, string ApprovedBy, DateTimeOffset ApprovedAt, DateTimeOffset Until,
    DateTimeOffset? WithdrawnAt, bool Expired);

public sealed record AuditsView(IReadOnlyList<AuditView> Audits, IReadOnlyList<FindingView> Findings, IReadOnlyList<CertificateView> Certificates, IReadOnlyList<WaiverView> Waivers);

/// <summary>
/// Audits and what they find (Admin → Compliance → Audits): the audit programme, each audit's findings with their correction,
/// root cause, corrective action and its verification (ISO 27001 9.2 and 10.2), the certificates the company holds and when
/// they end, and the waivers of controls not met for a time. Every change is audited.
/// </summary>
public sealed class Audits(AppDbContext db, Identity.Audit audit, TimeProvider clock)
{
    /// <summary>A waiver lasts a year at most: then it is decided again.</summary>
    public static readonly TimeSpan WaiverLasts = TimeSpan.FromDays(365);

    public async Task<AuditsView> AllAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var audits = await db.ComplianceAudits.AsNoTracking().OrderByDescending(a => a.Starts).ToListAsync(ct);
        var findings = await db.Findings.AsNoTracking().OrderBy(f => f.Status == Finding.Verified).ThenBy(f => f.Due ?? DateTimeOffset.MaxValue).ToListAsync(ct);
        var names = audits.ToDictionary(a => a.Id, a => a.Title);
        return new AuditsView(
            [.. audits.Select(a => new AuditView(a.Id, a.Title, a.Kind, a.Framework, a.Scope, a.Auditor, a.Starts, a.Ends, a.Conclusion, a.ReportedAt, a.CreatedBy,
                findings.Count(f => f.AuditId == a.Id), findings.Count(f => f.AuditId == a.Id && f.Status != Finding.Verified)))],
            [.. findings.Select(f => View(f, f.AuditId is { } id ? names.GetValueOrDefault(id) : null, now))],
            [.. (await db.Certificates.AsNoTracking().OrderBy(c => c.Expires).ToListAsync(ct)).Select(c =>
                new CertificateView(c.Id, c.Framework, c.Body, c.Number, c.Scope, c.Issued, c.Expires, (int)Math.Floor((c.Expires - now).TotalDays)))],
            [.. (await db.ControlWaivers.AsNoTracking().OrderBy(w => w.WithdrawnAt != null).ThenBy(w => w.Until).ToListAsync(ct)).Select(w =>
                new WaiverView(w.Id, w.Control, w.Reason, w.Compensating, w.ApprovedBy, w.ApprovedAt, w.Until, w.WithdrawnAt, w.WithdrawnAt is null && w.Until < now))]);
    }

    public async Task<AuditView> SaveAuditAsync(Guid? id, AuditInput input, string by, CancellationToken ct)
    {
        var a = id is { } i ? await db.ComplianceAudits.SingleOrDefaultAsync(x => x.Id == i, ct) ?? throw new KeyNotFoundException() : new ComplianceAudit { Title = "", CreatedBy = by };
        a.Title = Text(input.Title, 300) ?? throw new AuditException("Name the audit.");
        a.Kind = ComplianceAudit.Kinds.Contains(input.Kind) ? input.Kind! : throw new AuditException($"Its kind is one of: {string.Join(", ", ComplianceAudit.Kinds)}.");
        a.Framework = input.Framework is { Length: > 0 } fw ? (ComplianceControls.Catalogs.ContainsKey(fw) ? fw : throw new AuditException($"No framework {fw}.")) : null;
        if (input.Ends is { } end && end < input.Starts)
        {
            throw new AuditException("It ends after it starts.");
        }
        (a.Scope, a.Auditor, a.Starts, a.Ends) = (Text(input.Scope, 4000), Text(input.Auditor, 256), input.Starts, input.Ends);
        if (id is null)
        {
            db.ComplianceAudits.Add(a);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "compliance.audit.plan" : "compliance.audit.change", a.Title, detail: $"{a.Kind}{(a.Framework is null ? "" : $", {a.Framework}")}, from {a.Starts:yyyy-MM-dd}");
        return (await AllAsync(ct)).Audits.Single(x => x.Id == a.Id);
    }

    /// <summary>The auditor's conclusion, once the audit is reported.</summary>
    public async Task<AuditView> ReportAsync(Guid id, string? conclusion, string by, CancellationToken ct)
    {
        var a = await db.ComplianceAudits.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        a.Conclusion = Text(conclusion, 4000) ?? throw new AuditException("Say what the audit concluded.");
        a.ReportedAt = clock.GetUtcNow();
        a.Ends ??= a.ReportedAt;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.audit.report", a.Title, detail: a.Conclusion.Length > 200 ? a.Conclusion[..200] + "…" : a.Conclusion);
        return (await AllAsync(ct)).Audits.Single(x => x.Id == id);
    }

    public async Task<FindingView> SaveFindingAsync(Guid? id, FindingInput input, string by, CancellationToken ct)
    {
        var f = id is { } i ? await db.Findings.SingleOrDefaultAsync(x => x.Id == i, ct) ?? throw new KeyNotFoundException() : new Finding { Title = "", CreatedBy = by, CreatedAt = clock.GetUtcNow() };
        if (f.Status == Finding.Verified)
        {
            throw new AuditException("It is verified and closed.");
        }
        if (input.AuditId is { } aid && !await db.ComplianceAudits.AnyAsync(a => a.Id == aid, ct))
        {
            throw new AuditException("No such audit.");
        }
        f.AuditId = input.AuditId;
        f.Title = Text(input.Title, 300) ?? throw new AuditException("Name the finding.");
        f.Kind = Finding.Kinds.Contains(input.Kind) ? input.Kind! : throw new AuditException($"A finding is {string.Join(", ", Finding.Kinds)}.");
        f.Clause = Clause(input.Clause);
        f.Control = input.Control is { Length: > 0 } c ? (ComplianceControls.All.Controls.Any(x => x.Id == c) ? c : throw new AuditException($"No control {c}.")) : null;
        (f.Description, f.Owner, f.Due) = (Text(input.Description, 4000), Text(input.Owner, 256), input.Due);
        (f.Correction, f.RootCause, f.CorrectiveAction) = (Text(input.Correction, 4000), Text(input.RootCause, 4000), Text(input.CorrectiveAction, 4000));
        if (id is null)
        {
            db.Findings.Add(f);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "compliance.finding.raise" : "compliance.finding.change", f.Title, detail: $"{f.Kind}{(f.Clause is null ? "" : $", {f.Clause}")}");
        return await FindingAsync(f.Id, ct);
    }

    /// <summary>Its corrective action taken: a nonconformity needs its root cause and action said first.</summary>
    public async Task<FindingView> DoneAsync(Guid id, string by, CancellationToken ct)
    {
        var f = await db.Findings.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (f.Status != Finding.Open)
        {
            throw new AuditException("Its action is taken already.");
        }
        if (f.Kind is "major" or "minor" && (f.RootCause is null || f.CorrectiveAction is null))
        {
            throw new AuditException("A nonconformity needs its root cause and its corrective action said first.");
        }
        (f.Status, f.DoneAt, f.DoneBy) = (Finding.Done, clock.GetUtcNow(), by);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.finding.done", f.Title);
        return await FindingAsync(id, ct);
    }

    /// <summary>Checked that the action worked (closed), or that it did not (open again, with why).</summary>
    public async Task<FindingView> VerifyAsync(Guid id, bool worked, string? note, string by, CancellationToken ct)
    {
        var f = await db.Findings.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (f.Status != Finding.Done)
        {
            throw new AuditException("Its action is to be taken before it is checked.");
        }
        note = Text(note, 4000) ?? throw new AuditException("Say how it was checked.");
        if (worked)
        {
            (f.Status, f.VerifiedAt, f.VerifiedBy, f.Verification) = (Finding.Verified, clock.GetUtcNow(), by, note);
        }
        else
        {
            (f.Status, f.DoneAt, f.DoneBy, f.Verification) = (Finding.Open, null, null, $"Not effective ({by}): {note}");
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(worked ? "compliance.finding.verify" : "compliance.finding.reopen", f.Title, detail: note);
        return await FindingAsync(id, ct);
    }

    public async Task<CertificateView> SaveCertificateAsync(Guid? id, CertificateInput input, string by, CancellationToken ct)
    {
        var c = id is { } i ? await db.Certificates.SingleOrDefaultAsync(x => x.Id == i, ct) ?? throw new KeyNotFoundException() : new Certificate { Framework = "", Body = "", CreatedBy = by };
        c.Framework = input.Framework is { Length: > 0 } fw && ComplianceControls.Catalogs.ContainsKey(fw) ? fw : throw new AuditException("Which framework it certifies.");
        c.Body = Text(input.Body, 256) ?? throw new AuditException("Who issued it.");
        if (input.Expires <= input.Issued)
        {
            throw new AuditException("It ends after it was issued.");
        }
        (c.Number, c.Scope, c.Issued, c.Expires) = (Text(input.Number, 128), Text(input.Scope, 4000), input.Issued, input.Expires);
        if (id is null)
        {
            db.Certificates.Add(c);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "compliance.certificate.add" : "compliance.certificate.change", c.Framework, detail: $"{c.Body}, until {c.Expires:yyyy-MM-dd}");
        return (await AllAsync(ct)).Certificates.Single(x => x.Id == c.Id);
    }

    /// <summary>A control not met for a time, in the approver's name: why, what makes up for it, until when (a year at most).</summary>
    public async Task<WaiverView> WaiveAsync(WaiverInput input, string by, CancellationToken ct)
    {
        var control = input.Control?.Trim() is { Length: > 0 } c && (ComplianceControls.All.Controls.Any(x => x.Id == c) || Clause(c) is not null) ? c
            : throw new AuditException("Name the control (IAM-03) or the clause (iso27001 A.8.5).");
        var now = clock.GetUtcNow();
        if (input.Until <= now || input.Until > now + WaiverLasts)
        {
            throw new AuditException("A waiver lasts until a date within the next year.");
        }
        var w = new ControlWaiver
        {
            Control = control, Reason = Text(input.Reason, 4000) ?? throw new AuditException("Say why it is not met."), Compensating = Text(input.Compensating, 4000),
            ApprovedBy = by, ApprovedAt = now, Until = input.Until,
        };
        db.ControlWaivers.Add(w);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.waiver.approve", control, detail: $"until {w.Until:yyyy-MM-dd}: {w.Reason}");
        return (await AllAsync(ct)).Waivers.Single(x => x.Id == w.Id);
    }

    public async Task<WaiverView> WithdrawAsync(Guid id, string by, CancellationToken ct)
    {
        var w = await db.ControlWaivers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        w.WithdrawnAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.waiver.withdraw", w.Control, detail: by);
        return (await AllAsync(ct)).Waivers.Single(x => x.Id == id);
    }

    private async Task<FindingView> FindingAsync(Guid id, CancellationToken ct) => (await AllAsync(ct)).Findings.Single(f => f.Id == id);

    private static FindingView View(Finding f, string? audit, DateTimeOffset now) =>
        new(f.Id, f.AuditId, audit, f.Title, f.Kind, f.Description, f.Clause, f.Control, f.Owner, f.Due, f.Status == Finding.Open && f.Due < now, f.Correction,
            f.RootCause, f.CorrectiveAction, f.Status, f.DoneAt, f.DoneBy, f.VerifiedAt, f.VerifiedBy, f.Verification, f.CreatedBy, f.CreatedAt);

    /// <summary>"iso27001 A.8.5": a framework and a clause its catalog lists (or one it is part of); empty is none.</summary>
    private static string? Clause(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        var parts = s.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && ComplianceControls.IsClause(parts[0], parts[1]) ? $"{parts[0]} {parts[1]}"
            : throw new AuditException($"\"{s.Trim()}\" is not a framework's clause: say it as iso27001 A.8.5.");
    }

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        s = s.Trim();
        return s.Length <= max ? s : throw new AuditException($"{max} characters at most.");
    }
}
