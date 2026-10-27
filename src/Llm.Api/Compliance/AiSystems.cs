using Llm.Core.Access;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Compliance;

public sealed class AiSystemException(string message) : Exception(message);

public sealed record AiSystemInput(string? Name, string? Kind, string? Source, string? Purpose, string? IntendedUse, string? Affected, string? Class, string? ClassWhy,
    string? HighRiskArea, string? Role, string? Owner, string? Oversight, string? Evaluation);

public sealed record AiSystemView(Guid Id, string Name, string Kind, string? Source, string? Purpose, string? IntendedUse, string? Affected, string Class,
    string? ClassWhy, string? HighRiskArea, string Role, string? Owner, string? Oversight, string? Evaluation, string? ImpactAssessment,
    DateTimeOffset? ImpactAssessedAt, string? ImpactAssessedBy, DateTimeOffset? InUseSince, DateTimeOffset ReviewDue, bool ReviewOverdue,
    DateTimeOffset? ReviewedAt, string? ReviewedBy, DateTimeOffset? RetiredAt, IReadOnlyList<string> Missing);

public sealed record AiSystemSuggestion(string Source, string Name, string Kind);

/// <summary>
/// The register of the company's AI systems (Admin → Compliance → AI systems): each one's purpose, intended use and whom it
/// affects, its class under the EU AI Act (a prohibited practice is never put into use) and the company's role, its impact
/// assessment (ISO 42001 6.1.4; the AI Act's assessment of fundamental rights where it applies), its human oversight and
/// evaluation. A system is put into use only once what its class needs is said; reviewed each year (a high-risk one every six
/// months). The shared assistants, Agent Studio's workflows, the shared apps and the models are offered until registered.
/// </summary>
public sealed class AiSystems(AppDbContext db, Identity.Audit audit, TimeProvider clock)
{
    public static TimeSpan ReviewEvery(string cls) => cls == "high" ? TimeSpan.FromDays(182) : TimeSpan.FromDays(365);

    /// <summary>What a system still needs before it may be put into use (and while in use, if changed since).</summary>
    public static IReadOnlyList<string> Missing(AiSystem s)
    {
        var missing = new List<string>();
        if (s.Class == "prohibited")
        {
            missing.Add("a prohibited practice (Article 5) is never put into use");
        }
        if (s.Class == "unassessed")
        {
            missing.Add("its class under the AI Act");
        }
        if (s.Purpose is null)
        {
            missing.Add("its purpose");
        }
        if (s.ImpactAssessment is null)
        {
            missing.Add("its impact assessment");
        }
        if (s.Class == "high")
        {
            if (s.HighRiskArea is null)
            {
                missing.Add("its Annex III area");
            }
            if (s.Oversight is null)
            {
                missing.Add("its human oversight");
            }
            if (s.Evaluation is null)
            {
                missing.Add("its evaluation");
            }
        }
        return missing;
    }

