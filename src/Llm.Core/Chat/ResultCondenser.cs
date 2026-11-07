using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Llm.Core.Chat;

/// <summary>What a tool does with the path its call names, for the condenser: reads it, changes it, or neither.</summary>
public enum ToolUse
{
    Other,
    Read,
    Write,
}

/// <summary>What one pass of the condenser changed: results summarised, file reads left as their outline, older reads dropped, and the characters freed.</summary>
public readonly record struct Condensed(int Summarised, int Outlined, int Dropped, long Freed)
{
    public int Count => Summarised + Outlined + Dropped;

    public static Condensed operator +(Condensed a, Condensed b) =>
        new(a.Summarised + b.Summarised, a.Outlined + b.Outlined, a.Dropped + b.Dropped, a.Freed + b.Freed);

    /// <summary>The pass in a line for the person ("Summarised 3 earlier tool results…"); empty when it changed nothing.</summary>
    public string Describe(double charsPerToken)
    {
        if (Count == 0)
        {
            return "";
        }
        var parts = new List<string>();
        if (Summarised > 0)
        {
            parts.Add($"summarised {Summarised} earlier tool {(Summarised == 1 ? "result" : "results")}");
        }
        if (Outlined > 0)
        {
            parts.Add($"left {Outlined} file {(Outlined == 1 ? "read" : "reads")} as {(Outlined == 1 ? "its outline" : "their outlines")} and the lines cited");
        }
        if (Dropped > 0)
        {
            parts.Add($"dropped {Dropped} older {(Dropped == 1 ? "read" : "reads")} of files read again");
        }
        var said = string.Join(", ", parts);
        return char.ToUpperInvariant(said[0]) + said[1..] + $" (~{Math.Round(Freed / Math.Max(1.0, charsPerToken)):N0} tokens freed).";
    }
}

/// <summary>
/// Tool results the model has used, made small: shared by Code Arena's agent and Argus Arena's web chat. A result two
/// answers or more behind (the model answered after it, then stepped on) goes as a summary: what was asked, its key lines
/// (paths, line numbers, errors, the figures), and a handle to read it whole again. A file read goes as its outline and
/// the lines the model cited, once the latest steps no longer touch that file. A file read again after it was edited, or
/// read again the same way, drops the older read. It runs before every step, not only near the window's end, and never
/// touches what the model has not answered yet (the step's own results). A pass that would free little changes nothing,
/// so the request's start, which the engine keeps in its cache, does not change for a few characters.
/// </summary>
public sealed partial class ResultCondenser
{
    /// <summary>How a summarised result starts, and how a dropped read does.</summary>
    public const string SummaryMark = "[Summarised once used:", DroppedMark = "[Dropped:";

    /// <summary>The answers that must follow a result before it is summarised: 2, so the step just before keeps its results whole.</summary>
    public int UsedBy { get; init; } = 2;

    /// <summary>A result this long or shorter stays whole.</summary>
    public int SummariseAbove { get; init; } = 1_500;

    /// <summary>A pass that frees fewer characters than this changes nothing (dropped reads always go).</summary>
    public int MinFreed { get; init; } = 3_000;

    /// <summary>The most key lines a summary keeps, and their characters.</summary>
    public int KeyLines { get; init; } = 24;
    public int KeyChars { get; init; } = 2_000;

    /// <summary>The most cited lines a file read keeps, and their characters.</summary>
    public int CitedLines { get; init; } = 40;
    public int CitedChars { get; init; } = 3_000;

    /// <summary>What a tool does with the path it names (read_file reads; edit_file writes).</summary>
    public Func<string, ToolUse> UseOf { get; init; } = _ => ToolUse.Other;

    /// <summary>The path a call names (its "path" or its "file").</summary>
    public Func<JsonObject, string?> PathOf { get; init; } = DefaultPath;

    /// <summary>
    /// Keeps a result whole for the model to read again: the words that say how (Code Arena's "kept as r4: read_result
    /// {"id":"r4"} reads it"); null when it cannot be kept. Called only for the results a pass changes.
    /// </summary>
    public Func<string, string, string, string?>? Keep { get; init; }

    /// <summary>A file read's outline: the path, the lines read (without their numbers) and the first one's number; null: by the lines that look like declarations.</summary>
    public Func<string, string, int, string?>? Outline { get; init; }

