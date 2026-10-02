using System.Globalization;
using Llm.Api.Schedules;

namespace Llm.Tests;

/// <summary>Schedules as crontab reads them, in a zone's wall clock.</summary>
public sealed class CronTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static Cron Of(string expression) => Cron.Parse(expression).Cron ?? throw new InvalidOperationException(Cron.Parse(expression).Problem);

    private static DateTimeOffset Local(string at, TimeZoneInfo zone)
    {
        var time = DateTime.Parse(at, CultureInfo.InvariantCulture);
        return new DateTimeOffset(time, zone.GetUtcOffset(time));
    }

    private static string Wall(DateTimeOffset? at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at!.Value, zone).ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public void Weekdays_at_nine_skip_the_weekend()
    {
        var cron = Of("0 9 * * 1-5");
        // 2026-10-02 is a Friday.
        Assert.Equal("Fri 2026-10-02 09:00", Wall(cron.Next(Local("2026-10-02 08:59", Berlin), Berlin), Berlin));
        Assert.Equal("Mon 2026-10-05 09:00", Wall(cron.Next(Local("2026-10-02 09:00", Berlin), Berlin), Berlin));
        Assert.Equal("Mon 2026-10-05 09:00", Wall(cron.Next(Local("2026-10-03 12:00", Berlin), Berlin), Berlin));
    }

    [Fact]
    public void Steps_lists_ranges_and_names_read_as_crontab_does()
    {
        Assert.Equal("Fri 2026-10-02 10:15", Wall(Of("*/15 * * * *").Next(Local("2026-10-02 10:07", Berlin), Berlin), Berlin));
        Assert.Equal("Fri 2026-10-02 18:30", Wall(Of("30 8,12,18 * * FRI").Next(Local("2026-10-02 12:30", Berlin), Berlin), Berlin));
        Assert.Equal("Sun 2026-11-01 00:00", Wall(Of("0 0 1 NOV *").Next(Local("2026-10-02 12:00", Berlin), Berlin), Berlin));
        // Sunday is 0 and 7.
        Assert.Equal("Sun 2026-10-04 07:00", Wall(Of("0 7 * * 7").Next(Local("2026-10-02 12:00", Berlin), Berlin), Berlin));
        // Both day fields limited: either runs.
        Assert.Equal("Mon 2026-10-05 06:00", Wall(Of("0 6 13 * MON").Next(Local("2026-10-02 12:00", Berlin), Berlin), Berlin));
        Assert.Equal("Tue 2026-10-13 06:00", Wall(Of("0 6 13 * MON").Next(Local("2026-10-12 12:00", Berlin), Berlin), Berlin));
        Assert.Null(Of("0 0 31 2 *").Next(Local("2026-10-02 12:00", Berlin), Berlin));
    }

    [Fact]
    public void The_hour_the_clocks_skip_does_not_run_and_the_wall_clock_holds_after()
    {
        // Berlin goes from 02:00 to 03:00 on 2027-03-28.
        var cron = Of("30 2 * * *");
        Assert.Equal("Mon 2027-03-29 02:30", Wall(cron.Next(Local("2027-03-28 01:00", Berlin), Berlin), Berlin));
        Assert.Equal("Sun 2027-03-28 09:00", Wall(Of("0 9 * * *").Next(Local("2027-03-28 01:00", Berlin), Berlin), Berlin));
    }

    [Fact]
    public void A_schedule_that_is_not_one_says_why()
    {
        Assert.Contains("five fields", Cron.Parse("0 9 * *").Problem, StringComparison.Ordinal);
        Assert.Contains("between 0 and 59", Cron.Parse("60 9 * * *").Problem, StringComparison.Ordinal);
        Assert.Contains("backwards", Cron.Parse("0 9 * * 5-1").Problem, StringComparison.Ordinal);
        Assert.Contains("step", Cron.Parse("*/0 * * * *").Problem, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(15), Of("*/15 * * * *").ShortestGap(Local("2026-10-02 10:00", Berlin), Berlin));
    }
}
