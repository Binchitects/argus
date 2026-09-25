using System.Text.Json.Nodes;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// Every write the indexer and the server make, ported from argus/store/writes.py.
///
/// Each public method is one transaction, which is the unit the Python module
/// commits at. Python's sqlite3 opens an implicit transaction before the first
/// write and the method's own <c>commit()</c> ends it; an exception part-way
/// leaves the transaction open for the worker's <c>rollback()</c>. Here the
/// transaction is explicit and rolls back by itself, so a failure half-way
/// through <see cref="UpsertFile"/> can never leave the FTS index out of step
/// with <c>files</c>.
/// </summary>
public static class Writes
{
    public const int MaxRetryAttempts = 3;

    static void Atomic(SqliteConnection conn, Action body)
    {
        using var tx = conn.BeginTransaction();
        body();
        tx.Commit();
    }

    public static long UpsertRepo(SqliteConnection conn, long gitlabId, string pathWithNamespace,
        string defaultBranch, string httpUrl, string? branch = null)
    {
        branch = string.IsNullOrEmpty(branch) ? defaultBranch : branch;
        Atomic(conn, () => Sql.Exec(conn, """
            INSERT INTO repos (gitlab_id, path_with_namespace, default_branch,
                               branch, http_url)
            VALUES (?, ?, ?, ?, ?)
            ON CONFLICT(gitlab_id, branch) DO UPDATE SET
                path_with_namespace = excluded.path_with_namespace,
                default_branch      = excluded.default_branch,
                http_url            = excluded.http_url
            """, gitlabId, pathWithNamespace, defaultBranch, branch, httpUrl));
        return Convert.ToInt64(Sql.Scalar(conn, "SELECT id FROM repos WHERE gitlab_id = ? AND branch = ?", gitlabId, branch));
    }

    public static Row? GetAclCache(SqliteConnection conn, string tokenHash) =>
        Sql.One(conn, "SELECT user_id, username, repo_ids_json, fetched_at FROM acl_cache WHERE token_hash = ?", tokenHash);

    public static void UpsertAclCache(SqliteConnection conn, string tokenHash, long userId, string username,
        string repoIdsJson, long fetchedAt) =>
        Atomic(conn, () => Sql.Exec(conn, """
            INSERT INTO acl_cache (token_hash, user_id, username, repo_ids_json, fetched_at)
            VALUES (?, ?, ?, ?, ?)
            ON CONFLICT(token_hash) DO UPDATE SET
                user_id       = excluded.user_id,
                username      = excluded.username,
                repo_ids_json = excluded.repo_ids_json,
                fetched_at    = excluded.fetched_at
            """, tokenHash, userId, username, repoIdsJson, fetchedAt));

    public static void SetLastIndexed(SqliteConnection conn, long repoId, string sha, long ts) =>
        Atomic(conn, () => Sql.Exec(conn, "UPDATE repos SET last_indexed_sha = ?, last_indexed_at = ? WHERE id = ?", sha, ts, repoId));

    /// <summary>Record that this repo was checked, and how. <c>error</c> null clears a previous failure.</summary>
    public static void RecordRunState(SqliteConnection conn, long repoId, bool timedOut, bool symbolsFailed, long ts, string? error = null) =>
        Atomic(conn, () => Sql.Exec(conn,
            "UPDATE repos SET last_run_timed_out = ?, last_run_symbols_failed = ?," +
            "                 last_run_at = ?, last_run_error = ? WHERE id = ?",
            timedOut ? 1L : 0L, symbolsFailed ? 1L : 0L, ts, error, repoId));

    static void FtsDelete(SqliteConnection conn, Row row) =>
        Sql.Exec(conn, "INSERT INTO files_fts(files_fts, rowid, path, content) VALUES ('delete', ?, ?, ?)",
            row["id"], row["path"], row["content"]);

