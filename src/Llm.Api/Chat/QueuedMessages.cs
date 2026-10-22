using System.Security.Claims;
using Llm.Api.Access;
using System.Text.Json;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Api.Safeguards;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A message written while the chat answers, to send when the answer ends.</summary>
/// <param name="Research">Deep research for this one.</param>
/// <param name="Quote">The part of an earlier answer it replies to (ChatQuotes).</param>
/// <param name="Mentions">The room's members it names.</param>
/// <param name="ReplyTo">A room's message it replies to: a reply to one of the model's answers is a question to it.</param>
/// <param name="Skill">A skill it uses (/name and its parameters): a question to the model, in a room too.</param>
public sealed record QueueRequest(string? Content, Guid[]? Attachments = null, bool Research = false, string? Quote = null, Guid[]? Mentions = null, Guid? ReplyTo = null,
    Guid[]? Acknowledged = null, string? Justification = null, SkillUse? Skill = null);

/// <summary>
/// Messages sent while a chat answers wait here, on the server: a reload or another
/// tab still shows them, and each becomes the chat's next question when the answer
/// before it ends, however it ended (AnswerJobs asks for the next one). A message that
/// waits after a restart goes once the app is back.
/// </summary>
public sealed partial class QueuedMessages(IServiceScopeFactory scopes, AnswerJobs jobs, RoomHub rooms, Studio.StudioRuns studio, ILogger<QueuedMessages> logger)
{
    /// <summary>How many may wait in one chat.</summary>
    public const int Max = 10;

    /// <summary>How many may wait in a room, and of one member's.</summary>
    public const int MaxInRoom = 20;
    public const int MaxEachInRoom = 3;

