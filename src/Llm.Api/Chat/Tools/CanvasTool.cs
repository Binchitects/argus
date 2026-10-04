using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// The canvas: documents and code beside the chat, which the person edits there too.
/// The model writes one with canvas_create and changes it by exact find and replace
/// (canvas_edit), so a change to one part leaves the rest as it was; canvas_rewrite is
/// for starting over. Every change is a version the person can see and restore.
/// </summary>
public sealed class CanvasTool(Canvases canvases) : IChatTool
{
    /// <summary>What one canvas_read returns at most; the rest from next_from_line.</summary>
    public const int MaxReadChars = 20_000;

    public const string Create = "canvas_create";
    public const string Read = "canvas_read";
    public const string Edit = "canvas_edit";
    public const string Rewrite = "canvas_rewrite";

    public string Id => "canvas";
    public string Title => "Canvas";
    public string Description => "Writes documents and code in a canvas beside the chat, and changes them part by part; you edit them there too.";
    public string Icon => "file-pen";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    private const string Notes =
        "Canvas: write a document or code the person will keep working on (a report, a plan, a letter, a program longer than a snippet) " +
        "in a canvas with canvas_create, not in your answer, then say in a sentence or two what you wrote. The person sees it beside the chat and edits it there. " +
        "Change a canvas with canvas_edit, a part at a time: exact text to find and what replaces it. Never rewrite a whole canvas to change a part " +
        "(canvas_rewrite is only for starting over). The person may have changed a canvas since you saw it: read it with canvas_read before you edit it. " +
        "When the person quotes part of a canvas (its id and lines), change only that part.";

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var chat = context.Conversation.Id;
        // Which canvases the chat has: their ids, titles and kinds only (these change rarely, so the prompt's start stays the same).
        var list = await canvases.ListAsync(chat, ct);
        var notes = list.Count == 0 ? Notes : Notes + "\nThis chat's canvases:\n" + string.Join("\n", list.Select(c => $"- \"{c.Title}\" (id {c.Id}, {Kind(c)})"));
        // Kept short: every request carries these definitions. A node has one parent, so each function gets its own.
        static JsonObject CanvasId() => Schema.Text("Canvas id");
        static JsonObject Said() => Schema.Text("A few words for its version history");
        static JsonObject Line() => new() { ["type"] = "integer" };
        return new LocalRun(
            [
                Schema.Function(Create, "A new canvas beside the chat (Markdown document or code); returns its id.",
                    new JsonObject
                    {
                        ["title"] = Schema.Text("For code, a file name"),
                        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("document", "code") },
                        ["language"] = Schema.Text("For code, e.g. python"),
                        ["content"] = Schema.Text("The whole text"),
                    }, "title", "kind", "content"),
                Schema.Function(Read, "A canvas as it is now (the person may edit it); no id: lists them. Read on from next_from_line.",
                    new JsonObject { ["id"] = CanvasId(), ["from_line"] = Line(), ["to_line"] = Line() }),
                Schema.Function(Edit, "Changes parts of a canvas: each find is exact text found once in it; in turn, all or none.",
                    new JsonObject
                    {
                        ["id"] = CanvasId(),
                        ["edits"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject { ["find"] = Schema.Text("Exact text, once in the canvas"), ["replace"] = Schema.Text("New text") },
                                ["required"] = new JsonArray("find", "replace"),
                            },
                        },
                        ["summary"] = Said(),
                    }, "id", "edits"),
                Schema.Function(Rewrite, "Replaces all its text: only to start over.",
                    new JsonObject { ["id"] = CanvasId(), ["content"] = Schema.Text("The whole text"), ["title"] = Schema.Text("New title") },
                    "id", "content"),
            ],
            notes,
            async (function, args, token) =>
            {
                try
                {
                    return function switch
                    {
                        Create => await CreateAsync(chat, args, token),
                        Read => await ReadAsync(chat, args, token),
                        Edit => await EditAsync(chat, args, token),
                        Rewrite => await RewriteAsync(chat, args, token),
                        _ => new ToolResult($"There is no function {function}.", IsError: true),
                    };
                }
                catch (CanvasException ex)
                {
                    return new ToolResult(ex.Message, IsError: true);
                }
                catch (CanvasConflictException)
                {
                    return new ToolResult("The person changed the canvas at the same moment. Read it again with canvas_read, then make the change on what it is now.", IsError: true);
                }
            });
    }

    private async Task<ToolResult> CreateAsync(Guid chat, JsonObject args, CancellationToken ct)
    {
        var canvas = await canvases.CreateAsync(chat, Schema.Str(args, "title"), Schema.Str(args, "kind"), Schema.Str(args, "language"), Schema.Str(args, "content") ?? "",
            Canvases.Model, ct);
        return Done(canvas, new JsonObject { ["created"] = true });
    }

    private async Task<ToolResult> ReadAsync(Guid chat, JsonObject args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Schema.Str(args, "id")))
        {
            var all = await canvases.ListAsync(chat, ct);
            return new ToolResult(new JsonObject
            {
                ["canvases"] = new JsonArray([.. all.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id.ToString(), ["title"] = c.Title, ["kind"] = c.Kind, ["language"] = c.Language, ["version"] = c.Version, ["lines"] = Canvases.Split(c.Content).Length,
                })]),
            }.ToJsonString(Mcp.Plain));
        }
        var canvas = await FindAsync(chat, args, ct);
        var lines = Canvases.Split(canvas.Content);
        var from = Math.Max(1, Int(args, "from_line") ?? 1);
        var to = Math.Min(lines.Length, Int(args, "to_line") ?? lines.Length);
        if (lines.Length > 0 && from > lines.Length)
        {
            return new ToolResult($"\"{canvas.Title}\" has {lines.Length} lines; line {from} is past its end.", IsError: true);
        }
        var text = new System.Text.StringBuilder();
        var last = from - 1;
        for (var i = from - 1; i < to; i++)
        {
            if (text.Length + lines[i].Length + 1 > MaxReadChars && i > from - 1)
            {
                break;
            }
            text.Append(lines[i]).Append('\n');
            last = i + 1;
        }
        return new ToolResult(new JsonObject
        {
            ["id"] = canvas.Id.ToString(), ["title"] = canvas.Title, ["kind"] = canvas.Kind, ["language"] = canvas.Language, ["version"] = canvas.Version,
            ["total_lines"] = lines.Length, ["from_line"] = from, ["to_line"] = last,
            ["next_from_line"] = last < to ? last + 1 : null,
            ["content"] = text.ToString(),
        }.ToJsonString(Mcp.Plain));
    }

    private async Task<ToolResult> EditAsync(Guid chat, JsonObject args, CancellationToken ct)
    {
        var canvas = await FindAsync(chat, args, ct);
        var edits = Edits(args);
        await canvases.EditAsync(canvas, edits, Canvases.Model, Schema.Str(args, "summary"), ct);
        return Done(canvas, new JsonObject { ["edits"] = edits.Count });
    }

    private async Task<ToolResult> RewriteAsync(Guid chat, JsonObject args, CancellationToken ct)
    {
        var canvas = await FindAsync(chat, args, ct);
        var content = Schema.Str(args, "content") ?? throw new CanvasException("Give the whole new text in \"content\".");
        await canvases.ChangeAsync(canvas, content, Schema.Str(args, "title"), Canvases.Model, "Rewritten", null, ct);
        return Done(canvas, new JsonObject { ["rewritten"] = true });
    }

    /// <summary>What the model reads after a change, and what the page opens (the canvas, at its version).</summary>
    private static ToolResult Done(Canvas canvas, JsonObject what)
    {
        what["id"] = canvas.Id.ToString();
        what["title"] = canvas.Title;
        what["version"] = canvas.Version;
        what["lines"] = Canvases.Split(canvas.Content).Length;
        what["shown_to_the_person"] = true;
        return new ToolResult(what.ToJsonString(Mcp.Plain))
        {
            Details = new JsonObject { ["canvas"] = new JsonObject { ["id"] = canvas.Id.ToString(), ["title"] = canvas.Title, ["version"] = canvas.Version } },
        };
    }

    /// <summary>The canvas by its id (or, as models sometimes give, its title).</summary>
    private async Task<Canvas> FindAsync(Guid chat, JsonObject args, CancellationToken ct)
    {
        var given = (Schema.Str(args, "id") ?? "").Trim();
        Canvas? canvas = null;
        if (Guid.TryParse(given, out var id))
        {
            canvas = await canvases.FindAsync(chat, id, ct);
        }
        else if (given.Length > 0 && (await canvases.ListAsync(chat, ct)).Where(c => string.Equals(c.Title, given, StringComparison.OrdinalIgnoreCase)).ToList() is [var only])
        {
            canvas = await canvases.FindAsync(chat, only.Id, ct);
        }
        if (canvas is not null)
        {
            return canvas;
        }
        var all = await canvases.ListAsync(chat, ct);
        throw new CanvasException(all.Count == 0
            ? "This chat has no canvas yet: make one with canvas_create."
            : $"There is no canvas '{given}' in this chat. Its canvases: {string.Join(", ", all.Select(c => $"\"{c.Title}\" (id {c.Id})"))}.");
    }

    /// <summary>The edits as the model wrote them: a list (or a list written as a string), or one find and replace at the top.</summary>
    private static List<CanvasEdit> Edits(JsonObject args)
    {
        var node = args["edits"];
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.TrimStart().StartsWith('['))
        {
            try
            {
                node = JsonNode.Parse(s);
            }
            catch (JsonException)
            {
                throw new CanvasException("\"edits\" is not a valid list: give [{\"find\": \"…\", \"replace\": \"…\"}].");
            }
        }
        var edits = new List<CanvasEdit>();
        if (node is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject e || Schema.Str(e, "find") is not { } find)
                {
                    throw new CanvasException("Each edit is {\"find\": exact text in the canvas, \"replace\": what replaces it}.");
                }
                edits.Add(new CanvasEdit(find, Schema.Str(e, "replace") ?? ""));
            }
        }
        else if (Schema.Str(args, "find") is { } find)
        {
            edits.Add(new CanvasEdit(find, Schema.Str(args, "replace") ?? ""));
        }
        return edits;
    }

    private static string Kind(Canvas c) => c.Kind == "code" ? $"code{(c.Language is { } l ? ", " + l : "")}" : "document";

    private static int? Int(JsonObject args, string name) => args[name] switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
}
