using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Notifications;

/// <summary>What the bell says: one line of news for a person, with where it leads.</summary>
/// <param name="Kind">answer, task, usage, alert or download (the bell's icon).</param>
/// <param name="Key">The news it is, once per person: given, the same news is not said twice.</param>
public sealed record News(string Kind, string Title, string? Body = null, string? Link = null, string? Key = null);

/// <summary>Puts news in people's bells (the page checks it often, and says it on the desktop when allowed).</summary>
public sealed class Notifier(AppDbContext db, UserManager<AppUser> users, TimeProvider clock)
{
    /// <returns>False when this person had this news already (same key).</returns>
    public async Task<bool> SendAsync(Guid userId, News news, CancellationToken ct = default)
    {
        if (news.Key is { } key && await db.Notifications.AnyAsync(n => n.UserId == userId && n.Key == key, ct))
        {
            return false;
        }
        var n = new Notification
        {
            UserId = userId, Kind = news.Kind, Title = Cut(news.Title, 300), Body = news.Body is null ? null : Cut(news.Body, 2000),
            Link = news.Link, Key = news.Key, CreatedAt = clock.GetUtcNow(),
        };
        db.Notifications.Add(n);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException) when (news.Key is not null)
        {
            // Said at the same moment by another instance: once is enough.
            db.Entry(n).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>The same news to every admin who can sign in.</summary>
    public async Task ToAdminsAsync(News news, CancellationToken ct = default)
    {
        foreach (var admin in (await users.GetUsersInRoleAsync(Roles.Admin)).Where(a => !a.IsDisabled))
        {
            await SendAsync(admin.Id, news, ct);
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
