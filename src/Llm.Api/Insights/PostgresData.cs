using System.Diagnostics;
using System.Text.Json.Nodes;
using Llm.Core.Insights;
using Npgsql;

namespace Llm.Api.Insights;

/// <summary>Why a data source could not be read, or a query not run, in words for the person (and the model, to fix it).</summary>
public sealed class DataException(string message) : Exception(message);

public sealed record DataColumn(string Name, string Type, string? Comment);

/// <param name="Name">"schema.name".</param>
/// <param name="Kind">table, view, materialized view or foreign table.</param>
public sealed record DataTable(string Name, string Kind, string? Comment, List<DataColumn> Columns);

public sealed record ResultColumn(string Name, string Type);

public sealed record DataResult(List<ResultColumn> Columns, List<JsonArray> Rows, bool Truncated, int Milliseconds);

/// <summary>
/// A PostgreSQL database people ask: its tables read once (those its role may see), every query run in a read-only
/// transaction with a time limit, and only after its plan shows it reads nothing but the tables allowed (a view allowed
/// lets its own tables be read through it). The role should be one that may only read: a superuser, or one that may read
/// the server's files or run its programs, is refused.
/// </summary>
public sealed class PostgresData : IDataEngine
{
    private const int MostTables = DataEngines.MostTables;

    public string Kind => DataSource.Postgres;
    public string Title => "PostgreSQL";
    public string Names => "with their schema (public.orders), and the columns' short names in double quotes (\"Orders\")";
    public string Month => "date_trunc('month', placed_at)::date as \"Month\"";

