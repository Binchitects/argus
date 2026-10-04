using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Knowledge;

/// <summary>
/// A document in passages of about 1,000 characters: cut at headings first, then between paragraphs,
/// a long paragraph at line or word ends. A passage cut for size starts with the end of the one before
/// (a short paragraph, or about 150 characters), so a sentence on the cut is whole in one of them. Each
/// passage knows its lines and the headings it is under (Deploy › Rollback), to cite and to embed.
/// </summary>
public static partial class Chunker
{
    public sealed record Chunk(int Line, int EndLine, string? Heading, string Text);

    public const int Target = 1_000;
    /// <summary>A heading starts a new passage when the one being made has at least this much.</summary>
    private const int Min = 300;
    /// <summary>A paragraph this short at the end of a passage starts the next one too.</summary>
    private const int Overlap = 200;
    private const int Tail = 150;

    private sealed record Block(int Start, int End, string Text, int Level = 0, string? Title = null);

    public static List<Chunk> Split(string text)
    {
        var chunks = new List<Chunk>();
        // The passage being made: its blocks, each with the headings it is under.
        var current = new List<(Block Block, string? Under)>();
        var size = 0;
        var carried = false;
        var headings = new string?[7];
        string? Path() => headings.Any(h => h is not null) ? string.Join(" › ", headings.Where(h => h is not null)) : null;

        void Clear()
        {
            current.Clear();
            size = 0;
            carried = false;
        }

        void Flush(bool overlap)
        {
            if (current.Count == 0 || carried)
            {
                return;
            }
            // A passage is under the headings most of its text is under.
            var heading = current.GroupBy(c => c.Under ?? "").MaxBy(g => g.Sum(c => c.Block.Text.Length))!.Key;
            chunks.Add(new Chunk(current[0].Block.Start, current[^1].Block.End, heading.Length > 0 ? heading : null, string.Join("\n\n", current.Select(c => c.Block.Text))));
            var last = current[^1];
            Clear();
            if (overlap && last.Block.Level == 0 && last.Block.Text.Length <= Overlap)
            {
                current.Add(last);
                size = last.Block.Text.Length;
                carried = true;
            }
        }

        void Add(Block b)
        {
            current.Add((b, Path()));
            size += b.Text.Length + 2;
            carried = false;
        }

        foreach (var b in Blocks(text))
        {
            if (b.Level > 0)
            {
                if (carried)
                {
                    // A new section does not start with the end of the last.
                    Clear();
                }
                else if (size >= Min)
                {
                    Flush(false);
                }
                headings[b.Level] = b.Title;
                Array.Fill(headings, null, b.Level + 1, headings.Length - b.Level - 1);
                Add(b);
                continue;
            }
            if (b.Text.Length > Target)
            {
                if (carried)
                {
                    Clear();
                }
                if (size >= Min)
                {
                    Flush(false);
                }
                // What is left (a heading, a short paragraph) starts its first piece.
                chunks.AddRange(Pieces(b, [.. current.Select(c => c.Block)], Path()));
                Clear();
                continue;
            }
            if (size > 0 && size + b.Text.Length > Target)
            {
                Flush(true);
            }
            Add(b);
        }
        Flush(false);
        return chunks;
    }

    /// <summary>What is embedded for a passage: its document's title and headings, then its text.</summary>
    public static string ForEmbedding(string title, Chunk c) =>
        (c.Heading is { Length: > 0 } h ? $"{title} › {h}" : title) + "\n\n" + c.Text;

