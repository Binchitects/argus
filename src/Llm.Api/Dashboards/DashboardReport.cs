using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Reports;

namespace Llm.Api.Dashboards;

/// <summary>What one dashboard adds to a report: its stats as figures, its charts, its tables, and what could not be read.</summary>
public sealed record DashboardParts(List<ReportFigure> Figures, List<ReportChart> Charts, List<ReportTable> Tables);

/// <summary>
/// A dashboard as a report, the same in PDF, Excel and CSV as Accounting's: every panel over the time window as the page
/// shows it (its variables as chosen), a stat or gauge as a figure (a Now panel as it was when the report was made), a
/// chart as a chart (lines, or stacked bars when the panel stacks), a table as a table, a log as its lines.
/// </summary>
public static class DashboardReport
{
    /// <summary>A chart keeps this many series; the rest are named in its note.</summary>
    public const int MaxSeries = 12;

    /// <summary>A table or a log keeps this many rows.</summary>
    public const int MaxRows = 1000;

    /// <summary>A stat with more values than this (one a core) is a table, not a row of figures.</summary>
    private const int MaxFigures = 8;

    public static async Task<ReportDocument> DocumentAsync(Dashboard d, PanelQuery q, PanelSources src, string madeFor, CancellationToken ct)
    {
        var parts = await PartsAsync(d, q, src, null, ct);
        return new ReportDocument(d.Title, "Every panel of the dashboard over the time window, as the page shows it; the Now figures as they were when this was made.",
            q.From, q.To, src.Clock.GetUtcNow(), parts.Figures, parts.Charts, parts.Tables, madeFor) { ChartsAsTables = true };
    }

