using System.Text.RegularExpressions;

namespace Llm.Api.Chat;

/// <summary>
/// The parts of a long text about a question: its blocks scored by the question's words (BM25:
/// rarer words count more, long blocks less), the best kept in the text's order within a budget,
/// each with where it starts, to read around it.
/// </summary>
public static partial class Passages
{
    public sealed record Passage(int Start, string Text);

    private const int BlockChars = 1_500;

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "what", "which", "how", "are", "was", "were", "has", "have", "its",
        "into", "about", "when", "where", "who", "why", "does", "did", "can", "not", "but", "you", "your", "they", "their", "there",
    };

    public static List<Passage> Pick(string text, string focus, int budget)
    {
        var terms = Words(focus).Distinct().ToList();
        var blocks = Blocks(text);
        if (terms.Count == 0 || blocks.Count == 0)
        {
            return [];
        }
        var words = blocks.Select(b => Words(b.Text).ToList()).ToList();
        var average = Math.Max(1, words.Average(w => w.Count));
        var df = terms.ToDictionary(t => t, t => words.Count(w => w.Contains(t)));
        var scores = words.Select(w => terms.Sum(t =>
        {
            var tf = w.Count(x => x == t);
            var idf = Math.Log(1 + (blocks.Count - df[t] + 0.5) / (df[t] + 0.5));
            return tf == 0 ? 0 : idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * w.Count / average));
        })).ToList();
        var chosen = new SortedSet<int>();
        var used = 0;
        foreach (var i in Enumerable.Range(0, blocks.Count).Where(i => scores[i] > 0).OrderByDescending(i => scores[i]))
        {
            if (used + blocks[i].Text.Length > budget)
            {
                continue;
            }
            chosen.Add(i);
            used += blocks[i].Text.Length;
            // The heading a chosen block is under says what it is about.
            if (i > 0 && blocks[i - 1].Text.StartsWith('#') && blocks[i - 1].Text.Length < 200 && used + blocks[i - 1].Text.Length <= budget && chosen.Add(i - 1))
            {
                used += blocks[i - 1].Text.Length;
            }
        }
        return [.. chosen.Select(i => blocks[i])];
    }

    /// <summary>Paragraphs (split at blank lines), a long one in pieces of about 1,500 characters at line or word ends.</summary>
    private static List<Passage> Blocks(string text)
    {
        var blocks = new List<Passage>();
        foreach (Match m in Paragraph().Matches(text))
        {
            var start = m.Index;
            var rest = m.Value;
            while (rest.Length > BlockChars)
            {
                var cut = rest.LastIndexOf('\n', BlockChars);
                if (cut < BlockChars / 2)
                {
                    cut = rest.LastIndexOf(' ', BlockChars);
                }
                if (cut < BlockChars / 2)
                {
                    cut = BlockChars;
                }
                blocks.Add(new Passage(start, rest[..cut].Trim()));
                start += cut;
                rest = rest[cut..];
            }
            if (rest.Trim().Length > 0)
            {
                blocks.Add(new Passage(start, rest.Trim()));
            }
        }
        return blocks;
    }

    private static IEnumerable<string> Words(string text) => Word().Matches(text.ToLowerInvariant())
        .Select(m => m.Value.Length > 4 && m.Value.EndsWith('s') ? m.Value[..^1] : m.Value)
        .Where(w => !Common.Contains(w));

    [GeneratedRegex(@"[\p{L}\p{N}]{3,}")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?:(?!\n\s*\n)[\s\S])+")]
    private static partial Regex Paragraph();
}
