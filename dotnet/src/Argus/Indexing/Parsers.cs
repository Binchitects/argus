using System.Text.RegularExpressions;
using Argus.Store;
using Argus.Util;

namespace Argus.Indexing;

/// <summary>Which files are worth indexing (argus/parse/filters.py).</summary>
public static class Filters
{
    public static readonly Dictionary<string, string> ExtensionLang = new(StringComparer.Ordinal)
    {
        [".c"] = "c", [".h"] = "cpp", [".hpp"] = "cpp", [".hxx"] = "cpp", [".inl"] = "cpp",
        [".cc"] = "cpp", [".cpp"] = "cpp", [".cxx"] = "cpp",
        [".py"] = "python", [".cs"] = "csharp",
        [".ts"] = "typescript", [".tsx"] = "typescript",
        [".js"] = "javascript", [".jsx"] = "javascript",
        [".md"] = "markdown", [".txt"] = "text",
    };

    public static readonly HashSet<string> HeaderExtensions = new(StringComparer.Ordinal) { ".h", ".hpp", ".hxx", ".inl" };

    public static bool IsBinary(ReadOnlySpan<byte> data) => data[..Math.Min(data.Length, 8192)].IndexOf((byte)0) >= 0;

    /// <summary>PurePosixPath(path).suffix: the last dot-suffix of the final component, if not a dotfile.</summary>
    public static string Suffix(string path)
    {
        var name = PyStr.AfterLast(path, '/');
        int i = name.LastIndexOf('.');
        if (i <= 0 || i == name.Length - 1) return "";
        return name.Substring(i);
    }

    public static string? DetectLang(string path) =>
        ExtensionLang.TryGetValue(Suffix(path).ToLowerInvariant(), out var lang) ? lang : null;

    public static bool ShouldIndex(string path, long size, ReadOnlySpan<byte> data, long maxBytes, IReadOnlyList<string> excludeDirs)
    {
        if (size > maxBytes) return false;
        if (DetectLang(path) is null) return false;
        var excluded = new HashSet<string>(excludeDirs.Select(d => d.ToLowerInvariant()), StringComparer.Ordinal);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (excluded.Contains(parts[i].ToLowerInvariant())) return false;
        return !IsBinary(data);
    }
}

/// <summary>#include extraction (argus/parse/includes.py).</summary>
public static class Includes
{
    static readonly Regex IncludeRe = new(
        @"^[ \t]*#[ \t]*include[ \t]*(?:<(?<angle>[^>\r\n]+)>|""(?<quote>[^""\r\n]+)"")",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    static readonly Regex LineCommentRe = new(@"^[ \t]*(?://|/\*|\*)", RegexOptions.CultureInvariant);

    public static List<Writes.IncludeRow> Extract(string content)
    {
        var seen = new HashSet<(string, long)>();
        var output = new List<Writes.IncludeRow>();
        foreach (var line in PyStr.SplitLines(content))
        {
            if (LineCommentRe.IsMatch(line)) continue;
            var m = IncludeRe.Match(line);
            if (!m.Success || m.Index != 0) continue;
            bool angle = m.Groups["angle"].Success;
            var raw = angle ? m.Groups["angle"].Value : m.Groups["quote"].Value;
            var key = (raw, angle ? 1L : 0L);
            if (!seen.Add(key)) continue;
            output.Add(new Writes.IncludeRow(raw, key.Item2));
        }
        return output;
    }
}

/// <summary>
/// The doc comment attached to a symbol -- what it DOES, not what it is called
/// (argus/parse/docs.py). Walks backwards from a definition's first line over
/// the text already stored; no language server, no AST.
/// </summary>
public static class DocComments
{
    const int SearchLines = 60;
    const int MaxChars = 1200;
    const int DocstringLines = 60;