    /// <summary>The dashboard's parts; with <paramref name="prefix"/> each title starts with it (a dashboard inside Accounting's report).</summary>
    public static async Task<DashboardParts> PartsAsync(Dashboard d, PanelQuery q, PanelSources src, string? prefix, CancellationToken ct)
    {
        var figures = new List<ReportFigure>();
        var charts = new List<ReportChart>();
        var tables = new List<ReportTable>();
        var unread = new List<object?[]>();
        string Named(string title) => prefix is null ? title : $"{prefix}: {title}";
        string? section = null;
        foreach (var panel in d.Panels.OrderBy(p => Y(p)).ThenBy(p => X(p)))
        {
            if (panel.Type == "row")
            {
                section = panel.Title;
                continue;
            }
            if (panel.Type == "text" || !DashboardEndpoints.Supported(panel))
            {
                continue;
            }
            var title = panel.Title is { Length: > 0 } t ? t : "Panel";
            List<TargetResult> results;
            TimeSpan bucket;
            try
            {
                (bucket, results) = await DashboardEndpoints.RunAsync(d, panel, q, src, ct);
            }
            catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                unread.Add([title, ex.Message]);
                continue;
            }
            foreach (var r in results.Where(r => r.Error is not null))
            {
                unread.Add([title, r.Error]);
            }
            var ok = results.Where(r => r.Error is null).ToList();
            var unit = panel.Definition["fieldConfig"]?["defaults"]?["unit"]?.GetValue<string>();
            var decimals = panel.Definition["fieldConfig"]?["defaults"]?["decimals"] is JsonValue dv && dv.TryGetValue<int>(out var dec) ? dec : (int?)null;
            switch (panel.Type)
            {
                case "stat" or "gauge" or "bargauge":
                {
                    var calc = (panel.Definition["options"]?["reduceOptions"]?["calcs"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "lastNotNull";
                    var byName = panel.Definition["options"]?["textMode"]?.GetValue<string>() == "name";
                    var values = Reduce(ok, calc);
                    var hint = panel.Live ? "Now" : section;
                    if (values.Count == 0 || values.All(v => v.Value is null))
                    {
                        // Nothing measured: what the page says ("idle", "No data").
                        var none = panel.Definition["fieldConfig"]?["defaults"]?["noValue"]?.GetValue<string>() ?? "No data";
                        figures.Add(new ReportFigure(Named(title), none, hint));
                        break;
                    }
                    if (values.Count > MaxFigures)
                    {
                        tables.Add(new ReportTable(Named(title), [new("Name", ReportKind.Text, 2), new("Value", ReportKind.Text)],
                            [.. values.Select(v => (object?[])[v.Name, byName ? v.Name : Units.Format(v.Value, unit, decimals)])], hint));
                    }
                    else
                    {
                        // Several values: each named by its series, the panel's title under it.
                        figures.AddRange(values.Select(v => new ReportFigure(Named(values.Count > 1 && !byName ? v.Name : title),
                            byName ? v.Name : Units.Format(v.Value, unit, decimals), values.Count > 1 && !byName ? title + (panel.Live ? " (now)" : "") : hint)));
                    }
                    break;
                }
                case "timeseries" or "barchart":
                {
                    var series = ok.SelectMany(r => r.Series ?? []).ToList();
                    if (series.Count == 0)
                    {
                        charts.Add(new ReportChart(Named(Titled(title, unit)), [], [], ReportKind.Fraction, false, section));
                        break;
                    }
                    var (scale, kind) = Units.Scale(unit);
                    var shown = series.Take(MaxSeries).ToList();
                    // The whole window, bucket by bucket, so every chart has the same time along it (a gap where nothing was measured).
                    var ms = Math.Max(1000, bucket.TotalMilliseconds);
                    var start = Math.Floor(q.From.ToUnixTimeMilliseconds() / ms) * ms;
                    var count = (int)Math.Min(5000, Math.Floor((q.To.ToUnixTimeMilliseconds() - start) / ms) + 1);
                    var at = Enumerable.Range(0, count).Select(i => start + i * ms).ToList();
                    var rows = at.Select(_ => Enumerable.Repeat(double.NaN, shown.Count).ToArray()).ToList();
                    for (var s = 0; s < shown.Count; s++)
                    {
                        foreach (var p in shown[s].Points)
                        {
                            if (p[0] is { } x && p[1] is { } v && double.IsFinite(v) && (int)Math.Round((x - start) / ms) is var i && i >= 0 && i < count)
                            {
                                rows[i][s] = v * scale;
                            }
                        }
                    }
                    var stacked = panel.Definition["fieldConfig"]?["defaults"]?["custom"]?["stacking"]?["mode"]?.GetValue<string>() is "normal" or "percent";
                    var note = series.Count > MaxSeries ? $"{series.Count - MaxSeries} more series not shown: {string.Join(", ", series.Skip(MaxSeries).Take(8).Select(s => s.Name))}" + (series.Count > MaxSeries + 8 ? ", …" : "") : section;
                    charts.Add(new ReportChart(Named(Titled(title, unit)), [.. shown.Select(s => s.Name.Length == 0 ? title : s.Name)],
                        [.. at.Select((x, i) => (DateTimeOffset.FromUnixTimeMilliseconds((long)x), rows[i]))], kind, stacked, note));
                    break;
                }
                case "table":
                    foreach (var table in ok.Select(r => r.Table).OfType<TableResult>())
                    {
                        tables.Add(Table(Named(title), table, section));
                    }
                    break;
                case "logs":
                {
                    var lines = ok.SelectMany(r => r.Logs ?? []).OrderByDescending(l => l.Time).ToList();
                    tables.Add(new ReportTable(Named(title), [new("Time", ReportKind.Time, 1.2), new("Line", ReportKind.Text, 5)],
                        [.. lines.Take(MaxRows).Select(l => (object?[])[DateTimeOffset.FromUnixTimeMilliseconds((long)l.Time), l.Line])],
                        lines.Count > MaxRows ? $"The newest {MaxRows} of {lines.Count} lines." : section));
                    break;
                }
            }
        }
        if (unread.Count > 0)
        {
            tables.Add(new ReportTable(Named("Panels that could not be read"), [new("Panel", ReportKind.Text, 2), new("Why", ReportKind.Text, 4)], unread));
        }
        return new DashboardParts(figures, charts, tables);
    }

    private static int Y(Panel p) => p.Definition["gridPos"]?["y"]?.GetValue<int>() ?? 0;

    private static int X(Panel p) => p.Definition["gridPos"]?["x"]?.GetValue<int>() ?? 0;

    private static string Titled(string title, string? unit) =>
        Units.Label(unit) is { } label && !title.Contains($"({label})", StringComparison.Ordinal) ? $"{title} ({label})" : title;

    /// <summary>A panel's table: numbers as numbers, times as times, the rest as text; at most <see cref="MaxRows"/> rows.</summary>
    private static ReportTable Table(string title, TableResult t, string? section)
    {
        var kinds = t.Columns.Select(c => c.Type switch { "number" => ReportKind.Fraction, "time" => ReportKind.Time, _ => ReportKind.Text }).ToList();
        var rows = t.Rows.Take(MaxRows).Select(row => (object?[])[.. row.Select((v, i) => Cell(v, kinds[i]))]).ToList();
        var note = t.Rows.Count > MaxRows || t.Capped ? $"The first {Math.Min(MaxRows, t.Rows.Count)} rows." : section;
        return new ReportTable(title, [.. t.Columns.Select((c, i) => new ReportColumn(c.Name, kinds[i], kinds[i] == ReportKind.Text ? 2.2 : 1))], rows, note);
    }

    private static object? Cell(object? v, ReportKind kind) => v switch
    {
        null => null,
        double d when !double.IsFinite(d) => null,
        string s when kind == ReportKind.Time && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) => at,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        bool b => b ? "yes" : "no",
        double or float or int or long or decimal => v,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    /// <summary>The values a stat shows: one a series reduced by its calc (Grafana's), or a table's first number named by its first text.</summary>
    public static List<(string Name, double? Value)> Reduce(IEnumerable<TargetResult> results, string calc)
    {
        var out_ = new List<(string, double?)>();
        foreach (var r in results)
        {
            if (r.Series is { } series)
            {
                out_.AddRange(series.Select(s => (s.Name, Calc([.. s.Points.Select(p => p[1]).OfType<double>().Where(double.IsFinite)], calc))));
            }
            else if (r.Table is { } t)
            {
                var num = t.Columns.ToList().FindIndex(c => c.Type == "number");
                var text = t.Columns.ToList().FindIndex(c => c.Type == "string");
                if (num < 0)
                {
                    continue;
                }
                if (text < 0 || t.Rows.Count <= 1)
                {
                    out_.Add((t.Columns[num].Name, Calc([.. t.Rows.Select(row => row[num]).OfType<double>()], calc)));
                }
                else
                {
                    out_.AddRange(t.Rows.Select(row => (Convert.ToString(row[text], CultureInfo.InvariantCulture) ?? "", row[num] as double?)));
                }
            }
        }
        return out_;
    }

    private static double? Calc(List<double> v, string how) => v.Count == 0 ? null : how switch
    {
        "sum" => v.Sum(),
        "mean" => v.Average(),
        "max" => v.Max(),
        "min" => v.Min(),
        "first" or "firstNotNull" => v[0],
        "count" => v.Count,
        "range" => v.Max() - v.Min(),
        "delta" => v[^1] - v[0],
        _ => v[^1],
    };
}

/// <summary>A dashboard's units as the page reads them (lib/format.ts), for a report.</summary>
public static class Units
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A value as the page shows it: "42.5%", "1.2 GiB", "350 W", "3 hours 5 minutes", "1.2 K".</summary>
    public static string Format(double? value, string? unit, int? decimals = null)
    {
        if (value is not { } v || !double.IsFinite(v))
        {
            return "–";
        }
        string F(double n, int d) => n.ToString("#,##0" + (d > 0 ? "." + new string('0', d) : ""), Inv);
        switch (unit)
        {
            case "currencyUSD":
                return decimals is { } dd ? "$" + F(v, dd) : ReportFiles.Money((decimal)v);
            case "percent":
                return F(v, decimals ?? (Math.Abs(v) < 10 ? 1 : 0)) + "%";
            case "percentunit":
                return F(v * 100, decimals ?? 1) + "%";
            case "s":
                return v < 1 ? F(v * 1000, 0) + " ms" : F(v, decimals ?? 2) + " s";
            case "ms":
                return v >= 1000 ? F(v / 1000, decimals ?? 2) + " s" : F(v, decimals ?? 0) + " ms";
            case "dtdurations":
            {
                (long Size, string Name)[] parts = [(86400, "day"), (3600, "hour"), (60, "minute"), (1, "second")];
                var total = (long)Math.Max(0, Math.Round(v));
                var i = Array.FindIndex(parts, p => total >= p.Size);
                if (i < 0)
                {
                    return "0 seconds";
                }
                string Say(long n, string name) => $"{n} {name}{(n == 1 ? "" : "s")}";
                var rest = i + 1 < parts.Length ? total % parts[i].Size / parts[i + 1].Size : 0;
                return rest > 0 ? $"{Say(total / parts[i].Size, parts[i].Name)} {Say(rest, parts[i + 1].Name)}" : Say(total / parts[i].Size, parts[i].Name);
            }
            case "celsius":
                return F(v, decimals ?? (v == Math.Floor(v) ? 0 : 1)) + " C";
            case "watt":
                return Math.Abs(v) >= 1000 ? F(v / 1000, decimals ?? 2) + " kW" : F(v, decimals ?? 0) + " W";
            case "kwatth":
                return Math.Abs(v) >= 1000 ? F(v / 1000, decimals ?? 2) + " MWh" : F(v, decimals ?? (Math.Abs(v) < 10 ? 2 : 0)) + " kWh";
            case "rotmhz":
                return Format(v * 1e6, "hertz", decimals);
            case "hertz" or "Bps":
            {
                string[] names = unit == "hertz" ? ["Hz", "kHz", "MHz", "GHz", "THz"] : ["B/s", "kB/s", "MB/s", "GB/s", "TB/s"];
                for (var i = names.Length - 1; i > 0; i--)
                {
                    if (Math.Abs(v) >= Math.Pow(1000, i))
                    {
                        return (v / Math.Pow(1000, i)).ToString(decimals is { } d ? "0." + new string('0', d) : "0.##", Inv) + " " + names[i];
                    }
                }
                return F(v, decimals ?? 0) + " " + names[0];
            }
            case "bytes" or "decbytes":
            {
                string[] names = ["B", "KiB", "MiB", "GiB", "TiB"];
                var i = 0;
                for (; Math.Abs(v) >= 1024 && i < names.Length - 1; i++)
                {
                    v /= 1024;
                }
                return F(v, decimals ?? (i > 0 ? 1 : 0)) + " " + names[i];
            }
            default:
            {
                if (decimals is { } d && Math.Abs(v) < 1000)
                {
                    return F(v, d);
                }
                foreach (var (size, s) in new[] { (1e12, "T"), (1e9, "B"), (1e6, "M"), (1e3, "K") })
                {
                    if (Math.Abs(v) >= size)
                    {
                        return (v / size).ToString("0.##", Inv) + " " + s;
                    }
                }
                return v == Math.Floor(v) ? v.ToString("#,##0", Inv) : Math.Abs(v) < 1 ? v.ToString("G3", Inv) : v.ToString("#,##0.##", Inv);
            }
        }
    }

