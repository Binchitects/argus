using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Llm.Core.Insights;
using Npgsql;
using NpgsqlTypes;

namespace Llm.Api.Insights;

/// <summary>
/// Data files as a database: each file uploaded (CSV, TSV, Excel, JSON), or each sheet of a workbook, a table in a schema
/// of the source's own in the stack's PostgreSQL, read through a login role of its own that may read only that schema.
/// People ask it as they ask PostgreSQL, with the same checks (its plan, read only); every table uploaded is open.
/// </summary>
public sealed partial class FilesData : IDataEngine
{
    private readonly PostgresData _postgres = new();

    public string Kind => DataSource.Files;
    public string Title => "PostgreSQL";
    public string Names => _postgres.Names;
    public string Month => _postgres.Month;
    public bool GrantsOnly => true;

    public string? CheckConnection(string? connection) => _postgres.CheckConnection(connection);

    public string Where(string connection) => "uploaded files";

    public Task<(List<DataTable> Tables, List<string> Warnings)> ReadAsync(string connection, CancellationToken ct) => _postgres.ReadAsync(connection, ct);

    public Task<DataResult> RunAsync(string connection, string sql, IReadOnlyCollection<string> allowed, int mostRows, int seconds, CancellationToken ct) =>
        _postgres.RunAsync(connection, sql, allowed, mostRows, seconds, ct);

