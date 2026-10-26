using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Meetings;

/// <summary>
/// A meeting's action items, reminded on the day each is due (or after, when missed): the person it falls to is told once,
/// in the bell (and on their devices). The replica that leads looks every few minutes.
/// </summary>
public sealed partial class MeetingReminders(IServiceScopeFactory scopes, Operations.Replicas replicas, TimeProvider clock, ILogger<MeetingReminders> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>The most reminded in one look (the rest, at the next).</summary>
    public const int MostAtOnce = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            if (!replicas.IsLeader)
            {
                continue;
            }
            try
            {
                await RemindAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Each action item due by today, not done, not reminded, whose person is known: reminded now. Returns how many.</summary>
    public async Task<int> RemindAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<Notifications.Notifier>();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var due = await db.MeetingActions.Where(a => !a.Done && a.RemindedAt == null && a.PersonId != null && a.Due != null && a.Due <= today)
            .Join(db.Meetings, a => a.MeetingId, m => m.Id, (a, m) => new { Action = a, m.Title, m.UserId, MeetingId = m.Id })
            .OrderBy(x => x.Action.Due).Take(MostAtOnce).ToListAsync(ct);
        foreach (var x in due)
        {
            await notifier.SendAsync(x.Action.PersonId!.Value, new Notifications.News(MeetingEngine.Tag,
                x.Action.Due == today ? $"Due today: {x.Action.Text}" : $"Overdue since {x.Action.Due:yyyy-MM-dd}: {x.Action.Text}",
                $"An action item from {x.Title}.", x.Action.PersonId == x.UserId ? $"/meetings/{x.MeetingId}" : null, $"meeting-action:{x.Action.Id}"), ct);
            x.Action.RemindedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meeting reminders failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
