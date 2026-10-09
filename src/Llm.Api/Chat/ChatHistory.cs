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
    /// chat on screen has no older message left. Text only: never a file, nor anyone else's.
    /// </summary>
    private static async Task<IResult> HistoryAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DateTimeOffset? before = null, int limit = 50,
        CancellationToken ct = default)
    {
        var me = await Me(p, users);
        limit = Math.Clamp(limit, 1, 200);
        // The database takes times in UTC.
        before = before?.ToUniversalTime();
        var sent = await db.ChatMessages.AsNoTracking()
            .Join(db.Conversations.Where(c => c.UserId == me.Id), m => m.ConversationId, c => c.Id, (m, _) => m)
            .Where(m => m.Role == "user" && m.Content != "" && m.Content.Length <= HistoryMaxChars && (before == null || m.CreatedAt < before))
            .OrderByDescending(m => m.CreatedAt).Take(limit)
            .Select(m => new SentMessage(m.Content, m.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(sent);
    }
}
