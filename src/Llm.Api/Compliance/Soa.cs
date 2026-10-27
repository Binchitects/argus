using System.Globalization;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Compliance;

public sealed class SoaException(string message) : Exception(message);

/// <param name="Decided">Whether the company has said whether it applies and how far it is in place.</param>
/// <param name="Controls">Arena's controls that serve it.</param>
/// <param name="Risks">The open risks those controls treat.</param>
public sealed record SoaLine(string Clause, string Title, bool Decided, bool Applicable, string? Status, string? Justification, string? UpdatedBy,
    DateTimeOffset? UpdatedAt, IReadOnlyList<string> Controls, IReadOnlyList<SoaRisk> Risks);

public sealed record SoaRisk(Guid Id, string Title);

public sealed record SoaApprovalView(int Version, string ApprovedBy, DateTimeOffset ApprovedAt, string? Note);

/// <param name="Approved">The newest approval, if any.</param>
/// <param name="ChangedSinceApproval">A line was changed after it.</param>
public sealed record SoaView(string Framework, string Title, IReadOnlyList<SoaLine> Lines, SoaApprovalView? Approved, bool ChangedSinceApproval,
    IReadOnlyList<SoaApprovalView> Versions);

public sealed record SoaUpdate(bool Applicable, string? Status, string? Justification);

/// <summary>
/// The Statements of Applicability (ISO 27001 6.1.3 d; ISO 42001 6.1.3): for each of a framework's Annex A controls, whether
/// it applies and why, how far it is in place, Arena's controls that serve it and the risks they treat. Approved as a whole,
/// each approval a version kept as it stood.
/// </summary>
public sealed class Soa(AppDbContext db, Identity.Audit audit, TimeProvider clock)
{
    public static readonly string[] Frameworks = ["iso27001", "iso42001"];

    public static readonly string[] Statuses = [SoaEntry.Implemented, SoaEntry.Partial, SoaEntry.Planned, SoaEntry.NotImplemented];

    /// <summary>A framework's Annex A controls, as its catalog lists them.</summary>
    public static IReadOnlyList<CatalogClause> AnnexA(string framework) =>
        Frameworks.Contains(framework) ? [.. ComplianceControls.Catalogs[framework].Clauses.Where(c => c.Id.StartsWith("A.", StringComparison.Ordinal))]
            : throw new KeyNotFoundException();

