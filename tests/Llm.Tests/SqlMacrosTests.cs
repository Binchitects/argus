using Llm.Api.Dashboards;

namespace Llm.Tests;

public sealed class SqlMacrosTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 24, 12, 30, 5, TimeSpan.FromHours(3));

    [Fact]
    public void Time_filter_is_utc_rfc3339_like_grafana()
    {
        var sql = SqlMacros.Expand("""where $__timeFilter(s."startTime")""", From, To, TimeSpan.FromMinutes(5));
        Assert.Equal("""where s."startTime" BETWEEN '2026-09-01T00:00:00Z' AND '2026-09-24T09:30:05Z'""", sql);
    }

    [Fact]
    public void Time_group_alias_buckets_by_seconds()
    {
        Assert.Equal("""select floor(extract(epoch from s."startTime")/86400)*86400 AS "time", 1""",
            SqlMacros.Expand("""select $__timeGroupAlias(s."startTime", 1d), 1""", From, To, TimeSpan.FromMinutes(5)));
        Assert.Equal("""floor(extract(epoch from "startTime")/300)*300 AS "time" """.TrimEnd(),
            SqlMacros.Expand("""$__timeGroupAlias("startTime", $__interval)""", From, To, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Nested_parentheses_and_quotes_in_arguments_survive()
    {
        var sql = SqlMacros.Expand("$__timeFilter(coalesce(a.\"t,x\", date_trunc('day', b)))", From, To, TimeSpan.FromMinutes(1));
        Assert.StartsWith("coalesce(a.\"t,x\", date_trunc('day', b)) BETWEEN '2026-09-01T00:00:00Z'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Interval_variables_expand()
    {
        var to = From.AddDays(23); // exactly 23 days: 1,987,200 s
        Assert.Equal("300000 5m 1987200 23d", SqlMacros.Expand("$__interval_ms $__interval $__range_s $__range", From, to, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(1_000, 1_000)]
    [InlineData(40_000, 30_000)]
    [InlineData(100_000, 120_000)]
    [InlineData(4_000_000, 3_600_000)]
    [InlineData(6_480_000, 7_200_000)]   // 30 days over 400 points
    [InlineData(90_000_000, 86_400_000)]
    public void Intervals_round_like_grafana(long rawMs, long expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), SqlMacros.RoundInterval(TimeSpan.FromMilliseconds(rawMs)));

    [Theory]
    [InlineData("1d", 86400)]
    [InlineData("'5m'", 300)]
    [InlineData("30s", 30)]
    [InlineData("90", 90)]
    [InlineData("1w", 604800)]
    public void Intervals_parse(string text, double seconds) =>
        Assert.Equal(seconds, SqlMacros.ParseInterval(text).TotalSeconds);
}
