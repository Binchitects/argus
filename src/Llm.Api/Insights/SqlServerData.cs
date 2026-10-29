using System.Data;
using System.Diagnostics;
using System.Xml.Linq;
using Llm.Core.Insights;
using Microsoft.Data.SqlClient;

namespace Llm.Api.Insights;

/// <summary>
/// A SQL Server database (Azure SQL too) people ask. Each query's plan is read first (SHOWPLAN_XML, which runs nothing): it
/// must be one SELECT that reads only the open tables and the tables of open views, with no linked server and no function
/// of the database's own; then it runs in a transaction that is rolled back, with a time limit. A login that is sysadmin,
/// owns the database or may bulk-load the server's files is refused.
/// </summary>
public sealed class SqlServerData : IDataEngine
{
    public string Kind => DataSource.SqlServer;
    public string Title => "SQL Server";
    public string Names => "with their schema (dbo.orders), and the columns' short names in brackets ([Orders]); TOP (n) for the rows, never LIMIT";
    public string Month => "datefromparts(year(placed_at), month(placed_at), 1) as [Month]";

    public string? CheckConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
        {
            return "Give the connection: Server=…,1433;Database=…;User ID=…;Password=…;Encrypt=True";
        }
        try
        {
            var b = new SqlConnectionStringBuilder(connection);
            if (string.IsNullOrEmpty(b.DataSource) || string.IsNullOrEmpty(b.InitialCatalog))
            {
                return "The connection needs a Server and a Database.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return $"That is not a connection string SQL Server takes: {ex.Message}";
        }
        return null;
    }

