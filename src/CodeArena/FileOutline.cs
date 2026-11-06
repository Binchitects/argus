using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// A large file as the model first sees it (read_file without a range, a file named with @): how long it is, its symbols
/// with their lines, and its first lines; it then reads the part it needs, by lines or by a symbol's name. The index's
/// scanners read C#, TypeScript, JavaScript, Python and Markdown; other code by the lines that look like declarations.
/// </summary>
internal static partial class FileOutline
{
    /// <summary>A file past either is large: it goes as its outline, not whole.</summary>
    public const int LargeLines = 500, LargeChars = 24_000;

    /// <summary>The most an outline takes, and the first lines it shows.</summary>
    public const int OutlineChars = 6_000, HeadLines = 30;

    /// <summary>Text past this size is not scanned for symbols (its first lines and its length only).</summary>
    private const int MostScanned = 8_000_000;

    /// <summary>A file past this is not read whole even for its outline: its first lines and how many it has, read as it streams.</summary>
    public const long HugeBytes = 16_000_000;

    public static bool Large(string text, int lines) => lines > LargeLines || text.Length > LargeChars;

    /// <summary>A file too big to hold (a log, a dump): its size, its line count and its first lines, read a line at a time.</summary>
    public static async Task<string> HugeAsync(string path, string show, long size, string readOn, CancellationToken ct)
    {
        var head = new StringBuilder();
        var count = 0;
        using (var reader = new StreamReader(Files.OpenShared(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (++count <= HeadLines)
                {
                    head.Append(count.ToString().PadLeft(6)).Append('\t').Append(line.Length > 300 ? line[..300] + " … (line cut)" : line).Append('\n');
                }
            }
        }
        return $"{show}: {count:N0} lines, {Files.Size(size)}; too big to take whole. {readOn}\n\nIts first {Math.Min(HeadLines, count)} lines:\n{head}";
    }

    /// <summary>A text's symbols: the index's scanner's, else the lines that look like declarations, each to the line before the next.</summary>
    public static List<CodeSymbol> Symbols(string path, string text)
    {
        if (text.Length > MostScanned)
        {
            return [];
        }
        var lang = CodeFiles.LanguageOf(path);
        if (text.Length <= CodeFiles.MostSymbolBytes && CodeIndex.Outline(lang, text) is { Count: > 0 } scanned)
        {
            return [.. scanned.Where(s => s.Kind is not "namespace" || s.End - s.Start < 50).OrderBy(s => s.Start).ThenByDescending(s => s.End)];
        }
        return lang == "markdown" ? [] : Declarations(text);
    }

    /// <summary>
    /// The summary of a large file: its size, its outline (nested by where each symbol is; children left out past
    /// <see cref="OutlineChars"/>), its first lines, and how to read on.
    /// </summary>
    public static string Summary(string show, string text, string[] lines, string readOn)
    {
        var symbols = Symbols(show, text);
        var sb = new StringBuilder();
        sb.Append($"{show}: {lines.Length:N0} lines, {Files.Size(Encoding.UTF8.GetByteCount(text))}; too long to take whole, so its outline and first lines. {readOn}\n");
        if (symbols.Count > 0)
        {
            var depths = Depths(symbols);
            var shown = Render(symbols, depths, int.MaxValue);
            for (var most = 3; shown.Length > OutlineChars && most > 0; most--)
            {
                shown = Render(symbols, depths, most - 1);
            }
            if (shown.Length > OutlineChars)
            {
                var cut = shown.LastIndexOf('\n', OutlineChars);
                shown = shown[..Math.Max(0, cut)] + $"\n  … more symbols: read_file with offset past line {LineOf(shown, cut)} lists the rest of the file's lines";
            }
            sb.Append($"\nOutline ({symbols.Count:N0} symbols; lines in brackets):\n").Append(shown);
        }
        else
        {
            sb.Append("\n(No symbols found in it: grep finds lines in it by text.)\n");
        }
        sb.Append($"\nIts first {Math.Min(HeadLines, lines.Length)} lines:\n");
        for (var i = 0; i < Math.Min(HeadLines, lines.Length); i++)
        {
            var line = lines[i];
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').Append(line.Length > 300 ? line[..300] + " … (line cut)" : line).Append('\n');
        }
        return sb.ToString();
    }

    private static int LineOf(string shown, int cut) =>
        cut > 0 && BracketLines().Matches(shown[..cut]) is { Count: > 0 } m ? int.Parse(m[^1].Groups[2].Value) : 1;

    /// <summary>Each symbol's depth: how many symbols before it hold its lines.</summary>
    private static int[] Depths(List<CodeSymbol> symbols)
    {
        var depths = new int[symbols.Count];
        var open = new Stack<int>();
        for (var i = 0; i < symbols.Count; i++)
        {
            while (open.Count > 0 && symbols[open.Peek()].End < symbols[i].Start)
            {
                open.Pop();
            }
            depths[i] = open.Count;
            open.Push(i);
        }
        return depths;
    }

    private static string Render(List<CodeSymbol> symbols, int[] depths, int deepest)
    {
        var sb = new StringBuilder();
        // The symbols too deep to list each on a line: by name and first line, together on one.
        var hidden = new List<string>();
        void Fold()
        {
            if (hidden.Count > 0)
            {
                sb.Append(new string(' ', 2 * (deepest + 2))).Append(Fmt.OneLine(string.Join(", ", hidden), 160)).Append('\n');
                hidden.Clear();
            }
        }
        for (var i = 0; i < symbols.Count; i++)
        {
            var s = symbols[i];
            if (depths[i] > deepest)
            {
                hidden.Add($"{s.Name} {s.Start}");
                continue;
            }
            Fold();
            var head = s.Signature is { Length: > 0 } sig ? Fmt.OneLine(sig, 110) : $"{s.Kind} {s.Name}";
            sb.Append(new string(' ', 2 * (depths[i] + 1))).Append(head).Append($"  [{s.Start}-{s.End}]\n");
        }
        Fold();
        return sb.ToString();
    }

    /// <summary>
    /// The symbol a name means: its whole qualified name, or its end after a dot (Type.Member, Member); several that
    /// match equally are all given, for the model to choose.
    /// </summary>
    public static List<CodeSymbol> Find(List<CodeSymbol> symbols, string name)
    {
        var asked = name.Trim().TrimEnd('(', ')');
        var whole = symbols.Where(s => s.Qualified == asked).ToList();
        if (whole.Count > 0)
        {
            return whole;
        }
        var end = symbols.Where(s => s.Qualified.EndsWith("." + asked, StringComparison.Ordinal) || s.Name == asked).ToList();
        return end.Count > 0 ? end : [.. symbols.Where(s => s.Qualified.EndsWith(asked, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(asked, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Lines that look like a declaration in most languages, each to the line before the next one as deep or less.</summary>
    internal static List<CodeSymbol> Declarations(string text)
    {
        var lines = text.Split('\n');
        var found = new List<(int Line, int Indent, string Name, string Text)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 400 || Declaration().Match(line) is not { Success: true } m)
            {
                continue;
            }
            found.Add((i + 1, m.Groups["indent"].Value.Replace("\t", "    ").Length, m.Groups["name"].Value, line.Trim()));
        }
        var symbols = new List<CodeSymbol>();
        for (var i = 0; i < found.Count; i++)
        {
            var end = lines.Length;
            for (var j = i + 1; j < found.Count; j++)
            {
                if (found[j].Indent <= found[i].Indent)
                {
                    end = found[j].Line - 1;
                    break;
                }
            }
            // Blank lines before the next one are not its.
            while (end > found[i].Line && lines[end - 1].Trim().Length == 0)
            {
                end--;
            }
            var (line, _, name, decl) = found[i];
            symbols.Add(new CodeSymbol("declaration", name, name, decl, line, end, null, null, ""));
        }
        return symbols;
    }

    // func, fn, def, class… with what may come before them; or a C-like function at the line's start: a type, a name, its
    // parameters, and no ; after them (a declaration only, or a call).
    [GeneratedRegex(@"^(?:(?<indent>[ \t]{0,12})(?:(?:export|public|private|protected|internal|static|async|pub(?:\([\w:]+\))?|final|abstract|virtual|override|extern|inline|unsafe|default|open|data|sealed|suspend|const)\s+)*(?:func|fn|def|class|struct|interface|enum|trait|impl|type|module|function|object|record|namespace|package|sub|proc)\s+(?:\([^)]*\)\s*)?(?<name>[A-Za-z_][\w:.]*)|(?<indent>)(?!(?:return|else|if|for|while|switch|case|do|goto)\b)[A-Za-z_][\w:<>,*& ]*[\s*&]+(?<name>[A-Za-z_~][\w:~]*)\s*\((?!.*\)\s*;\s*$))")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\[(\d+)-(\d+)\]")]
    private static partial Regex BracketLines();
}
