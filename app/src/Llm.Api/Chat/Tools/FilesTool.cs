using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// The chat's files, read in parts: a long attachment goes to the model only up to
/// a budget, and this lets it read on (by lines) or find the parts it needs.
/// Files the tools made (a CSV from Python) are read the same way.
/// </summary>
public sealed class FilesTool(AppDbContext db) : IChatTool
{
    /// <summary>Lines one read returns at most, and characters (a line is cut at <see cref="MaxLineChars"/>).</summary>
    public const int MaxLines = 400;
    public const int MaxChars = 24_000;
    private const int MaxLineChars = 2_000;
    private const int MaxMatches = 40;

    public string Id => "files";
    public string Title => "Reading files";
    public string Description => "Reads the chat's files in parts, and searches them: for files longer than what fits in one message.";
    public string Icon => "file-text";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function("list_files", "Lists the files in this chat: the person's attachments and files tools made, with their length in lines.", new JsonObject()),
            Schema.Function("read_file", $"Reads lines of a file in this chat, numbered. Up to {MaxLines} lines a call; read on from next_from_line.",
                new JsonObject
                {
                    ["file"] = Schema.Text("The file's name, as list_files shows it"),
                    ["from_line"] = new JsonObject { ["type"] = "integer", ["description"] = "First line to read, from 1 (default 1)" },
                    ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = $"How many lines (default 200, at most {MaxLines})" },
                }, "file"),
            Schema.Function("search_file", "Finds the lines of a file that contain some text (case does not matter), with their numbers, to read around them with read_file.",
                new JsonObject { ["file"] = Schema.Text("The file's name"), ["text"] = Schema.Text("The text to find") }, "file", "text"),
        ],
        "The person's files may be longer than what you were shown: a note under a file says so. Then read on with read_file, " +
        "or find what you need with search_file, before answering from the part you saw. Say which lines you used.",
        async (function, args, token) =>
        {
            var files = await FilesAsync(context.Conversation.Id, token);
            if (function == "list_files")
            {
                return new ToolResult(new JsonObject
                {
                    ["files"] = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject
                    {
                        ["file"] = f.Name, ["lines"] = f.Lines.Length, ["characters"] = f.Text.Length, ["cut_when_attached"] = f.Truncated,
                    })]),
                }.ToJsonString(Mcp.Plain));
            }
            var name = Schema.Str(args, "file") ?? "";
            if (Find(files, name) is not { } file)
            {
                return new ToolResult(files.Count == 0
                    ? "This chat has no files to read."
                    : $"There is no file '{name}' in this chat. Its files: {string.Join(", ", files.Select(f => f.Name))}.", IsError: true);
            }
            return function switch
            {
                "read_file" => Read(file, Int(args, "from_line") ?? 1, Int(args, "lines") ?? 200),
                "search_file" => Search(file, Schema.Str(args, "text") ?? ""),
                _ => new ToolResult($"There is no function {function}.", IsError: true),
            };
        }));

    public sealed record ChatFile(string Name, string Text, string[] Lines, bool Truncated);

    /// <summary>The conversation's readable files, oldest first; a repeated name gets " (2)".</summary>
    public async Task<IReadOnlyList<ChatFile>> FilesAsync(Guid conversationId, CancellationToken ct)
    {
        var lists = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.AttachmentsJson != null)
            .OrderBy(m => m.Sequence).Select(m => m.AttachmentsJson).ToListAsync(ct);
        var ids = lists.SelectMany(ChatService.ParseIds).Distinct().ToList();
        var found = await db.ChatAttachments.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.Kind != "image" && a.Text != "")
            .Select(a => new { a.Id, a.FileName, a.Text, a.Truncated }).ToDictionaryAsync(a => a.Id, ct);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ChatFile>();
        foreach (var id in ids.Where(found.ContainsKey))
        {
            var a = found[id];
            var n = seen[a.FileName] = seen.GetValueOrDefault(a.FileName) + 1;
            var name = n == 1 ? a.FileName : $"{Path.GetFileNameWithoutExtension(a.FileName)} ({n}){Path.GetExtension(a.FileName)}";
            // A last newline ends the last line; it does not start another.
            files.Add(new ChatFile(name, a.Text, (a.Text.EndsWith('\n') ? a.Text[..^1] : a.Text).Split('\n'), a.Truncated));
        }
        return files;
    }

    /// <summary>By name, exactly; else ignoring case; else the one name that contains it.</summary>
    private static ChatFile? Find(IReadOnlyList<ChatFile> files, string name)
    {
        name = name.Trim();
        return files.LastOrDefault(f => f.Name == name)
            ?? files.LastOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (files.Where(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] && name.Length > 0 ? only : null);
    }

    private static ToolResult Read(ChatFile file, int from, int count)
    {
        var total = file.Lines.Length;
        from = Math.Max(1, from);
        if (from > total)
        {
            return new ToolResult($"{file.Name} has {total} lines; line {from} is past its end.", IsError: true);
        }
        count = Math.Clamp(count, 1, MaxLines);
        var text = new System.Text.StringBuilder();
        var last = from - 1;
        for (var i = from - 1; i < Math.Min(total, from - 1 + count); i++)
        {
            var line = file.Lines[i];
            var shown = line.Length > MaxLineChars ? line[..MaxLineChars] + " […]" : line;
            if (text.Length + shown.Length > MaxChars && i > from - 1)
            {
                break;
            }
            text.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(": ").Append(shown).Append('\n');
            last = i + 1;
        }
        return new ToolResult(new JsonObject
        {
            ["file"] = file.Name, ["from_line"] = from, ["to_line"] = last, ["total_lines"] = total,
            ["next_from_line"] = last < total ? last + 1 : null,
            ["cut_when_attached"] = file.Truncated && last == total ? true : null,
            ["text"] = text.ToString(),
        }.ToJsonString(Mcp.Plain));
    }

    private static ToolResult Search(ChatFile file, string needle)
    {
        needle = needle.Trim();
        if (needle.Length == 0)
        {
            return new ToolResult("Say what to find in 'text'.", IsError: true);
        }
        var matches = new JsonArray();
        var count = 0;
        for (var i = 0; i < file.Lines.Length; i++)
        {
            var line = file.Lines[i];
            var at = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }
            if (++count <= MaxMatches)
            {
                // The part of a long line around the match.
                var start = Math.Max(0, at - 120);
                var end = Math.Min(line.Length, at + needle.Length + 180);
                matches.Add(new JsonObject { ["line"] = i + 1, ["text"] = (start > 0 ? "…" : "") + line[start..end] + (end < line.Length ? "…" : "") });
            }
        }
        return new ToolResult(new JsonObject
        {
            ["file"] = file.Name, ["matches"] = matches, ["total_matches"] = count,
            ["more"] = count > MaxMatches ? $"{count - MaxMatches} more; search for something more precise" : null,
        }.ToJsonString(Mcp.Plain));
    }

    private static int? Int(JsonObject args, string name) => args[name] switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
}
