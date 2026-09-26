using System.Runtime.InteropServices;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Packs;

/// <summary>A pack's embeddings are not comparable with this server's.</summary>
public sealed class PackMismatch(string message) : Exception(message);

/// <summary>
/// The on-disk pack format: one SQLite file holding the
/// documents (zstd), their chunks, the API symbol index, an FTS5 table and two
/// sqlite-vec tables. Packs are opened immutable, so any number of readers can
/// share one file without locking.
/// </summary>
public static class PackFormat
{
    public const int PackSchemaVersion = 1;

    const string Schema = """
        CREATE TABLE pack_meta (
          key TEXT PRIMARY KEY, value TEXT NOT NULL
        );

        CREATE TABLE docs (
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE,
          title TEXT,
          url TEXT,
          lang TEXT,
          content BLOB NOT NULL,
          content_len INTEGER NOT NULL,
          content_sha TEXT
        );

        CREATE TABLE chunks (
          id INTEGER PRIMARY KEY,
          doc_id INTEGER NOT NULL REFERENCES docs(id),
          heading_path TEXT,
          anchor TEXT,
          start_line INTEGER,
          text BLOB NOT NULL
        );

        CREATE TABLE api_symbols (
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          kind TEXT,
          namespace TEXT,
          doc_id INTEGER REFERENCES docs(id),
          anchor TEXT,
          signature TEXT
        );
        CREATE INDEX idx_api_name ON api_symbols(name);

        CREATE VIRTUAL TABLE docs_fts USING fts5(
          title, body, content='', tokenize='unicode61 remove_diacritics 2'
        );
        """;

    static string CreateVecBin => $"CREATE VIRTUAL TABLE vec_bin USING vec0(chunk_id INTEGER PRIMARY KEY, embedding bit[{Quantize.Dim}])";
    static string CreateVecI8 => $"CREATE VIRTUAL TABLE vec_i8 USING vec0(chunk_id INTEGER PRIMARY KEY, embedding int8[{Quantize.Dim}])";

    static string? _vecPath;
    static readonly Lock VecGate = new();

    /// <summary>Where the sqlite-vec loadable extension lives, found once.</summary>
    public static string VecExtensionPath()
    {
        lock (VecGate)
        {
            if (_vecPath is not null) return _vecPath;
            var env = Environment.GetEnvironmentVariable("ARGUS_SQLITE_VEC_PATH");
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            var (file, rid) = NativeName();
            var bases = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) ?? "" };
            foreach (var b in bases.Where(b => b.Length > 0).Distinct())
            {
                candidates.Add(Path.Combine(b, "runtimes", rid, "native", file));
                candidates.Add(Path.Combine(b, file));
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return _vecPath = c;
            throw new InvalidOperationException(
                $"sqlite-vec ({file}) was not found; looked in: {string.Join(", ", candidates)}. " +
                "Run tools/fetch-sqlite-vec.sh, or set ARGUS_SQLITE_VEC_PATH.");
        }
    }

    static (string File, string Rid) NativeName()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        if (OperatingSystem.IsWindows()) return ("vec0.dll", $"win-{arch}");
        if (OperatingSystem.IsMacOS()) return ("vec0.dylib", $"osx-{arch}");
        return ("vec0.so", $"linux-{arch}");
    }

    public static void LoadVecExtension(SqliteConnection conn)
    {
        conn.EnableExtensions(true);
        try
        {
            var path = VecExtensionPath();
            // Pass the path without its suffix: SQLite appends the platform's own.
            var noExt = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
            conn.LoadExtension(noExt, "sqlite3_vec_init");
        }
        finally
        {
            conn.EnableExtensions(false);
        }
    }

    public static SqliteConnection CreatePack(string path)
    {
        Db.Init();
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        LoadVecExtension(conn);
        Sql.Script(conn, Schema);
        Sql.Exec(conn, CreateVecBin);
        Sql.Exec(conn, CreateVecI8);
        Sql.Exec(conn, "INSERT INTO pack_meta (key, value) VALUES (?, ?)", "pack_schema_version", PackSchemaVersion.ToString());
        return conn;
    }

    /// <summary>Open a pack read-only and immutable, with sqlite-vec loaded.</summary>
    public static SqliteConnection OpenPack(string path)
    {
        Db.Init();
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new SqliteException($"unable to open database file: {path}", 14);
        var uri = "file:" + Uri.EscapeDataString(full.Replace('\\', '/')).Replace("%2F", "/").Replace("%3A", ":") + "?mode=ro&immutable=1";
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Pooling = false }.ToString());
        conn.Open();
        LoadVecExtension(conn);
        return conn;
    }

    public static SqliteConnection OpenPackWritable(string path)
    {
        Db.Init();
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        LoadVecExtension(conn);
        return conn;
    }

    public static void WriteMeta(SqliteConnection conn, IEnumerable<(string Key, object? Value)> kv)
    {
        using var tx = conn.BeginTransaction();
        foreach (var (k, v) in kv)
            Sql.Exec(conn,
                "INSERT INTO pack_meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                k, v switch { null => "None", bool b => b ? "True" : "False", _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) });
        tx.Commit();
    }

    public static Dictionary<string, string> ReadMeta(SqliteConnection conn)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Sql.Query(conn, "SELECT key, value FROM pack_meta")) meta[row.Str("key")] = row.Str("value");
        return meta;
    }

    public static void RequireCompatible(IReadOnlyDictionary<string, string> meta, string model, int dim)
    {
        meta.TryGetValue("embedding_model", out var packModel);
        if (packModel != model)
            throw new PackMismatch(
                $"pack was built with embedding model {PyStr.Repr(packModel)}, but this instance serves {PyStr.Repr(model)} -- semantic search is disabled for this pack");
        if (!meta.TryGetValue("embedding_dim", out var packDim) || !int.TryParse(packDim, out var d) || d != dim)
            throw new PackMismatch(
                $"pack embeddings are {PyStr.Repr(packDim)}-dimensional, but this instance expects {dim} -- semantic search is disabled for this pack");
        if (!meta.TryGetValue("pack_schema_version", out var schema) || !int.TryParse(schema, out var s) || s != PackSchemaVersion)
            throw new PackMismatch(
                $"pack schema version {PyStr.Repr(schema)} is not compatible with this instance's pack format version {PackSchemaVersion} -- the pack must be rebuilt");
    }
}