    /// <summary>A file read's lines with their numbers in the file; empty when it is not a read of lines. By default as Code Arena numbers them ("    12\tcode").</summary>
    public Func<string, List<(int Line, string Text)>> ReadLines { get; init; } = Numbered;

    /// <summary>Whether a result is already short for another reason (cleared to make room, kept aside): left as it is.</summary>
    public Func<string, bool>? Settled { get; init; }

    public static string? DefaultPath(JsonObject args) =>
        args["path"] is JsonValue p && p.TryGetValue<string>(out var path) ? path
        : args["file"] is JsonValue f && f.TryGetValue<string>(out var file) ? file
        : null;

    /// <summary>Whether a result is one of the condenser's (summarised or dropped).</summary>
    public static bool IsCondensed(string text) => text.StartsWith(SummaryMark, StringComparison.Ordinal) || text.StartsWith(DroppedMark, StringComparison.Ordinal);

    private sealed record Call(string Id, string Name, JsonObject Args, int Assistant, ToolUse Use, string? Path);

    private sealed record Change(int At, string Kind, string Text, Call Call, string Old);

    /// <summary>Condenses <paramref name="messages"/> in place (OpenAI's chat messages); what it changed, and where (<paramref name="changed"/>).</summary>
    public Condensed Condense(IList<JsonObject> messages, List<int>? changed = null)
    {
        // Each result is its call's in the answer before it: ids are not unique across answers (some engines number them from 0).
        var calls = new List<Call>();
        var assistants = new List<int>();
        var results = new List<(int At, Call Call, string Text)>();
        var step = new Dictionary<string, Call>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            var role = Str(messages[i], "role");
            if (role == "tool")
            {
                if (Str(messages[i], "tool_call_id") is { } id && step.TryGetValue(id, out var call) && Str(messages[i], "content") is { } text)
                {
                    results.Add((i, call, text));
                }
                continue;
            }
            if (role != "assistant")
            {
                continue;
            }
            assistants.Add(i);
            step = new Dictionary<string, Call>(StringComparer.Ordinal);
            foreach (var c in (messages[i]["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = Str(c, "id") ?? "";
                var name = Str(c["function"] as JsonObject, "name") ?? "";
                var args = ParseArgs(Str(c["function"] as JsonObject, "arguments"));
                var use = UseOf(name);
                var made = new Call(id, name, args, i, use, use == ToolUse.Other ? null : Normal(PathOf(args)));
                calls.Add(made);
                step[id] = made;
            }
        }
        if (assistants.Count == 0)
        {
            return default;
        }
        var lastAnswer = assistants[^1];
        // The steps the model takes now: a file they touch is not left yet.
        var recent = assistants.Skip(Math.Max(0, assistants.Count - UsedBy)).ToHashSet();
        var touched = calls.Where(c => recent.Contains(c.Assistant) && c.Path is not null).Select(c => c.Path!).ToHashSet(StringComparer.Ordinal);
        var changes = new List<Change>();
        var dropped = new HashSet<int>();
        // A file read again after it changed, or read again the same way: the older read goes, the newer stands.
        foreach (var (at, call, text) in results)
        {
            if (call.Use != ToolUse.Read || call.Path is null || at > lastAnswer || IsCondensed(text) || text.Length < 200 || Error(text))
            {
                continue;
            }
            bool Edited(Call later) => calls.Any(c => c.Use == ToolUse.Write && c.Path == call.Path && c.Assistant > call.Assistant && c.Assistant < later.Assistant);
            var again = results.FirstOrDefault(r => r.At > at && r.Call.Use == ToolUse.Read && r.Call.Path == call.Path && !Error(r.Text) && !IsCondensed(r.Text)
                && (Edited(r.Call) || JsonNode.DeepEquals(call.Args, r.Call.Args)));
            if (again.Call is null)
            {
                continue;
            }
            var edited = Edited(again.Call);
            var note = $"{DroppedMark} {call.Name} of {call.Path} ({text.Length:N0} characters): it was read again {(edited ? "after it was edited" : "the same way")} further on, and that read stands.]";
            changes.Add(new Change(at, "dropped", note, call, text));
            dropped.Add(at);
        }
        // The results the model has used: two answers or more behind.
        var answersAfter = new int[messages.Count + 1];
        for (int i = messages.Count - 1, n = 0; i >= 0; i--)
        {
            answersAfter[i] = n;
            if (Str(messages[i], "role") == "assistant")
            {
                n++;
            }
        }
        foreach (var (at, call, text) in results)
        {
            if (dropped.Contains(at) || answersAfter[at] < UsedBy || text.Length <= SummariseAbove || IsCondensed(text) || Settled?.Invoke(text) == true)
            {
                continue;
            }
            if (call.Use == ToolUse.Read && call.Path is not null)
            {
                if (touched.Contains(call.Path))
                {
                    continue;
                }
                var numbered = ReadLines(text);
                if (numbered.Count >= 3)
                {
                    var later = LaterWords(messages, at);
                    changes.Add(new Change(at, "outlined", ReadDigest(call, text, numbered, later), call, text));
                    continue;
                }
            }
            changes.Add(new Change(at, "summarised", Digest(call, text, LaterWords(messages, at)), call, text));
        }
        var freed = changes.Sum(c => (long)c.Old.Length - c.Text.Length);
        if (changes.Count == 0 || (freed < MinFreed && changes.All(c => c.Kind != "dropped")))
        {
            return default;
        }
        var (summarised, outlined, droppedCount, total) = (0, 0, 0, 0L);
        foreach (var change in changes)
        {
            var text = change.Text;
            if (change.Kind != "dropped")
            {
                var kept = Keep?.Invoke(change.Call.Name, change.Call.Id, change.Old);
                // A result that cannot be read again stays whole, but a file read: the file can be read again.
                if (kept is null && change.Call.Use != ToolUse.Read)
                {
                    continue;
                }
                text = text.Replace(HandleSlot, kept ?? $"{change.Call.Name} reads it again", StringComparison.Ordinal);
            }
            if (text.Length >= change.Old.Length)
            {
                continue;
            }
            messages[change.At]["content"] = text;
            changed?.Add(change.At);
            total += change.Old.Length - text.Length;
            if (change.Kind == "dropped")
            {
                droppedCount++;
            }
            else if (change.Kind == "outlined")
            {
                outlined++;
            }
            else
            {
                summarised++;
            }
        }
        return new Condensed(summarised, outlined, droppedCount, total);
    }

    /// <summary>Where the words that say how to read a result again go, filled once it is kept.</summary>
    private const string HandleSlot = "\u0001handle\u0001";

    /// <summary>The call in a line: its tool and its arguments, long values cut.</summary>
    public static string Asked(string tool, JsonObject args)
    {
        var shown = new JsonObject();
        foreach (var (key, value) in args)
        {
            shown[key] = value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 80 ? s[..77] + "…" : value?.DeepClone();
        }
        var line = shown.ToJsonString(Plain);
        return line == "{}" ? tool : $"{tool} {(line.Length > 240 ? line[..237] + "…" : line)}";
    }

    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A result as its summary: what was asked, how big it was, its key lines with their numbers, and how to read it whole.</summary>
    private string Digest(Call call, string text, string later)
    {
        var echoed = Echoes(later);
        var sb = new StringBuilder();
        if (Flat(text) is { Count: > 0 } fields)
        {
            // JSON (an MCP server's, the web chat's tools'): its fields by their path, the telling ones first, in order.
            var shown = KeyLineNumbers([.. fields], echoed);
            sb.Append(CultureInfo.InvariantCulture, $"{SummaryMark} {Asked(call.Name, call.Args)} gave {text.Length:N0} characters of JSON, {fields.Count:N0} fields; the key ones are below. The whole result: {HandleSlot}.]\n");
            foreach (var i in shown)
            {
                sb.Append(Cut(fields[i], 200)).Append('\n');
            }
            if (shown.Count < fields.Count)
            {
                sb.Append(CultureInfo.InvariantCulture, $"… {fields.Count - shown.Count:N0} more fields\n");
            }
            return sb.ToString().TrimEnd();
        }
        var lines = Lines(text);
        var keep = KeyLineNumbers(lines, echoed);
        sb.Append(CultureInfo.InvariantCulture, $"{SummaryMark} {Asked(call.Name, call.Args)} gave {text.Length:N0} characters in {lines.Length:N0} lines; its key lines are below, by their line in the result. The whole result: {HandleSlot}.]\n");
        Render(sb, lines, keep, numbered: false);
        return sb.ToString().TrimEnd();
    }

    /// <summary>A JSON result's values as "path: value" lines (a first line in brackets, a note before it, is left aside); null when it is not JSON.</summary>
    private static List<string>? Flat(string text)
    {
        var body = text.TrimStart();
        if (body.StartsWith('[') && body.IndexOf('\n') is var nl and > 0 && body[..nl].TrimEnd().EndsWith(']') && !body[..nl].TrimEnd().EndsWith("]]", StringComparison.Ordinal))
        {
            body = body[(nl + 1)..].TrimStart();
        }
        if (!(body.StartsWith('{') || body.StartsWith('[')))
        {
            return null;
        }
        JsonNode? root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = Node(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        var lines = new List<string>();
        void Walk(JsonNode? node, string path)
        {
            if (lines.Count >= 2_000)
            {
                return;
            }
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        Walk(value, path.Length == 0 ? key : $"{path}.{key}");
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        Walk(a[i], $"{path}[{i}]");
                    }
                    break;
                case JsonValue v:
                    var said = v.TryGetValue<string>(out var s) ? s.Replace('\n', ' ') : v.ToJsonString();
                    lines.Add($"{path}: {said}");
                    break;
            }
        }
        Walk(root, "");
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>A file read as its outline and the lines the model cited after it, with their line numbers in the file.</summary>
    private string ReadDigest(Call call, string text, List<(int Line, string Text)> numbered, string later)
    {
        var (first, last) = (numbered[0].Line, numbered[^1].Line);
        var body = string.Join('\n', numbered.Select(n => n.Text));
        var outline = Outline?.Invoke(call.Path!, body, first) ?? Declarations(numbered);
        var cited = Cited(numbered, later);
        // A log's errors stay, cited or not.
        var flagged = numbered.Where(n => Failing().IsMatch(n.Text) && cited.All(c => c.Line != n.Line)).Take(10).ToList();
        cited = [.. cited.Concat(flagged).OrderBy(c => c.Line)];
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{SummaryMark} {Asked(call.Name, call.Args)}: lines {first}-{last} ({text.Length:N0} characters). Below are their outline and the lines cited since; the whole read: {HandleSlot}, or read the file again (it may have changed).]\n");
        if (outline.Trim().Length > 0)
        {
            sb.Append("Outline:\n").Append(outline.TrimEnd()).Append('\n');
        }
        if (cited.Count > 0)
        {
            sb.Append(flagged.Count > 0 ? "Lines cited, and lines that look like errors:\n" : "Lines cited:\n");
            var previous = -1;
            foreach (var (line, said) in cited)
            {
                if (previous >= 0 && line > previous + 1)
                {
                    sb.Append("     …\n");
                }
                sb.Append(CultureInfo.InvariantCulture, $"{line,6}\t{Cut(said, 200)}\n");
                previous = line;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The lines of a result worth keeping: its first, its last two, errors, places (path:line), and figures; by their worth, then in order.</summary>
    private SortedSet<int> KeyLineNumbers(string[] lines, List<Regex> echoed)
    {
        var scored = new List<(int Index, int Score)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }
            var score = Failing().IsMatch(line) ? 4 : 0;
            score += Place().IsMatch(line) ? 3 : PathLike().IsMatch(line) ? 1 : 0;
            score += Figure().IsMatch(line) ? 2 : 0;
            // What was said after it (a figure, a name as code, a quoted phrase): the part of it that was used.
            score += echoed.Any(e => e.IsMatch(line)) ? 4 : 0;
            if (i == 0 || i >= lines.Length - 2)
            {
                score += 5;
            }
            if (score > 0)
            {
                scored.Add((i, score));
            }
        }
        var keep = new SortedSet<int>();
        var chars = 0;
        foreach (var (index, _) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index))
        {
            var length = Math.Min(lines[index].Length, 200) + 8;
            if (keep.Count >= KeyLines || chars + length > KeyChars)
            {
                break;
            }
            keep.Add(index);
            chars += length;
        }
        return keep;
    }

