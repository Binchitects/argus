using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// How a use was tied to what it names, surest first: the code's own context (its type and bases, its namespace and usings,
/// its imports), the declared type of what it is called on, the one definition of that name, or a guess among two or three.
/// </summary>
internal enum Evidence
{
    Scope,
    Type,
    Name,
    Guess,
}

/// <summary>A symbol where the index keeps it; overloads share one id (path#Qualified).</summary>
internal sealed record SymbolRef(IndexedFile File, CodeSymbol Symbol)
{
    public string Id => $"{File.Path}#{Symbol.Qualified}";
}

/// <summary>
/// A name used in code, on a line of a file, inside a symbol (null: at the file's top), and what it was tied to: one symbol, or
/// two or three for a guess; none when nothing in the folder declares it (<see cref="Candidates"/> 0) or more than three do.
/// </summary>
internal sealed record CodeEdge(string Path, int Line, CodeSymbol? From, string Name, bool Call, IReadOnlyList<SymbolRef> To, Evidence? Evidence, int Candidates)
{
    /// <summary>Declared in the folder, but in too many places to say which.</summary>
    public bool Unresolved => To.Count == 0 && Candidates > 3;
}

/// <summary>A file as it is on disk now: its text, what a scan finds in it, and what its imports bind (TypeScript and Python).</summary>
internal sealed class FileNow
{
    public required IndexedFile File { get; init; }
    public required string Text { get; init; }
    /// <summary>The text with comments and strings blanked: where declared types are looked for.</summary>
    public required string Code { get; init; }
    public required ScannedFile Scanned { get; init; }
    /// <summary>Its lines, split once.</summary>
    public string[] Lines => _lines ??= Text.Split('\n');
    private string[]? _lines;
    /// <summary>Each symbol's name on the line that declares it: not a use.</summary>
    public HashSet<(string Name, int Line)> Declared { get; } = [];
    /// <summary>C#: the namespaces its usings (and the project's global ones) bring in.</summary>
    public List<string>? Usings { get; set; }
    /// <summary>The type each name is declared with or made from in the file (CodeGraph.TypesIn).</summary>
    public Dictionary<string, string> Types { get; init; } = [];
    /// <summary>A local name an import binds: the module or path imported, and the name there ("*": the module itself; "default").</summary>
    public Dictionary<string, (string Spec, string Name)> Bindings { get; } = new(StringComparer.Ordinal);

    /// <summary>The innermost symbol a line is in (namespaces aside); null at the file's top.</summary>
    public CodeSymbol? Enclosing(int line)
    {
        CodeSymbol? best = null;
        foreach (var s in Scanned.Symbols)
        {
            if (s.Start <= line && line <= s.End && s.Kind is not ("namespace" or "heading") && (best is null || s.End - s.Start <= best.End - best.Start))
            {
                best = s;
            }
        }
        return best;
    }

    /// <summary>The uses in its code, less the names where they are declared.</summary>
    public IEnumerable<CodeUse> Uses => Scanned.Uses.Where(u => !Declared.Contains((u.Name, u.Line)));
}

/// <summary>
/// Who uses what, worked out when asked from the files as they are now: each use of a name in code is tied to what it names
/// by the language's own rules where the code says enough (C#: the enclosing types and their bases, the namespace, usings;
/// TypeScript: imports followed to the file that exports the name, through tsconfig paths and re-exports; Python: imports,
/// relative ones too, and self), else by the declared type of what it is called on, else by the name if one thing declares
/// it, else a guess among two or three; more than three stay unresolved, and are counted.
/// </summary>
internal sealed partial class CodeGraph
{
    private static readonly string[] TypeKinds = ["class", "struct", "interface", "enum", "record", "delegate", "type", "component"];
    private static readonly string[] ScriptEnds = [".ts", ".tsx", ".d.ts", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs"];
    private const int MostHops = 3;

    private readonly CodeIndex _index;
    private readonly Dictionary<string, IndexedFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SymbolRef>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SymbolRef>> _byQualified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IndexedFile>> _modules = new(StringComparer.Ordinal);
    private readonly List<string> _globalUsings = [];
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, FileNow? Now)> _now = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<(string Pattern, List<string> Targets)>> _paths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<SymbolRef>> _bases = new(StringComparer.Ordinal);

    public CodeGraph(CodeIndex index, IEnumerable<IndexedFile> files)
    {
        _index = index;
        foreach (var f in files)
        {
            _files[f.Path] = f;
            foreach (var (s, _) in f.Symbols)
            {
                if (s.Kind is "heading" or "main")
                {
                    continue;
                }
                var r = new SymbolRef(f, s);
                Add(_byName, s.Name, r);
                Add(_byQualified, s.Qualified, r);
            }
            if (f.Lang == "python")
            {
                var path = f.Path[..f.Path.LastIndexOf('.')];
                if (path.EndsWith("/__init__", StringComparison.Ordinal) || path == "__init__")
                {
                    path = Dir(path);
                }
                var parts = path.Split('/');
                for (var k = 0; k < parts.Length; k++)
                {
                    Add(_modules, string.Join('.', parts[k..]), f);
                }
            }
            if (f.Lang == "csharp")
            {
                _globalUsings.AddRange(f.Imports.Where(i => i.StartsWith("global ", StringComparison.Ordinal)).Select(i => i[7..]));
            }
        }
    }

