using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// One replica at a time through the start (the database made, migrated, the first admin, the
    /// OIDC keys): replicas starting together on a new database would race for each. An advisory lock
    /// in the server's maintenance database ("postgres"), as the app's own may not exist yet; it is
    /// let go when the returned handle is disposed. Without that database (a managed server that
    /// keeps it from the app), the start goes on unlocked and says so.
    /// </summary>
    public static async Task<IAsyncDisposable> OneAtATimeAsync(string connectionString, ILogger logger, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("arena-start:" + builder.Database)), 0);
        builder.Database = "postgres";
        builder.Pooling = false;
        for (var attempt = 1; ; attempt++)
        {
            var conn = new NpgsqlConnection(builder.ConnectionString);
            try
            {
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", conn) { CommandTimeout = 0 };
                cmd.Parameters.AddWithValue("key", key);
                await cmd.ExecuteNonQueryAsync(ct);
                return conn;
            }
            catch (PostgresException ex)
            {
                await conn.DisposeAsync();
                LogUnlocked(logger, ex.MessageText);
                return conn;
            }
            catch (NpgsqlException ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                await conn.DisposeAsync();
                LogWaiting(logger, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database is ready")]
    private static partial void LogReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not reachable yet (attempt {Attempt}): {Reason}")]
    private static partial void LogWaiting(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting without the start lock (the maintenance database said: {Reason}); start one replica at a time")]
    private static partial void LogUnlocked(ILogger logger, string reason);
}