    /// <summary>
    /// The chat's first queued message becomes its next question, on the branch on screen, and is
    /// answered (in the background, as any answer). False when nothing waits or the chat is answering.
    /// </summary>
    public async Task<bool> StartNextAsync(Guid conversationId)
    {
        for (var attempt = 0; attempt <= Max; attempt++)
        {
            AnswerJobs.Job? job = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var next = await db.QueuedMessages.Where(q => q.ConversationId == conversationId)
                    .OrderBy(q => q.Position).ThenBy(q => q.CreatedAt).FirstOrDefaultAsync();
                if (next is null || await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId) is not { } c)
                {
                    return false;
                }
                // Code Arena is answering in it: this one goes when its turn ends.
                if (CodeArenaSync.Answering(c))
                {
                    return false;
                }
                // Answering: this one goes when that answer ends. The answer is its sender's (in a room, not the owner's).
                if (!next.ForPeople)
                {
                    job = jobs.Reserve(c.Id, next.UserId);
                    if (job is null)
                    {
                        return false;
                    }
                }
                else if (jobs.IsAnswering(c.Id))
                {
                    return false;
                }
                // Its files, those still there.
                var ids = ChatService.ParseIds(next.AttachmentsJson).ToList();
                var kept = await db.ChatAttachments.Where(a => ids.Contains(a.Id) && a.UserId == next.UserId).Select(a => a.Id).ToListAsync();
                var files = ids.Where(kept.Contains).ToList();
                var last = await db.ChatMessages.Where(m => m.ConversationId == c.Id).MaxAsync(m => (int?)m.Sequence) ?? 0;
                var question = new ChatMessage
                {
                    ConversationId = c.Id, ParentId = c.CurrentLeafId, Role = "user", Sequence = last + 1, Content = next.Content, Quote = next.Quote, Skill = next.Skill,
                    AttachmentsJson = files.Count > 0 ? JsonSerializer.Serialize(files) : null,
                    AuthorId = next.UserId, ForPeople = next.ForPeople, Mentions = next.Mentions, ReplyToId = next.ReplyToId,
                };
                db.ChatMessages.Add(question);
                db.QueuedMessages.Remove(next);
                c.CurrentLeafId = question.Id;
                c.ArchivedAt = null;
                c.UpdatedAt = DateTimeOffset.UtcNow;
                // A room keeps the name it was given.
                var titled = last == 0 && !c.IsRoom;
                if (titled)
                {
                    c.Title = ChatService.TitleFrom(next.Content.Length > 0 ? next.Content : SkillLibrary.Kept(next.Skill)?.Title ?? "Attached files");
                }
                await db.SaveChangesAsync();
                if (job is null)
                {
                    // For the people only: saved, and the next in line goes; a room's workflows hear it.
                    rooms.Said(c.Id, question.Id);
                    if (c.IsRoom && question.AuthorId is { } author)
                    {
                        studio.RoomMessage(c.Id, author, question.Content);
                    }
                    attempt = -1;
                    continue;
                }
                job.Emit(new { type = "question", id = question.Id, parentId = question.ParentId });
                if (titled)
                {
                    job.Emit(new { type = "title", title = c.Title });
                }
                jobs.Start(job, question.Id, new AnswerOverrides(Model: next.Model, Research: next.Research));
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Cancelled as it was about to go: the next one in line, if any.
                ReleaseIfHeld(job);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                ReleaseIfHeld(job);
                LogFailed(logger, conversationId, ex);
                return false;
            }
        }
        return false;
    }

    private void ReleaseIfHeld(AnswerJobs.Job? job)
    {
        if (job is not null)
        {
            jobs.Release(job);
        }
    }

    /// <summary>Every chat with messages waiting and no answer running (after a restart): their next ones go.</summary>
    public async Task StartWaitingAsync(CancellationToken ct)
    {
        List<Guid> chats;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            chats = await db.QueuedMessages.Select(q => q.ConversationId).Distinct().ToListAsync(ct);
        }
        foreach (var chat in chats.Where(c => !jobs.IsAnswering(c)))
        {
            ct.ThrowIfCancellationRequested();
            await StartNextAsync(chat);
        }
    }

    /// <summary>The chat's queued messages in line order, as the page shows them.</summary>
    public static async Task<List<object>> ListAsync(AppDbContext db, Guid conversationId, CancellationToken ct)
    {
        var queued = await db.QueuedMessages.AsNoTracking().Where(q => q.ConversationId == conversationId)
            .OrderBy(q => q.Position).ThenBy(q => q.CreatedAt).ToListAsync(ct);
        var ids = queued.SelectMany(q => ChatService.ParseIds(q.AttachmentsJson)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.FileName, a.Size, a.Truncated, a.Kind, a.ContentType, original = a.Kind != "image" && a.Data != null }).ToDictionaryAsync(a => a.Id, ct);
        return [.. queued.Select(q => (object)new
        {
            q.Id, q.Content, q.Quote, skill = SkillLibrary.Kept(q.Skill), q.Research, q.CreatedAt, q.UserId, q.ForPeople,
            attachments = ChatService.ParseIds(q.AttachmentsJson).Where(files.ContainsKey).Select(a => files[a]),
        })];
    }

    public static void MapQueue(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat/conversations/{id:guid}/queue").PersonOnly();
        g.MapPost("", AddAsync).RequirePermission(Llm.Core.Access.Permissions.Chat);
        g.MapDelete("/{queuedId:guid}", CancelAsync);
        g.MapPost("/{queuedId:guid}/now", NowAsync);
    }

    private static async Task<(AppUser Me, Conversation? Chat)> OwnedAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        return (me, await RoomAccess.FindAsync(db, id, me, Need.Write, ct));
    }

    /// <summary>
    /// Queues a message, checked as a sent one is (its files, the safeguards). The chat's line, and
    /// whether it is answering: one that ended meanwhile takes the message at once.
    /// </summary>
    private static async Task<IResult> AddAsync(Guid id, QueueRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        Safeguards.Safeguards safeguards, ModelPolicy policy, ChatModels models, QueuedMessages queue, AnswerJobs jobs, Tools.ToolRegistry registry, Access.AccessService access,
        Notifications.Notifier notifier, CancellationToken ct)
    {
        var (me, c) = await OwnedAsync(id, p, users, db, ct);
        if (c is null)
        {
            return Results.NotFound();
        }
        var text = (body.Content ?? "").Trim();
        var attachments = (body.Attachments ?? []).Distinct().ToArray();
        // A skill used: filled in now, as the person sees it, and sent so when its turn comes.
        SkillCall? skill = null;
        if (body.Skill is { Name: { Length: > 0 } skillName } use)
        {
            if (await SkillEndpoints.FindAsync(me, skillName, access, db, ct) is not { } found)
            {
                return AuthEndpoints.Problem(400, "skill", $"There is no skill /{skillName.TrimStart('/')} you may use.");
            }
            (skill, var missing) = SkillLibrary.Use(found, use.Args, text);
            if (missing is not null)
            {
                return AuthEndpoints.Problem(400, "skill", missing);
            }
        }
        if (text.Length == 0 && attachments.Length == 0 && skill is null)
        {
            return AuthEndpoints.Problem(400, "empty", "Write a message first.");
        }
        var quote = ChatQuotes.Clean(body.Quote);
        if (ChatQuotes.Check(quote) is { } tooLong)
        {
            return AuthEndpoints.Problem(400, "quote", tooLong);
        }
        if (attachments.Length > 0 && await db.ChatAttachments.CountAsync(a => attachments.Contains(a.Id) && a.UserId == me.Id, ct) != attachments.Length)
        {
            return AuthEndpoints.Problem(400, "attachment", "An attachment is missing or is not yours.");
        }
        if (await db.QueuedMessages.CountAsync(q => q.ConversationId == id, ct) >= (c.IsRoom ? MaxInRoom : Max))
        {
            return AuthEndpoints.Problem(409, "queue_full", $"{(c.IsRoom ? MaxInRoom : Max)} messages wait in this chat already: send one now, or cancel one.");
        }
        if (c.IsRoom && await db.QueuedMessages.CountAsync(q => q.ConversationId == id && q.UserId == me.Id, ct) >= MaxEachInRoom)
        {
            return AuthEndpoints.Problem(409, "queue_full", $"{MaxEachInRoom} of your messages wait in this room already: wait for them to go, or cancel one.");
        }
        // In a room the model answers only a message that names it (@Arena) or replies to one of its answers; the rest
        // are for the people. A reply to an answer carries the answer to the model, as a quote does.
        ChatMessage? repliedTo = null;
        if (body.ReplyTo is { } replyId)
        {
            repliedTo = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == replyId && m.ConversationId == id, ct);
            if (repliedTo is null || !c.IsRoom)
            {
                return AuthEndpoints.Problem(400, "reply", "That message is not in this room.");
            }
        }
        var toModel = !c.IsRoom || skill is not null || RoomEndpoints.NamesArena(text) || repliedTo is { Role: "assistant" };
        var forPeople = !toModel;
        if (repliedTo is { Role: "assistant" } answer && quote is null)
        {
            quote = ChatQuotes.Cut(answer.Content);
        }
        var mentions = c.IsRoom ? await RoomEndpoints.MentionsAsync(db, c, body.Mentions, ct) : [];
        if (body.Research && !await registry.MayUseAsync(await access.MembershipAsync(me, ct), Tools.ResearchTool.ToolId, ct))
        {
            return AuthEndpoints.Problem(403, "research", Tools.ResearchTool.NotYours);
        }
        var model = c.Model ?? (await policy.ForAsync(me, await models.ListAsync(ct), ct)).Default?.Name;
        // A skill's instructions are read with the words: the rules see what the model would.
        var verdict = await safeguards.CheckMessageAsync(me, skill is null ? text : SkillLibrary.ForModel(skill, text), attachments.Length, body.Research, model, ct, quote,
            new SendContext(body.Acknowledged, body.Justification, c.Id));
        if (!verdict.Allowed)
        {
            return Safeguards.Safeguards.Problem(verdict);
        }
        if (body.Research)
        {
            await safeguards.MarkResearchAsync(me.Id, ct);
        }
        var last = await db.QueuedMessages.Where(q => q.ConversationId == id).MaxAsync(q => (int?)q.Position, ct) ?? 0;
        db.QueuedMessages.Add(new QueuedMessage
        {
            // Its secrets masked, when the person's policy masks them (with a skill, as the model reads it: ChatService masks it).
            ConversationId = id, Content = skill is null ? verdict.Text ?? text : text, Skill = skill is null ? null : SkillLibrary.Keep(skill), Quote = verdict.Quote ?? quote, Research = body.Research && !forPeople, Position = last + 1,
            AttachmentsJson = attachments.Length > 0 ? JsonSerializer.Serialize(attachments) : null,
            UserId = me.Id, ForPeople = forPeople, Mentions = mentions, ReplyToId = repliedTo?.Id, Model = verdict.Model,
        });
        // Writing in an archived chat brings it back to the list.
        c.ArchivedAt = null;
        await db.SaveChangesAsync(ct);
        if (c.IsRoom)
        {
            await RoomEndpoints.TellAsync(notifier, c, me, mentions, repliedTo, text, ct);
        }
        await queue.StartNextAsync(id);
        return Results.Ok(new { queued = await ListAsync(db, id, ct), answering = jobs.IsAnswering(id) });
    }

    /// <summary>Takes a message out of line; 404 when it is not waiting any more (it went).</summary>
    private static async Task<IResult> CancelAsync(Guid id, Guid queuedId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var (me, c) = await OwnedAsync(id, p, users, db, ct);
        if (c is null)
        {
            return Results.NotFound();
        }
        // In a room a member takes out their own; its owner, any.
        var owner = c.UserId == me.Id;
        return await db.QueuedMessages.Where(q => q.Id == queuedId && q.ConversationId == id && (owner || q.UserId == me.Id)).ExecuteDeleteAsync(ct) == 0
            ? AuthEndpoints.Problem(404, "not_queued", "That message is not waiting any more: it was sent.")
            : Results.NoContent();
    }

    /// <summary>Send now: the message goes first in line, and the answer running stops (keeping what it has) so it goes at once.</summary>
    private static async Task<IResult> NowAsync(Guid id, Guid queuedId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        QueuedMessages queue, AnswerJobs jobs, CancellationToken ct)
    {
        var (me, c) = await OwnedAsync(id, p, users, db, ct);
        // In a room only its owner puts one first (it stops the answer running, someone else's maybe).
        if (c is null || c.UserId != me.Id)
        {
            return Results.NotFound();
        }
        if (await db.QueuedMessages.SingleOrDefaultAsync(q => q.Id == queuedId && q.ConversationId == id, ct) is not { } q)
        {
            return AuthEndpoints.Problem(404, "not_queued", "That message is not waiting any more: it was sent.");
        }
        q.Position = (await db.QueuedMessages.Where(x => x.ConversationId == id).MinAsync(x => x.Position, ct)) - 1;
        await db.SaveChangesAsync(ct);
        if (!jobs.Stop(id))
        {
            await queue.StartNextAsync(id);
        }
        return Results.Ok(new { queued = await ListAsync(db, id, ct), answering = jobs.IsAnswering(id) });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {Conversation}: its next queued message could not start")]
    private static partial void LogFailed(ILogger logger, Guid conversation, Exception ex);
}