    public CodeIndex Index => _index;

    private static void Add<T>(Dictionary<string, List<T>> into, string key, T value)
    {
        if (!into.TryGetValue(key, out var list))
        {
            into[key] = list = [];
        }
        list.Add(value);
    }

    /// <summary>The family a language's names are looked up in: TypeScript and JavaScript import each other.</summary>
    public static string? Family(string? lang) => lang switch
    {
        "csharp" => "csharp",
        "typescript" or "javascript" => "script",
        "python" => "python",
        _ => null,
    };

    // ------------------------------------------------------------------------------------------------- asking

    /// <summary>The symbols a name names: Name, Type.Member (or any tail of the qualified name), or path#Qualified as answers cite them.</summary>
    public List<SymbolRef> Find(string name)
    {
        name = name.Trim().TrimEnd('(', ')');
        IEnumerable<SymbolRef> found;
        if (name.IndexOf('#') is var hash and > 0 && _files.TryGetValue(name[..hash], out var file))
        {
            var q = name[(hash + 1)..];
            found = file.Symbols.Where(s => s.Symbol.Qualified == q || s.Symbol.Qualified.EndsWith("." + q, StringComparison.Ordinal))
                .Select(s => new SymbolRef(file, s.Symbol));
        }
        else if (name.Contains('.', StringComparison.Ordinal))
        {
            var last = name[(name.LastIndexOf('.') + 1)..];
            found = _byName.GetValueOrDefault(last, []).Where(r => r.Symbol.Qualified == name || r.Symbol.Qualified.EndsWith("." + name, StringComparison.Ordinal));
        }
        else
        {
            found = _byName.GetValueOrDefault(name, []);
        }
        return Distinct(found).OrderBy(r => r.File.Tags.HasFlag(CodeFileTags.Test)).ThenBy(r => r.File.Path, StringComparer.Ordinal).ThenBy(r => r.Symbol.Start).ToList();
    }

    /// <summary>
    /// The uses of a symbol across the folder (calls and references, in code): each use of its name, in the files that use the
    /// name, tied to it; and how many uses of the name could be tied to nothing for certain (more than three declare it).
    /// </summary>
    public (List<CodeEdge> Uses, int Unresolved) UsesOf(SymbolRef target, int mostFiles = 400)
    {
        var name = target.Symbol.Name;
        var family = Family(target.File.Lang);
        var uses = new List<CodeEdge>();
        var unresolved = 0;
        foreach (var f in _files.Values.Where(f => f.Names.Contains(name) && Family(f.Lang) == family).OrderBy(f => f.Path, StringComparer.Ordinal).Take(mostFiles))
        {
            if (Now(f) is not { } now)
            {
                continue;
            }
            foreach (var use in now.Uses.Where(u => u.Name == name))
            {
                var edge = Resolve(now, use);
                if (edge.To.Any(t => t.Id == target.Id))
                {
                    uses.Add(edge);
                }
                else if (edge.Unresolved)
                {
                    unresolved++;
                }
            }
        }
        return (uses, unresolved);
    }

    /// <summary>What a symbol uses (calls and references in its own lines), each tied to what it names; itself left out.</summary>
    public List<CodeEdge> UsesFrom(SymbolRef from)
    {
        if (Now(from.File) is not { } now)
        {
            return [];
        }
        // Its lines as they are now: the same symbol, nearest where the index saw it.
        var here = now.Scanned.Symbols.Where(s => s.Qualified == from.Symbol.Qualified).MinBy(s => Math.Abs(s.Start - from.Symbol.Start)) ?? from.Symbol;
        var edges = new List<CodeEdge>();
        foreach (var use in now.Uses.Where(u => u.Line >= here.Start && u.Line <= here.End))
        {
            var edge = Resolve(now, use);
            if (edge.To.Count > 0 && !edge.To.Any(t => t.Id == from.Id))
            {
                edges.Add(edge);
            }
        }
        return edges;
    }

    /// <summary>Every use in a file's code, resolved.</summary>
    public List<CodeEdge> EdgesIn(IndexedFile file) => Now(file) is { } now ? [.. now.Uses.Select(u => Resolve(now, u))] : [];

    /// <summary>The files a file imports, where they are in the folder (TypeScript and Python; C#'s usings name namespaces, not files).</summary>
    public List<IndexedFile> Imported(IndexedFile file)
    {
        var family = Family(file.Lang);
        if (family == "script")
        {
            return [.. file.Imports.Select(spec => ScriptTarget(file, spec)).OfType<IndexedFile>().Distinct()];
        }
        if (family == "python" && Now(file) is { } now)
        {
            return [.. now.Bindings.Values.Select(b => PythonModule(file, b.Spec)).Concat(file.Imports.Select(i => PythonModule(file, i))).OfType<IndexedFile>().Distinct()];
        }
        return [];
    }

