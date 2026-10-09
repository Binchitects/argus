using System.Security.Claims;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A message the person sent, for the message box's ↑: its text and when.</summary>
public sealed record SentMessage(string Text, DateTimeOffset At);

public static partial class ChatEndpoints
{
    /// <summary>Messages longer than this are left out of ↑'s history (a pasted document): they stay in their chat.</summary>
    public const int HistoryMaxChars = 32_000;

    /// <summary>
    /// The person's own messages across their chats (archived ones too), newest first, a page at a time
    /// (before: the time of the last one of the page before). The message box's ↑ goes on to these when the
    /// chat on screen has no older message left. Text only: never a file, nor anyone else's. A scheduled task's
    /// runs are left out (their questions carry an event's text, written by others), and so are the messages a
    /// fork copied: someone else's from their shared chat, or one's own, still in the chat they came from.
    /// </summary>
    private static async Task<IResult> HistoryAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DateTimeOffset? before = null, int limit = 50,
        CancellationToken ct = default)
    {
        var me = await Me(p, users);
        limit = Math.Clamp(limit, 1, 200);
        // The database takes times in UTC.
        before = before?.ToUniversalTime();
        var sent = await db.ChatMessages.AsNoTracking()
            .Join(db.Conversations.Where(c => c.UserId == me.Id && c.ScheduledTaskId == null), m => m.ConversationId, c => c.Id, (m, c) => new { m, Since = c.CreatedAt })
            // A copy keeps its original's time, from before the fork was made.
            .Where(x => x.m.CreatedAt >= x.Since)
            .Select(x => x.m)
            .Where(m => m.Role == "user" && m.Content != "" && m.Content.Length <= HistoryMaxChars && (before == null || m.CreatedAt < before))
            .OrderByDescending(m => m.CreatedAt).Take(limit)
            .Select(m => new SentMessage(m.Content, m.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(sent);
    }
}
