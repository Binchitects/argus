using System.Globalization;

namespace Llm.Api.Schedules;

/// <summary>
/// A five-field cron schedule (minute hour day-of-month month day-of-week), as crontab
/// reads it: *, a value, a range a-b, a step */n or a-b/n, and lists of these; months and
/// days by number or by name (JAN, MON); Sunday is 0 or 7. When both day fields are
/// limited, a day matching either runs (crontab's rule). Times are a zone's wall clock.
/// </summary>
public sealed class Cron
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    public string Expression { get; }

    private Cron(string expression, string[] f)
    {
        Expression = expression;
        Fill(_minutes, f[0], 0, 59, null);
        Fill(_hours, f[1], 0, 23, null);
        _anyDay = f[2] is "*" or "?";
        Fill(_days, f[2], 1, 31, null);
        Fill(_months, f[3], 1, 12, ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]);
        _anyWeekday = f[4] is "*" or "?";
        var weekdays = new bool[8];
        Fill(weekdays, f[4], 0, 7, ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"]);
        for (var i = 0; i < 7; i++)
        {
            _weekdays[i] = weekdays[i] || (i == 0 && weekdays[7]);
        }
    }

    /// <summary>The schedule, or why it is not one.</summary>
    public static (Cron? Cron, string? Problem) Parse(string? expression)
    {
        var fields = (expression ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return (null, "A schedule has five fields: minute, hour, day of the month, month and day of the week (e.g. 0 9 * * 1-5).");
        }
        try
        {
            return (new Cron(string.Join(' ', fields), fields), null);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The first run after <paramref name="after"/> (to the minute), as an instant in UTC; null when it never runs (31 February).</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var from = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        // Five years: long enough for 29 February on a Monday.
        for (var day = from.Date; day < from.Date.AddYears(5); day = day.AddDays(1))
        {
            if (!_months[day.Month] || !DayMatches(day))
            {
                continue;
            }
            for (var h = day == from.Date ? from.Hour : 0; h < 24; h++)
            {
                if (!_hours[h])
                {
                    continue;
                }
                for (var m = day == from.Date && h == from.Hour ? from.Minute : 0; m < 60; m++)
                {
                    if (!_minutes[m])
                    {
                        continue;
                    }
                    var at = day.AddHours(h).AddMinutes(m);
                    // A wall-clock time the zone skips (the hour the clocks go forward) does not happen.
                    if (zone.IsInvalidTime(at))
                    {
                        continue;
                    }
                    var instant = new DateTimeOffset(at, zone.GetUtcOffset(at)).ToUniversalTime();
                    if (instant > after)
                    {
                        return instant;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>The next few runs, for people to see what they set.</summary>
    public IReadOnlyList<DateTimeOffset> NextRuns(DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        var runs = new List<DateTimeOffset>();
        for (var at = after; runs.Count < count && Next(at, zone) is { } next; at = next)
        {
            runs.Add(next);
        }
        return runs;
    }

    /// <summary>The shortest time between two runs, over the next few days (to keep a schedule from running every minute).</summary>
    public TimeSpan? ShortestGap(DateTimeOffset from, TimeZoneInfo zone)
    {
        var runs = NextRuns(from, zone, 50);
        return runs.Count < 2 ? null : runs.Zip(runs.Skip(1), (a, b) => b - a).Min();
    }

    private bool DayMatches(DateTime day)
    {
        var dom = _days[day.Day];
        var dow = _weekdays[(int)day.DayOfWeek];
        return (_anyDay, _anyWeekday) switch
        {
            (true, true) => true,
            (true, false) => dow,
            (false, true) => dom,
            _ => dom || dow,
        };
    }

    private static void Fill(bool[] set, string field, int min, int max, string[]? names)
    {
        foreach (var part in field.Split(','))
        {
            var (range, step) = part.Split('/') switch
            {
                [var r] => (r, 1),
                [var r, var s] when int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 => (r, n),
                _ => throw new FormatException($"\"{part}\" is not a step: write */15 or 0-30/5."),
            };
            int from, to;
            if (range is "*" or "?")
            {
                (from, to) = (min, max);
            }
            else if (range.Split('-') is [var a, var b])
            {
                (from, to) = (Value(a, min, max, names), Value(b, min, max, names));
                if (from > to)
                {
                    throw new FormatException($"\"{range}\" runs backwards: write the smaller first.");
                }
            }
            else
            {
                from = Value(range, min, max, names);
                // "5/15": from 5, every 15, to the end.
                to = part.Contains('/') ? max : from;
            }
            for (var v = from; v <= to; v += step)
            {
                set[v] = true;
            }
        }
    }

    private static int Value(string text, int min, int max, string[]? names)
    {
        if (names is not null && Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) is var i and >= 0)
        {
            return i + (min == 1 ? 1 : 0);
        }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
        {
            throw new FormatException($"\"{text}\" is not between {min} and {max}.");
        }
        return v;
    }
}
