using System.Globalization;

namespace Argus.Indexing;

/// <summary>How a repository is brought up to date by itself.</summary>
public enum ScheduleKind
{
    /// <summary>With each scheduled pass over every repository (the app's schedule, or ARGUS_INDEX_INTERVAL).</summary>
    Pass,
    /// <summary>Never by itself: only on a push or merge, or when someone asks.</summary>
    Off,
    Hours,
    Daily,
    Weekly,
}

/// <summary>
/// A repository's schedule as it is stored ("pass", "off", "hours:6", "daily:02:30",
/// "weekly:1:02:30", the day 1 for Monday to 7 for Sunday), in words, and when it next
/// comes round in the index's time zone.
/// </summary>
public sealed record RepoSchedule(ScheduleKind Kind, int Hours = 0, int Day = 0, int Hour = 0, int Minute = 0)
{
    /// <summary>What a repository does when nobody chose: as before schedules, with each pass.</summary>
    public static readonly RepoSchedule WithPass = new(ScheduleKind.Pass);
    public const int MaxHours = 168;
    static readonly string[] DayNames = ["Mondays", "Tuesdays", "Wednesdays", "Thursdays", "Fridays", "Saturdays", "Sundays"];

    /// <summary>A stored or submitted schedule; the problem in words when it is not one.</summary>
    public static bool TryParse(string? text, out RepoSchedule schedule, out string? problem)
    {
        schedule = WithPass;
        problem = null;
        var t = (text ?? "").Trim().ToLowerInvariant();
        var parts = t.Split(':');
        bool Time(string h, string m, out int hour, out int minute) =>
            int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out hour) & int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out minute)
            && hour is >= 0 and < 24 && minute is >= 0 and < 60;
        switch (parts[0])
        {
            case "pass" when parts.Length == 1:
                return true;
            case "off" when parts.Length == 1:
                schedule = new RepoSchedule(ScheduleKind.Off);
                return true;
            case "hours" when parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxHours:
                schedule = new RepoSchedule(ScheduleKind.Hours, Hours: n);
                return true;
            case "daily" when parts.Length == 3 && Time(parts[1], parts[2], out var h, out var m):
                schedule = new RepoSchedule(ScheduleKind.Daily, Hour: h, Minute: m);
                return true;
            case "weekly" when parts.Length == 4 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 7
                               && Time(parts[2], parts[3], out var wh, out var wm):
                schedule = new RepoSchedule(ScheduleKind.Weekly, Day: d, Hour: wh, Minute: wm);
                return true;
        }
        problem = $"not a schedule: {text}. Use pass, off, hours:N (1 to {MaxHours}), daily:HH:MM or weekly:D:HH:MM (D from 1 for Monday to 7 for Sunday)";
        return false;
    }

    /// <summary>A stored schedule; one that cannot be read is taken as the default (with each pass).</summary>
    public static RepoSchedule Parse(string? text) => TryParse(text, out var s, out _) ? s : WithPass;

    /// <summary>The stored form.</summary>
    public override string ToString() => Kind switch
    {
        ScheduleKind.Off => "off",
        ScheduleKind.Hours => $"hours:{Hours}",
        ScheduleKind.Daily => $"daily:{Hour:00}:{Minute:00}",
        ScheduleKind.Weekly => $"weekly:{Day}:{Hour:00}:{Minute:00}",
        _ => "pass",
    };

    /// <summary>In words: "Every 6 hours", "Every day at 02:30", "Mondays at 02:30".</summary>
    public string Words => Kind switch
    {
        ScheduleKind.Off => "Off: only pushes and when asked",
        ScheduleKind.Hours => Hours == 1 ? "Every hour" : $"Every {Hours} hours",
        ScheduleKind.Daily => $"Every day at {Hour:00}:{Minute:00}",
        ScheduleKind.Weekly => $"{DayNames[Day - 1]} at {Hour:00}:{Minute:00}",
        _ => "With each scheduled pass",
    };

    /// <summary>Whether Argus's own scheduler runs it (the others go with the passes, or only on pushes and when asked).</summary>
    public bool Timed => Kind is ScheduleKind.Hours or ScheduleKind.Daily or ScheduleKind.Weekly;

    /// <summary>The longest a repository on this schedule goes between runs by design; null when it has no such bound.</summary>
    public TimeSpan? Period => Kind switch
    {
        ScheduleKind.Hours => TimeSpan.FromHours(Hours),
        ScheduleKind.Daily => TimeSpan.FromDays(1),
        ScheduleKind.Weekly => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>
    /// When it next comes round: the first time after <paramref name="anchor"/> (when its schedule last
    /// started a run of it, or was set). Every N hours counts from its last check too, whatever started it.
    /// Null when Argus's scheduler does not run it.
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset anchor, DateTimeOffset? lastCheck, TimeZoneInfo zone)
    {
        switch (Kind)
        {
            case ScheduleKind.Hours:
                var from = lastCheck is { } c && c > anchor ? c : anchor;
                return from + TimeSpan.FromHours(Hours);
            case ScheduleKind.Daily or ScheduleKind.Weekly:
                var local = TimeZoneInfo.ConvertTime(anchor, zone).DateTime;
                var day = local.Date;
                if (Kind == ScheduleKind.Weekly)
                    day = day.AddDays(((int)(DayOfWeek)(Day % 7) - (int)day.DayOfWeek + 7) % 7);
                var step = Kind == ScheduleKind.Weekly ? 7 : 1;
                for (int i = 0; i < 3; i++, day = day.AddDays(step))
                {
                    var at = AtLocal(day.AddHours(Hour).AddMinutes(Minute), zone);
                    if (at > anchor) return at;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>A wall-clock time in the zone as an instant; a time the clocks skip (spring forward) is taken an hour later.</summary>
    static DateTimeOffset AtLocal(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }

    /// <summary>An IANA time zone (UTC when empty); the problem in words when there is no such zone.</summary>
    public static (TimeZoneInfo Zone, string? Problem) Zone(string? id)
    {
        var name = (id ?? "").Trim();
        if (name.Length == 0 || name.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return (TimeZoneInfo.Utc, null);
        try { return (TimeZoneInfo.FindSystemTimeZoneById(name), null); }
        catch (Exception exc) when (exc is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return (TimeZoneInfo.Utc, $"\"{name}\" is not a time zone: use an IANA name such as Europe/Berlin");
        }
    }
}
