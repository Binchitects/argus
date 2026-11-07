using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;

namespace Llm.Api.Chat;

/// <summary>
/// The web chat's tool results the model has used, made small in the request before each step (<see cref="ResultCondenser"/>,
/// as Code Arena does): summarised with their key lines and a handle read_file reads them whole by (result:&lt;call&gt;, or the
/// file a big one was kept as); a read of the chat's files as its outline and the lines cited; a file read again the same way
/// drops the older read. The chat keeps every result whole; the request only is changed. Chat:CondenseResults turns it off.
/// </summary>
public sealed partial class ChatService
{
    /// <summary>The condenser for a chat: <paramref name="canRead"/>, whether the model has read_file to read a result again.</summary>
    public static ResultCondenser Condenser(bool canRead) => new()
    {
        UseOf = name => name == "read_file" ? ToolUse.Read : ToolUse.Other,
        // A result read again by its handle is not a file of the chat's.
        PathOf = args => args["file"] is JsonValue f && f.TryGetValue<string>(out var file) && !file.Trim().StartsWith("result:", StringComparison.Ordinal) ? file.Trim() : null,
        ReadLines = ReadLines,
        Keep = (_, call, text) => KeptFile().Match(text) is { Success: true } kept ? $"it is the file {kept.Groups[1].Value} in this chat, read_file reads it"
            : canRead && call.Length > 0 ? $"read_file with file \"{ResultHandle(call)}\" reads it (search_file finds lines in it)"
            : null,
        Outline = (path, text, first) => FileOutline.Sections(path, text) is { Count: > 0 } sections
            ? FileOutline.Render([.. sections.Select(s => s with { Start = s.Start + first - 1, End = s.End + first - 1 })], 1_200)
            : null,
        Settled = text => text.StartsWith("[Cleared to make room in the context:", StringComparison.Ordinal) || text == LeftOutResult || text.Contains("\n\n[Cut here:", StringComparison.Ordinal),
    };

    /// <summary>
    /// Condenses this request's messages in place; a result's note that it is the web's or the company's content (the
    /// safeguards' line before it) stays first. What it changed.
    /// </summary>
    public static Condensed CondenseResults(JsonArray messages, bool canRead)
    {
        var list = messages.OfType<JsonObject>().ToList();
        var before = list.Select(m => m["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList();
        var changed = new List<int>();
        var done = Condenser(canRead).Condense(list, changed);
        foreach (var i in changed)
        {
            if (before[i] is { } old && UntrustedNote().Match(old) is { Success: true } note && list[i]["content"] is JsonValue v && v.TryGetValue<string>(out var now) && !now.StartsWith(note.Value, StringComparison.Ordinal))
            {
                list[i]["content"] = note.Value + now;
            }
        }
        return done;
    }

    /// <summary>
    /// The model's calls mended in place where what it meant is plain (<see cref="ToolCallRepair"/>): a tool named as
    /// another harness names it, arguments that are not quite JSON. What was mended, by call id.
    /// </summary>
    public static Dictionary<string, string> MendCalls(JsonArray calls, IReadOnlyCollection<string> names)
    {
        var mended = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in calls.OfType<JsonObject>())
        {
            if (call["function"] is not JsonObject function || call["id"]?.GetValue<string>() is not { } id)
            {
                continue;
            }
            var fixes = new List<string>();
            var name = function["name"]?.GetValue<string>() ?? "";
            if (!names.Contains(name) && ToolCallRepair.ToolName(name, names) is { } meant)
            {
                function["name"] = meant;
                fixes.Add($"{name} → {meant}");
            }
            var raw = function["arguments"]?.GetValue<string>() ?? "";
            if (TryArguments(raw) is null && ToolCallRepair.Arguments(raw, out var fix) is { } args)
            {
                function["arguments"] = args.ToJsonString();
                fixes.Add(fix ?? "the JSON mended");
            }
            if (fixes.Count > 0)
            {
                mended[id] = string.Join("; ", fixes);
            }
        }
        return mended;
    }

    /// <summary>The lines of the chat's read_file (its JSON: "text" of "12: line" lines), with their numbers.</summary>
    private static List<(int Line, string Text)> ReadLines(string result)
    {
        var start = result.IndexOf('{');
        if (start < 0)
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(result[start..]);
            if (!doc.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                return [];
            }
            var lines = new List<(int, string)>();
            foreach (var line in (text.GetString() ?? "").Split('\n'))
            {
                if (ReadLine().Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    lines.Add((n, m.Groups[2].Value));
                }
            }
            return lines;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^(\d{1,7}): (.*)$")]
    private static partial Regex ReadLine();

    /// <summary>A big result's note that the whole of it is a file in the chat (<see cref="Oversized"/>).</summary>
    [GeneratedRegex(@"All of it is the file (\S+) in this chat")]
    private static partial Regex KeptFile();

    /// <summary>The safeguards' line before a result from the web or the company's documents (Safeguards.Untrusted).</summary>
    [GeneratedRegex(@"^\[Content from [^\n]*\]\n")]
    private static partial Regex UntrustedNote();
}