    /// <summary>Arena's controls that serve each clause of a framework (a cited clause counting for the one it is part of).</summary>
    public static Dictionary<string, List<string>> Serving(string framework)
    {
        var catalog = ComplianceControls.Catalogs[framework];
        var serving = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in ComplianceControls.All.Controls)
        {
            foreach (var clause in c.Frameworks.GetValueOrDefault(framework) ?? [])
            {
                if (catalog.Entry(clause) is { } e && !(serving.TryGetValue(e, out var list) ? list : serving[e] = []).Contains(c.Id))
                {
                    serving[e].Add(c.Id);
                }
            }
        }
        return serving;
    }

    public async Task<SoaView> GetAsync(string framework, CancellationToken ct)
    {
        var clauses = AnnexA(framework);
        var saved = await db.SoaEntries.AsNoTracking().Where(e => e.Framework == framework).ToDictionaryAsync(e => e.Clause, ct);
        var serving = Serving(framework);
        var risks = await db.Risks.AsNoTracking().Where(r => r.ClosedAt == null).Select(r => new { r.Id, r.Title, r.Controls }).ToListAsync(ct);
        var approvals = await db.SoaApprovals.AsNoTracking().Where(a => a.Framework == framework).OrderByDescending(a => a.Version)
            .Select(a => new SoaApprovalView(a.Version, a.ApprovedBy, a.ApprovedAt, a.Note)).ToListAsync(ct);
        var lines = clauses.Select(c =>
        {
            var controls = serving.GetValueOrDefault(c.Id) ?? [];
            var treating = risks.Where(r => r.Controls.Any(controls.Contains)).Select(r => new SoaRisk(r.Id, r.Title)).ToList();
            return saved.TryGetValue(c.Id, out var e)
                ? new SoaLine(c.Id, c.Title, true, e.Applicable, e.Status, e.Justification, e.UpdatedBy, e.UpdatedAt, controls, treating)
                : new SoaLine(c.Id, c.Title, false, true, null, null, null, null, controls, treating);
        }).ToList();
        var latest = approvals.FirstOrDefault();
        return new SoaView(framework, ComplianceControls.Catalogs[framework].Title, lines, latest,
            latest is not null && saved.Values.Any(e => e.UpdatedAt > latest.ApprovedAt), approvals);
    }

    /// <summary>One control's line: whether it applies (an exclusion says why), and how far it is in place.</summary>
    public async Task<SoaLine> SetAsync(string framework, string clause, SoaUpdate update, string by, CancellationToken ct)
    {
        var title = AnnexA(framework).FirstOrDefault(c => c.Id == clause)?.Title ?? throw new KeyNotFoundException();
        var why = string.IsNullOrWhiteSpace(update.Justification) ? null : update.Justification.Trim();
        if (why is { Length: > 2000 })
        {
            throw new SoaException("2000 characters at most.");
        }
        if (!update.Applicable && why is null)
        {
            throw new SoaException("Say why it does not apply: an exclusion needs its reason.");
        }
        var status = update.Applicable ? update.Status : SoaEntry.NotImplemented;
        if (!Statuses.Contains(status))
        {
            throw new SoaException($"How far it is in place: {string.Join(", ", Statuses)}.");
        }
        var entry = await db.SoaEntries.SingleOrDefaultAsync(e => e.Framework == framework && e.Clause == clause, ct);
        if (entry is null)
        {
            entry = new SoaEntry { Framework = framework, Clause = clause, UpdatedBy = by };
            db.SoaEntries.Add(entry);
        }
        (entry.Applicable, entry.Status, entry.Justification, entry.UpdatedBy, entry.UpdatedAt) = (update.Applicable, status!, why, by, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.soa.set", $"{framework} {clause}", detail: $"{(update.Applicable ? "applies" : "excluded")}, {status}{(why is null ? "" : $": {why}")}");
        return (await GetAsync(framework, ct)).Lines.Single(l => l.Clause == clause);
    }

    /// <summary>Approved as it stands, as a new version: every control decided first.</summary>
    public async Task<SoaApprovalView> ApproveAsync(string framework, string? note, string by, CancellationToken ct)
    {
        var view = await GetAsync(framework, ct);
        var open = view.Lines.Count(l => !l.Decided);
        if (open > 0)
        {
            throw new SoaException($"{open} of its {view.Lines.Count} controls are not decided yet: each applies or not, and how far.");
        }
        var version = (view.Approved?.Version ?? 0) + 1;
        var approval = new SoaApproval
        {
            Framework = framework, Version = version, ApprovedBy = by, ApprovedAt = clock.GetUtcNow(), Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Snapshot = Csv(view),
        };
        db.SoaApprovals.Add(approval);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.soa.approve", framework, detail: $"version {version}{(approval.Note is null ? "" : $": {approval.Note}")}");
        return new SoaApprovalView(version, by, approval.ApprovedAt, approval.Note);
    }

    /// <summary>The statement as an auditor reads it: each control, whether it applies and why, how far, Arena's controls and the risks.</summary>
    public static string Csv(SoaView view)
    {
        static string Cell(string? s) => s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
        var rows = new List<string> { "control,title,applies,how far,why,Arena's controls,risks,decided by,decided at" };
        rows.AddRange(view.Lines.Select(l => string.Join(",", Cell(l.Clause), Cell(l.Title), l.Decided ? (l.Applicable ? "yes" : "no") : "",
            Cell(l.Status), Cell(l.Justification), Cell(string.Join(" ", l.Controls)), Cell(string.Join("; ", l.Risks.Select(r => r.Title))), Cell(l.UpdatedBy),
            l.UpdatedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "")));
        return string.Join("\n", rows) + "\n";
    }
}
