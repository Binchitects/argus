using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Llm.Core.Chat;

/// <summary>
/// A piece of a request to the model: the part of the window it is (<see cref="ContextWindow.Parts"/>), its name, and its
/// text, or for a picture or a sound what it weighs in characters. <paramref name="Ref"/>: the app's id of what it came from
/// (a message, a tool), for the actions on it; <paramref name="At"/>: its message's place in the request (-1: the tools).
/// </summary>
public sealed record WindowPiece(string Part, string Name, string Text, string? Ref = null, string? Tool = null, long Weight = 0, int At = -1, string? Preview = null)
{
    /// <summary>The characters it counts as.</summary>
    public long Chars => Weight > 0 ? Weight : Text.Length;
}

/// <summary>A section of the system message, in its order: its part, its name and its length (the app's own words, a tool's notes, the memory).</summary>
public sealed record WindowSection(string Part, string Name, int Length, string? Ref = null);

/// <summary>A request as the app sent it, or will send it, with what only the app knows of it.</summary>
public sealed record WindowRequest(JsonObject Request)
{
    /// <summary>The system message's sections in order; what they leave is the system prompt's.</summary>
    public IReadOnlyList<WindowSection> System { get; init; } = [];

    /// <summary>The app's id of the message at a place of the request's messages (the web chat's message ids), or null.</summary>
    public Func<int, string?>? RefOf { get; init; }

    /// <summary>A message that is not what its role says (Code Arena's notes, a summary sent as a person's message): its part and name.</summary>
    public Func<int, JsonObject, (string Part, string Name)?>? Classify { get; init; }

    /// <summary>The last characters of a message that are the memories recalled for it: its place and how many.</summary>
    public (int Message, int Chars)? Recalled { get; init; }

    /// <summary>The tool a function is of (its id), which turns it off.</summary>
    public Func<string, string?>? ToolOf { get; init; }

    /// <summary>The characters a picture or a sound counts as (the engines read one as about a thousand tokens).</summary>
    public int PictureChars { get; init; } = 3_500;
}

/// <summary>Counts each text in the model's own tokens (the engine's /tokenize); null when it cannot.</summary>
public delegate Task<IReadOnlyList<int>?> TokenCounter(IReadOnlyList<string> texts, CancellationToken ct);

/// <summary>
/// What fills the model's window, as Claude Code's /context shows it: the request split into its pieces (the system prompt
/// and the tools' notes, the instructions, the memory, each tool's definition, the skills listed and loaded, the files, each
/// message, each tool call and result, the summary of a compaction), each counted in the model's own tokens where its engine
/// has a tokenizer (else estimated from its characters), adding up to the request's tokens: what the chat format adds is a
/// part of its own. Then the room kept for the answer, what is free, and what could be freed. The web chat and Code Arena
/// both build it here (Code Arena links this file, as it builds with nothing but the SDK), so both show the same figures.
/// </summary>
public static partial class ContextWindow
{
    /// <summary>The parts, in the order they are drawn, with their words.</summary>
    public static readonly (string Key, string Label)[] Parts =
    [
        ("system", "System prompt and tool notes"), ("tools", "Tool definitions"), ("instructions", "Your instructions"), ("memory", "Memory"),
        ("skills", "Skills"), ("summary", "Summary of earlier messages"), ("files", "Files"), ("you", "Your messages"),
        ("answers", "Answers"), ("toolResults", "Tool calls and results"), ("format", "Chat format"),
    ];

    /// <summary>The most items a part lists; the rest are summed as more.</summary>
    public const int MostItems = 40;

    /// <summary>The functions that load a skill, whose result is the skill's body: the web chat's, Code Arena's.</summary>
    public static readonly string[] SkillFunctions = ["use_skill", "skill"];

