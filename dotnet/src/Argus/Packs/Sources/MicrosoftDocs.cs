using System.Text.RegularExpressions;
using Argus.Util;

namespace Argus.Packs.Sources;

/// <summary>Front matter values: a scalar string or a list of strings.</summary>
public sealed class Meta : Dictionary<string, object>
{
    public Meta() : base(StringComparer.Ordinal) { }

    public string Str(string key) => TryGetValue(key, out var v) ? v switch { string s => s, List<string> l => "[" + string.Join(", ", l.Select(x => PyStr.Repr(x))) + "]", _ => "" } : "";
    public List<string>? List(string key) => TryGetValue(key, out var v) ? v as List<string> : null;
}

/// <summary>Microsoft Learn markdown conventions.</summary>
public static class MsLearn
{
    const RegexOptions O = RegexOptions.CultureInvariant;
    static readonly Regex FrontMatterRe = new(@"\A---\r?\n(.*?)\r?\n---\r?\n?", RegexOptions.Singleline | O);
    static readonly Regex ScalarRe = new(@"^([A-Za-z_][\w.\-]*)\s*:\s*(.*)$", O);
    static readonly Regex JsonListRe = new("\"([^\"]+)\"", O);
    static readonly Regex BlockItemRe = new(@"^[ \t]*-\s+(.*)$", O);
    static readonly Regex SyntaxRe = new(@"^##\s+Syntax\s*$.*?^```(?:\w+)?\s*$(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline | O);
    static readonly Regex AtxRe = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Multiline | O);
    static readonly Regex TitleNameRe = new(@"^([A-Za-z_][\w:]*)\s+(?:function|macro|structure|struct|enumeration|interface|callback|union|method|routine|ioctl)\b", O);
    static readonly Regex TitleQualifiedRe = new(@"^([A-Za-z_]\w*(?:::[A-Za-z_]\w*)+)\s*\(", O);
    static readonly Regex LearnMoreRe = new(@"^learn more about\s*:?\s*", RegexOptions.IgnoreCase | O);
    static readonly Regex ListItemRe = new(@"^\s*([-*+]|\d+\.)\s", O);

    static readonly Dictionary<string, string> UidKinds = new(StringComparer.Ordinal)
    {
        ["NF"] = "function", ["NS"] = "struct", ["NN"] = "interface", ["NE"] = "enum", ["NC"] = "callback",
        ["NI"] = "ioctl", ["NL"] = "class", ["NA"] = "apiset", ["NT"] = "typedef", ["ND"] = "define", ["NU"] = "union",
    };

    /// <summary>Python's multiline `$`: end of line, or before a final newline.</summary>
    static string Unquote(string s) => PyStr.Strip(PyStr.Strip(s, "\""), "'");

    public static (Meta Meta, string Body) ParseFrontMatter(string text)
    {
        var m = FrontMatterRe.Match(text);
        if (!m.Success) return (new Meta(), text);
        var meta = new Meta();
        string? pending = null;
        foreach (var line in PyStr.SplitLines(m.Groups[1].Value))
        {
            var item = BlockItemRe.Match(line);
            if (item.Success && pending is not null)
            {
                if (meta.GetValueOrDefault(pending) is not List<string>) meta[pending] = new List<string>();
                ((List<string>)meta[pending]).Add(PyStr.Strip(PyStr.Strip(PyStr.Strip(item.Groups[1].Value), "\""), "'"));
                continue;
            }
            var found = ScalarRe.Match(line);
            if (!found.Success) continue;
            var key = found.Groups[1].Value;
            var raw = PyStr.Strip(found.Groups[2].Value);
            pending = null;
            if (raw.StartsWith('[') && raw.EndsWith(']')) meta[key] = JsonListRe.Matches(raw).Select(x => x.Groups[1].Value).ToList();
            else if (raw.Length == 0) { pending = key; meta[key] = ""; }
            else meta[key] = Unquote(raw);
        }
        return (meta, text.Substring(m.Index + m.Length));
    }

    public static (string Kind, string Module, string Name)? ParseUid(string uid)
    {
        int colon = uid.IndexOf(':');
        if (colon < 0) return null;
        var prefix = uid.Substring(0, colon);
        var rest = uid.Substring(colon + 1);
        if (rest.Length == 0) return null;
        if (!UidKinds.TryGetValue(prefix.ToUpperInvariant(), out var kind)) return null;
        int dot = rest.IndexOf('.');
        var module = dot < 0 ? rest : rest.Substring(0, dot);
        var name = dot < 0 ? "" : rest.Substring(dot + 1);
        name = name.Split('~', 2)[0];
        if (name.Length == 0) return null;
        return (kind, module, name);
    }

