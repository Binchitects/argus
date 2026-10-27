using System.Globalization;
using System.Text;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Compliance;

public sealed class GovernanceException(string message) : Exception(message);

public sealed record ReviewInput(DateTimeOffset HeldAt, string? Attendees, string? Minutes, string? Decisions);

public sealed record ObjectiveInput(string? Title, string? Check, int Target, string? Owner);

/// <param name="Achieved">The share of days in the last 90 the check held (ok), out of 100; null before any day is kept.</param>
public sealed record ObjectiveView(Guid Id, string Title, string Check, int Target, string? Owner, int? Achieved, int Days, bool Met);

public sealed record EvidenceRequestView(Guid Id, string Text, string? Clause, string RequestedBy, DateTimeOffset RequestedAt, string? Answer, string? AnsweredBy,
    DateTimeOffset? AnsweredAt);

/// <summary>
/// Running the management system (Admin → Compliance → Management): each control's owner; the management reviews (9.3), their
/// inputs drawn up from the system as it stands; the security objectives (6.2), each measured by a check's daily history; and the
/// auditors' requests for evidence, with their answers.
/// </summary>
public sealed class Governance(AppDbContext db, Readiness readiness, Risks risks, Audits audits, Identity.Audit audit, TimeProvider clock)
{
    public async Task<Dictionary<string, string>> OwnersAsync(CancellationToken ct) =>
        await db.ControlOwners.AsNoTracking().ToDictionaryAsync(o => o.ControlId, o => o.Owner, ct);

    public async Task SetOwnerAsync(string control, string? owner, string by, CancellationToken ct)
    {
        if (!ComplianceControls.All.Controls.Any(c => c.Id == control))
        {
            throw new KeyNotFoundException();
        }
        var row = await db.ControlOwners.SingleOrDefaultAsync(o => o.ControlId == control, ct);
        if (string.IsNullOrWhiteSpace(owner))
        {
            if (row is not null)
            {
                db.ControlOwners.Remove(row);
            }
        }
        else
        {
            owner = owner.Trim();
            if (owner.Length > 256)
            {
                throw new GovernanceException("256 characters at most.");
            }
            if (row is null)
            {
                db.ControlOwners.Add(new ControlOwner { ControlId = control, Owner = owner, SetBy = by, SetAt = clock.GetUtcNow() });
            }
            else
            {
                (row.Owner, row.SetBy, row.SetAt) = (owner, by, clock.GetUtcNow());
            }
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.owner", control, detail: owner ?? "none");
    }

    /// <summary>
    /// A management review's inputs, as the clause names them, drawn up from the system as it stands: the actions from the reviews
    /// before, nonconformities and corrective actions, monitoring and measurement (the objectives, readiness), audit results, the
    /// risks and their treatment, and opportunities to improve.
    /// </summary>
    public async Task<string> InputsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sb = new StringBuilder();
        void Line(string s) => sb.AppendLine(s);
        Line($"# Management review: inputs, {now:yyyy-MM-dd}");
        Line("");
        var last = await db.ManagementReviews.AsNoTracking().OrderByDescending(r => r.HeldAt).FirstOrDefaultAsync(ct);
        Line("## Actions from the review before");
        Line(last is null ? "This is the first review." : $"The review of {last.HeldAt:yyyy-MM-dd} decided: {last.Decisions ?? "nothing recorded"}");
        Line("");
        var all = await audits.AllAsync(ct);
        var since = last?.HeldAt ?? DateTimeOffset.MinValue;
        Line("## Nonconformities and corrective actions");
        var open = all.Findings.Where(f => f.Status != Finding.Verified).ToList();
        Line($"{open.Count} finding(s) open, {all.Findings.Count(f => f.Status == Finding.Verified && f.VerifiedAt >= since)} closed since the last review.");
        foreach (var f in open.Take(20))
        {
            Line($"- {f.Kind}: {f.Title}{(f.Late ? " (past its date)" : "")}");
        }
        Line("");
        Line("## Audit results");
        foreach (var a in all.Audits.Where(a => a.Starts >= since).Take(20))
        {
            Line($"- {a.Title} ({a.Kind}, {a.Starts:yyyy-MM-dd}): {a.Conclusion ?? "not reported yet"}");
        }
        if (!all.Audits.Any(a => a.Starts >= since))
        {
            Line("No audit since the last review.");
        }
        Line("");
        Line("## Monitoring and measurement");
        foreach (var o in await ObjectivesAsync(ct))
        {
            Line($"- {o.Title}: {(o.Achieved is { } v ? $"{v}% of {o.Days} days" : "not measured yet")}, target {o.Target}%{(o.Met ? "" : ", not met")}");
        }
        foreach (var r in (await readiness.AllAsync(ct)).Where(r => r.Framework is "iso27001" or "iso42001" or "soc2"))
        {
            Line($"- Ready for {r.Title}: {r.Score} of 100, {r.Gaps.Count} gap(s)");
        }
        Line("");
        Line("## Risks and their treatment");
        var live = (await risks.ListAsync(ct)).Where(r => r.ClosedAt is null).ToList();
        Line($"{live.Count} open risk(s): {string.Join(", ", live.GroupBy(r => r.ResidualLevel ?? r.Level).Select(g => $"{g.Count()} {g.Key}"))}; {live.Count(r => r.ReviewOverdue)} past their review.");
        foreach (var r in live.Where(r => (r.ResidualLevel ?? r.Level) is "high" or "critical").Take(20))
        {
            Line($"- {r.Title}: {r.ResidualLevel ?? r.Level}, {r.Treatment}");
        }
        Line("");
        Line("## Changes inside and outside, the interested parties' feedback, opportunities to improve");
        Line("To be said at the review.");
        return sb.ToString();
    }