    private static void Render(StringBuilder sb, string[] lines, SortedSet<int> keep, bool numbered)
    {
        var previous = -1;
        foreach (var i in keep)
        {
            if (i > previous + 1)
            {
                sb.Append(CultureInfo.InvariantCulture, $"     … {i - previous - 1:N0} {(i - previous - 1 == 1 ? "line" : "lines")}\n");
            }
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1,6}\t{Cut(lines[i], 200)}\n");
            previous = i;
        }
        if (previous < lines.Length - 1 && !numbered)
        {
            sb.Append(CultureInfo.InvariantCulture, $"     … {lines.Length - 1 - previous:N0} more lines\n");
        }
    }

    /// <summary>The lines of a file read as it numbers them ("    12\tcode"); empty when it is not numbered so.</summary>
    public static List<(int Line, string Text)> Numbered(string text)
    {
        var found = new List<(int, string)>();
        foreach (var line in Lines(text))
        {
            if (NumberedLine().Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                found.Add((n, m.Groups[2].Value));
            }
        }
        return found;
    }

    /// <summary>
    /// What was said and asked after a result, to the end of the exchange after its own (so a summary made later is the
    /// same): the answers' words, the person's next message, and the calls' arguments (an edit's old_string is what it
    /// read), each string value on its own lines.
    /// </summary>
    private static string LaterWords(IList<JsonObject> messages, int at)
    {
        var sb = new StringBuilder();
        var questions = 0;
        for (var i = at + 1; i < messages.Count; i++)
        {
            var role = Str(messages[i], "role");
            if (role == "user" && ++questions > 1)
            {
                break;
            }
            if (role == "user" && Str(messages[i], "content") is { } asked)
            {
                sb.Append(asked).Append('\n');
                continue;
            }
            if (role != "assistant")
            {
                continue;
            }
            if (Str(messages[i], "content") is { } said)
            {
                sb.Append(said).Append('\n');
            }
            foreach (var c in (messages[i]["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var (_, value) in ParseArgs(Str(c["function"] as JsonObject, "arguments")))
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        sb.Append(s).Append('\n');
                    }
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The lines of a read the model cited: by number ("line 42", "a.cs:42", "lines 40-44"), word for word (an edit's
    /// old_string, a quote), or by a name it wrote as code (`Name`, Name() ) on the first line that holds it.
    /// </summary>
    private List<(int Line, string Text)> Cited(List<(int Line, string Text)> numbered, string later)
    {
        if (later.Length == 0)
        {
            return [];
        }
        var byLine = numbered.GroupBy(n => n.Line).ToDictionary(g => g.Key, g => g.First().Text);
        var cited = new SortedSet<int>();
        foreach (Match m in LineNumber().Matches(later))
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var from))
            {
                continue;
            }
            var to = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end) && end >= from ? Math.Min(end, from + 9) : from;
            for (var n = from; n <= to; n++)
            {
                if (byLine.ContainsKey(n))
                {
                    cited.Add(n);
                }
            }
        }
        var said = later.Split('\n').Select(l => l.Trim()).Where(l => l.Length >= 8).ToHashSet(StringComparer.Ordinal);
        foreach (var (line, text) in numbered)
        {
            if (said.Contains(text.Trim()))
            {
                cited.Add(line);
            }
        }
        foreach (var name in CodeName().Matches(later).Select(m => m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value).Distinct(StringComparer.Ordinal).Take(40))
        {
            var pattern = new Regex(@"(?<![\w$])" + Regex.Escape(name) + @"(?![\w$])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            if (numbered.FirstOrDefault(n => pattern.IsMatch(n.Text)) is { Text: not null } hit)
            {
                cited.Add(hit.Line);
            }
        }
        var kept = new List<(int, string)>();
        var chars = 0;
        foreach (var n in cited)
        {
            var text = byLine[n];
            if (text.Trim().Length == 0)
            {
                continue;
            }
            if (kept.Count >= CitedLines || chars + Math.Min(text.Length, 200) > CitedChars)
            {
                break;
            }
            kept.Add((n, text));
            chars += Math.Min(text.Length, 200) + 8;
        }
        return kept;
    }

    /// <summary>
    /// What later words take from a result, as patterns: the figures said (42, 3.5), the names written as code (`Total`,
    /// Total()) and the phrases quoted; at most 40.
    /// </summary>
    private static List<Regex> Echoes(string later)
    {
        var found = new List<string>();
        foreach (Match m in Number().Matches(later))
        {
            found.Add(@"(?<![\w.])" + Regex.Escape(m.Value) + @"(?![\w])");
        }
        foreach (Match m in CodeName().Matches(later))
        {
            found.Add(@"(?<![\w$])" + Regex.Escape(m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value) + @"(?![\w$])");
        }
        foreach (Match m in Quoted().Matches(later))
        {
            found.Add(Regex.Escape(m.Groups[1].Value));
        }
        return [.. found.Distinct(StringComparer.Ordinal).Take(40).Select(p => new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))];
    }

    /// <summary>An outline from the lines that look like declarations, with their numbers.</summary>
    private static string Declarations(List<(int Line, string Text)> numbered)
    {
        var sb = new StringBuilder();
        foreach (var (line, text) in numbered)
        {
            if (sb.Length > 1_200)
            {
                sb.Append("  …\n");
                break;
            }
            if (text.Length < 300 && DeclarationLine().IsMatch(text))
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {Cut(text.Trim(), 120)}  [{line}]\n");
            }
        }
        return sb.ToString();
    }

    private static bool Error(string text) => text.StartsWith("Error", StringComparison.Ordinal) || text.StartsWith("There is no", StringComparison.Ordinal);

    private static string[] Lines(string text) => (text.EndsWith('\n') ? text[..^1] : text).Split('\n');

    private static string Cut(string text, int most) => text.Length <= most ? text.TrimEnd('\r') : text[..most] + "…";

    private static string? Normal(string? path) => path is null ? null : path.Trim().Replace('\\', '/').TrimStart('.', '/') is { Length: > 0 } p ? p : null;

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject ParseArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return Node(doc.RootElement) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A call's arguments as a tree; a key written twice (models do) keeps its last value.</summary>
    private static JsonNode? Node(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Aggregate(new JsonObject(), (o, p) =>
        {
            o[p.Name] = Node(p.Value);
            return o;
        }),
        JsonValueKind.Array => new JsonArray([.. e.EnumerateArray().Select(Node)]),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => JsonValue.Create(e.Clone()),
    };

    [GeneratedRegex(@"^\s{0,8}(\d{1,7})\t(.*)$")]
    private static partial Regex NumberedLine();

    /// <summary>A line that reads as an error or a failure (as the web chat flags them in a long result).</summary>
    [GeneratedRegex(@"(?<!\b0 )\b(?:error|errors|fail|failed|failure|failing|fatal|exception|traceback|panic|assert(?:ion)?(?:error)?|denied|segmentation fault|not found|warning)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Failing();

    /// <summary>A place in a file: path:line, path(line,col), "line N" of a file.</summary>
    [GeneratedRegex(@"[\w./\\-]+\.\w{1,6}(?::\d+|\(\d+(?:,\d+)?\))|\bline \d+", RegexOptions.CultureInvariant)]
    private static partial Regex Place();

    [GeneratedRegex(@"(?:^|[\s'""(])[\w.-]*[/\\][\w./\\-]+|\b[\w-]+\.(?:cs|ts|tsx|js|jsx|py|go|rs|java|kt|cpp|c|h|hpp|json|yaml|yml|toml|md|sql|sh|ps1|csproj|sln)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PathLike();

    /// <summary>A line with the figures of a run: counts of tests, errors and warnings, an exit code, a total, a time, a share.</summary>
    [GeneratedRegex(@"\b\d+\s+(?:passed|failed|skipped|tests?|errors?|warnings?|files?|matches|lines|changed|insertions?|deletions?)\b|\b(?:passed|failed|total|exit code|exit status|errors?|warnings?)\s*[:=]\s*\d+|\b\d+(?:\.\d+)?\s?(?:ms|s|%)\b|\bexit(?:ed)?\b.*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Figure();

    [GeneratedRegex(@"(?:\blines?\s+|[\w.-]\.\w{1,6}:|#L|\bL)(\d{1,7})(?:\s*(?:-|–|to)\s*L?(\d{1,7}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineNumber();

    [GeneratedRegex(@"`(?<a>[A-Za-z_$][\w$.]{2,60})`|\b(?<b>[A-Za-z_$][\w$]{2,60})\(", RegexOptions.CultureInvariant)]
    private static partial Regex CodeName();

    /// <summary>A figure worth finding again: two digits or more, or a decimal.</summary>
    [GeneratedRegex(@"(?<![\w.])\d+[.,]\d+(?![\w])|(?<![\w.])\d{2,}(?![\w])", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"""([^""\n]{6,80})""", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\s{0,12}(?:(?:export|public|private|protected|internal|static|async|pub|final|abstract|virtual|override|sealed|partial|const|default)\s+)*(?:func|fn|def|class|struct|interface|enum|trait|impl|type|module|function|record|namespace)\s+[A-Za-z_]|^\s{0,12}#{1,4}\s+\S|^\s{0,12}(?:public|private|protected|internal)\s+[\w<>\[\],? ]+\s+\w+\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationLine();
}