    public string Where(string connection)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connection);
            return $"{(b.UserID is { Length: > 0 } u ? u : "integrated")}@{b.DataSource}/{b.InitialCatalog}";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return "";
        }
    }

    /// <summary>Its own connection each time (no pool), asking for a readable replica where there is one, named in the server's sessions.</summary>
    private static string Prepared(string connection, int seconds) => new SqlConnectionStringBuilder(connection)
    {
        Pooling = false, ConnectTimeout = 10, CommandTimeout = seconds + 5, ApplicationIntent = ApplicationIntent.ReadOnly, MultipleActiveResultSets = false,
        ApplicationName = "Argus Arena (Ask your data)",
    }.ConnectionString;

    private static async Task<SqlConnection> OpenAsync(string connection, int seconds, CancellationToken ct)
    {
        var conn = new SqlConnection(Prepared(connection, seconds));
        try
        {
            await conn.OpenAsync(ct);
            await ExecAsync(conn, "set lock_timeout 2000; set xact_abort on; set transaction isolation level read committed", ct);
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException or InvalidOperationException)
        {
            await conn.DisposeAsync();
            throw new DataException($"The database could not be reached: {ex.Message}");
        }
        return conn;
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct, SqlTransaction? tx = null)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Refuses a login that is sysadmin or controls the server, owns the database, or may bulk-load files (OPENROWSET(BULK …));
    /// and one without SHOWPLAN, whose queries' plans cannot be read.
    /// </summary>
    private static async Task CheckLoginAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            select isnull(is_srvrolemember('sysadmin'), 0) | isnull(has_perms_by_name(null, null, 'CONTROL SERVER'), 0),
              isnull(is_member('db_owner'), 0) | isnull(has_perms_by_name(null, 'DATABASE', 'CONTROL'), 0),
              isnull(has_perms_by_name(null, null, 'ADMINISTER BULK OPERATIONS'), 0) | isnull(is_srvrolemember('bulkadmin'), 0),
              isnull(has_perms_by_name(null, 'DATABASE', 'SHOWPLAN'), 0), user_name()
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        if (r.GetInt32(0) != 0)
        {
            throw new DataException("The connection signs in as a sysadmin. Give it a login that may only read the tables people ask.");
        }
        if (r.GetInt32(1) != 0)
        {
            throw new DataException("The connection's login owns the database. Give it a login that may only read the tables people ask.");
        }
        if (r.GetInt32(2) != 0)
        {
            throw new DataException("The connection's login may bulk-load the server's files. Give it a login that may only read the tables people ask.");
        }
        if (r.GetInt32(3) == 0)
        {
            throw new DataException($"Every query's plan is read before it runs, which takes SHOWPLAN: grant it (grant showplan to [{r.GetString(4)}]).");
        }
    }

    public async Task<(List<DataTable> Tables, List<string> Warnings)> ReadAsync(string connection, CancellationToken ct)
    {
        await using var conn = await OpenAsync(connection, 30, ct);
        await CheckLoginAsync(conn, ct);
        var tables = new Dictionary<string, DataTable>(StringComparer.Ordinal);
        await using (var cmd = new SqlCommand("""
            select s.name, o.name, rtrim(o.type), cast(ep.value as nvarchar(4000)), c.name,
              type_name(c.user_type_id) + case
                when type_name(c.user_type_id) in ('varchar', 'char', 'varbinary', 'binary') then '(' + iif(c.max_length = -1, 'max', cast(c.max_length as varchar(10))) + ')'
                when type_name(c.user_type_id) in ('nvarchar', 'nchar') then '(' + iif(c.max_length = -1, 'max', cast(c.max_length / 2 as varchar(10))) + ')'
                when type_name(c.user_type_id) in ('decimal', 'numeric') then '(' + cast(c.precision as varchar(3)) + ', ' + cast(c.scale as varchar(3)) + ')'
                else '' end,
              cast(epc.value as nvarchar(4000))
            from sys.objects o
            join sys.schemas s on s.schema_id = o.schema_id
            join sys.columns c on c.object_id = o.object_id
            left join sys.extended_properties ep on ep.class = 1 and ep.major_id = o.object_id and ep.minor_id = 0 and ep.name = 'MS_Description'
            left join sys.extended_properties epc on epc.class = 1 and epc.major_id = o.object_id and epc.minor_id = c.column_id and epc.name = 'MS_Description'
            where o.type in ('U', 'V') and o.is_ms_shipped = 0 and has_perms_by_name(quotename(s.name) + '.' + quotename(o.name), 'OBJECT', 'SELECT') = 1
            order by s.name, o.name, c.column_id
            """, conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = $"{r.GetString(0)}.{r.GetString(1)}";
                if (!tables.TryGetValue(name, out var t))
                {
                    if (tables.Count >= DataEngines.MostTables)
                    {
                        continue;
                    }
                    tables[name] = t = new DataTable(name, r.GetString(2) == "V" ? "view" : "table", r.IsDBNull(3) ? null : r.GetString(3), []);
                }
                t.Columns.Add(new DataColumn(r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }
        var warnings = new List<string>();
        if (tables.Count >= DataEngines.MostTables)
        {
            warnings.Add($"The login sees more than {DataEngines.MostTables} tables: only the first {DataEngines.MostTables} are listed.");
        }
        await using (var cmd = new SqlCommand("""
            select count(*) from sys.tables t join sys.schemas s on s.schema_id = t.schema_id
            where t.is_ms_shipped = 0 and (has_perms_by_name(quotename(s.name) + '.' + quotename(t.name), 'OBJECT', 'INSERT') = 1
              or has_perms_by_name(quotename(s.name) + '.' + quotename(t.name), 'OBJECT', 'UPDATE') = 1
              or has_perms_by_name(quotename(s.name) + '.' + quotename(t.name), 'OBJECT', 'DELETE') = 1)
            """, conn))
        {
            var writable = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (writable > 0)
            {
                warnings.Add($"This login may change {writable} {(writable == 1 ? "table" : "tables")}. Every question is rolled back, but a login that may only read is the surer guard.");
            }
        }
        return ([.. tables.Values], warnings);
    }

    public async Task<DataResult> RunAsync(string connection, string sql, IReadOnlyCollection<string> allowed, int mostRows, int seconds, CancellationToken ct)
    {
        var (query, problem) = SqlCheck.One(sql, SqlDialect.SqlServer);
        if (problem is not null)
        {
            throw new DataException(problem);
        }
        var started = Stopwatch.GetTimestamp();
        await using var conn = await OpenAsync(connection, seconds, ct);
        try
        {
            await CheckLoginAsync(conn, ct);
            await CheckPlanAsync(conn, query, allowed, ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using var cmd = new SqlCommand(query, conn, tx) { CommandTimeout = seconds };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var result = await DataEngines.RowsAsync(r, KindOf, (reader, i) => DataEngines.Json(reader.GetValue(i)), mostRows, started, ct);
            if (result.Truncated)
            {
                // The rows not read are not fetched: the reader would drain them as it closes.
                cmd.Cancel();
            }
            return result;
            // Never committed: the transaction rolls back as it is disposed.
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            throw new DataException($"The query ran longer than {seconds} seconds, the most here. Ask for less, or a narrower time.");
        }
        catch (SqlException ex)
        {
            throw new DataException(ex.Message);
        }
    }

    private static readonly XNamespace Plan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static string Unquote(string? name) => (name ?? "").Trim('[', ']').Replace("]]", "]", StringComparison.Ordinal);

    /// <summary>
    /// The query's plan, read without running it: one SELECT; every table read in this database and open (or read by an
    /// open view); no remote query (linked servers, OPENROWSET, OPENQUERY) and no function of the database's own.
    /// </summary>
    private static async Task CheckPlanAsync(SqlConnection conn, string query, IReadOnlyCollection<string> allowed, CancellationToken ct)
    {
        string database;
        await using (var db = new SqlCommand("select db_name()", conn))
        {
            database = (string)(await db.ExecuteScalarAsync(ct))!;
        }
        var readable = await ReadableAsync(conn, allowed, ct);
        var plans = new List<XDocument>();
        await ExecAsync(conn, "set showplan_xml on", ct);
        try
        {
            await using var cmd = new SqlCommand(query, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            do
            {
                while (await r.ReadAsync(ct))
                {
                    plans.Add(XDocument.Parse(r.GetString(0)));
                }
            }
            while (await r.NextResultAsync(ct));
        }
        finally
        {
            await ExecAsync(conn, "set showplan_xml off", CancellationToken.None);
        }
        // The batch's own statements (a function's are nested under the statement that calls it).
        var statements = plans.SelectMany(p => p.Root?.Elements(Plan + "BatchSequence").Elements(Plan + "Batch").Elements(Plan + "Statements").Elements() ?? []).ToList();
        if (statements.Count != 1 || statements[0].Name != Plan + "StmtSimple" || (string?)statements[0].Attribute("StatementType") != "SELECT")
        {
            throw new DataException(statements.Count != 1
                ? "One query at a time: SQL Server reads more than one statement in it."
                : $"Only a query that reads can be run here: SQL Server reads it as {(string?)statements[0].Attribute("StatementType") ?? "something else"}.");
        }
        var refused = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in statements[0].Descendants(Plan + "Object"))
        {
            // Built-in table functions (OPENJSON, STRING_SPLIT) name no database.
            if (o.Attribute("Database") is not { } at || o.Attribute("Table") is null)
            {
                continue;
            }
            var name = $"{Unquote((string?)o.Attribute("Schema"))}.{Unquote((string?)o.Attribute("Table"))}";
            if (!string.Equals(Unquote(at.Value), database, StringComparison.OrdinalIgnoreCase))
            {
                refused.Add($"{Unquote(at.Value)}.{name}");
            }
            else if (!readable.Contains(name))
            {
                refused.Add(name);
            }
        }
        foreach (var op in statements[0].Descendants(Plan + "RelOp").Select(e => (string?)e.Attribute("PhysicalOp") ?? "").Where(p => p.StartsWith("Remote", StringComparison.Ordinal)).Distinct())
        {
            refused.Add($"another server ({op})");
        }
        foreach (var f in statements[0].Descendants(Plan + "UserDefinedFunction").Select(e => (string?)e.Attribute("FunctionName") ?? "").Distinct())
        {
            refused.Add($"{f}()");
        }
        if (refused.Count > 0)
        {
            throw new DataException($"The query reads what is not open to questions: {string.Join(", ", refused)}. Use only: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
        }
    }

    /// <summary>
    /// The open tables, and what the open views read (all the way down: a plan names the tables under a view, not the
    /// view). Reading a view's references takes VIEW DEFINITION on it.
    /// </summary>
    private static async Task<HashSet<string>> ReadableAsync(SqlConnection conn, IReadOnlyCollection<string> allowed, CancellationToken ct)
    {
        var readable = new HashSet<string>(allowed, StringComparer.Ordinal);
        var views = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new SqlCommand("select s.name + '.' + v.name from sys.views v join sys.schemas s on s.schema_id = v.schema_id", conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                views.Add(r.GetString(0));
            }
        }
        var next = new Queue<string>(allowed.Where(views.Contains));
        while (next.TryDequeue(out var view))
        {
            var dot = view.IndexOf('.', StringComparison.Ordinal);
            await using var cmd = new SqlCommand("""
                select distinct s.name + '.' + o.name, rtrim(o.type)
                from sys.dm_sql_referenced_entities(@view, 'OBJECT') e
                join sys.objects o on o.object_id = e.referenced_id
                join sys.schemas s on s.schema_id = o.schema_id
                where e.referenced_server_name is null and e.referenced_database_name is null and e.referenced_minor_id = 0
                """, conn);
            cmd.Parameters.AddWithValue("@view", $"[{view[..dot].Replace("]", "]]", StringComparison.Ordinal)}].[{view[(dot + 1)..].Replace("]", "]]", StringComparison.Ordinal)}]");
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (readable.Add(r.GetString(0)) && r.GetString(1) == "V")
                    {
                        next.Enqueue(r.GetString(0));
                    }
                }
            }
            catch (SqlException ex)
            {
                throw new DataException($"SQL Server does not show the login what the view {view} reads ({ex.Message}). Grant it VIEW DEFINITION on the view.");
            }
        }
        return readable;
    }

    /// <summary>A column's kind for the page: number, date, time, bool or text.</summary>
    private static string KindOf(string type) => type switch
    {
        "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "number",
        "date" => "date",
        "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "time",
        "bit" => "bool",
        _ => "text",
    };
}
