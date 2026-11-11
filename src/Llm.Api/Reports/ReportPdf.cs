using System.Globalization;
using System.IO.Compression;
using System.Text;

using Branding;
namespace Llm.Api.Reports;

/// <summary>
/// A report as a PDF, drawn here (no library, nothing to install): A4 pages with the title and the period at the top, the
/// figures as tiles, each chart as stacked bars over time with its scale and legend, and each table with its header repeated
/// on every page it runs over, numbers to the right; a footer with the report and the page on each page. The text is in the
/// standard PDF fonts (Helvetica), which every reader has: letters outside Western European scripts show as "?" here, so
/// names in those scripts read best in the Excel and CSV files, which keep them as they are.
/// </summary>
public static class ReportPdf
{
    private const double PageW = 595.28, PageH = 841.89, Left = 48, Right = 48, Top = 52, Bottom = 58;
    private const double Width = PageW - Left - Right;

    private static readonly (double R, double G, double B) Ink = (0.12, 0.14, 0.19), Muted = (0.42, 0.45, 0.50), Accent = (0.31, 0.27, 0.90),
        Tile = (0.96, 0.965, 0.98), Rule = (0.85, 0.87, 0.90), HeaderFill = (0.93, 0.94, 0.96), Zebra = (0.98, 0.983, 0.988);

    /// <summary>The series' colours, in order: indigo, sky, amber, emerald, rose, slate.</summary>
    private static readonly (double R, double G, double B)[] Palette =
        [(0.31, 0.27, 0.90), (0.05, 0.65, 0.91), (0.96, 0.62, 0.04), (0.06, 0.73, 0.51), (0.94, 0.27, 0.27), (0.39, 0.45, 0.55)];

    private sealed class Page
    {
        public StringBuilder Content { get; } = new();
    }

    private sealed class Canvas
    {
        public List<Page> Pages { get; } = [];
        public double Y { get; set; }
        private Page Current => Pages[^1];

        public void NewPage()
        {
            Pages.Add(new Page());
            Y = PageH - Top;
            // A thin band of the accent colour across the top of every page.
            Fill(Accent, 0, PageH - 6, PageW, 6);
        }

        /// <summary>Room for <paramref name="height"/> more below, else a new page.</summary>
        public bool Need(double height)
        {
            if (Y - height >= Bottom)
            {
                return false;
            }
            NewPage();
            return true;
        }

        public void Fill((double R, double G, double B) c, double x, double y, double w, double h) =>
            Current.Content.Append(CultureInfo.InvariantCulture, $"{N(c.R)} {N(c.G)} {N(c.B)} rg {N(x)} {N(y)} {N(w)} {N(h)} re f\n");

        public void Line((double R, double G, double B) c, double x1, double y1, double x2, double y2, double width = 0.5) =>
            Current.Content.Append(CultureInfo.InvariantCulture, $"{N(c.R)} {N(c.G)} {N(c.B)} RG {N(width)} w {N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S\n");

        public void Text(string text, double x, double y, double size, bool bold = false, (double R, double G, double B)? colour = null)
        {
            var c = colour ?? Ink;
            Current.Content.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(c.R)} {N(c.G)} {N(c.B)} rg {N(x)} {N(y)} Td ").Append(Literal(text)).Append(" Tj ET\n");
        }

