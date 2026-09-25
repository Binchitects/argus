using System.Collections.Concurrent;
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

/// <summary>A new chat. Tools: the tool ids it may call (default: those on in new chats); UseArgus: Argus on or off in that list.</summary>
public sealed record NewConversation(string? Thinking = null, bool? UseArgus = null, string? Model = null, string? SystemPrompt = null,
    double? Temperature = null, double? TopP = null, int? MaxTokens = null, string[]? Tools = null);

/// <summary>Only what is sent changes. For the numbers, a negative value clears them (back to the model's default).</summary>
public sealed record ConversationChange(string? Title = null, string? Thinking = null, bool? UseArgus = null, string? Model = null,
    string? SystemPrompt = null, double? Temperature = null, double? TopP = null, int? MaxTokens = null, bool? Archived = null, string[]? Tools = null);

/// <summary>The person's answer to a call waiting for them ("ask before running").</summary>
public sealed record ToolDecision(bool Allow);

/// <summary>Fork up to this message (default: the end of the branch on screen).</summary>
public sealed record ForkRequest(Guid? MessageId = null);

/// <summary>
/// A question. It follows <see cref="ParentId"/> (default: the end of the branch on
/// screen); <see cref="Root"/> starts a new first question. Editing a question is
/// sending its new text with the old one's parent: a sibling, so both stay.
/// </summary>
public sealed record NewMessage(string Content, Guid[]? Attachments = null, Guid? ParentId = null, bool Root = false);

/// <summary>Answer a question again (default: the last one on the branch), optionally with another model or thinking level.</summary>
public sealed record Regenerate(Guid? MessageId = null, string? Model = null, string? Thinking = null);

