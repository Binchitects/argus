using System.Data;
using System.Diagnostics;
using Llm.Core.Insights;
using MySqlConnector;

namespace Llm.Api.Insights;

/// <summary>
/// A MySQL or MariaDB database people ask. Their plans do not say which database a table is in, so the account is the
/// guard: it may only read (SELECT and SHOW VIEW, granted to it directly: roles are set aside), never the system's schemas,
/// and only the tables open to questions, which is checked before every query. Each query runs alone, in a read-only
/// session and transaction, with a time limit.
/// </summary>
public sealed class MySqlData : IDataEngine
{
    private static readonly string[] SystemSchemas = ["mysql", "information_schema", "performance_schema", "sys"];
    private static readonly HashSet<string> Reading = new(["SELECT", "SHOW VIEW", "USAGE"], StringComparer.OrdinalIgnoreCase);

    public string Kind => DataSource.MySql;
    public string Title => "MySQL or MariaDB";
    public string Names => "with their database (shop.orders), and the columns' short names in backticks (`Orders`); LIMIT for the rows";
    public string Month => "date_format(placed_at, '%Y-%m-01') as `Month`";
    public bool GrantsOnly => true;

    public string? CheckConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
        {
            return "Give the connection: Server=…;Port=3306;Database=…;User ID=…;Password=…";
        }
        try
        {
            var b = new MySqlConnectionStringBuilder(connection);
            if (string.IsNullOrEmpty(b.Server) || string.IsNullOrEmpty(b.Database) || string.IsNullOrEmpty(b.UserID))
            {
                return "The connection needs a Server, a Database and a User ID.";
            }
        }
        catch (ArgumentException ex)
        {
            return $"That is not a connection string MySQL takes: {ex.Message}";
        }
        return null;
    }

    public string Where(string connection)
    {
        try
        {
            var b = new MySqlConnectionStringBuilder(connection);
            return $"{b.UserID}@{b.Server}{(b.Port is 3306 ? "" : $":{b.Port}")}/{b.Database}";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>Its own connection each time (no pool), no local files, named in the server's process list.</summary>
    private static string Prepared(string connection, int seconds) => new MySqlConnectionStringBuilder(connection)
    {
        Pooling = false, ConnectionTimeout = 10, DefaultCommandTimeout = (uint)(seconds + 5), AllowLoadLocalInfile = false,
        AllowUserVariables = false, ApplicationName = "Argus Arena (Ask your data)",
    }.ConnectionString;

    /// <summary>
    /// The session: roles set aside (the account's own grants are what is checked), a SQL mode whose strings this lexes as
    /// the server does (backslashes escape), read only, and time limits.
    /// </summary>
    private static async Task<MySqlConnection> OpenAsync(string connection, int seconds, CancellationToken ct)
    {
        var conn = new MySqlConnection(Prepared(connection, seconds));
        try
        {
            await conn.OpenAsync(ct);
            var mariaDb = conn.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
            foreach (var set in new[]
            {
                "set role none",
                "set session sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION'",
                "set session transaction read only",
                "set session lock_wait_timeout = 2",
                "set session innodb_lock_wait_timeout = 2",
                mariaDb ? $"set session max_statement_time = {seconds}" : $"set session max_execution_time = {seconds * 1000}",
            })
            {
                await using var cmd = new MySqlCommand(set, conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex) when (ex is MySqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            await conn.DisposeAsync();
            throw new DataException($"The database could not be reached: {ex.Message}");
        }
        return conn;
    }

    /// <summary>
    /// Refuses an account that may do more than read, anywhere (FILE and SUPER among them), or read the system's schemas
    /// (password hashes, other sessions' queries).
    /// </summary>
    private static async Task CheckAccountAsync(MySqlConnection conn, CancellationToken ct)
    {
        var more = new SortedSet<string>(StringComparer.Ordinal);
        await using (var cmd = new MySqlCommand("""
            select privilege_type, '*.*', grantee from information_schema.user_privileges
            union all select privilege_type, concat(table_schema, '.*'), grantee from information_schema.schema_privileges
            union all select privilege_type, concat(table_schema, '.', table_name), grantee from information_schema.table_privileges
            union all select privilege_type, concat(table_schema, '.', table_name, '.', column_name), grantee from information_schema.column_privileges
            """, conn))
        {
            await using var me = new MySqlCommand("select concat('''', substring_index(current_user(), '@', 1), '''@''', substring_index(current_user(), '@', -1), '''')", conn);
            var grantee = (string)(await me.ExecuteScalarAsync(ct))!;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (privilege, on) = (r.GetString(0), r.GetString(1));
                if (r.GetString(2) != grantee)
                {
                    continue;
                }
                var system = SystemSchemas.Any(s => on.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase));
                if (!Reading.Contains(privilege) || system || (on == "*.*" && privilege != "USAGE"))
                {
                    more.Add($"{privilege} on {on}");
                }
            }
        }
        if (more.Count > 0)
        {
            throw new DataException($"The account may do more than read the tables people ask ({string.Join(", ", more.Take(5))}{(more.Count > 5 ? ", …" : "")}). Give it an account that has only SELECT on those tables, granted to it directly.");
        }
    }

    private static async Task<Dictionary<string, DataTable>> TablesAsync(MySqlConnection conn, CancellationToken ct)
    {
        var tables = new Dictionary<string, DataTable>(StringComparer.Ordinal);
        await using var cmd = new MySqlCommand("""
            select t.table_schema, t.table_name, t.table_type, t.table_comment, c.column_name, c.column_type, c.column_comment
            from information_schema.tables t
            join information_schema.columns c on c.table_schema = t.table_schema and c.table_name = t.table_name
            where t.table_schema not in ('mysql', 'information_schema', 'performance_schema', 'sys')
            order by t.table_schema, t.table_name, c.ordinal_position
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = $"{r.GetString(0)}.{r.GetString(1)}";
            if (!tables.TryGetValue(name, out var t))
            {
                var view = r.GetString(2) == "VIEW";
                var comment = r.IsDBNull(3) ? null : r.GetString(3);
                tables[name] = t = new DataTable(name, view ? "view" : "table", comment is { Length: > 0 } && !(view && comment == "VIEW") ? comment : null, []);
            }
            t.Columns.Add(new DataColumn(r.GetString(4), r.GetString(5), r.IsDBNull(6) || r.GetString(6).Length == 0 ? null : r.GetString(6)));
        }
        return tables;
    }

    public async Task<(List<DataTable> Tables, List<string> Warnings)> ReadAsync(string connection, CancellationToken ct)
    {
        await using var conn = await OpenAsync(connection, 30, ct);
        await CheckAccountAsync(conn, ct);
        var tables = await TablesAsync(conn, ct);
        var warnings = new List<string>();
        if (tables.Count > DataEngines.MostTables)
        {
            warnings.Add($"The account may read {tables.Count} tables: give it fewer, at most {DataEngines.MostTables}.");
        }
        return ([.. tables.Values], warnings);
    }

    public async Task<DataResult> RunAsync(string connection, string sql, IReadOnlyCollection<string> allowed, int mostRows, int seconds, CancellationToken ct)
    {
        var (query, problem) = SqlCheck.One(sql, SqlDialect.MySql);
        if (problem is not null)
        {
            throw new DataException(problem);
        }
        var started = Stopwatch.GetTimestamp();
        await using var conn = await OpenAsync(connection, seconds, ct);
        try
        {
            await CheckAccountAsync(conn, ct);
            var beyond = (await TablesAsync(conn, ct)).Keys.Where(t => !allowed.Contains(t)).Order(StringComparer.Ordinal).ToList();
            if (beyond.Count > 0)
            {
                throw new DataException($"The account may read tables that are not open to questions ({string.Join(", ", beyond.Take(5))}{(beyond.Count > 5 ? ", …" : "")}). MySQL does not show which tables a query reads, so it must read only those open: open them, or take their SELECT away.");
            }
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, ct);
            await using var cmd = new MySqlCommand(query, conn, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await DataEngines.RowsAsync(r, KindOf, (reader, i) => DataEngines.Json(reader.GetValue(i)), mostRows, started, ct);
            // Never committed: the transaction rolls back as it is disposed.
        }
        catch (MySqlException ex) when (ex.Number is 3024 or 1969 || ex.ErrorCode == MySqlErrorCode.CommandTimeoutExpired)
        {
            throw new DataException($"The query ran longer than {seconds} seconds, the most here. Ask for less, or a narrower time.");
        }
        catch (MySqlException ex)
        {
            throw new DataException(ex.Message);
        }
    }

    /// <summary>A column's kind for the page: number, date, time, bool or text.</summary>
    private static string KindOf(string type) => type.ToUpperInvariant().Replace(" UNSIGNED", "", StringComparison.Ordinal) switch
    {
        "TINYINT" or "SMALLINT" or "MEDIUMINT" or "INT" or "INTEGER" or "BIGINT" or "DECIMAL" or "NEWDECIMAL" or "DOUBLE" or "FLOAT" or "YEAR" => "number",
        "DATE" => "date",
        "DATETIME" or "TIMESTAMP" => "time",
        "BOOL" or "BOOLEAN" => "bool",
        _ => "text",
    };
}
