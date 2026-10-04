using System.Security.Claims;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Api.Operations;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// A new chat. Tools: the tool ids it may call (default: those on in new chats); UseArgus: Argus on or off in that list.
/// With an assistant, what is not sent comes from the assistant.
/// </summary>
public sealed record NewConversation(string? Thinking = null, bool? UseArgus = null, string? Model = null, string? SystemPrompt = null,
    double? Temperature = null, double? TopP = null, int? MaxTokens = null, string[]? Tools = null, Guid? AssistantId = null);

/// <summary>Only what is sent changes. For the numbers, a negative value clears them (back to the model's default).</summary>
/// <param name="AssistantId">With this assistant; Guid.Empty takes it away from its assistant.</param>
public sealed record ConversationChange(string? Title = null, string? Thinking = null, bool? UseArgus = null, string? Model = null,
    string? SystemPrompt = null, double? Temperature = null, double? TopP = null, int? MaxTokens = null, bool? Archived = null, string[]? Tools = null,
    Guid? AssistantId = null);

/// <summary>The person's answer to a call waiting for them ("ask before running").</summary>
public sealed record ToolDecision(bool Allow);

/// <summary>Fork up to this message (default: the end of the branch on screen).</summary>
public sealed record ForkRequest(Guid? MessageId = null);

/// <summary>A text to read aloud (an answer, as it is shown: Markdown and code are left out).</summary>
public sealed record SpeechRequest(string? Text);

/// <summary>
/// A question. It follows <see cref="ParentId"/> (default: the end of the branch on
/// screen); <see cref="Root"/> starts a new first question. Editing a question is
/// sending its new text with the old one's parent: a sibling, so both stay.
/// </summary>
/// <param name="Research">Deep research: the answer plans, has sub-agents search the web, and writes a sourced report.</param>
/// <param name="Spoken">Said aloud in Talk: the answer is read aloud as it is written, so it is asked for in spoken sentences.</param>
public sealed record NewMessage(string Content, Guid[]? Attachments = null, Guid? ParentId = null, bool Root = false, bool Research = false, bool Spoken = false);

/// <summary>Compact the branch down to this message (default: the end of the branch on screen).</summary>
public sealed record CompactRequest(Guid? MessageId = null);

/// <summary>Answer a question again (default: the last one on the branch), optionally with another model or thinking level.</summary>
/// <param name="Length">"shorter" or "longer" than the answer <paramref name="AnswerId"/> (its last message), for this answer only.</param>
public sealed record Regenerate(Guid? MessageId = null, string? Model = null, string? Thinking = null, string? Length = null, Guid? AnswerId = null);

public sealed record LeafChange(Guid MessageId);