    public static long UpsertFile(SqliteConnection conn, long repoId, string path, string? lang, long size,
        string blobSha, string content)
    {
        long fileId = 0;
        Atomic(conn, () =>
        {
            var existing = Sql.One(conn, "SELECT id, path, content FROM files WHERE repo_id = ? AND path = ?", repoId, path);
            if (existing is not null)
            {
                FtsDelete(conn, existing);
                Sql.Exec(conn, "UPDATE files SET lang = ?, size = ?, blob_sha = ?, content = ? WHERE id = ?",
                    lang, size, blobSha, content, existing["id"]);
                fileId = existing.Long("id");
            }
            else
            {
                Sql.Exec(conn, "INSERT INTO files (repo_id, path, lang, size, blob_sha, content) VALUES (?, ?, ?, ?, ?, ?)",
                    repoId, path, lang, size, blobSha, content);
                fileId = Convert.ToInt64(Sql.Scalar(conn, "SELECT last_insert_rowid()"));
            }
            Sql.Exec(conn, "INSERT INTO files_fts(rowid, path, content) VALUES (?, ?, ?)", fileId, path, content);
        });
        return fileId;
    }

    public static void DeleteFile(SqliteConnection conn, long repoId, string path) =>
        Atomic(conn, () =>
        {
            var row = Sql.One(conn, "SELECT id, path, content FROM files WHERE repo_id = ? AND path = ?", repoId, path);
            if (row is null) return;
            FtsDelete(conn, row);
            Sql.Exec(conn, "DELETE FROM files WHERE id = ?", row["id"]);
        });

    /// <summary>Remove a repo and every dependent row, including its FTS entries (no cascade reaches those).</summary>
    public static void DeleteRepo(SqliteConnection conn, long repoId) =>
        Atomic(conn, () =>
        {
            foreach (var row in Sql.Query(conn, "SELECT id, path, content FROM files WHERE repo_id = ?", repoId))
                FtsDelete(conn, row);
            Sql.Exec(conn, "DELETE FROM repos WHERE id = ?", repoId);
        });

    public sealed record SymbolRow(
        string Name, string Kind, long Line, long? EndLine, string? Signature, string? Scope,
        long IsPublic, string? Doc, string? Language = null)
    {
        public string? Doc { get; set; } = Doc;
    }

