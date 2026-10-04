using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Llm.Api.Chat;

/// <summary>
/// A Markdown document as a Word file (.docx), built here: the OOXML parts zipped
/// (System.IO.Compression), with no library. Headings, paragraphs, bullet and numbered
/// lists (nested), quotes, code blocks, tables, rules, and bold, italic, struck, code
/// and links inside a line. A paragraph that starts in Persian, Arabic or Hebrew reads
/// right to left.
/// </summary>
public static partial class CanvasDocx
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Rels = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The page's text width (A4, 2.54 cm margins), in twentieths of a point: tables share it.</summary>
    private const int TextWidth = 9026;

    public static byte[] Build(string title, string markdown, DateTimeOffset at)
    {
        var doc = new Writer();
        doc.Blocks(markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes);
            Add(zip, "_rels/.rels", PackageRels);
            Add(zip, "docProps/core.xml", Core(title, at));
            Add(zip, "word/document.xml", doc.Document());
            Add(zip, "word/styles.xml", Styles);
            Add(zip, "word/numbering.xml", doc.Numbering());
            Add(zip, "word/_rels/document.xml.rels", doc.Relationships());
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(xml);
    }

    /// <summary>Text made safe for XML: escaped, and without the characters XML cannot hold.</summary>
    internal static string Esc(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c))
            {
                continue;
            }
            b.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }
        return b.ToString();
    }

    /// <summary>Whether text starts right to left: its first letter is Hebrew, Arabic or Persian.</summary>
    internal static bool StartsRtl(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                return Rtl(c);
            }
        }
        return false;
    }

    private static bool Rtl(char c) => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    /// <summary>One run of text inside a paragraph, with its formatting.</summary>
    internal sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false, string? Link = null, bool Break = false);

    /// <summary>The document's body as it is written, and the lists and links it needs.</summary>
    private sealed partial class Writer
    {
        private readonly StringBuilder _body = new();
        private readonly List<string> _links = [];
        /// <summary>Each numbered list's own numbering (so each starts again), with where it starts.</summary>
        private readonly List<int> _ordered = [];
        private bool _lastWasTable;

        public void Blocks(string[] lines)
        {
            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    i++;
                    continue;
                }
                if (Fence().Match(line) is { Success: true } fence)
                {
                    var mark = fence.Groups[1].Value;
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !(lines[i].Trim() is { } close && close.Length >= mark.Length && close.All(c => c == mark[0])))
                    {
                        code.Add(lines[i]);
                        i++;
                    }
                    i++;
                    Code(code);
                    continue;
                }
                if (Heading().Match(line) is { Success: true } heading)
                {
                    Paragraph($"Heading{heading.Groups[1].Value.Length}", Inline(heading.Groups[2].Value.Trim()));
                    i++;
                    continue;
                }
                if (Rule().IsMatch(line))
                {
                    _body.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"BFBFBF\"/></w:pBdr></w:pPr></w:p>");
                    _lastWasTable = false;
                    i++;
                    continue;
                }
                if (TableStarts(lines, i))
                {
                    var rows = new List<List<string>> { Cells(line) };
                    i += 2;
                    while (i < lines.Length && lines[i].Contains('|', StringComparison.Ordinal) && lines[i].Trim().Length > 0)
                    {
                        rows.Add(Cells(lines[i]));
                        i++;
                    }
                    Table(rows);
                    continue;
                }
                if (Quote().IsMatch(line))
                {
                    var quoted = new List<string>();
                    while (i < lines.Length && Quote().Match(lines[i]) is { Success: true } q)
                    {
                        quoted.Add(q.Groups[1].Value);
                        i++;
                    }
                    foreach (var part in Paragraphs(quoted))
                    {
                        Paragraph("Quote", Inline(part));
                    }
                    continue;
                }
                if (Item().IsMatch(line))
                {
                    i = List(lines, i);
                    continue;
                }
                // A paragraph: its lines up to a blank line or another kind of block.
                var text = new List<string> { line };
                i++;
                while (i < lines.Length && lines[i].Trim().Length > 0 && !Starts(lines, i))
                {
                    text.Add(lines[i]);
                    i++;
                }
                Paragraph(null, Joined(text));
            }
            if (_lastWasTable)
            {
                // Word wants a paragraph after a table at the end.
                _body.Append("<w:p/>");
            }
        }

        private static bool Starts(string[] lines, int i) =>
            Fence().IsMatch(lines[i]) || Heading().IsMatch(lines[i]) || Rule().IsMatch(lines[i]) || Quote().IsMatch(lines[i]) || Item().IsMatch(lines[i]) ||
            TableStarts(lines, i);

        /// <summary>A table: a row of cells, then a row of dashes between bars.</summary>
        private static bool TableStarts(string[] lines, int i) =>
            i + 1 < lines.Length && lines[i].Contains('|', StringComparison.Ordinal) && lines[i + 1].Contains('|', StringComparison.Ordinal) && TableRule().IsMatch(lines[i + 1]);

        /// <summary>Lines of a paragraph as runs: joined with spaces, a line ending in two spaces or a backslash breaks there.</summary>
        private static List<Run> Joined(List<string> lines)
        {
            var runs = new List<Run>();
            for (var k = 0; k < lines.Count; k++)
            {
                var l = lines[k];
                var hard = l.EndsWith("  ", StringComparison.Ordinal) || l.EndsWith('\\');
                runs.AddRange(Inline(l.TrimEnd('\\').Trim()));
                if (k < lines.Count - 1)
                {
                    runs.Add(hard ? new Run("", Break: true) : new Run(" "));
                }
            }
            return runs;
        }

        /// <summary>Quoted lines as paragraphs (a blank quoted line between them).</summary>
        private static IEnumerable<string> Paragraphs(List<string> lines)
        {
            var part = new List<string>();
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0)
                {
                    if (part.Count > 0)
                    {
                        yield return string.Join(' ', part);
                    }
                    part.Clear();
                    continue;
                }
                part.Add(l.Trim());
            }
            if (part.Count > 0)
            {
                yield return string.Join(' ', part);
            }
        }

        /// <summary>A list, nested by indentation; returns the line after it.</summary>
        private int List(string[] lines, int i)
        {
            // A numbered list at each depth gets its own numbering when it starts.
            var numbering = new Dictionary<int, int>();
            // Each depth's indentation (two spaces or four, as written).
            var indents = new List<int>();
            while (i < lines.Length)
            {
                if (Item().Match(lines[i]) is not { Success: true } m)
                {
                    break;
                }
                var indent = m.Groups[1].Value.Replace("\t", "    ", StringComparison.Ordinal).Length;
                while (indents.Count > 0 && indent < indents[^1])
                {
                    indents.RemoveAt(indents.Count - 1);
                }
                if (indents.Count == 0 || indent > indents[^1])
                {
                    indents.Add(indent);
                }
                var depth = Math.Min(8, indents.Count - 1);
                var ordered = char.IsDigit(m.Groups[2].Value[0]);
                var text = new List<string> { m.Groups[3].Value };
                i++;
                // Its other lines: indented, not another item.
                while (i < lines.Length && lines[i].Trim().Length > 0 && !Item().IsMatch(lines[i]) && !Starts(lines, i) && char.IsWhiteSpace(lines[i][0]))
                {
                    text.Add(lines[i].Trim());
                    i++;
                }
                foreach (var deeper in numbering.Keys.Where(d => d > depth).ToList())
                {
                    numbering.Remove(deeper);
                }
                int num;
                if (!ordered)
                {
                    num = 1;
                    numbering.Remove(depth);
                }
                else if (!numbering.TryGetValue(depth, out num))
                {
                    _ordered.Add(int.TryParse(m.Groups[2].Value.TrimEnd('.', ')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ? start : 1);
                    num = numbering[depth] = _ordered.Count + 1;
                }
                var first = text[0];
                var task = Task().Match(first);
                if (task.Success)
                {
                    first = (task.Groups[1].Value is "x" or "X" ? "☑ " : "☐ ") + first[task.Length..];
                    text[0] = first;
                }
                var runs = Joined(text);
                var bidi = StartsRtl(string.Concat(runs.Select(r => r.Text)));
                _body.Append("<w:p><w:pPr><w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"").Append(depth.ToString(CultureInfo.InvariantCulture))
                    .Append("\"/><w:numId w:val=\"").Append(num.ToString(CultureInfo.InvariantCulture)).Append("\"/></w:numPr>").Append(bidi ? "<w:bidi/>" : "").Append("</w:pPr>");
                Runs(runs);
                _body.Append("</w:p>");
                // A blank line between items does not end the list.
                if (i + 1 < lines.Length && lines[i].Trim().Length == 0 && Item().IsMatch(lines[i + 1]))
                {
                    i++;
                }
            }
            _lastWasTable = false;
            return i;
        }

        private void Code(List<string> lines)
        {
            _body.Append("<w:p><w:pPr><w:pStyle w:val=\"Code\"/></w:pPr><w:r><w:rPr><w:rStyle w:val=\"CodeChar\"/></w:rPr>");
            for (var k = 0; k < lines.Count; k++)
            {
                if (k > 0)
                {
                    _body.Append("<w:br/>");
                }
                var parts = lines[k].Split('\t');
                for (var t = 0; t < parts.Length; t++)
                {
                    if (t > 0)
                    {
                        _body.Append("<w:tab/>");
                    }
                    if (parts[t].Length > 0)
                    {
                        _body.Append("<w:t xml:space=\"preserve\">").Append(Esc(parts[t])).Append("</w:t>");
                    }
                }
            }
            _body.Append("</w:r></w:p>");
            _lastWasTable = false;
        }

        private void Table(List<List<string>> rows)
        {
            var columns = rows.Max(r => r.Count);
            var width = TextWidth / Math.Max(1, columns);
            var w = width.ToString(CultureInfo.InvariantCulture);
            _body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/>")
                .Append("<w:tblLook w:val=\"04A0\" w:firstRow=\"1\" w:lastRow=\"0\" w:firstColumn=\"1\" w:lastColumn=\"0\" w:noHBand=\"0\" w:noVBand=\"1\"/></w:tblPr><w:tblGrid>");
            for (var c = 0; c < columns; c++)
            {
                _body.Append("<w:gridCol w:w=\"").Append(w).Append("\"/>");
            }
            _body.Append("</w:tblGrid>");
            for (var r = 0; r < rows.Count; r++)
            {
                _body.Append("<w:tr>");
                if (r == 0)
                {
                    _body.Append("<w:trPr><w:tblHeader/></w:trPr>");
                }
                for (var c = 0; c < columns; c++)
                {
                    var cell = c < rows[r].Count ? rows[r][c] : "";
                    var runs = Inline(cell);
                    if (r == 0)
                    {
                        runs = [.. runs.Select(x => x with { Bold = true })];
                    }
                    _body.Append("<w:tc><w:tcPr><w:tcW w:w=\"").Append(w).Append("\" w:type=\"dxa\"/></w:tcPr><w:p><w:pPr>")
                        .Append(StartsRtl(cell) ? "<w:bidi/>" : "").Append("<w:spacing w:after=\"0\"/></w:pPr>");
                    Runs(runs);
                    _body.Append("</w:p></w:tc>");
                }
                _body.Append("</w:tr>");
            }
            _body.Append("</w:tbl>");
            _lastWasTable = true;
        }

        private static List<string> Cells(string line)
        {
            var t = line.Trim();
            if (t.StartsWith('|'))
            {
                t = t[1..];
            }
            if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal))
            {
                t = t[..^1];
            }
            var cells = new List<string>();
            var cell = new StringBuilder();
            for (var k = 0; k < t.Length; k++)
            {
                if (t[k] == '\\' && k + 1 < t.Length && t[k + 1] == '|')
                {
                    cell.Append('|');
                    k++;
                }
                else if (t[k] == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                }
                else
                {
                    cell.Append(t[k]);
                }
            }
            cells.Add(cell.ToString().Trim());
            return cells;
        }

        private void Paragraph(string? style, List<Run> runs)
        {
            var bidi = StartsRtl(string.Concat(runs.Select(r => r.Text)));
            _body.Append("<w:p>");
            if (style is not null || bidi)
            {
                _body.Append("<w:pPr>").Append(style is null ? "" : $"<w:pStyle w:val=\"{style}\"/>").Append(bidi ? "<w:bidi/>" : "").Append("</w:pPr>");
            }
            Runs(runs);
            _body.Append("</w:p>");
            _lastWasTable = false;
        }

        private void Runs(List<Run> runs)
        {
            foreach (var group in Groups(runs))
            {
                if (group.Link is { } url)
                {
                    _links.Add(url);
                    _body.Append("<w:hyperlink r:id=\"rIdL").Append(_links.Count.ToString(CultureInfo.InvariantCulture)).Append("\" w:history=\"1\">");
                }
                foreach (var run in group.Runs)
                {
                    RunXml(run);
                }
                if (group.Link is not null)
                {
                    _body.Append("</w:hyperlink>");
                }
            }
        }

        /// <summary>Runs side by side with the same link, together (a link's text may have formatting inside).</summary>
        private static IEnumerable<(string? Link, List<Run> Runs)> Groups(List<Run> runs)
        {
            var group = new List<Run>();
            string? link = null;
            foreach (var run in runs)
            {
                if (group.Count > 0 && run.Link != link)
                {
                    yield return (link, group);
                    group = [];
                }
                link = run.Link;
                group.Add(run);
            }
            if (group.Count > 0)
            {
                yield return (link, group);
            }
        }

        private void RunXml(Run run)
        {
            _body.Append("<w:r>");
            var props = new StringBuilder();
            if (run.Link is not null)
            {
                props.Append("<w:rStyle w:val=\"Hyperlink\"/>");
                if (run.Code)
                {
                    props.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/>");
                }
            }
            else if (run.Code)
            {
                props.Append("<w:rStyle w:val=\"CodeChar\"/>");
            }
            if (run.Bold)
            {
                props.Append("<w:b/><w:bCs/>");
            }
            if (run.Italic)
            {
                props.Append("<w:i/><w:iCs/>");
            }
            if (run.Strike)
            {
                props.Append("<w:strike/>");
            }
            if (run.Text.Any(Rtl))
            {
                props.Append("<w:rtl/>");
            }
            if (props.Length > 0)
            {
                _body.Append("<w:rPr>").Append(props).Append("</w:rPr>");
            }
            if (run.Break)
            {
                _body.Append("<w:br/>");
            }
            else
            {
                _body.Append("<w:t xml:space=\"preserve\">").Append(Esc(run.Text)).Append("</w:t>");
            }
            _body.Append("</w:r>");
        }

        public string Document() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body>" + _body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>";

        public string Relationships()
        {
            var b = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
            b.Append("<Relationships xmlns=\"").Append(Rels).Append("\">")
                .Append("<Relationship Id=\"rIdStyles\" Type=\"").Append(R).Append("/styles\" Target=\"styles.xml\"/>")
                .Append("<Relationship Id=\"rIdNumbering\" Type=\"").Append(R).Append("/numbering\" Target=\"numbering.xml\"/>");
            for (var k = 0; k < _links.Count; k++)
            {
                b.Append("<Relationship Id=\"rIdL").Append((k + 1).ToString(CultureInfo.InvariantCulture)).Append("\" Type=\"").Append(R)
                    .Append("/hyperlink\" Target=\"").Append(Esc(_links[k])).Append("\" TargetMode=\"External\"/>");
            }
            return b.Append("</Relationships>").ToString();
        }

        /// <summary>Bullets (list 1), and the numbered lists, each starting again at its first number.</summary>
        public string Numbering()
        {
            var b = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
            b.Append("<w:numbering xmlns:w=\"").Append(W).Append("\">");
            string[] bullets = ["•", "◦", "▪"];
            string[] formats = ["decimal", "lowerLetter", "lowerRoman"];
            for (var kind = 0; kind < 2; kind++)
            {
                b.Append("<w:abstractNum w:abstractNumId=\"").Append(kind.ToString(CultureInfo.InvariantCulture)).Append("\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
                for (var level = 0; level < 9; level++)
                {
                    var lvl = level.ToString(CultureInfo.InvariantCulture);
                    var (format, text) = kind == 0 ? ("bullet", bullets[level % 3]) : (formats[level % 3], $"%{level + 1}.");
                    b.Append("<w:lvl w:ilvl=\"").Append(lvl).Append("\"><w:start w:val=\"1\"/><w:numFmt w:val=\"").Append(format).Append("\"/><w:lvlText w:val=\"")
                        .Append(text).Append("\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"").Append((720 * (level + 1)).ToString(CultureInfo.InvariantCulture))
                        .Append("\" w:hanging=\"360\"/></w:pPr></w:lvl>");
                }
                b.Append("</w:abstractNum>");
            }
            b.Append("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
            for (var k = 0; k < _ordered.Count; k++)
            {
                b.Append("<w:num w:numId=\"").Append((k + 2).ToString(CultureInfo.InvariantCulture)).Append("\"><w:abstractNumId w:val=\"1\"/>")
                    .Append("<w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"").Append(_ordered[k].ToString(CultureInfo.InvariantCulture)).Append("\"/></w:lvlOverride>");
                for (var level = 1; level < 9; level++)
                {
                    b.Append("<w:lvlOverride w:ilvl=\"").Append(level.ToString(CultureInfo.InvariantCulture)).Append("\"><w:startOverride w:val=\"1\"/></w:lvlOverride>");
                }
                b.Append("</w:num>");
            }
            return b.Append("</w:numbering>").ToString();
        }
    }

    /// <summary>
    /// What is inside a line: `code`, **bold**, *italic* (or _italic_ between words), ~~struck~~,
    /// [links](https://…) and &lt;https://…&gt;; a picture is its description. A backslash
    /// keeps the next mark as it is.
    /// </summary>
    internal static List<Run> Inline(string text, Run? style = null)
    {
        style ??= new Run("");
        var runs = new List<Run>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
            {
                runs.Add(style with { Text = plain.ToString() });
                plain.Clear();
            }
        }
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var ticks = 0;
                while (i + ticks < text.Length && text[i + ticks] == '`')
                {
                    ticks++;
                }
                var fence = new string('`', ticks);
                var end = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    var code = text[(i + ticks)..end];
                    if (code.Length > 1 && code[0] == ' ' && code[^1] == ' ')
                    {
                        code = code[1..^1];
                    }
                    runs.Add(style with { Text = code, Code = true });
                    i = end + ticks;
                    continue;
                }
                plain.Append(fence);
                i += ticks;
                continue;
            }
            if (Delimited(text, i, "**", out var inner, out var next) || Delimited(text, i, "__", out inner, out next))
            {
                Flush();
                runs.AddRange(Inline(inner, style with { Bold = true }));
                i = next;
                continue;
            }
            if (Delimited(text, i, "~~", out inner, out next))
            {
                Flush();
                runs.AddRange(Inline(inner, style with { Strike = true }));
                i = next;
                continue;
            }
            if ((c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))) && Delimited(text, i, c.ToString(), out inner, out next) &&
                (c == '*' || next >= text.Length || !char.IsLetterOrDigit(text[next])))
            {
                Flush();
                runs.AddRange(Inline(inner, style with { Italic = true }));
                i = next;
                continue;
            }
            if ((c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '[')) && Link().Match(text, i) is { Success: true } link && link.Index == i)
            {
                Flush();
                var url = link.Groups[3].Value.Trim();
                if (link.Groups[1].Value == "!")
                {
                    plain.Append(link.Groups[2].Value);
                }
                else
                {
                    runs.AddRange(Inline(link.Groups[2].Value, style with { Link = Safe(url) }));
                }
                i += link.Length;
                continue;
            }
            if (c == '<' && Autolink().Match(text, i) is { Success: true } auto && auto.Index == i)
            {
                Flush();
                runs.Add(style with { Text = auto.Groups[1].Value, Link = Safe(auto.Groups[1].Value) });
                i += auto.Length;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return runs;
    }

    /// <summary>A link Word may open: the web or mail; anything else stays as text.</summary>
    private static string? Safe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeMailto) ? u.AbsoluteUri : null;

    /// <summary>Text between a mark and the same mark after it, the inside not starting or ending with a space.</summary>
    private static bool Delimited(string text, int i, string mark, out string inner, out int next)
    {
        (inner, next) = ("", i);
        if (string.CompareOrdinal(text, i, mark, 0, mark.Length) != 0 || i + mark.Length >= text.Length || char.IsWhiteSpace(text[i + mark.Length]))
        {
            return false;
        }
        // A single mark is not half of a double one.
        if (mark.Length == 1 && text[i + 1] == mark[0])
        {
            return false;
        }
        var from = i + mark.Length;
        while (true)
        {
            var end = text.IndexOf(mark, from, StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }
            if (mark.Length == 1 && end + 1 < text.Length && text[end + 1] == mark[0])
            {
                from = end + 2;
                continue;
            }
            if (!char.IsWhiteSpace(text[end - 1]) && end > i + mark.Length)
            {
                (inner, next) = (text[(i + mark.Length)..end], end + mark.Length);
                return true;
            }
            from = end + 1;
        }
    }

    private static string Core(string title, DateTimeOffset at)
    {
        var when = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{Esc(title)}</dc:title><dcterms:created xsi:type=\"dcterms:W3CDTF\">{when}</dcterms:created>" +
            $"<dcterms:modified xsi:type=\"dcterms:W3CDTF\">{when}</dcterms:modified></cp:coreProperties>";
    }

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
        "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
        "</Types>";

    private const string PackageRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        "</Relationships>";

    private static string HeadingStyle(int level, int size, string color) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>" +
        "<w:uiPriority w:val=\"9\"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"" + (level <= 2 ? "360" : "240") + "\" w:after=\"120\"/>" +
        $"<w:outlineLvl w:val=\"{level - 1}\"/></w:pPr><w:rPr><w:b/><w:bCs/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";

    private static readonly string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        $"<w:styles xmlns:w=\"{W}\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:eastAsia=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Arial\"/>" +
        "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:bidi=\"fa-IR\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>" +
        "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>" +
        "<w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/>" +
        "<w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
        "<w:style w:type=\"numbering\" w:default=\"1\" w:styleId=\"NoList\"><w:name w:val=\"No List\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>" +
        HeadingStyle(1, 36, "1F3864") + HeadingStyle(2, 30, "2F5496") + HeadingStyle(3, 26, "2F5496") + HeadingStyle(4, 24, "2F5496") + HeadingStyle(5, 22, "404040") + HeadingStyle(6, 22, "595959") +
        "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"29\"/><w:qFormat/>" +
        "<w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"D0D5DD\"/></w:pBdr><w:ind w:left=\"360\"/></w:pPr><w:rPr><w:i/><w:iCs/><w:color w:val=\"4A5468\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"34\"/><w:qFormat/>" +
        "<w:pPr><w:spacing w:after=\"60\"/><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/>" +
        "<w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F4F6FA\"/><w:spacing w:after=\"160\" w:line=\"240\" w:lineRule=\"auto\"/><w:ind w:left=\"144\" w:right=\"144\"/></w:pPr>" +
        "<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/><w:szCs w:val=\"19\"/></w:rPr></w:style>" +
        "<w:style w:type=\"character\" w:styleId=\"CodeChar\"><w:name w:val=\"Code Char\"/><w:basedOn w:val=\"DefaultParagraphFont\"/>" +
        "<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F0F2F7\"/></w:rPr></w:style>" +
        "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/>" +
        "<w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
        "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/><w:uiPriority w:val=\"39\"/>" +
        "<w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:tblPr><w:tblBorders>" +
        "<w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
        "<w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
        "<w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
        "</w:tblBorders></w:tblPr></w:style>" +
        "</w:styles>";

    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"^\[([ xX])\]\s+")]
    private static partial Regex Task();

    [GeneratedRegex(@"(!?)\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<((?:https?|mailto):[^>\s]+)>")]
    private static partial Regex Autolink();
}