    public async Task<ManagementReview> RecordAsync(ReviewInput input, string by, CancellationToken ct)
    {
        if (input.HeldAt > clock.GetUtcNow().AddDays(1))
        {
            throw new GovernanceException("A review is recorded once held.");
        }
        var review = new ManagementReview
        {
            HeldAt = input.HeldAt, Attendees = Text(input.Attendees, 2000) ?? throw new GovernanceException("Who took part."),
            Minutes = Text(input.Minutes, 20_000), Decisions = Text(input.Decisions, 10_000) ?? throw new GovernanceException("What it decided."),
            Inputs = await InputsAsync(ct), CreatedBy = by, CreatedAt = clock.GetUtcNow(),
        };
        db.ManagementReviews.Add(review);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.management_review", review.HeldAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), detail: review.Decisions.Length > 200 ? review.Decisions[..200] + "…" : review.Decisions);
        return review;
    }

    public async Task<IReadOnlyList<ManagementReview>> ReviewsAsync(CancellationToken ct) =>
        await db.ManagementReviews.AsNoTracking().OrderByDescending(r => r.HeldAt).ToListAsync(ct);

    /// <summary>Each objective, measured over the last 90 days of its check's daily history.</summary>
    public async Task<IReadOnlyList<ObjectiveView>> ObjectivesAsync(CancellationToken ct)
    {
        var objectives = await db.SecurityObjectives.AsNoTracking().OrderBy(o => o.Title).ToListAsync(ct);
        var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(-90));
        var checksIn = objectives.Select(o => o.Check).Distinct().ToList();
        var days = await db.ComplianceSnapshots.AsNoTracking().Where(s => s.Day >= since && checksIn.Contains(s.Check))
            .GroupBy(s => s.Check).Select(g => new { g.Key, Days = g.Count(), Ok = g.Count(s => s.State == ComplianceChecks.Ok) }).ToDictionaryAsync(g => g.Key, ct);
        return [.. objectives.Select(o =>
        {
            var d = days.GetValueOrDefault(o.Check);
            int? achieved = d is { Days: > 0 } ? (int)Math.Round(100.0 * d.Ok / d.Days) : null;
            return new ObjectiveView(o.Id, o.Title, o.Check, o.Target, o.Owner, achieved, d?.Days ?? 0, achieved >= o.Target);
        })];
    }

    public async Task<ObjectiveView> SaveObjectiveAsync(Guid? id, ObjectiveInput input, string by, CancellationToken ct)
    {
        var o = id is { } i ? await db.SecurityObjectives.SingleOrDefaultAsync(x => x.Id == i, ct) ?? throw new KeyNotFoundException() : new SecurityObjective { Title = "", Check = "", CreatedBy = by };
        o.Title = Text(input.Title, 300) ?? throw new GovernanceException("Say the objective.");
        o.Check = ComplianceChecks.All.Any(c => c.Id == input.Check) ? input.Check! : throw new GovernanceException("Name the check that measures it.");
        o.Target = input.Target is >= 1 and <= 100 ? input.Target : throw new GovernanceException("Its target is 1 to 100, the share of days the check holds.");
        o.Owner = Text(input.Owner, 256);
        if (id is null)
        {
            db.SecurityObjectives.Add(o);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "compliance.objective.set" : "compliance.objective.change", o.Title, detail: $"{o.Check}, {o.Target}%");
        return (await ObjectivesAsync(ct)).Single(x => x.Id == o.Id);
    }

    public async Task RemoveObjectiveAsync(Guid id, CancellationToken ct)
    {
        var o = await db.SecurityObjectives.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        db.SecurityObjectives.Remove(o);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.objective.remove", o.Title);
    }

    public async Task<IReadOnlyList<EvidenceRequestView>> RequestsAsync(CancellationToken ct) =>
        await db.EvidenceRequests.AsNoTracking().OrderBy(r => r.AnsweredAt != null).ThenByDescending(r => r.RequestedAt)
            .Select(r => new EvidenceRequestView(r.Id, r.Text, r.Clause, r.RequestedBy, r.RequestedAt, r.Answer, r.AnsweredBy, r.AnsweredAt)).ToListAsync(ct);

    /// <summary>An auditor asks (they may read, and ask; never change anything else).</summary>
    public async Task<EvidenceRequestView> AskAsync(string? text, string? clause, string by, CancellationToken ct)
    {
        var r = new EvidenceRequest { Text = Text(text, 4000) ?? throw new GovernanceException("Say what evidence is asked for."), Clause = Text(clause, 64), RequestedBy = by, RequestedAt = clock.GetUtcNow() };
        db.EvidenceRequests.Add(r);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.evidence.ask", r.Clause ?? "", detail: r.Text.Length > 200 ? r.Text[..200] + "…" : r.Text);
        return (await RequestsAsync(ct)).Single(x => x.Id == r.Id);
    }

    public async Task<EvidenceRequestView> AnswerAsync(Guid id, string? answer, string by, CancellationToken ct)
    {
        var r = await db.EvidenceRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        r.Answer = Text(answer, 8000) ?? throw new GovernanceException("Answer it: where the evidence is, or why there is none.");
        (r.AnsweredBy, r.AnsweredAt) = (by, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.evidence.answer", r.Clause ?? "", detail: r.Answer.Length > 200 ? r.Answer[..200] + "…" : r.Answer);
        return (await RequestsAsync(ct)).Single(x => x.Id == id);
    }

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        s = s.Trim();
        return s.Length <= max ? s : throw new GovernanceException($"{max} characters at most.");
    }
}
