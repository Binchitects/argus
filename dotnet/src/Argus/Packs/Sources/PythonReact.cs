using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Argus.Indexing;
using Argus.Util;

namespace Argus.Packs.Sources;

public sealed class InventoryError(string message) : Exception(message);

public sealed record InventoryEntry(string Name, string Domain, string Role, int Priority, string Uri, string Dispname);

/// <summary>The CPython documentation, with symbols from Sphinx's objects.inv (python_docs.py).</summary>
public sealed class PythonDocs : ISource
{
    public string Name => "python";
    public string RepoUrl => "https://github.com/python/cpython";
    public string Branch => "3.14";
    public string Subtree => "Doc";
    public string License => "PSF-2.0";
    public string LicenseUrl => "https://docs.python.org/3/license.html";
    public string Attribution => "Python documentation. Copyright (c) 2001-2026 Python Software Foundation; All Rights Reserved. Used under the PSF License Agreement.";
    public const string DocsBaseUrl = "https://docs.python.org/3/";

    const RegexOptions O = RegexOptions.CultureInvariant;
    static readonly Regex LineRe = new(@"^(.+?)\s+(\S+)\s+(-?\d+)\s+?(\S*)\s+(.*)", O);
    static readonly string[] InventoryCandidates = ["objects.inv", "Doc/build/html/objects.inv", "Doc/objects.inv"];
    static readonly Regex UnderlineRe = new(@"^([=\-~^""'`#*+:.,_])\1{2,}\s*$", O);
    static readonly Regex DirectiveRe = new(
        @"^(\s*)\.\.\s+(?:(py|c):)?(module|currentmodule|function|method|class|exception|data|attribute|decorator|classmethod|staticmethod|macro|type|var|member|struct|union|enum|enumerator)::\s*(.+?)\s*$", O);
    static readonly HashSet<string> ObjectDirectives = new(StringComparer.Ordinal)
    {
        "function", "method", "class", "exception", "data", "attribute", "decorator", "classmethod", "staticmethod",
        "macro", "type", "var", "member", "struct", "union", "enum", "enumerator",
    };

    public static List<InventoryEntry> ParseObjectsInv(byte[] data)
    {
        int headerEnd = 0;
        for (int i = 0; i < 4; i++)
        {
            int nl = Array.IndexOf(data, (byte)'\n', headerEnd);
            if (nl < 0) throw new InventoryError("objects.inv is truncated: fewer than four header lines");
            headerEnd = nl + 1;
        }
        var header = Utf8.DecodeReplace(data[..headerEnd]);
        var first = PyStr.SplitLines(header)[0];
        if (!first.Contains("version 2"))
            throw new InventoryError($"unsupported inventory header {PyStr.Repr(first)}; only Sphinx inventory version 2 is understood");
        if (!header.Contains("zlib"))
            throw new InventoryError("inventory header does not declare zlib compression; refusing to guess at the payload encoding");
        string body;
        try
        {
            using var input = new MemoryStream(data, headerEnd, data.Length - headerEnd);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            body = new UTF8Encoding(false, true).GetString(output.ToArray());
        }
        catch (Exception exc) when (exc is InvalidDataException or DecoderFallbackException)
        {
            throw new InventoryError($"objects.inv payload is not valid zlib: {exc.Message}");
        }
        var entries = new List<InventoryEntry>();
        foreach (var line in PyStr.SplitLines(body))
        {
            if (PyStr.Strip(line).Length == 0) continue;
            var m = LineRe.Match(line);
            if (!m.Success) throw new InventoryError($"unparsable inventory line: {PyStr.Repr(line)}");
            var name = m.Groups[1].Value;
            var domainRole = m.Groups[2].Value;
            int colon = domainRole.IndexOf(':');
            var domain = colon < 0 ? domainRole : domainRole.Substring(0, colon);
            var role = colon < 0 ? "" : domainRole.Substring(colon + 1);
            var uri = m.Groups[4].Value;
            if (uri.EndsWith('$')) uri = uri[..^1] + name;
            var disp = PyStr.Strip(m.Groups[5].Value);
            entries.Add(new InventoryEntry(name, domain, role, int.Parse(m.Groups[3].Value), uri, disp == "-" ? name : disp));
        }
        return entries;
    }

