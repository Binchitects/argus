using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Argus.Util;

namespace Argus.Packs.Sources;

/// <summary>
/// HTML to markdown-ish text (html_docs.py's _Extractor over html.parser).
///
/// A small tokenizer with html.parser's leniency where it matters here: tags are
/// case-insensitive, script and style are raw text, comments, doctypes and
/// processing instructions vanish, and a stray '&lt;' that opens no tag is data.
/// Character references are decoded per text run, as convert_charrefs does.
/// </summary>
public static class HtmlText
{
    static readonly HashSet<string> Drop = new(StringComparer.Ordinal) { "script", "style", "noscript", "nav", "footer" };
    static readonly HashSet<string> Block = new(StringComparer.Ordinal) { "p", "div", "section", "article", "table", "tr", "ul", "ol", "dl", "pre", "blockquote", "hr" };
    static readonly HashSet<string> Cells = new(StringComparer.Ordinal) { "td", "th", "li", "dt", "dd" };
    static readonly Regex BlanksRe = new(@"\n{3,}", RegexOptions.CultureInvariant);
    static readonly Regex TrailingWsRe = new(@"[ \t]+\n", RegexOptions.CultureInvariant);
    static readonly Regex TagNameRe = new(@"\G([a-zA-Z][^\t\n\r\f />\x00]*)", RegexOptions.CultureInvariant);

    static int Heading(string tag) => tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6' ? tag[1] - '0' : 0;

    sealed class Extractor
    {
        public readonly StringBuilder Title = new();
        readonly StringBuilder _parts = new();
        int _skip;
        bool _inTitle;
        int? _heading;

        public void Start(string tag)
        {
            if (Drop.Contains(tag)) { _skip++; return; }
            if (_skip > 0) return;
            if (tag == "title") _inTitle = true;
            else if (Heading(tag) is > 0 and var level) { _heading = level; _parts.Append("\n\n").Append('#', level).Append(' '); }
            else if (tag == "br") _parts.Append('\n');
            else if (Block.Contains(tag)) _parts.Append("\n\n");
            else if (Cells.Contains(tag)) _parts.Append('\n');
        }

        public void End(string tag)
        {
            if (Drop.Contains(tag)) { _skip = Math.Max(0, _skip - 1); return; }
            if (_skip > 0) return;
            if (tag == "title") _inTitle = false;
            else if (Heading(tag) > 0) { _heading = null; _parts.Append('\n'); }
            else if (Block.Contains(tag)) _parts.Append("\n\n");
        }

        public void Data(string data)
        {
            if (_skip > 0) return;
            if (_inTitle) { Title.Append(data); return; }
            if (_heading is not null) { _parts.Append(string.Join(" ", PyStr.SplitWhitespace(data))); return; }
            _parts.Append(data);
        }

        public string Text()
        {
            var joined = TrailingWsRe.Replace(_parts.ToString(), "\n");
            return PyStr.Strip(BlanksRe.Replace(joined, "\n\n"));
        }
    }

    public static (string Title, string Body) ToText(string source)
    {
        var x = new Extractor();
        int i = 0, n = source.Length;
        var data = new StringBuilder();
        void FlushData()
        {
            if (data.Length == 0) return;
            x.Data(WebUtility.HtmlDecode(data.ToString()));
            data.Clear();
        }
        while (i < n)
        {
            int lt = source.IndexOf('<', i);
            if (lt < 0) { data.Append(source, i, n - i); break; }
            data.Append(source, i, lt - i);
            i = lt;
            if (string.CompareOrdinal(source, i, "<!--", 0, 4) == 0)
            {
                FlushData();
                int end = source.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }
            if (i + 1 < n && (source[i + 1] == '!' || source[i + 1] == '?'))
            {
                FlushData();
                int end = source.IndexOf('>', i + 2);
                i = end < 0 ? n : end + 1;
                continue;
            }
            bool closing = i + 1 < n && source[i + 1] == '/';
            var nameMatch = TagNameRe.Match(source, i + (closing ? 2 : 1));
            if (!nameMatch.Success)
            {
                data.Append('<');
                i++;
                continue;
            }
            FlushData();
            var tag = nameMatch.Groups[1].Value.ToLowerInvariant();
            int close = FindTagEnd(source, nameMatch.Index + nameMatch.Length);
            bool selfClosing = close > 0 && source[close - 1] == '/';
            i = close < 0 ? n : close + 1;
            if (closing) { x.End(tag); continue; }
            x.Start(tag);
            if (selfClosing) { x.End(tag); continue; }
            if (tag is "script" or "style")
            {
                var endTag = "</" + tag;
                int end = source.IndexOf(endTag, i, StringComparison.OrdinalIgnoreCase);
                var raw = end < 0 ? source[i..] : source[i..end];
                x.Data(raw);
                if (end < 0) { i = n; break; }
                int gt = source.IndexOf('>', end);
                i = gt < 0 ? n : gt + 1;
                x.End(tag);
            }
        }
        FlushData();
        return (string.Join(" ", PyStr.SplitWhitespace(x.Title.ToString())), x.Text());
    }

