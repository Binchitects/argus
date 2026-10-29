using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Llm.Api.Insights;

/// <summary>A table read from a file: its name, its columns' headers, and its rows (text, numbers, truth, times; null when empty).</summary>
public sealed record Sheet(string Name, List<string> Header, List<object?[]> Rows);

/// <summary>
/// Data files read as tables: CSV and TSV (the delimiter found from the first line), Excel workbooks (.xlsx, each sheet a
/// table, dates by their cells' formats), and JSON (an array of objects, or an object holding one) or JSON Lines. The
/// first row of a sheet or delimited file is its header.
/// </summary>
public static class TabularFiles
{
    public const int MostRows = 1_000_000;
    public const int MostColumns = 500;
    public static readonly string[] Extensions = [".csv", ".tsv", ".tab", ".txt", ".xlsx", ".json", ".jsonl", ".ndjson"];

    public static List<Sheet> Read(string fileName, byte[] bytes)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var sheets = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" or ".tsv" or ".tab" or ".txt" => [Delimited(name, Text(bytes))],
            ".xlsx" => Xlsx(name, bytes),
            ".json" => [Json(name, bytes)],
            ".jsonl" or ".ndjson" => [JsonLines(name, Text(bytes))],
            ".xls" or ".ods" or ".numbers" => throw new DataException($"{fileName}: save it as .xlsx or .csv, and upload that."),
            _ => throw new DataException($"{fileName}: a data file is CSV, TSV, Excel (.xlsx), JSON or JSON Lines."),
        };
        return [.. sheets.Where(s => s.Header.Count > 0)];
    }

    /// <summary>UTF-8 (its byte order mark dropped), else Windows-1252's Latin-1 subset.</summary>
    private static string Text(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ------------------------------------------------------------- delimited --

    private static readonly char[] Delimiters = [',', ';', '\t', '|'];

    private static Sheet Delimited(string name, string text)
    {
        var first = text.AsSpan(0, Math.Min(text.Length, 64 * 1024));
        var line = (first.IndexOfAny('\r', '\n') is var end and >= 0 ? first[..end] : first).ToString();
        var delimiter = Delimiters.MaxBy(d => Outside(line, d));
        var rows = new List<object?[]>();
        List<string>? header = null;
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var any = false;
        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
        }
        void EndRow()
        {
            EndCell();
            if (row.Count > 1 || row[0].Length > 0)
            {
                if (header is null)
                {
                    header = [.. row.Take(MostColumns)];
                }
                else if (rows.Count < MostRows)
                {
                    rows.Add([.. row.Take(MostColumns).Select(v => v.Length == 0 ? null : (object?)v)]);
                }
            }
            row = [];
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            any = true;
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                EndCell();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                EndRow();
                any = false;
            }
            else
            {
                cell.Append(c);
            }
        }
        if (any)
        {
            EndRow();
        }
        return new Sheet(name, header ?? [], rows);
    }

    /// <summary>How often the delimiter comes outside quotes on the line.</summary>
    private static int Outside(string line, char delimiter)
    {
        var n = 0;
        var quoted = false;
        foreach (var c in line)
        {
            quoted ^= c == '"';
            n += !quoted && c == delimiter ? 1 : 0;
        }
        return n;
    }

    // ----------------------------------------------------------------- Excel --

    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static XmlReader Open(ZipArchive zip, string path) =>
        XmlReader.Create(zip.GetEntry(path)!.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });

    private static List<Sheet> Xlsx(string name, byte[] bytes)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new DataException($"{name}.xlsx is not an Excel workbook.");
        }
        using (zip)
        {
            if (zip.GetEntry("xl/workbook.xml") is null)
            {
                throw new DataException($"{name}.xlsx is not an Excel workbook.");
            }
            var shared = SharedStrings(zip);
            var dates = DateStyles(zip);
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (zip.GetEntry("xl/_rels/workbook.xml.rels") is not null)
            {
                using var r = Open(zip, "xl/_rels/workbook.xml.rels");
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") is { } id && r.GetAttribute("Target") is { } target)
                    {
                        targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                    }
                }
            }
            var sheets = new List<(string Name, string Path)>();
            using (var r = Open(zip, "xl/workbook.xml"))
            {
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet" && r.NamespaceURI == S && r.GetAttribute("id", R) is { } rid && targets.TryGetValue(rid, out var path))
                    {
                        sheets.Add((r.GetAttribute("name") ?? $"Sheet{sheets.Count + 1}", path));
                    }
                }
            }
            return [.. sheets.Where(s => zip.GetEntry(s.Path) is not null).Select(s => Worksheet(sheets.Count == 1 ? name : $"{name} {s.Name}", zip, s.Path, shared, dates))];
        }
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is null)
        {
            return shared;
        }
        using var r = Open(zip, "xl/sharedStrings.xml");
        var item = new StringBuilder();
        var inText = false;
        var phonetic = 0;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == S)
            {
                switch (r.LocalName)
                {
                    case "si":
                        item.Clear();
                        break;
                    case "rPh":
                        phonetic += r.IsEmptyElement ? 0 : 1;
                        break;
                    case "t":
                        inText = !r.IsEmptyElement && phonetic == 0;
                        break;
                }
            }
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace && inText)
            {
                item.Append(r.Value);
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == S)
            {
                switch (r.LocalName)
                {
                    case "t":
                        inText = false;
                        break;
                    case "rPh":
                        phonetic--;
                        break;
                    case "si":
                        shared.Add(item.ToString());
                        break;
                }
            }
        }
        return shared;
    }

    /// <summary>Which cell styles (by their index) show a number as a date or a time: built-in formats, and custom ones with days, years or hours.</summary>
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var dates = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is null)
        {
            return dates;
        }
        var custom = new Dictionary<int, string>();
        var formats = new List<int>();
        var inXfs = false;
        using (var r = Open(zip, "xl/styles.xml"))
        {
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == S)
                {
                    if (r.LocalName == "numFmt" && int.TryParse(r.GetAttribute("numFmtId"), CultureInfo.InvariantCulture, out var id))
                    {
                        custom[id] = r.GetAttribute("formatCode") ?? "";
                    }
                    else if (r.LocalName == "cellXfs")
                    {
                        inXfs = !r.IsEmptyElement;
                    }
                    else if (r.LocalName == "xf" && inXfs)
                    {
                        formats.Add(int.TryParse(r.GetAttribute("numFmtId"), CultureInfo.InvariantCulture, out var f) ? f : 0);
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "cellXfs")
                {
                    inXfs = false;
                }
            }
        }
        static bool IsDate(string code)
        {
            var plain = new StringBuilder();
            var quoted = false;
            var bracket = false;
            foreach (var c in code)
            {
                quoted ^= c == '"';
                bracket = c == '[' || (bracket && c != ']');
                if (!quoted && !bracket)
                {
                    plain.Append(char.ToLowerInvariant(c));
                }
            }
            var p = plain.ToString();
            return p.Contains('y') || p.Contains('d') || (p.Contains('h') && p.Contains(':'));
        }
        for (var i = 0; i < formats.Count; i++)
        {
            var f = formats[i];
            if (f is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58 || (custom.TryGetValue(f, out var code) && IsDate(code)))
            {
                dates.Add(i);
            }
        }
        return dates;
    }

    private static int ColumnOf(string reference)
    {
        var n = 0;
        foreach (var c in reference)
        {
            if (c is < 'A' or > 'Z')
            {
                break;
            }
            n = (n * 26) + (c - 'A' + 1);
        }
        return n - 1;
    }

    private static Sheet Worksheet(string name, ZipArchive zip, string path, List<string> shared, HashSet<int> dates)
    {
        List<string>? header = null;
        var rows = new List<object?[]>();
        var row = new SortedDictionary<int, object?>();
        string? type = null;
        var style = 0;
        var column = -1;
        var value = new StringBuilder();
        var inValue = false;
        void EndRow()
        {
            if (row.Count == 0 || row.Values.All(v => v is null or ""))
            {
                row.Clear();
                return;
            }
            if (header is null)
            {
                var width = Math.Min(row.Keys.Max() + 1, MostColumns);
                header = [.. Enumerable.Range(0, width).Select(i => row.TryGetValue(i, out var h) ? Convert.ToString(h, CultureInfo.InvariantCulture) ?? "" : "")];
            }
            else if (rows.Count < MostRows)
            {
                var cells = new object?[Math.Min(Math.Max(header.Count, row.Keys.Max() + 1), MostColumns)];
                foreach (var (i, v) in row.Where(x => x.Key < cells.Length))
                {
                    cells[i] = v is "" ? null : v;
                }
                rows.Add(cells);
            }
            row.Clear();
        }
        using var r = Open(zip, path);
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == S)
            {
                switch (r.LocalName)
                {
                    case "row":
                        row.Clear();
                        column = -1;
                        if (r.IsEmptyElement)
                        {
                            EndRow();
                        }
                        break;
                    case "c":
                        type = r.GetAttribute("t");
                        style = int.TryParse(r.GetAttribute("s"), CultureInfo.InvariantCulture, out var s) ? s : 0;
                        column = r.GetAttribute("r") is { } reference ? ColumnOf(reference) : column + 1;
                        value.Clear();
                        break;
                    case "v" or "t":
                        inValue = !r.IsEmptyElement;
                        break;
                }
            }
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace && inValue)
            {
                value.Append(r.Value);
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == S)
            {
                switch (r.LocalName)
                {
                    case "v" or "t":
                        inValue = false;
                        break;
                    case "c":
                        var text = value.ToString();
                        row[column] = type switch
                        {
                            "s" => int.TryParse(text, CultureInfo.InvariantCulture, out var i) && i < shared.Count ? shared[i] : "",
                            "b" => text == "1",
                            "str" or "inlineStr" => text,
                            "e" => null,
                            "d" => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : text,
                            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                                ? dates.Contains(style) && n is > -657435 and < 2958466 ? DateTime.FromOADate(n) : n
                                : null,
                        };
                        break;
                    case "row":
                        EndRow();
                        break;
                }
            }
        }
        return new Sheet(name, header ?? [], rows);
    }

    // ------------------------------------------------------------------ JSON --

    private static Sheet Json(string name, byte[] bytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new DataException($"{name}.json is not JSON: {ex.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            // An array of objects, or an object that holds one ({"data": [...]}), or one object (one row).
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList()
                : root.ValueKind == JsonValueKind.Object && root.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object)) is { Value.ValueKind: JsonValueKind.Array } held
                    ? held.Value.EnumerateArray().ToList()
                    : [root];
            return Objects(name, items);
        }
    }

    private static Sheet JsonLines(string name, string text)
    {
        var items = new List<JsonElement>();
        foreach (var (line, n) in text.Split('\n').Select((l, i) => (l.Trim(), i + 1)).Where(x => x.Item1.Length > 0))
        {
            try
            {
                items.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
            catch (JsonException ex)
            {
                throw new DataException($"{name}: line {n} is not JSON: {ex.Message}");
            }
        }
        return Objects(name, items);
    }

    private static Sheet Objects(string name, List<JsonElement> items)
    {
        var header = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in items.Where(i => i.ValueKind == JsonValueKind.Object).Take(MostRows))
        {
            foreach (var p in o.EnumerateObject())
            {
                if (!index.ContainsKey(p.Name) && header.Count < MostColumns)
                {
                    index[p.Name] = header.Count;
                    header.Add(p.Name);
                }
            }
        }
        if (header.Count == 0 && items.Count > 0)
        {
            // Plain values: one column.
            header.Add("value");
            return new Sheet(name, header, [.. items.Take(MostRows).Select(i => new[] { Value(i) })]);
        }
        var rows = new List<object?[]>();
        foreach (var o in items.Where(i => i.ValueKind == JsonValueKind.Object).Take(MostRows))
        {
            var cells = new object?[header.Count];
            foreach (var p in o.EnumerateObject())
            {
                if (index.TryGetValue(p.Name, out var i))
                {
                    cells[i] = Value(p.Value);
                }
            }
            rows.Add(cells);
        }
        return new Sheet(name, header, rows);
    }

    private static object? Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.TryGetDecimal(out var d) ? d : e.GetDouble(),
        JsonValueKind.String => e.GetString() is { Length: > 0 } s ? s : null,
        _ => e.GetRawText(),
    };
}
