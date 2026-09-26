using System.Text.RegularExpressions;
using Argus.Util;

namespace Argus.Packs;

public sealed record Chunk(string HeadingPath, string? Anchor, int StartLine, string Body);

/// <summary>
/// Markdown (and reStructuredText, converted) into retrieval chunks:
/// split at headings, keep fenced blocks whole, pack
/// paragraphs up to a size, and carry the heading trail and a stable anchor.
/// </summary>
public static class Chunker
{
    const RegexOptions O = RegexOptions.CultureInvariant;
    static readonly Regex HeadingRe = new(@"^(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$", O);
    static readonly Regex FenceRe = new(@"^(```+|~~~+)", O);
    static readonly Regex PinnedAnchorRe = new(@"\s*\{/\*\s*(.*?)\s*\*/\}\s*$", O);
    static readonly Regex RstRuleRe = new(@"^([=\-~^""'`#*+:.,_])\1{2,}\s*$", O);
    static readonly Regex RstRoleRe = new(@":[\w:+-]+:`([^`]*)`", O);
    static readonly Regex RstLiteralRe = new(@"``([^`]*)``", O);
    static readonly Regex RstInterpretedRe = new(@"`([^`]*)`", O);
    static readonly Regex RstEmphasisRe = new(@"\*{1,2}([^*]+)\*{1,2}", O);
    static readonly Regex SlugStripRe = new(@"[^\w\s-]", O);
    static readonly Regex SlugWhitespaceRe = new(@"[\s_]+", O);

    static bool ClosesFence(string marker, (char Char, int Min) opener) => marker[0] == opener.Char && marker.Length >= opener.Min;

    static string RoleText(Match m)
    {
        var inner = m.Groups[1].Value;
        if (inner.StartsWith('!')) return inner.Substring(1);
        if (inner.StartsWith('~')) return PyStr.AfterLast(inner.Substring(1), '.');
        return inner;
    }

    public static string StripRstInline(string text)
    {
        text = RstRoleRe.Replace(text, RoleText);
        text = RstLiteralRe.Replace(text, "$1");
        text = RstInterpretedRe.Replace(text, "$1");
        text = RstEmphasisRe.Replace(text, "$1");
        return PyStr.Strip(text);
    }

    /// <summary>Rewrite reST section titles as ATX headings, levels in order of first appearance.</summary>
    public static string RstToAtx(string text)
    {
        var lines = PyStr.SplitLines(text);
        var output = new List<string>(lines);
        var styles = new List<(char, bool)>();
        int LevelFor((char, bool) style)
        {
            if (!styles.Contains(style)) styles.Add(style);
            return Math.Min(styles.IndexOf(style) + 1, 6);
        }
        int index = 0;
        while (index < lines.Count)
        {
            var current = PyStr.Strip(lines[index]);
            var over = RstRuleRe.IsMatch(current);
            if (over && index + 2 < lines.Count)
            {
                var title = PyStr.Strip(lines[index + 1]);
                var below = PyStr.Strip(lines[index + 2]);
                if (title.Length > 0 && !RstRuleRe.IsMatch(title) && RstRuleRe.IsMatch(below) && below[0] == current[0])
                {
                    var marker = new string('#', LevelFor((current[0], true)));
                    output[index] = "";
                    output[index + 1] = $"{marker} {StripRstInline(title)}";
                    output[index + 2] = "";
                    index += 3;
                    continue;
                }
            }
            if (current.Length > 0 && !over && index + 1 < lines.Count)
            {
                var below = PyStr.Strip(lines[index + 1]);
                if (RstRuleRe.IsMatch(below) && PyStr.Len(below) >= PyStr.Len(current))
                {
                    var marker = new string('#', LevelFor((below[0], false)));
                    output[index] = $"{marker} {StripRstInline(current)}";
                    output[index + 1] = "";
                    index += 2;
                    continue;
                }
            }
            index++;
        }
        return string.Join("\n", output);
    }

    public static List<bool> FencedLineFlags(IReadOnlyList<string> lines)
    {
        var flags = new List<bool>();
        (char, int)? opener = null;
        foreach (var line in lines)
        {
            var m = FenceRe.Match(PyStr.Strip(line));
            if (opener is { } o)
            {
                flags.Add(true);
                if (m.Success && ClosesFence(m.Groups[1].Value, o)) opener = null;
                continue;
            }
            if (m.Success)
            {
                opener = (m.Groups[1].Value[0], m.Groups[1].Value.Length);
                flags.Add(true);
                continue;
            }
            flags.Add(false);
        }
        return flags;
    }

    public static string Slugify(string title)
    {
        var slug = PyStr.Strip(title).ToLowerInvariant();
        slug = SlugStripRe.Replace(slug, "");
        slug = SlugWhitespaceRe.Replace(slug, "-");
        return PyStr.Strip(slug, "-");
    }

    static string DedupeAnchor(string b, Dictionary<string, int> seen)
    {
        var count = seen.GetValueOrDefault(b, 0) + 1;
        seen[b] = count;
        return count == 1 ? b : $"{b}-{count}";
    }

    sealed record Section(string HeadingPath, string? Anchor, int StartLine, List<string> Lines);

