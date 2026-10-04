using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Bots;

/// <summary>Text for the platforms: answers cut to a post's size, Markdown as Slack writes it, mentions and subjects cleaned.</summary>
public static partial class BotText
{
    /// <summary>
    /// The answer as it fits a post of at most <paramref name="limit"/> characters: whole, or cut
    /// at a line or a word and ended with <paramref name="more"/> (a link to the whole of it in
    /// the chat). A code block left open by the cut is closed.
    /// </summary>
    public static string Fit(string text, int limit, string more)
    {
        if (text.Length <= limit)
        {
            return text;
        }
        var room = Math.Max(0, limit - more.Length - 8);
        var cut = text[..room];
        var at = cut.LastIndexOf('\n');
        if (at < room * 0.8)
        {
            at = cut.LastIndexOf(' ');
        }
        if (at >= room * 0.8)
        {
            cut = cut[..at];
        }
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }
        cut = cut.TrimEnd();
        if (Regex.Count(cut, "^\\s*```", RegexOptions.Multiline) % 2 == 1)
        {
            cut += "\n```";
        }
        return $"{cut}\n…\n\n{more}";
    }

    /// <summary>Markdown as Slack's mrkdwn writes it: *bold*, _italic_, ~struck~, &lt;url|links&gt;, headings in bold, code as it is.</summary>
    public static string SlackMarkdown(string markdown)
    {
        var sb = new StringBuilder();
        var parts = Fence().Split(markdown);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                // A code block: the language after the fence means nothing to Slack, and shows as a line.
                sb.Append("```").Append(Escape(FenceLanguage().Replace(parts[i], ""))).Append("```");
                continue;
            }
            sb.Append(Inline(parts[i]));
        }
        return sb.ToString();
    }

    private static string Inline(string text)
    {
        // Inline code is kept as it is; everything else is converted around it.
        var pieces = InlineCode().Split(text);
        var sb = new StringBuilder();
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i % 2 == 1)
            {
                sb.Append('`').Append(Escape(pieces[i])).Append('`');
                continue;
            }
            var links = new List<string>();
            var s = Link().Replace(pieces[i], m =>
            {
                links.Add($"<{m.Groups[2].Value}|{Escape(m.Groups[1].Value)}>");
                return $"\u0002{links.Count - 1}\u0002";
            });
            s = Escape(s);
            s = Quote().Replace(s, "> ");
            s = Heading().Replace(s, "\u0001$1\u0001");
            s = Bold().Replace(s, "\u0001$2\u0001");
            s = Italic().Replace(s, "_$1_");
            s = Struck().Replace(s, "~$1~");
            s = Bullet().Replace(s, "$1• ");
            s = s.Replace('\u0001', '*');
            s = Placeholder().Replace(s, m => links[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
            sb.Append(s);
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>Slack's mentions (&lt;@U123&gt;) taken out of a question, and its escapes undone.</summary>
    public static string SlackQuestion(string text) =>
        SlackMention().Replace(text, "").Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal).Trim();

    /// <summary>Teams' mentions (&lt;at&gt;Name&lt;/at&gt;) taken out of a question.</summary>
    public static string TeamsQuestion(string text) => System.Net.WebUtility.HtmlDecode(TeamsMention().Replace(text, "")).Trim();

    /// <summary>An email's subject as its thread: without Re:, Fwd: and the like, spaces folded, in lower case.</summary>
    public static string Subject(string? subject)
    {
        var s = (subject ?? "").Trim();
        while (ReplyPrefix().Match(s) is { Success: true } m)
        {
            s = s[m.Length..].TrimStart();
        }
        s = Spaces().Replace(s, " ").ToLowerInvariant();
        return s.Length > 400 ? s[..400] : s;
    }

    [GeneratedRegex(@"```([\s\S]*?)```")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^[A-Za-z0-9_+#.-]+(?=\n)")]
    private static partial Regex FenceLanguage();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^#{1,6}[ \t]+(.+?)[ \t]*#*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\*\w])\*(?=[^\s*])([^*\n]+?)(?<=\S)\*(?![\*\w])")]
    private static partial Regex Italic();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex Struck();

    [GeneratedRegex(@"^([ \t]*)[-*][ \t]+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^&gt;[ \t]?", RegexOptions.Multiline)]
    private static partial Regex Quote();

    [GeneratedRegex("\u0002(\\d+)\u0002")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"<@[A-Z0-9]+(\|[^>]*)?>")]
    private static partial Regex SlackMention();

    [GeneratedRegex(@"<at>[^<]*</at>", RegexOptions.IgnoreCase)]
    private static partial Regex TeamsMention();

    [GeneratedRegex(@"^(re|fw|fwd|aw|wg|sv|vs|antw)\s*(\[\d+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