    // ------------------------------------------------------------------------------------------------- reading now

    /// <summary>A file read and scanned as it is now (kept while its size and time stay); null for a secret's file, or one the scanners do not read.</summary>
    public FileNow? Now(IndexedFile file)
    {
        if (file.Tags.HasFlag(CodeFileTags.Sensitive) || Family(file.Lang) is null)
        {
            return null;
        }
        var full = _index.FullOf(file.Path);
        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists || info.Length > CodeFiles.MostSymbolBytes)
            {
                return null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (_now.TryGetValue(file.Path, out var kept) && kept.Size == info.Length && kept.Modified == info.LastWriteTimeUtc)
        {
            return kept.Now;
        }
        FileNow? now = null;
        try
        {
            var text = Files.ReadText(full).Text;
            if (CodeIndex.Scan(file.Lang, text) is { } scanned)
            {
                var code = Argus.Util.CodeText.Blank(file.Lang, text);
                now = new FileNow { File = file, Text = text, Code = code, Scanned = scanned, Types = TypesIn(code, Family(file.Lang)) };
                Declare(now);
                Bind(now);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        _now[file.Path] = (info.Length, info.LastWriteTimeUtc, now);
        return now;
    }

    /// <summary>Each symbol's name on the first line of it that holds the name (its attributes and decorators come before).</summary>
    private static void Declare(FileNow now)
    {
        var lines = now.Code.Split('\n');
        foreach (var s in now.Scanned.Symbols)
        {
            for (var l = s.Start; l <= Math.Min(s.End, s.Start + 12) && l <= lines.Length; l++)
            {
                if (HasWord(lines[l - 1], s.Name))
                {
                    now.Declared.Add((s.Name, l));
                    break;
                }
            }
        }
    }

    private static bool HasWord(string line, string word)
    {
        for (var at = line.IndexOf(word, StringComparison.Ordinal); at >= 0; at = line.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !(char.IsLetterOrDigit(line[at - 1]) || line[at - 1] is '_' or '$');
            var end = at + word.Length;
            var after = end >= line.Length || !(char.IsLetterOrDigit(line[end]) || line[end] is '_' or '$');
            if (before && after)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>What a TypeScript or Python file's imports bind, by local name.</summary>
    private static void Bind(FileNow now)
    {
        var family = Family(now.File.Lang);
        if (family == "script")
        {
            var code = Argus.Util.CodeText.Blank(now.File.Lang, now.Text, keepStrings: true);
            foreach (Match m in ScriptImport().Matches(code))
            {
                var spec = m.Groups["spec"].Value;
                if (m.Groups["default"].Success)
                {
                    now.Bindings[m.Groups["default"].Value] = (spec, "default");
                }
                if (m.Groups["all"].Success)
                {
                    now.Bindings[m.Groups["all"].Value] = (spec, "*");
                }
                foreach (var part in m.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w != "type").ToArray();
                    if (words.Length == 1)
                    {
                        now.Bindings[words[0]] = (spec, words[0]);
                    }
                    else if (words.Length == 3 && words[1] == "as")
                    {
                        now.Bindings[words[2]] = (spec, words[0]);
                    }
                }
            }
            foreach (Match m in ScriptRequire().Matches(code))
            {
                now.Bindings[m.Groups[1].Value] = (m.Groups[2].Value, "*");
            }
        }
        else if (family == "python")
        {
            foreach (Match m in PythonFrom().Matches(now.Code))
            {
                foreach (var part in m.Groups[2].Value.Trim('(', ')', ' ').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 1 && words[0] != "*")
                    {
                        now.Bindings[words[0]] = (m.Groups[1].Value, words[0]);
                    }
                    else if (words.Length == 3 && words[1] == "as")
                    {
                        now.Bindings[words[2]] = (m.Groups[1].Value, words[0]);
                    }
                }
            }
            foreach (Match m in PythonImport().Matches(now.Code))
            {
                foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 3 && words[1] == "as")
                    {
                        now.Bindings[words[2]] = (words[0], "*");
                    }
                    else if (words.Length == 1)
                    {
                        // import a.b: a (and b, as code calls it a.b.f(): the name before the call) bound to the module.
                        now.Bindings[words[0].Split('.')[0]] = (words[0].Split('.')[0], "*");
                        now.Bindings[words[0].Split('.')[^1]] = (words[0], "*");
                    }
                }
            }
        }
    }

    [GeneratedRegex("""\bimport\s+(?:type\s+)?(?:(?<default>[A-Za-z_$][\w$]*)\s*,?\s*)?(?:\*\s*as\s+(?<all>[A-Za-z_$][\w$]*)|\{(?<names>[^}]*)\})?\s*from\s*["'](?<spec>[^"'\n]+)["']""")]
    private static partial Regex ScriptImport();

    [GeneratedRegex("""\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*require\s*\(\s*["']([^"'\n]+)["']""")]
    private static partial Regex ScriptRequire();

    [GeneratedRegex(@"^[ \t]*from\s+(\.*[\w.]*)\s+import\s+(\([^)]*\)|[^\n]+)", RegexOptions.Multiline)]
    private static partial Regex PythonFrom();

