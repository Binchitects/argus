using Llm.Core.Data;
using Npgsql;

namespace Llm.Api;

public static partial class StartupDatabase
{
    public static async Task MigrateAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        // Postgres can still be starting when the app does (a restart of the
        // database alone, or a first boot initialising its volume).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DatabaseBootstrap.EnsureReadyAsync(db, logger, ct);
                LogReady(logger);
                return;
            }
            catch (NpgsqlException ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                LogWaiting(logger, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database is ready")]
    private static partial void LogReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not reachable yet (attempt {Attempt}): {Reason}")]
    private static partial void LogWaiting(ILogger logger, int attempt, string reason);
}
