using System.Text.RegularExpressions;

namespace Llm.Api.Notifications;

/// <summary>
/// A notification's words as the bell shows them: plain text, not Markdown (no code, no stars, links as their words), a
/// gateway's error as its message (not its classes), on one line, short.
/// </summary>
public static partial class NewsText
{
    public const int Most = 280;

    public static string? Plain(string? text, int most = Most)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var t = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        t = Fence().Replace(t, " (code) ");
        t = Image().Replace(t, "$1");
        t = Link().Replace(t, "$1");
        t = InlineCode().Replace(t, "$1");
        t = Emphasis().Replace(t, "$2");
        t = LineMarks().Replace(t, "");
        // A gateway's error: its message, without the classes it passed through ("litellm.InternalServerError: …: OpenAIException - ").
        t = ErrorClasses().Replace(t, "");
        t = Space().Replace(t, " ").Trim();
        if (t.Length == 0)
        {
            return null;
        }
        return t.Length <= most ? t : t[..(most - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"```[^\n]*\n?[\s\S]*?(```|$)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`([^`]*)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"(\*\*|__|\*|_|~~)(\S(?:.*?\S)?)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?m)^\s{0,3}(#{1,6}\s+|>\s?|[-*+]\s+|\d+[.)]\s+|\|)|\|\s*$|^\s*[-|: ]{3,}\s*$")]
    private static partial Regex LineMarks();

    [GeneratedRegex(@"(?:\b[a-z_]+\.)?\b[A-Za-z]*(?:Error|Exception)\b(?:\s*[:-]\s*)")]
    private static partial Regex ErrorClasses();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
