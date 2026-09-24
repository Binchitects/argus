using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Operations;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// One answer to the last user message of a conversation: the model streams,
/// calls Argus's tools as the person when it wants to, and every turn is saved
/// as it completes. Events go to the browser as they happen (see ChatEndpoints).
/// </summary>
public sealed partial class ChatService(
    AppDbContext db,
    GatewayChat gateway,
    ArgusMcp argus,
    IOptionsMonitor<ChatOptions> chat,
    IOptions<StackOptions> stack,
    ILogger<ChatService> logger)
{
    public async Task AnswerAsync(AppUser user, Conversation conversation, Func<object, Task> emit, CancellationToken ct)
    {
        var email = user.Email!.ToLowerInvariant();
        var model = stack.Value.ModelName ?? "default";

        ArgusSession? session = null;
        JsonArray? tools = null;
        if (conversation.UseArgus && argus.Enabled)
        {
            try
            {
                session = await argus.ConnectAsync(email, ct);
                tools = ArgusMcp.ToOpenAiTools(await session.ToolsAsync(ct));
            }
            catch (ArgusToolException ex)
            {
                session = null;
                await emit(new { type = "notice", kind = "argus_unavailable", text = $"Argus is not available for this answer: {ex.Message}" });
            }
        }

        var messages = await BuildHistoryAsync(conversation, session?.Instructions, ct);
        var next = await db.ChatMessages.Where(m => m.ConversationId == conversation.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0;

        for (var round = 0; ; round++)
        {
            var msg = new ChatMessage { ConversationId = conversation.Id, Role = "assistant", Sequence = ++next, Model = model };
            db.ChatMessages.Add(msg);
            await emit(new { type = "assistant", id = msg.Id });

            var request = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
                ["stream"] = true,
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
                // Enforcement: the end-user budget binds on this field (see deploy/identity-proxy).
                ["user"] = email,
            };
            if (ThinkingPresets.TemplateKwargs(conversation.Thinking) is { } kwargs)
            {
                request["chat_template_kwargs"] = kwargs;
            }
            // No tools on the last allowed round: the model must answer with what it has.
            if (tools is { Count: > 0 } && round < chat.CurrentValue.MaxToolRounds)
            {
                request["tools"] = tools.DeepClone();
            }

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
            try
            {
                await foreach (var e in gateway.StreamAsync(request, email, ct))
                {
                    switch (e)
                    {
                        case ReasoningDelta r:
                            reasoning.Append(r.Text);
                            await emit(new { type = "reasoning", text = r.Text });
                            break;
                        case ContentDelta c:
                            content.Append(c.Text);
                            await emit(new { type = "content", text = c.Text });
                            break;
                        case ToolCallDelta t:
                            var slot = calls.TryGetValue(t.Index, out var existing) ? existing : (null, null, new StringBuilder());
                            calls[t.Index] = (t.Id ?? slot.Id, t.Name ?? slot.Name, slot.Args.Append(t.Arguments));
                            break;
                        case UsageReport u:
                            (msg.PromptTokens, msg.CachedTokens, msg.CompletionTokens) = (u.Prompt, u.Cached, u.Completion);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stop: keep what arrived. The request is gone, so save without its token.
                msg.Content = content.ToString();
                msg.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
                msg.Status = MessageStatus.Stopped;
                await FinishAsync(conversation, CancellationToken.None);
                return;
            }
            catch (ChatGatewayException ex)
            {
                msg.Content = content.ToString();
                msg.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
                msg.Status = MessageStatus.Failed;
                msg.Error = ex.Message;
                await FinishAsync(conversation, CancellationToken.None);
                await emit(new { type = "error", message = ex.Message });
                return;
            }

            msg.Content = content.ToString();
            msg.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
            await emit(new { type = "usage", prompt = msg.PromptTokens, cached = msg.CachedTokens, completion = msg.CompletionTokens });

            if (calls.Count == 0 || session is null)
            {
                await FinishAsync(conversation, ct);
                await emit(new { type = "done", id = msg.Id });
                return;
            }

            // The model asked for tools: record the request, run each as the person, feed the answers back.
            var toolCalls = new JsonArray([.. calls.Select(kv => (JsonNode)new JsonObject
            {
                ["id"] = kv.Value.Id ?? $"call_{round}_{kv.Key}",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = kv.Value.Name ?? "", ["arguments"] = kv.Value.Args.ToString() },
            })]);
            msg.ToolCallsJson = toolCalls.ToJsonString();
            await db.SaveChangesAsync(ct);
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = msg.Content, ["tool_calls"] = toolCalls.DeepClone() });

            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                var id = call["id"]!.GetValue<string>();
                var name = call["function"]!["name"]!.GetValue<string>();
                var rawArgs = call["function"]!["arguments"]!.GetValue<string>();
                await emit(new { type = "tool_call", id, name, arguments = rawArgs });
                string text;
                bool isError;
                try
                {
                    var args = JsonNode.Parse(rawArgs.Length == 0 ? "{}" : rawArgs) as JsonObject ?? [];
                    (text, isError) = await session.CallAsync(name, args, ct);
                }
                catch (JsonException)
                {
                    (text, isError) = ($"The arguments were not valid JSON: {rawArgs}", true);
                }
                catch (ArgusToolException ex)
                {
                    (text, isError) = (ex.Message, true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await FinishAsync(conversation, CancellationToken.None);
                    return;
                }
                db.ChatMessages.Add(new ChatMessage
                {
                    ConversationId = conversation.Id, Role = "tool", Sequence = ++next, ToolCallId = id, ToolName = name,
                    Content = text, Status = isError ? MessageStatus.Failed : MessageStatus.Complete,
                });
                await db.SaveChangesAsync(ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text });
                await emit(new { type = "tool_result", id, name, text, isError, noAccess = ArgusMcp.IsNoAccess(text) });
            }
        }
    }

    private async Task FinishAsync(Conversation conversation, CancellationToken ct)
    {
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The conversation as the model reads it, trimmed from the oldest end to fit its context.</summary>
    private async Task<JsonArray> BuildHistoryAsync(Conversation conversation, string? argusInstructions, CancellationToken ct)
    {
        var stored = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).OrderBy(m => m.Sequence).ToListAsync(ct);
        var attachmentIds = stored.SelectMany(m => ParseIds(m.AttachmentsJson)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => attachmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        var turns = new List<JsonObject>();
        foreach (var m in stored)
        {
            switch (m.Role)
            {
                case "user":
                    var text = new StringBuilder(m.Content);
                    foreach (var id in ParseIds(m.AttachmentsJson))
                    {
                        if (files.TryGetValue(id, out var f))
                        {
                            text.Append("\n\n<attachment name=\"").Append(f.FileName.Replace("\"", "'", StringComparison.Ordinal)).Append("\">\n")
                                .Append(f.Text).Append(f.Truncated ? "\n[truncated]" : "").Append("\n</attachment>");
                        }
                    }
                    turns.Add(new JsonObject { ["role"] = "user", ["content"] = text.ToString() });
                    break;
                case "assistant" when m.ToolCallsJson is not null:
                    turns.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Content, ["tool_calls"] = JsonNode.Parse(m.ToolCallsJson) });
                    break;
                case "assistant" when m.Content.Length > 0:
                    turns.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Content });
                    break;
                case "tool":
                    turns.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content });
                    break;
            }
        }

        var system = $"Today is {DateTimeOffset.UtcNow.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)} (UTC).";
        if (!string.IsNullOrWhiteSpace(argusInstructions))
        {
            system += "\n\n" + argusInstructions;
        }

        // Rough, and on the safe side: ~3.5 characters a token for English and code.
        var context = int.TryParse(stack.Value.ModelContext, out var c) ? c : 32768;
        var output = int.TryParse(stack.Value.ModelMaxOutput, out var o) ? o : 8192;
        var budgetChars = (long)Math.Max(4096, context - output - 1024) * 7 / 2 - system.Length;
        var dropped = 0;
        while (turns.Count > 1 && turns.Sum(t => (long)t.ToJsonString().Length) > budgetChars)
        {
            turns.RemoveAt(0);
            dropped++;
            // Never start on a tool answer or a tool request whose answers were cut.
            while (turns.Count > 1 && turns[0]["role"]!.GetValue<string>() != "user")
            {
                turns.RemoveAt(0);
                dropped++;
            }
        }
        if (dropped > 0)
        {
            LogTrimmed(logger, conversation.Id, dropped);
        }

        return [new JsonObject { ["role"] = "system", ["content"] = system }, .. turns];
    }

    public static IEnumerable<Guid> ParseIds(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Guid[]>(json) ?? [];

    /// <summary>A title from the first question: its first line, cut at a word, at most 60 characters.</summary>
    public static string TitleFrom(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        if (line.Length <= 60)
        {
            return line.Length == 0 ? "New chat" : line;
        }
        var cut = line[..60];
        var space = cut.LastIndexOf(' ');
        return (space > 30 ? cut[..space] : cut).TrimEnd('.', ',', ';', ':') + "…";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {Conversation}: {Dropped} oldest messages left out to fit the model's context")]
    private static partial void LogTrimmed(ILogger logger, Guid conversation, int dropped);
}
