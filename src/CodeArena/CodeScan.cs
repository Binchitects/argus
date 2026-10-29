using System.Security.Cryptography;
using System.Text;

namespace CodeArena;

/// <summary>
/// A symbol a file declares: what kind, its name and its name with what holds it (Namespace.Type.Member), its first line
/// (one line of it), where it starts and ends (attributes and decorators included), what holds it, its doc's first line,
/// and a hash of its code that changes only when its code does (not its comments or spacing).
/// </summary>
internal sealed record CodeSymbol(string Kind, string Name, string Qualified, string Signature, int Start, int End, string? Parent, string? Doc, string Hash);

/// <summary>A name used in code (not in a comment or string), on a line; a call when it is followed by (, and the name before its dot.</summary>
internal readonly record struct CodeUse(string Name, int Line, bool Call, string? Receiver);

/// <summary>What a scan found: the symbols, what the file imports (namespaces, modules, paths), and the names it uses.</summary>
internal sealed record ScannedFile(List<CodeSymbol> Symbols, List<string> Imports, List<CodeUse> Uses, bool Approximate = false);

/// <summary>A language family's reader: what a file declares, imports and uses, from its text.</summary>
internal interface ICodeScanner
{
    /// <summary>The scanner's version: a new one reads every file of its languages again.</summary>
    int Version { get; }

    ScannedFile Scan(string text);
}

/// <summary>A word or a mark of code, where it is.</summary>
internal readonly record struct CodeToken(string Text, int Line, int Offset, bool Word)
{
    public bool Is(string s) => Text == s;
}

/// <summary>Code cut into words and marks, on its comments-and-strings-blanked text, with the lines they are on (1 first).</summary>
internal static class CodeTokens
{
    private static readonly string[] Pairs = ["=>", "?.", "::", "??", "->", "<=", ">=", "==", "!="];

    public static List<CodeToken> Of(string blanked, bool hashComments = false)
    {
        var tokens = new List<CodeToken>(blanked.Length / 4);
        var line = 1;
        var lineStart = true;
        for (var i = 0; i < blanked.Length; i++)
        {
            var c = blanked[i];
            if (c == '\n')
            {
                line++;
                lineStart = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            // A preprocessor line (#if, #region, #nullable) is not code: both of an #if's branches are read.
            if (c == '#' && lineStart && !hashComments)
            {
                while (i + 1 < blanked.Length && blanked[i + 1] != '\n')
                {
                    i++;
                }
                continue;
            }
            lineStart = false;
            if (char.IsLetter(c) || c == '_' || c == '@' || c == '$')
            {
                var start = i;
                while (i + 1 < blanked.Length && (char.IsLetterOrDigit(blanked[i + 1]) || blanked[i + 1] == '_' || blanked[i + 1] == '$'))
                {
                    i++;
                }
                var word = blanked[start..(i + 1)].TrimStart('@');
                if (word.Length > 0 && word != "$")
                {
                    tokens.Add(new CodeToken(word, line, start, true));
                }
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i + 1 < blanked.Length && (char.IsLetterOrDigit(blanked[i + 1]) || blanked[i + 1] is '.' or '_'))
                {
                    i++;
                }
                continue;
            }
            if (i + 1 < blanked.Length && Pairs.Contains(blanked.Substring(i, 2)))
            {
                tokens.Add(new CodeToken(blanked.Substring(i, 2), line, i, false));
                i++;
                continue;
            }
            tokens.Add(new CodeToken(c.ToString(), line, i, false));
        }
        return tokens;
    }

    /// <summary>A symbol's code hashed: its blanked text with spacing collapsed, so only a change to its code changes it.</summary>
    public static string Hash(string blanked, int offsetFrom, int offsetTo)
    {
        var b = new StringBuilder(Math.Max(0, offsetTo - offsetFrom));
        var space = false;
        for (var i = Math.Max(0, offsetFrom); i < Math.Min(blanked.Length, offsetTo); i++)
        {
            if (char.IsWhiteSpace(blanked[i]))
            {
                space = true;
                continue;
            }
            if (space && b.Length > 0)
            {
                b.Append(' ');
            }
            space = false;
            b.Append(blanked[i]);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString()))[..8]);
    }

    /// <summary>One line of a declaration, as written: from its start to its body, spacing collapsed, at most 200 characters.</summary>
    public static string Signature(string text, int from, int to)
    {
        var s = string.Join(' ', text[Math.Max(0, from)..Math.Min(text.Length, Math.Max(from, to))].Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 200 ? s[..199] + "…" : s;
    }

    /// <summary>The first line of the doc comment just above a line (/// or // lines, or a block's first), from the original text.</summary>
    public static string? DocAbove(string[] lines, int line, string prefix = "//")
    {
        var i = line - 2;
        string? first = null;
        while (i >= 0)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                i--;
                continue;
            }
            if (!t.StartsWith(prefix, StringComparison.Ordinal))
            {
                break;
            }
            var words = t.TrimStart('/', '#', ' ').Replace("<summary>", "", StringComparison.Ordinal).Replace("</summary>", "", StringComparison.Ordinal).Trim();
            if (words.Length > 0)
            {
                first = words;
            }
            i--;
        }
        return first is null ? null : first.Length > 200 ? first[..199] + "…" : first;
    }
}

