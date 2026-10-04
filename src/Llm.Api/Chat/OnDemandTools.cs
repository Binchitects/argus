using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;

namespace Llm.Api.Chat;

/// <summary>
/// Tools on demand. Every tool's whole definition in every request is thousands of tokens
/// before the question. Past a budget (Chat:ToolTextChars), only the tools the chat loaded go
/// whole; the others are listed in the system prompt, a line per tool, and load_tools loads
/// them. What a chat loads stays loaded (Conversation.LoadedTools), so the prompt's start stays
/// the same from one turn to the next and the engine's cache keeps it.
/// </summary>
public sealed class OnDemandTools
{
    public const string Function = "load_tools";

    private readonly List<(IChatTool Tool, JsonObject Definition, string Name)> _all;
    private readonly List<(string Tool, string Text)> _instructions;
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

    public OnDemandTools(JsonArray functions, IReadOnlyDictionary<string, (ToolChoice Choice, IToolRun Run)> runs, List<(string Tool, string Text)> instructions,
        IEnumerable<string>? loaded, int budgetChars)
    {
        _all = [];
        foreach (var f in functions.OfType<JsonObject>())
        {
            if (f["function"]?["name"]?.GetValue<string>() is { } name && runs.TryGetValue(name, out var run))
            {
                _all.Add((run.Choice.Tool, f, name));
            }
        }
        _instructions = instructions;
        Active = budgetChars > 0 && functions.ToJsonString().Length > budgetChars;
        if (Active)
        {
            Load(loaded ?? []);
        }
    }

    /// <summary>Whether the tools go on demand at all (their definitions are past the budget).</summary>
    public bool Active { get; }

    /// <summary>The functions loaded, by name, in their usual order.</summary>
    public List<string> Loaded => [.. _all.Where(f => _loaded.Contains(f.Name)).Select(f => f.Name)];

    public bool IsLoaded(string function) => !Active || _loaded.Contains(function);

    /// <summary>Loads functions by their own names, or all of a tool's by its id or title; the functions newly loaded, and the names not found.</summary>
    public (List<string> Added, List<string> Unknown) Load(IEnumerable<string> names)
    {
        var added = new List<string>();
        var unknown = new List<string>();
        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            var matched = _all.Where(f => f.Name == name || f.Tool.Id == name || string.Equals(f.Tool.Title, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count == 0)
            {
                unknown.Add(name);
            }
            added.AddRange(matched.Where(f => _loaded.Add(f.Name)).Select(f => f.Name));
        }
        return (added, unknown);
    }

    /// <summary>What a request carries: the loaded tools whole, in their usual order, and load_tools while any is left to load.</summary>
    public JsonArray Request()
    {
        var sent = new JsonArray([.. _all.Where(f => IsLoaded(f.Name)).Select(f => (JsonNode)f.Definition.DeepClone())]);
        if (Active && _all.Any(f => !_loaded.Contains(f.Name)))
        {
            sent.Add(LoadFunction());
        }
        return sent;
    }

    /// <summary>The system prompt's part for the tools: the notes of the tools in use (and of what has no tools), and the list of those to load.</summary>
    public string Notes()
    {
        var used = _all.Where(f => IsLoaded(f.Name)).Select(f => f.Tool.Id).ToHashSet(StringComparer.Ordinal);
        var parts = _instructions.Where(i => used.Contains(i.Tool) || _all.All(f => f.Tool.Id != i.Tool)).Select(i => i.Text).ToList();
        var waiting = _all.Where(f => !IsLoaded(f.Name)).GroupBy(f => f.Tool.Id).ToList();
        if (waiting.Count > 0)
        {
            parts.Add("Tools to load: call load_tools with a tool's name (in brackets) or its functions' names, then call them. Load only what the question needs.\n" +
                string.Join("\n", waiting.Select(g =>
                {
                    var tool = g.First().Tool;
                    var about = tool.Description.Length <= 200 ? tool.Description : tool.Description[..200] + "…";
                    return $"- {tool.Title} [{tool.Id}]: {about} Functions: {string.Join(", ", g.Select(f => f.Name))}.";
                })));
        }
        return string.Join("\n\n", parts);
    }

    private static JsonObject LoadFunction() => Schema.Function(Function,
        "Loads tools from the list under \"Tools to load\" in your instructions, so that you can call them from your next step on. Give tool names (in brackets) or function names.",
        new JsonObject
        {
            ["names"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Tool names or function names." },
        }, "names");
}
