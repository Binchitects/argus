using System.Globalization;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <param name="Start">"HH:mm", in the working hours' time zone.</param>
/// <param name="End">"HH:mm"; before the start, it runs past midnight; equal, all day.</param>
/// <param name="DefaultModel">The model new chats use meanwhile; "": the usual.</param>
public sealed record ModelWindowRequest(string? Name = null, bool? Enabled = null, List<int>? Days = null, string? Start = null, string? End = null,
    List<string>? Keep = null, string? DefaultModel = null, int? Order = null);

/// <summary>Admin → Models → Working hours: which models are kept loaded, and which one new chats use, by day and hour.</summary>
public static class ModelHoursEndpoints
{
    public static void MapModelHours(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/model-hours").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapPost("", AddAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db, ModelHours hours, TimeProvider clock, IOptionsMonitor<ModelHoursOptions> options,
        IOptions<EngineOptions> engine, CancellationToken ct)
    {
        var now = await hours.RefreshAsync(ct);
        var (zone, _) = Hours.Zone(options.CurrentValue.TimeZone);
        var windows = await db.ModelWindows.AsNoTracking().OrderBy(w => w.Order).ThenBy(w => w.CreatedAt).ToListAsync(ct);
        return Results.Ok(new
        {
            timeZone = zone.Id,
            problem = now.Problem,
            localNow = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            active = now.Window is { } w ? new { w.Id, w.Name, until = now.Until } : null,
            max = engine.Value.ModelsMax,
            windows = windows.Select(View),
        });
    }

    private static object View(ModelWindow w) => new
    {
        w.Id, w.Name, w.Enabled, days = w.Days.Order(), start = w.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
        end = w.End.ToString("HH:mm", CultureInfo.InvariantCulture), w.Keep, w.DefaultModel, w.Order,
    };

    private static async Task<IResult> AddAsync(ModelWindowRequest body, AppDbContext db, ModelHours hours, EngineWatcher watcher, ChatModels chatModels,
        ModelLibrary library, HardwareProbe hardware, IOptions<EngineOptions> engine, Audit audit, CancellationToken ct)
    {
        var window = new ModelWindow { Name = "", Order = (await db.ModelWindows.MaxAsync(w => (int?)w.Order, ct) ?? 0) + 1 };
        if (await ApplyAsync(window, body, db, chatModels, library, hardware, engine.Value, creating: true, ct) is { } problem)
        {
            return problem;
        }
        db.ModelWindows.Add(window);
        await db.SaveChangesAsync(ct);
        await AfterChangeAsync(hours, watcher, chatModels, ct);
        await audit.WriteAsync("model.hours_add", window.Name, detail: Describe(window));
        return Results.Created($"/api/admin/model-hours/{window.Id}", View(window));
    }

