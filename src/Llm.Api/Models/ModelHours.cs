using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>Configuration section "ModelHours".</summary>
public sealed class ModelHoursOptions
{
    /// <summary>The clock working hours follow: an IANA name (Asia/Tehran, Europe/Berlin).</summary>
    public string TimeZone { get; set; } = "UTC";
}

/// <summary>Which working-hours window applies when (pure: the clock and the zone are given).</summary>
public static class Hours
{
    /// <summary>The zone by its IANA name; an unknown one is UTC (and says so).</summary>
    public static (TimeZoneInfo Zone, string? Problem) Zone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (TimeZoneInfo.Utc, null);
        }
        try
        {
            return (TimeZoneInfo.FindSystemTimeZoneById(name.Trim()), null);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return (TimeZoneInfo.Utc, $"The time zone \"{name}\" is unknown, so working hours follow UTC. Set an IANA name such as Europe/Berlin under Settings.");
        }
    }

    /// <summary>The window in force at <paramref name="now"/>, and when it ends; the lowest Order wins an overlap.</summary>
    public static (ModelWindow? Window, DateTimeOffset? Until) Active(IEnumerable<ModelWindow> windows, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        foreach (var w in windows.Where(w => w.Enabled && w.Days.Count > 0).OrderBy(w => w.Order).ThenBy(w => w.CreatedAt))
        {
            if (StartedOn(w, local) is { } day)
            {
                return (w, Ends(w, day, zone));
            }
        }
        return (null, null);
    }

    /// <summary>The local date the window's current run began on, if it is running at <paramref name="local"/>.</summary>
    private static DateOnly? StartedOn(ModelWindow w, DateTimeOffset local)
    {
        var today = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);
        bool On(DateOnly d) => w.Days.Contains(Iso(d.DayOfWeek));
        if (w.Start == w.End)
        {
            return On(today) ? today : null;
        }
        if (w.Start < w.End)
        {
            return On(today) && time >= w.Start && time < w.End ? today : null;
        }
        // Past midnight: from the start to midnight on its day, and on to the end the next morning.
        if (time >= w.Start && On(today))
        {
            return today;
        }
        var yesterday = today.AddDays(-1);
        return time < w.End && On(yesterday) ? yesterday : null;
    }

    private static DateTimeOffset Ends(ModelWindow w, DateOnly started, TimeZoneInfo zone)
    {
        var endDay = w.Start < w.End ? started : started.AddDays(1);
        var local = endDay.ToDateTime(w.End);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static int Iso(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;
}

/// <summary>The window in force now, as the engine watcher last found it (every few seconds, and when an admin changes one).</summary>
public sealed class ModelHoursState
{
    private volatile Snapshot _now = new(null, null, null);

    public sealed record Snapshot(ModelWindow? Window, DateTimeOffset? Until, string? Problem);

    public Snapshot Now => _now;

    public void Set(Snapshot now) => _now = now;
}

/// <summary>Finds the window in force (from the database and the clock) and records it for the engine, the gateway and the chat.</summary>
public sealed class ModelHours(AppDbContext db, ModelHoursState state, TimeProvider clock, IOptionsMonitor<ModelHoursOptions> options)
{
    public async Task<ModelHoursState.Snapshot> RefreshAsync(CancellationToken ct)
    {
        var (zone, problem) = Hours.Zone(options.CurrentValue.TimeZone);
        var windows = await db.ModelWindows.AsNoTracking().ToListAsync(ct);
        var (window, until) = Hours.Active(windows, clock.GetUtcNow(), zone);
        var now = new ModelHoursState.Snapshot(window, until, problem);
        state.Set(now);
        return now;
    }
}
