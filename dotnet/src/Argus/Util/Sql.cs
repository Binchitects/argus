using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Argus.Util;

/// <summary>
/// Thin helpers over Microsoft.Data.Sqlite that keep the Python module's SQL
/// verbatim.
///
/// The queries are ported unchanged, <c>?</c> placeholders included, because every
/// one of them was measured and reviewed as written, and rewriting them to named
/// parameters by hand is exactly where a port quietly changes a predicate.
/// Microsoft.Data.Sqlite binds by name only, so each bare <c>?</c> is numbered
/// (<c>?1</c>, <c>?2</c>, ...) once per distinct statement text and cached; SQLite
/// treats <c>?NNN</c> identically to the positional form.
/// </summary>
public static class Sql
{
    static readonly ConcurrentDictionary<string, (string Text, int Count)> Rewritten = new();

    static (string Text, int Count) Number(string sql) => Rewritten.GetOrAdd(sql, static s =>
    {
        var sb = new StringBuilder(s.Length + 16);
        int n = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; sb.Append(c); continue; }
            if (c == '?' && (i + 1 >= s.Length || !char.IsDigit(s[i + 1])))
            {
                n++;
                sb.Append('?').Append(n);
                continue;
            }
            sb.Append(c);
        }
        return (sb.ToString(), n);
    });

    public static SqliteCommand Command(SqliteConnection conn, string sql, IReadOnlyList<object?>? args)
    {
        var (text, count) = Number(sql);
        var cmd = conn.CreateCommand();
        cmd.CommandText = text;
        int supplied = args?.Count ?? 0;
        if (supplied != count)
            throw new ArgumentException($"statement has {count} placeholder(s) but {supplied} value(s) were supplied");
        for (int i = 0; i < supplied; i++)
            cmd.Parameters.Add(new SqliteParameter("?" + (i + 1), Normalise(args![i])));
        return cmd;
    }

    static object Normalise(object? value) => value switch
    {
        null => DBNull.Value,
        bool b => b ? 1L : 0L,
        int i => (long)i,
        _ => value,
    };

    public static List<Row> Query(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Command(conn, sql, args);
        using var reader = cmd.ExecuteReader();
        return ReadAll(reader);
    }

    public static List<Row> QueryList(SqliteConnection conn, string sql, IReadOnlyList<object?> args)
    {
        using var cmd = Command(conn, sql, args);
        using var reader = cmd.ExecuteReader();
        return ReadAll(reader);
    }

    public static Row? One(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Command(conn, sql, args);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadRow(reader, Names(reader));
    }

    public static object? Scalar(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Command(conn, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static int Exec(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Command(conn, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static int ExecList(SqliteConnection conn, string sql, IReadOnlyList<object?> args)
    {
        using var cmd = Command(conn, sql, args);
        return cmd.ExecuteNonQuery();
    }

    /// <summary><c>executescript</c>: several statements, no parameters.</summary>
    public static void Script(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static List<string> Names(SqliteDataReader reader)
    {
        var names = new List<string>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));
        return names;
    }

    static Row ReadRow(SqliteDataReader reader, List<string> names)
    {
        var values = new List<object?>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            if (reader.IsDBNull(i)) { values.Add(null); continue; }
            object v = reader.GetValue(i);
            values.Add(v switch
            {
                int n => (long)n,
                _ => v,
            });
        }
        return new Row(new List<string>(names), values);
    }

    public static List<Row> ReadAll(SqliteDataReader reader)
    {
        var names = Names(reader);
        var rows = new List<Row>();
        while (reader.Read()) rows.Add(ReadRow(reader, names));
        return rows;
    }

    /// <summary><c>",".join("?" for _ in ids)</c>.</summary>
    public static string Marks(int count) => count == 0 ? "" : string.Join(",", Enumerable.Repeat("?", count));
}