    public static string ExtractTitle(string body)
    {
        var lines = PyStr.SplitLines(body);
        for (int i = 0; i < lines.Count; i++)
        {
            var text = PyStr.Strip(lines[i]);
            if (UnderlineRe.IsMatch(text) && i + 2 < lines.Count)
            {
                var candidate = PyStr.Strip(lines[i + 1]);
                if (candidate.Length > 0 && UnderlineRe.IsMatch(PyStr.Strip(lines[i + 2]))) return Chunker.StripRstInline(candidate);
            }
            if (text.Length == 0 || UnderlineRe.IsMatch(text)) continue;
            if (i + 1 < lines.Count)
            {
                var under = PyStr.Strip(lines[i + 1]);
                if (UnderlineRe.IsMatch(under) && PyStr.Len(under) >= PyStr.Len(text)) return Chunker.StripRstInline(text);
            }
        }
        return "";
    }

    static string PageKey(string path)
    {
        foreach (var suffix in new[] { ".html", ".rst", ".txt" })
            if (path.EndsWith(suffix, StringComparison.Ordinal)) return path[..^suffix.Length];
        return path;
    }

    static IEnumerable<(string Name, string Argument, string Summary)> IterObjects(string body)
    {
        var lines = PyStr.SplitLines(body);
        string module = "", className = "";
        int classIndent = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            var m = DirectiveRe.Match(lines[index]);
            if (!m.Success) continue;
            var indent = m.Groups[1].Value;
            var domain = m.Groups[2].Success ? m.Groups[2].Value : null;
            var directive = m.Groups[3].Value;
            var argument = m.Groups[4].Value;
            int depth = indent.Length;
            if (directive is "module" or "currentmodule")
            {
                (module, className, classIndent) = (PyStr.Strip(argument), "", -1);
                continue;
            }
            if (!ObjectDirectives.Contains(directive)) continue;
            if (domain == "c")
            {
                var cname = CName(argument);
                if (cname.Length > 0) yield return (cname, PyStr.Strip(argument), SummaryAfter(lines, index + 1, depth));
                continue;
            }
            var local = PyStr.Strip(argument.Split('(', 2)[0]);
            if (local.Length == 0) continue;
            string full;
            if (directive == "class")
            {
                (className, classIndent) = (local, depth);
                full = Qualify(module, local);
            }
            else if (className.Length > 0 && depth > classIndent)
            {
                var nested = local.StartsWith($"{className}.", StringComparison.Ordinal) ? local : $"{className}.{local}";
                full = Qualify(module, nested);
            }
            else
            {
                (className, classIndent) = ("", -1);
                full = Qualify(module, local);
            }
            yield return (full, PyStr.Strip(argument), SummaryAfter(lines, index + 1, depth));
        }
    }

    static string CName(string argument)
    {
        var head = argument.Split('(', 2)[0].Replace('*', ' ');
        var tokens = PyStr.SplitWhitespace(head);
        return tokens.Count > 0 ? tokens[^1] : "";
    }

    static string SummaryAfter(List<string> lines, int start, int depth, int words = 30)
    {
        int index = start;
        while (index < lines.Count && PyStr.Strip(lines[index]).Length > 0) index++;
        while (index < lines.Count)
        {
            var raw = lines[index];
            var text = PyStr.Strip(raw);
            index++;
            if (text.Length == 0) continue;
            if (raw.Length - PyStr.LStrip(raw).Length <= depth) return "";
            if (text.StartsWith(':') || text.StartsWith("..", StringComparison.Ordinal)) continue;
            return string.Join(" ", PyStr.SplitWhitespace(Chunker.StripRstInline(text)).Take(words));
        }
        return "";
    }

    static string Describe(string? signature, string? summary, string title)
    {
        signature = PyStr.Strip(signature ?? "");
        summary = PyStr.Strip(summary ?? "");
        if (signature.Length > 0 && summary.Length > 0) return $"{signature} -- {summary}";
        return signature.Length > 0 ? signature : summary.Length > 0 ? summary : title;
    }

    static string Qualify(string module, string local) =>
        module.Length > 0 && !local.StartsWith($"{module}.", StringComparison.Ordinal) ? $"{module}.{local}" : local;

    public IEnumerable<Doc> IterDocs(string root)
    {
        var docRoot = Path.Combine(root, Subtree);
        foreach (var path in Walk.Files(docRoot, n => n.EndsWith(".rst", StringComparison.Ordinal)))
        {
            var body = Walk.ReadText(path);
            var relative = Walk.Relative(docRoot, path);
            var html = relative[..^4] + ".html";
            var title = ExtractTitle(body);
            yield return new Doc(relative, title.Length > 0 ? title : relative, DocsBaseUrl + html, "rst", body);
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var entries = ParseObjectsInv(File.ReadAllBytes(FindInventory(root)));
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var doc in IterDocs(root))
        {
            foreach (var (name, argument, summary) in IterObjects(doc.Body))
            {
                signatures[name] = argument;
                if (summary.Length > 0) summaries[name] = summary;
            }
            titles[PageKey(doc.Path)] = doc.Title;
        }
        foreach (var entry in entries)
        {
            int hash = entry.Uri.IndexOf('#');
            if (hash < 0) continue;
            var docPath = entry.Uri.Substring(0, hash);
            var anchor = entry.Uri.Substring(hash + 1);
            if (anchor.Length == 0) continue;
            yield return new ApiSymbol(entry.Name, entry.Role, entry.Domain, docPath, anchor,
                Describe(signatures.GetValueOrDefault(entry.Name), summaries.GetValueOrDefault(entry.Name), titles.GetValueOrDefault(PageKey(docPath), "")));
        }
    }

    string FindInventory(string root)
    {
        foreach (var candidate in InventoryCandidates)
        {
            var path = Path.Combine(root, candidate);
            if (File.Exists(path)) return path;
        }
        throw new InventoryError(
            $"no objects.inv under {root}; looked in {string.Join(", ", InventoryCandidates)}. It is a build artifact, so a bare source checkout will not have one -- fetch the published inventory from {DocsBaseUrl}objects.inv");
    }
}