public static partial class ChatEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How often a quiet stream says it is still there, so proxies keep it open.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    public static void MapChat(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization();
        g.MapGet("/config", Config);
        g.MapGet("/conversations", ListAsync);
        g.MapPost("/conversations", CreateAsync);
        g.MapGet("/conversations/{id:guid}", GetAsync);
        g.MapPatch("/conversations/{id:guid}", UpdateAsync);
        g.MapDelete("/conversations/{id:guid}", DeleteAsync);
        g.MapPost("/conversations/{id:guid}/messages", SendAsync);
        g.MapPost("/conversations/{id:guid}/regenerate", RegenerateAsync);
        g.MapGet("/conversations/{id:guid}/stream", WatchAsync);
        g.MapPost("/conversations/{id:guid}/stop", StopAsync);
        g.MapPost("/conversations/{id:guid}/hurry", HurryAsync);
        g.MapGet("/conversations/{id:guid}/summary", SummaryAsync);
        g.MapPost("/conversations/{id:guid}/compact", CompactAsync);
        g.MapPut("/conversations/{id:guid}/leaf", LeafAsync);
        g.MapPost("/conversations/{id:guid}/fork", ForkAsync);
        g.MapPost("/conversations/{id:guid}/tool-calls/{callId}", DecideAsync);
        g.MapPost("/attachments", UploadAsync).DisableAntiforgery();
        g.MapPost("/speech", SpeechAsync);
        g.MapPost("/transcribe", Talk.TranscribeAsync).DisableAntiforgery();
        g.MapGet("/attachments/{id:guid}/content", ContentAsync);
        g.MapGet("/attachments/{id:guid}/pages", PagesAsync);
        g.MapGet("/search", SearchAsync);
        g.MapGet("/attachments/{id:guid}/pages/{number:int}", PageAsync);
    }

    private static async Task<IResult> Config(ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access, ModelPolicy policy,
        IOptions<ArgusOptions> argusOptions, ArgusMcp argus, ChatModels models, SmallModel small, IOptionsMonitor<ChatOptions> chat, CancellationToken ct)
    {
        var me = await Me(p, users);
        // The models this person may use; a model of the engine that is not loaded is listed, marked so.
        var (list, first, onEngine) = await policy.ForAsync(me, await models.ListAsync(ct), ct);
        var tools = await registry.ForAsync(await access.MembershipAsync(me, ct), ct);
        // Auto, while a model for small steps is set that this person may use.
        var helper = await small.OfferedAsync(me, ct);
        return Results.Ok(new
        {
            model = first?.Name ?? models.DefaultName,
            auto = helper is null ? null : new { model = helper.Name, byDefault = models.AutoByDefault },
            models = list.Select(m => new
            {
                m.Name, m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking, loaded = policy.Loaded(m.Name, onEngine), onRequest = policy.OnRequest(m.Name, onEngine),
                prices = new { input = m.InputPerMtok, cachedInput = m.CachedInputPerMtok, output = m.OutputPerMtok },
            }),
            presets = ThinkingPresets.Parse(chat.CurrentValue.ThinkingPresets),
            defaultThinking = chat.CurrentValue.DefaultThinking,
            argus = tools.Any(t => t.Tool.Id == "argus"),
            // The tools this person may use; a chat turns them on and off.
            tools = tools.Select(t => new
            {
                id = t.Tool.Id, title = t.Tool.Title, description = t.Tool.Description, icon = t.Tool.Icon,
                onByDefault = t.Setting.OnByDefault, askFirst = t.Setting.AskFirst,
            }),
            // For links from Argus's answers to the code (Argus reads the same GitLab).
            gitlabUrl = argus.Enabled ? (string.IsNullOrWhiteSpace(chat.CurrentValue.GitlabLinkUrl) ? argusOptions.Value.GitlabUrl : chat.CurrentValue.GitlabLinkUrl)?.TrimEnd('/') : null,
            maxUploadBytes = chat.CurrentValue.MaxUploadBytes,
            imageTypes = Attachments.ImageTypes,
        });
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static Task<Conversation?> Owned(AppDbContext db, Guid id, AppUser me) =>
        db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id);

    /// <summary>The person's chats, newest first; archived ones only when asked for.</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs, string? q = null, bool archived = false,
        Guid? assistant = null)
    {
        var me = await Me(p, users);
        var query = db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id && (archived ? c.ArchivedAt != null : c.ArchivedAt == null));
        if (assistant is { } aid)
        {
            query = query.Where(c => c.AssistantId == aid);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(c => EF.Functions.ILike(c.Title, "%" + q.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }
        var list = await query.OrderByDescending(c => c.UpdatedAt).Take(300).Select(c => new { c.Id, c.Title, c.UpdatedAt, c.ArchivedAt, c.AssistantId }).ToListAsync();
        return Results.Ok(list.Select(c => new { c.Id, c.Title, c.UpdatedAt, c.ArchivedAt, c.AssistantId, answering = jobs.IsAnswering(c.Id) }));
    }

    private static async Task<IResult> CreateAsync(NewConversation body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<ChatOptions> chat, ChatModels models,
        ToolRegistry registry, AccessService access, ModelPolicy policy, SmallModel small, CancellationToken ct)
    {
        var me = await Me(p, users);
        var membership = await access.MembershipAsync(me, ct);
        var allowed = await registry.ForAsync(membership, ct);
        Assistant? assistant = null;
        if (body.AssistantId is { } aid)
        {
            if ((assistant = await Assistants.UsableAsync(db, me, membership, aid, ct)) is null)
            {
                return AuthEndpoints.Problem(400, "assistant", "There is no such assistant, or it is not shared with you.");
            }
            // What the request leaves out comes from the assistant: its model, thinking and tools.
            body = await Assistants.StartAsync(body, assistant, me, allowed, policy, models, chat.CurrentValue, ct);
        }
        var c = new Conversation { UserId = me.Id, AssistantId = assistant?.Id };
        if (await ApplyAsync(c, new ConversationChange(null, body.Thinking, null, body.Model, body.SystemPrompt, body.Temperature, body.TopP, body.MaxTokens), chat.CurrentValue, models, me, policy, small) is { } problem)
        {
            return problem;
        }
        if (ApplyTools(c, body.Tools, body.UseArgus, allowed) is { } refused)
        {
            return refused;
        }
        db.Conversations.Add(c);
        await db.SaveChangesAsync(ct);
        if (assistant is not null)
        {
            await db.Assistants.Where(x => x.Id == assistant.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChatsStarted, x => x.ChatsStarted + 1), ct);
        }
        return Results.Created($"/api/chat/conversations/{c.Id}", Shape(c, OnFor(c, allowed), null, [], false, Assistants.Brief(assistant)));
    }

    /// <summary>The ids of the tools a chat has on, of those the person may use.</summary>
    private static List<string> OnFor(Conversation c, IReadOnlyList<ToolChoice> allowed) => [.. ToolRegistry.Chosen(c.Tools, allowed).Select(t => t.Tool.Id)];

    /// <summary>A chat's tools from a request: a whole list, and/or Argus switched on or off in it.</summary>
    private static IResult? ApplyTools(Conversation c, string[]? tools, bool? useArgus, IReadOnlyList<ToolChoice> allowed)
    {
        if (tools is not null)
        {
            var known = allowed.Select(t => t.Tool.Id).ToHashSet();
            if (tools.FirstOrDefault(t => !known.Contains(t)) is { } unknown)
            {
                return AuthEndpoints.Problem(400, "tools", $"The tool {unknown} is not available to you.");
            }
            c.Tools = [.. tools.Distinct()];
        }
        if (useArgus is { } on)
        {
            var current = OnFor(c, allowed);
            current.Remove("argus");
            if (on && allowed.Any(t => t.Tool.Id == "argus"))
            {
                current.Insert(0, "argus");
            }
            c.Tools = current;
        }
        return null;
    }

    /// <summary>A chat's settings, checked. Null when all is well.</summary>
    private static async Task<IResult?> ApplyAsync(Conversation c, ConversationChange body, ChatOptions chat, ChatModels models, AppUser me, ModelPolicy policy, SmallModel small)
    {
        if (body.Thinking is { } t)
        {
            if (t != "" && !ValidThinking(t, chat))
            {
                return AuthEndpoints.Problem(400, "thinking", "Unknown thinking level.");
            }
            c.Thinking = t == "" ? null : t;
        }
        if (body.Model == SmallModel.Auto)
        {
            if (await small.OfferedAsync(me) is null)
            {
                return AuthEndpoints.Problem(403, "model", "Auto is not available to you: it needs a model for small steps that you may use.");
            }
            c.Model = SmallModel.Auto;
        }
        else if (body.Model is { } model)
        {
            if (model != "" && (await models.ListAsync()).All(m => m.Name != model))
            {
                return AuthEndpoints.Problem(400, "model", $"The gateway does not serve {model}.");
            }
            if (model != "" && !(await policy.AllowedAsync(me, [model])).Contains(model))
            {
                return AuthEndpoints.Problem(403, "model", $"You may not use {model}.");
            }
            c.Model = model == "" ? null : model;
        }
        if (body.SystemPrompt is { } prompt)
        {
            if (prompt.Length > 20000)
            {
                return AuthEndpoints.Problem(400, "instructions", "Instructions can be at most 20,000 characters.");
            }
            c.SystemPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        }
        if (body.Temperature is { } temperature)
        {
            if (temperature > 2)
            {
                return AuthEndpoints.Problem(400, "temperature", "Temperature is from 0 to 2.");
            }
            c.Temperature = temperature < 0 ? null : temperature;
        }
        if (body.TopP is { } topP)
        {
            if (topP > 1)
            {
                return AuthEndpoints.Problem(400, "top_p", "Top-p is from 0 to 1.");
            }
            c.TopP = topP < 0 ? null : topP;
        }
        if (body.MaxTokens is { } maxTokens)
        {
            var limit = (await models.ResolveAsync(c.Model))?.MaxOutput ?? int.MaxValue;
            if (maxTokens == 0 || maxTokens > limit)
            {
                return AuthEndpoints.Problem(400, "max_tokens", $"The longest answer is from 1 to {limit:N0} tokens.");
            }
            c.MaxTokens = maxTokens < 0 ? null : maxTokens;
        }
        return null;
    }

    internal static bool ValidThinking(string level, ChatOptions chat) =>
        level == "off" || ThinkingPresets.Parse(chat.ThinkingPresets).Any(p => p.Level == level);

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ToolRegistry registry, AccessService access, AnswerJobs jobs,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Sequence).ToListAsync(ct);
        var forkedFrom = c.ForkedFromId is { } from
            ? await db.Conversations.AsNoTracking().Where(x => x.Id == from && x.UserId == me.Id).Select(x => new { x.Id, x.Title }).SingleOrDefaultAsync(ct)
            : null;
        var assistant = await Assistants.OfChatAsync(db, access, me, c.AssistantId, ct);
        var tools = OnFor(c, await registry.ForAsync(await access.MembershipAsync(me, ct), ct));
        // The person's thumbs, and the arena's answers without their models until voted on.
        var quality = await Quality.ChatQuality.ForAsync(db, me.Id, id, messages, ct);
        return Results.Ok(Shape(c, tools, forkedFrom, await MessagesAsync(db, messages, ct, quality), jobs.IsAnswering(id), assistant, quality.Arenas,
            await QueuedMessages.ListAsync(db, id, ct)));
    }

    /// <summary>
    /// Messages as the page shows them, with their files (a chat, or a chat shared with the person). With the
    /// owner's quality view: their thumbs, and the arena's answers without their models until voted on.
    /// </summary>
    internal static async Task<IEnumerable<object>> MessagesAsync(AppDbContext db, IReadOnlyList<ChatMessage> messages, CancellationToken ct, Quality.ChatQuality? quality = null)
    {
        var ids = messages.SelectMany(m => ChatService.ParseIds(m.AttachmentsJson)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.FileName, a.Size, a.Truncated, a.Kind, a.ContentType, original = a.Kind != "image" && a.Data != null }).ToDictionaryAsync(a => a.Id, ct);
        return messages.Select(m => (object)new
        {
            m.Id, m.ParentId, m.Role, m.Content, m.Reasoning, m.ToolName, m.ToolCallId,
            toolCalls = m.ToolCallsJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(m.ToolCallsJson),
            attachments = ChatService.ParseIds(m.AttachmentsJson).Where(files.ContainsKey).Select(a => files[a]),
            details = (quality is null ? m.DetailsJson : quality.Details(m)) is { } details ? JsonSerializer.Deserialize<JsonElement>(details) : (JsonElement?)null,
            context = m.ContextJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(m.ContextJson),
            m.CutShort,
            status = m.Status.ToString().ToLowerInvariant(), error = quality is null ? m.Error : quality.Error(m), model = quality is null ? m.Model : quality.Model(m),
            m.PromptTokens, m.CachedTokens, m.CompletionTokens, m.ThinkingMs, m.DurationMs, m.CreatedAt, m.Summary,
            noAccess = m.Role == "tool" && ArgusMcp.IsNoAccess(m.Content),
            feedback = quality?.Feedback(m.Id),
        });
    }

    /// <param name="answering">An answer is being written: the page watches it (GET …/stream).</param>
    /// <param name="arenas">The chat's comparisons of two models (arena mode).</param>
    /// <param name="queued">Messages waiting for the answer to end (QueuedMessages).</param>
    private static object Shape(Conversation c, IReadOnlyList<string> tools, object? forkedFrom, IEnumerable<object> messages, bool answering, object? assistant = null,
        object? arenas = null, IEnumerable<object>? queued = null) =>
        new
        {
            c.Id, c.Title, c.Thinking, tools, useArgus = tools.Contains("argus"), c.Model, c.SystemPrompt, c.Temperature, c.TopP, c.MaxTokens,
            c.CurrentLeafId, c.ArchivedAt, forkedFrom, assistant, c.CreatedAt, c.UpdatedAt, answering, messages, arenas = arenas ?? Array.Empty<object>(), queued = queued ?? [],
        };

    private static async Task<IResult> UpdateAsync(Guid id, ConversationChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<ChatOptions> chat, ChatModels models,
        ToolRegistry registry, AccessService access, ModelPolicy policy, SmallModel small, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        if (body.Title is { } title)
        {
            c.Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim()[..Math.Min(200, title.Trim().Length)];
        }
        if (await ApplyAsync(c, body, chat.CurrentValue, models, me, policy, small) is { } problem)
        {
            return problem;
        }
        if (body.Tools is not null || body.UseArgus is not null)
        {
            var allowed = await registry.ForAsync(await access.MembershipAsync(me, ct), ct);
            if (ApplyTools(c, body.Tools, body.UseArgus, allowed) is { } refused)
            {
                return refused;
            }
        }
        if (body.Archived is { } archive)
        {
            c.ArchivedAt = archive ? c.ArchivedAt ?? DateTimeOffset.UtcNow : null;
        }
        if (body.AssistantId is { } aid)
        {
            if (aid != Guid.Empty && await Assistants.UsableAsync(db, me, await access.MembershipAsync(me, ct), aid, ct) is null)
            {
                return AuthEndpoints.Problem(400, "assistant", "There is no such assistant, or it is not shared with you.");
            }
            c.AssistantId = aid == Guid.Empty ? null : aid;
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// A new chat holding the branch up to one message: its questions, answers,
    /// tool calls and files, with the chat's settings. The original stays as it
    /// is. A fork ends on a question or a finished answer, never inside a tool round.
    /// </summary>
    private static async Task<IResult> ForkAsync(Guid id, ForkRequest? body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        var byId = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).ToDictionaryAsync(m => m.Id);
        if ((body?.MessageId ?? c.CurrentLeafId) is not { } end || !byId.TryGetValue(end, out var last))
        {
            return AuthEndpoints.Problem(400, "message", byId.Count == 0 ? "This chat has nothing to fork yet." : "That message is not in this chat.");
        }
        if (ChatForks.Refusal(last) is { } why)
        {
            return AuthEndpoints.Problem(400, "message", why);
        }
        var fork = new Conversation
        {
            UserId = me.Id, Title = ChatForks.Title(c.Title), Thinking = c.Thinking, Tools = c.Tools is null ? null : [.. c.Tools], Model = c.Model, SystemPrompt = c.SystemPrompt,
            Temperature = c.Temperature, TopP = c.TopP, MaxTokens = c.MaxTokens, ForkedFromId = c.Id,
        };
        ChatForks.Copy(db, fork, byId, last);
        db.Conversations.Add(fork);
        await db.SaveChangesAsync();
        return Results.Created($"/api/chat/conversations/{fork.Id}", new { fork.Id, fork.Title });
    }

    /// <summary>Yes or no to a tool call waiting for the person ("ask before running").</summary>
    private static async Task<IResult> DecideAsync(Guid id, string callId, ToolDecision body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ToolApprovals approvals,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is null)
        {
            return Results.NotFound();
        }
        return await approvals.DecideAnywhereAsync(id, callId, body.Allow, ct)
            ? Results.NoContent()
            : AuthEndpoints.Problem(409, "not_waiting", "That tool call is not waiting for an answer any more.");
    }

    /// <summary>Shows another branch: the newest line of messages below the one chosen.</summary>
    private static async Task<IResult> LeafAsync(Guid id, LeafChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).Select(m => new { m.Id, m.ParentId, m.Sequence }).ToListAsync();
        if (all.All(m => m.Id != body.MessageId))
        {
            return AuthEndpoints.Problem(400, "message", "That message is not in this chat.");
        }
        var children = all.Where(m => m.ParentId is not null).ToLookup(m => m.ParentId!.Value);
        var leaf = body.MessageId;
        while (children[leaf].OrderByDescending(m => m.Sequence).FirstOrDefault() is { } newest)
        {
            leaf = newest.Id;
        }
        c.CurrentLeafId = leaf;
        await db.SaveChangesAsync();
        return Results.Ok(new { currentLeafId = leaf });
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Retention.Retention retention)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is null)
        {
            return Results.NotFound();
        }
        // Its files go too, except those another of the person's chats (a fork) or a project still uses; under legal hold it is only hidden.
        await retention.DeleteAsync(me, [id]);
        return Results.NoContent();
    }

    private static async Task SendAsync(Guid id, NewMessage body, HttpContext http, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs,
        Safeguards.Safeguards safeguards, ModelPolicy policy, ChatModels models, SmallModel small)
    {
        var me = await Me(http.User, users);
        if (await Owned(db, id, me) is not { } c)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        // Writing in an archived chat brings it back to the list.
        c.ArchivedAt = null;
        var text = (body.Content ?? "").Trim();
        var attachments = (body.Attachments ?? []).Distinct().ToArray();
        if (text.Length == 0 && attachments.Length == 0)
        {
            await Problem(http, 400, "empty", "Write a message first.");
            return;
        }
        if (attachments.Length > 0 && await db.ChatAttachments.CountAsync(a => attachments.Contains(a.Id) && a.UserId == me.Id) != attachments.Length)
        {
            await Problem(http, 400, "attachment", "An attachment is missing or is not yours.");
            return;
        }
        Guid? parent = body.Root ? null : body.ParentId ?? c.CurrentLeafId;
        if (parent is { } p && !await db.ChatMessages.AnyAsync(m => m.ConversationId == c.Id && m.Id == p))
        {
            await Problem(http, 400, "parent", "That message is not in this chat.");
            return;
        }
        // Safeguards first: limits, blocked words, and (when on) the model's check: the small model's when there is one.
        var model = (await small.ForAsync(me, http.RequestAborted))?.Name ?? (c.Model is { } chosen && chosen != SmallModel.Auto ? chosen : null)
            ?? (await policy.ForAsync(me, await models.ListAsync(http.RequestAborted), http.RequestAborted)).Default?.Name;
        var verdict = await safeguards.CheckMessageAsync(me, text, attachments.Length, body.Research, model, http.RequestAborted);
        if (!verdict.Allowed)
        {
            await Problem(http, verdict.Status, $"safeguard_{verdict.Kind}", verdict.Reason!);
            return;
        }
        if (body.Research)
        {
            await safeguards.MarkResearchAsync(me.Id, http.RequestAborted);
        }
        // Its secrets masked, when the person's policy masks them.
        text = verdict.Text ?? text;
        await RunAsync(http, c, me, db, jobs, new AnswerOverrides(Research: body.Research, Again: body.Spoken ? Talk.Note : null), async () =>
        {
            var next = await db.ChatMessages.Where(m => m.ConversationId == c.Id).MaxAsync(m => (int?)m.Sequence) ?? 0;
            var first = next == 0;
            var question = new ChatMessage
            {
                ConversationId = c.Id, ParentId = parent, Role = "user", Sequence = next + 1, Content = text,
                AttachmentsJson = attachments.Length > 0 ? JsonSerializer.Serialize(attachments) : null,
            };
            db.ChatMessages.Add(question);
            c.CurrentLeafId = question.Id;
            if (first)
            {
                c.Title = ChatService.TitleFrom(text.Length > 0 ? text : "Attached files");
            }
            c.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return (question, first);
        });
    }

    /// <summary>A new answer beside the old one (which stays, as another branch).</summary>
    private static async Task RegenerateAsync(Guid id, HttpContext http, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs, IOptionsMonitor<ChatOptions> chat, ChatModels models)
    {
        var me = await Me(http.User, users);
        if (await Owned(db, id, me) is not { } c)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        // Writing in an archived chat brings it back to the list.
        c.ArchivedAt = null;
        var body = await ReadBodyAsync<Regenerate>(http) ?? new Regenerate();
        if (body.Thinking is { Length: > 0 } t && !ValidThinking(t, chat.CurrentValue))
        {
            await Problem(http, 400, "thinking", "Unknown thinking level.");
            return;
        }
        if (body.Model is { Length: > 0 } model && (await models.ListAsync()).All(m => m.Name != model))
        {
            await Problem(http, 400, "model", $"The gateway does not serve {model}.");
            return;
        }
        string? again = null;
        if (body.Length is { Length: > 0 } length)
        {
            if (length is not ("shorter" or "longer") || body.AnswerId is not { } answerId)
            {
                await Problem(http, 400, "length", "Length is shorter or longer, with the answer it is measured against.");
                return;
            }
            // The answer as the person read it: its words in every round, from the question down to its last message.
            var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == c.Id).ToDictionaryAsync(m => m.Id);
            var path = ChatService.PathTo(all, answerId);
            var from = path.FindLastIndex(m => m.Role == "user");
            if (!all.ContainsKey(answerId) || from < 0)
            {
                await Problem(http, 400, "length", "There is no such answer in this chat.");
                return;
            }
            again = AnswerLengths.Again(length, path.Skip(from + 1).Where(m => m.Role == "assistant").Sum(m => AnswerLengths.Words(m.Content)));
        }
        await RunAsync(http, c, me, db, jobs, new AnswerOverrides(body.Model, body.Thinking, Again: again), async () =>
        {
            var all = await db.ChatMessages.Where(m => m.ConversationId == c.Id).ToDictionaryAsync(m => m.Id);
            var question = body.MessageId is { } asked
                ? all.GetValueOrDefault(asked)
                : ChatService.PathTo(all, c.CurrentLeafId).LastOrDefault(m => m.Role == "user");
            if (question is not { Role: "user" })
            {
                throw new InvalidOperationException("There is no question to answer again.");
            }
            // The chat stays on the answer it shows until the new one exists: a stop
            // before that would otherwise leave it on the question, every answer hidden.
            return (question, false);
        });
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext http) where T : class
    {
        if (http.Request.ContentLength is 0 || !(http.Request.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the answer and streams it as server-sent events. The answer runs on
    /// its own (AnswerJobs): this request only watches it, so closing the page
    /// leaves it to finish, and the page picks it up again from GET …/stream.
    /// <paramref name="start"/> starts another kind of answer (the arena's two).
    /// </summary>
    private static async Task RunAsync(HttpContext http, Conversation c, AppUser me, AppDbContext db, AnswerJobs jobs, AnswerOverrides overrides,
        Func<Task<(ChatMessage Question, bool Titled)>> prepare, Action<AnswerJobs.Job, ChatMessage>? start = null)
    {
        // A chat with an assistant the person lost access to says so, and answers no more.
        if (await Assistants.LostAsync(db, http.RequestServices.GetRequiredService<AccessService>(), me, c, http.RequestAborted) is { } lost)
        {
            await Problem(http, 403, "assistant", lost);
            return;
        }
        if (jobs.Reserve(c.Id, me.Id) is not { } job)
        {
            await Problem(http, 409, "busy", "This chat is already answering. Stop it first, or wait.");
            return;
        }
        ChatMessage question;
        bool titled;
        try
        {
            (question, titled) = await prepare();
            // What else changed (an archived chat back in the list) is saved before the answer reads the chat.
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            jobs.Release(job);
            if (ex is InvalidOperationException)
            {
                await Problem(http, 400, "invalid", ex.Message);
                return;
            }
            throw;
        }
        job.Emit(new { type = "question", id = question.Id, parentId = question.ParentId });
        if (titled)
        {
            job.Emit(new { type = "title", title = c.Title });
        }
        if (start is null)
        {
            jobs.Start(job, question.Id, overrides with { Titled = titled });
        }
        else
        {
            start(job, question);
        }
        await StreamAsync(http, job);
    }

    /// <summary>
    /// Compacts the branch (default: the one on screen): the model summarizes it, and
    /// the next answers read the summary instead. Streams as an answer does.
    /// </summary>
    private static async Task CompactAsync(Guid id, HttpContext http, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs)
    {
        var me = await Me(http.User, users);
        if (await Owned(db, id, me) is not { } c)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var body = await ReadBodyAsync<CompactRequest>(http) ?? new CompactRequest();
        if ((body.MessageId ?? c.CurrentLeafId) is not { } leaf || !await db.ChatMessages.AnyAsync(m => m.ConversationId == id && m.Id == leaf))
        {
            await Problem(http, 400, "message", c.CurrentLeafId is null ? "This chat has nothing to compact yet." : "That message is not in this chat.");
            return;
        }
        if (await db.ChatMessages.AnyAsync(m => m.Id == leaf && (m.Role == "tool" || m.ToolCallsJson != null)))
        {
            await Problem(http, 400, "message", "Compact at a question or a finished answer.");
            return;
        }
        if (jobs.Reserve(c.Id, me.Id) is not { } job)
        {
            await Problem(http, 409, "busy", "This chat is answering. Compact it when the answer is done.");
            return;
        }
        jobs.StartCompaction(job, leaf);
        await StreamAsync(http, job);
    }

    /// <summary>The answer being written, from its start and then live; 204 when the chat is not answering.</summary>
    private static async Task WatchAsync(Guid id, HttpContext http, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs)
    {
        var me = await Me(http.User, users);
        if (await Owned(db, id, me) is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (jobs.Find(id) is not { } job)
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        await StreamAsync(http, job);
    }

    /// <summary>Stops the chat's answer; it keeps what it has, marked stopped.</summary>
    private static async Task<IResult> StopAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is null)
        {
            return Results.NotFound();
        }
        return await jobs.StopAnywhereAsync(id, ct) ? Results.Accepted() : AuthEndpoints.Problem(409, "not_answering", "This chat is not answering.");
    }

    /// <summary>The branch on screen summarized by the model for a reader (to export): made now, kept nowhere.</summary>
    private static async Task<IResult> SummaryAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ChatService chat, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        try
        {
            return Results.Ok(new { title = c.Title, summary = await chat.SummaryForReaderAsync(me, c, ct) });
        }
        catch (ChatGatewayException ex)
        {
            return AuthEndpoints.Problem(503, "summary", $"The chat could not be summarized: {ex.Message}");
        }
    }

    /// <summary>"Answer now": the answer being written stops thinking and answers with what it has.</summary>
    private static async Task<IResult> HurryAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is null)
        {
            return Results.NotFound();
        }
        return await jobs.HurryAnywhereAsync(id, ct) ? Results.Accepted() : AuthEndpoints.Problem(409, "not_answering", "This chat is not answering.");
    }

    /// <summary>Server-sent events: one JSON object per event, flushed as it happens. Leaving stops the watching, not the answer.</summary>
    private static async Task StreamAsync(HttpContext http, AnswerJobs.Job job)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        // Tell proxies not to buffer: every token should reach the browser at once.
        http.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await http.Response.Body.FlushAsync(http.RequestAborted);
            await foreach (var line in job.WatchAsync(Heartbeat, http.RequestAborted))
            {
                // A comment line: the page ignores it, a proxy sees the stream is alive.
                await http.Response.WriteAsync(line.Length == 0 ? ": still answering\n\n" : "data: " + line + "\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && http.RequestAborted.IsCancellationRequested)
        {
            // The page closed or went elsewhere: the answer goes on without it.
        }
    }

    private static async Task Problem(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { status = code, error = message });
    }

    /// <summary>
    /// A text read aloud: an MP3 from the gateway's text to speech, in the person's name (Persian in a Persian voice),
    /// passed on as the speech server writes it. Talk asks for each sentence of an answer as it is written.
    /// </summary>
    private static async Task<IResult> SpeechAsync(SpeechRequest body, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, GatewayChat gateway, ChatModels models, CancellationToken ct)
    {
        var me = await Me(p, users);
        var text = Tools.Voices.Plain(body.Text ?? "");
        if (text.Length == 0)
        {
            return AuthEndpoints.Problem(400, "text", "Nothing to read aloud.");
        }
        if (await models.OfModeAsync("audio_speech", null, ct) is null)
        {
            return AuthEndpoints.Problem(503, "no_speech", "The gateway has no text to speech model (the audio module).");
        }
        var (model, voice) = Tools.Voices.For(text);
        try
        {
            var res = await gateway.OpenSpeechAsync(model, text, voice, me.Email!, ct);
            http.Response.RegisterForDispose(res);
            return Results.Stream(await res.Content.ReadAsStreamAsync(ct), "audio/mpeg");
        }
        catch (ChatGatewayException ex)
        {
            return AuthEndpoints.Problem(502, "gateway", ex.Message);
        }
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<ChatOptions> monitor,
        Media media, Safeguards.Safeguards safeguards)
    {
        var options = monitor.CurrentValue;
        var me = await Me(p, users);
        // The setting, not Kestrel's 30 MB default, decides the size (plus room for the form around the file).
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = options.MaxUploadBytes + 1024 * 1024;
        }
        if (!request.HasFormContentType || (await request.ReadFormAsync()).Files is not { Count: 1 } files)
        {
            return AuthEndpoints.Problem(400, "file", "Send exactly one file.");
        }
        var file = files[0];
        if (file.Length > options.MaxUploadBytes)
        {
            return AuthEndpoints.Problem(413, "too_large", $"{file.FileName} is larger than {options.MaxUploadBytes / 1024 / 1024} MB.");
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        if (Attachments.ImageType(file.FileName, ms.ToArray()) is { } imageType)
        {
            var image = new ChatAttachment
            {
                UserId = me.Id, FileName = Path.GetFileName(file.FileName), ContentType = imageType, Size = file.Length,
                Text = "", Kind = "image", Data = ms.ToArray(),
            };
            db.ChatAttachments.Add(image);
            await db.SaveChangesAsync();
            return Results.Ok(new { image.Id, image.FileName, image.Size, image.Kind, image.ContentType, chars = 0, image.Truncated });
        }
        // A sound or a video: what the models take is made once, now, in the sandbox.
        if (Media.Detect(file.FileName, file.ContentType, ms.ToArray()) is { } kind)
        {
            var m = new ChatAttachment
            {
                UserId = me.Id, FileName = Path.GetFileName(file.FileName), ContentType = kind.ContentType, Size = file.Length, Text = "", Kind = kind.Kind, Data = ms.ToArray(),
            };
            db.ChatAttachments.Add(m);
            try
            {
                await media.PrepareAsync(m, request.HttpContext.RequestAborted);
            }
            catch (MediaException ex)
            {
                return AuthEndpoints.Problem(400, "unreadable", ex.Message);
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { m.Id, m.FileName, m.Size, m.Kind, m.ContentType, chars = 0, m.Truncated, m.Seconds });
        }
        try
        {
            var (text, truncated, converted) = Attachments.Extract(file.FileName, file.ContentType ?? "", ms.ToArray(), options.MaxAttachmentChars);
            // Secrets in the file: refused, or masked (and then its original, which still holds them, is not kept).
            var verdict = await safeguards.CheckFileAsync(me, Path.GetFileName(file.FileName), text, request.HttpContext.RequestAborted);
            if (!verdict.Allowed)
            {
                return AuthEndpoints.Problem(verdict.Status, $"safeguard_{verdict.Kind}", verdict.Reason!);
            }
            if (verdict.Text is { } masked)
            {
                (text, converted) = (masked, false);
            }
            var a = new ChatAttachment
            {
                UserId = me.Id, FileName = Path.GetFileName(file.FileName), ContentType = file.ContentType ?? "application/octet-stream",
                Size = file.Length, Text = text, Truncated = truncated,
                // A document's own bytes are kept beside its text: the Python sandbox opens the real .xlsx.
                Data = converted ? ms.ToArray() : null,
            };
            db.ChatAttachments.Add(a);
            await db.SaveChangesAsync();
            return Results.Ok(new { a.Id, a.FileName, a.Size, a.Kind, a.ContentType, chars = text.Length, a.Truncated, original = a.Data is not null });
        }
        catch (AttachmentException ex)
        {
            return AuthEndpoints.Problem(400, "unreadable", ex.Message);
        }
    }

    /// <summary>
    /// An attachment for the Files panel and image previews: the picture itself, or
    /// the text that went to the model. Its owner's only; never served as HTML.
    /// </summary>
    private static async Task<IResult> ContentAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, HttpContext http)
    {
        var me = await Me(p, users);
        var a = await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (a is null || (a.UserId != me.Id && !await ShareEndpoints.MayReadFileAsync(db, access, me, id, http.RequestAborted)))
        {
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "private, max-age=3600";
        // ?download: the file itself (a document's original, a file Python made), to save, never to render.
        if (http.Request.Query.ContainsKey("download"))
        {
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return a.Data is not null
                ? Results.File(a.Data, "application/octet-stream", a.FileName)
                : Results.File(System.Text.Encoding.UTF8.GetBytes(a.Text), "text/plain; charset=utf-8", a.FileName);
        }
        if (a.Kind is "audio" or "video" && a.Data is not null)
        {
            // Sound and video players seek: ranges.
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(a.Data, a.ContentType, enableRangeProcessing: true);
        }
        return a.Kind == "image" && a.Data is not null
            ? Results.File(a.Data, a.ContentType)
            : Results.Text(a.Text, "text/plain; charset=utf-8");
    }

    /// <summary>
    /// A document's pages as pictures (drawn in the sandbox as they are first asked for): how many it has, and how
    /// many are drawn, at least <paramref name="upTo"/> when it has them (the next twenty at most per request).
    /// </summary>
    private static async Task<IResult> PagesAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, DocumentPages pages, CancellationToken ct,
        int upTo = DocumentPages.Batch)
    {
        var me = await Me(p, users);
        var a = await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (a is null || (a.UserId != me.Id && !await ShareEndpoints.MayReadFileAsync(db, access, me, id, ct)))
        {
            return Results.NotFound();
        }
        if (!DocumentPages.CanDraw(a))
        {
            return AuthEndpoints.Problem(400, "kind", "Only PDF, Word, PowerPoint, Excel and OpenDocument files have pages to show.");
        }
        try
        {
            var (total, drawn) = await pages.PagesAsync(a, upTo, ct);
            return Results.Ok(new { total, drawn, pages = Enumerable.Range(1, drawn).Select(n => $"/api/chat/attachments/{id}/pages/{n}") });
        }
        catch (DocumentPagesException ex)
        {
            return AuthEndpoints.Problem(503, "pages", ex.Message);
        }
    }

    private static async Task<IResult> PageAsync(Guid id, int number, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, HttpContext http,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        var page = await db.AttachmentPages.AsNoTracking().Where(x => x.AttachmentId == id && x.Number == number).Select(x => x.Data).SingleOrDefaultAsync(ct);
        if (page is null || (!await db.ChatAttachments.AnyAsync(a => a.Id == id && a.UserId == me.Id, ct) && !await ShareEndpoints.MayReadFileAsync(db, access, me, id, ct)))
        {
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(page, "image/jpeg");
    }
}