    /// <summary>What a chart's numbers are in, for its title: "%", "GiB", "seconds"; null for a plain count.</summary>
    public static string? Label(string? unit) => unit switch
    {
        "percent" or "percentunit" => "%",
        "s" => "seconds",
        "ms" => "ms",
        "bytes" or "decbytes" => "GiB",
        "Bps" => "MB/s",
        "hertz" or "rotmhz" => "MHz",
        "celsius" => "C",
        "watt" => "W",
        "kwatth" => "kWh",
        "currencyUSD" => "$",
        "dtdurations" => "seconds",
        _ => null,
    };

    /// <summary>What a chart's values are multiplied by for its <see cref="Label"/>, and how they read.</summary>
    public static (double Scale, ReportKind Kind) Scale(string? unit) => unit switch
    {
        "percentunit" => (100, ReportKind.Fraction),
        "bytes" or "decbytes" => (1.0 / (1024 * 1024 * 1024), ReportKind.Fraction),
        "Bps" => (1e-6, ReportKind.Fraction),
        "hertz" => (1e-6, ReportKind.Fraction),
        "currencyUSD" => (1, ReportKind.Money),
        _ => (1, ReportKind.Fraction),
    };
}

/// <summary>Every dashboard a person may see, added to another report (Accounting's), each part titled with its dashboard.</summary>
public sealed class DashboardBook(DashboardStore store, SqlDatasource sql, GatewayDatasource ledger, PromDatasource prom, LokiDatasource loki,
    Microsoft.Extensions.Options.IOptions<DashboardOptions> options, TimeProvider clock, Access.AccessService access, Operations.Features features)
{
    public async Task<ReportDocument> AppendAsync(ReportDocument doc, Core.Identity.AppUser me, CancellationToken ct)
    {
        if (!features.IsOn("monitoring"))
        {
            return doc;
        }
        var src = new PanelSources(sql, ledger, prom, loki, options.Value, clock);
        var charts = doc.Charts.ToList();
        var tables = doc.Tables.ToList();
        var q = new PanelQuery(doc.From, doc.To);
        foreach (var d in store.All())
        {
            if (!await access.MayAsync(me, d.Permission, ct))
            {
                continue;
            }
            var parts = await DashboardReport.PartsAsync(d, q, src, d.Title, ct);
            if (parts.Figures.Count > 0)
            {
                tables.Add(new ReportTable($"{d.Title}: at a glance", [new("Figure", ReportKind.Text, 3), new("Value", ReportKind.Text, 1.2), new("Where", ReportKind.Text, 1.6)],
                    [.. parts.Figures.Select(f => (object?[])[f.Label[(d.Title.Length + 2)..], f.Value, f.Hint])]));
            }
            charts.AddRange(parts.Charts);
            tables.AddRange(parts.Tables);
        }
        return doc with { Charts = charts, Tables = tables, Subtitle = doc.Subtitle + " With every dashboard over the same window.", ChartsAsTables = true };
    }
}
