namespace CodeArena;

/// <summary>
/// A message that quotes part of an earlier answer (the person selected it in the IDE's chat, or in the web chat, and pressed
/// Reply). The session keeps what the model reads: the quote first, marked as such, then what was typed (and after that what
/// went with it, <see cref="Mentions"/>). Arena keeps the quote as its own field: <see cref="Split"/> takes it out again,
/// for the page and for the sync. The text is the one Arena's own model reads (its ChatQuotes.ForModel).
/// </summary>
internal static class Quotes
{
    /// <summary>The longest quote a message carries.</summary>
    public const int MaxChars = 4000;

    private const string Head = "The person quotes this part of an earlier answer:\n<quote>\n";
    private const string Tail = "\n</quote>";
    private const string Close = "</quote>";
    private const string Escaped = "<\\/quote>";

    /// <summary>A quote as it is sent: line endings tidied and trimmed; null when there is none.</summary>
    public static string? Clean(string? quote)
    {
        var text = (quote ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// What the model reads: the quote, then what was typed. A "&lt;/quote&gt;" in the quote is written "&lt;\/quote&gt;", so the
    /// quote cannot end early. Without a quote, the message as it is.
    /// </summary>
    public static string Compose(string? quote, string content)
    {
        if (string.IsNullOrEmpty(quote))
        {
            return content;
        }
        var head = Head + quote.Replace(Close, Escaped, StringComparison.Ordinal) + Tail;
        return content.Length == 0 ? head : head + "\n\n" + content;
    }

    /// <summary>A message the model reads split into its quote (null: none) and the rest (Text); matched only at its very start.</summary>
    public static (string? Quote, string Text) Split(string content)
    {
        if (!content.StartsWith(Head, StringComparison.Ordinal) || content.IndexOf(Tail, Head.Length, StringComparison.Ordinal) is not (var end and >= 0))
        {
            return (null, content);
        }
        var quote = content[Head.Length..end].Replace(Escaped, Close, StringComparison.Ordinal);
        var rest = content[(end + Tail.Length)..];
        return (quote, rest.StartsWith("\n\n", StringComparison.Ordinal) ? rest[2..] : rest);
    }
}
