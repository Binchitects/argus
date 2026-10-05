using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Argus.Packs.Sources;

/// <summary>A documented member of a qdoc page: its heading as the page shows it, and what the text under it says.</summary>
public sealed class QDocMember
{
    /// <summary>The declaration, without qdoc's bracketed tags.</summary>
    public string Signature { get; set; } = "";
    /// <summary>qdoc's tags other than since and deprecated: static, signal, slot, virtual, constexpr.</summary>
    public List<string> Tags { get; } = [];
    public string Brief { get; set; } = "";
    /// <summary>"Qt 5.10", from a [since 5.10] tag or an "introduced in" sentence.</summary>
    public string Since { get; set; } = "";
    /// <summary>"6.4" for "deprecated in 6.4", "deprecated" or "obsolete" when the page says no more.</summary>
    public string Deprecated { get; set; } = "";
    internal List<string> Paragraphs { get; } = [];

    /// <summary>The signature with the tags that change how it is called, as qdoc writes them.</summary>
    public string Display => Tags.Count > 0 ? $"[{string.Join(", ", Tags)}] {Signature}" : Signature;
}

/// <summary>One qdoc page, read: its markdown body and the facts its symbols are described with.</summary>
public sealed class QDocPage
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Brief { get; set; } = "";
    /// <summary>The include, "&lt;QString&gt;".</summary>
    public string Header { get; set; } = "";
    public string Since { get; set; } = "";
    /// <summary>"Deprecated since 6.5", "obsolete", or "".</summary>
    public string Status { get; set; } = "";
    public string Inherits { get; set; } = "";
    /// <summary>A QML type's import statement.</summary>
    public string Import { get; set; } = "";
    /// <summary>Every id and named anchor on the page.</summary>
    public HashSet<string> Anchors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, QDocMember> Members { get; } = new(StringComparer.Ordinal);
    /// <summary>Enum constants as the value tables spell them ("Qt::AlignLeft") to their descriptions.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// qdoc's HTML, from Qt 4.8, 5.15 and 6, as markdown. Keeps the headings, the
/// declarations and the code; drops the navigation, the sidebar and the footer.
/// A member's heading carries its qdoc anchor pinned ({/* anchor */}) so a
/// chunk links to the declaration itself.
/// </summary>
public static class QDocHtml
{
    const RegexOptions O = RegexOptions.CultureInvariant;
    static readonly Regex Blanks = new(@"\n{3,}", O);
    static readonly Regex IncludeRe = new(@"#include\s*(<[^>\s]+>|""[^""]+"")", O);
    static readonly Regex IntroducedRe = new(@"\bintroduced in (Qt(?:\s?[A-Z][A-Za-z]*)*\s?\d+(?:\.\d+)+)", O);
    static readonly Regex DeprecatedSinceRe = new(@"\bdeprecated (?:since|in) (?:Qt )?(\d+(?:\.\d+)+)", RegexOptions.IgnoreCase | O);
    static readonly Regex StatusSentenceRe = new(@"^This (?:function|enum|enum value|property|typedef|member|class|type|macro|variable|signal|slot|struct|namespace|element|method|QML type) is (obsolete|deprecated)", O);
    static readonly Regex BoilerplateRe = new(
        @"^(?:This (?:function|enum|property|typedef|class|macro|variable|method|signal|type|element|QML type) was introduced in|This is an overloaded function|This function overloads|This function is (?:obsolete|deprecated)|This (?:enum|property|typedef|macro|class|method|signal) is (?:obsolete|deprecated)|We strongly advise against|Note:|Warning:|See also|Access functions:|Notifier signal:|Reimplements:|Reimplements an access function)",
        O);
    static readonly Regex ParenOpen = new(@"\s*\(\s*", O);
    static readonly Regex ParenClose = new(@"\s+\)", O);
    static readonly Regex SpaceComma = new(@"\s+,", O);
    static readonly Regex TrailingTagRe = new(@"\s*\[([a-z ,]+)\]$", O);