/// <summary>
/// C#: namespaces (file-scoped and block), types (class, struct, interface, enum, record, with primary constructors), and
/// their members (methods, constructors, properties, indexers, events, operators, fields, constants, delegates), local
/// functions; the usings; and every name used in code. A symbol runs from its first attribute to its closing brace or semicolon.
/// </summary>
internal sealed class CSharpScanner : ICodeScanner
{
    public int Version => 1;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
        "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "file", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "partial", "private", "protected", "public", "readonly", "record", "ref", "required", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while", "yield", "nint", "nuint", "scoped", "global", "with", "and", "or", "not",
    };

    private static readonly string[] TypeWords = ["class", "struct", "interface", "enum", "record"];

    private sealed class Container(string kind, string? name, string? qualified, int depth, int startLine, int startOffset, bool members)
    {
        public string Kind { get; } = kind;
        public string? Name { get; } = name;
        public string? Qualified { get; } = qualified;
        public int Depth { get; } = depth;
        public int StartLine { get; } = startLine;
        public int StartOffset { get; } = startOffset;
        /// <summary>Its body declares members (a namespace or a type), not statements (a method).</summary>
        public bool Members { get; } = members;
        public int Index { get; set; } = -1;
        public string Signature { get; set; } = "";
    }

    public ScannedFile Scan(string text)
    {
        var blanked = Argus.Util.CodeText.Blank("csharp", text);
        var tokens = CodeTokens.Of(blanked);
        var lines = text.Split('\n');
        var symbols = new List<CodeSymbol>();
        var imports = new List<string>();
        var uses = new List<CodeUse>();
        var stack = new List<Container>();
        string? fileNamespace = null;
        var depth = 0;
        var parens = 0;
        // Blocks inside an expression (an initializer, a lambda's body, a switch expression): part of the declaration they are in.
        var inline = 0;
        var head = new List<CodeToken>();

        string Prefix() => string.Join('.', new[] { fileNamespace }.Concat(stack.Where(c => c.Kind is "namespace" or "type").Select(c => c.Name)).Where(n => n is { Length: > 0 }));

        Container? InnermostNamed() => stack.LastOrDefault(c => c.Kind != "block");

        void Add(string kind, string name, CodeToken first, int endLine, int endOffset, int signatureEnd, Container? into = null)
        {
            var holder = InnermostNamed();
            var prefix = Prefix();
            var qualified = prefix.Length > 0 ? $"{prefix}.{name}" : name;
            var parent = holder?.Qualified ?? (fileNamespace is { Length: > 0 } ns ? ns : null);
            var symbol = new CodeSymbol(kind, name, qualified, CodeTokens.Signature(text, first.Offset, signatureEnd), first.Line, endLine, parent,
                CodeTokens.DocAbove(lines, first.Line), CodeTokens.Hash(blanked, first.Offset, endOffset));
            if (into is not null)
            {
                into.Index = symbols.Count;
            }
            symbols.Add(symbol);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Word)
            {
                if (!Keywords.Contains(t.Text))
                {
                    var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
                    var call = next.Is("(") || (next.Is("<") && GenericCall(tokens, i + 1));
                    var receiver = i >= 2 && (tokens[i - 1].Is(".") || tokens[i - 1].Is("?.")) && tokens[i - 2].Word ? tokens[i - 2].Text : null;
                    uses.Add(new CodeUse(t.Text, t.Line, call, receiver));
                }
            }
            if (inline > 0)
            {
                inline += t.Is("{") ? 1 : t.Is("}") ? -1 : 0;
                parens += t.Is("(") ? 1 : t.Is(")") && parens > 0 ? -1 : 0;
                head.Add(t);
                continue;
            }
            if (t.Is("("))
            {
                parens++;
                head.Add(t);
                continue;
            }
            if (t.Is(")"))
            {
                parens = Math.Max(0, parens - 1);
                head.Add(t);
                continue;
            }
            if (parens > 0 && !t.Is("{") && !t.Is("}"))
            {
                head.Add(t);
                continue;
            }
            if (t.Is("{"))
            {
                if (parens > 0 || Assigns(head))
                {
                    inline = 1;
                    head.Add(t);
                    continue;
                }
                var container = Open(head, t);
                stack.Add(container);
                depth++;
                head.Clear();
                continue;
            }
            if (t.Is("}"))
            {
                if (stack.Count > 0)
                {
                    var c = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (c.Index >= 0)
                    {
                        var s = symbols[c.Index];
                        symbols[c.Index] = s with { End = t.Line, Hash = CodeTokens.Hash(blanked, c.StartOffset, t.Offset + 1) };
                    }
                }
                depth = Math.Max(0, depth - 1);
                head.Clear();
                continue;
            }
            if (t.Is(";"))
            {
                Statement(head, t);
                head.Clear();
                continue;
            }
            head.Add(t);
        }
        return new ScannedFile(Dedupe(symbols), imports, uses);

        // A declaration that opens a block: a namespace, a type, a member with a body, a local function; else a plain block.
        Container Open(List<CodeToken> h, CodeToken brace)
        {
            var at = Strip(h);
            var holder = stack.LastOrDefault();
            var inMembers = holder is null || holder.Members;
            if (at.Count == 0)
            {
                return new Container("block", null, null, depth, brace.Line, brace.Offset, false);
            }
            var first = at[0];
            var startTok = h.Count > 0 ? h[0] : first;
            var ns = at.FindIndex(x => x.Is("namespace"));
            if (ns >= 0 && inMembers)
            {
                var name = Dotted(at, ns + 1);
                var c = new Container("namespace", name, Join(Prefix(), name), depth, startTok.Line, startTok.Offset, true);
                Add("namespace", name, startTok, brace.Line, brace.Offset, brace.Offset, c);
                return c;
            }
            var typeAt = TypeKeyword(at);
            if (typeAt >= 0 && inMembers)
            {
                var kind = at[typeAt].Text == "record" && typeAt + 1 < at.Count && at[typeAt + 1].Text is "struct" or "class" ? "record" : at[typeAt].Text;
                var nameAt = at[typeAt].Text == "record" && typeAt + 1 < at.Count && at[typeAt + 1].Text is "struct" or "class" ? typeAt + 2 : typeAt + 1;
                if (nameAt < at.Count && at[nameAt].Word)
                {
                    var name = at[nameAt].Text;
                    var c = new Container("type", name, Join(Prefix(), name), depth, startTok.Line, startTok.Offset, kind != "enum");
                    Add(kind, name, startTok, brace.Line, brace.Offset, brace.Offset, c);
                    return c;
                }
            }
            if (inMembers && holder is { Kind: "type" })
            {
                // A member with a body: a method or constructor (a parameter list after its name), an indexer, an operator, a property, an event.
                if (Callable(at) is { } member)
                {
                    var c = new Container("member", member.Name, Join(Prefix(), member.Name), depth, startTok.Line, startTok.Offset, false);
                    Add(member.Kind, member.Name, startTok, brace.Line, brace.Offset, brace.Offset, c);
                    return c;
                }
                if (!Assigns(at) && at[^1].Word && !Keywords.Contains(at[^1].Text))
                {
                    var kind = at.Any(x => x.Is("event")) ? "event" : "property";
                    var c = new Container("member", at[^1].Text, Join(Prefix(), at[^1].Text), depth, startTok.Line, startTok.Offset, false);
                    Add(kind, at[^1].Text, startTok, brace.Line, brace.Offset, brace.Offset, c);
                    return c;
                }
            }
            else if (!inMembers && Callable(at) is { Kind: "method" } local && !Assigns(at) && !Statements.Contains(first.Text))
            {
                var c = new Container("member", local.Name, Join(Prefix(), local.Name), depth, startTok.Line, startTok.Offset, false);
                Add("function", local.Name, startTok, brace.Line, brace.Offset, brace.Offset, c);
                return c;
            }
            return new Container("block", null, null, depth, brace.Line, brace.Offset, false);
        }

        // A declaration that ends at a semicolon: a using, a file-scoped namespace, a field, a property or method with =>, an interface's
        // or abstract method, an event field, a delegate, a record with only its primary constructor.
        void Statement(List<CodeToken> h, CodeToken semi)
        {
            var at = Strip(h);
            if (at.Count == 0)
            {
                return;
            }
            var holder = stack.LastOrDefault();
            var inMembers = holder is null || holder.Members;
            var startTok = h[0];
            if (at[0].Is("using") || (at[0].Is("global") && at.Count > 1 && at[1].Is("using")))
            {
                if (inMembers && holder is null or { Kind: "namespace" })
                {
                    var u = at.FindIndex(x => x.Is("using"));
                    var eq = at.FindIndex(x => x.Is("="));
                    var from = eq > u ? eq + 1 : u + 1 < at.Count && at[u + 1].Is("static") ? u + 2 : u + 1;
                    if (Dotted(at, from) is { Length: > 0 } imported)
                    {
                        imports.Add(imported);
                    }
                }
                return;
            }
            if (!inMembers)
            {
                // A local function with an expression body: int Twice(int x) => x * 2;
                var arrowAt = at.FindIndex(x => x.Is("=>"));
                if (arrowAt > 0 && at[arrowAt - 1].Is(")") && !Statements.Contains(at[0].Text) && !Assigns(at[..arrowAt]) && Callable(at[..arrowAt]) is { Kind: "method" } f)
                {
                    Add("function", f.Name, startTok, semi.Line, semi.Offset + 1, at[arrowAt].Offset);
                }
                return;
            }
            if (at.FindIndex(x => x.Is("namespace")) is var nsAt and >= 0 && holder is null)
            {
                fileNamespace = Dotted(at, nsAt + 1);
                symbols.Add(new CodeSymbol("namespace", fileNamespace, fileNamespace, CodeTokens.Signature(text, startTok.Offset, semi.Offset + 1), startTok.Line, lines.Length,
                    null, null, CodeTokens.Hash(blanked, startTok.Offset, semi.Offset + 1)));
                return;
            }
            var typeAt = TypeKeyword(at);
            if (typeAt >= 0)
            {
                // A record (or class) with only its primary constructor: record Point(int X, int Y);
                var nameAt = at[typeAt].Text == "record" && typeAt + 1 < at.Count && at[typeAt + 1].Text is "struct" or "class" ? typeAt + 2 : typeAt + 1;
                if (nameAt < at.Count && at[nameAt].Word)
                {
                    Add(at[typeAt].Text == "record" ? "record" : at[typeAt].Text, at[nameAt].Text, startTok, semi.Line, semi.Offset + 1, semi.Offset);
                }
                return;
            }
            if (holder is not { Kind: "type" })
            {
                return;
            }
            if (at.Any(x => x.Is("delegate")) && Callable(at) is { } d)
            {
                Add("delegate", d.Name, startTok, semi.Line, semi.Offset + 1, semi.Offset);
                return;
            }
            var arrow = at.FindIndex(x => x.Is("=>"));
            var assign = at.FindIndex(x => x.Is("="));
            var before = arrow >= 0 && (assign < 0 || arrow < assign) ? at[..arrow] : assign >= 0 ? at[..assign] : at;
            var sigEnd = arrow >= 0 && (assign < 0 || arrow < assign) ? at[arrow].Offset : assign >= 0 ? at[assign].Offset : semi.Offset;
            if (Callable(before) is { } m)
            {
                Add(m.Kind, m.Name, startTok, semi.Line, semi.Offset + 1, sigEnd);
                return;
            }
            var last = before.LastOrDefault(x => x.Word && !Keywords.Contains(x.Text));
            if (last.Text is null)
            {
                return;
            }
            var kind = at.Any(x => x.Is("event")) ? "event" : at.Any(x => x.Is("const")) ? "const" : arrow >= 0 && (assign < 0 || arrow < assign) ? "property" : "field";
            // int a, b; declares both: the first is enough to find them.
            var name = before.Any(x => x.Is(",")) ? before.TakeWhile(x => !x.Is(",")).Last(x => x.Word).Text : last.Text;
            Add(kind, name, startTok, semi.Line, semi.Offset + 1, sigEnd);
        }
    }

    private static readonly HashSet<string> Statements = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "using", "lock", "catch", "fixed", "else", "do", "try", "finally", "checked", "unchecked", "unsafe", "return", "await", "yield",
    };

    private static string Join(string prefix, string name) => prefix.Length > 0 ? $"{prefix}.{name}" : name;

    /// <summary>Whether a head assigns (=, or =>) outside its parentheses: an expression follows, not a declaration's body.</summary>
    private static bool Assigns(List<CodeToken> head)
    {
        var parens = 0;
        foreach (var t in head)
        {
            parens += t.Is("(") || t.Is("[") ? 1 : t.Is(")") || t.Is("]") ? -1 : 0;
            if (parens == 0 && (t.Is("=") || t.Is("=>")))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The head without its attributes ([Fact], [property: X]) at the start, and the generic constraints at its end.</summary>
    private static List<CodeToken> Strip(List<CodeToken> head)
    {
        var at = new List<CodeToken>(head);
        while (at.Count > 0 && at[0].Is("["))
        {
            var depth = 0;
            var i = 0;
            for (; i < at.Count; i++)
            {
                depth += at[i].Is("[") ? 1 : at[i].Is("]") ? -1 : 0;
                if (depth == 0)
                {
                    break;
                }
            }
            at.RemoveRange(0, Math.Min(at.Count, i + 1));
        }
        return at;
    }

    private static int TypeKeyword(List<CodeToken> at)
    {
        for (var i = 0; i < at.Count; i++)
        {
            if (at[i].Is("(") || at[i].Is("=") || at[i].Is("=>"))
            {
                return -1;
            }
            // Not a constraint (where T : class, struct) nor a member's name (x.Class).
            if (at[i].Word && TypeWords.Contains(at[i].Text) && (i == 0 || !(at[i - 1].Is(".") || at[i - 1].Is(":") || at[i - 1].Is(","))))
            {
                return i;
            }
        }
        return -1;
    }

    private static string Dotted(List<CodeToken> at, int from)
    {
        var b = new StringBuilder();
        for (var i = from; i < at.Count; i++)
        {
            if (at[i].Word && at[i].Text != "global")
            {
                b.Append(at[i].Text);
            }
            else if (at[i].Is(".") || at[i].Is("::"))
            {
                b.Append('.');
            }
            else
            {
                break;
            }
        }
        return b.ToString().Trim('.');
    }

    /// <summary>
    /// A method's, constructor's, operator's or indexer's head: the first parameter list after a name that is not a keyword
    /// (generic arguments skipped), so a tuple return type and a base constructor call are not taken for it.
    /// </summary>
    private static (string Kind, string Name)? Callable(List<CodeToken> at)
    {
        for (var i = 0; i < at.Count; i++)
        {
            if (at[i].Is("operator"))
            {
                var name = i + 1 < at.Count ? "operator " + at[i + 1].Text : "operator";
                return ("operator", name);
            }
            if (at[i].Is("this") && i + 1 < at.Count && at[i + 1].Is("["))
            {
                return ("indexer", "this[]");
            }
            if (!at[i].Is("("))
            {
                continue;
            }
            var j = i - 1;
            if (j >= 0 && at[j].Is(">"))
            {
                // M<T>( : back over the generic arguments.
                var depth = 0;
                for (; j >= 0; j--)
                {
                    depth += at[j].Is(">") ? 1 : at[j].Is("<") ? -1 : 0;
                    if (depth == 0)
                    {
                        break;
                    }
                }
                j--;
            }
            if (j >= 0 && at[j].Word && !Keywords.Contains(at[j].Text) && !(j > 0 && (at[j - 1].Is("new") || at[j - 1].Is(".") || at[j - 1].Is("?."))))
            {
                return ("method", at[j].Text);
            }
            // A parameter list of a keyword (if, base, this…) or a type's: none here; the next one may be.
            var close = 1;
            for (i++; i < at.Count && close > 0; i++)
            {
                close += at[i].Is("(") ? 1 : at[i].Is(")") ? -1 : 0;
            }
            i--;
            if (i + 1 < at.Count && at[i + 1].Is(":"))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Whether a &lt; at <paramref name="at"/> opens generic arguments followed by a call: Make&lt;T&gt;(…).</summary>
    internal static bool GenericCall(List<CodeToken> tokens, int at)
    {
        var depth = 0;
        for (var i = at; i < tokens.Count && i < at + 40; i++)
        {
            var t = tokens[i];
            if (t.Is("<"))
            {
                depth++;
            }
            else if (t.Is(">"))
            {
                if (--depth == 0)
                {
                    return i + 1 < tokens.Count && tokens[i + 1].Is("(");
                }
            }
            else if (!(t.Word || t.Is(",") || t.Is(".") || t.Is("?") || t.Is("[") || t.Is("]")))
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Both branches of an #if may declare the same member: once is enough.</summary>
    private static List<CodeSymbol> Dedupe(List<CodeSymbol> symbols) =>
        [.. symbols.GroupBy(s => (s.Kind, s.Qualified, s.Signature)).Select(g => g.First()).OrderBy(s => s.Start).ThenBy(s => -s.End)];
}
