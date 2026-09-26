using System.Reflection;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// Opening the index.
///
/// Three modes, and the difference between them is load-bearing: the indexer
/// writes (WAL, foreign keys on), the server reads with <c>query_only</c> so no
/// request path can ever write index data, and the audit trail lives in a
/// sidecar file so a tool call's audit write never waits behind an indexing run.
/// </summary>
public static class Db
{
    static int _initialised;

    public static void Init()
    {
        if (Interlocked.Exchange(ref _initialised, 1) == 1) return;
        SQLitePCL.Batteries_V2.Init();
    }

    /// <summary>The migration scripts, ordered by their numeric prefix.</summary>
    public static IReadOnlyList<(int Version, string Sql)> Migrations { get; } = LoadMigrations();

    static List<(int, string)> LoadMigrations()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<(int, string)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("migrations/", StringComparison.Ordinal) || !name.EndsWith(".sql", StringComparison.Ordinal)) continue;
            var file = name["migrations/".Length..];
            var version = int.Parse(file.Split('_', 2)[0]);
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((version, reader.ReadToEnd()));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    public static SqliteConnection Connect(string dbPath)
    {
        Init();
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = null,
            DefaultTimeout = 5,
        }.ToString());
        conn.Open();
        Sql.Script(conn, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;");
        return conn;
    }

    public static int Migrate(SqliteConnection conn)
    {
        var current = Convert.ToInt32(Sql.Scalar(conn, "PRAGMA user_version"));
        foreach (var (version, sql) in Migrations)
        {
            if (version <= current) continue;
            Sql.Script(conn, sql);
            Sql.Script(conn, $"PRAGMA user_version = {version}");
            current = version;
        }
        return current;
    }

    public static SqliteConnection Open(string dbPath)
    {
        var conn = Connect(dbPath);
        Migrate(conn);
        return conn;
    }

    /// <summary>Open the index read-only. The server must never write index data.</summary>
    public static SqliteConnection ConnectReadonly(string dbPath)
    {
        Init();
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        conn.Open();
        Sql.Script(conn, "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;");
        return conn;
    }

    public const string AuditSuffix = "-audit.db";

    const string AuditSchema = """
        CREATE TABLE IF NOT EXISTS audit (
          id            INTEGER PRIMARY KEY,
          ts            INTEGER NOT NULL,
          user_id       INTEGER,
          username      TEXT,
          tool          TEXT    NOT NULL,
          args_json     TEXT    NOT NULL,
          repo_ids_json TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit(ts);
        """;

    /// <summary>Sidecar path for <paramref name="dbPath"/>: index.db -> index-audit.db.</summary>
    public static string AuditDbPath(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(dbPath);
        return Path.Combine(dir, stem + AuditSuffix);
    }

    public static SqliteConnection ConnectAudit(string dbPath)
    {
        var conn = Connect(AuditDbPath(dbPath));
        Sql.Script(conn, AuditSchema);
        return conn;
    }

    /// <summary>
    /// Run <paramref name="body"/> in one transaction: committed on success, rolled
    /// back on any exception. The Python module commits after each write; a
    /// transaction here makes a batch of those writes one fsync instead of many
    /// without changing what a reader can observe at any commit point.
    /// </summary>
    public static T InTransaction<T>(SqliteConnection conn, Func<T> body)
    {
        using var tx = conn.BeginTransaction();
        var result = body();
        tx.Commit();
        return result;
    }
}