    public static void ReplaceSymbols(SqliteConnection conn, long repoId, long fileId, IReadOnlyList<SymbolRow> symbols, string blobSha) =>
        Atomic(conn, () =>
        {
            Sql.Exec(conn, "DELETE FROM symbols WHERE file_id = ?", fileId);
            using var cmd = Sql.Command(conn,
                "INSERT INTO symbols" +
                " (repo_id, file_id, name, kind, line, end_line, signature, scope," +
                "  is_public, doc)" +
                " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                new object?[10]);
            foreach (var s in symbols)
            {
                object?[] values = [repoId, fileId, s.Name, s.Kind, s.Line, s.EndLine, s.Signature, s.Scope, s.IsPublic,
                    string.IsNullOrEmpty(s.Doc) ? null : s.Doc];
                for (int i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            // An empty symbol list is a successful extraction, not an incomplete one.
            Sql.Exec(conn, "UPDATE files SET symbols_sha = ? WHERE id = ?", blobSha, fileId);
        });

    /// <summary>Drop the symbol rows of these paths so they cannot look up to date.</summary>
    public static void ClearSymbolsForPaths(SqliteConnection conn, long repoId, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        Atomic(conn, () =>
        {
            foreach (var p in paths)
                Sql.Exec(conn, "DELETE FROM symbols WHERE file_id IN (SELECT id FROM files WHERE repo_id = ? AND path = ?)", repoId, p);
            foreach (var p in paths)
                Sql.Exec(conn, "UPDATE files SET symbols_sha = NULL WHERE repo_id = ? AND path = ?", repoId, p);
        });
    }

    public sealed record IncludeRow(string Raw, long IsAngle);

    public static void ReplaceIncludes(SqliteConnection conn, long repoId, long fileId, IReadOnlyList<IncludeRow> includes) =>
        Atomic(conn, () =>
        {
            Sql.Exec(conn, "DELETE FROM includes WHERE file_id = ?", fileId);
            foreach (var i in includes)
                Sql.Exec(conn, "INSERT INTO includes (repo_id, file_id, raw, is_angle) VALUES (?, ?, ?, ?)", repoId, fileId, i.Raw, i.IsAngle);
        });

    public static void RecordError(SqliteConnection conn, long repoId, string? path, string stage, string message, long ts) =>
        Atomic(conn, () => Sql.Exec(conn,
            "INSERT INTO index_errors (repo_id, path, stage, message, ts) VALUES (?, ?, ?, ?, ?)",
            repoId, path, stage, PyStr.Prefix(message, 2000), ts));

    /// <summary>Persist paths that failed this pass so the next pass retries them.</summary>
    public static void EnqueueRetry(SqliteConnection conn, long repoId, IReadOnlyCollection<string> paths, string reason, long ts)
    {
        if (paths.Count == 0) return;
        var sorted = paths.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var payload = PyJson.Dumps(new JsonObject
        {
            ["reason"] = reason,
            ["paths"] = new JsonArray(sorted.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        });
        Atomic(conn, () => Sql.Exec(conn, """
            INSERT INTO index_queue (repo_id, enqueued_at, reason)
            VALUES (?, ?, ?)
            ON CONFLICT(repo_id) DO UPDATE SET
                enqueued_at = excluded.enqueued_at,
                reason      = excluded.reason
            """, repoId, ts, payload));
    }

    /// <summary>Count one more failure per path; return the cumulative counts.</summary>
    public static Dictionary<string, long> BumpRetryAttempts(SqliteConnection conn, long repoId, IReadOnlyCollection<string> paths)
    {
        var result = new Dictionary<string, long>();
        if (paths.Count == 0) return result;
        Atomic(conn, () =>
        {
            foreach (var p in paths)
                Sql.Exec(conn,
                    "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, ?, 1)" +
                    " ON CONFLICT(repo_id, path) DO UPDATE SET attempts = attempts + 1", repoId, p);
        });
        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
        foreach (var row in Sql.Query(conn, "SELECT path, attempts FROM retry_attempts WHERE repo_id = ?", repoId))
            if (wanted.Contains(row.Str("path"))) result[row.Str("path")] = row.Long("attempts");
        return result;
    }

    public static void ClearRetryAttempts(SqliteConnection conn, long repoId, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        Atomic(conn, () =>
        {
            foreach (var p in paths)
                Sql.Exec(conn, "DELETE FROM retry_attempts WHERE repo_id = ? AND path = ?", repoId, p);
        });
    }

    /// <summary>The paths queued for retry for this repo, leaving the queue intact.</summary>
    public static List<string> PeekRetryPaths(SqliteConnection conn, long repoId)
    {
        var row = Sql.One(conn, "SELECT reason FROM index_queue WHERE repo_id = ?", repoId);
        if (row is null) return [];
        try
        {
            var payload = JsonNode.Parse(row.Str("reason"));
            if (payload is not JsonObject obj) return [];
            if (obj["paths"] is not JsonArray arr) return [];
            var paths = new List<string>();
            foreach (var p in arr)
                if (p is JsonValue v && v.TryGetValue<string>(out var s)) paths.Add(s);
            return paths;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static void ClearRetryQueue(SqliteConnection conn, long repoId) =>
        Atomic(conn, () => Sql.Exec(conn, "DELETE FROM index_queue WHERE repo_id = ?", repoId));

    public static List<string> DrainRetryPaths(SqliteConnection conn, long repoId)
    {
        var paths = PeekRetryPaths(conn, repoId);
        ClearRetryQueue(conn, repoId);
        return paths;
    }

    /// <summary>Append one audit row: one call attempt, one row. <paramref name="conn"/> is the audit sidecar.</summary>
    public static void RecordAudit(SqliteConnection conn, long ts, long? userId, string? username, string tool,
        string argsJson, string? repoIdsJson) =>
        Atomic(conn, () => Sql.Exec(conn,
            "INSERT INTO audit (ts, user_id, username, tool, args_json, repo_ids_json) VALUES (?, ?, ?, ?, ?, ?)",
            ts, userId, username, tool, argsJson, repoIdsJson));
}