    public static string FirstHeading(string body)
    {
        var m = AtxRe.Match(body);
        return m.Success ? PyStr.Strip(PyStr.Strip(m.Groups[2].Value), "`") : "";
    }

    public static string SyntaxSignature(string body, int limit = 400)
    {
        var m = SyntaxRe.Match(body);
        if (!m.Success) return "";
        return PyStr.Prefix(string.Join(" ", PyStr.SplitWhitespace(m.Groups[1].Value)), limit);
    }

    static readonly (string Label, string Key)[] OsFields =
    [
        ("Minimum client", "req.target-min-winverclnt"), ("Minimum server", "req.target-min-winversvr"),
        ("Redistributable", "req.redist"), ("Minimum KMDF", "req.kmdf-ver"), ("Minimum UMDF", "req.umdf-ver"),
    ];

    static string MetaText(Meta meta, string key) => meta.TryGetValue(key, out var v) ? meta.Str(key) : "";

    /// <summary>str(meta.get(key) or ''): an empty list or string is falsy.</summary>
    static bool Present(Meta meta, string key) =>
        meta.TryGetValue(key, out var v) && v switch { string s => PyStr.Strip(s).Length > 0, List<string> l => l.Count > 0, _ => false };

    public static string PrependRequirements(Meta meta, string description, string body)
    {
        var wanted = OsFields.Concat(new (string Label, string Key)[] { ("Header", "req.header"), ("Library", "req.lib"), ("DLL", "req.dll"), ("IRQL", "req.irql"), ("Unicode/ANSI", "req.unicode-ansi") });
        var lines = wanted.Where(w => Present(meta, w.Key)).Select(w => $"{w.Label}: {MetaText(meta, w.Key)}").ToList();
        var parts = new[] { description, string.Join("\n", lines), body }.Where(p => PyStr.Strip(p).Length > 0);
        return string.Join("\n\n", parts);
    }

    public static string CleanDescription(Meta meta)
    {
        var description = string.Join(" ", PyStr.SplitWhitespace(Present(meta, "description") ? meta.Str("description") : ""));
        var cleaned = PyStr.Strip(LearnMoreRe.Replace(description, "", 1));
        return cleaned.Length > 0 ? cleaned : description;
    }

    static readonly string[] ChromePrefixes = ["#", "```", ":::", ">", "|", "[!", "<", "---", "==="];

    public static string PageLede(string body, int words = 30)
    {
        bool fenced = false;
        foreach (var line in PyStr.SplitLines(body))
        {
            var text = PyStr.Strip(line);
            if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced || text.Length == 0) continue;
            if (ChromePrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)) || ListItemRe.IsMatch(text)) continue;
            return string.Join(" ", PyStr.SplitWhitespace(text).Take(words));
        }
        return "";
    }

    public static string RequirementLine(Meta meta)
    {
        var wanted = OsFields.Concat(new (string Label, string Key)[] { ("Header", "req.header"), ("Library", "req.lib"), ("DLL", "req.dll"), ("IRQL", "req.irql") });
        var contract = string.Join("; ", wanted.Where(w => Present(meta, w.Key)).Select(w => $"{w.Label}: {MetaText(meta, w.Key)}"));
        var description = CleanDescription(meta);
        if (description.Length == 0) return contract;
        return contract.Length > 0 ? $"{contract} -- {description}" : description;
    }

    public static List<string> Aliases(Meta meta, string symbol = "")
    {
        var output = new List<string>();
        var title = Present(meta, "title") ? meta.Str("title") : "";
        var named = TitleNameRe.Match(title);
        if (!named.Success) named = TitleQualifiedRe.Match(title);
        if (named.Success) output.Add(PyStr.RStrip(named.Groups[1].Value, ":"));
        if (symbol.Contains('.'))
        {
            int i = symbol.IndexOf('.');
            output.Add(symbol.Substring(0, i) + "::" + symbol.Substring(i + 1));
        }
        foreach (var key in new[] { "api_name", "f1_keywords" })
        {
            if (meta.List(key) is not { } values) continue;
            foreach (var entry in values)
            {
                var candidate = PyStr.AfterLast(entry, '/');
                if (candidate.Length > 0 && PyStr.IsAlnum(candidate.Replace("_", ""))) output.Add(candidate);
            }
        }
        return output;
    }

    public static string Title(Meta meta, string body, string relative)
    {
        var title = Present(meta, "title") ? meta.Str("title") : "";
        if (title.Length > 0) return title;
        var heading = FirstHeading(body);
        return heading.Length > 0 ? heading : relative;
    }

    public static string DescriptionOf(Meta meta) => Present(meta, "description") ? meta.Str("description") : "";
}

