using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Insights;

namespace Llm.Api.Insights;

/// <summary>
/// A kind of database people ask: its connection checked and shown without its password, its tables read, and a query run
/// that only reads, and only what is open to questions. PostgreSQL and SQL Server prove each query by its plan; MySQL and
/// MariaDB, whose plans do not name their tables' databases, by the account's grants (it may read nothing else).
/// </summary>
public interface IDataEngine
{
    string Kind { get; }

    /// <summary>Its name, for people and the model ("PostgreSQL").</summary>
    string Title { get; }

    /// <summary>How the model writes a table and a column's name here, and how many rows it asks for.</summary>
    string Names { get; }

    /// <summary>A month's start from a time column, as the model writes it here.</summary>
    string Month { get; }

    /// <summary>Whether the account's grants alone decide what a query reads (MySQL): then every table it may read is open.</summary>
    bool GrantsOnly => false;

    /// <summary>The connection, or why it is not one that can be used.</summary>
    string? CheckConnection(string? connection);

    /// <summary>Who the connection signs in as, and where: never its password.</summary>
    string Where(string connection);

    /// <summary>The tables and views the account may read, with their columns; and what is worth knowing about the account.</summary>
    Task<(List<DataTable> Tables, List<string> Warnings)> ReadAsync(string connection, CancellationToken ct);

    /// <summary>Runs the query read only, reading only <paramref name="allowed"/>, and brings back at most <paramref name="mostRows"/> rows.</summary>
    Task<DataResult> RunAsync(string connection, string sql, IReadOnlyCollection<string> allowed, int mostRows, int seconds, CancellationToken ct);
}

/// <summary>The kinds of database here, and what they share: the tables as stored and told to the model, rows as JSON.</summary>
public static class DataEngines
{
    public const int MostTables = 500;
    public const int MostSql = 20_000;
    private const int MostText = 1000;

    public static readonly IReadOnlyDictionary<string, IDataEngine> All = new Dictionary<string, IDataEngine>(StringComparer.Ordinal)
    {
        [DataSource.Postgres] = new PostgresData(),
        [DataSource.MySql] = new MySqlData(),
        [DataSource.SqlServer] = new SqlServerData(),
        [DataSource.Files] = new FilesData(),
    };

    public static IDataEngine Of(string kind) => All.TryGetValue(kind, out var e) ? e : throw new DataException($"\"{kind}\" is not a kind of database here.");

    public static IDataEngine Of(DataSource s) => Of(s.Kind);

    public static List<DataTable> Parse(string schema) => JsonSerializer.Deserialize<List<DataTable>>(schema, JsonSerializerOptions.Web) ?? [];

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

