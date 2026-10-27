using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Compliance;

public sealed class RiskException(string message) : Exception(message);

/// <summary>Something a risk can threaten, and the kind it is.</summary>
public sealed record RiskAsset(string Group, string Name);

/// <summary>What a risk is said to be: its fields as a person enters them.</summary>
public sealed record RiskInput(string? Title, string? Description, string? Category, string[]? Assets, string? Threat, int Likelihood, int Impact,
    string? Owner, string? Treatment, string[]? Controls, string? Plan, int? ResidualLikelihood, int? ResidualImpact);

/// <summary>A risk as the register shows it: its levels, and what is due.</summary>
public sealed record RiskView(Guid Id, string Title, string? Description, string Category, string[] Assets, string? Threat, int Likelihood, int Impact,
    int Score, string Level, string? Owner, string Treatment, string[] Controls, string? Plan, int? ResidualLikelihood, int? ResidualImpact, int? ResidualScore,
    string? ResidualLevel, string? AcceptedBy, string? AcceptedWhy, DateTimeOffset? AcceptedUntil, DateTimeOffset ReviewDue, bool ReviewOverdue,
    DateTimeOffset? ClosedAt, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int OpenActions, int LateActions);

/// <summary>
/// The risk register (Admin → Compliance → Risks): risks to the company's information and its AI, each on a 5 by 5
/// scale before and after treatment, owned, treated (reduce with Arena's controls, accept with who, why and until
/// when, transfer, avoid), with the treatment's actions and a review date that its level sets. Every change is in
/// its history and the audit log.
/// </summary>
public sealed class Risks(AppDbContext db, Identity.Audit audit, TimeProvider clock)
{
    public static readonly string[] Categories = ["security", "privacy", "ai", "continuity", "supplier", "legal", "other"];

    public static readonly string[] Treatments = [Risk.Reduce, Risk.Accept, Risk.Transfer, Risk.Avoid];

    /// <summary>An acceptance counts for a year at most: then the risk is decided again.</summary>
    public static readonly TimeSpan AcceptanceLasts = TimeSpan.FromDays(365);

    /// <summary>The level of likelihood times impact: low 1–4, medium 5–9, high 10–16, critical 20–25.</summary>
    public static string Level(int score) => score switch { >= 20 => "critical", >= 10 => "high", >= 5 => "medium", _ => "low" };

    /// <summary>How long until it is looked at again, by its level (the residual one once assessed): 3 months for critical, 6 for high, a year otherwise.</summary>
    public static TimeSpan ReviewEvery(string level) => level switch { "critical" => TimeSpan.FromDays(91), "high" => TimeSpan.FromDays(182), _ => TimeSpan.FromDays(365) };

    public static RiskView View(Risk r, IReadOnlyCollection<RiskAction> actions, DateTimeOffset now)
    {
        var residual = r.ResidualLikelihood is { } l && r.ResidualImpact is { } i ? l * i : (int?)null;
        return new RiskView(r.Id, r.Title, r.Description, r.Category, r.Assets, r.Threat, r.Likelihood, r.Impact, r.Likelihood * r.Impact, Level(r.Likelihood * r.Impact),
            r.Owner, r.Treatment, r.Controls, r.Plan, r.ResidualLikelihood, r.ResidualImpact, residual, residual is { } s ? Level(s) : null, r.AcceptedBy, r.AcceptedWhy,
            r.AcceptedUntil, r.ReviewDue, r.ClosedAt is null && r.ReviewDue < now, r.ClosedAt, r.CreatedBy, r.CreatedAt, r.UpdatedAt,
            actions.Count(a => a.DoneAt is null), actions.Count(a => a.DoneAt is null && a.Due < now));
    }

