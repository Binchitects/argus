using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace CodeArena;

/// <summary>
/// This session's own history. What a compaction summarized is out of the model's window but kept in the session's file,
/// every message in order: session_search finds it by its words, session_read reads it again by its number (#N, its place
/// in the session from 1, as the summary cites it). A session can go on for days and still find what was said at its start.
/// </summary>
internal static class SessionHistory
{
    /// <summary>The most messages session_search finds, and session_read reads at once.</summary>
    public const int MostFound = 12, MostRead = 20;

    /// <summary>What of a message session_read gives (a tool's result less), and of all it reads at once.</summary>
    public const int MessageChars = 3_000, ToolChars = 1_500, ReadChars = 16_000;

    /// <summary>Every message of the session, in order, compactions aside.</summary>
    private static List<JsonObject> Messages(ToolContext c) =>
        c.Store?.Invoke() is { } store
            ? store.Read(s => s.Stream.Select(e => e.Message.Clone()).ToList())
            : throw new ToolError("This session keeps no history of its own to read.");

    /// <summary>What of a message is searched and read: its words, and its calls with their arguments.</summary>
    private static string Said(JsonObject m)
    {
        var text = new StringBuilder(SessionSearch.Text(m));
        foreach (var call in (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            text.Append(text.Length > 0 ? "\n" : "").Append("called ").Append(call["function"].Str("name")).Append('(').Append(call["function"].Str("arguments")).Append(')');
        }
        return text.ToString();
    }

    /// <summary>Who said it: the person, the model, or a tool (named by the call it answers).</summary>
    private static string Who(List<JsonObject> all, int i) => all[i].Str("role") switch
    {
        "user" => "the person",
        "tool" => (all.Take(i).Reverse().Select(x => (x["tool_calls"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c.Str("id") == all[i].Str("tool_call_id")))
            .FirstOrDefault(c => c is not null)?["function"].Str("name") ?? "a tool") + "'s result",
        _ => "you",
    };

    public static IEnumerable<ToolDef> Tools() =>
    [
        new()
        {
            Name = "session_search",
            Kind = ToolKind.Read,
            Description = "Find the earlier messages of this session (what a compaction summarized, too) that hold some words: each with its number, who said it and the words around them, the best first. For an exact name, number, command or what the person said.",
            Parameters = (JsonObject)JsonNode.Parse("""
                {"type":"object","properties":{"query":{"type":"string","description":"The words to find: a name, a number, a path, a few words of what was said."}},"required":["query"]}
                """)!,
            Summary = a => a.Str("query") ?? "",
            Run = (a, c, ct) =>
            {
                var query = a.Str("query")?.Trim() ?? "";
                var terms = HistoryWords.Terms(query);
                if (terms.Count == 0)
                {
                    throw new ToolError("Say which words to find: {\"query\":\"the migration's name\"}.");
                }
                var all = Messages(c);
                var found = all.Select((m, i) => (Number: i + 1, Index: i, Text: Said(m))).Select(f => (f.Number, f.Index, f.Text, Hits: HistoryWords.Hits(f.Text, terms)))
                    .Where(f => f.Hits > 0).ToList();
                if (found.Count == 0)
                {
                    return Task.FromResult(new ToolResult($"No message of this session holds \"{query}\". Try other words, or fewer.") { Display = c.Ui.Dim("none found") });
                }
                var best = found.OrderByDescending(f => f.Hits).ThenByDescending(f => f.Number).Take(MostFound).ToList();
                var text = $"{found.Count} {(found.Count == 1 ? "message holds" : "messages hold")} those words" + (found.Count > best.Count ? $"; the {best.Count} best" : "") +
                    ". session_read reads one whole.\n" + string.Join('\n', best.Select(f => $"#{f.Number} {Who(all, f.Index)}: {HistoryWords.Around(f.Text, terms)}"));
                return Task.FromResult(new ToolResult(text) { Display = c.Ui.Dim($"{found.Count} found") });
            },
        },
        new()
        {
            Name = "session_read",
            Kind = ToolKind.Read,
            Description = "Read this session's messages again by their numbers (#N, as the summary or session_search gives them), word for word: up to 20 at once.",
            Parameters = (JsonObject)JsonNode.Parse("""
                {"type":"object","properties":{
                  "from":{"type":"integer","description":"The first message's number."},
                  "to":{"type":"integer","description":"The last message's number (default: 5 after the first)."}},
                 "required":["from"]}
                """)!,
            Summary = a => $"#{a.Int("from")}" + (a.Int("to") is { } to ? $"–#{to}" : ""),
            Run = (a, c, ct) =>
            {
                var all = Messages(c);
                if (a.Int("from") is not { } from || from < 1 || from > all.Count)
                {
                    throw new ToolError($"Give the first message's number, from 1 to {all.Count}: {{\"from\":12}}.");
                }
                var to = Math.Clamp(a.Int("to") ?? from + 5, from, Math.Min(all.Count, from + MostRead - 1));
                var text = new StringBuilder();
                for (var n = from; n <= to; n++)
                {
                    var said = Said(all[n - 1]);
                    var most = all[n - 1].Str("role") == "tool" ? ToolChars : MessageChars;
                    if (said.Length > most)
                    {
                        said = said[..most] + $" […{said.Length - most:N0} characters more]";
                    }
                    var line = $"#{n} {Who(all, n - 1)}: {said}\n\n";
                    if (text.Length + line.Length > ReadChars && text.Length > 0)
                    {
                        text.Append(CultureInfo.InvariantCulture, $"(Up to #{n - 1}: read on from #{n}.)");
                        break;
                    }
                    text.Append(line);
                }
                return Task.FromResult(new ToolResult(text.ToString().TrimEnd()) { Display = c.Ui.Dim($"#{from}–#{to}") });
            },
        },
    ];

    /// <summary>
    /// For a summary of the messages before <paramref name="keepFrom"/> in <paramref name="messages"/> (the model's history,
    /// whose word-for-word part starts at the session's <paramref name="from"/>, after <paramref name="prefix"/> messages of
    /// an earlier summary): their numbers in the session, and the person's among them with their first words, so the summary
    /// can cite them. Empty when the history does not map onto the session (a sub-agent's).
    /// </summary>
    public static string Numbers(IReadOnlyList<JsonObject> messages, int keepFrom, int prefix, int from)
    {
        if (keepFrom <= prefix)
        {
            return "";
        }
        var first = from + 1;
        var last = from + keepFrom - prefix;
        var asked = Enumerable.Range(prefix, keepFrom - prefix).Where(i => messages[i].Str("role") == "user").TakeLast(60)
            .Select(i => $"#{from + i - prefix + 1} \"{Fmt.OneLine(SessionSearch.Text(messages[i]), 60)}\"");
        return $"\n\nThe messages above (after any earlier summary) are #{first} to #{last} of this session, in order; the person's: {string.Join(", ", asked)}. " +
            "Give the numbers where the summary speaks of what was said there (\"chose Postgres (#212)\"), and end with ## Earlier parts: a line for each stretch " +
            "of the session with its numbers and what it was about (\"#1–#140: the login bug\"), keeping the lines of an earlier summary.";
    }
}
