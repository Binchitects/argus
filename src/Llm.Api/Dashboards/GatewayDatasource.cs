using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Dashboards;

/// <summary>
/// The dashboards' SQL on the app's own ledger of model requests (the `gateway-db` datasource): the app's database, read
/// only, as the read-only role, which may read the schema gateway and nothing else of the app's (its tables and its two
/// views), and with the schema as the search path, so a panel names `requests`.
/// </summary>
public sealed partial class GatewayDatasource(IOptions<DashboardOptions> options, IConfiguration config, ILogger<GatewayDatasource> logger)
{
    public const string Uid = "gateway-db";

    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(config))
    {
        ApplicationName = "llm-app dashboards",
    }.ConnectionString;

    private int _rolePrepared;

    public async Task<TableResult> QueryAsync(string sql, CancellationToken ct)
    {
        sql = sql.Trim().TrimEnd(';');
        if (SqlDatasource.HasSecondStatement(sql))
        {
            throw new FormatException("A panel query must be a single statement.");
        }
        await EnsureRoleAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var setup = new NpgsqlCommand(
            $"SET TRANSACTION READ ONLY; SET LOCAL ROLE {SqlDatasource.ReadOnlyRole}; SET LOCAL search_path = gateway; SET LOCAL statement_timeout = {(int)options.Value.StatementTimeout.TotalMilliseconds}", conn, tx))
        {
            await setup.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new Column(reader.GetName(i), SqlDatasource.TypeOf(reader.GetFieldType(i)))).ToList();
        var rows = new List<object?[]>();
        var capped = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == options.Value.MaxRows)
            {
                capped = true;
                break;
            }
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : SqlDatasource.Normalise(reader.GetValue(i));
            }
            rows.Add(row);
        }
        return new TableResult(columns, rows, capped);
    }

    /// <summary>
    /// The read-only role (roles are the server's, so it is the one LiteLLM's datasource uses) given the schema gateway of
    /// the app's database and nothing else of it: public's tables (people, chats, keys) stay out of panel SQL.
    /// </summary>
    private async Task EnsureRoleAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _rolePrepared) == 1)
        {
            return;
        }
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{SqlDatasource.ReadOnlyRole}') THEN
                CREATE ROLE {SqlDatasource.ReadOnlyRole} NOLOGIN;
              END IF;
            END $$;
            GRANT {SqlDatasource.ReadOnlyRole} TO CURRENT_USER;
            REVOKE ALL ON SCHEMA public FROM {SqlDatasource.ReadOnlyRole};
            REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {SqlDatasource.ReadOnlyRole};
            GRANT USAGE ON SCHEMA gateway TO {SqlDatasource.ReadOnlyRole};
            GRANT SELECT ON ALL TABLES IN SCHEMA gateway TO {SqlDatasource.ReadOnlyRole};
            ALTER DEFAULT PRIVILEGES IN SCHEMA gateway GRANT SELECT ON TABLES TO {SqlDatasource.ReadOnlyRole};
            """, conn);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
                Volatile.Write(ref _rolePrepared, 1);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState is "23505" or "XX000" or "42710" && attempt < 3)
            {
                // Another first query, here or for the other datasource (the role is the server's), set it up at the same
                // moment: this script's grants were rolled back with it, so it runs again (the role is there now).
                LogRoleRace(logger, ex.MessageText);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The ledger's read-only role setup raced another query: {Reason}")]
    private static partial void LogRoleRace(ILogger logger, string reason);
}
