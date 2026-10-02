using System.Text.Json.Nodes;

namespace Llm.Api.Chat.Tools;

/// <summary>A part of a task for a sub-agent: what to call it, and what to do.</summary>
public sealed record AgentTask(string Title, string Instructions);

/// <summary>
/// Sub-agents: the model splits a task into parts that do not need each other (read
/// three repositories, compare two designs), each part is done by an agent of its own
/// (a clean context, the chat's tools, in parallel up to Agents:Parallel), and their
/// results come back to the model to put together. The chat runs them (it has the
/// answer's model and tools): this tool only offers the function and checks the parts.
/// </summary>
public sealed class AgentsTool : IChatTool
{
    public const string Function = "delegate";
    public const int MaxParts = 10;

    public string Id => "agents";
    public string Title => "Sub-agents";
    public string Description => "The model splits a task into parts that sub-agents do side by side, each with its own tools, then puts their results together.";
    public string Icon => "network";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    private const string Instructions =
        "When a task has parts that do not depend on each other and each needs real work with tools (look into several repositories, files, " +
        "sites or questions), call delegate with one part per sub-agent instead of doing them one after another. Each sub-agent starts with " +
        "nothing but its instructions and has your tools (not delegate): write each part so it stands alone, and say what to return. Then put " +
        "their results together in your answer. Do not delegate a simple question, or one part.";

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = Function,
                    ["description"] = $"Runs 2 to {MaxParts} independent parts of a task at once, each by a sub-agent with your tools. Returns each part's result.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["tasks"] = new JsonObject
                            {
                                ["type"] = "array", ["minItems"] = 2, ["maxItems"] = MaxParts,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["title"] = Schema.Text("A few words naming the part, e.g. \"Auth service data flow\"."),
                                        ["instructions"] = Schema.Text("Everything the sub-agent needs: what to find out or do, where, and what to return."),
                                    },
                                    ["required"] = new JsonArray("title", "instructions"),
                                },
                            },
                        },
                        ["required"] = new JsonArray("tasks"),
                    },
                },
            },
        ],
        Instructions,
        async (function, args, token) =>
        {
            if (function != Function || context.Agents is not { } run)
            {
                return new ToolResult($"There is no function {function}.", IsError: true);
            }
            return Parts(args) is { } parts ? await run(parts, token) : new ToolResult(
                $"Give 2 to {MaxParts} tasks, each with a title and instructions: {{\"tasks\":[{{\"title\":\"…\",\"instructions\":\"…\"}},{{\"title\":\"…\",\"instructions\":\"…\"}}]}}.",
                IsError: true);
        }));

    internal static IReadOnlyList<AgentTask>? Parts(JsonObject args)
    {
        if (args["tasks"] is not JsonArray list || list.Count is < 2 or > MaxParts)
        {
            return null;
        }
        var parts = list.OfType<JsonObject>()
            .Select(t => new AgentTask(Schema.Str(t, "title")?.Trim() ?? "", Schema.Str(t, "instructions")?.Trim() ?? ""))
            .ToList();
        return parts.Count == list.Count && parts.All(p => p.Title.Length > 0 && p.Instructions.Length > 0) ? parts : null;
    }
}