    /// <summary>The request in pieces, in its order: the system message by its sections, each message, each tool's definition.</summary>
    public static List<WindowPiece> Pieces(WindowRequest window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var pieces = new List<WindowPiece>();
        var calls = new Dictionary<string, (string Name, string? Skill)>(StringComparer.Ordinal);
        var messages = window.Request["messages"] as JsonArray ?? [];
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject m)
            {
                continue;
            }
            var reference = window.RefOf?.Invoke(i);
            var (text, media) = Content(m["content"]);
            if (window.Classify?.Invoke(i, m) is { } kind)
            {
                pieces.Add(new WindowPiece(kind.Part, kind.Name, text, reference, At: i, Preview: Preview(text)));
                continue;
            }
            switch (Str(m, "role"))
            {
                case "system":
                    var at = 0;
                    foreach (var s in window.System)
                    {
                        var length = Math.Clamp(s.Length, 0, text.Length - at);
                        if (length > 0)
                        {
                            pieces.Add(new WindowPiece(s.Part, s.Name, text.Substring(at, length), s.Ref, At: i));
                        }
                        at += length;
                    }
                    if (at < text.Length)
                    {
                        pieces.Add(new WindowPiece("system", window.System.Count == 0 ? "System prompt" : "The rest of the system prompt", text[at..], At: i));
                    }
                    break;
                case "user":
                    if (window.Recalled is { } recalled && recalled.Message == i && recalled.Chars > 0 && recalled.Chars <= text.Length)
                    {
                        pieces.Add(new WindowPiece("memory", "Recalled for this question", text[^recalled.Chars..], reference, At: i));
                        text = text[..^recalled.Chars];
                    }
                    pieces.AddRange(Asked(text, reference, i));
                    foreach (var part in media)
                    {
                        pieces.Add(new WindowPiece("files", part, "", reference, Weight: window.PictureChars, At: i));
                    }
                    break;
                case "assistant":
                    if (text.Length > 0)
                    {
                        pieces.Add(new WindowPiece("answers", "An answer", text, reference, At: i, Preview: Preview(text)));
                    }
                    foreach (var call in (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var name = Str(call["function"] as JsonObject, "name") ?? "a tool";
                        if (Str(call, "id") is { } id)
                        {
                            calls[id] = (name, SkillFunctions.Contains(name) ? SkillOf(Str(call["function"] as JsonObject, "arguments")) : null);
                        }
                        pieces.Add(new WindowPiece("toolResults", name + " (call)", call.ToJsonString(), reference, window.ToolOf?.Invoke(name), At: i,
                            Preview: Preview(Str(call["function"] as JsonObject, "arguments") ?? "")));
                    }
                    break;
                case "tool":
                    var (function, loaded) = calls.TryGetValue(Str(m, "tool_call_id") ?? "", out var made) ? made : ("a tool", null);
                    if (SkillFunctions.Contains(function))
                    {
                        var skill = SkillBody().Match(text) is { Success: true } body ? body.Groups[1].Value : loaded ?? "A skill";
                        pieces.Add(new WindowPiece("skills", skill + " (loaded)", text, reference, At: i));
                        break;
                    }
                    pieces.Add(new WindowPiece("toolResults", function, text, reference, window.ToolOf?.Invoke(function), At: i, Preview: Preview(text)));
                    break;
                default:
                    pieces.Add(new WindowPiece("you", Str(m, "role") ?? "A message", text, reference, At: i, Preview: Preview(text)));
                    break;
            }
        }
        foreach (var tool in (window.Request["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Str(tool["function"] as JsonObject, "name") ?? "a tool";
            pieces.Add(new WindowPiece("tools", name, tool.ToJsonString(), Tool: window.ToolOf?.Invoke(name)));
        }
        return pieces;
    }

    /// <summary>A person's message: the skill used with it, the files that went with it (attached, or @mentioned in Code Arena), and their words.</summary>
    private static IEnumerable<WindowPiece> Asked(string text, string? reference, int at)
    {
        var words = text;
        foreach (Match skill in SkillBody().Matches(text))
        {
            yield return new WindowPiece("skills", skill.Groups[1].Value + " (used)", skill.Value, reference, At: at);
            words = words.Replace(skill.Value, "", StringComparison.Ordinal);
        }
        foreach (Match file in Attachment().Matches(words))
        {
            // What went with a skill used in Code Arena and nothing else is the skill's.
            if (file.Groups[1].Success || file.Value.Length > EmptyAttached)
            {
                yield return new WindowPiece("files", file.Groups[1].Success ? file.Groups[1].Value : "Files mentioned", file.Value, reference, At: at);
            }
        }
        words = Attachment().Replace(words, m => m.Groups[1].Success || m.Value.Length > EmptyAttached ? "" : m.Value);
        if (words.Length > 0)
        {
            yield return new WindowPiece("you", "Your message", words, reference, At: at, Preview: Preview(words));
        }
    }

    /// <summary>A message's text, and its pictures and sounds by kind.</summary>
    private static (string Text, List<string> Media) Content(JsonNode? content)
    {
        if (content is JsonArray parts)
        {
            var media = new List<string>();
            var text = new System.Text.StringBuilder();
            foreach (var p in parts.OfType<JsonObject>())
            {
                switch (Str(p, "type"))
                {
                    case "text":
                        text.Append(Str(p, "text"));
                        break;
                    case "image_url":
                        media.Add("A picture");
                        break;
                    case "input_audio":
                        media.Add("A sound");
                        break;
                    default:
                        media.Add("A file");
                        break;
                }
            }
            return (text.ToString(), media);
        }
        return (content is JsonValue v && v.TryGetValue<string>(out var s) ? s : "", []);
    }

    /// <summary>The pieces in tokens: what the model's tokenizer counted, or estimated; adding up to the request's tokens when they are known.</summary>
    /// <param name="counted">Each piece's tokens as the tokenizer counted them (pictures are estimated); null: estimated from characters.</param>
    /// <param name="total">The request's tokens as the engine counted them; the chat format is what they hold beyond the pieces.</param>
    /// <param name="charsPerToken">The characters a token holds, for what is estimated.</param>
    public static (long[] Tokens, long Format) Count(IReadOnlyList<WindowPiece> pieces, IReadOnlyList<int>? counted, long? total, double charsPerToken)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var perToken = charsPerToken > 0 ? charsPerToken : 3.5;
        if (counted is null || counted.Count != pieces.Count)
        {
            // Estimated: the request's tokens shared out by characters, rounded so they add up to it; else at the characters a token holds.
            return total is { } all ? (Shared(pieces.Select(p => (double)p.Chars).ToList(), all), 0)
                : ([.. pieces.Select(p => (long)Math.Round(p.Chars / perToken))], 0);
        }
        var tokens = pieces.Select((p, i) => p.Weight > 0 ? (long)Math.Round(p.Weight / perToken) : Math.Max(0, counted[i])).ToArray();
        var sum = tokens.Sum();
        if (total is not { } known)
        {
            return (tokens, 0);
        }
        if (known >= sum)
        {
            return (tokens, known - sum);
        }
        // Counted one by one they hold a little more than together (a word split at a piece's edge): shared out to the total.
        return (Shared(tokens.Select(t => (double)t).ToList(), known), 0);
    }