    /// <summary>The source's schema, and the role that reads it: one name.</summary>
    public static string SchemaOf(Guid source) => $"files_{source:N}";

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<NpgsqlConnection> OwnerAsync(string owner, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(owner);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// The source's schema and its role (made again with a new password when it is missing, as after a restore into
    /// another server, which keeps no roles); its connection. The stack's database user must be allowed to make roles.
    /// </summary>
    public static async Task<string> EnsureAsync(string owner, Guid source, CancellationToken ct)
    {
        var name = SchemaOf(source);
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await using var conn = await OwnerAsync(owner, ct);
        try
        {
            await ExecAsync(conn, $"""
                do $$ begin
                  if exists (select from pg_roles where rolname = '{name}') then
                    alter role {Quote(name)} login password '{password}';
                  else
                    create role {Quote(name)} login password '{password}' nosuperuser nocreatedb nocreaterole noinherit connection limit 20;
                  end if;
                end $$;
                create schema if not exists {Quote(name)};
                grant usage on schema {Quote(name)} to {Quote(name)};
                grant select on all tables in schema {Quote(name)} to {Quote(name)};
                """, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new DataException($"The stack's database user may not make roles ({ex.MessageText}): files are read by a role of their own. Give it CREATEROLE.");
        }
        return new NpgsqlConnectionStringBuilder(owner) { Username = name, Password = password }.ConnectionString;
    }

    /// <summary>The source's schema, its tables and its role, gone.</summary>
    public static async Task DropAsync(string owner, Guid source, CancellationToken ct)
    {
        var name = SchemaOf(source);
        await using var conn = await OwnerAsync(owner, ct);
        await ExecAsync(conn, $"drop schema if exists {Quote(name)} cascade; drop role if exists {Quote(name)}", ct);
    }

    public static async Task DropTableAsync(string owner, Guid source, string table, CancellationToken ct)
    {
        await using var conn = await OwnerAsync(owner, ct);
        await ExecAsync(conn, $"drop table if exists {Quote(SchemaOf(source))}.{Quote(table)}", ct);
    }

    /// <summary>A name PostgreSQL takes without quotes where it can: lower case, letters (any script), digits and _, at most 60 bytes.</summary>
    public static string Safe(string text, string fallback)
    {
        var s = Underscores().Replace(new string([.. text.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_')]), "_").Trim('_');
        if (s.Length == 0)
        {
            s = fallback;
        }
        if (char.IsDigit(s[0]))
        {
            s = "n_" + s;
        }
        while (Encoding.UTF8.GetByteCount(s) > 60)
        {
            s = s[..^1];
        }
        return s.TrimEnd('_');
    }

    [GeneratedRegex("_+")]
    private static partial Regex Underscores();

    private enum ColumnType { Bool, Int, Num, Real, Date, Time, Text }

    private static readonly string[] Truths = ["true", "yes"];
    private static readonly string[] Falsehoods = ["false", "no"];

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?)?$")]
    private static partial Regex IsoTime();

    private static bool Integral(string s) => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) && !(s.Length > 1 && s[0] == '0');

    private static bool Numeric(string s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !(s.Length > 1 && s[0] == '0' && s[1] != '.');

    /// <summary>The narrowest type every value of a column fits: truth, whole numbers, numbers, dates, times, or text.</summary>
    private static ColumnType TypeOf(IEnumerable<object?> values)
    {
        bool truth = true, whole = true, number = true, big = false, date = true, time = true, any = false;
        foreach (var v in values)
        {
            if (v is null)
            {
                continue;
            }
            any = true;
            switch (v)
            {
                case bool:
                    whole = number = date = time = false;
                    break;
                case long or int:
                    truth = date = time = false;
                    break;
                case double d:
                    truth = date = time = false;
                    whole &= Math.Abs(d) < 9e15 && d == Math.Floor(d);
                    big |= !double.IsFinite(d) || Math.Abs(d) > 7e27;
                    break;
                case decimal m:
                    truth = date = time = false;
                    whole &= m == decimal.Truncate(m) && Math.Abs(m) < long.MaxValue;
                    break;
                case DateTime t:
                    truth = whole = number = false;
                    date &= t.TimeOfDay == TimeSpan.Zero;
                    break;
                case string s:
                    var trimmed = s.Trim();
                    truth &= Truths.Contains(trimmed, StringComparer.OrdinalIgnoreCase) || Falsehoods.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
                    whole &= Integral(trimmed);
                    number &= Numeric(trimmed);
                    var iso = IsoTime().IsMatch(trimmed);
                    time &= iso && DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);
                    date &= iso && trimmed.Length == 10;
                    break;
                default:
                    truth = whole = number = date = time = false;
                    break;
            }
        }
        return !any ? ColumnType.Text : truth ? ColumnType.Bool : whole ? ColumnType.Int : number ? (big ? ColumnType.Real : ColumnType.Num)
            : date ? ColumnType.Date : time ? ColumnType.Time : ColumnType.Text;
    }

    private static (string Sql, NpgsqlDbType Db) Pg(ColumnType t) => t switch
    {
        ColumnType.Bool => ("boolean", NpgsqlDbType.Boolean),
        ColumnType.Int => ("bigint", NpgsqlDbType.Bigint),
        ColumnType.Num => ("numeric", NpgsqlDbType.Numeric),
        ColumnType.Real => ("double precision", NpgsqlDbType.Double),
        ColumnType.Date => ("date", NpgsqlDbType.Date),
        ColumnType.Time => ("timestamp", NpgsqlDbType.Timestamp),
        _ => ("text", NpgsqlDbType.Text),
    };

    private static object Converted(object v, ColumnType t) => t switch
    {
        ColumnType.Bool => v is bool b ? b : Truths.Contains(((string)v).Trim(), StringComparer.OrdinalIgnoreCase),
        ColumnType.Int => v switch { long l => l, int i => (long)i, double d => (long)d, decimal m => (long)m, _ => long.Parse(((string)v).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) },
        ColumnType.Num => v switch { long l => (decimal)l, int i => (decimal)i, double d => (decimal)d, decimal m => m, _ => decimal.Parse(((string)v).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) },
        ColumnType.Real => v switch { long l => (double)l, int i => (double)i, double d => d, decimal m => (double)m, _ => double.Parse(((string)v).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) },
        ColumnType.Date => v is DateTime t1 ? DateOnly.FromDateTime(t1) : DateOnly.ParseExact(((string)v).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        ColumnType.Time => DateTime.SpecifyKind(v is DateTime t2 ? t2 : DateTime.Parse(((string)v).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Unspecified),
        _ => v switch
        {
            DateTime t3 => t3.ToString(t3.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        },
    };

    /// <summary>What an upload made: a table, its rows and columns.</summary>
    public sealed record Imported(string Table, string From, int Rows, int Columns);

    /// <summary>
    /// A sheet as a table of the source (one of the same name is replaced): its columns named from its header (the header
    /// kept as their comment), typed by their values, its rows copied in, readable by the source's role only.
    /// </summary>
    public static async Task<Imported> ImportAsync(string owner, Guid source, Sheet sheet, string from, CancellationToken ct)
    {
        var schema = Quote(SchemaOf(source));
        var table = Safe(sheet.Name, "data");
        var width = Math.Max(sheet.Header.Count, sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Length));
        var names = new List<string>();
        var headers = new List<string>();
        for (var i = 0; i < width; i++)
        {
            var header = i < sheet.Header.Count ? sheet.Header[i].Trim() : "";
            var name = Safe(header, $"column_{i + 1}");
            for (var n = 2; names.Contains(name); n++)
            {
                name = $"{Safe(header, $"column_{i + 1}")}_{n}";
            }
            names.Add(name);
            headers.Add(header);
        }
        var types = Enumerable.Range(0, width).Select(i => TypeOf(sheet.Rows.Select(r => i < r.Length ? r[i] : null))).ToList();
        await using var conn = await OwnerAsync(owner, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var columns = string.Join(", ", names.Select((n, i) => $"{Quote(n)} {Pg(types[i]).Sql}"));
        var comments = new StringBuilder($"comment on table {schema}.{Quote(table)} is {Literal(from)};");
        for (var i = 0; i < width; i++)
        {
            if (headers[i].Length > 0 && headers[i] != names[i])
            {
                comments.Append(CultureInfo.InvariantCulture, $"comment on column {schema}.{Quote(table)}.{Quote(names[i])} is {Literal(headers[i])};");
            }
        }
        await using (var cmd = new NpgsqlCommand($"drop table if exists {schema}.{Quote(table)}; create table {schema}.{Quote(table)} ({columns}); {comments} grant select on {schema}.{Quote(table)} to {schema}", conn, tx))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var rows = 0;
        await using (var copy = await conn.BeginBinaryImportAsync($"copy {schema}.{Quote(table)} ({string.Join(", ", names.Select(Quote))}) from stdin (format binary)", ct))
        {
            foreach (var row in sheet.Rows)
            {
                await copy.StartRowAsync(ct);
                for (var i = 0; i < width; i++)
                {
                    var v = i < row.Length ? row[i] : null;
                    if (v is null)
                    {
                        await copy.WriteNullAsync(ct);
                    }
                    else
                    {
                        await copy.WriteAsync(Converted(v, types[i]), Pg(types[i]).Db, ct);
                    }
                }
                rows++;
            }
            await copy.CompleteAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new Imported(table, from, rows, width);
    }

    private static string Literal(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}
