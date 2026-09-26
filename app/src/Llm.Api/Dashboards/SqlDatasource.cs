using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Dashboards;

/// <summary>
/// Runs a panel's SQL against the gateway's database, as safely as SQL from a
/// file can be run: one statement only, a read-only transaction, a role that
/// can only SELECT, a statement timeout and a row cap.
/// </summary>
public sealed partial class SqlDatasource(IOptions<DashboardOptions> options, IConfiguration config, ILogger<SqlDatasource> logger)
{
    public const string Uid = "litellm-db";
    public const string ReadOnlyRole = "llm_dashboards";

    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(config))
    {
        Database = options.Value.SqlDatabase,
        ApplicationName = "llm-app dashboards",
    }.ConnectionString;

    private int _rolePrepared;

    public Task<TableResult> QueryAsync(string sql, CancellationToken ct) => QueryAsync(sql, null, ct);

    public async Task<TableResult> QueryAsync(string sql, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        sql = sql.Trim().TrimEnd(';');
        if (HasSecondStatement(sql))
        {
            throw new FormatException("A panel query must be a single statement.");
        }
        await EnsureRoleAsync(ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var timeoutMs = (int)options.Value.StatementTimeout.TotalMilliseconds;
        await using (var setup = new NpgsqlCommand(
            $"SET TRANSACTION READ ONLY; SET LOCAL ROLE {ReadOnlyRole}; SET LOCAL statement_timeout = {timeoutMs}", conn, tx))
        {
            await setup.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters ?? new Dictionary<string, object>())
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new Column(reader.GetName(i), TypeOf(reader.GetFieldType(i)))).ToList();
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
                row[i] = reader.IsDBNull(i) ? null : Normalise(reader.GetValue(i));
            }
            rows.Add(row);
        }
        return new TableResult(columns, rows, capped);
    }

    /// <summary>
    /// A role that can only read, created once per start in the gateway's
    /// database. NOLOGIN: it is entered with SET ROLE inside each query's
    /// transaction, so it needs no password. Default privileges cover tables
    /// LiteLLM creates in later versions.
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
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ReadOnlyRole}') THEN
                CREATE ROLE {ReadOnlyRole} NOLOGIN;
              END IF;
            END $$;
            GRANT {ReadOnlyRole} TO CURRENT_USER;
            GRANT USAGE ON SCHEMA public TO {ReadOnlyRole};
            GRANT SELECT ON ALL TABLES IN SCHEMA public TO {ReadOnlyRole};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO {ReadOnlyRole};
            """, conn);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            Volatile.Write(ref _rolePrepared, 1);
        }
        catch (PostgresException ex) when (ex.SqlState is "23505" or "XX000" or "42710")
        {
            // Two first queries at once both creating the role: the other one won.
            LogRoleRace(logger, ex.MessageText);
        }
    }

    /// <summary>True when a ';' outside quotes and comments is followed by more SQL.</summary>
    public static bool HasSecondStatement(string sql)
    {
        var quote = '\0';
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i);
                i = nl < 0 ? sql.Length : nl;
            }
            else if (c == ';' && sql[(i + 1)..].Trim().Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string TypeOf(Type t) =>
        t == typeof(string) ? "string"
        : t == typeof(bool) ? "boolean"
        : t == typeof(DateTime) || t == typeof(DateTimeOffset) ? "time"
        : t.IsPrimitive || t == typeof(decimal) ? "number"
        : "string";

    private static object? Normalise(object v) => v switch
    {
        decimal m => (double)m,
        long or int or short or float or double => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        string or bool or DateTimeOffset => v,
        _ => v.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Read-only role setup raced another query: {Reason}")]
    private static partial void LogRoleRace(ILogger logger, string reason);
}
