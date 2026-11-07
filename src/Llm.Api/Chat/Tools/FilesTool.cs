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
            Schema.Function("read_file", $"Reads lines of a file in this chat, numbered: from a line, or one section of its outline (a heading, a page, a function) by name. Up to {MaxLines} lines a call; read on from next_from_line.",
                new JsonObject
                {
                    ["file"] = Schema.Text("The file's name, as list_files shows it; or an earlier tool result's handle (result:...), as the note under it says"),
                    ["from_line"] = new JsonObject { ["type"] = "integer", ["description"] = "First line to read, from 1 (default 1)" },
                    ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = $"How many lines (default 200, at most {MaxLines})" },
                    ["section"] = Schema.Text("Instead of from_line: a section's name as the file's outline shows it (a heading, page 3, a function's name)"),
                }, "file"),
            Schema.Function("outline_file", "The outline of a file in this chat: its sections (headings, pages, functions and classes) with their lines, to read only the part needed.",
                new JsonObject { ["file"] = Schema.Text("The file's name") }, "file"),
            Schema.Function("search_file", "Finds the lines of a file that contain some text (case does not matter), with their numbers, to read around them with read_file.",
                new JsonObject { ["file"] = Schema.Text("The file's name"), ["text"] = Schema.Text("The text to find") }, "file", "text"),
        ],
        "The person's files may be longer than what you were shown: a note under a file says so, with its outline. Then read the " +
        "section you need with read_file, or find what you need with search_file, before answering from the part you saw. Say which lines you used.",
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
            // An earlier tool result of this chat, by the handle its cut note gives (result:<its call's id>).
            var file = name.Trim().StartsWith("result:", StringComparison.Ordinal) ? await ResultAsync(context.Conversation.Id, name.Trim(), token) : Find(files, name);
            if (file is null && name.Trim().StartsWith("result:", StringComparison.Ordinal))
            {
                return new ToolResult($"There is no tool result {name.Trim()} in this chat: the handle is in the note under a result that was cut.", IsError: true);
            }
            if (file is null)
            {
                return new ToolResult(files.Count == 0
                    ? "This chat has no files to read."
                    : $"There is no file '{name}' in this chat. Its files: {string.Join(", ", files.Select(f => f.Name))}.", IsError: true);
            }
            return function switch
            {
                "read_file" when Schema.Str(args, "section") is { Length: > 0 } section => ReadSection(file, section),
                "read_file" => Read(file, Int(args, "from_line") ?? 1, Int(args, "lines") ?? 200),
                "outline_file" => Outline(file),
                "search_file" => Search(file, Schema.Str(args, "text") ?? ""),
                _ => new ToolResult($"There is no function {function}.", IsError: true),
            };
        }));

    public sealed record ChatFile(string Name, string Text, string[] Lines, bool Truncated);

    /// <summary>A tool result of the conversation, whole as the chat keeps it, read as a file by its handle; null when there is none.</summary>
    private async Task<ChatFile?> ResultAsync(Guid conversationId, string handle, CancellationToken ct)
    {
        var call = handle["result:".Length..];
        var text = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId && m.Role == "tool" && m.ToolCallId == call)
            .OrderByDescending(m => m.Sequence).Select(m => m.Content).FirstOrDefaultAsync(ct);
        return text is null ? null : new ChatFile(handle, text, (text.EndsWith('\n') ? text[..^1] : text).Split('\n'), false);
    }

    /// <summary>The conversation's readable files, oldest first; a repeated name gets " (2)".</summary>
    public async Task<IReadOnlyList<ChatFile>> FilesAsync(Guid conversationId, CancellationToken ct)
    {
        var ids = await ChatService.FileIdsAsync(db, conversationId, ct);
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

    private static ToolResult Outline(ChatFile file)
    {
        var outline = FileOutline.Render(FileOutline.Sections(file.Name, file.Text), MaxChars);
        return new ToolResult(new JsonObject
        {
            ["file"] = file.Name, ["total_lines"] = file.Lines.Length,
            ["outline"] = outline.Length > 0 ? outline : "No sections found in it (no headings, pages or functions): read it by lines, or find words with search_file.",
        }.ToJsonString(Mcp.Plain));
    }

    /// <summary>One section of the file's outline by its name: its lines (the first <see cref="MaxLines"/> of a longer one, and where to read on).</summary>
    private static ToolResult ReadSection(ChatFile file, string name)
    {
        var sections = FileOutline.Sections(file.Name, file.Text);
        if (FileOutline.Find(sections, name, out var candidates) is not { } section)
        {
            return new ToolResult(candidates.Count == 0
                ? $"{file.Name} has no section '{name}'" + (sections.Count > 0 ? $"; its sections: {string.Join("; ", sections.Take(30).Select(s => s.Name))}." : ": it has no outline; read it by lines.")
                : $"'{name}' is {candidates.Count} sections of {file.Name}: {string.Join("; ", candidates.Take(20).Select(s => $"{s.Name} [lines {s.Start}-{s.End}]"))}. Name one, or read by from_line.", IsError: true);
        }
        return Read(file, section.Start, section.End - section.Start + 1);
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