/// <summary>A Microsoft Learn API reference repository (sdk-api, windows-driver-docs-ddi).</summary>
public sealed record MicrosoftApiRef(string Name, string RepoUrl, string Branch, string Subtree, string BaseUrl,
    string License, string LicenseUrl, string Attribution) : ISource
{
    public static MicrosoftApiRef Win32Api() => new(
        "win32", "https://github.com/MicrosoftDocs/sdk-api", "docs", "sdk-api-src/content",
        "https://learn.microsoft.com/en-us/windows/win32/api/", "CC-BY-4.0",
        "https://github.com/MicrosoftDocs/sdk-api/blob/docs/LICENSE",
        "Windows SDK API reference. Copyright (c) Microsoft Corporation. Used under CC BY 4.0.");

    public static MicrosoftApiRef WdkDdi() => new(
        "wdk", "https://github.com/MicrosoftDocs/windows-driver-docs-ddi", "staging", "wdk-ddi-src/content",
        "https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/", "CC-BY-4.0",
        "https://github.com/MicrosoftDocs/windows-driver-docs-ddi/blob/staging/LICENSE",
        "Windows Driver Kit DDI reference. Copyright (c) Microsoft Corporation. Used under CC BY 4.0.");

    IEnumerable<string> Pages(string root) =>
        Walk.Files(Path.Combine(root, Subtree), n => n.EndsWith(".md", StringComparison.Ordinal) && n != "TOC.md");

    public IEnumerable<Doc> IterDocs(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var path in Pages(root))
        {
            var (meta, body) = MsLearn.ParseFrontMatter(Walk.ReadText(path));
            var relative = Walk.Relative(content, path);
            yield return new Doc(relative, MsLearn.Title(meta, body, relative), BaseUrl + relative[..^3], "md",
                MsLearn.PrependRequirements(meta, MsLearn.DescriptionOf(meta), body));
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var path in Pages(root))
        {
            var (meta, body) = MsLearn.ParseFrontMatter(Walk.ReadText(path));
            var uid = meta.TryGetValue("UID", out _) ? meta.Str("UID") : "";
            if (MsLearn.ParseUid(uid) is not { } parsed) continue;
            var docPath = Walk.Relative(content, path)[..^3];
            var signature = MsLearn.RequirementLine(meta);
            if (signature.Length == 0) signature = MsLearn.PageLede(body);
            var seen = new HashSet<string>(StringComparer.Ordinal) { parsed.Name };
            yield return new ApiSymbol(parsed.Name, parsed.Kind, parsed.Module, docPath, "", signature);
            foreach (var alias in MsLearn.Aliases(meta, parsed.Name))
            {
                if (!seen.Add(alias)) continue;
                yield return new ApiSymbol(alias, parsed.Kind, parsed.Module, docPath, "", signature);
            }
        }
    }
}

/// <summary>Microsoft C++, C and Assembler documentation (MicrosoftDocs/cpp-docs).</summary>
public sealed class CppDocs : ISource
{
    public string Name => "cpp";
    public string RepoUrl => "https://github.com/MicrosoftDocs/cpp-docs";
    public string Branch => "main";
    public string Subtree => "docs";
    public string License => "CC-BY-4.0";
    public string LicenseUrl => "https://github.com/MicrosoftDocs/cpp-docs/blob/main/LICENSE";
    public string Attribution => "Microsoft C++, C, and Assembler documentation. Copyright (c) Microsoft Corporation. Used under CC BY 4.0.";
    const string BaseUrl = "https://learn.microsoft.com/en-us/cpp/";

    IEnumerable<string> Pages(string root) =>
        Walk.Files(Path.Combine(root, Subtree), n => n.EndsWith(".md", StringComparison.Ordinal) && !n.StartsWith("TOC", StringComparison.Ordinal));