    static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "ul", "ol", "li", "dl", "dt", "dd", "blockquote", "hr", "section", "article", "table", "center",
    };

    /// <summary>Read one page. <paramref name="titleSuffix"/> follows the title in the body's first heading, " (Qt 6.10)".</summary>
    public static QDocPage Read(string html, string titleSuffix)
    {
        var (start, end) = Region(html);
        var c = new Converter(titleSuffix);
        c.Run(html, start, end);
        var page = c.Finish();
        if (page.Title.Length == 0)
        {
            page.Title = TitleTag(html);
            page.Body = $"# {page.Title}{titleSuffix}\n\n{page.Body}".TrimEnd();
        }
        return page;
    }

    /// <summary>The page between its title heading and its footer; the whole body when either is missing.</summary>
    static (int Start, int End) Region(string html)
    {
        int start = html.IndexOf("<h1 class=\"title\"", StringComparison.Ordinal);
        if (start < 0) start = html.IndexOf("<h1", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            start = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (start < 0) start = 0;
        }
        int end = html.IndexOf("<div class=\"footer\"", start, StringComparison.Ordinal);
        if (end < 0) end = html.IndexOf("<div class=\"ft\"", start, StringComparison.Ordinal);
        if (end < 0) end = html.IndexOf("</body", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = html.Length;
        return (start, end);
    }

    static string TitleTag(string html)
    {
        int open = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return "";
        int close = html.IndexOf("</title>", open, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return "";
        var title = Collapse(WebUtility.HtmlDecode(html[(open + 7)..close]));
        if (title.StartsWith("Qt 4.8: ", StringComparison.Ordinal)) title = title[8..];
        int bar = title.IndexOf(" | ", StringComparison.Ordinal);
        return bar > 0 ? title[..bar] : title;
    }

    static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Qt 4 spaces its declarations, "append ( const QString &amp; str )"; the later sets do not.</summary>
    public static string NormalizeSignature(string text)
    {
        var s = Collapse(text);
        s = ParenOpen.Replace(s, "(");
        s = ParenClose.Replace(s, ")");
        return SpaceComma.Replace(s, ",");
    }

    /// <summary>The attribute <paramref name="name"/> of a tag whose attributes are html[from..to).</summary>
    static string Attr(string html, int from, int to, string name)
    {
        int i = from;
        while (i < to)
        {
            while (i < to && (char.IsWhiteSpace(html[i]) || html[i] == '/')) i++;
            int ns = i;
            while (i < to && html[i] != '=' && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '/') i++;
            int ne = i;
            if (ne == ns) { i++; continue; }
            while (i < to && char.IsWhiteSpace(html[i])) i++;
            string value = "";
            if (i < to && html[i] == '=')
            {
                i++;
                while (i < to && char.IsWhiteSpace(html[i])) i++;
                if (i < to && html[i] is '"' or '\'')
                {
                    char q = html[i++];
                    int vs = i;
                    while (i < to && html[i] != q) i++;
                    value = html[vs..i];
                    i++;
                }
                else
                {
                    int vs = i;
                    while (i < to && !char.IsWhiteSpace(html[i]) && html[i] != '>') i++;
                    value = html[vs..i];
                }
            }
            if (ne - ns == name.Length && string.Compare(html, ns, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return WebUtility.HtmlDecode(value);
        }
        return "";
    }

    static bool HasClass(string cls, string wanted)
    {
        foreach (var part in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part == wanted) return true;
        return false;
    }

    sealed class Table
    {
        public string Class = "";
        public bool Transparent;
        public bool QmlNames;
        public List<string>? Row;
        public StringBuilder? Cell;
        public bool HeaderRow;
        public string RowId = "";
    }

    sealed class Converter(string titleSuffix)
    {
        readonly QDocPage _page = new();
        readonly StringBuilder _md = new();
        readonly StringBuilder _inline = new();
        string _bullet = "";
        int _listDepth;
        bool _inPre;
        readonly StringBuilder _pre = new();
        string _preLang = "";
        int _heading;
        bool _headMember;
        readonly StringBuilder _head = new();
        readonly StringBuilder _headTags = new();
        string _headAnchor = "";
        bool _inHeadTag;
        bool _spaceAfterSpan;
        readonly List<Table> _tables = [];
        readonly Stack<bool> _ticks = new();
        readonly Stack<bool> _divs = new();
        int _fnGroups;
        bool _groupHasHeading;
        List<QDocMember>? _current;
        string _lastName = "";
        bool _intro = true;
        readonly List<string> _introParagraphs = [];
        string _skipTag = "";
        int _skipDepth;

        Table? Top => _tables.Count > 0 ? _tables[^1] : null;
        StringBuilder? OpenCell => _tables.Count > 0 && !_tables[^1].Transparent ? _tables[^1].Cell : null;
        bool InGroup => _fnGroups > 0 || (Top?.QmlNames ?? false);

        public void Run(string html, int start, int end)
        {
            int i = start;
            var text = new StringBuilder();
            while (i < end)
            {
                int lt = html.IndexOf('<', i, end - i);
                if (lt < 0) { text.Append(html, i, end - i); break; }
                text.Append(html, i, lt - i);
                i = lt;
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    Text(text);
                    int close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = close < 0 ? end : close + 3;
                    continue;
                }
                if (i + 1 < end && html[i + 1] is '!' or '?')
                {
                    Text(text);
                    int close = html.IndexOf('>', i + 2);
                    i = close < 0 ? end : close + 1;
                    continue;
                }
                bool closing = i + 1 < end && html[i + 1] == '/';
                int ns = i + (closing ? 2 : 1);
                int ne = ns;
                while (ne < end && (char.IsAsciiLetterOrDigit(html[ne]) || html[ne] is '-' or ':')) ne++;
                if (ne == ns || !char.IsAsciiLetter(html[ns]))
                {
                    text.Append('<');
                    i++;
                    continue;
                }
                Text(text);
                var tag = html[ns..ne].ToLowerInvariant();
                int gt = TagEnd(html, ne, end);
                int attrEnd = gt < 0 ? end : gt;
                bool selfClosing = attrEnd > ne && html[attrEnd - 1] == '/';
                i = gt < 0 ? end : gt + 1;
                if (tag is "script" or "style")
                {
                    if (!closing)
                    {
                        int close = html.IndexOf("</" + tag, i, StringComparison.OrdinalIgnoreCase);
                        int after = close < 0 ? -1 : html.IndexOf('>', close);
                        i = after < 0 ? end : after + 1;
                    }
                    continue;
                }
                if (closing) End(tag);
                else
                {
                    Start(tag, html, ne, attrEnd);
                    if (selfClosing || tag is "br" or "hr" or "img" or "meta" or "link" or "input") End(tag);
                }
            }
            Text(text);
        }

        static int TagEnd(string s, int from, int end)
        {
            char quote = '\0';
            for (int i = from; i < end; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') { quote = c; continue; }
                if (c == '>') return i;
            }
            return -1;
        }

        void Text(StringBuilder raw)
        {
            if (raw.Length == 0) return;
            var s = raw.ToString();
            raw.Clear();
            if (_skipTag.Length > 0) return;
            if (s.Contains('&')) s = WebUtility.HtmlDecode(s);
            if (_inPre && OpenCell is null && _heading == 0) { _pre.Append(s); return; }
            var target = _heading > 0 ? (_inHeadTag ? _headTags : _head) : OpenCell ?? _inline;
            AppendCollapsed(target, s);
            if (_lastName.Length > 0 && s.Any(ch => !char.IsWhiteSpace(ch))) _lastName = "";
        }

        static void AppendCollapsed(StringBuilder target, string s)
        {
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (target.Length > 0 && target[^1] != ' ' && target[^1] != '\n') target.Append(' ');
                }
                else target.Append(ch);
            }
        }

        void Start(string tag, string html, int from, int to)
        {
            if (_skipTag.Length > 0)
            {
                if (tag == _skipTag) _skipDepth++;
                return;
            }
            var id = Attr(html, from, to, "id");
            if (id.Length > 0) _page.Anchors.Add(id);
            var cls = tag is "div" or "p" or "h3" or "table" or "td" or "th" or "pre" or "code" or "tr" or "span" ? Attr(html, from, to, "class") : "";
            if (cls.Contains("naviNextPrevious", StringComparison.Ordinal) || HasClass(cls, "sidebar") || HasClass(cls, "toc"))
            {
                _skipTag = tag;
                _skipDepth = 1;
                return;
            }
            if (tag == "a")
            {
                var name = Attr(html, from, to, "name");
                if (name.Length > 0)
                {
                    _page.Anchors.Add(name);
                    if (_heading > 0) { if (_headAnchor.Length == 0) _headAnchor = name; }
                    else _lastName = name;
                }
                return;
            }
            if (_heading > 0)
            {
                // qdoc's bracketed tags ([static], [since 6.1]) open the heading in a code element.
                if (tag is "code" or "tt") _inHeadTag = HasClass(cls, "extra") || _head.Length == 0;
                else if (tag == "br") _head.Append(' ');
                // Qt 4 runs "read-only" into the property name: <span class="qmlreadonly">read-only</span>activeFocus.
                else if (tag == "span" && cls.StartsWith("qml", StringComparison.Ordinal)) _spaceAfterSpan = true;
                return;
            }
            if (tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6' && OpenCell is null)
            {
                Flush();
                _heading = tag[1] - '0';
                _headMember = _heading == 3 && (HasClass(cls, "fn") || HasClass(cls, "flags"));
                _head.Clear();
                _headTags.Clear();
                _headAnchor = id.Length > 0 ? id : _lastName;
                _lastName = "";
                return;
            }
            switch (tag)
            {
                case "pre":
                    if (OpenCell is not null) { OpenCell.Append(' '); return; }
                    Flush();
                    _inPre = true;
                    _pre.Clear();
                    _preLang = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(c => c is "cpp" or "qml" or "js" or "javascript" or "bash" or "xml" or "json" or "cmake" or "python") ?? "";
                    return;
                case "br":
                    if (_inPre && OpenCell is null) _pre.Append('\n');
                    else if (OpenCell is { } cell) cell.Append(' ');
                    else _inline.Append('\n');
                    return;
                case "code" or "tt" or "kbd":
                {
                    bool tick = !_inPre && !HasClass(cls, "extra");
                    _ticks.Push(tick);
                    if (tick) (OpenCell ?? _inline).Append('`');
                    return;
                }
                case "div":
                {
                    bool group = HasClass(cls, "fngroup");
                    _divs.Push(group);
                    if (group) { _fnGroups++; _groupHasHeading = false; }
                    if (OpenCell is { } cell) cell.Append(' '); else Flush();
                    return;
                }
                case "table":
                {
                    if (OpenCell is { } cell) cell.Append(' '); else Flush();
                    var t = new Table { Class = cls, Transparent = HasClass(cls, "propsummary"), QmlNames = HasClass(cls, "qmlname") };
                    if (t.QmlNames) _groupHasHeading = false;
                    _tables.Add(t);
                    return;
                }
                case "tr":
                    if (Top is { Transparent: false } tr) { tr.Row = []; tr.HeaderRow = true; tr.RowId = id; }
                    return;
                case "td" or "th":
                    if (Top is { Transparent: false } td)
                    {
                        if (td.QmlNames && (HasClass(cls, "tblQmlPropNode") || HasClass(cls, "tblQmlFuncNode")))
                        {
                            _heading = 3;
                            _headMember = true;
                            _head.Clear();
                            _headTags.Clear();
                            _headAnchor = td.RowId;
                            return;
                        }
                        if (tag == "td") td.HeaderRow = false;
                        td.Cell = new StringBuilder();
                    }
                    return;
                case "li":
                    if (OpenCell is { } itemCell) { itemCell.Append(' '); return; }
                    Flush();
                    _bullet = new string(' ', 2 * Math.Max(0, _listDepth - 1)) + "- ";
                    return;
                case "ul" or "ol":
                    if (OpenCell is { } listCell) { listCell.Append(' '); return; }
                    Flush();
                    _listDepth++;
                    return;
            }
            if (BlockTags.Contains(tag))
            {
                if (OpenCell is { } cell) cell.Append(' ');
                else Flush();
            }
        }

        void End(string tag)
        {
            if (_skipTag.Length > 0)
            {
                if (tag == _skipTag && --_skipDepth == 0) _skipTag = "";
                return;
            }
            if (_heading > 0)
            {
                if (tag is "code" or "tt") { _inHeadTag = false; return; }
                if (tag == "span" && _spaceAfterSpan) { _head.Append(' '); _spaceAfterSpan = false; return; }
                bool closesQmlCell = tag is "td" or "th" && _headMember && Top is { QmlNames: true };
                if (!(tag.Length == 2 && tag[0] == 'h' && tag[1] - '0' == _heading) && !closesQmlCell) return;
                EmitHeading();
                return;
            }
            switch (tag)
            {
                case "pre":
                    if (!_inPre || OpenCell is not null) { OpenCell?.Append(' '); return; }
                    _inPre = false;
                    WriteFence(_pre.ToString(), _preLang);
                    return;
                case "code" or "tt" or "kbd":
                    if (_ticks.Count > 0 && _ticks.Pop()) (OpenCell ?? _inline).Append('`');
                    return;
                case "div":
                    if (_divs.Count > 0 && _divs.Pop()) _fnGroups = Math.Max(0, _fnGroups - 1);
                    if (OpenCell is { } cell) cell.Append(' '); else Flush();
                    return;
                case "td" or "th":
                    if (Top is { Transparent: false, Row: not null, Cell: not null } t)
                    {
                        t.Row.Add(t.Cell.ToString().Trim());
                        t.Cell = null;
                    }
                    return;
                case "tr":
                    if (Top is { Transparent: false, Row: not null } row)
                    {
                        EmitRow(row);
                        row.Row = null;
                    }
                    return;
                case "table":
                    if (_tables.Count > 0) _tables.RemoveAt(_tables.Count - 1);
                    if (OpenCell is null) Flush();
                    if (_md.Length > 0 && !EndsWithBlank()) _md.Append('\n');
                    return;
                case "li":
                    if (OpenCell is { } itemCell) itemCell.Append(' '); else Flush();
                    return;
                case "ul" or "ol":
                    if (OpenCell is not null) return;
                    Flush();
                    _listDepth = Math.Max(0, _listDepth - 1);
                    if (_listDepth == 0 && _md.Length > 0 && !EndsWithBlank()) _md.Append('\n');
                    return;
                case "p":
                    if (OpenCell is { } pc) pc.Append(' '); else Flush();
                    return;
            }
            if (BlockTags.Contains(tag))
            {
                if (OpenCell is { } cell) cell.Append(' ');
                else Flush();
            }
        }

        bool EndsWithBlank() => _md.Length >= 2 && _md[^1] == '\n' && _md[^2] == '\n';

        void EnsureBlank()
        {
            if (_md.Length == 0) return;
            if (_md[^1] != '\n') _md.Append("\n\n");
            else if (!EndsWithBlank()) _md.Append('\n');
        }

        void EmitHeading()
        {
            int level = _heading;
            _heading = 0;
            _inHeadTag = false;
            var text = Collapse(_head.ToString());
            var tags = Collapse(_headTags.ToString());
            var anchor = _headAnchor;
            _headAnchor = "";
            if (_headMember)
            {
                _headMember = false;
                EmitMember(text, tags, anchor);
                return;
            }
            if (text.Length == 0 && tags.Length == 0) return;
            if (tags.Length > 0) text = $"{tags} {text}".Trim();
            _current = null;
            if (level == 1)
            {
                if (_page.Title.Length == 0) _page.Title = text;
                WriteHeading(1, text + titleSuffix, "");
                return;
            }
            if (level == 2) _intro = false;
            WriteHeading(level, text, anchor);
        }

        void EmitMember(string text, string tags, string anchor)
        {
            var member = new QDocMember { Signature = NormalizeSignature(text) };
            if (tags.StartsWith('[') && tags.EndsWith(']'))
            {
                foreach (var raw in tags[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (raw.StartsWith("since ", StringComparison.Ordinal)) member.Since = "Qt " + raw[6..].Trim();
                    else if (raw.StartsWith("deprecated", StringComparison.Ordinal))
                        member.Deprecated = raw.StartsWith("deprecated in ", StringComparison.Ordinal) ? raw[14..].Trim() : "deprecated";
                    else member.Tags.Add(raw);
                }
            }
            else if (tags.Length > 0) member.Signature = NormalizeSignature($"{tags} {text}");
            // Qt 4 writes the tag after the declaration: "fromAscii ( const char * str ) [static]".
            if (member.Tags.Count == 0 && TrailingTagRe.Match(member.Signature) is { Success: true } trailing)
            {
                member.Tags.AddRange(trailing.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                member.Signature = member.Signature[..trailing.Index];
                tags = $"[{string.Join(", ", member.Tags)}]";
            }
            if (member.Signature.Length == 0 && member.Tags.Count == 0) return;
            if (anchor.Length > 0) _page.Members.TryAdd(anchor, member);
            var shown = tags.Length > 0 ? $"{tags} {member.Signature}" : member.Signature;
            if (InGroup && _groupHasHeading && _current is not null)
            {
                EnsureBlank();
                _md.Append('`').Append(shown).Append("`\n\n");
                _current.Add(member);
            }
            else
            {
                WriteHeading(3, shown, anchor);
                _current = [member];
            }
            if (InGroup) _groupHasHeading = true;
        }

        void WriteHeading(int level, string text, string anchor)
        {
            Flush();
            EnsureBlank();
            _md.Append('#', level).Append(' ').Append(text);
            if (anchor.Length > 0) _md.Append(" {/* ").Append(anchor).Append(" */}");
            _md.Append("\n\n");
        }

        void WriteFence(string code, string lang)
        {
            var lines = code.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) return;
            int indent = lines.Where(l => l.Length > 0).Min(l => l.Length - l.TrimStart(' ').Length);
            Flush();
            EnsureBlank();
            if (_bullet.Length > 0) _bullet = "";
            _md.Append("```").Append(lang).Append('\n');
            foreach (var line in lines) _md.Append(line.Length >= indent ? line[indent..] : line).Append('\n');
            _md.Append("```\n\n");
        }

        void EmitRow(Table t)
        {
            var cells = t.Row!.Where(c => c.Length > 0).ToList();
            if (cells.Count == 0) return;
            if (t.QmlNames) return;
            if (_intro && cells.Count == 2 && cells[0].EndsWith(':') && !t.HeaderRow)
            {
                Fact(cells[0][..^1].Trim(), cells[1]);
                Line($"{cells[0]} {cells[1]}", bullet: false);
                return;
            }
            if (HasClass(t.Class, "valuelist"))
            {
                if (!t.HeaderRow && cells.Count >= 2)
                {
                    var constant = cells[0].Trim('`', ' ');
                    var description = t.Row!.Count >= 3 ? t.Row[2].Trim() : "";
                    if (constant.Length > 0 && description.Length > 0) _page.Values.TryAdd(constant, description);
                }
                Line(string.Join(" | ", cells), bullet: true);
                return;
            }
            Line(string.Join(HasClass(t.Class, "alignedsummary") ? " " : " | ", cells), bullet: true);
        }

        void Fact(string key, string value)
        {
            switch (key)
            {
                case "Header":
                    var m = IncludeRe.Match(value);
                    if (m.Success && _page.Header.Length == 0) _page.Header = m.Groups[1].Value;
                    break;
                case "Since": _page.Since = value.Trim('`', ' '); break;
                case "Status": _page.Status = value.Trim('`', ' '); break;
                case "Inherits": _page.Inherits = value.Trim('`', ' ', '.'); break;
                case "Import Statement": _page.Import = value.Trim('`', ' '); break;
            }
        }

        void Line(string text, bool bullet)
        {
            Flush();
            if (_md.Length > 0 && _md[^1] != '\n') _md.Append('\n');
            if (bullet) _md.Append("- ");
            _md.Append(text).Append('\n');
        }

        /// <summary>Write the pending inline text as a paragraph or list item.</summary>
        void Flush()
        {
            if (_inline.Length == 0) return;
            var raw = _inline.ToString();
            _inline.Clear();
            var text = string.Join("\n", raw.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            // The brief links to the detailed description: "The QString class provides ... More..."
            if (_intro && text.EndsWith(" More...", StringComparison.Ordinal)) text = text[..^8];
            if (text.Length == 0) return;
            if (_bullet.Length > 0)
            {
                if (_md.Length > 0 && _md[^1] != '\n') _md.Append('\n');
                _md.Append(_bullet).Append(text.Replace("\n", " ")).Append('\n');
                _bullet = "";
            }
            else
            {
                EnsureBlank();
                _md.Append(text).Append("\n\n");
            }
            if (_listDepth > 0) return;
            if (_current is not null && _current[0].Paragraphs.Count < 6)
                foreach (var m in _current) m.Paragraphs.Add(text);
            if (_intro && _introParagraphs.Count < 16) _introParagraphs.Add(text);
        }

        public QDocPage Finish()
        {
            Flush();
            if (_inPre) { _inPre = false; WriteFence(_pre.ToString(), _preLang); }
            _page.Body = Blanks.Replace(_md.ToString(), "\n\n").Trim();
            foreach (var member in _page.Members.Values) Describe(member);
            foreach (var para in _introParagraphs)
            {
                var text = para.Replace("`", "");
                if (_page.Brief.Length == 0 && !text.StartsWith("Header:", StringComparison.Ordinal))
                {
                    var brief = text.EndsWith(" More...", StringComparison.Ordinal) ? text[..^8] : text;
                    if (!BoilerplateRe.IsMatch(brief)) _page.Brief = brief.Trim();
                }
                if (_page.Since.Length == 0 && IntroducedRe.Match(text) is { Success: true } since) _page.Since = since.Groups[1].Value;
                if (_page.Status.Length == 0 && StatusSentenceRe.Match(text) is { Success: true } status) _page.Status = status.Groups[1].Value;
                if (_page.Inherits.Length == 0 && text.StartsWith("Inherits: ", StringComparison.Ordinal)) _page.Inherits = text[10..].Trim().TrimEnd('.');
                if (_page.Header.Length == 0 && IncludeRe.Match(text) is { Success: true } include) _page.Header = include.Groups[1].Value;
            }
            if (_page.Header.Length == 0)
            {
                // Qt 4 shows the include as a code block under the brief, before the first section.
                int firstSection = _page.Body.IndexOf("\n## ", StringComparison.Ordinal);
                var intro = firstSection < 0 ? _page.Body : _page.Body[..firstSection];
                if (IncludeRe.Match(intro) is { Success: true } include) _page.Header = include.Groups[1].Value;
            }
            return _page;
        }

        static void Describe(QDocMember member)
        {
            foreach (var para in member.Paragraphs)
            {
                var text = para.Replace("`", "");
                if (member.Since.Length == 0 && IntroducedRe.Match(text) is { Success: true } since) member.Since = since.Groups[1].Value;
                if (DeprecatedSinceRe.Match(text) is { Success: true } dep) { if (member.Deprecated is "" or "deprecated") member.Deprecated = dep.Groups[1].Value; }
                else if (member.Deprecated.Length == 0 && StatusSentenceRe.Match(text) is { Success: true } status) member.Deprecated = status.Groups[1].Value;
                if (member.Brief.Length == 0 && !BoilerplateRe.IsMatch(text)) member.Brief = FirstSentence(text);
            }
            member.Paragraphs.Clear();
        }

        static string FirstSentence(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            for (int i = 0; i < words.Length && i < 40; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(words[i]);
                if (words[i].EndsWith('.') && i >= 3 && !words[i].EndsWith("e.g.", StringComparison.Ordinal) && !words[i].EndsWith("i.e.", StringComparison.Ordinal)) break;
            }
            return sb.ToString();
        }
    }
}