    static List<Section> SplitIntoSections(string text)
    {
        var lines = PyStr.SplitLines(text);
        var stack = new List<(int Level, string Title, string Pinned)>();
        var sections = new List<Section>();
        var currentLines = new List<string>();
        int currentStart = 1;
        (char, int)? opener = null;
        var seenAnchors = new Dictionary<string, int>(StringComparer.Ordinal);

        void Flush()
        {
            var headingPath = string.Join(" > ", stack.Select(s => s.Title));
            string? anchor = null;
            if (stack.Count > 0)
            {
                var (_, title, pinned) = stack[^1];
                anchor = DedupeAnchor(pinned.Length > 0 ? pinned : Slugify(title), seenAnchors);
            }
            sections.Add(new Section(headingPath, anchor, currentStart, new List<string>(currentLines)));
        }

        for (int i = 0; i < lines.Count; i++)
        {
            int lineno = i + 1;
            var line = lines[i];
            var fence = FenceRe.Match(PyStr.Strip(line));
            if (opener is { } o)
            {
                currentLines.Add(line);
                if (fence.Success && ClosesFence(fence.Groups[1].Value, o)) opener = null;
                continue;
            }
            if (fence.Success)
            {
                opener = (fence.Groups[1].Value[0], fence.Groups[1].Value.Length);
                currentLines.Add(line);
                continue;
            }
            var heading = HeadingRe.Match(line);
            if (heading.Success)
            {
                Flush();
                int level = heading.Groups[1].Value.Length;
                var title = PyStr.Strip(heading.Groups[2].Value);
                var pinnedMatch = PinnedAnchorRe.Match(title);
                var pinned = pinnedMatch.Success ? pinnedMatch.Groups[1].Value : "";
                if (pinnedMatch.Success) title = PyStr.Strip(title.Substring(0, pinnedMatch.Index));
                while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
                stack.Add((level, title, pinned));
                currentLines = [];
                currentStart = lineno + 1;
                continue;
            }
            currentLines.Add(line);
        }
        Flush();
        return sections.Where(s => PyStr.Strip(string.Join("\n", s.Lines)).Length > 0).ToList();
    }

    sealed record Block(int StartLine, string Text, bool IsFence);

    static List<Block> SplitIntoBlocks(List<string> lines, int startLine)
    {
        var blocks = new List<Block>();
        var cur = new List<string>();
        int curStart = startLine;
        (char, int)? opener = null;
        for (int offset = 0; offset < lines.Count; offset++)
        {
            int lineno = startLine + offset;
            var line = lines[offset];
            var stripped = PyStr.Strip(line);
            var fence = FenceRe.Match(stripped);
            if (opener is { } o)
            {
                cur.Add(line);
                if (fence.Success && ClosesFence(fence.Groups[1].Value, o))
                {
                    blocks.Add(new Block(curStart, string.Join("\n", cur), true));
                    cur = [];
                    opener = null;
                }
                continue;
            }
            if (fence.Success)
            {
                if (cur.Count > 0) { blocks.Add(new Block(curStart, string.Join("\n", cur), false)); cur = []; }
                curStart = lineno;
                cur = [line];
                opener = (fence.Groups[1].Value[0], fence.Groups[1].Value.Length);
                continue;
            }
            if (stripped.Length == 0)
            {
                if (cur.Count > 0) { blocks.Add(new Block(curStart, string.Join("\n", cur), false)); cur = []; }
                continue;
            }
            if (cur.Count == 0) curStart = lineno;
            cur.Add(line);
        }
        if (cur.Count > 0) blocks.Add(new Block(curStart, string.Join("\n", cur), opener is not null));
        return blocks;
    }

    static List<string> WrapByChars(string text, int maxChars)
    {
        var words = PyStr.SplitWhitespace(text);
        if (words.Count == 0) return [];
        var pieces = new List<string>();
        var cur = new List<string>();
        int curLen = 0;
        foreach (var word in words)
        {
            int extra = PyStr.Len(word) + (cur.Count > 0 ? 1 : 0);
            if (cur.Count > 0 && curLen + extra > maxChars)
            {
                pieces.Add(string.Join(" ", cur));
                cur = [word];
                curLen = PyStr.Len(word);
            }
            else
            {
                cur.Add(word);
                curLen += extra;
            }
        }
        if (cur.Count > 0) pieces.Add(string.Join(" ", cur));
        return pieces;
    }

    static List<(int, string)> PackUnits(List<(int Start, string Text)> units, int maxChars)
    {
        var packed = new List<(int, string)>();
        var curTexts = new List<string>();
        int curStart = 0, curLen = 0;
        foreach (var (start, text) in units)
        {
            int extra = PyStr.Len(text) + (curTexts.Count > 0 ? 2 : 0);
            if (curTexts.Count > 0 && curLen + extra > maxChars)
            {
                packed.Add((curStart, string.Join("\n\n", curTexts)));
                curTexts = [text];
                curStart = start;
                curLen = PyStr.Len(text);
            }
            else
            {
                if (curTexts.Count == 0) curStart = start;
                curTexts.Add(text);
                curLen += extra;
            }
        }
        if (curTexts.Count > 0) packed.Add((curStart, string.Join("\n\n", curTexts)));
        return packed;
    }

    public static List<Chunk> ChunkMarkdown(string text, int maxChars = 1200)
    {
        var chunks = new List<Chunk>();
        foreach (var section in SplitIntoSections(text))
        {
            var units = new List<(int, string)>();
            foreach (var block in SplitIntoBlocks(section.Lines, section.StartLine))
            {
                if (block.IsFence || PyStr.Len(block.Text) <= maxChars) units.Add((block.StartLine, block.Text));
                else units.AddRange(WrapByChars(block.Text, maxChars).Select(p => (block.StartLine, p)));
            }
            foreach (var (start, body) in PackUnits(units, maxChars))
                chunks.Add(new Chunk(section.HeadingPath, section.Anchor, start, body));
        }
        return chunks;
    }

    public static string EmbedText(Chunk chunk) => chunk.HeadingPath.Length == 0 ? chunk.Body : $"{chunk.HeadingPath}\n{chunk.Body}";
}