    /// <summary><paramref name="total"/> shared out by <paramref name="weights"/>, rounded so the shares add up to it.</summary>
    private static long[] Shared(List<double> weights, long total)
    {
        var all = weights.Sum();
        var shares = new long[weights.Count];
        double sum = 0;
        long before = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            sum += weights[i];
            var upTo = all > 0 ? (long)Math.Round(sum / all * total) : 0;
            shares[i] = upTo - before;
            before = upTo;
        }
        if (all <= 0 && shares.Length > 0)
        {
            shares[0] = total;
        }
        return shares;
    }

    /// <summary>What may free room, and how: the tools never called, big tool results, files that went with earlier messages, and compacting.</summary>
    /// <param name="toolOff">Whether a tool (by its id) can be turned off for this chat.</param>
    /// <param name="leaveOut">Whether a message can be left out from here (the web chat's).</param>
    /// <param name="dropFiles">Whether a file that went with a message can be left out from here.</param>
    /// <param name="compact">Whether the chat can compact now.</param>
    /// <param name="called">The functions called in the chat beyond this request (its earlier messages), which are used.</param>
    public static JsonArray Freeable(IReadOnlyList<WindowPiece> pieces, long[] tokens, long limit, Func<string, bool> toolOff, bool leaveOut, bool dropFiles, bool compact,
        IEnumerable<string>? called = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(toolOff);
        var list = new JsonArray();
        var used = pieces.Where(p => p.Part is "toolResults" or "skills" && p.At >= 0).Select(p => p.Name.Replace(" (call)", "", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        used.UnionWith(called ?? []);
        if (pieces.Any(p => p.Part == "skills" && p.At >= 0))
        {
            used.UnionWith(SkillFunctions);
        }
        // A tool none of whose functions was called: its definitions go in every request for nothing.
        var unused = pieces.Select((p, i) => (p, t: tokens[i])).Where(x => x.p.Part == "tools" && x.p.Tool is not null).GroupBy(x => x.p.Tool!, StringComparer.Ordinal)
            .Where(g => g.All(x => !used.Contains(x.p.Name)))
            .Select(g => (Tool: g.Key, Tokens: g.Sum(x => x.t), Functions: g.Select(x => x.p.Name).ToList()))
            .Where(x => x.Tokens > 0).OrderByDescending(x => x.Tokens).Take(8);
        foreach (var (tool, t, functions) in unused)
        {
            var off = toolOff(tool);
            list.Add(Item("tool", $"{tool}: not called in this chat", t, tool, off ? "tool-off" : null, string.Join(", ", functions)));
        }
        // A big result: what a tool brought back, read again by every request that follows.
        var big = Math.Max(1_000, limit / 50);
        foreach (var (p, t) in pieces.Select((p, i) => (p, t: tokens[i])).Where(x => x.p.Part == "toolResults" && !x.p.Name.EndsWith(" (call)", StringComparison.Ordinal) && x.t >= big)
            .OrderByDescending(x => x.t).Take(5))
        {
            list.Add(Item("result", $"{p.Name}'s result", t, p.Ref, leaveOut && p.Ref is not null ? "leave-out" : null, p.Preview));
        }
        // A file that went with an earlier message: read again by every request, though the talk may have moved on.
        var lastAsked = pieces.Where(p => p.Part is "you" or "files" && p.At >= 0).Select(p => p.At).DefaultIfEmpty(-1).Max();
        foreach (var (p, t) in pieces.Select((p, i) => (p, t: tokens[i])).Where(x => x.p.Part == "files" && x.p.At >= 0 && x.p.At < lastAsked && x.t >= 200)
            .OrderByDescending(x => x.t).Take(5))
        {
            list.Add(Item("file", $"{p.Name}, with an earlier message", t, p.Ref, dropFiles && p.Ref is not null && p.Weight == 0 ? "drop-file" : null, p.Name));
        }
        // The older messages: a compaction makes them a summary (about a tenth of them stays).
        var older = pieces.Select((p, i) => (p, t: tokens[i])).Where(x => x.p.Part is "you" or "answers" or "toolResults" or "files" && x.p.At >= 0 && x.p.At < lastAsked).Sum(x => x.t);
        if (older >= Math.Max(2_000, limit / 20))
        {
            list.Add(Item("compact", "Older messages: compacting makes them a summary", older - older / 10, null, compact ? "compact" : null, null));
        }
        return list;
    }

    private static JsonObject Item(string kind, string label, long tokens, string? reference, string? action, string? detail) => new()
    {
        ["kind"] = kind, ["label"] = label, ["tokens"] = tokens, ["ref"] = reference, ["action"] = action, ["detail"] = detail,
    };

    /// <summary>
    /// The window as the views show it: by part, each with its items (the biggest first, at most <see cref="MostItems"/>),
    /// the total, the room kept for the answer, what is free, how it was counted, and what could be freed.
    /// </summary>
    /// <param name="by">The tokenizer's engine ("llama.cpp", "vLLM", "SGLang"), or why it was estimated.</param>
    public static JsonObject Shape(IReadOnlyList<WindowPiece> pieces, long[] tokens, long format, long limit, long reserve, bool tokenizer, string? by, JsonArray? freeable = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(tokens);
        var total = tokens.Sum() + format;
        var parts = new JsonArray();
        foreach (var (key, label) in Parts)
        {
            var mine = pieces.Select((p, i) => (p, t: tokens[i])).Where(x => x.p.Part == key).ToList();
            var sum = key == "format" ? format : mine.Sum(x => x.t);
            if (sum <= 0 && mine.Count == 0)
            {
                continue;
            }
            var items = mine.OrderByDescending(x => x.t).ToList();
            var shown = items.Take(MostItems).ToList();
            parts.Add(new JsonObject
            {
                ["key"] = key,
                ["label"] = label,
                ["tokens"] = sum,
                ["count"] = items.Count,
                ["items"] = new JsonArray([.. shown.Select(x => (JsonNode)new JsonObject
                {
                    ["name"] = x.p.Name, ["preview"] = x.p.Preview, ["tokens"] = x.t, ["ref"] = x.p.Ref, ["tool"] = x.p.Tool,
                })]),
                ["more"] = items.Count - shown.Count,
                ["moreTokens"] = items.Skip(MostItems).Sum(x => x.t),
            });
        }
        return new JsonObject
        {
            ["total"] = total,
            ["limit"] = limit,
            ["reserve"] = reserve,
            ["free"] = Math.Max(0, limit - total - reserve),
            ["counted"] = tokenizer ? "tokenizer" : "estimate",
            ["by"] = by,
            ["parts"] = parts,
            ["freeable"] = freeable ?? [],
        };
    }

    /// <summary>
    /// The window as text, for a terminal: a bar of the whole, then each part with its tokens and share, and its biggest
    /// items (Code Arena's /context, as Claude Code's).
    /// </summary>
    public static IEnumerable<string> Lines(JsonObject window, int items = 5, int width = 40)
    {
        ArgumentNullException.ThrowIfNull(window);
        var limit = Math.Max(1, window["limit"]?.GetValue<long>() ?? 1);
        var total = window["total"]?.GetValue<long>() ?? 0;
        var reserve = window["reserve"]?.GetValue<long>() ?? 0;
        var used = (int)Math.Round(Math.Min(1.0, (double)total / limit) * width);
        var kept = (int)Math.Round(Math.Min(1.0 - (double)used / width, (double)reserve / limit) * width);
        yield return "[" + new string('█', used) + new string('▒', Math.Max(0, kept)) + new string('·', Math.Max(0, width - used - kept)) + "] " +
            $"{Tokens(total)} of {Tokens(limit)} tokens ({Percent(total, limit)})";
        yield return window["counted"]?.GetValue<string>() == "tokenizer"
            ? $"Counted with the model's tokenizer ({window["by"]?.GetValue<string>() ?? "its engine"})."
            : $"Estimated from characters{(window["by"]?.GetValue<string>() is { Length: > 0 } why ? $" ({why})" : "")}.";
        foreach (var part in (window["parts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var tokens = part["tokens"]?.GetValue<long>() ?? 0;
            yield return $"  {part["label"]?.GetValue<string>(),-30} {Tokens(tokens),8}  {Percent(tokens, limit),4}";
            foreach (var item in (part["items"] as JsonArray ?? []).OfType<JsonObject>().Take(items))
            {
                yield return $"      {Cut(item["name"]?.GetValue<string>() ?? "", 34),-34} {Tokens(item["tokens"]?.GetValue<long>() ?? 0),6}";
            }
            var rest = (part["count"]?.GetValue<int>() ?? 0) - Math.Min(items, (part["items"] as JsonArray)?.Count ?? 0);
            if (rest > 0)
            {
                yield return $"      and {rest} more";
            }
        }
        yield return $"  {"Kept for the answer",-30} {Tokens(reserve),8}  {Percent(reserve, limit),4}";
        var free = window["free"]?.GetValue<long>() ?? 0;
        yield return $"  {"Free",-30} {Tokens(free),8}  {Percent(free, limit),4}";
        var freeable = (window["freeable"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (freeable.Count > 0)
        {
            yield return "Could be freed:";
            foreach (var f in freeable)
            {
                yield return $"  {Cut(f["label"]?.GetValue<string>() ?? "", 50),-50} ~{Tokens(f["tokens"]?.GetValue<long>() ?? 0)}";
            }
        }
    }

    private static string Tokens(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(long n, long of) => (100.0 * n / Math.Max(1, of)).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Cut(string s, int most) => s.Length <= most ? s : s[..(most - 1)] + "…";

    /// <summary>A text's start on one line, for an item's preview.</summary>
    public static string Preview(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 80 ? line : line[..80] + "…";
    }

    /// <summary>The length of Code Arena's block of what went with a message when it holds nothing.</summary>
    private const int EmptyAttached = 30;

    /// <summary>The skill a call loads, by its arguments' name.</summary>
    private static string? SkillOf(string? arguments)
    {
        try
        {
            return arguments is null ? null : Str(JsonNode.Parse(arguments) as JsonObject, "name");
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A skill's body, as a skill used with a message or loaded by use_skill carries it.</summary>
    [GeneratedRegex("<skill name=\"([^\"]*)\">.*?</skill>", RegexOptions.Singleline)]
    private static partial Regex SkillBody();

    /// <summary>A file attached in the web chat (its name), or what went with a question in Code Arena (its @files and the lines chosen).</summary>
    [GeneratedRegex("<attachment name=\"([^\"]*)\">.*?</attachment>|\n\n<attached>\n.*\n</attached>", RegexOptions.Singleline)]
    private static partial Regex Attachment();
}
