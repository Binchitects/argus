using System.Text;
using Llm.Core.Chat;

namespace Llm.Api.Chat;

/// <summary>A memory read from Markdown: its words, whose (and its project), its kind, pinned or not.</summary>
public sealed record MarkdownMemory(string Text, string Scope, string? Project, string Kind, bool Pinned);

/// <summary>
/// A person's memories as Markdown, out and back in (Memory → Download, Import): a section for each whose (## About you,
/// ## Project host/group/name, ## The company's), a heading for each kind (### Preferences, ### Facts…), a line for each
/// memory (- its words, " (pinned)" when it is). What another assistant's export or a hand-written list says reads too:
/// lines with no heading are facts about the person.
/// </summary>
public static class MemoryMarkdown
{
    /// <summary>The most lines one import reads.</summary>
    public const int MostLines = 1_000;

    private const string PinnedMark = " (pinned)";

    private static readonly (string Kind, string Heading)[] Headings =
        [(Memory.Preference, "Preferences"), (Memory.Fact, "Facts"), (Memory.Decision, "Decisions"), (Memory.HowTo, "How to"), (Memory.Lesson, "Lessons")];

    public static string Write(IEnumerable<Memory> memories)
    {
        var md = new StringBuilder("# Memory\n");
        foreach (var whose in memories.Where(m => !m.Proposed && m.ReplacedBy is null)
            .GroupBy(m => (m.Scope, m.ProjectKey)).OrderBy(g => g.Key.Scope == Memory.Person ? 0 : g.Key.Scope == Memory.Project ? 1 : 2).ThenBy(g => g.Key.ProjectKey))
        {
            md.Append("\n## ").Append(whose.Key.Scope switch
            {
                Memory.Project => "Project " + whose.Key.ProjectKey,
                Memory.Company => "The company's",
                _ => "About you",
            }).Append('\n');
            foreach (var (kind, heading) in Headings)
            {
                var these = whose.Where(m => m.Kind == kind).OrderByDescending(m => m.Pinned).ThenBy(m => m.CreatedAt).ToList();
                if (these.Count == 0)
                {
                    continue;
                }
                md.Append("\n### ").Append(heading).Append("\n\n");
                foreach (var m in these)
                {
                    md.Append("- ").Append(m.Text.ReplaceLineEndings(" ")).Append(m.Pinned ? PinnedMark : "").Append('\n');
                }
            }
        }
        return md.ToString();
    }

    public static List<MarkdownMemory> Read(string markdown)
    {
        var found = new List<MarkdownMemory>();
        var (scope, project, kind) = (Memory.Person, (string?)null, Memory.Fact);
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n').Take(MostLines))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal) && !line.StartsWith("### ", StringComparison.Ordinal))
            {
                var whose = line[3..].Trim();
                (scope, project, kind) = whose.StartsWith("Project ", StringComparison.OrdinalIgnoreCase) && whose.Length > 8
                    ? (Memory.Project, (string?)whose[8..].Trim(), Memory.Fact)
                    : whose.Contains("company", StringComparison.OrdinalIgnoreCase) ? (Memory.Company, null, Memory.Fact) : (Memory.Person, null, Memory.Fact);
                continue;
            }
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = line[4..].Trim();
                kind = Headings.FirstOrDefault(h => h.Heading.Equals(heading, StringComparison.OrdinalIgnoreCase)
                    || h.Kind.Equals(heading.TrimEnd('s'), StringComparison.OrdinalIgnoreCase)).Kind ?? Memory.Fact;
                continue;
            }
            if (line.Length < 3 || line[0] is not ('-' or '*') || line[1] != ' ')
            {
                continue;
            }
            var text = line[2..].Trim();
            var pinned = text.EndsWith(PinnedMark, StringComparison.OrdinalIgnoreCase);
            if (pinned)
            {
                text = text[..^PinnedMark.Length].TrimEnd();
            }
            if (text.Length > 0)
            {
                found.Add(new MarkdownMemory(text, scope, project, kind, pinned));
            }
        }
        return found;
    }
}
