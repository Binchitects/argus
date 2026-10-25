using System.Text;

namespace Llm.Core.Chat;

/// <summary>
/// Finding what was said earlier in a long conversation by its words: the web chat's chat_search and Code Arena's
/// session_search (linked into Code Arena) read a query the same way and show a match the same way.
/// </summary>
public static class HistoryWords
{
    /// <summary>Words that say little on their own.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "a", "about", "again", "all", "also", "an", "and", "any", "are", "as", "at", "be", "before", "but", "by", "can", "could", "did", "do",
        "does", "earlier", "for", "from", "had", "has", "have", "how", "i", "in", "is", "it", "its", "me", "mention", "mentioned", "my", "of",
        "on", "or", "our", "said", "say", "should", "so", "some", "talk", "talked", "tell", "that", "the", "their", "then", "there", "these",
        "this", "those", "to", "told", "us", "was", "we", "were", "what", "when", "where", "which", "who", "why", "will", "with", "would",
        "you", "your",
    };

    /// <summary>The words of a query worth finding, in lower case.</summary>
    public static List<string> Terms(string query)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in query.ToLowerInvariant().Append(' '))
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' && word.Length > 0)
            {
                word.Append(ch);
                continue;
            }
            var w = word.ToString().Trim('.', '-');
            if (w.Length > 1 && !Common.Contains(w) && !words.Contains(w))
            {
                words.Add(w);
            }
            word.Clear();
        }
        return words;
    }

    /// <summary>How many of the terms a text holds (any case), or 0 when it holds fewer than half of them.</summary>
    public static int Hits(string text, IReadOnlyList<string> terms)
    {
        var hits = terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        return hits * 2 >= terms.Count ? hits : 0;
    }

    /// <summary>The words around the first of the terms a message holds, on one line.</summary>
    public static string Around(string text, IReadOnlyList<string> terms, int chars = 240)
    {
        var at = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - chars / 3);
        var end = Math.Min(text.Length, start + chars);
        var part = text[start..end].ReplaceLineEndings(" ");
        return (start > 0 ? "…" : "") + part + (end < text.Length ? "…" : "");
    }
}