    public IEnumerable<Doc> IterDocs(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var path in Pages(root))
        {
            var (meta, body) = MsLearn.ParseFrontMatter(Walk.ReadText(path));
            var relative = Walk.Relative(content, path);
            var description = MsLearn.DescriptionOf(meta);
            yield return new Doc(relative, MsLearn.Title(meta, body, relative), BaseUrl + relative[..^3], "md",
                description.Length > 0 ? $"{description}\n\n{body}" : body);
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var content = Path.Combine(root, Subtree);
        foreach (var path in Pages(root))
        {
            var (meta, body) = MsLearn.ParseFrontMatter(Walk.ReadText(path));
            if (meta.List("f1_keywords") is not { } keywords) continue;
            var docPath = Walk.Relative(content, path)[..^3];
            var description = MsLearn.PageLede(body);
            if (description.Length == 0) description = MsLearn.CleanDescription(meta);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in keywords)
            {
                int slash = entry.IndexOf('/');
                var header = slash < 0 ? entry : entry.Substring(0, slash);
                var qualified = slash < 0 ? "" : entry.Substring(slash + 1);
                var symbol = qualified.Length > 0 ? qualified : header;
                if (symbol.Length == 0 || seen.Contains(symbol) || symbol.Contains(' ')) continue;
                seen.Add(symbol);
                yield return new ApiSymbol(symbol, symbol.EndsWith("_class", StringComparison.Ordinal) ? "class" : "cpp",
                    qualified.Length > 0 ? header : Name, docPath, "", description);
            }
        }
    }
}

/// <summary>Debugging Tools for Windows: the debugger command reference and its guide.</summary>
public sealed class DebuggerDocs : ISource
{
    public string Name => "debugger";
    public string RepoUrl => "https://github.com/MicrosoftDocs/windows-driver-docs";
    public string Branch => "staging";
    public string Subtree => "windows-driver-docs-pr";
    public string License => "CC-BY-4.0";
    public string LicenseUrl => "https://github.com/MicrosoftDocs/windows-driver-docs/blob/staging/LICENSE";
    public string Attribution => "Debugging Tools for Windows documentation. Copyright (c) Microsoft Corporation. Used under CC BY 4.0.";

    public const string CommandsDir = "debuggercmds";
    static readonly (string Dir, string Url)[] Docsets =
    [
        (CommandsDir, "https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/"),
        ("debugger", "https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/"),
    ];
    static readonly Regex TitleGlossRe = new(@"\s*\(.*$", RegexOptions.CultureInvariant);

    public static List<string> CommandNames(string? title)
    {
        var head = PyStr.Strip(TitleGlossRe.Replace(title ?? "", "", 1));
        var names = new List<string>();
        foreach (var part in head.Split(','))
        {
            var words = PyStr.SplitWhitespace(part);
            if (words.Count > 0) names.Add(words[0]);
        }
        return names;
    }

    public static List<string> AliasNames(Meta meta)
    {
        if (meta.List("api_name") is not { } raw) return [];
        return raw.Select(e => PyStr.Strip(e)).Where(t => t.Length > 0 && t != "NA" && PyStr.SplitWhitespace(t).Count == 1).ToList();
    }

    static IEnumerable<string> Pages(string content) =>
        Walk.Files(content, n => n.EndsWith(".md", StringComparison.Ordinal) && !n.StartsWith("TOC", StringComparison.Ordinal));

    public IEnumerable<Doc> IterDocs(string root)
    {
        foreach (var (docset, baseUrl) in Docsets)
        {
            var content = Path.Combine(root, Subtree, docset);
            if (!Directory.Exists(content)) continue;
            foreach (var path in Pages(content))
            {
                var (meta, body) = MsLearn.ParseFrontMatter(Walk.ReadText(path));
                var relative = Walk.Relative(content, path);
                var description = MsLearn.DescriptionOf(meta);
                yield return new Doc($"{docset}/{relative}", MsLearn.Title(meta, body, relative), baseUrl + relative[..^3], "md",
                    description.Length > 0 ? $"{description}\n\n{body}" : body);
            }
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var content = Path.Combine(root, Subtree, CommandsDir);
        if (!Directory.Exists(content)) yield break;
        var pages = new List<(string Path, Meta Meta, List<string> Names)>();
        foreach (var path in Pages(content))
        {
            var (meta, _) = MsLearn.ParseFrontMatter(Walk.ReadText(path));
            if (meta.List("topic_type") is not { } topics || !topics.Contains("apiref")) continue;
            pages.Add((path, meta, CommandNames(meta.TryGetValue("title", out _) ? meta.Str("title") : "")));
        }
        var commands = new HashSet<string>(pages.SelectMany(p => p.Names), StringComparer.Ordinal);
        foreach (var (path, meta, names) in pages)
        {
            var relative = Walk.Relative(content, path);
            var docPath = $"{CommandsDir}/{relative[..^3]}";
            var description = MsLearn.DescriptionOf(meta);
            var own = new HashSet<string>(names, StringComparer.Ordinal);
            var aliases = AliasNames(meta).Where(a => own.Contains(a) || !commands.Contains(a));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbol in names.Concat(aliases))
            {
                if (!seen.Add(symbol)) continue;
                yield return new ApiSymbol(symbol, "command", "debugger", docPath, "", description);
            }
        }
    }
}
