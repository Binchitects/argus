using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Compaction: a long chat's older messages become a summary the model reads
/// instead of them, so the chat goes on past the model's context. The summary is
/// kept on the last message it covers (<see cref="ChatMessage.Summary"/>), so it
/// holds for every branch that passes there. It happens on its own before the
/// context fills (Chat:AutoCompactPercent), or when the person asks (/compact).
/// </summary>
public sealed partial class ChatService
{
    /// <summary>Longest a summary may be, in tokens.</summary>
    private const int SummaryTokens = 2048;

    /// <summary>What of one tool answer goes into what is summarized.</summary>
    private const int ToolAnswerChars = 3_000;

    internal const string CompactPrompt =
        "You compact a conversation between a person and an AI assistant, so that it can go on without the whole history: " +
        "the assistant reads your summary instead of the messages it covers.\n" +
        "Keep what the person wants, their preferences and constraints; facts, decisions and conclusions; names of files, functions, " +
        "repositories, commands, settings and numbers; code that still matters (short, in fenced blocks); what the tools found; " +
        "open questions, and what was about to happen next. The latest messages matter most: keep their details.\n" +
        "Leave out greetings, repetition and dead ends. Write in the language the person writes in, in short headed sections " +
        "with bullet points, at most about 800 words. Write only the summary.";

