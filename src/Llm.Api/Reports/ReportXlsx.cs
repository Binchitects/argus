using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

using Branding;
namespace Llm.Api.Reports;

/// <summary>
/// A report as an Excel workbook, written here (no library): a Summary sheet (the title, the period and the figures), a sheet
/// for each chart's numbers over time, and one for each table, each with its header bold, frozen and filtered, its columns
/// wide enough, and real numbers (money, hours, times formatted as such) so they add up and chart in Excel.
/// </summary>
public static class ReportXlsx
{
    private const int Plain = 0, Header = 1, Whole = 2, Dollars = 3, HoursStyle = 4, PercentStyle = 5, TimeStyle = 6, TitleStyle = 7;

    private sealed class Strings
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        public List<string> All { get; } = [];

        public int Of(string s)
        {
            if (!_index.TryGetValue(s, out var i))
            {
                i = All.Count;
                All.Add(s);
                _index[s] = i;
            }
            return i;
        }
    }

    private sealed record Sheet(string Name, string Xml);

    public static byte[] Render(ReportDocument doc)
    {
        var strings = new Strings();
        var sheets = new List<Sheet> { new("Summary", Summary(doc, strings)) };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summary" };
        foreach (var chart in doc.Charts)
        {
            var columns = new List<ReportColumn> { new("Time", ReportKind.Time, 1.4) };
            columns.AddRange(chart.Series.Select(s => new ReportColumn(s, chart.Kind)));
            var rows = chart.Buckets.Select(b => (object?[])[b.At, .. b.Values.Select(v => (object?)v)]).ToList();
            sheets.Add(new(Name(chart.Title, names), Table(new ReportTable(chart.Title, columns, rows), strings)));
        }
        foreach (var table in doc.Tables)
        {
            sheets.Add(new(Name(table.Title, names), Table(table, strings)));
        }
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            Put("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>"""
                + string.Concat(sheets.Select((_, i) => $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""")) + "</Types>");
            Put("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>""");
            Put("docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>{X(doc.Title)}</dc:title><dc:creator>{X(doc.MadeBy ?? "{Brand.Product}")}</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">{doc.Made.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</dcterms:created></cp:coreProperties>""");
            Put("xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>"""
                + string.Concat(sheets.Select((s, i) => $"""<sheet name="{X(s.Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""")) + "</sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
                + string.Concat(sheets.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""))
                + $"""<Relationship Id="rId{sheets.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rId{sheets.Count + 2}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>""");
            Put("xl/styles.xml", Styles);
            for (var i = 0; i < sheets.Count; i++)
            {
                Put($"xl/worksheets/sheet{i + 1}.xml", sheets[i].Xml);
            }
            Put("xl/sharedStrings.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{strings.All.Count}" uniqueCount="{strings.All.Count}">"""
                + string.Concat(strings.All.Select(s => $"""<si><t xml:space="preserve">{X(s)}</t></si>""")) + "</sst>");
        }
        return stream.ToArray();
    }

    /// <summary>Number formats: whole numbers, dollars, hours (2 places), percent (as written, 0 to 100) and times.</summary>
    private const string Styles = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="4"><numFmt numFmtId="164" formatCode="&quot;$&quot;#,##0.00"/><numFmt numFmtId="165" formatCode="0.00"/><numFmt numFmtId="166" formatCode="0&quot;%&quot;"/><numFmt numFmtId="167" formatCode="yyyy-mm-dd hh:mm"/></numFmts><fonts count="3"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="14"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEEF0F4"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style="thin"><color rgb="FFC8CCD4"/></bottom><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="8"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/><xf numFmtId="3" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="167" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>""";

    private static string Summary(ReportDocument doc, Strings strings)
    {
        var rows = new StringBuilder();
        var r = 0;
        void Row(params (object? Value, int Style)[] cells)
        {
            r++;
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{r}\">");
            for (var c = 0; c < cells.Length; c++)
            {
                rows.Append(Cell(Ref(c, r), cells[c].Value, cells[c].Style, strings));
            }
            rows.Append("</row>");
        }
        Row((doc.Title, TitleStyle));
        Row((doc.Subtitle, Plain));
        Row(("From", Header), (doc.From, TimeStyle));
        Row(("To", Header), (doc.To, TimeStyle));
        Row(("Made", Header), (doc.Made, TimeStyle));
        r++;
        Row(("Figure", Header), ("Value", Header), ("Note", Header));
        foreach (var f in doc.Figures)
        {
            Row((f.Label, Plain), (f.Value, Plain), (f.Hint, Plain));
        }
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cols><col min="1" max="1" width="28" customWidth="1"/><col min="2" max="2" width="22" customWidth="1"/><col min="3" max="3" width="48" customWidth="1"/></cols><sheetData>{rows}</sheetData></worksheet>""";
    }

    private static string Table(ReportTable t, Strings strings)
    {
        var rows = new StringBuilder();
        rows.Append("<row r=\"1\">");
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var col = t.Columns[c];
            var header = col.Kind switch { ReportKind.Hours => col.Header + " (hours)", _ => col.Header };
            rows.Append(Cell(Ref(c, 1), header, Header, strings));
        }
        rows.Append("</row>");
        var widths = t.Columns.Select(c => (double)Math.Max(c.Header.Length + 4, c.Kind is ReportKind.Time ? 18 : 10)).ToArray();
        for (var i = 0; i < t.Rows.Count; i++)
        {
            var row = t.Rows[i];
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{i + 2}\">");
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var kind = t.Columns[c].Kind;
                var value = c < row.Length ? row[c] : null;
                if (kind == ReportKind.Hours && value is not null and not string)
                {
                    value = Convert.ToDouble(value, CultureInfo.InvariantCulture) / 3600;
                }
                rows.Append(Cell(Ref(c, i + 2), value, StyleOf(kind), strings));
                if (value is string s)
                {
                    widths[c] = Math.Min(60, Math.Max(widths[c], s.Length + 2));
                }
            }
            rows.Append("</row>");
        }
        var last = Ref(t.Columns.Count - 1, Math.Max(1, t.Rows.Count + 1));
        var cols = string.Concat(widths.Select((w, i) => $"""<col min="{i + 1}" max="{i + 1}" width="{w.ToString("0.#", CultureInfo.InvariantCulture)}" customWidth="1"/>"""));
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>{cols}</cols><sheetData>{rows}</sheetData><autoFilter ref="A1:{last}"/></worksheet>""";
    }

    private static int StyleOf(ReportKind kind) => kind switch
    {
        ReportKind.Number => Whole,
        ReportKind.Money => Dollars,
        ReportKind.Hours => HoursStyle,
        ReportKind.Percent => PercentStyle,
        ReportKind.Time => TimeStyle,
        _ => Plain,
    };

    private static string Cell(string reference, object? value, int style, Strings strings) => value switch
    {
        null => $"<c r=\"{reference}\" s=\"{style}\"/>",
        string s => $"<c r=\"{reference}\" s=\"{(style is TitleStyle or Header ? style : Plain)}\" t=\"s\"><v>{strings.Of(s)}</v></c>",
        DateTimeOffset t => $"<c r=\"{reference}\" s=\"{TimeStyle}\"><v>{(t.UtcDateTime - new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc)).TotalDays.ToString("0.########", CultureInfo.InvariantCulture)}</v></c>",
        bool b => $"<c r=\"{reference}\" s=\"{style}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>",
        _ => $"<c r=\"{reference}\" s=\"{style}\"><v>{Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)}</v></c>",
    };

    private static string Ref(int column, int row)
    {
        var name = "";
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
        {
            name = (char)('A' + (c - 1) % 26) + name;
        }
        return name + row.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A sheet's name: at most 31 characters, none of []:*?/\, and not one already taken.</summary>
    private static string Name(string title, HashSet<string> taken)
    {
        var clean = new string([.. title.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\'))]).Trim();
        clean = clean.Length == 0 ? "Sheet" : clean.Length > 28 ? clean[..28].Trim() : clean;
        var name = clean;
        for (var i = 2; !taken.Add(name); i++)
        {
            name = $"{clean} {i}";
        }
        return name;
    }

    /// <summary>Text for XML, without the characters XML may not hold.</summary>
    private static string X(string s) => SecurityElement.Escape(new string([.. s.Where(c => c is '\t' or '\n' or '\r' || c >= ' ')])) ?? "";
}