public sealed record LeafChange(Guid MessageId);

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One answer at a time per conversation: a second tab cannot interleave two.</summary>
    private static readonly ConcurrentDictionary<Guid, byte> Answering = new();

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
        g.MapPut("/conversations/{id:guid}/leaf", LeafAsync);
        g.MapPost("/conversations/{id:guid}/fork", ForkAsync);
        g.MapPost("/conversations/{id:guid}/tool-calls/{callId}", DecideAsync);
        g.MapPost("/attachments", UploadAsync).DisableAntiforgery();
        g.MapGet("/attachments/{id:guid}/content", ContentAsync);
    }

    private static async Task<IResult> Config(ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access, ModelPolicy policy,
        IOptions<StackOptions> stack, IOptions<ArgusOptions> argusOptions, ArgusMcp argus, ChatModels models, IOptionsMonitor<ChatOptions> chat, CancellationToken ct)
    {
        var me = await Me(p, users);
        // The models this person may use; a model of the engine that is not loaded is listed, marked so.
        var (list, first, onEngine) = await policy.ForAsync(me, await models.ListAsync(ct), ct);
        var tools = await registry.ForAsync(await access.MembershipAsync(me, ct), ct);
        return Results.Ok(new
        {
            model = first?.Name ?? stack.Value.ModelName,
            models = list.Select(m => new
            {
                m.Name, m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking, loaded = policy.Ready(m.Name, onEngine),
                prices = new { input = m.InputPerMtok, cachedInput = m.CachedInputPerMtok, output = m.OutputPerMtok },
            }),
            presets = ThinkingPresets.Parse(stack.Value.ThinkingPresets),
            defaultThinking = string.Equals(stack.Value.ModelEnableThinking, "false", StringComparison.OrdinalIgnoreCase) ? "off" : stack.Value.ModelReasoningEffort,
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
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, string? q = null, bool archived = false)
    {
        var me = await Me(p, users);
        var query = db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id && (archived ? c.ArchivedAt != null : c.ArchivedAt == null));
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(c => EF.Functions.ILike(c.Title, "%" + q.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }
        return Results.Ok(await query.OrderByDescending(c => c.UpdatedAt).Take(300)
            .Select(c => new { c.Id, c.Title, c.UpdatedAt, c.ArchivedAt }).ToListAsync());
    }

    private static async Task<IResult> CreateAsync(NewConversation body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptions<StackOptions> stack, ChatModels models,
        ToolRegistry registry, AccessService access, ModelPolicy policy, CancellationToken ct)
    {
        var me = await Me(p, users);
        var c = new Conversation { UserId = me.Id };
        if (await ApplyAsync(c, new ConversationChange(null, body.Thinking, null, body.Model, body.SystemPrompt, body.Temperature, body.TopP, body.MaxTokens), stack.Value, models, me, policy) is { } problem)
        {
            return problem;
        }
        var allowed = await registry.ForAsync(await access.MembershipAsync(me, ct), ct);
        if (ApplyTools(c, body.Tools, body.UseArgus, allowed) is { } refused)
        {
            return refused;
        }
        db.Conversations.Add(c);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/chat/conversations/{c.Id}", Shape(c, OnFor(c, allowed), null, []));
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
    private static async Task<IResult?> ApplyAsync(Conversation c, ConversationChange body, StackOptions stack, ChatModels models, AppUser me, ModelPolicy policy)
    {
        if (body.Thinking is { } t)
        {
            if (t != "" && !ValidThinking(t, stack))
            {
                return AuthEndpoints.Problem(400, "thinking", "Unknown thinking level.");
            }
            c.Thinking = t == "" ? null : t;
        }
        if (body.Model is { } model)
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

    private static bool ValidThinking(string level, StackOptions stack) =>
        level == "off" || ThinkingPresets.Parse(stack.ThinkingPresets).Any(p => p.Level == level);

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ToolRegistry registry, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Sequence).ToListAsync(ct);
        var ids = messages.SelectMany(m => ChatService.ParseIds(m.AttachmentsJson)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.FileName, a.Size, a.Truncated, a.Kind, a.ContentType }).ToDictionaryAsync(a => a.Id, ct);
        var forkedFrom = c.ForkedFromId is { } from
            ? await db.Conversations.AsNoTracking().Where(x => x.Id == from && x.UserId == me.Id).Select(x => new { x.Id, x.Title }).SingleOrDefaultAsync(ct)
            : null;
        var tools = OnFor(c, await registry.ForAsync(await access.MembershipAsync(me, ct), ct));
        return Results.Ok(Shape(c, tools, forkedFrom, messages.Select(m => (object)new
        {
            m.Id, m.ParentId, m.Role, m.Content, m.Reasoning, m.ToolName, m.ToolCallId,
            toolCalls = m.ToolCallsJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(m.ToolCallsJson),
            attachments = ChatService.ParseIds(m.AttachmentsJson).Where(files.ContainsKey).Select(a => files[a]),
            status = m.Status.ToString().ToLowerInvariant(), m.Error, m.Model,
            m.PromptTokens, m.CachedTokens, m.CompletionTokens, m.ThinkingMs, m.DurationMs, m.CreatedAt,
            noAccess = m.Role == "tool" && ArgusMcp.IsNoAccess(m.Content),
        })));
    }

    private static object Shape(Conversation c, IReadOnlyList<string> tools, object? forkedFrom, IEnumerable<object> messages) =>
        new
        {
            c.Id, c.Title, c.Thinking, tools, useArgus = tools.Contains("argus"), c.Model, c.SystemPrompt, c.Temperature, c.TopP, c.MaxTokens,
            c.CurrentLeafId, c.ArchivedAt, forkedFrom, c.CreatedAt, c.UpdatedAt, messages,
        };

    private static async Task<IResult> UpdateAsync(Guid id, ConversationChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptions<StackOptions> stack, ChatModels models,
        ToolRegistry registry, AccessService access, ModelPolicy policy, CancellationToken ct)
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
        if (await ApplyAsync(c, body, stack.Value, models, me, policy) is { } problem)
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
        if (last.Role == "tool" || last.ToolCallsJson is not null)
        {
            return AuthEndpoints.Problem(400, "message", "Fork from a question or a finished answer.");
        }
        var path = new List<ChatMessage>();
        for (var m = last; m is not null; m = m.ParentId is { } parent ? byId.GetValueOrDefault(parent) : null)
        {
            path.Add(m);
        }
        path.Reverse();
        var title = (c.Title + " (fork)")[..Math.Min(200, c.Title.Length + 7)];
        var fork = new Conversation
        {
            UserId = me.Id, Title = title, Thinking = c.Thinking, Tools = c.Tools is null ? null : [.. c.Tools], Model = c.Model, SystemPrompt = c.SystemPrompt,
            Temperature = c.Temperature, TopP = c.TopP, MaxTokens = c.MaxTokens, ForkedFromId = c.Id,
        };
        var copies = new Dictionary<Guid, Guid>();
        var sequence = 0;
        foreach (var m in path)
        {
            var copy = new ChatMessage
            {
                ConversationId = fork.Id, ParentId = m.ParentId is { } parent ? copies[parent] : null, Sequence = ++sequence, Role = m.Role,
                Content = m.Content, Reasoning = m.Reasoning, ToolCallsJson = m.ToolCallsJson, ToolCallId = m.ToolCallId, ToolName = m.ToolName,
                AttachmentsJson = m.AttachmentsJson, Model = m.Model, PromptTokens = m.PromptTokens, CachedTokens = m.CachedTokens,
                CompletionTokens = m.CompletionTokens, ThinkingMs = m.ThinkingMs, DurationMs = m.DurationMs, Status = m.Status, Error = m.Error,
                CreatedAt = m.CreatedAt,
            };
            copies[m.Id] = copy.Id;
            db.ChatMessages.Add(copy);
        }
        fork.CurrentLeafId = copies[last.Id];
        db.Conversations.Add(fork);
        await db.SaveChangesAsync();
        return Results.Created($"/api/chat/conversations/{fork.Id}", new { fork.Id, fork.Title });
    }

    /// <summary>Yes or no to a tool call waiting for the person ("ask before running").</summary>
    private static async Task<IResult> DecideAsync(Guid id, string callId, ToolDecision body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ToolApprovals approvals)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is null)
        {
            return Results.NotFound();
        }
        return approvals.Decide(id, callId, body.Allow)
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

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db)
    {
        var me = await Me(p, users);
        if (await Owned(db, id, me) is not { } c)
        {
            return Results.NotFound();
        }
        // Its files go too, except those another of the person's chats (a fork) still uses.
        var used = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id && m.AttachmentsJson != null).Select(m => m.AttachmentsJson).ToListAsync();
        var elsewhere = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId != id && m.AttachmentsJson != null && db.Conversations.Any(x => x.Id == m.ConversationId && x.UserId == me.Id))
            .Select(m => m.AttachmentsJson).ToListAsync();
        var keep = elsewhere.SelectMany(ChatService.ParseIds).ToHashSet();
        var files = used.SelectMany(ChatService.ParseIds).Where(a => !keep.Contains(a)).ToList();
        db.Conversations.Remove(c);
        await db.SaveChangesAsync();
        await db.ChatAttachments.Where(a => a.UserId == me.Id && files.Contains(a.Id)).ExecuteDeleteAsync();
        return Results.NoContent();
    }

    private static async Task SendAsync(Guid id, NewMessage body, HttpContext http, UserManager<AppUser> users, AppDbContext db, ChatService chat)
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
        await RunAsync(http, c, me, db, chat, new AnswerOverrides(), async () =>
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
    private static async Task RegenerateAsync(Guid id, HttpContext http, UserManager<AppUser> users, AppDbContext db, ChatService chat, IOptions<StackOptions> stack, ChatModels models)
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
        if (body.Thinking is { Length: > 0 } t && !ValidThinking(t, stack.Value))
        {
            await Problem(http, 400, "thinking", "Unknown thinking level.");
            return;
        }
        if (body.Model is { Length: > 0 } model && (await models.ListAsync()).All(m => m.Name != model))
        {
            await Problem(http, 400, "model", $"The gateway does not serve {model}.");
            return;
        }
        await RunAsync(http, c, me, db, chat, new AnswerOverrides(body.Model, body.Thinking), async () =>
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

    /// <summary>Server-sent events: one JSON object per event, flushed as it happens.</summary>
    private static async Task RunAsync(HttpContext http, Conversation c, AppUser me, AppDbContext db, ChatService chat, AnswerOverrides overrides, Func<Task<(ChatMessage Question, bool Titled)>> prepare)
    {
        if (!Answering.TryAdd(c.Id, 0))
        {
            await Problem(http, 409, "busy", "This chat is already answering. Stop it first, or wait.");
            return;
        }
        try
        {
            ChatMessage question;
            bool titled;
            try
            {
                (question, titled) = await prepare();
            }
            catch (InvalidOperationException ex)
            {
                await Problem(http, 400, "invalid", ex.Message);
                return;
            }
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            // Tell proxies not to buffer: every token should reach the browser at once.
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.Body.FlushAsync();
            async Task Emit(object e)
            {
                await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(e, Json) + "\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
            await Emit(new { type = "question", id = question.Id, parentId = question.ParentId });
            if (titled)
            {
                await Emit(new { type = "title", title = c.Title });
            }
            try
            {
                await chat.AnswerAsync(me, c, question, overrides, Emit, http.RequestAborted);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                // The person left or pressed stop; ChatService saved what there was.
            }
        }
        finally
        {
            Answering.TryRemove(c.Id, out _);
        }
    }

    private static async Task Problem(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { status = code, error = message });
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<ChatOptions> monitor)
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
        try
        {
            var (text, truncated, converted) = Attachments.Extract(file.FileName, file.ContentType ?? "", ms.ToArray(), options.MaxAttachmentChars);
            var a = new ChatAttachment
            {
                UserId = me.Id, FileName = Path.GetFileName(file.FileName), ContentType = file.ContentType ?? "application/octet-stream",
                Size = file.Length, Text = text, Truncated = truncated,
                // A document's own bytes are kept beside its text: the Python sandbox opens the real .xlsx.
                Data = converted ? ms.ToArray() : null,
            };
            db.ChatAttachments.Add(a);
            await db.SaveChangesAsync();
            return Results.Ok(new { a.Id, a.FileName, a.Size, a.Kind, a.ContentType, chars = text.Length, a.Truncated });
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
    private static async Task<IResult> ContentAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, HttpContext http)
    {
        var me = await Me(p, users);
        var a = await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == me.Id);
        if (a is null)
        {
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "private, max-age=3600";
        return a.Kind == "image" && a.Data is not null
            ? Results.File(a.Data, a.ContentType)
            : Results.Text(a.Text, "text/plain; charset=utf-8");
    }
}