    /// <summary>The newest summary on a branch before <paramref name="end"/>, and where the messages after it start.</summary>
    internal static (string? Summary, int From) LastSummary(IReadOnlyList<ChatMessage> path, int end)
    {
        for (var i = Math.Min(end, path.Count) - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(path[i].Summary))
            {
                return (path[i].Summary, i + 1);
            }
        }
        return (null, 0);
    }

    /// <summary>
    /// The person asked to compact the branch down to <paramref name="leafId"/>: all
    /// of it becomes the summary the next answer reads.
    /// </summary>
    public async Task CompactAsync(AppUser user, Conversation conversation, Guid leafId, Func<object, Task> emit, CancellationToken ct)
    {
        var email = user.Email!.ToLowerInvariant();
        // The model for small steps writes it when there is one; else the chat's model.
        var (model, modelName, refusal) = await small.ForAsync(user, ct) is { } helper ? (helper, helper.Name, null) : await ModelForAsync(user, conversation, null, ct);
        if (refusal is not null)
        {
            await emit(new { type = "error", message = refusal });
            return;
        }
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).ToDictionaryAsync(m => m.Id, ct);
        var path = PathTo(all, leafId);
        var (earlier, from) = LastSummary(path, path.Count);
        if (path.Count == 0 || from >= path.Count)
        {
            await emit(new { type = "notice", kind = "compacted_already", text = "Nothing new to compact: the chat was compacted here already." });
            await emit(new { type = "done", id = leafId });
            return;
        }
        await emit(new { type = "compacting" });
        string summary;
        try
        {
            summary = await SummarizeAsync(earlier, path[from..], model, modelName, email, ct);
        }
        catch (ChatGatewayException ex)
        {
            await emit(new { type = "error", message = $"The chat could not be compacted: {ex.Message}" });
            return;
        }
        var target = path[^1];
        await db.ChatMessages.Where(m => m.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Summary, summary), CancellationToken.None);
        LogCompacted(logger, conversation.Id, path.Count - from, summary.Length, false);
        await emit(new { type = "compacted", id = target.Id, summary, auto = false, covered = path.Count - from, model = modelName });
        await emit(new { type = "done", id = target.Id });
    }

    /// <summary>
    /// Before an answer: when the branch fills more of the context than the setting
    /// allows, the older messages become a summary and the newest exchanges stay as
    /// they are (about a third of the room). Null when nothing was compacted.
    /// </summary>
    private async Task<(string Summary, int Cut)?> AutoCompactAsync(List<(JsonObject Turn, long Weight, ChatMessage Source)> turns, List<ChatMessage> stored, int from,
        string? earlier, long budgetChars, GatewayModel? model, string modelName, string email, Func<object, Task> emit, CancellationToken ct)
    {
        var percent = chat.CurrentValue.AutoCompactPercent;
        var total = turns.Sum(t => t.Weight) + (earlier?.Length ?? 0);
        if (percent <= 0 || total <= budgetChars * percent / 100)
        {
            return null;
        }
        // The newest exchanges that fit in a third of the room stay; always the question.
        var keep = budgetChars / 3;
        long tail = 0;
        var cut = -1;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            tail += turns[i].Weight;
            if (turns[i].Source.Role != "user")
            {
                continue;
            }
            if (cut >= 0 && tail > keep)
            {
                break;
            }
            cut = i;
        }
        var at = cut > 0 ? stored.IndexOf(turns[cut].Source) : -1;
        if (at <= from)
        {
            return null;
        }
        await emit(new { type = "compacting" });
        string summary;
        try
        {
            summary = await SummarizeAsync(earlier, stored[from..at], model, modelName, email, ct);
        }
        catch (ChatGatewayException ex)
        {
            await emit(new { type = "notice", kind = "compact_failed", text = $"The chat could not be compacted ({ex.Message}): its oldest messages are left out instead." });
            return null;
        }
        var target = stored[at - 1];
        target.Summary = summary;
        await db.ChatMessages.Where(m => m.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Summary, summary), CancellationToken.None);
        LogCompacted(logger, target.ConversationId, at - from, summary.Length, true);
        await emit(new { type = "compacted", id = target.Id, summary, auto = true, covered = at - from, model = modelName });
        return (summary, cut);
    }

    /// <summary>
    /// A summary of <paramref name="messages"/> after <paramref name="earlier"/>'s. What
    /// does not fit the model's context at once is summarized in parts, each folded
    /// into the summary so far.
    /// </summary>
    private async Task<string> SummarizeAsync(string? earlier, IReadOnlyList<ChatMessage> messages, GatewayModel? model, string modelName, string email, CancellationToken ct,
        string prompt = CompactPrompt)
    {
        var output = Math.Min(SummaryTokens, model?.MaxOutput ?? SummaryTokens);
        var room = (long)Math.Max(4096, (model?.Context ?? 32768) - output - 1024) * 7 / 2 - CompactPrompt.Length - 400;
        var ids = messages.Where(m => m.Role == "user").SelectMany(m => ParseIds(m.AttachmentsJson)).ToHashSet();
        var names = await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.FileName, ct);

        var summary = earlier;
        var part = new StringBuilder();
        foreach (var line in Transcript(messages, names))
        {
            var free = Math.Max(2_000, room - (summary?.Length ?? 0));
            if (part.Length > 0 && part.Length + line.Length > free)
            {
                summary = await SummaryOnceAsync(summary, part.ToString(), model, modelName, email, output, ct, prompt);
                part.Clear();
                free = Math.Max(2_000, room - summary.Length);
            }
            part.Append(line.Length > free ? line[..(int)free] + " […]" : line).Append("\n\n");
        }
        if (part.Length > 0)
        {
            summary = await SummaryOnceAsync(summary, part.ToString(), model, modelName, email, output, ct, prompt);
        }
        return summary ?? "";
    }

    /// <summary>The messages as the summarizer reads them: who said what, the tools' answers cut short.</summary>
    private static IEnumerable<string> Transcript(IEnumerable<ChatMessage> messages, IReadOnlyDictionary<Guid, string> files)
    {
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "user":
                    var attached = ParseIds(m.AttachmentsJson).Select(id => files.GetValueOrDefault(id)).OfType<string>().ToList();
                    yield return "Person: " + m.Content + (attached.Count > 0 ? $"\n[attached: {string.Join(", ", attached)}]" : "");
                    break;
                case "assistant":
                    var text = new StringBuilder();
                    if (m.Content.Length > 0)
                    {
                        text.Append("Assistant: ").Append(m.Content);
                    }
                    if (m.ToolCallsJson is not null && JsonNode.Parse(m.ToolCallsJson) is JsonArray calls)
                    {
                        foreach (var call in calls.OfType<JsonObject>())
                        {
                            var args = call["function"]?["arguments"]?.GetValue<string>() ?? "";
                            text.Append(text.Length > 0 ? "\n" : "").Append("Assistant called ").Append(call["function"]?["name"]?.GetValue<string>())
                                .Append('(').Append(args.Length > 500 ? args[..500] + "…" : args).Append(')');
                        }
                    }
                    if (text.Length > 0)
                    {
                        yield return text.ToString();
                    }
                    break;
                case "tool":
                    yield return $"{m.ToolName} answered: " + (m.Content.Length > ToolAnswerChars ? m.Content[..ToolAnswerChars] + " […]" : m.Content);
                    break;
            }
        }
    }

    private async Task<string> SummaryOnceAsync(string? earlier, string part, GatewayModel? model, string modelName, string email, int output, CancellationToken ct,
        string prompt = CompactPrompt)
    {
        var ask = new StringBuilder();
        if (earlier is not null)
        {
            ask.Append("The summary so far:\n<summary>\n").Append(earlier).Append("\n</summary>\n\nWhat came after it:\n");
        }
        else
        {
            ask.Append("The conversation:\n");
        }
        ask.Append("<conversation>\n").Append(part).Append("</conversation>\n\n")
            .Append(earlier is null ? "Write the summary." : "Write one summary of both: the summary so far and what came after it.");
        var request = new JsonObject
        {
            ["model"] = modelName,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = prompt },
                new JsonObject { ["role"] = "user", ["content"] = ask.ToString() }),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = output,
            ["temperature"] = 0.2,
            ["user"] = email,
        };
        // A summary needs no thinking: it is quicker and cheaper without.
        if (model?.Thinking == true)
        {
            request["chat_template_kwargs"] = ThinkingPresets.TemplateKwargs("off");
        }
        var text = new StringBuilder();
        await foreach (var e in gateway.StreamAsync(request, email, ct))
        {
            if (e is ContentDelta c)
            {
                text.Append(c.Text);
            }
        }
        var summary = text.ToString().Trim();
        return summary.Length > 0 ? summary : throw new ChatGatewayException("the model wrote no summary");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {Conversation}: {Messages} messages compacted into {Chars} characters (automatic: {Auto})")]
    private static partial void LogCompacted(ILogger logger, Guid conversation, int messages, int chars, bool auto);
}