    static int FindTagEnd(string s, int from)
    {
        char quote = '\0';
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '>') return i;
        }
        return -1;
    }

    public static string DescriptionAfterChrome(string body, int words = 30)
    {
        var lines = PyStr.SplitLines(body);
        int start = lines.FindIndex(l => l.StartsWith('#'));
        if (start < 0) return string.Join(" ", PyStr.SplitWhitespace(body).Take(words));
        var prose = lines.Skip(start).Where(l => PyStr.Strip(l).Length > 0 && !l.StartsWith('#'));
        return string.Join(" ", PyStr.SplitWhitespace(string.Join(" ", prose)).Take(words));
    }

    public static string FirstHeading(string body)
    {
        foreach (var line in PyStr.SplitLines(body))
            if (line.StartsWith('#')) return PyStr.Strip(PyStr.LStrip(line, "#"));
        return "";
    }

    public static IEnumerable<(string Path, string Relative, string Title, string Body)> IterHtml(string content)
    {
        foreach (var path in Walk.Files(content, n => n.EndsWith(".html", StringComparison.Ordinal)))
        {
            var (title, body) = ToText(Walk.ReadText(path));
            if (body.Length == 0) continue;
            var relative = Walk.Relative(content, path);
            var t = title.Length > 0 ? title : FirstHeading(body);
            yield return (path, relative, t.Length > 0 ? t : relative, body);
        }
    }
}

/// <summary>The SQLite documentation archive; the lang_*.html pages are the SQL statements.</summary>
public sealed class SqliteDocs : ISource
{
    public string Name => "sqlite";
    public string RepoUrl => "https://www.sqlite.org/";
    public string Branch => "";
    public string Subtree => "";
    public string ArchiveUrl => "https://www.sqlite.org/2026/sqlite-doc-3530400.zip";
    public string ArchiveSha256 => "";
    public string License => "public-domain";
    public string LicenseUrl => "https://www.sqlite.org/copyright.html";
    public string Attribution => "SQLite documentation. The SQLite source code and documentation are dedicated to the public domain.";
    const string BaseUrl = "https://www.sqlite.org/";

    static string? Content(string root)
    {
        if (File.Exists(Path.Combine(root, "index.html"))) return root;
        if (!Directory.Exists(root)) return null;
        foreach (var child in Directory.EnumerateDirectories(root).OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
            if (File.Exists(Path.Combine(child, "index.html"))) return child;
        return null;
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        if (Content(root) is not { } content) yield break;
        foreach (var (_, relative, title, body) in HtmlText.IterHtml(content))
            yield return new Doc(relative, title, BaseUrl + relative, "md", body);
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        if (Content(root) is not { } content) yield break;
        foreach (var (_, relative, title, body) in HtmlText.IterHtml(content))
        {
            if (!relative.StartsWith("lang_", StringComparison.Ordinal) || relative == "lang.html") continue;
            var statement = PyStr.RStrip(PyStr.Strip(title.Split('(')[0]), ".");
            if (statement.Length == 0 || PyStr.Len(statement) > 40) continue;
            yield return new ApiSymbol(statement, "statement", "sql", relative, "", HtmlText.DescriptionAfterChrome(body));
        }
    }
}

/// <summary>cppreference.com's offline HTML book.</summary>
public sealed class CppReference : ISource
{
    public string Name => "cppreference";
    public string RepoUrl => "https://github.com/PeterFeicht/cppreference-doc";
    public string Branch => "master";
    public string Subtree => "reference/en";
    public string ArchiveUrl => "https://github.com/PeterFeicht/cppreference-doc/releases/download/v20250209/html-book-20250209.tar.xz";
    public string ArchiveSha256 => "";
    public string License => "CC-BY-SA-3.0";
    public string LicenseUrl => "https://en.cppreference.com/w/Cppreference:Copyright/CC-BY-SA";
    public string Attribution => "cppreference.com, by the cppreference contributors. Used under CC BY-SA 3.0.";
    const string BaseUrl = "https://en.cppreference.com/w/";

