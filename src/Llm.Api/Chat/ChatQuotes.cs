using Llm.Core.Chat;

namespace Llm.Api.Chat;

/// <summary>
/// A question that quotes part of an earlier answer (the person selected it and pressed Reply). The quote is kept apart from what
/// they typed (ChatMessage.Quote) and is the person's own words to the model, never the model's: it is checked as the message is,
/// and the model is told only that it comes from "an earlier answer". Code Arena composes the same text (its Quotes.cs).
/// </summary>
public static class ChatQuotes
{
    /// <summary>The longest quote; the message's own limit applies to what was typed.</summary>
    public const int MaxChars = 4000;

    public const string TooLong = "A quote is at most 4,000 characters.";

    /// <summary>A quote as it is kept: line endings tidied and trimmed; null when there is none.</summary>
    public static string? Clean(string? quote)
    {
        var text = (quote ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Why a quote is refused (it is too long); null when it may go.</summary>
    public static string? Check(string? quote) => quote is { Length: > MaxChars } ? TooLong : null;

    /// <summary>
    /// What the model reads for a question: the quote first, marked as such, then what the person typed. A "&lt;/quote&gt;"
    /// in the quote is written "&lt;\/quote&gt;", so the quote cannot end early. Without a quote, the question as it is.
    /// </summary>
    public static string ForModel(string? quote, string content)
    {
        if (string.IsNullOrEmpty(quote))
        {
            return content;
        }
        var head = "The person quotes this part of an earlier answer:\n<quote>\n" + quote.Replace("</quote>", "<\\/quote>", StringComparison.Ordinal) + "\n</quote>";
        return content.Length == 0 ? head : head + "\n\n" + content;
    }

    /// <summary>What a question asks, for searching: the quote and the question together.</summary>
    public static string Asked(ChatMessage m) => string.IsNullOrEmpty(m.Quote) ? m.Content : m.Quote + "\n" + m.Content;
}
