using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Tools' results kept out of the model's window: one too big to go whole, or one cleared to make room before the session
/// compacts. Each is a file beside the session's (its folder sessions/ID.results), named r1, r2…; the model reads any
/// part of one again with read_result. A session with no file of its own (a sub-agent's) keeps none.
/// </summary>
internal static class KeptResults
{
    /// <summary>What of a result kept aside the model reads at once: its start, and its end.</summary>
    public const int HeadChars = 4_000, TailChars = 1_000;

    /// <summary>The most lines read_result gives at once.</summary>
    public const int MostLines = 1_000;

    public static string? Folder(ToolContext c) =>
        c.SessionsDir is { } dir && c.SessionId?.Invoke() is { Length: > 0 } id ? Path.Combine(dir, id + ".results") : null;

    /// <summary>Keeps a result; its id (r1, r2…), or null when this session keeps none.</summary>
    public static string? Keep(ToolContext c, string text)
    {
        if (Folder(c) is not { } folder)
        {
            return null;
        }
        try
        {
            PrivateFiles.EnsureDirectory(folder);
            var n = Directory.EnumerateFiles(folder, "r*.txt").Count() + 1;
            while (File.Exists(Path.Combine(folder, $"r{n}.txt")))
            {
                n++;
            }
            PrivateFiles.WriteAllText(Path.Combine(folder, $"r{n}.txt"), text);
            return $"r{n}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A kept result's text; null when there is none of that id.</summary>
    public static string? Read(ToolContext c, string id)
    {
        if (Folder(c) is not { } folder || id.Length is 0 or > 12 || id[0] != 'r' || !id[1..].All(char.IsAsciiDigit))
        {
            return null;
        }
        var file = Path.Combine(folder, id + ".txt");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>A big result as the model reads it, kept as <paramref name="id"/>: its start and its end, and how to read the rest.</summary>
    public static string Preview(string text, string id)
    {
        var lines = text.Count(ch => ch == '\n') + 1;
        var head = text[..HeadChars];
        var cut = head.LastIndexOf('\n');
        head = cut > HeadChars / 2 ? head[..cut] : head;
        var shownLines = head.Count(ch => ch == '\n') + 1;
        var tail = text[^TailChars..];
        var start = tail.IndexOf('\n');
        tail = start >= 0 && start < TailChars / 2 ? tail[(start + 1)..] : tail;
        return $"{head}\n\n[… {text.Length - head.Length - tail.Length:N0} characters not shown: the whole result ({text.Length:N0} characters, {lines:N0} lines) " +
            $"is kept as {id}. read_result {{\"id\":\"{id}\",\"from\":{shownLines + 1},\"lines\":200}} reads on; {{\"id\":\"{id}\",\"find\":\"text\"}} gives the lines that hold it. Its end:]\n\n{tail}";
    }

    /// <summary>What a result cleared to make room says instead.</summary>
    public static string Cleared(string tool, int chars, string id) =>
        $"[Cleared to make room in the window: {tool}'s result ({chars:N0} characters) is kept as {id}. read_result {{\"id\":\"{id}\"}} reads it again.]";

    /// <summary>Whether a result is one of those notes (cleared already, or a big one's preview): not kept again.</summary>
    public static bool IsKept(string text) => text.StartsWith("[Cleared to make room in the window:", StringComparison.Ordinal) || text.Contains("is kept as r", StringComparison.Ordinal);

    public static ToolDef Tool() => new()
    {
        Name = "read_result",
        Kind = ToolKind.Read,
        Description = "Read a tool's result kept out of the window (one too big to show whole, or one cleared to make room): its lines from a line on, or the lines that hold some text.",
        Parameters = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{
              "id":{"type":"string","description":"The result's id, as its note says (r1, r2…)."},
              "from":{"type":"integer","description":"The first line to read (from 1). Default 1."},
              "lines":{"type":"integer","description":"How many lines (at most 1000). Default 200."},
              "find":{"type":"string","description":"Instead: the lines holding this text (any case), with their numbers."}},
             "required":["id"]}
            """)!,
        Summary = a => (a.Str("id") ?? "") + (a.Str("find") is { } f ? $" find {f}" : a.Int("from") is { } from ? $" from {from}" : ""),
        Run = (a, c, ct) =>
        {
            var id = a.Str("id") ?? "";
            var text = Read(c, id) ?? throw new ToolError($"There is no kept result {id}: the ids are in the notes that say a result is kept.");
            var lines = text.Split('\n');
            if (a.Str("find") is { Length: > 0 } find)
            {
                var hits = lines.Select((l, i) => (l, i)).Where(x => x.l.Contains(find, StringComparison.OrdinalIgnoreCase)).Take(200).ToList();
                var found = hits.Count == 0 ? $"No line of {id} holds \"{find}\"." : string.Join('\n', hits.Select(h => $"{h.i + 1,6}\t{Fmt.OneLine(h.l, 400)}"));
                return Task.FromResult(new ToolResult(found) { Display = c.Ui.Dim($"{hits.Count} lines") });
            }
            var from = Math.Max(1, (int)(a.Int("from") ?? 1));
            var count = Math.Clamp((int)(a.Int("lines") ?? 200), 1, MostLines);
            var sb = new StringBuilder();
            for (var i = from - 1; i < Math.Min(lines.Length, from - 1 + count); i++)
            {
                sb.Append($"{i + 1,6}\t").Append(lines[i]).Append('\n');
            }
            var said = sb.Length == 0 ? $"{id} has {lines.Length:N0} lines." : sb + (from - 1 + count < lines.Length ? $"… {lines.Length - (from - 1 + count):N0} more lines." : "(its end)");
            return Task.FromResult(new ToolResult(said) { Display = c.Ui.Dim($"{id} lines {from}–{Math.Min(lines.Length, from - 1 + count)}") });
        },
    };
}