    static readonly HashSet<string> NotEntities = new(StringComparer.Ordinal)
    {
        "index", "language", "keyword", "concept", "header", "meta", "experimental", "symbol_index", "links", "types",
        "utility", "io", "numeric", "algorithm", "container", "iterator", "memory", "string", "thread", "regex",
        "chrono", "locale", "error", "preprocessor",
    };

    public static string SymbolFromPath(string relative)
    {
        if (!relative.EndsWith(".html", StringComparison.Ordinal)) return "";
        var parts = relative[..^5].Split('/');
        if (parts.Length < 3 || parts[0] != "cpp" || parts[1] == "language") return "";
        var tail = parts.Skip(1).Where(p => !NotEntities.Contains(p)).ToList();
        if (tail.Count == 0 || tail.Any(p => !PyStr.IsAlnum(p.Replace("_", "")))) return "";
        return "std::" + string.Join("::", tail);
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var (_, relative, title, body) in HtmlText.IterHtml(content))
            yield return new Doc(relative, title, BaseUrl + relative[..^5], "md", body);
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var (_, relative, _, body) in HtmlText.IterHtml(content))
        {
            var name = SymbolFromPath(relative);
            if (name.Length == 0) continue;
            yield return new ApiSymbol(name, "entity", relative.Split('/', 2)[0], relative[..^5], "",
                string.Join(" ", PyStr.SplitWhitespace(body).Take(30)));
        }
    }
}

/// <summary>.NET API reference from dotnet/dotnet-api-docs ECMA XML (dotnet_docs.py).</summary>
public sealed class DotnetApiDocs : ISource
{
    public string Name => "dotnet";
    public string RepoUrl => "https://github.com/dotnet/dotnet-api-docs";
    public string Branch => "main";
    public string Subtree => "xml";
    public string License => "CC-BY-4.0";
    public string LicenseUrl => "https://github.com/dotnet/dotnet-api-docs/blob/main/LICENSE";
    public string Attribution => ".NET API documentation. Copyright (c) .NET Foundation and Contributors. Used under CC BY 4.0.";
    const string BaseUrl = "https://learn.microsoft.com/dotnet/api/";
    const int PrologBytes = 4096;
    static readonly Regex CrefRe = new(@"^[A-Z]:", RegexOptions.CultureInvariant);
    static readonly HashSet<string> RefTags = new(StringComparer.Ordinal) { "see", "seealso", "paramref", "typeparamref" };

    /// <summary>ElementTree's .text: the character data before the first child element.</summary>
    static string? EtText(XElement e)
    {
        StringBuilder? sb = null;
        foreach (var node in e.Nodes())
        {
            if (node is XElement) break;
            if (node is XText t) (sb ??= new()).Append(t.Value);
        }
        return sb?.ToString();
    }

    /// <summary>ElementTree's .tail: the character data after this element, before the next sibling element.</summary>
    static string? EtTail(XElement e)
    {
        StringBuilder? sb = null;
        for (var node = e.NextNode; node is not null and not XElement; node = node.NextNode)
            if (node is XText t) (sb ??= new()).Append(t.Value);
        return sb?.ToString();
    }

    static IEnumerable<XElement> Iter(XElement e)
    {
        yield return e;
        foreach (var child in e.Elements())
            foreach (var d in Iter(child)) yield return d;
    }

    public static string Flatten(XElement? node)
    {
        if (node is null) return "";
        var parts = new List<string>();
        foreach (var element in Iter(node))
        {
            if (RefTags.Contains(element.Name.LocalName))
            {
                var r = (string?)element.Attribute("cref") ?? (string?)element.Attribute("name") ?? (string?)element.Attribute("langword") ?? "";
                if (r.Length > 0)
                {
                    if (CrefRe.IsMatch(r)) r = r[2..];
                    r = r.Split('(', 2)[0];
                    parts.Add(r.Contains('.') ? PyStr.AfterLast(r, '.') : r);
                }
            }
            if (EtText(element) is { Length: > 0 } text) parts.Add(text);
            if (EtTail(element) is { Length: > 0 } tail) parts.Add(tail);
        }
        return string.Join(" ", PyStr.SplitWhitespace(string.Join(" ", parts)));
    }

    static XElement? ParseGuarded(string path)
    {
        var prolog = new byte[PrologBytes];
        int read;
        using (var fs = File.OpenRead(path)) read = fs.Read(prolog, 0, PrologBytes);
        var lowered = Encoding.Latin1.GetString(prolog, 0, read).ToLowerInvariant();
        if (lowered.Contains("<!doctype") || lowered.Contains("<!entity")) return null;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
    }

