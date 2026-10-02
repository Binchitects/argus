using System.Text.Json.Nodes;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Questions for the person, as Claude asks them: the model puts a few questions
/// with choices to the person instead of guessing, and its answer ends there. The
/// page shows them as a card (pick one or several, or write your own) and sends
/// the choices as the person's next message, so nothing waits on the server, and
/// a reply days later carries on the same.
/// </summary>
public sealed class AskTool : IChatTool
{
    public const string Function = "ask_user";

    public string Id => "ask";
    public string Title => "Questions for you";
    public string Description => "The model asks you a few questions with choices when it needs your decision, instead of guessing.";
    public string Icon => "message-circle-question";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    private const string Instructions =
        "When the request leaves a choice open that changes the result (which option, which scope, which format), call ask_user " +
        "with 1 to 4 short questions, each with 2 to 6 choices, instead of guessing. The person may also write their own answer. " +
        "Do not ask what your other tools can find out. After calling ask_user, stop: say nothing more; the answers come as the person's next message.";

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = Function,
                    ["description"] = "Asks the person 1 to 4 questions, each with 2 to 6 choices (they may also write their own). Ends your turn: the answers come as their next message.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["questions"] = new JsonObject
                            {
                                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4,
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["question"] = Schema.Text("The question, ending with a question mark."),
                                        ["options"] = new JsonObject
                                        {
                                            ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 6,
                                            ["items"] = new JsonObject
                                            {
                                                ["type"] = "object",
                                                ["properties"] = new JsonObject
                                                {
                                                    ["label"] = Schema.Text("The choice, in a few words."),
                                                    ["description"] = Schema.Text("Optional: what choosing it means."),
                                                },
                                                ["required"] = new JsonArray("label"),
                                            },
                                        },
                                        ["multiple"] = new JsonObject { ["type"] = "boolean", ["description"] = "True when the person may pick several." },
                                    },
                                    ["required"] = new JsonArray("question", "options"),
                                },
                            },
                        },
                        ["required"] = new JsonArray("questions"),
                    },
                },
            },
        ],
        Instructions,
        (function, args, _) => Task.FromResult(function == Function ? Ask(args) : new ToolResult($"There is no function {function}.", IsError: true))));

    /// <summary>Checks the questions (a small model gets the shape wrong now and then, and is told how) and ends the answer.</summary>
    internal static ToolResult Ask(JsonObject args)
    {
        if (Problem(args) is { } problem)
        {
            return new ToolResult(problem + " Call ask_user again with {\"questions\":[{\"question\":\"…?\",\"options\":[{\"label\":\"…\"},{\"label\":\"…\"}]}]}.", IsError: true);
        }
        return new ToolResult("The questions are shown to the person. Stop here: their answers come as their next message.", EndsAnswer: true);
    }

    private static string? Problem(JsonObject args)
    {
        if (args["questions"] is not JsonArray questions || questions.Count is < 1 or > 4)
        {
            return "Give 1 to 4 questions.";
        }
        foreach (var q in questions)
        {
            if (q is not JsonObject question || Schema.Str(question, "question") is not { Length: > 0 })
            {
                return "Each question needs its text.";
            }
            if (question["options"] is not JsonArray options || options.Count is < 2 or > 6)
            {
                return "Each question needs 2 to 6 options.";
            }
            // A plain string is a label too: what a small model often writes.
            if (options.Any(o => (o is JsonObject x ? Schema.Str(x, "label") : o is JsonValue v && v.TryGetValue<string>(out var s) ? s : null) is not { Length: > 0 }))
            {
                return "Each option needs a label.";
            }
        }
        return null;
    }
}
