using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Dashboards;

/// <summary>
/// Grafana's PostgreSQL macros, expanded the way Grafana expands them, so a
/// panel's SQL gives the same rows here as in Grafana (the comparison test in
/// stack/scripts/compare-dashboards.py holds us to that).
/// </summary>
public static partial class SqlMacros
{
    [GeneratedRegex(@"\$__(timeFilter|timeGroupAlias|timeGroup|timeFrom|timeTo|unixEpochFilter)\s*\(")]
    private static partial Regex MacroStart();

    public static string Expand(string sql, DateTimeOffset from, DateTimeOffset to, TimeSpan interval)
    {
        // Plain variables first: $__interval can be an argument of a macro.
        sql = sql
            .Replace("$__interval_ms", ((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$__interval", FormatInterval(interval), StringComparison.Ordinal)
            .Replace("$__range_s", ((long)(to - from).TotalSeconds).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$__range", FormatInterval(to - from), StringComparison.Ordinal);

        var sb = new StringBuilder();
        var pos = 0;
        while (MacroStart().Match(sql, pos) is { Success: true } m)
        {
            sb.Append(sql, pos, m.Index - pos);
            var close = FindClose(sql, m.Index + m.Length);
            var args = SplitArgs(sql[(m.Index + m.Length)..close]);
            sb.Append(m.Groups[1].Value switch
            {
                "timeFilter" => $"{Arg(args, 0)} BETWEEN '{Rfc3339(from)}' AND '{Rfc3339(to)}'",
                "timeFrom" => $"'{Rfc3339(from)}'",
                "timeTo" => $"'{Rfc3339(to)}'",
                "unixEpochFilter" => $"{Arg(args, 0)} >= {from.ToUnixTimeSeconds()} AND {Arg(args, 0)} <= {to.ToUnixTimeSeconds()}",
                "timeGroup" => TimeGroup(args),
                "timeGroupAlias" => TimeGroup(args) + " AS \"time\"",
                _ => throw new InvalidOperationException(m.Value),
            });
            pos = close + 1;
        }
        sb.Append(sql, pos, sql.Length - pos);
        return sb.ToString();
    }

    /// <summary>Grafana: floor(extract(epoch from col)/secs)*secs.</summary>
    private static string TimeGroup(List<string> args)
    {
        var secs = ParseInterval(Arg(args, 1)).TotalSeconds;
        var s = secs.ToString("0.###", CultureInfo.InvariantCulture);
        return $"floor(extract(epoch from {Arg(args, 0)})/{s})*{s}";
    }

    private static string Arg(List<string> args, int i) =>
        i < args.Count ? args[i] : throw new FormatException($"A macro is missing argument {i + 1}.");

    /// <summary>Grafana writes times in UTC, RFC 3339 with nanoseconds trimmed.</summary>
    public static string Rfc3339(DateTimeOffset t) =>
        t.UtcDateTime.ToString(t.Millisecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static int FindClose(string s, int start)
    {
        var depth = 1;
        var quote = '\0';
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    return i;
            }
        }
        throw new FormatException("A macro's parenthesis is never closed.");
    }

    private static List<string> SplitArgs(string s)
    {
        var args = new List<string>();
        var depth = 0;
        var quote = '\0';
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        args.Add(s[start..].Trim());
        return args;
    }

    /// <summary>"1d", "5m", "30s", "250ms", "2h", "1w" (and a bare number of seconds).</summary>
    public static TimeSpan ParseInterval(string text)
    {
        text = text.Trim().Trim('\'', '"');
        var m = IntervalPattern().Match(text);
        if (!m.Success)
        {
            throw new FormatException($"Not an interval: {text}");
        }
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "" or "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            "w" => TimeSpan.FromDays(7 * n),
            "y" => TimeSpan.FromDays(365 * n),
            _ => throw new FormatException(text),
        };
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(ms|s|m|h|d|w|y)?$")]
    private static partial Regex IntervalPattern();

    /// <summary>The largest whole unit, as Grafana prints $__interval ("5m", "1h", "1d").</summary>
    public static string FormatInterval(TimeSpan t)
    {
        var ms = (long)t.TotalMilliseconds;
        foreach (var (unit, size) in new (string, long)[] { ("d", 86_400_000), ("h", 3_600_000), ("m", 60_000), ("s", 1000) })
        {
            if (ms >= size && ms % size == 0)
            {
                return (ms / size).ToString(CultureInfo.InvariantCulture) + unit;
            }
        }
        return ms.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    /// <summary>
    /// Grafana's interval rounding (rangeutil.roundInterval): the bucket a panel
    /// asks for is (range / points) rounded to a "nice" size from this table.
    /// </summary>
    public static TimeSpan RoundInterval(TimeSpan raw)
    {
        var i = raw.TotalMilliseconds;
        long ms = i switch
        {
            < 15 => 10, < 35 => 20, < 75 => 50, < 150 => 100, < 350 => 200, < 750 => 500,
            < 1500 => 1000, < 3500 => 2000, < 7500 => 5000, < 12500 => 10000, < 17500 => 15000,
            < 25000 => 20000, < 45000 => 30000, < 90000 => 60000, < 210000 => 120000,
            < 450000 => 300000, < 750000 => 600000, < 1050000 => 900000, < 1500000 => 1200000,
            < 2700000 => 1800000, < 5400000 => 3600000, < 9000000 => 7200000, < 16200000 => 10800000,
            < 32400000 => 21600000, < 86400000 => 43200000, < 604800000 => 86400000,
            < 1814400000 => 604800000, < 3628800000 => 2592000000, _ => 31536000000,
        };
        return TimeSpan.FromMilliseconds(ms);
    }
}
