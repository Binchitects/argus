using System.Text;

namespace Llm.Api.Tags;

/// <summary>The tags' settings (Tags:Known): the ones offered first, and the only ones Summarize and tag picks.</summary>
public sealed class TagOptions
{
    public string? Known { get; set; } = "review\nsecurity\nbug\nidea\nquestion\ndocs";

    /// <summary>The known tags, cleaned (<see cref="Tagging.Clean"/>), in the order given.</summary>
    public IReadOnlyList<string> KnownTags => Tagging.Clean(Gateway.AnswerCacheOptions.Split(Known), int.MaxValue);
}

/// <summary>
/// Tags on chats, projects and the prompt log's lines (review, security, …): short words, lower case, a dash for a space,
/// letters of any language, digits, '-' and '_'. A chat's request counts under its chat's tags and its project's
/// (Accounting → By tag).
/// </summary>
public static class Tagging
{
    public const int MaxLength = 32;
    public const int MaxTags = 10;

    /// <summary>A tag as kept: trimmed, lower case, spaces as dashes, other marks dropped; null when nothing is left.</summary>
    public static string? One(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }
        var b = new StringBuilder();
        foreach (var c in tag.Trim().TrimStart('#').ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                b.Append(c);
            }
            else if (char.IsWhiteSpace(c) && b.Length > 0 && b[^1] != '-')
            {
                b.Append('-');
            }
        }
        var s = b.ToString().Trim('-');
        return s.Length == 0 ? null : s.Length <= MaxLength ? s : s[..MaxLength].TrimEnd('-');
    }

    /// <summary>Each tag cleaned, once each, at most <paramref name="most"/>.</summary>
    public static List<string> Clean(IEnumerable<string?>? tags, int most = MaxTags) =>
        [.. (tags ?? []).Select(One).OfType<string>().Distinct(StringComparer.Ordinal).Take(most)];
}
