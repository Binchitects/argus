using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// A long chat's own history. Once a chat has compacted, the messages its summary stands for are out of the model's
/// window but kept: chat_search finds them by their words, chat_read reads them again by their numbers (#N, their place
/// on the chat's branch from 1, as the summary cites them). Not a tool people choose: every answer of a chat that has
/// compacted has it, so a chat can go on for thousands of messages and still find what was said at its start.
/// </summary>
public sealed class ChatHistoryTool(AppDbContext db) : IChatTool
{
    public const string ToolId = "chat_history", SearchFunction = "chat_search", ReadFunction = "chat_read";

    /// <summary>The most messages chat_search finds, and chat_read reads at once.</summary>
    public const int MostFound = 12, MostRead = 20;

    /// <summary>What of a message chat_read gives (a tool's answer less), and of all it reads at once.</summary>
    public const int MessageChars = 3_000, ToolChars = 1_500, ReadChars = 16_000;

    public string Id => ToolId;
    public string Title => "This chat's history";
    public string Description => "Finds and reads again what was said earlier in a long chat, after its older messages became a summary.";
    public string Icon => "history";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    private const string Instructions =
        "This chat is long: its older messages became the summary above, and are kept. When the summary leaves out what you need " +
        "(an exact number, a name, code, what the person said word for word), find it with chat_search, then read it with chat_read " +
        "by the message numbers the summary or the search gives (#212). Do not guess what was said: look.";

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function(SearchFunction,
                "Finds the earlier messages of this chat that hold some words: each with its number, who said it and the words around them, the best first.",
                new JsonObject { ["query"] = Schema.Text("The words to find: a name, a number, a term, a few words of what was said.") }, "query"),
            Schema.Function(ReadFunction,
                "Reads this chat's messages again by their numbers (#N), word for word: up to 20 at once.",
                new JsonObject
                {
                    ["from"] = new JsonObject { ["type"] = "integer", ["description"] = "The first message's number." },
                    ["to"] = new JsonObject { ["type"] = "integer", ["description"] = "The last message's number (default: 5 after the first)." },
                }, "from"),
        ],
        Instructions,
        (function, args, token) => function switch
        {
            SearchFunction => SearchAsync(context, args, token),
            ReadFunction => ReadAsync(context, args, token),
            _ => Task.FromResult(new ToolResult($"There is no function {function}.", IsError: true)),
        }));

    /// <summary>A chat's branch to its leaf (as an answer reads it), from its first message.</summary>
    internal static async Task<List<ChatMessage>> BranchAsync(AppDbContext db, Conversation chat, CancellationToken ct)
    {
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == chat.Id).ToDictionaryAsync(m => m.Id, ct);
        return chat.CurrentLeafId is { } leaf && all.ContainsKey(leaf) ? ChatService.PathTo(all, leaf) : [];
    }

    /// <summary>What of a message is searched and read: its words, its calls' names and arguments.</summary>
    private static string Said(ChatMessage m)
    {
        if (m.Role != "assistant" || m.ToolCallsJson is null || JsonNode.Parse(m.ToolCallsJson) is not JsonArray calls)
        {
            return m.Content;
        }
        var text = new StringBuilder(m.Content);
        foreach (var call in calls.OfType<JsonObject>())
        {
            text.Append(text.Length > 0 ? "\n" : "").Append("called ").Append(call["function"]?["name"]?.GetValue<string>())
                .Append('(').Append(call["function"]?["arguments"]?.GetValue<string>()).Append(')');
        }
        return text.ToString();
    }

    private static string Who(ChatMessage m) => m.Role switch
    {
        "user" => "the person",
        "tool" => $"{m.ToolName ?? "a tool"}'s answer",
        _ => "you",
    };

    private async Task<ToolResult> SearchAsync(ToolContext context, JsonObject args, CancellationToken ct)
    {
        var query = Schema.Str(args, "query")?.Trim() ?? "";
        return HistoryWords.Terms(query).Count == 0
            ? new ToolResult("Say which words to find: {\"query\":\"the vendor's name\"}.", IsError: true)
            : Search(await BranchAsync(db, context.Conversation, ct), query, "this chat", ReadFunction);
    }

    /// <summary>The messages of a branch that hold the query's words, the best first, each with its number.</summary>
    internal static ToolResult Search(List<ChatMessage> branch, string query, string where, string reader)
    {
        var terms = HistoryWords.Terms(query);
        // Each message by how many of the words it holds (all of them best), the newest first among equals; a question left
        // out by the person, and what answered it, are not found.
        var found = new List<(int Number, ChatMessage Message, int Hits, string Text)>();
        var leftOut = false;
        for (var i = 0; i < branch.Count; i++)
        {
            var m = branch[i];
            leftOut = m.Role == "user" ? m.LeftOut : leftOut;
            if (leftOut || m.LeftOut)
            {
                continue;
            }
            var text = Said(m);
            var hits = HistoryWords.Hits(text, terms);
            if (hits > 0)
            {
                found.Add((i + 1, m, hits, text));
            }
        }
        if (found.Count == 0)
        {
            return new ToolResult($"No message of {where} holds \"{query}\". Try other words, or fewer.");
        }
        var best = found.OrderByDescending(f => f.Hits).ThenByDescending(f => f.Number).Take(MostFound).ToList();
        var lines = best.Select(f => $"#{f.Number} {Who(f.Message)} ({f.Message.CreatedAt.ToString("d MMM", CultureInfo.InvariantCulture)}): {HistoryWords.Around(f.Text, terms)}");
        return new ToolResult($"{found.Count} {(found.Count == 1 ? "message holds" : "messages hold")} those words" +
            (found.Count > best.Count ? $"; the {best.Count} best" : "") + $". {reader} reads one whole.\n" + string.Join('\n', lines));
    }

    private async Task<ToolResult> ReadAsync(ToolContext context, JsonObject args, CancellationToken ct) =>
        Read(await BranchAsync(db, context.Conversation, ct), args);

    /// <summary>A branch's messages from one number to another (args from and to), word for word within limits.</summary>
    internal static ToolResult Read(List<ChatMessage> branch, JsonObject args)
    {
        if (Int(args, "from") is not { } from || from < 1 || from > branch.Count)
        {
            return new ToolResult($"Give the first message's number, from 1 to {branch.Count}: {{\"from\":12}}.", IsError: true);
        }
        var to = Math.Clamp(Int(args, "to") ?? from + 5, from, Math.Min(branch.Count, from + MostRead - 1));
        var text = new StringBuilder();
        var leftOut = false;
        for (var i = 0; i < from - 1; i++)
        {
            leftOut = branch[i].Role == "user" ? branch[i].LeftOut : leftOut;
        }
        for (var n = from; n <= to; n++)
        {
            var m = branch[n - 1];
            leftOut = m.Role == "user" ? m.LeftOut : leftOut;
            var said = leftOut || m.LeftOut ? "(left out by the person: not read)" : Said(m);
            var most = m.Role == "tool" ? ToolChars : MessageChars;
            if (said.Length > most)
            {
                said = said[..most] + $" […{said.Length - most:N0} characters more]";
            }
            var line = $"#{n} {Who(m)}: {said}\n\n";
            if (text.Length + line.Length > ReadChars && text.Length > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"(Up to #{n - 1}: read on from #{n}.)");
                break;
            }
            text.Append(line);
        }
        return new ToolResult(text.ToString().TrimEnd());
    }

    internal static int? Int(JsonObject args, string name) => args[name] switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
}