    public string Where(string connection)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connection);
            return $"{b.Username}@{b.Host}{(b.Port is 5432 ? "" : $":{b.Port}")}/{b.Database}";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    public string? CheckConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
        {
            return "Give the connection: Host=…;Port=5432;Database=…;Username=…;Password=…";
        }
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connection);
            if (string.IsNullOrEmpty(b.Host) || string.IsNullOrEmpty(b.Database) || string.IsNullOrEmpty(b.Username))
            {
                return "The connection needs a Host, a Database and a Username.";
            }
        }
        catch (ArgumentException ex)
        {
            return $"That is not a connection string PostgreSQL takes: {ex.Message}";
        }
        return null;
    }

    /// <summary>Its own connection each time (no pool: nothing one query sets stays for the next), named in the database's activity.</summary>
    private static string Prepared(string connection, int seconds) => new NpgsqlConnectionStringBuilder(connection)
    {
        Pooling = false, Timeout = 10, CommandTimeout = seconds + 5, ApplicationName = "Argus Arena (Ask your data)",
    }.ConnectionString;

    private static async Task<NpgsqlConnection> OpenAsync(string connection, int seconds, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(Prepared(connection, seconds));
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            await conn.DisposeAsync();
            throw new DataException($"The database could not be reached: {ex.Message}");
        }
        return conn;
    }

    /// <summary>Refuses a role that can do more than read: a superuser, or one that reads the server's files or runs its programs.</summary>
    private static async Task CheckRoleAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            select r.rolsuper,
              pg_has_role(current_user, 'pg_read_server_files', 'member') or pg_has_role(current_user, 'pg_write_server_files', 'member')
                or pg_has_role(current_user, 'pg_execute_server_program', 'member')
            from pg_roles r where r.rolname = current_user
            """, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            throw new DataException("The database did not say who its role is.");
        }
        if (r.GetBoolean(0))
        {
            throw new DataException("The connection signs in as a superuser. Give it a role that may only read the tables people ask.");
        }
        if (r.GetBoolean(1))
        {
            throw new DataException("The connection's role may read the server's files or run its programs. Give it a role that may only read the tables people ask.");
        }
    }

    /// <summary>The tables and views the role may see, with their columns; and what is worth knowing about the role.</summary>
    public async Task<(List<DataTable> Tables, List<string> Warnings)> ReadAsync(string connection, CancellationToken ct)
    {
        await using var conn = await OpenAsync(connection, 30, ct);
        await CheckRoleAsync(conn, null, ct);
        var tables = new Dictionary<string, DataTable>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("""
            select n.nspname, c.relname, c.relkind::text, obj_description(c.oid, 'pg_class'), a.attname, format_type(a.atttypid, a.atttypmod), col_description(c.oid, a.attnum)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            where c.relkind in ('r', 'p', 'v', 'm', 'f') and n.nspname not in ('pg_catalog', 'information_schema') and n.nspname not like 'pg\_toast%'
              and has_table_privilege(c.oid, 'select')
            order by n.nspname, c.relname, a.attnum
            """, conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = $"{r.GetString(0)}.{r.GetString(1)}";
                if (!tables.TryGetValue(name, out var t))
                {
                    if (tables.Count >= MostTables)
                    {
                        continue;
                    }
                    var kind = r.GetString(2) switch { "v" => "view", "m" => "materialized view", "f" => "foreign table", _ => "table" };
                    tables[name] = t = new DataTable(name, kind, r.IsDBNull(3) ? null : r.GetString(3), []);
                }
                t.Columns.Add(new DataColumn(r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }
        var warnings = new List<string>();
        if (tables.Count >= MostTables)
        {
            warnings.Add($"The role sees more than {MostTables} tables: only the first {MostTables} are listed.");
        }
        await using (var cmd = new NpgsqlCommand("""
            select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p') and n.nspname not in ('pg_catalog', 'information_schema') and n.nspname not like 'pg\_toast%'
              and has_table_privilege(c.oid, 'insert, update, delete, truncate')
            """, conn))
        {
            var writable = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            if (writable > 0)
            {
                warnings.Add($"This role may change {writable} {(writable == 1 ? "table" : "tables")}. Every question runs read only, but a role that may only read is the surer guard.");
            }
        }
        return ([.. tables.Values], warnings);
    }

    /// <summary>
    /// The query as one statement the server parses alone: with a positional parameter Npgsql sends the text as it is
    /// (it splits nothing at semicolons), and PostgreSQL refuses more than one statement in it. So no string the check
    /// above reads wrongly (E'\'' …) can carry a COMMIT out of the read-only transaction, or a second statement after it.
    /// </summary>
    private static NpgsqlCommand Single(string sql, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = 0 });
        return cmd;
    }

    /// <summary>
    /// Runs the query read only, after its plan shows it reads only the allowed tables (and the tables of allowed views),
    /// and brings back at most <paramref name="mostRows"/> rows.
    /// </summary>
    public async Task<DataResult> RunAsync(string connection, string sql, IReadOnlyCollection<string> allowed, int mostRows, int seconds, CancellationToken ct)
    {
        var (query, problem) = SqlCheck.One(sql, SqlDialect.Postgres);
        if (problem is not null)
        {
            throw new DataException(problem);
        }
        var started = Stopwatch.GetTimestamp();
        await using var conn = await OpenAsync(connection, seconds, ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        try
        {
            await using (var setup = new NpgsqlCommand(
                $"set transaction read only; set local statement_timeout = {seconds * 1000}; set local lock_timeout = 2000; set local idle_in_transaction_session_timeout = {(seconds + 10) * 1000}",
                conn, tx))
            {
                await setup.ExecuteNonQueryAsync(ct);
            }
            await CheckRoleAsync(conn, tx, ct);
            await CheckReadsAsync(conn, tx, query, allowed, ct);
            await using var cmd = Single(query, conn, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await DataEngines.RowsAsync(r, KindOf, Value, mostRows, started, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new DataException($"The query ran longer than {seconds} seconds, the most here. Ask for less, or a narrower time.");
        }
        catch (PostgresException ex)
        {
            throw new DataException(ex.Position > 0 ? $"{ex.MessageText} (at character {ex.Position})" : ex.MessageText);
        }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException)
        {
            throw new DataException($"The query ran longer than {seconds} seconds, the most here.");
        }
        // Never committed: the transaction rolls back as it is disposed.
    }

    /// <summary>The query's plan: every table it reads must be allowed, every function it scans the database's own.</summary>
    private static async Task CheckReadsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string query, IReadOnlyCollection<string> allowed, CancellationToken ct)
    {
        var readable = await ReadableAsync(conn, tx, allowed, ct);
        string plan;
        await using (var explain = Single($"explain (verbose, format json) {query}", conn, tx))
        {
            plan = (string)(await explain.ExecuteScalarAsync(ct))!;
        }
        var refused = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? node)
        {
            if (node is JsonArray a)
            {
                foreach (var n in a)
                {
                    Walk(n);
                }
            }
            else if (node is JsonObject o)
            {
                var schema = o["Schema"]?.GetValue<string>();
                if (o["Relation Name"]?.GetValue<string>() is { } relation && !readable.Contains($"{schema}.{relation}"))
                {
                    refused.Add($"{schema}.{relation}");
                }
                if (o["Function Name"]?.GetValue<string>() is { } function && schema is not "pg_catalog")
                {
                    refused.Add($"{schema}.{function}()");
                }
                foreach (var (_, child) in o)
                {
                    if (child is JsonObject or JsonArray)
                    {
                        Walk(child);
                    }
                }
            }
        }
        Walk(JsonNode.Parse(plan));
        if (refused.Count > 0)
        {
            throw new DataException($"The query reads what is not open to questions: {string.Join(", ", refused)}. Use only: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
        }
    }

    /// <summary>The allowed tables, and the tables (and views, all the way down) the allowed views read.</summary>
    private static async Task<HashSet<string>> ReadableAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyCollection<string> allowed, CancellationToken ct)
    {
        var readable = new HashSet<string>(allowed, StringComparer.Ordinal);
        var reads = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("""
            select distinct vn.nspname || '.' || v.relname, tn.nspname || '.' || t.relname
            from pg_depend d
            join pg_rewrite w on w.oid = d.objid
            join pg_class v on v.oid = w.ev_class
            join pg_namespace vn on vn.oid = v.relnamespace
            join pg_class t on t.oid = d.refobjid
            join pg_namespace tn on tn.oid = t.relnamespace
            where d.classid = 'pg_rewrite'::regclass and d.refclassid = 'pg_class'::regclass and v.relkind = 'v' and t.oid <> v.oid
            """, conn, tx))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                (reads.TryGetValue(r.GetString(0), out var list) ? list : reads[r.GetString(0)] = []).Add(r.GetString(1));
            }
        }
        var next = new Queue<string>(allowed);
        while (next.TryDequeue(out var view))
        {
            foreach (var t in reads.GetValueOrDefault(view) ?? [])
            {
                if (readable.Add(t))
                {
                    next.Enqueue(t);
                }
            }
        }
        return readable;
    }

    /// <summary>A column's kind for the page: number, date, time, bool or text.</summary>
    private static string KindOf(string type) => type switch
    {
        "smallint" or "integer" or "bigint" or "numeric" or "real" or "double precision" or "money" => "number",
        "date" => "date",
        "timestamp without time zone" or "timestamp with time zone" => "time",
        "boolean" => "bool",
        _ => "text",
    };

    private static JsonValue? Value(System.Data.Common.DbDataReader r, int i)
    {
        try
        {
            return DataEngines.Json(r.GetValue(i));
        }
        catch (InvalidCastException)
        {
            // A type with no .NET value of its own (a range, a custom type): its text.
            return JsonValue.Create(DataEngines.Cut(r.GetProviderSpecificValue(i)?.ToString() ?? ""));
        }
    }
}
