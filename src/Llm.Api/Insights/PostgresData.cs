using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public static class PostgresData
{
    public const int MostTables = 500;
    public const int MostSql = 20_000;
    private const int MostText = 1000;

    /// <summary>The connection, or why it is not one that can be used.</summary>
    public static string? CheckConnection(string? connection)
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
    public static async Task<(List<DataTable> Tables, List<string> Warnings)> ReadAsync(string connection, CancellationToken ct)
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

    /// <summary>Why this is not one query that only reads (more than one statement, not a SELECT), or null; and the query without its last semicolon.</summary>
    public static (string Sql, string? Problem) CheckSql(string? sql)
    {
        var text = (sql ?? "").Trim();
        if (text.Length == 0)
        {
            return (text, "There is no query.");
        }
        if (text.Length > MostSql)
        {
            return (text, $"A query is at most {MostSql:N0} characters.");
        }
        var words = new List<string>();
        var end = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 1;
            }
            else if (ch is '\'' or '"')
            {
                i++;
                while (i < text.Length && (text[i] != ch || (i + 1 < text.Length && text[i + 1] == ch && ++i > 0)))
                {
                    i++;
                }
            }
            else if (ch == '$' && DollarTag(text, i) is { } tag)
            {
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + tag.Length - 1;
            }
            else if (ch == ';')
            {
                end = i;
                if (text[(i + 1)..].Trim().Trim(';').Trim().Length > 0)
                {
                    return (text, "One query at a time: there is more after its semicolon.");
                }
                break;
            }
            else if (char.IsLetter(ch) && words.Count < 1)
            {
                var start = i;
                while (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_'))
                {
                    i++;
                }
                words.Add(text[start..(i + 1)].ToLowerInvariant());
            }
        }
        var query = end >= 0 ? text[..end].TrimEnd() : text;
        return words.FirstOrDefault() is "select" or "with" or "values" or "table"
            ? (query, null)
            : (query, "Only a query that reads (SELECT, or WITH … SELECT) can be run here.");
    }

    /// <summary>A dollar quote's tag at i ($$ or $name$), or null.</summary>
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

    private static string? DollarTag(string text, int i)
    {
        var j = i + 1;
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
        {
            j++;
        }
        return j < text.Length && text[j] == '$' && (j == i + 1 || !char.IsDigit(text[i + 1])) ? text[i..(j + 1)] : null;
    }

    /// <summary>
    /// Runs the query read only, after its plan shows it reads only the allowed tables (and the tables of allowed views),
    /// and brings back at most <paramref name="mostRows"/> rows.
    /// </summary>
    public static async Task<DataResult> RunAsync(string connection, string sql, IReadOnlyCollection<string> allowed, int mostRows, int seconds, CancellationToken ct)
    {
        var (query, problem) = CheckSql(sql);
        if (problem is not null)
        {
            throw new DataException(problem);
        }
        var clock = Stopwatch.StartNew();
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
            var columns = Enumerable.Range(0, r.FieldCount).Select(i => new ResultColumn(r.GetName(i), Kind(r.GetDataTypeName(i)))).ToList();
            var rows = new List<JsonArray>();
            var truncated = false;
            while (await r.ReadAsync(ct))
            {
                if (rows.Count >= mostRows)
                {
                    truncated = true;
                    break;
                }
                var row = new JsonArray();
                for (var i = 0; i < r.FieldCount; i++)
                {
                    row.Add(Value(r, i));
                }
                rows.Add(row);
            }
            return new DataResult(columns, rows, truncated, (int)clock.ElapsedMilliseconds);
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
    private static string Kind(string type) => type switch
    {
        "smallint" or "integer" or "bigint" or "numeric" or "real" or "double precision" or "money" => "number",
        "date" => "date",
        "timestamp without time zone" or "timestamp with time zone" => "time",
        "boolean" => "bool",
        _ => "text",
    };

    private static JsonValue? Value(NpgsqlDataReader r, int i)
    {
        if (r.IsDBNull(i))
        {
            return null;
        }
        try
        {
            return r.GetValue(i) switch
            {
                bool b => JsonValue.Create(b),
                short s => JsonValue.Create(s),
                int n => JsonValue.Create(n),
                long l => JsonValue.Create(l),
                decimal d => JsonValue.Create(d),
                float f => float.IsFinite(f) ? JsonValue.Create(f) : JsonValue.Create(f.ToString(CultureInfo.InvariantCulture)),
                double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
                DateTime t => JsonValue.Create(t.Kind == DateTimeKind.Utc ? t.ToString("O", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
                DateTimeOffset t => JsonValue.Create(t.ToString("O", CultureInfo.InvariantCulture)),
                DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                byte[] bytes => JsonValue.Create($"({bytes.Length} bytes)"),
                var v => JsonValue.Create(Cut(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "")),
            };
        }
        catch (InvalidCastException)
        {
            // A type with no .NET value of its own (a range, a custom type): its text.
            return JsonValue.Create(Cut(r.GetProviderSpecificValue(i)?.ToString() ?? ""));
        }
    }

    private static string Cut(string s) => s.Length > MostText ? s[..MostText] + "…" : s;

    /// <summary>The tables as the model reads them: name, kind, what it is, then its columns and their types.</summary>
    public static string Describe(IEnumerable<DataTable> tables)
    {
        var w = new System.Text.StringBuilder();
        foreach (var t in tables)
        {
            w.Append(t.Name).Append(" (").Append(t.Kind).Append(')');
            if (t.Comment is { Length: > 0 })
            {
                w.Append(": ").Append(t.Comment.ReplaceLineEndings(" "));
            }
            w.AppendLine();
            foreach (var c in t.Columns.Take(80))
            {
                w.Append("  ").Append(c.Name).Append(' ').Append(c.Type);
                if (c.Comment is { Length: > 0 })
                {
                    w.Append(" -- ").Append(c.Comment.ReplaceLineEndings(" "));
                }
                w.AppendLine();
            }
        }
        return w.ToString();
    }

    public static List<DataTable> Parse(string schema) => JsonSerializer.Deserialize<List<DataTable>>(schema, JsonSerializerOptions.Web) ?? [];
}
