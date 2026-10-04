using System.Text.Json;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Arena: a question for two models, side by side and blind. Models: the two
/// to compare; none for two at random of those the person may use.
/// </summary>
public sealed record CompareRequest(string Content, Guid[]? Attachments = null, Guid? ParentId = null, bool Root = false, string[]? Models = null);

public static partial class ChatEndpoints
{
    /// <summary>
    /// Arena mode: the question goes to two models, answered one after the other and shown
    /// as "Model A" and "Model B"; which is which is drawn, even when the person chose the
    /// two. Streams as an answer does; the person votes, then sees the names.
    /// </summary>
    internal static async Task CompareAsync(Guid id, CompareRequest body, HttpContext http, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs,
        Safeguards.Safeguards safeguards, ModelPolicy policy, ChatModels models)
    {
        var ct = http.RequestAborted;
        var me = await Me(http.User, users);
        if (await Owned(db, id, me) is not { } c)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        c.ArchivedAt = null;
        var text = (body.Content ?? "").Trim();
        var attachments = (body.Attachments ?? []).Distinct().ToArray();
        if (text.Length == 0 && attachments.Length == 0)
        {
            await Problem(http, 400, "empty", "Write a message first.");
            return;
        }
        if (attachments.Length > 0 && await db.ChatAttachments.CountAsync(a => attachments.Contains(a.Id) && a.UserId == me.Id, ct) != attachments.Length)
        {
            await Problem(http, 400, "attachment", "An attachment is missing or is not yours.");
            return;
        }
        Guid? parent = body.Root ? null : body.ParentId ?? c.CurrentLeafId;
        if (parent is { } p && !await db.ChatMessages.AnyAsync(m => m.ConversationId == c.Id && m.Id == p, ct))
        {
            await Problem(http, 400, "parent", "That message is not in this chat.");
            return;
        }
        var served = await models.ListAsync(ct);
        var (mine, _, onEngine) = await policy.ForAsync(me, served, ct);
        string[] pair;
        if (body.Models is { Length: > 0 } chosen)
        {
            if (chosen.Length != 2 || chosen[0] == chosen[1])
            {
                await Problem(http, 400, "models", "Choose two different models, or none for two at random.");
                return;
            }
            if (chosen.FirstOrDefault(m => served.All(s => s.Name != m)) is { } unknown)
            {
                await Problem(http, 400, "models", $"The gateway does not serve {unknown}.");
                return;
            }
            if (chosen.FirstOrDefault(m => mine.All(s => s.Name != m)) is { } refused)
            {
                await Problem(http, 403, "models", $"You may not use {refused}.");
                return;
            }
            pair = [.. chosen];
        }
        else
        {
            // Two that can answer now (loaded, or loaded when asked).
            var ready = mine.Where(m => policy.Ready(m.Name, onEngine)).Select(m => m.Name).ToList();
            if (ready.Count < 2)
            {
                await Problem(http, 400, "models", ready.Count == 0
                    ? "Comparing needs two models you may use that can answer now, and none can."
                    : $"Comparing needs two models you may use that can answer now, and only {ready[0]} can.");
                return;
            }
            pair = [.. ready.OrderBy(_ => Random.Shared.Next()).Take(2)];
        }
        if (Random.Shared.Next(2) == 1)
        {
            pair = [pair[1], pair[0]];
        }
        var verdict = await safeguards.CheckMessageAsync(me, text, attachments.Length, false, pair[0], ct);
        if (!verdict.Allowed)
        {
            await Problem(http, verdict.Status, $"safeguard_{verdict.Kind}", verdict.Reason!);
            return;
        }
        var match = new ArenaMatch { UserId = me.Id, ConversationId = c.Id, ModelA = pair[0], ModelB = pair[1] };
        await RunAsync(http, c, me, db, jobs, new AnswerOverrides(), async () =>
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
            match.QuestionId = question.Id;
            db.ArenaMatches.Add(match);
            await db.SaveChangesAsync();
            return (question, first);
        }, (job, question) => jobs.StartArena(job, question.Id, match.Id));
    }
}
