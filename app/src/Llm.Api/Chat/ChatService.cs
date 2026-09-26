using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>For one answer only: another model or thinking level than the chat's (retry with…).</summary>
public sealed record AnswerOverrides(string? Model = null, string? Thinking = null);

/// <summary>
/// One answer to a question in a conversation. The conversation is a tree: the
/// model reads the path from the first message to the question, streams, calls
/// the chat's tools (Argus, pictures, MCP servers...) as the person when it wants
/// to, and every step is saved as a child of the one before, so the answer is its
/// own branch. Events go to the browser as they happen (see ChatEndpoints).
/// </summary>
public sealed partial class ChatService(
    AppDbContext db,
    GatewayChat gateway,
    ToolRegistry registry,
    ToolApprovals approvals,
    AccessService access,
    Models.ModelPolicy policy,
    ChatModels models,
    IOptionsMonitor<ChatOptions> chat,
    ILogger<ChatService> logger)
{
    /// <summary>An image counts as this many characters of the context budget (roughly 1,000 tokens).</summary>
    private const int ImageWeight = 3_500;

    public async Task AnswerAsync(AppUser user, Conversation conversation, ChatMessage question, AnswerOverrides overrides, Func<object, Task> emit, CancellationToken ct)
    {
        var email = user.Email!.ToLowerInvariant();
        // A chat that chose no model takes the first this person may use that is loaded.
        var requested = overrides.Model ?? conversation.Model;
        var model = requested is null
            ? (await policy.ForAsync(user, await models.ListAsync(ct), ct)).Default
            : await models.ResolveAsync(requested, ct);
        var modelName = model?.Name ?? "default";
        if (model is not null && await policy.RefusalAsync(user, model.Name, ct) is { } refusal)
        {
            var sequence = (await db.ChatMessages.Where(m => m.ConversationId == conversation.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0) + 1;
            var refused = new ChatMessage
            {
                ConversationId = conversation.Id, ParentId = question.Id, Role = "assistant", Sequence = sequence, Model = modelName,
                Status = MessageStatus.Failed, Error = refusal,
            };
            db.ChatMessages.Add(refused);
            conversation.CurrentLeafId = refused.Id;
            await emit(new { type = "assistant", id = refused.Id, parentId = question.Id, model = modelName });
            await FinishAsync(conversation, ct);
            await emit(new { type = "error", message = refusal });
            return;
        }
        var thinking = overrides.Thinking ?? conversation.Thinking;

        // The chat's tools that this person may use, each made ready for this answer.
        var runs = new Dictionary<string, (ToolChoice Choice, IToolRun Run)>();
        var tools = new JsonArray();
        var instructions = new List<string>();
        if (model?.Tools != false)
        {
            var allowed = await registry.ForAsync(await access.MembershipAsync(user, ct), ct);
            foreach (var choice in ToolRegistry.Chosen(conversation.Tools, allowed))
            {
                IToolRun run;
                try
                {
                    run = await choice.Tool.StartAsync(new ToolContext(user, email, conversation), ct);
                }
                catch (McpException ex)
                {
                    await emit(new
                    {
                        type = "notice", kind = choice.Tool.Id == "argus" ? "argus_unavailable" : "tool_unavailable",
                        text = $"{choice.Tool.Title} is not available for this answer: {ex.Message}",
                    });
                    continue;
                }
                foreach (var f in run.Functions.OfType<JsonObject>())
                {
                    if (f["function"]?["name"]?.GetValue<string>() is { } fname && runs.TryAdd(fname, (choice, run)))
                    {
                        tools.Add(f.DeepClone());
                    }
                }
                if (!string.IsNullOrWhiteSpace(run.Instructions))
                {
                    instructions.Add(run.Instructions.Trim());
                }
            }
        }

        var (messages, imagesDropped) = await BuildHistoryAsync(conversation, question, model, string.Join("\n\n", instructions), runs.ContainsKey("read_file"), ct);
        if (imagesDropped)
        {
            await emit(new { type = "notice", kind = "no_vision", text = $"{modelName} cannot see images, so it got their names only. Choose a model that can see to ask about them." });
        }
        var next = await db.ChatMessages.Where(m => m.ConversationId == conversation.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0;
        var parent = question.Id;

        for (var round = 0; ; round++)
        {
            var msg = new ChatMessage { ConversationId = conversation.Id, ParentId = parent, Role = "assistant", Sequence = ++next, Model = modelName };
            db.ChatMessages.Add(msg);
            conversation.CurrentLeafId = msg.Id;
            await emit(new { type = "assistant", id = msg.Id, parentId = parent, model = modelName });

            var request = new JsonObject
            {
                ["model"] = modelName,
                ["messages"] = messages.DeepClone(),
                ["stream"] = true,
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
                // Enforcement: the end-user budget binds on this field (see deploy/identity-proxy).
                ["user"] = email,
            };
            if (conversation.Temperature is { } temperature)
            {
                request["temperature"] = temperature;
            }
            if (conversation.TopP is { } topP)
            {
                request["top_p"] = topP;
            }
            if (conversation.MaxTokens is { } maxTokens)
            {
                request["max_tokens"] = maxTokens;
            }
            if (ThinkingPresets.TemplateKwargs(thinking) is { } kwargs)
            {
                request["chat_template_kwargs"] = kwargs;
            }
            // No tools on the last allowed round: the model must answer with what it has.
            if (tools.Count > 0 && round < chat.CurrentValue.MaxToolRounds)
            {
                request["tools"] = tools.DeepClone();
            }

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
            var clock = Stopwatch.StartNew();
            TimeSpan? thoughtFrom = null;
            void EndThinking()
            {
                if (thoughtFrom is { } from && msg.ThinkingMs is null)
                {
                    msg.ThinkingMs = (int)(clock.Elapsed - from).TotalMilliseconds;
                }
            }
            void Keep(MessageStatus status)
            {
                EndThinking();
                msg.Content = content.ToString();
                msg.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
                msg.Status = status;
                msg.DurationMs = (int)clock.Elapsed.TotalMilliseconds;
            }
            try
            {
                await foreach (var e in gateway.StreamAsync(request, email, ct))
                {
                    switch (e)
                    {
                        case ReasoningDelta r:
                            thoughtFrom ??= clock.Elapsed;
                            reasoning.Append(r.Text);
                            await emit(new { type = "reasoning", text = r.Text });
                            break;
                        case ContentDelta c:
                            if (thoughtFrom is not null && msg.ThinkingMs is null)
                            {
                                EndThinking();
                                await emit(new { type = "thought", ms = msg.ThinkingMs });
                            }
                            content.Append(c.Text);
                            await emit(new { type = "content", text = c.Text });
                            break;
                        case ToolCallDelta t:
                            EndThinking();
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
                Keep(MessageStatus.Stopped);
                await FinishAsync(conversation, CancellationToken.None);
                return;
            }
            catch (ChatGatewayException ex)
            {
                Keep(MessageStatus.Failed);
                msg.Error = ex.Message;
                await FinishAsync(conversation, CancellationToken.None);
                await emit(new { type = "error", message = ex.Message });
                return;
            }

            Keep(MessageStatus.Complete);
            await emit(new
            {
                type = "usage", prompt = msg.PromptTokens, cached = msg.CachedTokens, completion = msg.CompletionTokens,
                thinkingMs = msg.ThinkingMs, durationMs = msg.DurationMs,
            });

            if (calls.Count == 0 || runs.Count == 0)
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
            parent = msg.Id;

            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                var id = call["id"]!.GetValue<string>();
                var name = call["function"]!["name"]!.GetValue<string>();
                var rawArgs = call["function"]!["arguments"]!.GetValue<string>();
                var known = runs.TryGetValue(name, out var target);
                await emit(new { type = "tool_call", id, name, arguments = rawArgs, tool = known ? target.Choice.Tool.Id : null });
                ToolResult outcome;
                var declined = false;
                var took = Stopwatch.StartNew();
                try
                {
                    var args = JsonNode.Parse(rawArgs.Length == 0 ? "{}" : rawArgs) as JsonObject ?? [];
                    if (!known)
                    {
                        outcome = new ToolResult($"There is no tool named {name}.", IsError: true);
                    }
                    else if (target.Choice.Setting.AskFirst && !await AskAsync(conversation, id, name, rawArgs, target.Choice.Tool, emit, ct))
                    {
                        declined = true;
                        outcome = new ToolResult($"The person did not allow {target.Choice.Tool.Title} to run this call. Do not try it again unless they ask.", IsError: true);
                    }
                    else
                    {
                        took.Restart();
                        outcome = await target.Run.CallAsync(name, args, ct);
                    }
                }
                catch (JsonException)
                {
                    outcome = new ToolResult($"The arguments were not valid JSON: {rawArgs}", IsError: true);
                }
                catch (McpException ex)
                {
                    outcome = new ToolResult(ex.Message, IsError: true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await FinishAsync(conversation, CancellationToken.None);
                    return;
                }
                var (text, isError) = (outcome.Text, outcome.IsError);
                var result = new ChatMessage
                {
                    ConversationId = conversation.Id, ParentId = parent, Role = "tool", Sequence = ++next, ToolCallId = id, ToolName = name,
                    Content = text, Status = declined ? MessageStatus.Declined : isError ? MessageStatus.Failed : MessageStatus.Complete,
                    DurationMs = (int)took.Elapsed.TotalMilliseconds,
                    AttachmentsJson = outcome.Files is { Count: > 0 } made ? JsonSerializer.Serialize(made.Select(f => f.Id)) : null,
                };
                db.ChatMessages.Add(result);
                conversation.CurrentLeafId = result.Id;
                parent = result.Id;
                await db.SaveChangesAsync(ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text });
                await emit(new
                {
                    type = "tool_result", id, messageId = result.Id, name, text, isError, declined, noAccess = ArgusMcp.IsNoAccess(text), durationMs = result.DurationMs,
                    attachments = (outcome.Files ?? []).Select(f => new { f.Id, f.FileName, f.Size, f.Truncated, f.Kind, f.ContentType, original = f.Kind != "image" && f.Data != null }),
                });
            }
        }
    }

    /// <summary>"Ask before running": the page shows the call, and the answer waits for yes or no (ten minutes at most).</summary>
    private async Task<bool> AskAsync(Conversation conversation, string callId, string name, string rawArgs, IChatTool tool, Func<object, Task> emit, CancellationToken ct)
    {
        var waiting = approvals.WaitAsync(conversation.Id, callId, TimeSpan.FromMinutes(10), ct);
        await emit(new { type = "approval", id = callId, name, arguments = rawArgs, tool = tool.Id, title = tool.Title });
        return await waiting;
    }

    private async Task FinishAsync(Conversation conversation, CancellationToken ct)
    {
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The messages from the first one down to <paramref name="leaf"/>, following parents.</summary>
    public static List<ChatMessage> PathTo(IReadOnlyDictionary<Guid, ChatMessage> byId, Guid? leaf)
    {
        var path = new List<ChatMessage>();
        var seen = new HashSet<Guid>();
        for (var id = leaf; id is { } current && byId.TryGetValue(current, out var m) && seen.Add(current); id = m.ParentId)
        {
            path.Add(m);
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// An attachment as it goes into the question: whole when it is short; otherwise
    /// its start, cut at a line, and a note saying how long it is and how to read on.
    /// </summary>
    public static string Inline(ChatAttachment f, int budget, bool canReadFiles)
    {
        var cutWhenAttached = f.Truncated ? " (the file itself was longer: it was cut when it was attached)" : "";
        if (f.Text.Length <= budget)
        {
            return f.Truncated ? f.Text + $"\n[end of what was kept{cutWhenAttached}]" : f.Text;
        }
        var end = f.Text.LastIndexOf('\n', Math.Max(0, budget - 1));
        var shown = f.Text[..(end > budget / 2 ? end : budget)];
        var shownLines = shown.Count(c => c == '\n') + 1;
        var totalLines = f.Text.Count(c => c == '\n') + 1;
        var how = canReadFiles
            ? $"Read on with read_file (file \"{f.FileName}\", from_line {shownLines + 1}), or find parts with search_file."
            : "The rest is not included here.";
        return shown + $"\n[This file goes on: lines 1 to {shownLines} of {totalLines} are above ({shown.Length:N0} of {f.Text.Length:N0} characters){cutWhenAttached}. {how}]";
    }

    /// <summary>
    /// The branch as the model reads it, trimmed from the oldest end to fit its context.
    /// Images go as pictures to a model that can see, and as their names to one that cannot.
    /// </summary>
    private async Task<(JsonArray Messages, bool ImagesDropped)> BuildHistoryAsync(Conversation conversation, ChatMessage question, GatewayModel? model, string? toolInstructions,
        bool canReadFiles, CancellationToken ct)
    {
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).ToDictionaryAsync(m => m.Id, ct);
        all[question.Id] = question;
        var stored = PathTo(all, question.Id);
        // Only questions' files go to the model; pictures a tool made are for the person.
        var attachmentIds = stored.Where(m => m.Role == "user").SelectMany(m => ParseIds(m.AttachmentsJson)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => attachmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var vision = model?.Vision == true;
        var imagesDropped = false;

        var turns = new List<(JsonObject Turn, long Weight)>();
        foreach (var m in stored)
        {
            switch (m.Role)
            {
                case "user":
                    var text = new StringBuilder(m.Content);
                    var images = new List<ChatAttachment>();
                    foreach (var id in ParseIds(m.AttachmentsJson))
                    {
                        if (!files.TryGetValue(id, out var f))
                        {
                            continue;
                        }
                        if (f.Kind == "image")
                        {
                            if (vision && f.Data is not null)
                            {
                                images.Add(f);
                            }
                            else
                            {
                                text.Append("\n\n[image attached: ").Append(f.FileName).Append(" (this model cannot see images)]");
                                imagesDropped |= m.Id == question.Id;
                            }
                            continue;
                        }
                        if (f.Kind == "file")
                        {
                            text.Append("\n\n[file attached: ").Append(f.FileName).Append(" (not text; run_python can open it)]");
                            continue;
                        }
                        text.Append("\n\n<attachment name=\"").Append(f.FileName.Replace("\"", "'", StringComparison.Ordinal)).Append("\">\n")
                            .Append(Inline(f, chat.CurrentValue.InlineAttachmentChars, canReadFiles)).Append("\n</attachment>");
                    }
                    if (images.Count == 0)
                    {
                        turns.Add((new JsonObject { ["role"] = "user", ["content"] = text.ToString() }, text.Length));
                    }
                    else
                    {
                        var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.ToString() });
                        foreach (var img in images)
                        {
                            parts.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject { ["url"] = $"data:{img.ContentType};base64,{Convert.ToBase64String(img.Data!)}" },
                            });
                        }
                        turns.Add((new JsonObject { ["role"] = "user", ["content"] = parts }, text.Length + (long)images.Count * ImageWeight));
                    }
                    break;
                case "assistant" when m.ToolCallsJson is not null:
                    var withCalls = new JsonObject { ["role"] = "assistant", ["content"] = m.Content, ["tool_calls"] = JsonNode.Parse(m.ToolCallsJson) };
                    turns.Add((withCalls, withCalls.ToJsonString().Length));
                    break;
                case "assistant" when m.Content.Length > 0:
                    turns.Add((new JsonObject { ["role"] = "assistant", ["content"] = m.Content }, m.Content.Length));
                    break;
                case "tool":
                    turns.Add((new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content }, m.Content.Length));
                    break;
            }
        }

        var system = $"Today is {DateTimeOffset.UtcNow.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)} (UTC).";
        if (!string.IsNullOrWhiteSpace(toolInstructions))
        {
            system += "\n\n" + toolInstructions;
        }
        if (!string.IsNullOrWhiteSpace(conversation.SystemPrompt))
        {
            system += "\n\nThe person's instructions for this conversation:\n" + conversation.SystemPrompt.Trim();
        }

        // Rough, and on the safe side: ~3.5 characters a token for English and code.
        var context = model?.Context ?? 32768;
        var output = conversation.MaxTokens ?? model?.MaxOutput ?? 8192;
        var budgetChars = (long)Math.Max(4096, context - output - 1024) * 7 / 2 - system.Length;
        var dropped = 0;
        while (turns.Count > 1 && turns.Sum(t => t.Weight) > budgetChars)
        {
            turns.RemoveAt(0);
            dropped++;
            // Never start on a tool answer or a tool request whose answers were cut.
            while (turns.Count > 1 && turns[0].Turn["role"]!.GetValue<string>() != "user")
            {
                turns.RemoveAt(0);
                dropped++;
            }
        }
        if (dropped > 0)
        {
            LogTrimmed(logger, conversation.Id, dropped);
        }

        return ([new JsonObject { ["role"] = "system", ["content"] = system }, .. turns.Select(t => t.Turn)], imagesDropped);
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