    public async Task<IReadOnlyList<RiskView>> ListAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var risks = await db.Risks.AsNoTracking().OrderBy(r => r.ClosedAt != null).ThenByDescending(r => r.Likelihood * r.Impact).ThenBy(r => r.Title).ToListAsync(ct);
        var actions = (await db.RiskActions.AsNoTracking().ToListAsync(ct)).ToLookup(a => a.RiskId);
        return [.. risks.Select(r => View(r, [.. actions[r.Id]], now))];
    }

    public async Task<(RiskView Risk, List<RiskAction> Actions, List<RiskEvent> Events)> GetAsync(Guid id, CancellationToken ct)
    {
        var risk = await db.Risks.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
        var actions = await db.RiskActions.AsNoTracking().Where(a => a.RiskId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var events = await db.RiskEvents.AsNoTracking().Where(e => e.RiskId == id).OrderBy(e => e.At).ToListAsync(ct);
        return (View(risk, actions, clock.GetUtcNow()), actions, events);
    }

    public async Task<RiskView> CreateAsync(RiskInput input, string by, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var risk = new Risk { Title = "", CreatedBy = by, CreatedAt = now, UpdatedAt = now };
        Apply(risk, input);
        risk.ReviewDue = now + ReviewEvery(CurrentLevel(risk));
        db.Risks.Add(risk);
        db.RiskEvents.Add(new RiskEvent { RiskId = risk.Id, At = now, By = by, Kind = "created", Text = Summary(risk) });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.risk.create", risk.Title, detail: Summary(risk));
        return View(risk, [], now);
    }

    public async Task<RiskView> UpdateAsync(Guid id, RiskInput input, string by, CancellationToken ct)
    {
        var risk = await db.Risks.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
        var before = Summary(risk);
        var treatment = risk.Treatment;
        Apply(risk, input);
        // Another treatment than acceptance: the acceptance no longer holds.
        if (treatment == Risk.Accept && risk.Treatment != Risk.Accept)
        {
            (risk.AcceptedBy, risk.AcceptedWhy, risk.AcceptedUntil) = (null, null, null);
        }
        var now = clock.GetUtcNow();
        risk.UpdatedAt = now;
        var after = Summary(risk);
        if (after != before)
        {
            db.RiskEvents.Add(new RiskEvent { RiskId = id, At = now, By = by, Kind = "changed", Text = after });
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.risk.change", risk.Title, detail: after);
        return await ViewAsync(risk, ct);
    }

    /// <summary>Looked at again: what was found, and the next review its level sets (or the date given, never later than that).</summary>
    public async Task<RiskView> ReviewAsync(Guid id, string? note, DateTimeOffset? next, string by, CancellationToken ct)
    {
        var risk = await Open(id, ct);
        note = Text(note, 2000) ?? throw new RiskException("Say what the review found.");
        var now = clock.GetUtcNow();
        var latest = now + ReviewEvery(CurrentLevel(risk));
        if (next is { } n && (n <= now || n > latest))
        {
            throw new RiskException($"The next review is after today and by {latest:yyyy-MM-dd}, which its level allows.");
        }
        risk.ReviewDue = next ?? latest;
        risk.UpdatedAt = now;
        db.RiskEvents.Add(new RiskEvent { RiskId = id, At = now, By = by, Kind = "reviewed", Text = $"{note} Next review: {risk.ReviewDue:yyyy-MM-dd}." });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.risk.review", risk.Title, detail: note);
        return await ViewAsync(risk, ct);
    }

    /// <summary>Accepted as it is left, by the person who decides it: why, and until when (a year at most).</summary>
    public async Task<RiskView> AcceptAsync(Guid id, string? why, DateTimeOffset? until, string by, CancellationToken ct)
    {
        var risk = await Open(id, ct);
        why = Text(why, 2000) ?? throw new RiskException("Say why it is accepted.");
        var now = clock.GetUtcNow();
        var end = until ?? now + AcceptanceLasts;
        if (end <= now || end > now + AcceptanceLasts)
        {
            throw new RiskException("An acceptance lasts until a date within the next year.");
        }
        (risk.Treatment, risk.AcceptedBy, risk.AcceptedWhy, risk.AcceptedUntil) = (Risk.Accept, by, why, end);
        // Decided again when the acceptance ends, at the latest.
        if (risk.ReviewDue > end)
        {
            risk.ReviewDue = end;
        }
        risk.UpdatedAt = now;
        db.RiskEvents.Add(new RiskEvent { RiskId = id, At = now, By = by, Kind = "accepted", Text = $"Until {end:yyyy-MM-dd}: {why}" });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.risk.accept", risk.Title, detail: $"until {end:yyyy-MM-dd}: {why}");
        return await ViewAsync(risk, ct);
    }

    public async Task<RiskView> CloseAsync(Guid id, bool close, string? why, string by, CancellationToken ct)
    {
        var risk = await db.Risks.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
        why = Text(why, 2000) ?? throw new RiskException(close ? "Say why it no longer applies." : "Say why it applies again.");
        if (close == risk.ClosedAt is not null)
        {
            throw new RiskException(close ? "It is closed already." : "It is open.");
        }
        var now = clock.GetUtcNow();
        risk.ClosedAt = close ? now : null;
        if (!close)
        {
            risk.ReviewDue = now + ReviewEvery(CurrentLevel(risk));
        }
        risk.UpdatedAt = now;
        db.RiskEvents.Add(new RiskEvent { RiskId = id, At = now, By = by, Kind = close ? "closed" : "reopened", Text = why });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(close ? "compliance.risk.close" : "compliance.risk.reopen", risk.Title, detail: why);
        return await ViewAsync(risk, ct);
    }

    public async Task<RiskAction> AddActionAsync(Guid id, string? text, string? owner, DateTimeOffset? due, string by, CancellationToken ct)
    {
        var risk = await Open(id, ct);
        var action = new RiskAction
        {
            RiskId = id, Text = Text(text, 2000) ?? throw new RiskException("Say what is to be done."), Owner = Text(owner, 256), Due = due, CreatedAt = clock.GetUtcNow(),
        };
        db.RiskActions.Add(action);
        var who = string.Join(", ", new[] { action.Owner, due is { } d ? $"by {d:yyyy-MM-dd}" : null }.OfType<string>());
        db.RiskEvents.Add(new RiskEvent { RiskId = id, At = action.CreatedAt, By = by, Kind = "action", Text = who.Length > 0 ? $"{action.Text} ({who})" : action.Text });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.risk.action", risk.Title, detail: action.Text);
        return action;
    }

    public async Task<RiskAction> CompleteActionAsync(Guid id, Guid actionId, string by, CancellationToken ct)
    {
        var action = await db.RiskActions.SingleOrDefaultAsync(a => a.Id == actionId && a.RiskId == id, ct) ?? throw new KeyNotFoundException();
        if (action.DoneAt is not null)
        {
            throw new RiskException("It is done already.");
        }
        (action.DoneAt, action.DoneBy) = (clock.GetUtcNow(), by);
        db.RiskEvents.Add(new RiskEvent { RiskId = id, At = action.DoneAt.Value, By = by, Kind = "action_done", Text = action.Text });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.risk.action_done", id.ToString(), detail: action.Text);
        return action;
    }

    /// <summary>What a risk can name as its assets: what Arena knows of (its stores, models, knowledge, data sources and tools).</summary>
    public async Task<IReadOnlyList<RiskAsset>> AssetsAsync(CancellationToken ct)
    {
        List<RiskAsset> all =
        [
            .. Stores.Select(s => new RiskAsset("Arena", s)),
            .. (await db.LocalModels.AsNoTracking().Select(m => m.Name).ToListAsync(ct)).Order(StringComparer.Ordinal).Select(n => new RiskAsset("Models", n)),
            .. (await db.MediaModels.AsNoTracking().Select(m => m.Name).ToListAsync(ct)).Order(StringComparer.Ordinal).Select(n => new RiskAsset("Models", n)),
            .. (await db.KnowledgeSources.AsNoTracking().Select(k => k.Name).ToListAsync(ct)).Order(StringComparer.Ordinal).Select(n => new RiskAsset("Knowledge", n)),
            .. (await db.DataSources.AsNoTracking().Select(d => d.Name).ToListAsync(ct)).Order(StringComparer.Ordinal).Select(n => new RiskAsset("Data sources", n)),
            .. (await db.McpServers.AsNoTracking().Select(m => m.Name).ToListAsync(ct)).Order(StringComparer.Ordinal).Select(n => new RiskAsset("Tools", n)),
        ];
        return [.. all.DistinctBy(a => a.Name)];
    }

    /// <summary>The stack's own stores of information, always there.</summary>
    public static readonly string[] Stores =
    [
        "Arena's database (Postgres)", "People's files", "The model library", "The backups", "The audit log", "Argus's code index", "The gateway (LiteLLM)",
        "Company sign-in (OIDC, SAML, LDAP)", "Cloud model providers",
    ];

    /// <summary>The level that sets its reviews: the residual one once assessed, else the inherent one.</summary>
    private static string CurrentLevel(Risk r) => Level(r.ResidualLikelihood is { } l && r.ResidualImpact is { } i ? l * i : r.Likelihood * r.Impact);

    private async Task<Risk> Open(Guid id, CancellationToken ct)
    {
        var risk = await db.Risks.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
        return risk.ClosedAt is null ? risk : throw new RiskException("It is closed: reopen it first.");
    }

    private async Task<RiskView> ViewAsync(Risk risk, CancellationToken ct) =>
        View(risk, await db.RiskActions.AsNoTracking().Where(a => a.RiskId == risk.Id).ToListAsync(ct), clock.GetUtcNow());

    private static void Apply(Risk risk, RiskInput input)
    {
        risk.Title = Text(input.Title, 300) ?? throw new RiskException("Name the risk.");
        risk.Description = Text(input.Description, 4000);
        risk.Category = Categories.Contains(input.Category) ? input.Category! : throw new RiskException($"Its category is one of: {string.Join(", ", Categories)}.");
        risk.Treatment = Treatments.Contains(input.Treatment) ? input.Treatment! : throw new RiskException($"Its treatment is one of: {string.Join(", ", Treatments)}.");
        if (input.Likelihood is < 1 or > 5 || input.Impact is < 1 or > 5)
        {
            throw new RiskException("Likelihood and impact are each 1 to 5.");
        }
        if ((input.ResidualLikelihood is null) != (input.ResidualImpact is null) || input.ResidualLikelihood is < 1 or > 5 || input.ResidualImpact is < 1 or > 5)
        {
            throw new RiskException("What is left after treatment is a likelihood and an impact, each 1 to 5, or neither yet.");
        }
        (risk.Likelihood, risk.Impact, risk.ResidualLikelihood, risk.ResidualImpact) = (input.Likelihood, input.Impact, input.ResidualLikelihood, input.ResidualImpact);
        var assets = (input.Assets ?? []).Select(a => Text(a, 200)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        risk.Assets = assets.Length <= 30 ? assets : throw new RiskException("30 assets at most: name what it threatens most.");
        var known = ComplianceControls.All.Controls.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var controls = (input.Controls ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        risk.Controls = controls.FirstOrDefault(c => !known.Contains(c)) is { } unknown ? throw new RiskException($"No control {unknown} (Admin → Compliance lists them).") : controls;
        risk.Threat = Text(input.Threat, 2000);
        risk.Owner = Text(input.Owner, 256);
        risk.Plan = Text(input.Plan, 4000);
    }

    private static string Summary(Risk r) =>
        $"{r.Category}, {r.Likelihood}×{r.Impact} ({Level(r.Likelihood * r.Impact)}), {r.Treatment}" +
        (r.ResidualLikelihood is { } l && r.ResidualImpact is { } i ? $", left {l}×{i} ({Level(l * i)})" : "") +
        (r.Controls.Length > 0 ? $", controls {string.Join(" ", r.Controls)}" : "") + (r.Owner is null ? "" : $", owner {r.Owner}");

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        s = s.Trim();
        return s.Length <= max ? s : throw new RiskException($"{max} characters at most.");
    }
}