    static readonly Dictionary<string, string[]> LineMarkers = new(StringComparer.Ordinal)
    {
        ["python"] = ["#"], ["ruby"] = ["#"], ["sh"] = ["#"], ["bash"] = ["#"],
        ["zsh"] = ["#"], ["perl"] = ["#"], ["r"] = ["#"], ["make"] = ["#"],
        ["cmake"] = ["#"], ["yaml"] = ["#"], ["toml"] = ["#"], ["powershell"] = ["#"],
        ["puppet"] = ["#"], ["awk"] = ["#"], ["tcl"] = ["#"],
        ["sql"] = ["--"], ["lua"] = ["--"], ["haskell"] = ["--"], ["ada"] = ["--"], ["elm"] = ["--"],
        ["lisp"] = [";"], ["clojure"] = [";"], ["scheme"] = [";"], ["asm"] = [";"],
        ["erlang"] = ["%"], ["matlab"] = ["%"], ["tex"] = ["%"],
        ["fortran"] = ["!"],
        ["vim"] = ["\""],
    };

    static readonly string[] DefaultLine = ["//"];

    static readonly HashSet<string> BlockLangs = new(StringComparer.Ordinal)
    {
        "c", "c++", "cpp", "cxx", "cc", "h", "h++", "hpp", "java", "c#", "cs",
        "go", "rust", "javascript", "typescript", "jsx", "tsx", "swift", "kotlin",
        "scala", "php", "dart", "objective-c", "objc", "css", "scss", "less",
        "protobuf", "solidity", "verilog", "systemverilog", "v", "d", "zig",
    };

    static readonly (string Open, string Close) DefaultBlock = ("/*", "*/");
    static readonly HashSet<string> DocstringLangs = new(StringComparer.Ordinal) { "python", "ruby" };

