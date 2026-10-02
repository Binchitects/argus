using System.Globalization;
using Llm.Api.Models;
using Llm.Core.Models;

namespace Llm.Tests;

/// <summary>Which working hours apply when: by day and time, past midnight, all day, and the first of two that overlap.</summary>
public sealed class ModelHoursTests
{
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");

    private static ModelWindow Window(string name, int[] days, string start, string end, int order = 0, bool enabled = true) => new()
    {
        Name = name, Days = [.. days], Start = TimeOnly.Parse(start, CultureInfo.InvariantCulture), End = TimeOnly.Parse(end, CultureInfo.InvariantCulture), Order = order, Enabled = enabled, Keep = ["small"],
    };

    /// <summary>A moment, as a wall clock in Tehran reads it (2026-10-05 is a Monday).</summary>
    private static DateTimeOffset At(string local)
    {
        var time = DateTime.Parse(local, CultureInfo.InvariantCulture);
        return new DateTimeOffset(time, Tehran.GetUtcOffset(time));
    }

    private static readonly int[] Weekdays = [1, 2, 3, 4, 5];

    [Fact]
    public void Busy_hours_hold_on_weekdays_from_their_start_until_their_end()
    {
        var busy = Window("Busy hours", Weekdays, "08:00", "18:00");
        Assert.Equal(busy, Hours.Active([busy], At("2026-10-05 08:00"), Tehran).Window);
        var (window, until) = Hours.Active([busy], At("2026-10-05 17:59"), Tehran);
        Assert.Equal(busy, window);
        Assert.Equal(At("2026-10-05 18:00"), until);
        Assert.Null(Hours.Active([busy], At("2026-10-05 18:00"), Tehran).Window);
        Assert.Null(Hours.Active([busy], At("2026-10-05 07:59"), Tehran).Window);
        // Saturday.
        Assert.Null(Hours.Active([busy], At("2026-10-10 12:00"), Tehran).Window);
    }

    [Fact]
    public void Night_hours_run_past_midnight_into_the_next_morning()
    {
        // Friday night only: from Friday 22:00 to Saturday 06:00.
        var night = Window("Night", [5], "22:00", "06:00");
        Assert.Equal(night, Hours.Active([night], At("2026-10-09 23:30"), Tehran).Window);
        var (window, until) = Hours.Active([night], At("2026-10-10 05:00"), Tehran);
        Assert.Equal(night, window);
        Assert.Equal(At("2026-10-10 06:00"), until);
        Assert.Null(Hours.Active([night], At("2026-10-10 06:00"), Tehran).Window);
        // Thursday's night is not one of its nights, nor is Friday morning.
        Assert.Null(Hours.Active([night], At("2026-10-09 05:00"), Tehran).Window);
    }

    [Fact]
    public void All_day_windows_and_overlaps_go_by_their_order_and_only_when_on()
    {
        var weekend = Window("Weekend", [6, 7], "00:00", "00:00", order: 2);
        var maintenance = Window("Maintenance", [6], "10:00", "12:00", order: 1);
        Assert.Equal(maintenance, Hours.Active([weekend, maintenance], At("2026-10-10 11:00"), Tehran).Window);
        var (window, until) = Hours.Active([weekend, maintenance], At("2026-10-10 13:00"), Tehran);
        Assert.Equal(weekend, window);
        Assert.Equal(At("2026-10-11 00:00"), until);
        maintenance.Enabled = false;
        Assert.Equal(weekend, Hours.Active([weekend, maintenance], At("2026-10-10 11:00"), Tehran).Window);
    }

    [Fact]
    public void An_unknown_time_zone_is_utc_and_says_so()
    {
        var (zone, problem) = Hours.Zone("Mars/Olympus_Mons");
        Assert.Equal(TimeZoneInfo.Utc, zone);
        Assert.Contains("unknown", problem, StringComparison.Ordinal);
        Assert.Null(Hours.Zone("Asia/Tehran").Problem);
    }
}
