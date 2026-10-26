using Llm.Core.Data;
using Llm.Core.Insights;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Insights;

/// <summary>
/// Runs the boards' charts again on their schedules (hourly, daily), as each board's owner: a few at a time, the longest
/// waiting first, so one slow database does not hold up the rest for long.
/// </summary>
public sealed partial class BoardRefresher(IServiceScopeFactory scopes, TimeProvider clock, ILogger<BoardRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private const int AtOnce = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Failed(logger, ex);
            }
        }
    }

    /// <summary>The charts whose time has come, run now. Returns how many.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var questions = scope.ServiceProvider.GetRequiredService<DataQuestions>();
        var now = clock.GetUtcNow();
        var hour = now.AddHours(-1);
        var day = now.AddDays(-1);
        var due = await db.DataPins
            .Where(x => (x.Refresh == DataPin.Hourly && (x.RanAt == null || x.RanAt <= hour)) || (x.Refresh == DataPin.Daily && (x.RanAt == null || x.RanAt <= day)))
            .OrderBy(x => x.RanAt).Take(AtOnce).ToListAsync(ct);
        foreach (var pin in due)
        {
            var owner = await db.DataBoards.Where(b => b.Id == pin.BoardId).Join(db.Users, b => b.OwnerId, u => u.Id, (b, u) => u).SingleOrDefaultAsync(ct);
            if (owner is null)
            {
                continue;
            }
            await DataEndpoints.RunPinAsync(pin, owner, db, questions, ct);
        }
        return due.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing the boards' charts failed")]
    private static partial void Failed(ILogger logger, Exception ex);
}
