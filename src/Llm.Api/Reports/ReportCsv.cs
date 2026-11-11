using System.Globalization;
using System.Text;

namespace Llm.Api.Reports;

/// <summary>
/// A report as CSV for a spreadsheet: its title and period, its figures (label, value), then each table under its title (and its note),
/// a blank line between. UTF-8 with its byte order mark, so Excel reads names in any script. Numbers are plain (no thousands
/// separator, money in dollars, hours as hours), times in ISO 8601. A cell that would start a formula (=, +, -, @) is kept
/// as text (a quote before it), so a name cannot run in the spreadsheet.
/// </summary>
public static class ReportCsv
{
    public static byte[] Render(ReportDocument doc)
    {
        var sb = new StringBuilder();
        Line(sb, [doc.Title]);
        Line(sb, [doc.Subtitle]);
        Line(sb, ["From", doc.From.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), "To", doc.To.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)]);
        sb.Append("\r\n");
        foreach (var f in doc.Figures)
        {
            Line(sb, [f.Label, f.Value]);
        }
        var charts = doc.ChartsAsTables
            ? doc.Charts.Select(c => new ReportTable(c.Title, [new ReportColumn("Time", ReportKind.Time), .. c.Series.Select(x => new ReportColumn(x, c.Kind))],
                [.. c.Buckets.Select(b => (object?[])[b.At, .. b.Values.Select(v => double.IsNaN(v) ? null : (object?)v)])], c.Note))
            : [];
        foreach (var t in charts.Concat(doc.Tables))
        {
            sb.Append("\r\n");
            Line(sb, [t.Title]);
            if (t.Note is { Length: > 0 } note)
            {
                Line(sb, [note]);
            }
            Line(sb, [.. t.Columns.Select(c => c.Kind == ReportKind.Hours ? c.Header + " (hours)" : c.Kind == ReportKind.Money ? c.Header + " ($)" : c.Header)]);
            foreach (var row in t.Rows)
            {
                Line(sb, [.. row.Select((v, i) => Cell(v, t.Columns[i].Kind))]);
            }
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Cell(object? v, ReportKind kind) => v switch
    {
        null => "",
        string s => s,
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        _ when kind == ReportKind.Hours => (Convert.ToDouble(v, CultureInfo.InvariantCulture) / 3600).ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static void Line(StringBuilder sb, IEnumerable<string> cells)
    {
        sb.AppendJoin(',', cells.Select(Quote)).Append("\r\n");
    }

    public static string Quote(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            s = "'" + s;
        }
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }
}
