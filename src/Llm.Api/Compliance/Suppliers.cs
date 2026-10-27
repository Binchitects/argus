using Llm.Core.Compliance;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Compliance;

public sealed class SupplierException(string message) : Exception(message);

public sealed record SupplierInput(string? Name, string? Kind, string? Service, string[]? Data, string? Location, string? Certifications, string? Agreements,
    string? Risk, string? Owner, string? Notes, string? Source);

public sealed record SupplierView(Guid Id, string Name, string Kind, string? Service, string[] Data, string? Location, string? Certifications, string? Agreements,
    string Risk, string? Owner, string? Notes, string? Source, DateTimeOffset ReviewDue, bool ReviewOverdue, DateTimeOffset? ReviewedAt, string? ReviewedBy,
    DateTimeOffset? RetiredAt, bool AgreementMissing);

/// <summary>A service Arena connects to that the register does not hold yet.</summary>
public sealed record SupplierSuggestion(string Source, string Name, string Kind, string Service);

/// <summary>
/// The supplier register (Admin → Compliance → Suppliers): each supplier with the data it receives, where, its certifications
/// and agreements, its risk and a review its risk sets (six months high, a year medium, two years low); and the services
/// Arena connects to (cloud model servers, tool servers, knowledge connectors, the GitLab Argus reads, Hugging Face) that
/// it does not hold yet.
/// </summary>
public sealed class Suppliers(AppDbContext db, IOptionsMonitor<Operations.ArgusOptions> argus, Identity.Audit audit, TimeProvider clock)
{
    public static readonly string[] Risks = ["low", "medium", "high"];

    public static TimeSpan ReviewEvery(string risk) => risk switch { "high" => TimeSpan.FromDays(182), "low" => TimeSpan.FromDays(730), _ => TimeSpan.FromDays(365) };

    /// <summary>A supplier that gets personal or health data with no agreement said (a DPA; a BAA for health data).</summary>
    public static bool AgreementMissing(Supplier s) =>
        (s.Data.Contains("personal") || s.Data.Contains("health")) && string.IsNullOrWhiteSpace(s.Agreements)
        || s.Data.Contains("health") && !(s.Agreements ?? "").Contains("BAA", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SupplierView>> ListAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return [.. (await db.Suppliers.AsNoTracking().OrderBy(s => s.RetiredAt != null).ThenBy(s => s.Name).ToListAsync(ct)).Select(s => View(s, now))];
    }

    /// <summary>What Arena connects to, not in the register (nor retired from it).</summary>
    public async Task<IReadOnlyList<SupplierSuggestion>> SuggestAsync(CancellationToken ct)
    {
        var known = (await db.Suppliers.AsNoTracking().Where(s => s.Source != null).Select(s => s.Source!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var all = new List<SupplierSuggestion>();
        foreach (var r in await db.RemoteServers.AsNoTracking().ToListAsync(ct))
        {
            all.Add(new($"remote-server:{r.Id}", r.Name, "model", $"Models served at {Host(r.BaseUrl)}"));
        }
        foreach (var m in await db.McpServers.AsNoTracking().ToListAsync(ct))
        {
            all.Add(new($"mcp:{m.Id}", m.Name, "connector", $"Tools at {Host(m.Url)}"));
        }
        foreach (var k in await db.KnowledgeSources.AsNoTracking().Where(k => k.Kind != "folder").ToListAsync(ct))
        {
            all.Add(new($"knowledge:{k.Id}", k.Name, "connector", $"Company knowledge from {k.Kind} ({Host(k.Location.Split('\n')[0])})"));
        }
        if (argus.CurrentValue.GitlabUrl is { Length: > 0 } gitlab)
        {
            all.Add(new("gitlab", $"GitLab ({Host(gitlab)})", "software", "The code Argus indexes, read with a read-only token"));
        }
        all.Add(new("huggingface", "Hugging Face", "model", "The models an admin downloads (Admin → Models)"));
        return [.. all.Where(s => !known.Contains(s.Source))];
    }

    public async Task<SupplierView> SaveAsync(Guid? id, SupplierInput input, string by, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var s = id is { } i ? await db.Suppliers.SingleOrDefaultAsync(x => x.Id == i, ct) ?? throw new KeyNotFoundException() : new Supplier { Name = "", CreatedBy = by, CreatedAt = now };
        s.Name = Text(input.Name, 200) ?? throw new SupplierException("Name the supplier.");
        s.Kind = Supplier.Kinds.Contains(input.Kind) ? input.Kind! : throw new SupplierException($"Its kind is one of: {string.Join(", ", Supplier.Kinds)}.");
        var risk = Risks.Contains(input.Risk) ? input.Risk! : throw new SupplierException("Its risk is low, medium or high.");
        var data = (input.Data ?? []).Distinct().ToArray();
        s.Data = data.FirstOrDefault(d => !Supplier.DataKinds.Contains(d)) is { } bad ? throw new SupplierException($"No kind of data {bad}.") : data;
        (s.Service, s.Location, s.Certifications, s.Agreements) = (Text(input.Service, 1000), Text(input.Location, 200), Text(input.Certifications, 2000), Text(input.Agreements, 2000));
        (s.Owner, s.Notes) = (Text(input.Owner, 256), Text(input.Notes, 4000));
        if (id is null)
        {
            s.Source = Text(input.Source, 128);
            s.ReviewDue = now + ReviewEvery(risk);
            db.Suppliers.Add(s);
        }
        else if (risk != s.Risk && s.ReviewDue > now + ReviewEvery(risk))
        {
            // A higher risk is looked at sooner.
            s.ReviewDue = now + ReviewEvery(risk);
        }
        s.Risk = risk;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "compliance.supplier.add" : "compliance.supplier.change", s.Name,
            detail: $"{s.Kind}, risk {s.Risk}{(s.Data.Length > 0 ? $", gets {string.Join(" ", s.Data)} data" : "")}");
        return View(s, now);
    }

    /// <summary>Reviewed: what was found (its certifications and agreements still hold), and the next review its risk sets.</summary>
    public async Task<SupplierView> ReviewAsync(Guid id, string? note, string by, CancellationToken ct)
    {
        var s = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        note = Text(note, 2000) ?? throw new SupplierException("Say what the review found.");
        var now = clock.GetUtcNow();
        (s.ReviewedAt, s.ReviewedBy, s.ReviewDue) = (now, by, now + ReviewEvery(s.Risk));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.supplier.review", s.Name, detail: note);
        return View(s, now);
    }

    /// <summary>No longer used: kept in the register as retired.</summary>
    public async Task<SupplierView> RetireAsync(Guid id, string by, CancellationToken ct)
    {
        var s = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        s.RetiredAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.supplier.retire", s.Name, detail: by);
        return View(s, clock.GetUtcNow());
    }

    private static SupplierView View(Supplier s, DateTimeOffset now) =>
        new(s.Id, s.Name, s.Kind, s.Service, s.Data, s.Location, s.Certifications, s.Agreements, s.Risk, s.Owner, s.Notes, s.Source, s.ReviewDue,
            s.RetiredAt is null && s.ReviewDue < now, s.ReviewedAt, s.ReviewedBy, s.RetiredAt, s.RetiredAt is null && AgreementMissing(s));

    private static string Host(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? u.Host : url.Trim();

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        s = s.Trim();
        return s.Length <= max ? s : throw new SupplierException($"{max} characters at most.");
    }
}
