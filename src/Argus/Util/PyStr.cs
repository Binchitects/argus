using System.Text;

namespace Argus.Util;

/// <summary>
/// Python string semantics the port depends on.
///
/// The Python implementation is the specification, and a handful of its string
/// primitives do not behave like their nearest .NET relative. Each one here is a
/// place where "close enough" would change an answer: <c>splitlines</c> splits on
/// eight separators <c>string.Split('\n')</c> does not know, <c>len</c> counts code
/// points rather than UTF-16 units, and <c>str.split()</c> treats U+001C..U+001F as
/// whitespace where <see cref="char.IsWhiteSpace(char)"/> does not.
/// </summary>
public static class PyStr
{
    /// <summary>Python's <c>str.isspace</c> for one UTF-16 unit.</summary>
    public static bool IsSpace(char c) =>
        c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r'
            or '\x1c' or '\x1d' or '\x1e' or '\x1f' or '\x85'
        || (c > 0x7f && char.IsWhiteSpace(c));

    static bool IsLineBreak(char c) =>
        c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

    /// <summary><c>str.splitlines()</c>: no trailing empty element, all Python separators.</summary>
    public static List<string> SplitLines(string? text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;
        int start = 0, i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (IsLineBreak(c))
            {
                lines.Add(text.Substring(start, i - start));
                if (c == '\r' && i + 1 < n && text[i + 1] == '\n') i++;
                i++;
                start = i;
                continue;
            }
            i++;
        }
        if (start < n) lines.Add(text.Substring(start));
        return lines;
    }

    /// <summary><c>str.split()</c> with no separator: runs of whitespace, no empties.</summary>
    public static List<string> SplitWhitespace(string? text)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(text)) return parts;
        int i = 0, n = text.Length;
        while (i < n)
        {
            while (i < n && IsSpace(text[i])) i++;
            if (i >= n) break;
            int s = i;
            while (i < n && !IsSpace(text[i])) i++;
            parts.Add(text.Substring(s, i - s));
        }
        return parts;
    }

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int s = 0, e = text.Length;
        while (s < e && IsSpace(text[s])) s++;
        while (e > s && IsSpace(text[e - 1])) e--;
        return text.Substring(s, e - s);
    }

    public static string LStrip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int s = 0;
        while (s < text.Length && IsSpace(text[s])) s++;
        return text.Substring(s);
    }

    public static string RStrip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int e = text.Length;
        while (e > 0 && IsSpace(text[e - 1])) e--;
        return text.Substring(0, e);
    }

    /// <summary><c>s.strip(chars)</c>.</summary>
    public static string Strip(string text, string chars)
    {
        int s = 0, e = text.Length;
        while (s < e && chars.IndexOf(text[s]) >= 0) s++;
        while (e > s && chars.IndexOf(text[e - 1]) >= 0) e--;
        return text.Substring(s, e - s);
    }

    public static string LStrip(string text, string chars)
    {
        int s = 0;
        while (s < text.Length && chars.IndexOf(text[s]) >= 0) s++;
        return text.Substring(s);
    }

    public static string RStrip(string text, string chars)
    {
        int e = text.Length;
        while (e > 0 && chars.IndexOf(text[e - 1]) >= 0) e--;
        return text.Substring(0, e);
    }

    static bool HasSurrogates(string s)
    {
        foreach (var c in s) if (char.IsSurrogate(c)) return true;
        return false;
    }

    /// <summary><c>len(s)</c>: code points, not UTF-16 units.</summary>
    public static int Len(string s)
    {
        if (!HasSurrogates(s)) return s.Length;
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    /// <summary><c>s[:count]</c> by code point, never splitting a surrogate pair.</summary>
    public static string Prefix(string s, int count)
    {
        if (count <= 0) return "";
        if (!HasSurrogates(s)) return count >= s.Length ? s : s.Substring(0, count);
        int units = 0, points = 0;
        while (units < s.Length && points < count)
        {
            if (char.IsHighSurrogate(s[units]) && units + 1 < s.Length && char.IsLowSurrogate(s[units + 1])) units += 2;
            else units++;
            points++;
        }
        return s.Substring(0, units);
    }

    /// <summary><c>s[start:start+count]</c> by code point.</summary>
    public static string Slice(string s, int start, int count)
    {
        if (!HasSurrogates(s))
        {
            if (start >= s.Length) return "";
            return s.Substring(start, Math.Min(count, s.Length - start));
        }
        var cps = new List<int>();
        for (int i = 0; i < s.Length; i++)
        {
            cps.Add(i);
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        }
        if (start >= cps.Count) return "";
        int from = cps[start];
        int endIndex = start + count;
        int to = endIndex >= cps.Count ? s.Length : cps[endIndex];
        return s.Substring(from, to - from);
    }

    /// <summary><c>s.rsplit(sep, 1)[-1]</c>.</summary>
    public static string AfterLast(string s, char sep)
    {
        int i = s.LastIndexOf(sep);
        return i < 0 ? s : s.Substring(i + 1);
    }

    /// <summary><c>s.rsplit(sep, 1)[0]</c>.</summary>
    public static string BeforeLast(string s, char sep)
    {
        int i = s.LastIndexOf(sep);
        return i < 0 ? s : s.Substring(0, i);
    }

    public static int Count(string s, char c)
    {
        int n = 0;
        foreach (var ch in s) if (ch == c) n++;
        return n;
    }

    /// <summary>Python's <c>repr()</c> of a str, for error messages that quote a value.</summary>
    public static string Repr(string? s)
    {
        if (s is null) return "None";
        bool useDouble = s.Contains('\'') && !s.Contains('"');
        char q = useDouble ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(q);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == q) { sb.Append('\\').Append(c); }
                    else if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append(q);
        return sb.ToString();
    }

    /// <summary><c>str.isalnum()</c>.</summary>
    public static bool IsAlnum(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (!char.IsLetterOrDigit(c)) return false;
        return true;
    }

    /// <summary><c>str.isdigit()</c> for the ASCII case the callers need.</summary>
    public static bool IsDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (!char.IsDigit(c)) return false;
        return true;
    }

    public static string Lower(string? s) => (s ?? "").ToLowerInvariant();

    /// <summary><c>textwrap.dedent</c>: remove the longest common leading whitespace.</summary>
    public static string Dedent(string text)
    {
        var lines = text.Split('\n');
        string? margin = null;
        foreach (var line in lines)
        {
            if (line.Trim(' ', '\t').Length == 0) continue;
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            var indent = line.Substring(0, i);
            if (margin is null) margin = indent;
            else if (indent.StartsWith(margin, StringComparison.Ordinal)) { }
            else if (margin.StartsWith(indent, StringComparison.Ordinal)) margin = indent;
            else
            {
                int k = 0;
                while (k < margin.Length && k < indent.Length && margin[k] == indent[k]) k++;
                margin = margin.Substring(0, k);
            }
        }
        var sb = new StringBuilder();
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (line.Trim(' ', '\t').Length == 0) line = "";
            else if (!string.IsNullOrEmpty(margin) && line.StartsWith(margin, StringComparison.Ordinal))
                line = line.Substring(margin.Length);
            if (li > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }

    /// <summary>Ordinal comparison, which is how Python compares str.</summary>
    public static int Cmp(string? a, string? b) => string.CompareOrdinal(a, b);
}
