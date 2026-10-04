using System.Security.Claims;
using System.Text.Json;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A message written while the chat answers, to send when the answer ends.</summary>
/// <param name="Research">Deep research for this one.</param>
public sealed record QueueRequest(string? Content, Guid[]? Attachments = null, bool Research = false);

/// <summary>
/// Messages sent while a chat answers wait here, on the server: a reload or another
/// tab still shows them, and each becomes the chat's next question when the answer
/// before it ends, however it ended (AnswerJobs asks for the next one). A message that
/// waits after a restart goes once the app is back.
/// </summary>
public sealed partial class QueuedMessages(IServiceScopeFactory scopes, AnswerJobs jobs, ILogger<QueuedMessages> logger)
{
    /// <summary>How many may wait in one chat.</summary>
    public const int Max = 10;

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
                // Answering: this one goes when that answer ends.
                job = jobs.Reserve(c.Id, c.UserId);
                if (job is null)
                {
                    return false;
                }
                // Its files, those still there.
                var ids = ChatService.ParseIds(next.AttachmentsJson).ToList();
                var kept = await db.ChatAttachments.Where(a => ids.Contains(a.Id) && a.UserId == c.UserId).Select(a => a.Id).ToListAsync();
                var files = ids.Where(kept.Contains).ToList();
                var last = await db.ChatMessages.Where(m => m.ConversationId == c.Id).MaxAsync(m => (int?)m.Sequence) ?? 0;
                var question = new ChatMessage
                {
                    ConversationId = c.Id, ParentId = c.CurrentLeafId, Role = "user", Sequence = last + 1, Content = next.Content,
                    AttachmentsJson = files.Count > 0 ? JsonSerializer.Serialize(files) : null,
                };
                db.ChatMessages.Add(question);
                db.QueuedMessages.Remove(next);
                c.CurrentLeafId = question.Id;
                c.ArchivedAt = null;
                c.UpdatedAt = DateTimeOffset.UtcNow;
                var titled = last == 0;
                if (titled)
                {
                    c.Title = ChatService.TitleFrom(next.Content.Length > 0 ? next.Content : "Attached files");
                }
                await db.SaveChangesAsync();
                job.Emit(new { type = "question", id = question.Id, parentId = question.ParentId });
                if (titled)
                {
                    job.Emit(new { type = "title", title = c.Title });
                }
                jobs.Start(job, question.Id, new AnswerOverrides(Research: next.Research));
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
            q.Id, q.Content, q.Research, q.CreatedAt,
            attachments = ChatService.ParseIds(q.AttachmentsJson).Where(files.ContainsKey).Select(a => files[a]),
        })];
    }

    public static void MapQueue(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat/conversations/{id:guid}/queue").RequireAuthorization();
        g.MapPost("", AddAsync);
        g.MapDelete("/{queuedId:guid}", CancelAsync);
        g.MapPost("/{queuedId:guid}/now", NowAsync);
    }

    private static async Task<(AppUser Me, Conversation? Chat)> OwnedAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        return (me, await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, ct));
    }

    /// <summary>
    /// Queues a message, checked as a sent one is (its files, the safeguards). The chat's line, and
    /// whether it is answering: one that ended meanwhile takes the message at once.
    /// </summary>
    private static async Task<IResult> AddAsync(Guid id, QueueRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        Safeguards.Safeguards safeguards, ModelPolicy policy, ChatModels models, QueuedMessages queue, AnswerJobs jobs, CancellationToken ct)
    {
        var (me, c) = await OwnedAsync(id, p, users, db, ct);
        if (c is null)
        {
            return Results.NotFound();
        }
        var text = (body.Content ?? "").Trim();
        var attachments = (body.Attachments ?? []).Distinct().ToArray();
        if (text.Length == 0 && attachments.Length == 0)
        {
            return AuthEndpoints.Problem(400, "empty", "Write a message first.");
        }
        if (attachments.Length > 0 && await db.ChatAttachments.CountAsync(a => attachments.Contains(a.Id) && a.UserId == me.Id, ct) != attachments.Length)
        {
            return AuthEndpoints.Problem(400, "attachment", "An attachment is missing or is not yours.");
        }
        if (await db.QueuedMessages.CountAsync(q => q.ConversationId == id, ct) >= Max)
        {
            return AuthEndpoints.Problem(409, "queue_full", $"{Max} messages wait in this chat already: send one now, or cancel one.");
        }
        var model = c.Model ?? (await policy.ForAsync(me, await models.ListAsync(ct), ct)).Default?.Name;
        var verdict = await safeguards.CheckMessageAsync(me, text, attachments.Length, body.Research, model, ct);
        if (!verdict.Allowed)
        {
            return AuthEndpoints.Problem(verdict.Status, $"safeguard_{verdict.Kind}", verdict.Reason!);
        }
        if (body.Research)
        {
            await safeguards.MarkResearchAsync(me.Id, ct);
        }
        var last = await db.QueuedMessages.Where(q => q.ConversationId == id).MaxAsync(q => (int?)q.Position, ct) ?? 0;
        db.QueuedMessages.Add(new QueuedMessage
        {
            ConversationId = id, Content = text, Research = body.Research, Position = last + 1,
            AttachmentsJson = attachments.Length > 0 ? JsonSerializer.Serialize(attachments) : null,
        });
        // Writing in an archived chat brings it back to the list.
        c.ArchivedAt = null;
        await db.SaveChangesAsync(ct);
        await queue.StartNextAsync(id);
        return Results.Ok(new { queued = await ListAsync(db, id, ct), answering = jobs.IsAnswering(id) });
    }

    /// <summary>Takes a message out of line; 404 when it is not waiting any more (it went).</summary>
    private static async Task<IResult> CancelAsync(Guid id, Guid queuedId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        if ((await OwnedAsync(id, p, users, db, ct)).Chat is null)
        {
            return Results.NotFound();
        }
        return await db.QueuedMessages.Where(q => q.Id == queuedId && q.ConversationId == id).ExecuteDeleteAsync(ct) == 0
            ? AuthEndpoints.Problem(404, "not_queued", "That message is not waiting any more: it was sent.")
            : Results.NoContent();
    }

    /// <summary>Send now: the message goes first in line, and the answer running stops (keeping what it has) so it goes at once.</summary>
    private static async Task<IResult> NowAsync(Guid id, Guid queuedId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        QueuedMessages queue, AnswerJobs jobs, CancellationToken ct)
    {
        if ((await OwnedAsync(id, p, users, db, ct)).Chat is null)
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
