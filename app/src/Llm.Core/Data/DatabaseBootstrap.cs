using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Llm.Core.Data;

public static partial class DatabaseBootstrap
{
    /// <summary>
    /// Creates the app's database if it is missing, then applies migrations.
    /// The shared Postgres only runs its init scripts on an empty volume, so an
    /// existing install would never get a new database from there.
    /// </summary>
    public static async Task EnsureReadyAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var cs = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        var name = cs.Database ?? throw new InvalidOperationException("The connection string names no database.");
        cs.Database = "postgres";
        await using (var conn = new NpgsqlConnection(cs.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @n", conn);
            exists.Parameters.AddWithValue("n", name);
            if (await exists.ExecuteScalarAsync(ct) is null)
            {
                LogCreatingDatabase(logger, name);
                // Identifiers cannot be parameters; the name is quoted and comes from our own config.
                await using var create = new NpgsqlCommand($"create database \"{name.Replace("\"", "\"\"")}\"", conn);
                await create.ExecuteNonQueryAsync(ct);
            }
        }
        await db.Database.MigrateAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Creating database {Database}")]
    private static partial void LogCreatingDatabase(ILogger logger, string database);
}
