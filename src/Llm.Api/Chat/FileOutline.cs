using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Chat;

/// <summary>
/// A long file's map for the model: its sections with their lines, so it reads the part it needs (read_file by lines or
/// by a section's name) rather than taking the file whole. Markdown by its headings, a PDF by its pages, code by the
/// lines that look like declarations (in most languages); other text has none.
/// </summary>
public static partial class FileOutline
{
    /// <summary>A part of a file: its name as the outline shows it, its first and last lines (from 1), and how deep it is.</summary>
    public sealed record Section(string Name, int Start, int End, int Depth);

    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".java", ".kt", ".rs", ".c", ".h", ".cc", ".cpp", ".hpp",
        ".swift", ".rb", ".php", ".scala", ".sh", ".ps1", ".lua", ".dart", ".m", ".vb", ".fs", ".sql",
    };

    /// <summary>A file's sections, in its order; none when it has no shape to show.</summary>
    public static List<Section> Sections(string fileName, string text)
    {
        var lines = (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || lines.Length > 0 && Page().IsMatch(lines[0]))
        {
            return Pages(lines);
        }
        if (Code.Contains(ext))
        {
            return Declarations(lines);
        }
        return Headings(lines);
    }

    /// <summary>
    /// The outline as the model reads it, within <paramref name="maxChars"/>: one line a section, indented by depth; past
    /// the room, the deeper ones are left out first. Empty when the file has no sections.
    /// </summary>
    public static string Render(List<Section> sections, int maxChars)
    {
        if (sections.Count == 0)
        {
            return "";
        }
        for (var deepest = sections.Max(s => s.Depth); ; deepest--)
        {
            var sb = new StringBuilder();
            var hidden = 0;
            foreach (var s in sections)
            {
                if (s.Depth > deepest)
                {
                    hidden++;
                    continue;
                }
                sb.Append(' ', 2 * (s.Depth + 1)).Append(s.Name.Length > 110 ? s.Name[..110] + "…" : s.Name)
                    .Append(CultureInfo.InvariantCulture, $"  [lines {s.Start}-{s.End}]\n");
            }
            if (hidden > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  ({hidden} smaller sections inside these not listed)\n");
            }
            if (sb.Length <= maxChars)
            {
                return sb.ToString();
            }
            if (deepest == 0)
            {
                var all = sb.ToString();
                var cut = all.LastIndexOf('\n', Math.Max(0, maxChars - 60));
                return all[..(cut + 1)] + "  (… more sections after these: outline_file lists them all)\n";
            }
        }
    }

    /// <summary>The section a name means: the same name, else the one whose name holds it (any case); null when none or several do.</summary>
    public static Section? Find(List<Section> sections, string name, out List<Section> candidates)
    {
        var asked = name.Trim().TrimStart('#').Trim();
        candidates = [.. sections.Where(s => Bare(s.Name).Equals(asked, StringComparison.OrdinalIgnoreCase))];
        if (candidates.Count == 0)
        {
            candidates = [.. sections.Where(s => s.Name.Contains(asked, StringComparison.OrdinalIgnoreCase))];
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string Bare(string name) => name.TrimStart('#').Trim();

    /// <summary>A section's last line, the blank lines before the next one left out.</summary>
    private static int Trimmed(string[] lines, int start, int end)
    {
        while (end > start && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }
        return end;
    }

    private static List<Section> Pages(string[] lines)
    {
        var starts = lines.Select((l, i) => (l, i)).Where(x => Page().IsMatch(x.l)).Select(x => (Name: x.l.Trim('-', ' '), Line: x.i + 1)).ToList();
        return [.. starts.Select((p, k) => new Section(p.Name, p.Line, Trimmed(lines, p.Line, k + 1 < starts.Count ? starts[k + 1].Line - 1 : lines.Length), 0))];
    }

    private static List<Section> Headings(string[] lines)
    {
        var heads = new List<(int Level, string Name, int Line)>();
        var fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }
            if (!fence && Heading().Match(line) is { Success: true } m)
            {
                heads.Add((m.Groups[1].Value.Length, line.Trim(), i + 1));
            }
        }
        var top = heads.Count == 0 ? 1 : heads.Min(h => h.Level);
        return [.. heads.Select((h, k) =>
        {
            var next = heads.Skip(k + 1).FirstOrDefault(n => n.Level <= h.Level);
            return new Section(h.Name, h.Line, Trimmed(lines, h.Line, next.Line > 0 ? next.Line - 1 : lines.Length), h.Level - top);
        })];
    }

    private static List<Section> Declarations(string[] lines)
    {
        var found = new List<(int Line, int Indent, string Text)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length <= 400 && Declaration().Match(line) is { Success: true } m)
            {
                found.Add((i + 1, m.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length, line.Trim()));
            }
        }
        var indents = found.Select(f => f.Indent).Distinct().Order().ToList();
        var sections = new List<Section>();
        for (var k = 0; k < found.Count; k++)
        {
            var end = lines.Length;
            for (var j = k + 1; j < found.Count; j++)
            {
                if (found[j].Indent <= found[k].Indent)
                {
                    end = found[j].Line - 1;
                    break;
                }
            }
            sections.Add(new Section(found[k].Text, found[k].Line, Trimmed(lines, found[k].Line, end), indents.IndexOf(found[k].Indent)));
        }
        return sections;
    }

    [GeneratedRegex(@"^-{3} page \d+ -{3}$")]
    private static partial Regex Page();

    [GeneratedRegex(@"^(#{1,6})\s+\S")]
    private static partial Regex Heading();

    // class, def, func, fn, function… with what may come before them (at most 12 spaces in); or a C-like function or
    // method: a type, a name, its parameters, and no ; after them (a declaration only, or a call).
    [GeneratedRegex(@"^(?:(?<indent>[ \t]{0,12})(?:(?:export|public|private|protected|internal|static|async|pub(?:\([\w:]+\))?|final|abstract|virtual|override|extern|inline|unsafe|default|open|data|sealed|suspend|const|partial)\s+)*(?:func|fn|def|class|struct|interface|enum|trait|impl|type|module|function|object|record|namespace|sub|proc)\s+(?:\([^)]*\)\s*)?[A-Za-z_][\w:.]*|(?<indent>[ \t]{0,12})(?!(?:return|else|if|for|foreach|while|switch|case|do|goto|throw|await|yield|new|using|lock|catch|when|typeof|sizeof|nameof|delete)\b)[A-Za-z_][\w:<>,*& ]*[\s*&]+[A-Za-z_~][\w:~]*\s*\((?!.*\)\s*;\s*$))")]
    private static partial Regex Declaration();
}
