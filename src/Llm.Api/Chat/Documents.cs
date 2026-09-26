using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Llm.Api.Chat;

/// <summary>
/// Office documents as text for the model: Word, Excel and PowerPoint (Office Open
/// XML), OpenDocument (Writer, Calc, Impress) and RTF. Read straight from the
/// file's XML: no Office, no converter process, and nothing in the file is run.
/// Tables become rows of cells ("| a | b |"), spreadsheets CSV per sheet, slides
/// "--- slide N ---" sections.
/// </summary>
public static partial class Documents
{
    /// <summary>What one part of the archive may unpack to: a zip bomb stops here.</summary>
    private const long MaxPartBytes = 64L * 1024 * 1024;
    private const int MaxParts = 5000;
    /// <summary>Columns of a spreadsheet row kept; the rest is summarised.</summary>
    private const int MaxColumns = 200;

    private static readonly XmlReaderSettings Safe = new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true,
    };

    /// <summary>The document's text (at most about <paramref name="maxChars"/>), or null when the file is none of these.</summary>
    public static string? Text(string fileName, byte[] bytes, int maxChars)
    {
        ReadOnlySpan<byte> b = bytes;
        if (b.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            throw new AttachmentException($"{fileName} is an old Office file (.doc, .xls or .ppt). Save it as .docx, .xlsx or .pptx, or as a PDF, and attach that.");
        }
        if (b.StartsWith("{\\rtf"u8))
        {
            return Rtf(Encoding.Latin1.GetString(bytes), maxChars);
        }
        if (!b.StartsWith("PK\u0003\u0004"u8))
        {
            return null;
        }
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        using (zip)
        {
            if (zip.Entries.Count > MaxParts)
            {
                throw new AttachmentException($"{fileName} has too many parts to read.");
            }
            try
            {
                if (zip.GetEntry("word/document.xml") is not null)
                {
                    return Word(zip, maxChars);
                }
                if (zip.GetEntry("xl/workbook.xml") is not null)
                {
                    return Excel(zip, maxChars);
                }
                if (zip.GetEntry("ppt/presentation.xml") is not null)
                {
                    return PowerPoint(zip, maxChars);
                }
                if (zip.GetEntry("content.xml") is not null && zip.GetEntry("mimetype") is not null)
                {
                    return OpenDocument(zip, maxChars);
                }
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
            {
                throw new AttachmentException($"{fileName} could not be read: the document looks damaged.");
            }
            return null;
        }
    }

    private static XmlReader Open(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException($"missing {path}");
        if (entry.Length > MaxPartBytes)
        {
            throw new AttachmentException("A part of the document unpacks to more than 64 MB.");
        }
        return XmlReader.Create(new Limited(entry.Open(), MaxPartBytes), Safe);
    }

    // ------------------------------------------------------------------- Word --

    private static string Word(ZipArchive zip, int maxChars)
    {
        var text = new Output(maxChars);
        foreach (var part in new[] { "word/document.xml", "word/footnotes.xml", "word/endnotes.xml" })
        {
            if (zip.GetEntry(part) is null || text.Full)
            {
                continue;
            }
            if (part != "word/document.xml")
            {
                text.Line(part.Contains("foot", StringComparison.Ordinal) ? "\n--- footnotes ---" : "\n--- endnotes ---");
            }
            using var r = Open(zip, part);
            WordBody(r, text);
        }
        return text.ToString();
    }

    private static void WordBody(XmlReader r, Output text)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var para = new StringBuilder();
        var prefix = "";
        var tables = 0;
        var row = new List<string>();
        var cell = new StringBuilder();
        var inText = false;
        while (r.Read() && !text.Full)
        {
            if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == W)
            {
                switch (r.LocalName)
                {
                    case "p":
                        para.Clear();
                        prefix = "";
                        break;
                    case "pStyle" when r.GetAttribute("val", W) is { } style:
                        var level = HeadingLevel().Match(style);
                        if (level.Success)
                        {
                            prefix = new string('#', Math.Min(6, int.Parse(level.Groups[1].Value, CultureInfo.InvariantCulture))) + " ";
                        }
                        else if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
                        {
                            prefix = "# ";
                        }
                        break;
                    case "numPr":
                        prefix = prefix.Length == 0 ? "- " : prefix;
                        break;
                    case "t":
                        inText = !r.IsEmptyElement;
                        break;
                    case "tab":
                        para.Append('\t');
                        break;
                    case "br" or "cr":
                        para.Append('\n');
                        break;
                    case "tbl":
                        tables++;
                        break;
                    case "tr" when tables == 1:
                        row.Clear();
                        break;
                    case "tc" when tables == 1:
                        cell.Clear();
                        break;
                }
            }
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace && inText)
            {
                para.Append(r.Value);
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == W)
            {
                switch (r.LocalName)
                {
                    case "t":
                        inText = false;
                        break;
                    case "p":
                        var line = prefix + para.ToString().TrimEnd();
                        if (tables > 0)
                        {
                            cell.Append(cell.Length > 0 && line.Length > 0 ? " " : "").Append(line.Trim());
                        }
                        else
                        {
                            text.Line(line);
                        }
                        break;
                    case "tc" when tables == 1:
                        row.Add(cell.ToString().Replace("|", "\\|", StringComparison.Ordinal));
                        break;
                    case "tr" when tables == 1:
                        text.Line("| " + string.Join(" | ", row) + " |");
                        break;
                    case "tbl":
                        tables--;
                        if (tables == 0)
                        {
                            text.Line("");
                        }
                        break;
                }
            }
        }
    }

    [GeneratedRegex(@"^(?:Heading|heading)\s?(\d)$")]
    private static partial Regex HeadingLevel();

    // ------------------------------------------------------------------ Excel --

    private static string Excel(ZipArchive zip, int maxChars)
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not null)
        {
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
        }

        // Sheets in workbook order, by name, and where each one is.
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
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet" && r.NamespaceURI == S)
                {
                    var name = r.GetAttribute("name") ?? $"Sheet{sheets.Count + 1}";
                    if (r.GetAttribute("id", R) is { } rid && targets.TryGetValue(rid, out var path))
                    {
                        sheets.Add((name, path));
                    }
                }
            }
        }

        var text = new Output(maxChars);
        foreach (var (name, path) in sheets)
        {
            if (text.Full || zip.GetEntry(path) is null)
            {
                continue;
            }
            text.Line($"## Sheet: {name}");
            using var r = Open(zip, path);
            var row = new SortedDictionary<int, string>();
            string? type = null;
            var column = 0;
            var value = new StringBuilder();
            var inValue = false;
            var wide = false;
            while (r.Read() && !text.Full)
            {
                if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == S)
                {
                    switch (r.LocalName)
                    {
                        case "row":
                            row.Clear();
                            column = 0;
                            break;
                        case "c":
                            type = r.GetAttribute("t");
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
                            var raw = value.ToString();
                            var shown = type switch
                            {
                                "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count => shared[i],
                                "b" => raw == "1" ? "TRUE" : "FALSE",
                                _ => raw,
                            };
                            if (shown.Length > 0)
                            {
                                if (column <= MaxColumns)
                                {
                                    row[column] = shown;
                                }
                                else
                                {
                                    wide = true;
                                }
                            }
                            break;
                        case "row":
                            if (row.Count > 0)
                            {
                                var cells = new string[row.Keys.Max()];
                                foreach (var (c, v) in row)
                                {
                                    cells[c - 1] = Csv(v);
                                }
                                text.Line(string.Join(',', cells.Select(c => c ?? "")));
                            }
                            break;
                    }
                }
            }
            if (wide)
            {
                text.Line($"[columns after {MaxColumns} left out]");
            }
            text.Line("");
        }
        return text.ToString();
    }

    /// <summary>"AB12" -> 28.</summary>
    private static int ColumnOf(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z')
            {
                break;
            }
            n = n * 26 + (ch - 'A' + 1);
        }
        return Math.Max(1, n);
    }

    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    // ------------------------------------------------------------- PowerPoint --

    private static string PowerPoint(ZipArchive zip, int maxChars)
    {
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var slides = zip.Entries
            .Select(e => (e.FullName, Match: SlidePath().Match(e.FullName)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(x => x.FullName)
            .ToList();
        var text = new Output(maxChars);
        var number = 0;
        foreach (var path in slides)
        {
            if (text.Full)
            {
                break;
            }
            text.Line($"--- slide {++number} ---");
            using var r = Open(zip, path);
            var para = new StringBuilder();
            var inText = false;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == A)
                {
                    switch (r.LocalName)
                    {
                        case "p":
                            para.Clear();
                            break;
                        case "t":
                            inText = !r.IsEmptyElement;
                            break;
                        case "br":
                            para.Append('\n');
                            break;
                    }
                }
                else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace && inText)
                {
                    para.Append(r.Value);
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.NamespaceURI == A)
                {
                    if (r.LocalName == "t")
                    {
                        inText = false;
                    }
                    else if (r.LocalName == "p" && para.ToString().Trim() is { Length: > 0 } line)
                    {
                        text.Line(line);
                    }
                }
            }
        }
        return text.ToString();
    }

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlidePath();

    // ----------------------------------------------------------- OpenDocument --

    /// <summary>Writer, Calc and Impress alike: paragraphs and headings, tables as rows (CSV for a spreadsheet), pages as slides.</summary>
    private static string OpenDocument(ZipArchive zip, int maxChars)
    {
        const string T = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        const string Tb = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        const string D = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        string mime;
        using (var m = new StreamReader(zip.GetEntry("mimetype")!.Open()))
        {
            mime = m.ReadToEnd().Trim();
        }
        var spreadsheet = mime.EndsWith("spreadsheet", StringComparison.Ordinal);
        var text = new Output(maxChars);
        using var r = Open(zip, "content.xml");
        var para = new StringBuilder();
        var paraDepth = 0;
        var tables = 0;
        var row = new List<string>();
        var cell = new StringBuilder();
        var repeat = 1;
        var rowRepeat = 1;
        var page = 0;
        while (r.Read() && !text.Full)
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                switch (r.NamespaceURI, r.LocalName)
                {
                    case (D, "page"):
                        text.Line($"--- slide {++page} ---");
                        break;
                    case (Tb, "table"):
                        tables++;
                        if (spreadsheet && tables == 1)
                        {
                            text.Line($"## Sheet: {r.GetAttribute("name", Tb) ?? "Sheet"}");
                        }
                        break;
                    case (Tb, "table-row") when tables == 1:
                        row.Clear();
                        rowRepeat = Repeat(r.GetAttribute("number-rows-repeated", Tb));
                        break;
                    case (Tb, "table-cell" or "covered-table-cell") when tables == 1:
                        cell.Clear();
                        repeat = Repeat(r.GetAttribute("number-columns-repeated", Tb));
                        if (r.IsEmptyElement)
                        {
                            AddCell(row, "", repeat);
                        }
                        break;
                    case (T, "p" or "h"):
                        if (paraDepth++ == 0)
                        {
                            para.Clear();
                            if (r.LocalName == "h")
                            {
                                para.Append('#', Math.Clamp(Repeat(r.GetAttribute("outline-level", T)), 1, 6)).Append(' ');
                            }
                        }
                        if (r.IsEmptyElement)
                        {
                            EndParagraph();
                        }
                        break;
                    case (T, "tab"):
                        para.Append('\t');
                        break;
                    case (T, "line-break"):
                        para.Append('\n');
                        break;
                    case (T, "s"):
                        para.Append(' ', Math.Min(Repeat(r.GetAttribute("c", T)), 100));
                        break;
                }
            }
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace && paraDepth > 0)
            {
                para.Append(r.Value);
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                switch (r.NamespaceURI, r.LocalName)
                {
                    case (T, "p" or "h"):
                        EndParagraph();
                        break;
                    case (Tb, "table-cell" or "covered-table-cell") when tables == 1:
                        AddCell(row, cell.ToString(), repeat);
                        break;
                    case (Tb, "table-row") when tables == 1:
                        while (row.Count > 0 && row[^1].Length == 0)
                        {
                            row.RemoveAt(row.Count - 1);
                        }
                        if (row.Count > 0)
                        {
                            var line = spreadsheet ? string.Join(',', row.Select(Csv)) : "| " + string.Join(" | ", row) + " |";
                            for (var i = 0; i < Math.Min(rowRepeat, 1000) && !text.Full; i++)
                            {
                                text.Line(line);
                            }
                        }
                        break;
                    case (Tb, "table"):
                        tables--;
                        if (tables == 0)
                        {
                            text.Line("");
                        }
                        break;
                }
            }
        }
        return text.ToString();

        void EndParagraph()
        {
            if (--paraDepth > 0)
            {
                return;
            }
            var line = para.ToString().TrimEnd();
            if (tables > 0)
            {
                cell.Append(cell.Length > 0 && line.Length > 0 ? " " : "").Append(line.Trim());
            }
            else
            {
                text.Line(line);
            }
        }
    }

    /// <summary>A repeated cell; a long run of empty ones (the rest of a row) counts once.</summary>
    private static void AddCell(List<string> row, string value, int repeat)
    {
        for (var i = 0; i < (value.Length == 0 ? Math.Min(repeat, 1) : Math.Min(repeat, MaxColumns)) && row.Count < MaxColumns; i++)
        {
            row.Add(value);
        }
    }

    private static int Repeat(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;

    // -------------------------------------------------------------------- RTF --

    /// <summary>RTF's text: paragraphs and tabs kept, formatting, fonts, pictures and hidden parts dropped.</summary>
    private static string Rtf(string rtf, int maxChars)
    {
        var text = new StringBuilder();
        var skip = new Stack<bool>();
        var skipping = false;
        var pendingSkip = 0;
        for (var i = 0; i < rtf.Length && text.Length < maxChars; i++)
        {
            var ch = rtf[i];
            switch (ch)
            {
                case '{':
                    skip.Push(skipping);
                    break;
                case '}':
                    skipping = skip.Count > 0 && skip.Pop();
                    break;
                case '\\' when i + 1 < rtf.Length:
                    var next = rtf[i + 1];
                    if (next is '\\' or '{' or '}')
                    {
                        if (!skipping)
                        {
                            text.Append(next);
                        }
                        i++;
                    }
                    else if (next == '\'' && i + 3 < rtf.Length)
                    {
                        if (!skipping && int.TryParse(rtf.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            AppendSkipping(Encoding.Latin1.GetString([(byte)code]));
                        }
                        i += 3;
                    }
                    else if (next == '*')
                    {
                        skipping = true;
                        i++;
                    }
                    else
                    {
                        var j = i + 1;
                        while (j < rtf.Length && char.IsAsciiLetter(rtf[j]))
                        {
                            j++;
                        }
                        var word = rtf[(i + 1)..j];
                        var k = j;
                        if (k < rtf.Length && (rtf[k] == '-' || char.IsAsciiDigit(rtf[k])))
                        {
                            k++;
                            while (k < rtf.Length && char.IsAsciiDigit(rtf[k]))
                            {
                                k++;
                            }
                        }
                        var number = rtf[j..k];
                        if (k < rtf.Length && rtf[k] == ' ')
                        {
                            k++;
                        }
                        i = k - 1;
                        switch (word)
                        {
                            case "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "object" or "header" or "footer" or "listtable" or "listoverridetable" or "themedata" or "datastore" or "latentstyles":
                                skipping = true;
                                break;
                            case "par" or "line" or "row" or "sect" or "page":
                                if (!skipping)
                                {
                                    text.Append('\n');
                                }
                                break;
                            case "tab" or "cell":
                                if (!skipping)
                                {
                                    text.Append('\t');
                                }
                                break;
                            case "u" when int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u):
                                if (!skipping)
                                {
                                    text.Append((char)(u < 0 ? u + 65536 : u));
                                }
                                pendingSkip = 1;
                                break;
                        }
                    }
                    break;
                case '\r' or '\n':
                    break;
                default:
                    AppendSkipping(ch.ToString());
                    break;
            }
        }
        return text.ToString().Trim();

        // After \uN, the next character is the fallback for readers without Unicode.
        void AppendSkipping(string s)
        {
            if (pendingSkip > 0)
            {
                pendingSkip--;
                return;
            }
            if (!skipping)
            {
                text.Append(s);
            }
        }
    }

    // ----------------------------------------------------------------- output --

    /// <summary>Lines up to a size; past it, the rest is not read.</summary>
    private sealed class Output(int maxChars)
    {
        private readonly StringBuilder _text = new();
        public bool Full => _text.Length >= maxChars;

        public void Line(string line)
        {
            if (!Full)
            {
                _text.Append(line).Append('\n');
            }
        }

        public override string ToString() => _text.ToString().Trim('\n');
    }

    /// <summary>A stream that refuses to give more than a limit: what a part claims to be is not trusted.</summary>
    private sealed class Limited(Stream inner, long limit) : Stream
    {
        private long _read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            return _read > limit ? throw new AttachmentException("A part of the document unpacks to more than 64 MB.") : n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
