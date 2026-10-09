using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Deep research, as a tool admins give to people (Admin → Tools): only those who may use it get
/// the composer's Deep research. On in a chat, the model may start one itself (deep_research),
/// asking the person first unless an admin turned that off; the chat then makes the answer itself
/// the research (ChatService), its parts and report as with the composer's switch. For an outside
/// agent, Arena MCP runs one in a chat of the person's own and returns the report (McpResearch).
/// </summary>
public sealed class ResearchTool : IChatTool
{
    public const string ToolId = "research";
    public const string Function = "deep_research";

    /// <summary>A message asked with Deep research by someone who may not use it.</summary>
    public const string NotYours = "Deep research is not available to you: an admin decides who may use it (Admin → Tools). Send it as a plain message.";

    public string Id => ToolId;
    public string Title => "Deep research";
    public string Description =>
        "A plan, sub-agents that search the web, and a report with its sources; it takes minutes. Deep research in the message box starts one; " +
        "on in a chat, the model may start one itself.";
    public string Icon => "telescope";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>For everyone, as before it was a tool; the model asks the person before it starts one.</summary>
    public ToolSetting Defaults() => new() { ToolId = Id, AskFirst = true };

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        if (context.NoResearch is { } why)
        {
            throw new McpException(why);
        }
        var functions = new JsonArray(Schema.Function(Function,
            "Starts deep research on a question that needs many sources (a comparison, a market, the state of a field, recent changes): " +
            "a plan, sub-agents that search the web, and a report with numbered sources. It takes minutes: use it only when a thorough, " +
            "sourced answer is wanted, not for a quick question.",
            new JsonObject { ["question"] = Schema.Text("What to research, in full: the question and what the report should cover") }, "question"));
        return Task.FromResult<IToolRun>(new LocalRun(functions, null, async (function, args, token) =>
        {
            if (function != Function)
            {
                return new ToolResult($"There is no function {function}.", IsError: true);
            }
            if (Question(args) is not { } question)
            {
                return NoQuestion;
            }
            return context.Research is { } run
                ? await run(question, token)
                : new ToolResult("Deep research runs in a chat's answer, or for an agent at Arena MCP.", IsError: true);
        }));
    }

    /// <summary>The question a deep_research call asked; null when it gave none.</summary>
    internal static string? Question(JsonObject args) => Schema.Str(args, "question")?.Trim() is { Length: > 0 } q ? q : null;

    internal static readonly ToolResult NoQuestion = new("Say what to research in 'question'.", IsError: true);
}