    private static async Task<IResult> UpdateAsync(Guid id, ModelWindowRequest body, AppDbContext db, ModelHours hours, EngineWatcher watcher, ChatModels chatModels,
        ModelLibrary library, HardwareProbe hardware, IOptions<EngineOptions> engine, Audit audit, CancellationToken ct)
    {
        if (await db.ModelWindows.SingleOrDefaultAsync(w => w.Id == id, ct) is not { } window)
        {
            return Results.NotFound();
        }
        if (await ApplyAsync(window, body, db, chatModels, library, hardware, engine.Value, creating: false, ct) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        await AfterChangeAsync(hours, watcher, chatModels, ct);
        await audit.WriteAsync("model.hours_update", window.Name, detail: Describe(window));
        return Results.Ok(View(window));
    }

    private static async Task<IResult> RemoveAsync(Guid id, AppDbContext db, ModelHours hours, EngineWatcher watcher, ChatModels chatModels, Audit audit, CancellationToken ct)
    {
        if (await db.ModelWindows.SingleOrDefaultAsync(w => w.Id == id, ct) is not { } window)
        {
            return Results.NotFound();
        }
        db.ModelWindows.Remove(window);
        await db.SaveChangesAsync(ct);
        await AfterChangeAsync(hours, watcher, chatModels, ct);
        await audit.WriteAsync("model.hours_remove", window.Name);
        return Results.NoContent();
    }

    /// <summary>In force at once: the state is read again, the engine watcher looks now, the chat's model list is fetched afresh.</summary>
    private static async Task AfterChangeAsync(ModelHours hours, EngineWatcher watcher, ChatModels chatModels, CancellationToken ct)
    {
        await hours.RefreshAsync(ct);
        chatModels.Forget();
        watcher.Wake();
    }

    private static string Describe(ModelWindow w) =>
        $"{(w.Enabled ? "" : "off, ")}{string.Join(",", w.Days.Order())} {w.Start:HH\\:mm}-{w.End:HH\\:mm}, keeps {(w.Keep.Count == 0 ? "none" : string.Join(", ", w.Keep))}{(w.DefaultModel is { } d ? $", chats start on {d}" : "")}";

    private static async Task<IResult?> ApplyAsync(ModelWindow w, ModelWindowRequest body, AppDbContext db, ChatModels chatModels, ModelLibrary library,
        HardwareProbe hardware, EngineOptions e, bool creating, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? w.Name;
        if (name.Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "Working hours need a name of up to 100 characters, e.g. Busy hours.");
        }
        if ((await db.ModelWindows.Where(x => x.Id != w.Id).Select(x => x.Name).ToListAsync(ct)).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            return AuthEndpoints.Problem(409, "exists", $"There are already working hours named {name}.");
        }
        var days = body.Days ?? w.Days;
        if (days.Count == 0 || days.Any(d => d is < 1 or > 7))
        {
            return AuthEndpoints.Problem(400, "days", "Choose at least one day (Monday 1 to Sunday 7).");
        }
        if (!Time(body.Start, w.Start, creating, out var start) || !Time(body.End, w.End, creating, out var end))
        {
            return AuthEndpoints.Problem(400, "time", "From and until are times of day, e.g. 08:00 and 18:00.");
        }
        var keep = (body.Keep ?? w.Keep).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var local = await db.LocalModels.AsNoTracking().ToListAsync(ct);
        var onEngine = local.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        if (keep.Count > e.ModelsMax)
        {
            return AuthEndpoints.Problem(409, "full", $"The engine holds {e.ModelsMax} model{(e.ModelsMax == 1 ? "" : "s")} at once: keep at most that many, or raise \"Models loaded at once\" under Settings.");
        }
        if (keep.FirstOrDefault(k => !onEngine.Contains(k)) is { } unknown)
        {
            return AuthEndpoints.Problem(400, "keep", $"{unknown} is not a model of this machine's engine: only those can be kept loaded.");
        }
        if (keep.Count > 0 && ModelEndpoints.Plan(keep, local, e, library, await hardware.GetAsync(ct)).FirstError is { } error)
        {
            return AuthEndpoints.Problem(409, "memory", error.Message);
        }
        var chosen = body.DefaultModel is null ? w.DefaultModel : body.DefaultModel.Trim() is { Length: > 0 } dm ? dm : null;
        if (chosen is not null && !onEngine.Contains(chosen) && (await chatModels.AllAsync(ct)).All(m => m.Name != chosen))
        {
            return AuthEndpoints.Problem(400, "default", $"There is no model named {chosen}.");
        }
        w.Name = name;
        w.Enabled = body.Enabled ?? w.Enabled;
        w.Days = [.. days.Distinct().Order()];
        w.Start = start;
        w.End = end;
        w.Keep = keep;
        w.DefaultModel = chosen;
        w.Order = body.Order ?? w.Order;
        return null;
    }

    private static bool Time(string? text, TimeOnly current, bool required, out TimeOnly value)
    {
        if (text is null)
        {
            value = current;
            return !required;
        }
        return TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
