using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Memory: the model keeps short facts about the person across their chats. What the person
/// asks it to remember ("remember that I deploy with Podman") is kept at once; what the model
/// offers on its own waits for the person, who keeps it or not on the card in the chat (nothing
/// waits on the server meanwhile). The card's state is the call's details (see MemoryEndpoints).
/// </summary>
public sealed partial class MemoryTool(Memories memories, AppDbContext db) : IChatTool
{
    public const string Function = "remember";

    /// <summary>A memory the model offered, waiting for the person.</summary>
    public const string Offered = "offered";
    /// <summary>Kept: in every answer from now on.</summary>
    public const string Kept = "kept";
    /// <summary>Offered, and the person said no.</summary>
    public const string Declined = "declined";
    /// <summary>Kept, then taken back from the card.</summary>
    public const string Forgotten = "forgotten";

    public string Id => "memory";
    public string Title => "Memory";
    public string Description => "Remembers what you ask it to across your chats, and offers to remember what would help later: you choose what it keeps (Your account → Memory).";
    public string Icon => "brain";

    public Task<string?> UnavailableAsync(CancellationToken ct) =>
        Task.FromResult(memories.Enabled ? null : "Memory is off for everyone here (Settings → Chat → Memory).");

    private const string Instructions =
        "You can remember things about the person across their chats with remember. Call it when they ask you to remember something, " +
        "or when they tell you a lasting fact or preference that would help in later chats (their role, team, projects, tools, how they " +
        "like answers); what they did not ask for is offered to them, and kept only if they accept. One short sentence each, about them " +
        "(\"Works on the payments team.\"). Never remember secrets, passwords or keys, health, or other people's personal details, nor what " +
        "matters only to this chat. What you remember already is in your instructions: do not remember it again.";

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(!memories.On(context.User)
        // Turned off by the person: no function, no word of it.
        ? new LocalRun([], null, (function, _, _) => Task.FromResult(new ToolResult($"There is no function {function}.", IsError: true)))
        : new LocalRun(
            [
                Schema.Function(Function,
                    "Remembers one short fact about the person for their later chats. Kept at once when they asked you to remember it; otherwise offered to them.",
                    new JsonObject { ["memory"] = Schema.Text("One short sentence about the person, e.g. \"Works on the payments team.\"") }, "memory"),
            ],
            Instructions,
            (function, args, token) => function == Function
                ? RememberAsync(context, args, token)
                : Task.FromResult(new ToolResult($"There is no function {function}.", IsError: true))));

    private async Task<ToolResult> RememberAsync(ToolContext context, JsonObject args, CancellationToken ct)
    {
        var (text, problem) = Memories.Check(Schema.Str(args, "memory"));
        if (problem is not null)
        {
            return new ToolResult(problem + " Call remember again with {\"memory\":\"…\"}.", IsError: true);
        }
        var userId = context.User.Id;
        if (await memories.FindAsync(userId, text, ct) is { } same)
        {
            await memories.KeepAsync(userId, same.Text, ct);
            return new ToolResult($"Already remembered: {same.Text}") { Details = Card(same.Id, same.Text, Kept) };
        }
        // A task's chat only offers: its question may carry an event's words (an issue, a webhook's JSON), never the person's own.
        if (context.Conversation.ScheduledTaskId is not null || !AskedToRemember().IsMatch(await QuestionAsync(context.Conversation, ct)))
        {
            return new ToolResult("Offered to the person: it is kept only if they accept it, on its card in the chat. Go on with your answer; do not wait for it.")
            {
                Details = Card(null, text, Offered),
            };
        }
        if (await memories.KeepAsync(userId, text, ct) is not { } kept)
        {
            return new ToolResult($"Not remembered: the person keeps {Memories.PerPerson} memories already. Tell them to delete some in Your account → Memory.", IsError: true);
        }
        return new ToolResult($"Remembered: {kept.Text}") { Details = Card(kept.Id, kept.Text, Kept) };
    }

    /// <summary>What the memory card in the chat shows: the memory, its id once kept, and where it stands.</summary>
    public static JsonObject Card(Guid? id, string text, string state) =>
        new() { ["memory"] = new JsonObject { ["id"] = id?.ToString(), ["text"] = text, ["state"] = state } };

    /// <summary>The question being answered: up the branch from the step the answer is at.</summary>
    private async Task<string> QuestionAsync(Conversation chat, CancellationToken ct)
    {
        var steps = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == chat.Id)
            .Select(m => new { m.Id, m.ParentId, m.Role, Content = m.Role == "user" ? m.Content : "" })
            .ToDictionaryAsync(m => m.Id, ct);
        var seen = new HashSet<Guid>();
        for (var id = chat.CurrentLeafId; id is { } at && seen.Add(at) && steps.TryGetValue(at, out var step); id = step.ParentId)
        {
            if (step.Role == "user")
            {
                return step.Content;
            }
        }
        return "";
    }

    /// <summary>The person's own words asking to remember: then what the model remembers is kept without asking them again.</summary>
    [GeneratedRegex(@"\b(remember|memori[sz]e)\b|\b(do not|don[’']?t|never) forget\b|\bkeep (this |that |it )?in mind\b", RegexOptions.IgnoreCase)]
    private static partial Regex AskedToRemember();
}