    static string Signature(XElement node, string tag) =>
        node.Elements(tag).FirstOrDefault(s => (string?)s.Attribute("Language") == "C#") is { } s ? (string?)s.Attribute("Value") ?? "" : "";

    static string DocId(XElement node, string tag) =>
        node.Elements(tag).FirstOrDefault(s => (string?)s.Attribute("Language") == "DocId") is { } s ? (string?)s.Attribute("Value") ?? "" : "";

    IEnumerable<(string Path, string Relative, XElement Node)> Types(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var path in Walk.Files(content, n => n.EndsWith(".xml", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("ns-", StringComparison.Ordinal) || name == "index.xml") continue;
            XElement? node;
            try { node = ParseGuarded(path); }
            catch (XmlException) { continue; }
            if (node is null || node.Name.LocalName != "Type") continue;
            yield return (path, Walk.Relative(content, path), node);
        }
    }

    static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    public IEnumerable<Doc> IterDocs(string root)
    {
        foreach (var (_, relative, node) in Types(root))
        {
            var fullName = Attr(node, "FullName");
            if (fullName.Length == 0) fullName = Attr(node, "Name");
            if (fullName.Length == 0) fullName = relative;
            var docs = node.Element("Docs");
            var lines = new List<string> { $"# {fullName}", "" };
            var signature = Signature(node, "TypeSignature");
            if (signature.Length > 0) lines.AddRange(["```csharp", signature, "```", ""]);
            foreach (var (tag, heading) in new[] { ("summary", (string?)null), ("remarks", "Remarks") })
            {
                var text = docs is not null ? Flatten(docs.Element(tag)) : "";
                if (text.Length == 0) continue;
                if (heading is not null) lines.AddRange([$"## {heading}", ""]);
                lines.AddRange([text, ""]);
            }
            foreach (var member in node.Elements("Members").Elements("Member"))
            {
                var memberName = Attr(member, "MemberName");
                var memberDocs = member.Element("Docs");
                var summary = memberDocs is not null ? Flatten(memberDocs.Element("summary")) : "";
                var memberSignature = Signature(member, "MemberSignature");
                if (summary.Length == 0 && memberSignature.Length == 0) continue;
                lines.AddRange([$"## {memberName}", ""]);
                if (memberSignature.Length > 0) lines.AddRange(["```csharp", memberSignature, "```", ""]);
                if (summary.Length > 0) lines.AddRange([summary, ""]);
            }
            var body = PyStr.Strip(string.Join("\n", lines));
            if (body.Length == 0) continue;
            yield return new Doc(relative, fullName, BaseUrl + fullName.ToLowerInvariant().Replace('`', '-'), "md", body);
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        foreach (var (_, relative, node) in Types(root))
        {
            var fullName = Attr(node, "FullName");
            if (fullName.Length == 0) fullName = Attr(node, "Name");
            if (fullName.Length == 0) continue;
            var docs = node.Element("Docs");
            var summary = docs is not null ? Flatten(docs.Element("summary")) : "";
            var ns = fullName.Contains('.') ? PyStr.BeforeLast(fullName, '.') : "";
            var typeSig = summary.Length > 0 ? summary : Signature(node, "TypeSignature");
            var typeAnchor = DocId(node, "TypeSignature");
            yield return new ApiSymbol(fullName, "type", ns, relative, typeAnchor, typeSig);
            var shortName = PyStr.AfterLast(fullName, '.');
            if (shortName.Length > 0 && shortName != fullName)
                yield return new ApiSymbol(shortName, "type", ns, relative, typeAnchor, typeSig);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in node.Elements("Members").Elements("Member"))
            {
                var memberName = Attr(member, "MemberName");
                if (memberName.Length == 0 || memberName.StartsWith("op_", StringComparison.Ordinal)) continue;
                var memberDocs = member.Element("Docs");
                var memberSummary = memberDocs is not null ? Flatten(memberDocs.Element("summary")) : "";
                var anchor = DocId(member, "MemberSignature");
                foreach (var candidate in new[] { $"{shortName}.{memberName}", memberName })
                {
                    if (!seen.Add(candidate)) continue;
                    yield return new ApiSymbol(candidate, "member", fullName, relative, anchor,
                        memberSummary.Length > 0 ? memberSummary : Signature(member, "MemberSignature"));
                }
            }
        }
    }
}