        /// <summary>Text ending at <paramref name="right"/>.</summary>
        public void TextRight(string text, double right, double y, double size, bool bold = false, (double R, double G, double B)? colour = null) =>
            Text(text, right - Measure(text, size, bold), y, size, bold, colour);
    }

    public static byte[] Render(ReportDocument doc)
    {
        var canvas = new Canvas();
        canvas.NewPage();
        Heading(canvas, doc);
        Figures(canvas, doc.Figures);
        foreach (var chart in doc.Charts)
        {
            Chart(canvas, chart, doc);
        }
        foreach (var table in doc.Tables)
        {
            Table(canvas, table);
        }
        for (var i = 0; i < canvas.Pages.Count; i++)
        {
            Footer(canvas.Pages[i], doc, i + 1, canvas.Pages.Count);
        }
        return Write(canvas.Pages, doc);
    }

    private static void Heading(Canvas c, ReportDocument doc)
    {
        c.Text(Branding.Brand.Product.ToUpperInvariant(), Left, c.Y - 8, 7.5, bold: true, Muted);
        c.TextRight("Made " + doc.Made.UtcDateTime.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture) + (doc.MadeBy is { } by ? " for " + by : ""),
            PageW - Right, c.Y - 8, 7.5, colour: Muted);
        c.Y -= 34;
        foreach (var line in Wrap(doc.Title, 22, true, Width))
        {
            c.Text(line, Left, c.Y, 22, bold: true);
            c.Y -= 26;
        }
        foreach (var line in Wrap(doc.Subtitle, 10, false, Width))
        {
            c.Text(line, Left, c.Y, 10, colour: Muted);
            c.Y -= 14;
        }
        c.Y -= 2;
        c.Text(doc.Period, Left, c.Y, 9.5, bold: true, Accent);
        c.Y -= 22;
    }

    private static void Figures(Canvas c, IReadOnlyList<ReportFigure> figures)
    {
        const int PerRow = 4;
        const double Gap = 10, H = 60;
        var w = (Width - (PerRow - 1) * Gap) / PerRow;
        for (var i = 0; i < figures.Count; i += PerRow)
        {
            c.Need(H + Gap);
            for (var j = 0; j < PerRow && i + j < figures.Count; j++)
            {
                var f = figures[i + j];
                var x = Left + j * (w + Gap);
                c.Fill(Tile, x, c.Y - H, w, H);
                c.Fill(Palette[(i + j) % Palette.Length], x, c.Y - H, 2.5, H);
                c.Text(Fit(f.Label.ToUpperInvariant(), 7, true, w - 18), x + 10, c.Y - 15, 7, bold: true, Muted);
                c.Text(Fit(f.Value, 16, true, w - 18), x + 10, c.Y - 36, 16, bold: true);
                if (f.Hint is { Length: > 0 } hint)
                {
                    c.Text(Fit(hint, 7, false, w - 18), x + 10, c.Y - 51, 7, colour: Muted);
                }
            }
            c.Y -= H + Gap;
        }
        c.Y -= 8;
    }

    private static void Chart(Canvas c, ReportChart chart, ReportDocument doc)
    {
        const double PlotH = 140, AxisW = 46;
        var names = chart.Series.Select(n => Fit(n, 8, false, 150)).ToList();
        var legendW = names.Sum(n => Measure(n, 8, false) + 22);
        var beside = legendW <= Width - Measure(chart.Title, 11, true) - 24;
        // A legend too long for beside the title goes under the plot, as many lines as it takes.
        var under = 0;
        for (double x = 0, i = 0; !beside && i < names.Count; i++)
        {
            var w = Measure(names[(int)i], 8, false) + 22;
            if (x == 0 || x + w > Width)
            {
                under++;
                x = 0;
            }
            x += w;
        }
        c.Need(PlotH + 70 + under * 12 + (chart.Note is null ? 0 : 12));
        c.Text(Fit(chart.Title, 11, true, Width), Left, c.Y - 12, 11, bold: true);
        if (beside)
        {
            var lx = PageW - Right;
            for (var s = names.Count - 1; s >= 0; s--)
            {
                lx -= Measure(names[s], 8, false);
                c.Text(names[s], lx, c.Y - 11, 8, colour: Muted);
                lx -= 12;
                c.Fill(Palette[s % Palette.Length], lx, c.Y - 11, 8, 8);
                lx -= 10;
            }
        }
        c.Y -= 26;
        var top = c.Y;
        var plotX = Left + AxisW;
        var plotW = Width - AxisW;
        var bottom = top - PlotH;
        var buckets = chart.Buckets;
        static double Real(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
        var most = buckets.Count == 0 ? 0 : chart.Stacked ? buckets.Max(b => b.Values.Sum(Real)) : buckets.Max(b => b.Values.Length == 0 ? 0 : b.Values.Max(Real));
        var scale = Nice(most);
        for (var g = 0; g <= 4; g++)
        {
            var y = bottom + PlotH * g / 4;
            c.Line(g == 0 ? Muted : Rule, plotX, y, plotX + plotW, y, g == 0 ? 0.7 : 0.4);
            c.TextRight(Axis(scale * g / 4, chart.Kind), plotX - 6, y - 3, 7, colour: Muted);
        }
        if (buckets.Count == 0 || most <= 0)
        {
            c.Text("Nothing in this time range.", plotX + 10, bottom + PlotH / 2, 9, colour: Muted);
        }
        else
        {
            var step = plotW / buckets.Count;
            var barW = Math.Max(1, step * 0.72);
            for (var s = 0; !chart.Stacked && s < chart.Series.Count; s++)
            {
                // Lines, each on its own: a gap where a value was not measured.
                (double X, double Y)? last = null;
                for (var i = 0; i < buckets.Count; i++)
                {
                    var v = s < buckets[i].Values.Length ? buckets[i].Values[s] : double.NaN;
                    if (double.IsNaN(v) || double.IsInfinity(v))
                    {
                        last = null;
                        continue;
                    }
                    var point = (X: plotX + i * step + step / 2, Y: bottom + PlotH * Math.Max(0, v) / scale);
                    if (last is { } from)
                    {
                        c.Line(Palette[s % Palette.Length], from.X, from.Y, point.X, point.Y, 1.1);
                    }
                    last = point;
                }
            }
            for (var i = 0; chart.Stacked && i < buckets.Count; i++)
            {
                var y = bottom;
                for (var s = 0; s < buckets[i].Values.Length; s++)
                {
                    var h = PlotH * Real(buckets[i].Values[s]) / scale;
                    if (h > 0)
                    {
                        c.Fill(Palette[s % Palette.Length], plotX + i * step + (step - barW) / 2, y, barW, h);
                        y += h;
                    }
                }
            }
            // About six times along the bottom.
            var span = doc.To - doc.From;
            var every = Math.Max(1, (int)Math.Ceiling(buckets.Count / 6.0));
            for (var i = 0; i < buckets.Count; i += every)
            {
                var at = buckets[i].At.UtcDateTime;
                var label = span <= TimeSpan.FromDays(2) ? at.ToString("HH:mm", CultureInfo.InvariantCulture) : at.ToString("d MMM", CultureInfo.InvariantCulture);
                var x = plotX + i * step + step / 2 - Measure(label, 7, false) / 2;
                c.Text(label, Math.Max(plotX, x), bottom - 12, 7, colour: Muted);
            }
        }
        c.Y = bottom - 26;
        if (!beside)
        {
            double x = 0;
            for (var s = 0; s < names.Count; s++)
            {
                var w = Measure(names[s], 8, false) + 22;
                if (x > 0 && x + w > Width)
                {
                    c.Y -= 12;
                    x = 0;
                }
                c.Fill(Palette[s % Palette.Length], Left + x, c.Y - 1, 8, 8);
                c.Text(names[s], Left + x + 12, c.Y, 8, colour: Muted);
                x += w;
            }
            c.Y -= 14;
        }
        if (chart.Note is { Length: > 0 } note)
        {
            c.Text(Fit(note, 7.5, false, Width), Left, c.Y, 7.5, colour: Muted);
            c.Y -= 12;
        }
        c.Y -= 8;
    }

    private static void Table(Canvas c, ReportTable t)
    {
        const double RowH = 16, HeadH = 19, Pad = 5;
        var total = t.Columns.Sum(col => col.Width);
        var widths = t.Columns.Select(col => Width * col.Width / total).ToArray();
        void Head(bool continued)
        {
            c.Text(t.Title + (continued ? " (continued)" : ""), Left, c.Y - 12, 11, bold: true);
            c.Y -= 22;
            c.Fill(HeaderFill, Left, c.Y - HeadH, Width, HeadH);
            var x = Left;
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var label = Fit(t.Columns[i].Header, 7.5, true, widths[i] - 2 * Pad);
                if (t.Columns[i].Kind == ReportKind.Text)
                {
                    c.Text(label, x + Pad, c.Y - 12.5, 7.5, bold: true, Muted);
                }
                else
                {
                    c.TextRight(label, x + widths[i] - Pad, c.Y - 12.5, 7.5, bold: true, Muted);
                }
                x += widths[i];
            }
            c.Y -= HeadH;
        }
        c.Need(22 + HeadH + RowH * Math.Min(3, Math.Max(1, t.Rows.Count)) + 8);
        Head(false);
        if (t.Rows.Count == 0)
        {
            c.Text("Nothing in this time range.", Left + Pad, c.Y - 12, 8.5, colour: Muted);
            c.Y -= RowH;
        }
        for (var r = 0; r < t.Rows.Count; r++)
        {
            if (c.Need(RowH))
            {
                Head(true);
            }
            if (r % 2 == 1)
            {
                c.Fill(Zebra, Left, c.Y - RowH, Width, RowH);
            }
            var x = Left;
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var value = i < t.Rows[r].Length ? t.Rows[r][i] : null;
                var text = Fit(ReportFiles.Format(value, t.Columns[i].Kind), 8, false, widths[i] - 2 * Pad);
                if (t.Columns[i].Kind == ReportKind.Text || value is string)
                {
                    c.Text(text, x + Pad, c.Y - 11, 8);
                }
                else
                {
                    c.TextRight(text, x + widths[i] - Pad, c.Y - 11, 8);
                }
                x += widths[i];
            }
            c.Y -= RowH;
        }
        c.Line(Rule, Left, c.Y, Left + Width, c.Y);
        if (t.Note is { Length: > 0 } note)
        {
            foreach (var line in Wrap(note, 7.5, false, Width))
            {
                c.Y -= 11;
                c.Text(line, Left, c.Y, 7.5, colour: Muted);
            }
        }
        c.Y -= 22;
    }

    private static void Footer(Page page, ReportDocument doc, int number, int count)
    {
        var sb = page.Content;
        var y = Bottom - 26;
        sb.Append(CultureInfo.InvariantCulture, $"{N(Rule.R)} {N(Rule.G)} {N(Rule.B)} RG 0.5 w {N(Left)} {N(y + 12)} m {N(PageW - Right)} {N(y + 12)} l S\n");
        var left = Fit(doc.Title + "  ·  " + doc.Period, 7.5, false, Width - 80);
        sb.Append(CultureInfo.InvariantCulture, $"BT /F1 7.5 Tf {N(Muted.R)} {N(Muted.G)} {N(Muted.B)} rg {N(Left)} {N(y)} Td ").Append(Literal(left)).Append(" Tj ET\n");
        var right = $"Page {number} of {count}";
        sb.Append(CultureInfo.InvariantCulture, $"BT /F1 7.5 Tf {N(Muted.R)} {N(Muted.G)} {N(Muted.B)} rg {N(PageW - Right - Measure(right, 7.5, false))} {N(y)} Td ")
            .Append(Literal(right)).Append(" Tj ET\n");
    }

    /// <summary>The axis' top: the most, rounded up to 1, 2, 2.5 or 5 times a power of ten.</summary>
    internal static double Nice(double most)
    {
        if (most <= 0)
        {
            return 1;
        }
        var power = Math.Pow(10, Math.Floor(Math.Log10(most)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (step * power >= most)
            {
                return step * power;
            }
        }
        return 10 * power;
    }

    private static string Axis(double v, ReportKind kind) => kind switch
    {
        ReportKind.Money => v == 0 ? "$0" : v < 1 ? "$" + v.ToString("0.###", CultureInfo.InvariantCulture) : "$" + ReportFiles.Compact(v),
        ReportKind.Hours => ReportFiles.Hours(v),
        _ => v is > 0 and < 10 && v != Math.Floor(v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : ReportFiles.Compact(v),
    };

    // ------------------------------------------------------------------ text --

    /// <summary>Helvetica's and Helvetica-Bold's widths (thousandths of the size) for ASCII 32 to 126, from their AFM files.</summary>
    private static readonly short[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly short[] Bold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    /// <summary>The characters of Windows-1252 above 127 that are not Latin-1's, by their code there.</summary>
    private static readonly Dictionary<char, byte> Specials = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['„'] = 0x84, ['…'] = 0x85, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94,
        ['™'] = 0x99, ['Œ'] = 0x8C, ['œ'] = 0x9C, ['Š'] = 0x8A, ['š'] = 0x9A, ['Ž'] = 0x8E, ['ž'] = 0x9E, ['Ÿ'] = 0x9F, ['ˆ'] = 0x88, ['˜'] = 0x98, ['‰'] = 0x89,
        ['†'] = 0x86, ['‡'] = 0x87, ['‹'] = 0x8B, ['›'] = 0x9B, ['ƒ'] = 0x83,
    };

    /// <summary>The text in the fonts' encoding (WinAnsi): a letter with an accent it lacks loses the accent; one it cannot show is "?".</summary>
    internal static byte[] Encode(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= ' ' and <= '~' || ch is >= ' ' and <= 'ÿ')
            {
                bytes.Add((byte)ch);
            }
            else if (Specials.TryGetValue(ch, out var b))
            {
                bytes.Add(b);
            }
            else if (ch is '\t' or '\n' or '\r')
            {
                bytes.Add((byte)' ');
            }
            else if (char.IsLowSurrogate(ch))
            {
                // The second half of a character already shown as "?".
            }
            else
            {
                var plain = ch.ToString().Normalize(NormalizationForm.FormD);
                bytes.Add(plain.Length > 0 && plain[0] is >= ' ' and <= '~' ? (byte)plain[0] : (byte)'?');
            }
        }
        return [.. bytes];
    }

    internal static double Measure(string text, double size, bool bold)
    {
        var widths = bold ? Bold : Regular;
        double sum = 0;
        foreach (var b in Encode(text))
        {
            sum += b is >= 32 and <= 126 ? widths[b - 32] : b is 0x85 or 0x97 or 0x89 ? 1000 : b == 0x95 ? 350 : b is 0x91 or 0x92 ? 222 : 556;
        }
        return sum * size / 1000;
    }

    /// <summary>The text, cut with "…" to fit the width.</summary>
    internal static string Fit(string text, double size, bool bold, double width)
    {
        if (Measure(text, size, bold) <= width)
        {
            return text;
        }
        var cut = text;
        while (cut.Length > 0 && Measure(cut + "…", size, bold) > width)
        {
            cut = cut[..^1];
        }
        return cut.TrimEnd() + "…";
    }

    /// <summary>The text in lines no wider than the width, broken between words.</summary>
    internal static List<string> Wrap(string text, double size, bool bold, double width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (Measure(next, size, bold) <= width || line.Length == 0)
            {
                line = next;
            }
            else
            {
                lines.Add(line);
                line = word;
            }
        }
        if (line.Length > 0)
        {
            lines.Add(Fit(line, size, bold, width));
        }
        return lines;
    }

    private static string Literal(string text)
    {
        var sb = new StringBuilder("(");
        foreach (var b in Encode(text))
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    sb.Append('\\').Append((char)b);
                    break;
                case < 32 or > 126:
                    sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    sb.Append((char)b);
                    break;
            }
        }
        return sb.Append(')').ToString();
    }

    private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ file --

    private static byte[] Write(List<Page> pages, ReportDocument doc)
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            output.Write(bytes);
        }
        void Object(string body)
        {
            offsets.Add(output.Position);
            Ascii($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        // The header, and a comment of bytes past 127: the file is binary.
        Ascii("%PDF-1.4\n");
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        // 1 catalog, 2 pages, 3 and 4 fonts, 5 info; then each page and its content.
        var kids = string.Join(" ", pages.Select((_, i) => $"{6 + 2 * i} 0 R"));
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        Object($"<< /Title {Utf16(doc.Title)} /Subject {Utf16(doc.Subtitle)} /Producer ({Brand.Product}) /CreationDate (D:{doc.Made.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}Z) >>");
        for (var i = 0; i < pages.Count; i++)
        {
            Object($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageW)} {N(PageH)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {7 + 2 * i} 0 R >>");
            byte[] packed;
            using (var buffer = new MemoryStream())
            {
                using (var z = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    z.Write(Encoding.Latin1.GetBytes(pages[i].Content.ToString()));
                }
                packed = buffer.ToArray();
            }
            offsets.Add(output.Position);
            Ascii($"{offsets.Count} 0 obj\n<< /Length {packed.Length} /Filter /FlateDecode >>\nstream\n");
            output.Write(packed);
            Ascii("\nendstream\nendobj\n");
        }
        var xref = output.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            Ascii($"{o:D10} 00000 n \n");
        }
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>A text string in UTF-16 (any script), for the document's properties.</summary>
    private static string Utf16(string s) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";
}
