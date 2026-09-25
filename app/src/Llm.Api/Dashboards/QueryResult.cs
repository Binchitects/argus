namespace Llm.Api.Dashboards;

public sealed record Column(string Name, string Type);

/// <summary>Rows as they came back ("table" format).</summary>
public sealed record TableResult(IReadOnlyList<Column> Columns, IReadOnlyList<object?[]> Rows, bool Capped);

/// <summary>One line of a chart: [epoch ms, value] points.</summary>
public sealed record Series(string Name, IReadOnlyList<double?[]> Points);

public sealed record TargetResult(string RefId, string Format, TableResult? Table, IReadOnlyList<Series>? Series, string? Error, IReadOnlyList<LogLine>? Logs = null);

public static class SeriesShaping
{
    /// <summary>
    /// Grafana's rule for the SQL "time_series" format: the `time` column is the
    /// x axis; a string `metric` column names the series; every other numeric
    /// column is a value. With a metric and several value columns a series is
    /// "&lt;metric&gt; &lt;column&gt;".
    /// </summary>
    public static IReadOnlyList<Series> ToSeries(TableResult t)
    {
        var timeIdx = t.Columns.ToList().FindIndex(c => c.Name == "time");
        if (timeIdx < 0)
        {
            throw new FormatException("A time_series query needs a column named \"time\".");
        }
        var metricIdx = t.Columns.ToList().FindIndex(c => c.Name == "metric" && c.Type == "string");
        var valueIdx = Enumerable.Range(0, t.Columns.Count).Where(i => i != timeIdx && i != metricIdx && t.Columns[i].Type == "number").ToList();
        var series = new Dictionary<string, List<double?[]>>(StringComparer.Ordinal);
        foreach (var row in t.Rows)
        {
            var time = ToEpochMs(row[timeIdx]);
            if (time is null)
            {
                continue;
            }
            foreach (var vi in valueIdx)
            {
                var name = metricIdx < 0 ? t.Columns[vi].Name
                    : valueIdx.Count == 1 ? row[metricIdx]?.ToString() ?? ""
                    : $"{row[metricIdx]} {t.Columns[vi].Name}";
                if (!series.TryGetValue(name, out var points))
                {
                    series[name] = points = [];
                }
                points.Add([time, row[vi] is double d ? d : null]);
            }
        }
        return [.. series.Select(kv => new Series(kv.Key, kv.Value))];
    }

    /// <summary>Epoch seconds (what $__timeGroup yields) or a timestamp, as epoch milliseconds.</summary>
    private static double? ToEpochMs(object? v) => v switch
    {
        double d => d < 1e11 ? d * 1000 : d,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        DateTimeOffset o => o.ToUnixTimeMilliseconds(),
        _ => null,
    };
}