    /// <summary>A paragraph longer than a passage, in pieces at line ends (or word ends), each starting with the end of the one before.</summary>
    private static List<Chunk> Pieces(Block b, List<Block> lead, string? heading)
    {
        var piece = new StringBuilder(lead.Count > 0 ? string.Join("\n\n", lead.Select(x => x.Text)) + "\n\n" : "");
        var first = lead.Count > 0 ? lead[0].Start : b.Start;
        var lines = b.Text.Split('\n');
        var line = b.Start;
        var pieces = new List<Chunk>();
        // A piece is never only what it starts with (the lead, or the end of the piece before).
        var floor = piece.Length;
        var added = false;
        for (var i = 0; i < lines.Length; i++, line++)
        {
            foreach (var part in Words(lines[i]))
            {
                if (piece.Length > floor && piece.Length + part.Length + 1 > Target)
                {
                    pieces.Add(new Chunk(first, Math.Max(first, line - (piece[^1] == '\n' ? 1 : 0)), heading, piece.ToString().TrimEnd()));
                    var tail = TailOf(pieces[^1].Text);
                    piece.Clear().Append(tail).Append(tail.Length > 0 ? " " : "");
                    floor = piece.Length;
                    added = false;
                    first = line;
                }
                piece.Append(part);
                added |= part.Trim().Length > 0;
            }
            piece.Append('\n');
        }
        if (added)
        {
            pieces.Add(new Chunk(first, b.End, heading, piece.ToString().TrimEnd()));
        }
        return pieces;
    }

    /// <summary>A line as it is, or a line longer than a passage in parts at spaces.</summary>
    private static IEnumerable<string> Words(string line)
    {
        while (line.Length > Target)
        {
            var cut = line.LastIndexOf(' ', Target - 1);
            cut = cut < Target / 2 ? Target - 1 : cut;
            yield return line[..(cut + 1)];
            line = line[(cut + 1)..];
        }
        yield return line;
    }

    /// <summary>About the last 150 characters of a passage, from a word's start.</summary>
    private static string TailOf(string text)
    {
        if (text.Length <= Tail)
        {
            return "";
        }
        var tail = text[^Tail..];
        var space = tail.IndexOfAny([' ', '\n']);
        return (space >= 0 ? tail[(space + 1)..] : tail).Replace('\n', ' ').Trim();
    }

    /// <summary>Headings (Markdown, AsciiDoc, a PDF's pages) and paragraphs (split at blank lines; never inside a code fence), with their lines.</summary>
    private static List<Block> Blocks(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<Block>();
        var buffer = new List<string>();
        var start = 0;
        var fence = false;
        void End(int i)
        {
            if (buffer.Count > 0)
            {
                blocks.Add(new Block(start + 1, i, string.Join('\n', buffer)));
                buffer.Clear();
            }
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();
            if (l.TrimStart().StartsWith("```", StringComparison.Ordinal) || l.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = !fence;
            }
            else if (!fence && l.Length == 0)
            {
                End(i);
                continue;
            }
            else if (!fence && Heading(l) is { } h)
            {
                End(i);
                blocks.Add(new Block(i + 1, i + 1, l.Trim(), h.Level, h.Title));
                continue;
            }
            else if (!fence && buffer.Count == 1 && Underline().IsMatch(l))
            {
                // A setext heading: its text, then a line of = or -.
                var title = buffer[0].Trim();
                buffer.Clear();
                blocks.Add(new Block(i, i + 1, $"{title}\n{l}", l[0] == '=' ? 1 : 2, title));
                continue;
            }
            if (buffer.Count == 0)
            {
                start = i;
            }
            buffer.Add(l);
        }
        End(lines.Length);
        return blocks;
    }

    private static (int Level, string Title)? Heading(string line)
    {
        if (MarkdownHeading().Match(line) is { Success: true } m)
        {
            return (m.Groups[1].Length, m.Groups[2].Value.Trim());
        }
        if (AsciiDocHeading().Match(line) is { Success: true } a)
        {
            return (a.Groups[1].Length, a.Groups[2].Value.Trim());
        }
        // A PDF's pages, as the chat extracts them.
        return PdfPage().Match(line) is { Success: true } p ? (1, $"Page {p.Groups[1].Value}") : null;
    }

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^(={1,6})\s+(\S.*)$")]
    private static partial Regex AsciiDocHeading();

    [GeneratedRegex(@"^--- page (\d+) ---$")]
    private static partial Regex PdfPage();

    [GeneratedRegex(@"^(=+|-+)\s*$")]
    private static partial Regex Underline();
}