    [GeneratedRegex(@"^[ \t]*import\s+([^\n]+)", RegexOptions.Multiline)]
    private static partial Regex PythonImport();

    // ------------------------------------------------------------------------------------------------- resolving

    /// <summary>A use tied to what it names: the language's rules first, then the name.</summary>
    public CodeEdge Resolve(FileNow now, CodeUse use)
    {
        var from = now.Enclosing(use.Line);
        var (to, evidence) = Family(now.File.Lang) switch
        {
            "csharp" => CSharp(now, from, use),
            "script" => Script(now, from, use),
            "python" => Python(now, from, use),
            _ => ([], null),
        };
        if (to.Count > 0)
        {
            to = Distinct(to);
            return new CodeEdge(now.File.Path, use.Line, from, use.Name, use.Call, to, evidence, to.Count);
        }
        // A plain lower-case name that is not called is a local's or a parameter's, not something declared elsewhere.
        if (!use.Call && use.Receiver is null && !char.IsUpper(use.Name[0]))
        {
            return new CodeEdge(now.File.Path, use.Line, from, use.Name, use.Call, [], null, 0);
        }
        var family = Family(now.File.Lang);
        var all = Distinct(_byName.GetValueOrDefault(use.Name, []).Where(r => Family(r.File.Lang) == family));
        var code = all.Where(r => !r.File.Tags.HasFlag(CodeFileTags.Test)).ToList();
        var candidates = code.Count > 0 ? code : all;
        return candidates.Count switch
        {
            0 => new CodeEdge(now.File.Path, use.Line, from, use.Name, use.Call, [], null, 0),
            1 => new CodeEdge(now.File.Path, use.Line, from, use.Name, use.Call, candidates, Evidence.Name, 1),
            <= 3 => new CodeEdge(now.File.Path, use.Line, from, use.Name, use.Call, Near(candidates, now.File.Path), Evidence.Guess, candidates.Count),
            _ => new CodeEdge(now.File.Path, use.Line, from, use.Name, use.Call, [], null, candidates.Count),
        };
    }