/// <summary>react.dev: markdown with JSX, symbols from code-span headings with pinned anchors (react_docs.py).</summary>
public sealed class ReactDocs : ISource
{
    public string Name => "react";
    public string RepoUrl => "https://github.com/reactjs/react.dev";
    public string Branch => "main";
    public string Subtree => "src/content";
    public string License => "CC-BY-4.0";
    public string LicenseUrl => "https://github.com/reactjs/react.dev/blob/main/LICENSE-DOCS.md";
    public string Attribution => "React documentation. Copyright (c) Meta Platforms, Inc. and affiliates. Used under CC BY 4.0.";
    const string BaseUrl = "https://react.dev/";
    static readonly string[] DocSuffixes = [".md", ".mdx"];

    const RegexOptions O = RegexOptions.CultureInvariant;
    static readonly Regex FrontMatterLineRe = new(@"^([A-Za-z_][\w-]*)\s*:\s*(.*)$", O);
    static readonly Regex HeadingRe = new(@"^(#{1,6})[ \t]+(.+?)[ \t]*(?:\{/\*(?<anchor>.*?)\*/\})?[ \t]*$", O);
    static readonly Regex CodeSpanRe = new(@"^`([^`]+)`$", O);
    static readonly Regex ApiRe = new(
        @"^(?:<(?<component>[A-Z][A-Za-z0-9]*)\s*/?>|(?<name>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)(?<call>\([^`]*\))?)$", O);
    static readonly Regex HookRe = new(@"^use[A-Z$_]", O);
    static readonly Regex JsxLineRe = new(@"^\s*</?[A-Za-z][\w.]*(?:\s[^>]*?)?/?>\s*$", O);
    static readonly Regex ModuleLineRe = new(@"^\s*(?:import|export)\s", O);

