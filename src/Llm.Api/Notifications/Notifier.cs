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
public sealed class Notifier(AppDbContext db, Access.AccessService access, WebPush push, TimeProvider clock)
{
    /// <returns>False when this person had this news already (same key).</returns>
    public async Task<bool> SendAsync(Guid userId, News news, CancellationToken ct = default)
    {
        var title = Cut(NewsText.Plain(news.Title, 300) ?? news.Kind, 300);
        var body = NewsText.Plain(news.Body);
        if (news.Key is { } key)
        {
            // Said at the same moment by another request or instance: the database keeps one, with no error to log.
            return await InsertKeyedAsync(userId, news, key, title, body, clock.GetUtcNow(), ct);
        }
        db.Notifications.Add(new Notification { UserId = userId, Kind = news.Kind, Title = title, Body = body, Link = news.Link, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The same news to many people at once (a keyed one once each).</summary>
    public async Task SendManyAsync(IReadOnlyCollection<Guid> userIds, News news, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var title = Cut(NewsText.Plain(news.Title, 300) ?? news.Kind, 300);
        var body = NewsText.Plain(news.Body);
        if (news.Key is { } key)
        {
            foreach (var u in userIds.Distinct())
            {
                await InsertKeyedAsync(u, news, key, title, body, now, ct);
            }
            return;
        }
        db.Notifications.AddRange(userIds.Select(u => new Notification { UserId = u, Kind = news.Kind, Title = title, Body = body, Link = news.Link, CreatedAt = now }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A keyed news written once per person: true when this call wrote it.</summary>
    /// <remarks>Written past the change tracker, so it is pushed to the person's devices here (as <see cref="PushOnSave"/> does for the rest).</remarks>
    private async Task<bool> InsertKeyedAsync(Guid userId, News news, string key, string title, string? body, DateTimeOffset at, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var wrote = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO notifications ("Id", "UserId", "Kind", "Title", "Body", "Link", "Key", "Cleared", "CreatedAt")
            VALUES ({id}, {userId}, {news.Kind}, {title}, {body}, {news.Link}, {key}, FALSE, {at})
            ON CONFLICT ("UserId", "Key") WHERE "Key" IS NOT NULL DO NOTHING
            """, ct) == 1;
        if (wrote)
        {
            push.Queue(userId, new PushMessage(title, body, news.Link, news.Kind, id.ToString()));
        }
        return wrote;
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