    private static List<SymbolRef> Distinct(IEnumerable<SymbolRef> refs) => [.. refs.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First())];

    /// <summary>Candidates nearest a path first: the most folders in common, then by path.</summary>
    private static List<SymbolRef> Near(List<SymbolRef> refs, string path) =>
        [.. refs.OrderByDescending(r => Common(r.File.Path, path)).ThenBy(r => r.File.Path, StringComparer.Ordinal).ThenBy(r => r.Symbol.Start)];

    private static int Common(string a, string b)
    {
        var x = a.Split('/');
        var y = b.Split('/');
        var n = 0;
        while (n < x.Length - 1 && n < y.Length - 1 && x[n] == y[n])
        {
            n++;
        }
        return n;
    }

    private static IEnumerable<string> Prefixes(string? qualified)
    {
        for (var q = qualified; !string.IsNullOrEmpty(q); q = q.LastIndexOf('.') is var dot and > 0 ? q[..dot] : null)
        {
            yield return q;
        }
    }

    /// <summary>The symbols of a qualified name in a family; in one file only for TypeScript and Python, whose names are a file's own.</summary>
    private List<SymbolRef> Named(string qualified, string family, IndexedFile? only = null) =>
        _byQualified.TryGetValue(qualified, out var refs) ? [.. refs.Where(r => Family(r.File.Lang) == family && (only is null || r.File == only))] : [];

    private static bool IsType(SymbolRef r) => TypeKinds.Contains(r.Symbol.Kind);

    /// <summary>A type's members by name: its own (all its partial parts), else its bases', up to three levels.</summary>
    private List<SymbolRef> Members(SymbolRef type, string name, int depth = 0)
    {
        var family = Family(type.File.Lang)!;
        var own = Named($"{type.Symbol.Qualified}.{name}", family, family == "csharp" ? null : type.File);
        if (own.Count > 0 || depth >= MostHops)
        {
            return own;
        }
        foreach (var b in Bases(type))
        {
            if (b.Id != type.Id && Members(b, name, depth + 1) is { Count: > 0 } inherited)
            {
                return inherited;
            }
        }
        return [];
    }

    /// <summary>A type's bases in the folder, by the names its first line gives.</summary>
    private List<SymbolRef> Bases(SymbolRef type) => _bases.GetOrAdd(type.Id, _ => BasesNow(type));

    private List<SymbolRef> BasesNow(SymbolRef type)
    {
        var family = Family(type.File.Lang)!;
        var bases = new List<SymbolRef>();
        var parts = family == "csharp" ? Named(type.Symbol.Qualified, family).Where(IsType).ToList() : [type];
        foreach (var part in parts)
        {
            foreach (var name in BasesOf(part.Symbol, family))
            {
                var found = family switch
                {
                    "csharp" => CSharpType(part.Symbol.Parent, Usings(part.File.Imports), name),
                    "script" => Now(part.File) is { } n ? ScriptName(n, null, name) : [],
                    _ => Now(part.File) is { } n ? PythonName(n, null, name) : [],
                };
                bases.AddRange(found.Where(IsType));
            }
        }
        return bases;
    }

    /// <summary>The names of a type's bases, from its first line: C#'s base list, extends and implements, Python's parentheses.</summary>
    internal static List<string> BasesOf(CodeSymbol type, string family)
    {
        var sig = type.Signature;
        var at = sig.IndexOf(type.Name, StringComparison.Ordinal);
        if (at < 0)
        {
            return [];
        }
        var rest = sig[(at + type.Name.Length)..];
        string list;
        if (family == "csharp")
        {
            var depth = 0;
            var colon = -1;
            for (var i = 0; i < rest.Length && colon < 0; i++)
            {
                depth += rest[i] is '<' or '(' or '[' ? 1 : rest[i] is '>' or ')' or ']' ? -1 : 0;
                colon = depth == 0 && rest[i] == ':' ? i : -1;
            }
            if (colon < 0)
            {
                return [];
            }
            list = rest[(colon + 1)..];
            list = list.Split('{')[0];
            list = Regex.Split(list, @"\bwhere\b")[0];
        }
        else if (family == "script")
        {
            var m = Regex.Match(rest, @"\b(?:extends|implements)\s+([^{]+)");
            if (!m.Success)
            {
                return [];
            }
            list = Regex.Replace(m.Groups[1].Value, @"\b(?:extends|implements)\b", ",");
        }
        else
        {
            var open = rest.IndexOf('(');
            var close = rest.LastIndexOf(')');
            if (open < 0 || close < open || rest[..open].Trim().Length > 0)
            {
                return [];
            }
            list = rest[(open + 1)..close];
        }
        // Generic arguments and arguments taken off: Base<T>(x) is Base.
        var names = new List<string>();
        var plain = new System.Text.StringBuilder();
        var nest = 0;
        foreach (var c in list)
        {
            nest += c is '<' or '(' or '[' ? 1 : c is '>' or ')' or ']' ? -1 : 0;
            if (nest == 0 && c is not ('>' or ')' or ']'))
            {
                plain.Append(c);
            }
        }
        foreach (var part in plain.ToString().Split(','))
        {
            var name = part.Trim();
            if (name.Length > 0 && !name.Contains('=') && Regex.IsMatch(name, @"^[A-Za-z_$][\w$.]*$"))
            {
                names.Add(name);
            }
        }
        return names;
    }

    // ------------------------------------------------------------------------------------------------- C#

    private List<string> Usings(IEnumerable<string> imports) =>
        [.. imports.Select(i => i.StartsWith("global ", StringComparison.Ordinal) ? i[7..] : i).Concat(_globalUsings).Distinct(StringComparer.Ordinal)];

    /// <summary>A type a name names from a place: the enclosing types and namespaces outwards, then the usings, then the global namespace.</summary>
    private List<SymbolRef> CSharpType(string? at, List<string> usings, string name)
    {
        if (name.Contains('.', StringComparison.Ordinal) && Named(name, "csharp").Where(IsType).ToList() is { Count: > 0 } whole)
        {
            return whole;
        }
        foreach (var p in Prefixes(at))
        {
            if (Named($"{p}.{name}", "csharp").Where(IsType).ToList() is { Count: > 0 } here)
            {
                return here;
            }
        }
        foreach (var u in usings)
        {
            if (Named($"{u}.{name}", "csharp").Where(IsType).ToList() is { Count: > 0 } used)
            {
                return used;
            }
            // using A.B.Name; (a using static, or an alias's target) names the type itself.
            if (u == name || u.EndsWith("." + name, StringComparison.Ordinal))
            {
                if (Named(u, "csharp").Where(IsType).ToList() is { Count: > 0 } type)
                {
                    return type;
                }
            }
        }
        return [.. Named(name, "csharp").Where(IsType)];
    }

    private (List<SymbolRef>, Evidence?) CSharp(FileNow now, CodeSymbol? from, CodeUse use)
    {
        var at = from?.Qualified ?? now.Scanned.Symbols.FirstOrDefault(s => s.Kind == "namespace")?.Qualified;
        var usings = now.Usings ??= Usings(now.Scanned.Imports);
        var name = use.Name;
        if (use.Receiver is null or "this" or "base")
        {
            foreach (var p in Prefixes(at))
            {
                var here = Named(p, "csharp").Where(IsType).ToList();
                if (use.Receiver != "base" && Named($"{p}.{name}", "csharp") is { Count: > 0 } member && (use.Receiver is null || here.Count > 0))
                {
                    return (member, Evidence.Scope);
                }
                foreach (var type in here)
                {
                    foreach (var b in Bases(type))
                    {
                        if (Members(b, name) is { Count: > 0 } inherited)
                        {
                            return (inherited, Evidence.Scope);
                        }
                    }
                }
                if (use.Receiver is not null && here.Count > 0)
                {
                    // this.X and base.X: the enclosing type only.
                    return ([], null);
                }
            }
            if (use.Receiver is null)
            {
                foreach (var u in usings)
                {
                    if (Named($"{u}.{name}", "csharp") is { Count: > 0 } used)
                    {
                        return (used, Evidence.Scope);
                    }
                }
                if (Named(name, "csharp") is { Count: > 0 } top)
                {
                    return (top, Evidence.Scope);
                }
            }
            return ([], null);
        }
        // On a type (a static member, a nested type, an enum's value), else on a variable of a type declared in the file.
        if (CSharpType(at, usings, use.Receiver) is { Count: > 0 } types)
        {
            if (types.SelectMany(t => Members(t, name)).ToList() is { Count: > 0 } members)
            {
                return (members, Evidence.Scope);
            }
        }
        else if (DeclaredType(now, use.Receiver) is { } declared)
        {
            var type = CSharpType(at, usings, declared);
            if (type.Count == 0 && _byName.GetValueOrDefault(declared[(declared.LastIndexOf('.') + 1)..], []).Where(r => r.File.Lang == "csharp" && IsType(r)).ToList() is { Count: 1 } one)
            {
                type = one;
            }
            if (type.SelectMany(t => Members(t, name)).ToList() is { Count: > 0 } members)
            {
                return (members, Evidence.Type);
            }
        }
        // An extension method of a namespace the file can see.
        var extensions = _byName.GetValueOrDefault(name, []).Where(r => r.File.Lang == "csharp" && r.Symbol.Kind == "method" && r.Symbol.Signature.Contains("(this ", StringComparison.Ordinal)).ToList();
        if (extensions.Count == 0)
        {
            return ([], null);
        }
        var visible = Prefixes(at).Concat(usings).ToHashSet(StringComparer.Ordinal);
        extensions = [.. extensions.Where(r => r.Symbol.Parent is { } parent && parent.LastIndexOf('.') is var dot and > 0 && visible.Contains(parent[..dot]))];
        return extensions.Count > 0 ? (extensions, Evidence.Scope) : ([], null);
    }

    /// <summary>The type a variable, field, property or parameter is declared with in the file: name = new Type(, Type name, name: Type.</summary>
    private static string? DeclaredType(FileNow now, string variable) => now.Types.GetValueOrDefault(variable);

    /// <summary>Every name's declared type in a file, in one pass: what it is made with (new Type, Type()) before what it is declared as.</summary>
    internal static Dictionary<string, string> TypesIn(string code, string? family)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var (made, declared) = family switch
        {
            "csharp" => (CSharpMade(), CSharpDeclared()),
            "script" => (ScriptMade(), ScriptDeclared()),
            _ => (PythonMade(), PythonDeclared()),
        };
        foreach (Match m in made.Matches(code))
        {
            types.TryAdd(m.Groups["name"].Value, m.Groups["type"].Value);
        }
        foreach (Match m in declared.Matches(code))
        {
            types.TryAdd(m.Groups["name"].Value, m.Groups["type"].Value);
        }
        return types;
    }

    [GeneratedRegex(@"\b(?<name>[A-Za-z_]\w*)\s*=\s*new\s+(?<type>[A-Z][\w.]*)")]
    private static partial Regex CSharpMade();

    [GeneratedRegex(@"\b(?<type>[A-Z][\w.]*)(?:<[^<>;(){}]*>)?\??\s+(?<name>[A-Za-z_]\w*)\s*[;=,){]")]
    private static partial Regex CSharpDeclared();

    [GeneratedRegex(@"\b(?<name>[A-Za-z_$][\w$]*)\s*=\s*new\s+(?<type>[A-Z][\w$]*)")]
    private static partial Regex ScriptMade();

    [GeneratedRegex(@"\b(?<name>[A-Za-z_$][\w$]*)\s*[?!]?:\s*(?<type>[A-Z][\w$]*)")]
    private static partial Regex ScriptDeclared();

    [GeneratedRegex(@"\b(?<name>[A-Za-z_]\w*)\s*=\s*(?<type>[A-Z]\w*)\s*\(")]
    private static partial Regex PythonMade();

    [GeneratedRegex(@"\b(?<name>[A-Za-z_]\w*)\s*:\s*(?<type>[A-Z]\w*)")]
    private static partial Regex PythonDeclared();

    // ------------------------------------------------------------------------------------------------- TypeScript and JavaScript

    /// <summary>What a name names in a file: its own declarations from the enclosing symbol outwards, else what an import binds it to.</summary>
    private List<SymbolRef> ScriptName(FileNow now, CodeSymbol? from, string name)
    {
        foreach (var p in Prefixes(from?.Qualified))
        {
            if (Named($"{p}.{name}", "script", now.File) is { Count: > 0 } inner)
            {
                return inner;
            }
        }
        if (Named(name, "script", now.File) is { Count: > 0 } own)
        {
            return own;
        }
        if (now.Bindings.TryGetValue(name, out var b) && b.Name != "*" && ScriptTarget(now.File, b.Spec) is { } target)
        {
            return Exported(target, b.Name, 0);
        }
        return [];
    }

    /// <summary>What a file exports by a name: its own top-level declaration, else one it re-exports (up to three files on).</summary>
    private List<SymbolRef> Exported(IndexedFile file, string name, int hops)
    {
        if (name == "default")
        {
            return [.. file.Symbols.Where(s => s.Symbol.Signature.Contains("export default", StringComparison.Ordinal)).Take(1).Select(s => new SymbolRef(file, s.Symbol))];
        }
        if (Named(name, "script", file) is { Count: > 0 } own)
        {
            return own;
        }
        if (hops >= MostHops)
        {
            return [];
        }
        foreach (var spec in file.Imports)
        {
            if (ScriptTarget(file, spec) is { } next && next != file && Exported(next, name, hops + 1) is { Count: > 0 } found)
            {
                return found;
            }
        }
        return [];
    }

    private (List<SymbolRef>, Evidence?) Script(FileNow now, CodeSymbol? from, CodeUse use)
    {
        var name = use.Name;
        if (use.Receiver is null)
        {
            return ScriptName(now, from, name) is { Count: > 0 } found ? (found, Evidence.Scope) : ([], null);
        }
        if (use.Receiver == "this")
        {
            var type = Prefixes(from?.Qualified).SelectMany(p => Named(p, "script", now.File)).FirstOrDefault(IsType);
            return type is not null && Members(type, name) is { Count: > 0 } own ? (own, Evidence.Scope) : ([], null);
        }
        if (now.Bindings.TryGetValue(use.Receiver, out var b) && b.Name == "*")
        {
            return ScriptTarget(now.File, b.Spec) is { } module && Exported(module, name, 0) is { Count: > 0 } exported ? (exported, Evidence.Scope) : ([], null);
        }
        if (ScriptName(now, from, use.Receiver) is { Count: > 0 } receivers)
        {
            if (receivers.SelectMany(r => Members(r, name)).ToList() is { Count: > 0 } members)
            {
                return (members, Evidence.Scope);
            }
        }
        else if (DeclaredType(now, use.Receiver) is { } declared && ScriptName(now, from, declared).SelectMany(t => Members(t, name)).ToList() is { Count: > 0 } typed)
        {
            return (typed, Evidence.Type);
        }
        return ([], null);
    }

    /// <summary>The file an import specifier names: relative, or through tsconfig's paths (with extends and references); null for a package.</summary>
    public IndexedFile? ScriptTarget(IndexedFile from, string spec)
    {
        if (spec.StartsWith('.'))
        {
            return Join(Dir(from.Path), spec) is { } path ? ScriptFile(path) : null;
        }
        foreach (var (pattern, targets) in PathsFor(Dir(from.Path)))
        {
            var star = pattern.EndsWith('*');
            var prefix = star ? pattern[..^1] : pattern;
            if (star ? !spec.StartsWith(prefix, StringComparison.Ordinal) : spec != pattern)
            {
                continue;
            }
            var rest = star ? spec[prefix.Length..] : "";
            foreach (var t in targets)
            {
                if (ScriptFile(t.Replace("*", rest, StringComparison.Ordinal)) is { } file)
                {
                    return file;
                }
            }
        }
        return null;
    }

    private IndexedFile? ScriptFile(string path)
    {
        if (ScriptEnds.Any(e => path.EndsWith(e, StringComparison.Ordinal)) && _files.TryGetValue(path, out var exact))
        {
            return exact;
        }
        // ESM's ./x.js names the x.ts it is built from.
        var stem = Regex.Replace(path, @"\.(m|c)?jsx?$", "");
        foreach (var candidate in ScriptEnds.Select(e => stem + e).Concat(ScriptEnds.Select(e => $"{stem}/index{e}")))
        {
            if (_files.TryGetValue(candidate, out var file))
            {
                return file;
            }
        }
        return null;
    }

    /// <summary>The paths of the tsconfig nearest a folder (walking up), with its extends' and references' after its own.</summary>
    private List<(string Pattern, List<string> Targets)> PathsFor(string dir) => _paths.GetOrAdd(dir, d =>
    {
        for (var at = d; ; at = Dir(at))
        {
            var config = at.Length > 0 ? $"{at}/tsconfig.json" : "tsconfig.json";
            if (File.Exists(_index.FullOf(config)))
            {
                return Config(config, 0);
            }
            if (at.Length == 0)
            {
                return [];
            }
        }
    });

    private List<(string Pattern, List<string> Targets)> Config(string path, int depth)
    {
        var paths = new List<(string, List<string>)>();
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(_index.FullOf(path)), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return paths;
        }
        if (depth > 5 || json is not JsonObject config)
        {
            return paths;
        }
        var dir = Dir(path);
        var options = config["compilerOptions"] as JsonObject;
        var baseUrl = options?["baseUrl"] is JsonValue b && b.TryGetValue<string>(out var url) ? Join(dir, url) : null;
        if (options?["paths"] is JsonObject map)
        {
            foreach (var (pattern, value) in map)
            {
                var targets = (value as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? Join(baseUrl ?? dir, s) : null).OfType<string>().ToList();
                paths.Add((pattern, targets));
            }
        }
        var extends = config["extends"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var one) => [one],
            JsonArray many => many.Select(m => m is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList(),
            _ => new List<string>(),
        };
        foreach (var e in extends.Where(e => e.StartsWith('.')))
        {
            if (Join(dir, e.EndsWith(".json", StringComparison.Ordinal) ? e : e + ".json") is { } parent)
            {
                paths.AddRange(Config(parent, depth + 1));
            }
        }
        foreach (var r in config["references"] as JsonArray ?? [])
        {
            if (r?["path"] is JsonValue v && v.TryGetValue<string>(out var reference) && Join(dir, reference) is { } target)
            {
                paths.AddRange(Config(target.EndsWith(".json", StringComparison.Ordinal) ? target : $"{target}/tsconfig.json", depth + 1));
            }
        }
        return paths;
    }

    private static string Dir(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    /// <summary>A relative path joined to a folder, ./ and ../ worked out; null when it climbs out of the folder.</summary>
    private static string? Join(string dir, string relative)
    {
        var parts = dir.Length == 0 ? new List<string>() : [.. dir.Split('/')];
        foreach (var p in relative.Split('/'))
        {
            if (p is "" or ".")
            {
                continue;
            }
            if (p == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(p);
            }
        }
        return string.Join('/', parts);
    }

    // ------------------------------------------------------------------------------------------------- Python

    /// <summary>The file of a module an import names: relative to the importing file's package, else by its dotted name, nearest first.</summary>
    private IndexedFile? PythonModule(IndexedFile from, string spec)
    {
        if (spec.StartsWith('.'))
        {
            var dots = spec.TakeWhile(c => c == '.').Count();
            var package = Dir(from.Path);
            for (var i = 1; i < dots; i++)
            {
                package = Dir(package);
            }
            var rest = spec[dots..].Replace('.', '/');
            var path = rest.Length > 0 ? (package.Length > 0 ? $"{package}/{rest}" : rest) : package;
            return _files.GetValueOrDefault(path + ".py") ?? _files.GetValueOrDefault(path + ".pyi")
                ?? _files.GetValueOrDefault(path.Length > 0 ? $"{path}/__init__.py" : "__init__.py");
        }
        return _modules.TryGetValue(spec, out var files) ? files.OrderByDescending(f => Common(f.Path, from.Path)).ThenBy(f => f.Path.Length).First() : null;
    }

    /// <summary>A module a local name stands for: import m, import m as x, from p import m.</summary>
    private IndexedFile? PythonModuleOf(FileNow now, string local)
    {
        if (!now.Bindings.TryGetValue(local, out var b))
        {
            return null;
        }
        if (b.Name == "*")
        {
            return PythonModule(now.File, b.Spec);
        }
        // from p import m: a module when p declares no m of its own.
        if (PythonModule(now.File, b.Spec) is { } package && Named(b.Name, "python", package).Count > 0)
        {
            return null;
        }
        return PythonModule(now.File, b.Spec.All(c => c == '.') ? b.Spec + b.Name : $"{b.Spec}.{b.Name}");
    }

    /// <summary>What a name names in a file: its own declarations from the enclosing symbol outwards, else what from … import binds it to.</summary>
    private List<SymbolRef> PythonName(FileNow now, CodeSymbol? from, string name)
    {
        foreach (var p in Prefixes(from?.Qualified))
        {
            if (Named($"{p}.{name}", "python", now.File) is { Count: > 0 } inner)
            {
                return inner;
            }
        }
        if (Named(name, "python", now.File) is { Count: > 0 } own)
        {
            return own;
        }
        if (now.Bindings.TryGetValue(name, out var b) && b.Name != "*" && PythonModule(now.File, b.Spec) is { } module)
        {
            return Named(b.Name, "python", module);
        }
        return [];
    }

    private (List<SymbolRef>, Evidence?) Python(FileNow now, CodeSymbol? from, CodeUse use)
    {
        var name = use.Name;
        if (use.Receiver is null)
        {
            return PythonName(now, from, name) is { Count: > 0 } found ? (found, Evidence.Scope) : ([], null);
        }
        if (use.Receiver is "self" or "cls")
        {
            var type = Prefixes(from?.Qualified).SelectMany(p => Named(p, "python", now.File)).FirstOrDefault(IsType);
            return type is not null && Members(type, name) is { Count: > 0 } own ? (own, Evidence.Scope) : ([], null);
        }
        if (PythonModuleOf(now, use.Receiver) is { } module)
        {
            return Named(name, "python", module) is { Count: > 0 } top ? (top, Evidence.Scope) : ([], null);
        }
        if (PythonName(now, from, use.Receiver) is { Count: > 0 } receivers)
        {
            if (receivers.SelectMany(r => Members(r, name)).ToList() is { Count: > 0 } members)
            {
                return (members, Evidence.Scope);
            }
        }
        else if (DeclaredType(now, use.Receiver) is { } declared && PythonName(now, from, declared).SelectMany(t => Members(t, name)).ToList() is { Count: > 0 } typed)
        {
            return (typed, Evidence.Type);
        }
        return ([], null);
    }
}