    /// <summary>The rows of a result, at most <paramref name="mostRows"/>, each value as JSON.</summary>
    public static async Task<DataResult> RowsAsync(DbDataReader r, Func<string, string> kindOf, Func<DbDataReader, int, JsonValue?> valueOf, int mostRows, long startedAt, CancellationToken ct)
    {
        var columns = Enumerable.Range(0, r.FieldCount).Select(i => new ResultColumn(r.GetName(i), kindOf(r.GetDataTypeName(i)))).ToList();
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
                row.Add(r.IsDBNull(i) ? null : valueOf(r, i));
            }
            rows.Add(row);
        }
        return new DataResult(columns, rows, truncated, (int)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    /// <summary>A value as JSON: numbers, truth, times in ISO 8601, bytes by their length, and the rest as text (cut long).</summary>
    public static JsonValue Json(object value) => value switch
    {
        bool b => JsonValue.Create(b),
        byte n => JsonValue.Create(n),
        sbyte n => JsonValue.Create(n),
        short n => JsonValue.Create(n),
        ushort n => JsonValue.Create(n),
        int n => JsonValue.Create(n),
        uint n => JsonValue.Create(n),
        long n => JsonValue.Create(n),
        ulong n => JsonValue.Create(n),
        decimal d => JsonValue.Create(d),
        float f => float.IsFinite(f) ? JsonValue.Create(f) : JsonValue.Create(f.ToString(CultureInfo.InvariantCulture)),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
        DateTime t => JsonValue.Create(t.Kind == DateTimeKind.Utc ? t.ToString("O", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset t => JsonValue.Create(t.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        byte[] bytes => JsonValue.Create($"({bytes.Length} bytes)"),
        var v => JsonValue.Create(Cut(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "")),
    };

    public static string Cut(string s) => s.Length > MostText ? s[..MostText] + "…" : s;
}

/// <summary>How a database's SQL is lexed, for the check that a query is one statement that reads.</summary>
/// <param name="Backslashes">A backslash escapes the next character in a string (MySQL).</param>
/// <param name="HashComments"># starts a comment to the end of the line (MySQL).</param>
/// <param name="DashSpace">-- starts a comment only before a space or the line's end (MySQL); else always.</param>
/// <param name="NestedComments">/* … */ nest (PostgreSQL, SQL Server).</param>
/// <param name="DollarQuotes">$$ … $$ and $tag$ … $tag$ are strings (PostgreSQL).</param>
/// <param name="Brackets">[ … ] is a name (SQL Server).</param>
/// <param name="Backticks">` … ` is a name (MySQL).</param>
/// <param name="Starts">The words a query that reads begins with.</param>
public sealed record SqlDialect(bool Backslashes, bool HashComments, bool DashSpace, bool NestedComments, bool DollarQuotes, bool Brackets, bool Backticks, string[] Starts)
{
    public static readonly SqlDialect Postgres = new(false, false, false, true, true, false, false, ["select", "with", "values", "table"]);
    public static readonly SqlDialect MySql = new(true, true, true, false, false, false, true, ["select", "with", "values", "table"]);
    public static readonly SqlDialect SqlServer = new(false, false, false, true, false, true, false, ["select", "with"]);
}

public static class SqlCheck
{
    /// <summary>
    /// Why this is not one query that only reads (more than one statement, not a SELECT, a MySQL comment the server runs),
    /// or null; and the query without its last semicolon. The databases check again themselves: this keeps the plain
    /// mistakes from reaching them.
    /// </summary>
    public static (string Sql, string? Problem) One(string? sql, SqlDialect d)
    {
        var text = (sql ?? "").Trim();
        if (text.Length == 0)
        {
            return (text, "There is no query.");
        }
        if (text.Length > DataEngines.MostSql)
        {
            return (text, $"A query is at most {DataEngines.MostSql:N0} characters.");
        }
        string? first = null;
        var end = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (ch == '-' && next == '-' && (!d.DashSpace || i + 2 >= text.Length || char.IsWhiteSpace(text[i + 2]) || char.IsControl(text[i + 2])))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (ch == '#' && d.HashComments)
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (ch == '/' && next == '*')
            {
                if (d.HashComments && i + 2 < text.Length && text[i + 2] == '!')
                {
                    // MySQL runs what is in /*! … */: no query here needs it.
                    return (text, "A /*! … */ comment is run by MySQL as part of the query: leave it out.");
                }
                var depth = 1;
                i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        depth--;
                        i++;
                    }
                    else if (d.NestedComments && text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                    {
                        depth++;
                        i++;
                    }
                    i++;
                }
                i--;
            }
            else if (ch is '\'' or '"' || (ch == '`' && d.Backticks) || (ch == '[' && d.Brackets))
            {
                var close = ch == '[' ? ']' : ch;
                i++;
                while (i < text.Length)
                {
                    if (d.Backslashes && ch != '`' && text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (text[i] == close)
                    {
                        if (i + 1 < text.Length && text[i + 1] == close)
                        {
                            i += 2;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
            }
            else if (ch == '$' && d.DollarQuotes && DollarTag(text, i) is { } tag)
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
            else if (char.IsLetter(ch) && first is null)
            {
                var start = i;
                while (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_'))
                {
                    i++;
                }
                first = text[start..(i + 1)].ToLowerInvariant();
            }
        }
        var query = end >= 0 ? text[..end].TrimEnd() : text;
        return d.Starts.Contains(first)
            ? (query, null)
            : (query, "Only a query that reads (SELECT, or WITH … SELECT) can be run here.");
    }

    /// <summary>A dollar quote's tag at i ($$ or $name$), or null.</summary>
    private static string? DollarTag(string text, int i)
    {
        var j = i + 1;
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
        {
            j++;
        }
        return j < text.Length && text[j] == '$' && (j == i + 1 || !char.IsDigit(text[i + 1])) ? text[i..(j + 1)] : null;
    }
}