    public async Task<IReadOnlyList<AiSystemView>> ListAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return [.. (await db.AiSystems.AsNoTracking().OrderBy(s => s.RetiredAt != null).ThenBy(s => s.Name).ToListAsync(ct)).Select(s => View(s, now))];
    }

    /// <summary>The shared assistants, the enabled workflows, the shared apps and the models, not in the register yet.</summary>
    public async Task<IReadOnlyList<AiSystemSuggestion>> SuggestAsync(CancellationToken ct)
    {
        var known = (await db.AiSystems.AsNoTracking().Where(s => s.Source != null).Select(s => s.Source!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var all = new List<AiSystemSuggestion>();
        all.AddRange((await db.Assistants.AsNoTracking().Where(a => a.Reach != Reach.Private).Select(a => new { a.Id, a.Name }).ToListAsync(ct))
            .Select(a => new AiSystemSuggestion($"assistant:{a.Id}", a.Name, "assistant")));
        all.AddRange((await db.Workflows.AsNoTracking().Where(w => w.Enabled).Select(w => new { w.Id, w.Name }).ToListAsync(ct))
            .Select(w => new AiSystemSuggestion($"workflow:{w.Id}", w.Name, "agent")));
        all.AddRange((await db.Apps.AsNoTracking().Where(a => a.Reach != Llm.Core.Apps.ArenaApp.Private).Select(a => new { a.Id, a.Name }).ToListAsync(ct))
            .Select(a => new AiSystemSuggestion($"app:{a.Id}", a.Name, "app")));
        all.AddRange((await db.LocalModels.AsNoTracking().Select(m => m.Name).ToListAsync(ct)).Select(n => new AiSystemSuggestion($"model:{n}", n, "model")));
        return [.. all.Where(s => !known.Contains(s.Source)).DistinctBy(s => s.Source)];
    }

    public async Task<AiSystemView> SaveAsync(Guid? id, AiSystemInput input, string by, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var s = id is { } i ? await db.AiSystems.SingleOrDefaultAsync(x => x.Id == i, ct) ?? throw new KeyNotFoundException() : new AiSystem { Name = "", CreatedBy = by, CreatedAt = now };
        s.Name = Text(input.Name, 200) ?? throw new AiSystemException("Name the system.");
        s.Kind = AiSystem.Kinds.Contains(input.Kind) ? input.Kind! : throw new AiSystemException($"Its kind is one of: {string.Join(", ", AiSystem.Kinds)}.");
        var cls = AiSystem.Classes.Contains(input.Class) ? input.Class! : throw new AiSystemException($"Its class is one of: {string.Join(", ", AiSystem.Classes)}.");
        s.Role = AiSystem.Roles.Contains(input.Role) ? input.Role! : throw new AiSystemException("The company's role is deployer, provider or both.");
        s.HighRiskArea = cls != "high" || input.HighRiskArea is null ? null
            : AiSystem.HighRiskAreas.Contains(input.HighRiskArea) ? input.HighRiskArea : throw new AiSystemException("Its area is one of Annex III's.");
        if (cls == "prohibited" && s.InUseSince is not null && s.RetiredAt is null)
        {
            throw new AiSystemException("A prohibited practice cannot stay in use: retire it.");
        }
        s.Class = cls;
        (s.Purpose, s.IntendedUse, s.Affected, s.ClassWhy) = (Text(input.Purpose, 2000), Text(input.IntendedUse, 2000), Text(input.Affected, 2000), Text(input.ClassWhy, 2000));
        (s.Owner, s.Oversight, s.Evaluation) = (Text(input.Owner, 256), Text(input.Oversight, 2000), Text(input.Evaluation, 2000));
        if (id is null)
        {
            s.Source = Text(input.Source, 128);
            s.ReviewDue = now + ReviewEvery(cls);
            db.AiSystems.Add(s);
        }
        else if (s.ReviewDue > now + ReviewEvery(cls))
        {
            s.ReviewDue = now + ReviewEvery(cls);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "compliance.ai.register" : "compliance.ai.change", s.Name, detail: $"{s.Kind}, {s.Class}{(s.HighRiskArea is null ? "" : $" ({s.HighRiskArea})")}, {s.Role}");
        return View(s, now);
    }

    /// <summary>Its impact assessment: on the people it touches and on society (ISO 42001 6.1.4, A.5), in the assessor's name.</summary>
    public async Task<AiSystemView> AssessAsync(Guid id, string? assessment, string by, CancellationToken ct)
    {
        var s = await db.AiSystems.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        s.ImpactAssessment = Text(assessment, 8000) ?? throw new AiSystemException("Write the assessment: whom it affects, how, and what limits the harm.");
        (s.ImpactAssessedAt, s.ImpactAssessedBy) = (clock.GetUtcNow(), by);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.ai.assess", s.Name);
        return View(s, clock.GetUtcNow());
    }

    /// <summary>Put into use, once what its class needs is said.</summary>
    public async Task<AiSystemView> PutInUseAsync(Guid id, string by, CancellationToken ct)
    {
        var s = await db.AiSystems.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (Missing(s) is { Count: > 0 } missing)
        {
            throw new AiSystemException($"Not yet: {string.Join("; ", missing)}.");
        }
        s.InUseSince ??= clock.GetUtcNow();
        s.RetiredAt = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.ai.in_use", s.Name, detail: $"{s.Class}, by {by}");
        return View(s, clock.GetUtcNow());
    }

    public async Task<AiSystemView> ReviewAsync(Guid id, string? note, string by, CancellationToken ct)
    {
        var s = await db.AiSystems.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        note = Text(note, 2000) ?? throw new AiSystemException("Say what the review found.");
        var now = clock.GetUtcNow();
        (s.ReviewedAt, s.ReviewedBy, s.ReviewDue) = (now, by, now + ReviewEvery(s.Class));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.ai.review", s.Name, detail: note);
        return View(s, now);
    }

    public async Task<AiSystemView> RetireAsync(Guid id, string by, CancellationToken ct)
    {
        var s = await db.AiSystems.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        s.RetiredAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.ai.retire", s.Name, detail: by);
        return View(s, clock.GetUtcNow());
    }

    private static AiSystemView View(AiSystem s, DateTimeOffset now) =>
        new(s.Id, s.Name, s.Kind, s.Source, s.Purpose, s.IntendedUse, s.Affected, s.Class, s.ClassWhy, s.HighRiskArea, s.Role, s.Owner, s.Oversight, s.Evaluation,
            s.ImpactAssessment, s.ImpactAssessedAt, s.ImpactAssessedBy, s.InUseSince, s.ReviewDue, s.RetiredAt is null && s.ReviewDue < now, s.ReviewedAt, s.ReviewedBy,
            s.RetiredAt, Missing(s));

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        s = s.Trim();
        return s.Length <= max ? s : throw new AiSystemException($"{max} characters at most.");
    }
}
