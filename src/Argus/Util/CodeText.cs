using System.Text;

namespace Argus.Util;

/// <summary>
/// Source text with its comments and string literals blanked: spaces, with line breaks kept, so every line and column
/// stays where it was. Code inside a string (an analyzer's test, a generator's template, a docstring) or a comment
/// declares and uses nothing, so what files declare (Links) and the lines that name a symbol (change_impact) are read
/// from what is left.
/// </summary>
public static class CodeText
{
    enum Family { CLike, Python, None }

    /// <summary>The characters a line ends at (Python's splitlines): kept, so the blanked text has the same lines.</summary>
    static bool Breaks(char c) => c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

    /// <summary>Whether Blank reads this language (else the text is returned as it is).</summary>
    public static bool Reads(string? lang) => FamilyOf(lang) != Family.None;

    static Family FamilyOf(string? lang) => lang switch
    {
        "csharp" or "java" or "kotlin" or "go" or "c" or "cpp" or "typescript" or "javascript" or "rust" or "proto" => Family.CLike,
        "python" => Family.Python,
        _ => Family.None,
    };

    /// <summary>
    /// The text with comments and strings blanked. With keepStrings, a plain quoted string ("…", and '…' where it is one)
    /// stays: Go's and TypeScript's imports are strings. Raw strings, text blocks and templates go either way.
    /// </summary>
    public static string Blank(string? lang, string text, bool keepStrings = false)
    {
        var family = FamilyOf(lang);
        if (family == Family.None) return text;
        var b = new StringBuilder(text.Length);
        var i = 0;
        void Skip(int to)
        {
            for (; i < to && i < text.Length; i++) b.Append(Breaks(text[i]) ? text[i] : ' ');
        }
        void Keep(int to)
        {
            for (; i < to && i < text.Length; i++) b.Append(text[i]);
        }
        // One quoted string on one line, with backslash escapes; kept or blanked.
        void Quoted(char quote)
        {
            var j = i + 1;
            while (j < text.Length && text[j] != quote && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
            if (keepStrings) Keep(Math.Min(text.Length, j + 1));
            else Skip(Math.Min(text.Length, j + 1));
        }
        bool At(string s) => text.AsSpan(i).StartsWith(s, StringComparison.Ordinal);
        bool Word(int at) => at >= 0 && at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_');

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (family == Family.Python)
            {
                if (c == '#') Skip(text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length);
                else if (At("\"\"\"") || At("'''"))
                {
                    var close = text.Substring(i, 3);
                    Skip(text.IndexOf(close, i + 3, StringComparison.Ordinal) is var end and >= 0 ? end + 3 : text.Length);
                }
                else if (c is '"' or '\'') Quoted(c);
                else Keep(i + 1);
                continue;
            }
            if (c == '/' && next == '/')
            {
                Skip(text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length);
            }
            else if (c == '/' && next == '*')
            {
                // Rust's and Kotlin's block comments nest.
                var nests = lang is "rust" or "kotlin";
                var depth = 0;
                var j = i;
                while (j < text.Length)
                {
                    if (text[j] == '/' && j + 1 < text.Length && text[j + 1] == '*' && (nests || depth == 0)) { depth++; j += 2; }
                    else if (text[j] == '*' && j + 1 < text.Length && text[j + 1] == '/') { depth--; j += 2; if (depth == 0) break; }
                    else j++;
                }
                Skip(j);
            }
            else if (lang == "csharp" && (c is '@' or '$') && !Word(i - 1))
            {
                // C#'s verbatim (@"…", "" inside), interpolated ($"…") and raw ($"""…""") strings.
                var j = i;
                while (j < text.Length && text[j] is '@' or '$') j++;
                if (j >= text.Length || text[j] != '"')
                {
                    Keep(i + 1);
                    continue;
                }
                var run = 0;
                while (j + run < text.Length && text[j + run] == '"') run++;
                if (run >= 3)
                {
                    var close = new string('"', run);
                    Skip(text.IndexOf(close, j + run, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length);
                    continue;
                }
                var verbatim = text.AsSpan(i, j - i).Contains('@');
                var k = j + 1;
                while (k < text.Length)
                {
                    if (verbatim && text[k] == '"' && k + 1 < text.Length && text[k + 1] == '"') k += 2;
                    else if (!verbatim && text[k] == '\\') k += 2;
                    else if (text[k] == '"') break;
                    else k++;
                }
                Skip(Math.Min(text.Length, k + 1));
            }
            else if (At("\"\"\"") && lang is "csharp" or "java" or "kotlin")
            {
                // C#'s raw strings (three quotes or more), Java's and Kotlin's text blocks.
                var run = 0;
                while (i + run < text.Length && text[i + run] == '"') run++;
                var close = new string('"', lang == "csharp" ? run : 3);
                Skip(text.IndexOf(close, i + run, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length);
            }
            else if (c == 'R' && next == '"' && lang is "c" or "cpp" && text.IndexOf('(', i + 2) is var open and > 0 && open - i <= 18)
            {
                // C++'s raw string: R"delim( … )delim".
                var close = ")" + text[(i + 2)..open] + "\"";
                Skip(text.IndexOf(close, open, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length);
            }
            else if (c == 'r' && lang == "rust" && !Word(i - 1) && (next == '"' || next == '#'))
            {
                // Rust's raw string: r"…", r#"…"#.
                var j = i + 1;
                while (j < text.Length && text[j] == '#') j++;
                if (j >= text.Length || text[j] != '"')
                {
                    Keep(i + 1);
                    continue;
                }
                var close = "\"" + new string('#', j - i - 1);
                Skip(text.IndexOf(close, j + 1, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length);
            }
            else if (c == '`' && lang is "go" or "typescript" or "javascript")
            {
                // Go's raw strings, JavaScript's templates.
                Skip(text.IndexOf('`', i + 1) is var end and >= 0 ? end + 1 : text.Length);
            }
            else if (c == '"' || c == '\'' && lang is "typescript" or "javascript" or "proto")
            {
                Quoted(c);
            }
            else if (c == '\'' && i + 2 < text.Length)
            {
                // A character literal ('x', '\n', '\''); not Rust's lifetime ('a).
                var end = text[i + 1] == '\\' ? text.IndexOf('\'', i + 3) : i + 2;
                if (end > i && end < text.Length && text[end] == '\'' && end - i <= 10) Skip(end + 1);
                else Keep(i + 1);
            }
            else
            {
                Keep(i + 1);
            }
        }
        return b.ToString();
    }
}
