using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Notifications;

/// <summary>What the bell says: one line of news for a person, with where it leads.</summary>
/// <param name="Kind">answer, task, usage, alert, download, workflow, fix or review (the bell's icon).</param>
/// <param name="Key">The news it is, once per person: given, the same news is not said twice.</param>
public sealed record News(string Kind, string Title, string? Body = null, string? Link = null, string? Key = null);

/// <summary>Puts news in people's bells (the page checks it often, and says it on the desktop when allowed).</summary>
public sealed class Notifier(AppDbContext db, Access.AccessService access, TimeProvider clock)
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
            UserId = userId, Kind = news.Kind, Title = Cut(NewsText.Plain(news.Title, 300) ?? news.Kind, 300), Body = NewsText.Plain(news.Body),
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

    /// <summary>The same news to many people at once (a keyed one once each), in one write.</summary>
    public async Task SendManyAsync(IReadOnlyCollection<Guid> userIds, News news, CancellationToken ct = default)
    {
        var had = news.Key is { } key ? (await db.Notifications.Where(n => n.Key == key && userIds.Contains(n.UserId)).Select(n => n.UserId).ToListAsync(ct)).ToHashSet() : [];
        var now = clock.GetUtcNow();
        var title = Cut(NewsText.Plain(news.Title, 300) ?? news.Kind, 300);
        var body = NewsText.Plain(news.Body);
        db.Notifications.AddRange(userIds.Where(u => !had.Contains(u)).Select(u => new Notification
        {
            UserId = u, Kind = news.Kind, Title = title, Body = body, Link = news.Link, Key = news.Key, CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The same news to every admin who can sign in.</summary>
    public async Task ToAdminsAsync(News news, CancellationToken ct = default)
    {
        foreach (var admin in await access.OwnersAsync(ct: ct))
        {
            await SendAsync(admin.Id, news, ct);
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