    static readonly Regex Blank = new(@"^\s*$", RegexOptions.CultureInvariant);
    static readonly Regex Skippable = new(@"^\s*(@|\[[A-Za-z]|#\[)", RegexOptions.CultureInvariant);
    static readonly Regex Banner = new(@"copyright|\(c\)\s*\d{4}|spdx-license|licensed under|all rights reserved",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static (string[] Line, (string Open, string Close)? Block) Markers(string? lang)
    {
        var key = PyStr.Strip(lang ?? "").ToLowerInvariant();
        if (LineMarkers.TryGetValue(key, out var markers))
            return (markers, BlockLangs.Contains(key) ? DefaultBlock : null);
        return (DefaultLine, DefaultBlock);
    }

    static string? StripLine(string text, string marker)
    {
        var stripped = PyStr.LStrip(text);
        if (!stripped.StartsWith(marker, StringComparison.Ordinal)) return null;
        if (marker == "#" && stripped.StartsWith("#!", StringComparison.Ordinal)) return null;
        var body = stripped.Substring(marker.Length);
        if (body.Length > 0 && body[0] == marker[0]) body = body.Substring(1);
        return PyStr.RStrip(PyStr.LStrip(body));
    }

    static bool IsBlank(string? s) => Blank.IsMatch(s ?? "");

    /// <summary>The doc comment ending immediately above <paramref name="line"/> (1-based), or "".</summary>
    public static string LeadingComment(IReadOnlyList<string> lines, long line, string? lang = null)
    {
        if (line < 2 || line > lines.Count) return "";
        var (lineMarkers, block) = Markers(lang);

        int i = (int)line - 2;
        while (i >= 0 && Skippable.IsMatch(lines[i] ?? "")) i--;
        if (i < 0) return "";

        if (IsBlank(lines[i]))
        {
            i--;
            if (i < 0 || IsBlank(lines[i])) return "";
        }

        if (block is { } b && (lines[i] ?? "").Contains(b.Close, StringComparison.Ordinal))
        {
            int k = i;
            var parts = new List<string>();
            while (k >= 0 && k > i - SearchLines)
            {
                var body = lines[k];
                if (body.Contains(b.Close, StringComparison.Ordinal))
                    body = body.Substring(0, body.LastIndexOf(b.Close, StringComparison.Ordinal));
                if (body.Contains(b.Open, StringComparison.Ordinal))
                {
                    body = body.Substring(body.IndexOf(b.Open, StringComparison.Ordinal) + b.Open.Length);
                    parts.Add(body);
                    parts.Reverse();
                    var blockText = CleanBlock(parts);
                    if (blockText.Length > 0 && !IsBanner(blockText, k + 1)) return Clip(blockText);
                    return "";
                }
                parts.Add(body);
                k--;
            }
        }

        var collected = new List<string>();
        int j = i;
        while (j >= 0 && j > i - SearchLines)
        {
            var text = lines[j];
            if (IsBlank(text)) break;
            string? body = null;
            foreach (var marker in lineMarkers)
            {
                body = StripLine(text, marker);
                if (body is not null) break;
            }
            if (body is null) break;
            collected.Add(body);
            j--;
        }

        if (collected.Count == 0) return "";
        collected.Reverse();
        while (collected.Count > 0 && PyStr.Strip(collected[^1]).Length == 0) collected.RemoveAt(collected.Count - 1);
        var joined = PyStr.Strip(string.Join("\n", collected));
        if (joined.Length == 0) return "";
        if (IsBanner(joined, j + 1)) return "";
        return Clip(joined);
    }

    static string CleanBlock(IEnumerable<string> parts)
    {
        var output = new List<string>();
        foreach (var raw in parts)
        {
            var body = PyStr.Strip(raw);
            if (body.StartsWith('*')) body = body.Substring(1);
            output.Add(PyStr.Strip(body));
        }
        while (output.Count > 0 && output[0].Length == 0) output.RemoveAt(0);
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        return PyStr.Strip(string.Join("\n", output));
    }

    static bool IsBanner(string text, int firstLineIndex) => firstLineIndex <= 1 && Banner.IsMatch(text);

    static string Clip(string text)
    {
        if (PyStr.Len(text) > MaxChars)
            text = PyStr.RStrip(PyStr.BeforeLast(PyStr.Prefix(text, MaxChars), '\n'));
        return text;
    }

    static readonly Regex DocstringRe = new(@"\A[rubf]{0,2}(""""""|'''|""|')(.*?)(?:\1)", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The docstring at the start of a body, for languages that use one.</summary>
    public static string Docstring(IReadOnlyList<string> lines, long line, string? lang = null)
    {
        var key = PyStr.Strip(lang ?? "").ToLowerInvariant();
        if (!DocstringLangs.Contains(key)) return "";
        if (line < 1 || line > lines.Count) return "";

        var head = lines[(int)line - 1];
        int colon = head.IndexOf(':');
        var after = colon >= 0 ? head.Substring(colon + 1) : "";
        var pending = PyStr.Strip(after).Length > 0 ? new List<string> { after } : [];
        int start = (int)line;

        for (int n = 0; n < 3; n++)
        {
            if (start >= lines.Count) return "";
            if (PyStr.Strip(lines[start]).Length > 0) break;
            start++;
        }
        if (start >= lines.Count) return "";

        var window = string.Join("\n", pending.Concat(lines.Skip(start).Take(DocstringLines)));
        var m = DocstringRe.Match(PyStr.LStrip(window));
        if (!m.Success) return "";
        var quote = m.Groups[1].Value;
        var content = m.Groups[2].Value;
        if (quote.Length == 1 && PyStr.Len(content) > 60) return "";
        return Clip(DedentBody(content));
    }

    static string DedentBody(string text)
    {
        var lines = PyStr.SplitLines(text);
        while (lines.Count > 0 && PyStr.Strip(lines[0]).Length == 0) lines.RemoveAt(0);
        if (lines.Count == 0) return "";
        var head = PyStr.Strip(lines[0]);
        var rest = PyStr.Dedent(string.Join("\n", lines.Skip(1)));
        return PyStr.Strip($"{head}\n{rest}");
    }

    /// <summary>Documentation for one symbol, from whichever convention the language uses.</summary>
    public static string ForSymbol(IReadOnlyList<string> lines, long line, string? lang = null)
    {
        var key = PyStr.Strip(lang ?? "").ToLowerInvariant();
        if (DocstringLangs.Contains(key))
        {
            var inside = Docstring(lines, line, lang);
            if (inside.Length > 0) return inside;
        }
        return LeadingComment(lines, line, lang);
    }
}