    public static (Dictionary<string, string> Meta, string Body) ParseFrontMatter(string text)
    {
        var lines = PyStr.SplitLines(text);
        if (lines.Count == 0 || PyStr.Strip(lines[0]) != "---") return (new(StringComparer.Ordinal), text);
        for (int index = 1; index < lines.Count; index++)
        {
            if (PyStr.Strip(lines[index]) != "---") continue;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1).Take(index - 1))
            {
                var m = FrontMatterLineRe.Match(line);
                if (m.Success) meta[m.Groups[1].Value] = PyStr.Strip(PyStr.Strip(m.Groups[2].Value), "'\"");
            }
            return (meta, string.Join("\n", lines.Skip(index + 1)));
        }
        return (new(StringComparer.Ordinal), text);
    }

    public static string StripJsx(string body)
    {
        var lines = PyStr.SplitLines(body);
        var flags = Chunker.FencedLineFlags(lines);
        return string.Join("\n", lines.Select((line, i) =>
            flags[i] || !(JsxLineRe.IsMatch(line) || ModuleLineRe.IsMatch(line)) ? line : ""));
    }

    static (string Name, string Kind, string Signature)? Classify(string inner)
    {
        var m = ApiRe.Match(PyStr.Strip(inner));
        if (!m.Success) return null;
        if (m.Groups["component"].Success && m.Groups["component"].Value.Length > 0) return (m.Groups["component"].Value, "component", "");
        var name = m.Groups["name"].Value;
        var call = m.Groups["call"].Success ? m.Groups["call"].Value : "";
        string kind = HookRe.IsMatch(name) ? "hook" : call.Length > 0 ? "function" : name.Contains('.') ? "member" : "api";
        return (name, kind, call.Length > 0 ? name + call : "");
    }

    public static IEnumerable<(string Name, string Kind, string Signature, string Anchor)> IterHeadingSymbols(string body)
    {
        var lines = PyStr.SplitLines(body);
        var flags = Chunker.FencedLineFlags(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            if (flags[i]) continue;
            var heading = HeadingRe.Match(lines[i]);
            if (!heading.Success) continue;
            var anchor = heading.Groups["anchor"].Success ? heading.Groups["anchor"].Value : "";
            if (anchor.Length == 0) continue;
            var span = CodeSpanRe.Match(PyStr.Strip(heading.Groups[2].Value));
            if (!span.Success) continue;
            if (Classify(span.Groups[1].Value) is not { } c) continue;
            yield return (c.Name, c.Kind, c.Signature, PyStr.Strip(anchor));
        }
    }

    public static string FirstHeading(string body)
    {
        var lines = PyStr.SplitLines(body);
        var flags = Chunker.FencedLineFlags(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            if (flags[i]) continue;
            var heading = HeadingRe.Match(lines[i]);
            if (!heading.Success) continue;
            var text = PyStr.Strip(heading.Groups[2].Value);
            var span = CodeSpanRe.Match(text);
            return span.Success ? span.Groups[1].Value : text;
        }
        return "";
    }

    public static string UrlPath(string relative)
    {
        foreach (var suffix in DocSuffixes)
            if (relative.EndsWith(suffix, StringComparison.Ordinal)) { relative = relative[..^suffix.Length]; break; }
        if (relative.EndsWith("/index", StringComparison.Ordinal)) relative = relative[..^"/index".Length];
        else if (relative == "index") relative = "";
        return relative;
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var path in Walk.Files(content, n => DocSuffixes.Contains(Indexing.Filters.Suffix(n))))
        {
            var (meta, body) = ParseFrontMatter(Walk.ReadText(path));
            var relative = Walk.Relative(content, path);
            var title = meta.GetValueOrDefault("title", "");
            if (title.Length == 0) title = FirstHeading(body);
            if (title.Length == 0) title = relative;
            yield return new Doc(relative, title, BaseUrl + UrlPath(relative), "md", StripJsx(body));
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        foreach (var doc in IterDocs(root))
        {
            var urlPath = UrlPath(doc.Path);
            var ns = urlPath.Contains('/') ? PyStr.BeforeLast(urlPath, '/') : "";
            if (ns.Length == 0) ns = Name;
            foreach (var (name, kind, signature, anchor) in IterHeadingSymbols(doc.Body))
                yield return new ApiSymbol(name, kind, ns, urlPath, anchor, signature);
        }
    }
}
