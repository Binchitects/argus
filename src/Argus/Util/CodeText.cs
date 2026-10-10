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
    /// stays: Go's and TypeScript's imports are strings. Raw strings, text blocks and templates go either way; the code in
    /// an interpolated string's holes ($"{x}", `${x}`, f"{x}", "${x}") stays, as code.
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
        bool AtOf(int at, string s) => at < text.Length && text.AsSpan(at).StartsWith(s, StringComparison.Ordinal);

        // A string inside an interpolation's hole ("…", '…', `…`): where it ends, so its braces and quotes do not count.
        int Nested(int at)
        {
            var quote = text[at];
            var j = at + 1;
            while (j < text.Length && text[j] != quote && !Breaks(text[j])) j += text[j] == '\\' ? 2 : 1;
            return Math.Min(text.Length, j + 1);
        }

        // An interpolated string from its first character of text: the text blanked, the code in its holes kept (a symbol
        // named there is used). close ends it; hole opens a hole ({, ${, or as many braces as a C# raw string's $s);
        // dollarName reads Kotlin's $name; doubledQuote is C# verbatim's "" and doubledBrace an escaped {{.
        void Interpolated(int from, string close, string hole, bool backslash, bool multiline, bool doubledQuote = false, bool doubledBrace = false, bool dollarName = false)
        {
            Skip(from);
            var j = from;
            while (j < text.Length)
            {
                if (backslash && text[j] == '\\') { j += 2; continue; }
                if (doubledQuote && AtOf(j, "\"\"")) { j += 2; continue; }
                if (doubledBrace && AtOf(j, "{{")) { j += 2; continue; }
                if (AtOf(j, close)) { j += close.Length; break; }
                if (!multiline && Breaks(text[j])) break;
                if (AtOf(j, hole))
                {
                    Skip(j + hole.Length);
                    var depth = 1;
                    var k = j + hole.Length;
                    while (k < text.Length && depth > 0)
                    {
                        var ch = text[k];
                        if (ch is '"' or '\'' or '`') { k = Nested(k); continue; }
                        if (ch == '{') depth++;
                        else if (ch == '}' && --depth == 0) break;
                        k++;
                    }
                    Keep(k);
                    j = k + 1;
                    Skip(j);
                    continue;
                }
                if (dollarName && text[j] == '$' && j + 1 < text.Length && (char.IsLetter(text[j + 1]) || text[j + 1] == '_'))
                {
                    Skip(j + 1);
                    var k = j + 1;
                    while (Word(k)) k++;
                    Keep(k);
                    j = k;
                    continue;
                }
                j++;
            }
            Skip(Math.Min(j, text.Length));
        }

        // Where a JavaScript regex literal may start: not after a value (a name, a number, a closing bracket).
        bool RegexStarts()
        {
            var j = i - 1;
            while (j >= 0 && text[j] is ' ' or '\t') j--;
            if (j < 0 || Breaks(text[j])) return true;
            if ("(,=:[!&|?{};+-*%<>~^".Contains(text[j])) return true;
            if (!Word(j)) return false;
            var end = j + 1;
            while (Word(j)) j--;
            return text[(j + 1)..end] is "return" or "typeof" or "case" or "do" or "else" or "in" or "of" or "new" or "delete" or "void" or "throw" or "yield" or "await";
        }

        // A C++ raw string's prefix (R, u8R, uR, UR, LR) and its delimiter: the '(' that ends it, else -1.
        int RawOpen()
        {
            var start = i;
            while (Word(start - 1)) start--;
            if (text[start..i] is not ("" or "u8" or "u" or "U" or "L")) return -1;
            for (var j = i + 2; j < text.Length && j - i - 2 <= 16; j++)
            {
                if (text[j] == '(') return j;
                if (text[j] is ' ' or ')' or '\\' or '"' or '\t' || Breaks(text[j])) return -1;
            }
            return -1;
        }

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (family == Family.Python)
            {
                if (c == '#')
                {
                    Skip(text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length);
                    continue;
                }
                // f-strings (f, rf, fr in any case): their holes are code.
                var p = i;
                while (p < text.Length && p - i < 2 && text[p] is 'f' or 'F' or 'r' or 'R' or 'b' or 'B' or 'u' or 'U') p++;
                var prefix = text[i..p].ToLowerInvariant();
                if (p > i && !Word(i - 1) && p < text.Length && text[p] is '"' or '\'' && prefix.Contains('f'))
                {
                    var triple = AtOf(p, new string(text[p], 3));
                    var close = new string(text[p], triple ? 3 : 1);
                    Interpolated(p + close.Length, close, "{", backslash: !prefix.Contains('r'), multiline: triple, doubledBrace: true);
                }
                else if (At("\"\"\"") || At("\'\'\'"))
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
            else if (c == '/' && lang is "typescript" or "javascript" && RegexStarts())
            {
                // A regex literal: a quote, a backtick or /* inside it opens nothing.
                var j = i + 1;
                var inClass = false;
                while (j < text.Length && !Breaks(text[j]))
                {
                    if (text[j] == '\\') { j += 2; continue; }
                    if (text[j] == '[') inClass = true;
                    else if (text[j] == ']') inClass = false;
                    else if (text[j] == '/' && !inClass) break;
                    j++;
                }
                if (j >= text.Length || Breaks(text[j]))
                {
                    Keep(i + 1);
                    continue;
                }
                j++;
                while (j < text.Length && char.IsAsciiLetter(text[j])) j++;
                if (keepStrings) Keep(j);
                else Skip(j);
            }
            else if (lang == "csharp" && (c is '@' or '$') && !Word(i - 1))
            {
                // C#'s verbatim (@"…", "" inside), interpolated ($"…", its holes code) and raw ($"""…""") strings.
                var j = i;
                while (j < text.Length && text[j] is '@' or '$') j++;
                if (j >= text.Length || text[j] != '"')
                {
                    Keep(i + 1);
                    continue;
                }
                var verbatim = text.AsSpan(i, j - i).Contains('@');
                var dollars = text.AsSpan(i, j - i).Count('$');
                var run = 0;
                while (j + run < text.Length && text[j + run] == '"') run++;
                if (run >= 3 && !verbatim)
                {
                    // A raw string ($ before it: its holes are as many braces as $s).
                    var close = new string('"', run);
                    if (dollars > 0) Interpolated(j + run, close, new string('{', dollars), backslash: false, multiline: true);
                    else Skip(text.IndexOf(close, j + run, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length);
                    continue;
                }
                if (dollars > 0)
                {
                    Interpolated(j + 1, "\"", "{", backslash: !verbatim, multiline: verbatim, doubledQuote: verbatim, doubledBrace: true);
                    continue;
                }
                var k = j + 1;
                while (k < text.Length)
                {
                    if (text[k] == '"' && k + 1 < text.Length && text[k + 1] == '"') k += 2;
                    else if (text[k] == '"') break;
                    else k++;
                }
                Skip(Math.Min(text.Length, k + 1));
            }
            else if (At("\"\"\"") && lang is "csharp" or "java" or "kotlin")
            {
                // C#'s raw strings (three quotes or more), Java's text blocks, Kotlin's raw strings (with $ templates).
                var run = 0;
                while (i + run < text.Length && text[i + run] == '"') run++;
                if (lang == "kotlin") Interpolated(i + 3, "\"\"\"", "${", backslash: false, multiline: true, dollarName: true);
                else
                {
                    var close = new string('"', lang == "csharp" ? run : 3);
                    Skip(text.IndexOf(close, i + run, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length);
                }
            }
            else if (c == '"' && lang == "kotlin")
            {
                Interpolated(i + 1, "\"", "${", backslash: true, multiline: false, dollarName: true);
            }
            else if (c == 'R' && next == '"' && lang is "c" or "cpp" && RawOpen() is var open and > 0)
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
            else if (c == '`' && lang == "go")
            {
                // Go's raw strings.
                Skip(text.IndexOf('`', i + 1) is var end and >= 0 ? end + 1 : text.Length);
            }
            else if (c == '`' && lang is "typescript" or "javascript")
            {
                // A template: its ${…} holes are code.
                Interpolated(i + 1, "`", "${", backslash: true, multiline: true);
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
