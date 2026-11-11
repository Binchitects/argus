namespace Llm.Api.Reports;

/// <summary>How a column's values read: text, a whole number, money (dollars), hours (from seconds), a percent (0 to 100), a time, or a number with its fraction (Fraction: a dashboard's 0.83).</summary>
public enum ReportKind
{
    Text,
    Number,
    Money,
    Hours,
    Percent,
    Time,
    Fraction,
}

public sealed record ReportColumn(string Header, ReportKind Kind = ReportKind.Text, double Width = 1);

/// <summary>A table: its title, columns, and rows (one value a column: string, a number, a DateTimeOffset, or null).</summary>
public sealed record ReportTable(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<object?[]> Rows, string? Note = null);

/// <summary>One figure at the top: what it is, its value as it reads, and a line under it.</summary>
public sealed record ReportFigure(string Label, string Value, string? Hint = null);

/// <summary>
/// A chart over time: each bucket's values, one per series, and how they read. Stacked bars, or lines one beside another
/// (<paramref name="Stacked"/> false: a dashboard's CPU and memory are not parts of one whole). A value not measured is NaN.
/// </summary>
public sealed record ReportChart(string Title, IReadOnlyList<string> Series, IReadOnlyList<(DateTimeOffset At, double[] Values)> Buckets, ReportKind Kind = ReportKind.Number,
    bool Stacked = true, string? Note = null);

/// <summary>
/// A report as people read it, whatever the file: its title, what and when it covers, its figures, its charts and its
/// tables. PDF (ReportPdf), Excel (ReportXlsx) and CSV (ReportCsv) are made from the same one, so they say the same.
/// </summary>
public sealed record ReportDocument(
    string Title,
    string Subtitle,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset Made,
    IReadOnlyList<ReportFigure> Figures,
    IReadOnlyList<ReportChart> Charts,
    IReadOnlyList<ReportTable> Tables,
    string? MadeBy = null)
{
    /// <summary>CSV has no charts: with this, each chart is written as a table of its buckets (a dashboard's), else left out (Accounting has its own "By time").</summary>
    public bool ChartsAsTables { get; init; }

    /// <summary>"1 Nov 2026 to 7 Nov 2026 (UTC)": an end at midnight is the day before's end.</summary>
    public string Period => $"{Day(From)} to {Day(To.UtcDateTime.TimeOfDay == TimeSpan.Zero && To - From >= TimeSpan.FromDays(1) ? To.AddDays(-1) : To)} (UTC)";

    public static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A report as a file: PDF, Excel or CSV, by the format asked for.</summary>
public static class ReportFiles
{
    public static readonly string[] Formats = ["pdf", "xlsx", "csv"];

    /// <summary>The file, or null for a format that is not one of these.</summary>
    public static IResult? Result(ReportDocument doc, string? format, string name)
    {
        var file = Safe(name) + "-" + doc.From.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + doc.To.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        return (format ?? "pdf").ToLowerInvariant() switch
        {
            "pdf" => Results.File(ReportPdf.Render(doc), "application/pdf", file + ".pdf"),
            "xlsx" => Results.File(ReportXlsx.Render(doc), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx"),
            "csv" => Results.File(ReportCsv.Render(doc), "text/csv; charset=utf-8", file + ".csv"),
            _ => null,
        };
    }

    /// <summary>A file name from a title: letters, digits and dashes.</summary>
    public static string Safe(string name)
    {
        var s = new string([.. name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
        while (s.Contains("--", StringComparison.Ordinal))
        {
            s = s.Replace("--", "-", StringComparison.Ordinal);
        }
        s = s.Trim('-');
        return s.Length == 0 ? "report" : s.Length > 60 ? s[..60].Trim('-') : s;
    }

    /// <summary>A value as it reads in a PDF or on the page: 1,234; $1.23; 2.5 h; 12%; 7 Nov 2026 10:00.</summary>
    public static string Format(object? value, ReportKind kind)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            null => "",
            string s => s,
            DateTimeOffset t => t.UtcDateTime.ToString("d MMM yyyy HH:mm", inv),
            _ when kind == ReportKind.Money => Money(Convert.ToDecimal(value, inv)),
            _ when kind == ReportKind.Hours => Hours(Convert.ToDouble(value, inv)),
            _ when kind == ReportKind.Percent => Convert.ToDouble(value, inv).ToString("0", inv) + "%",
            _ when kind == ReportKind.Fraction => Convert.ToDouble(value, inv) is var d && double.IsNaN(d) ? "" : d.ToString(Math.Abs(d) >= 100 ? "#,##0" : "#,##0.###", inv),
            _ => Convert.ToDouble(value, inv).ToString("#,##0", inv),
        };
    }

    public static string Money(decimal v) => v.ToString(v is > 0 and < 0.01m ? "$0.####" : "$#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Seconds as hours and minutes: "2 h 05 min", "12 min".</summary>
    public static string Hours(double seconds)
    {
        var minutes = (long)Math.Round(seconds / 60);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    /// <summary>Tokens short: 950, 12.3K, 4.1M.</summary>
    public static string Compact(double v) => v switch
    {
        >= 1e9 => (v / 1e9).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "B",
        >= 1e6 => (v / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M",
        >= 1e4 => (v / 1e3).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K",
        _ => v.ToString("#,##0", System.Globalization.CultureInfo.InvariantCulture),
    };
}
