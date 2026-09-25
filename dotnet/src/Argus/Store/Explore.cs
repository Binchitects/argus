using System.Text.Json.Nodes;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// Read-only operator queries over the index, with NO access filtering
/// (argus/store/explore.py).
///
/// These live apart from <see cref="Queries"/> on purpose: the only caller is the
/// admin surface, gated by ARGUS_ADMIN_TOKEN, whose holder may see everything. A
/// test asserts no MCP tool code reaches this class.
/// </summary>
public static class Explore
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    static int Clamp(int? limit) => limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>A substring pattern with LIKE's own metacharacters escaped.</summary>
    static string Like(string value) =>
        "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static JsonArray Repos(SqliteConnection conn)
    {
        var arr = new JsonArray();
        foreach (var row in Sql.Query(conn,
                     "SELECT r.id AS repo_id, r.path_with_namespace, r.branch," +
                     "       r.default_branch, r.last_run_at, r.last_indexed_at," +
                     "       r.last_run_error," +
                     "       (SELECT COUNT(*) FROM files   f WHERE f.repo_id = r.id) AS files," +
                     "       (SELECT COUNT(*) FROM symbols s WHERE s.repo_id = r.id) AS symbols," +
                     "       (SELECT COUNT(*) FROM symbols s WHERE s.repo_id = r.id" +
                     "         AND s.is_public = 1) AS public_symbols" +
                     "  FROM repos r ORDER BY r.path_with_namespace, r.branch"))
            arr.Add(row.ToJson());
        return arr;
    }

    public static JsonObject Symbols(SqliteConnection conn, string pattern = "", string repo = "", int? limit = null)
    {
        int lim = Clamp(limit);
        var where = new List<string>();
        var args = new List<object?>();
        if (pattern.Length > 0) { where.Add("s.name LIKE ? ESCAPE '\\'"); args.Add(Like(pattern)); }
        if (repo.Length > 0) { where.Add("r.path_with_namespace = ?"); args.Add(repo); }
        var clause = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        args.Add((long)lim + 1);
        var rows = Sql.QueryList(conn,
            "SELECT s.name, s.kind, s.scope, s.signature, s.line, s.end_line," +
            "       s.is_public, s.doc, f.path, f.lang," +
            "       r.path_with_namespace, r.branch," +
            "       s.repo_id" +
            "  FROM symbols s" +
            "  JOIN files f ON f.id = s.file_id" +
            "  JOIN repos r ON r.id = s.repo_id" +
            clause +
            " ORDER BY s.name, r.path_with_namespace, f.path" +
            " LIMIT ?", args);
        return Page(rows, lim);
    }

    public static JsonObject Files(SqliteConnection conn, string pattern = "", string repo = "", int? limit = null)
    {
        int lim = Clamp(limit);
        var where = new List<string>();
        var args = new List<object?>();
        if (pattern.Length > 0) { where.Add("f.path LIKE ? ESCAPE '\\'"); args.Add(Like(pattern)); }
        if (repo.Length > 0) { where.Add("r.path_with_namespace = ?"); args.Add(repo); }
        var clause = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        args.Add((long)lim + 1);
        var rows = Sql.QueryList(conn,
            "SELECT f.path, f.lang, f.size, f.blob_sha, f.is_vendored," +
            "       r.path_with_namespace, r.branch, f.repo_id," +
            "       (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id) AS symbols" +
            "  FROM files f" +
            "  JOIN repos r ON r.id = f.repo_id" +
            clause +
            " ORDER BY r.path_with_namespace, f.path" +
            " LIMIT ?", args);
        return Page(rows, lim);
    }

    static JsonObject Page(List<Row> rows, int limit)
    {
        var arr = new JsonArray();
        foreach (var r in rows.Take(limit)) arr.Add(r.ToJson());
        return new JsonObject { ["rows"] = arr, ["capped"] = rows.Count > limit, ["limit"] = limit };
    }
}

/// <summary>Materialise the cross-repo dependency graph (argus/store/graph.py).</summary>
public static class Graph
{
    public static int RebuildRepoDeps(SqliteConnection conn)
    {
        var rows = Sql.Query(conn,
            "SELECT repo_id AS from_repo_id, resolved_repo_id AS to_repo_id," +
            "       COUNT(DISTINCT file_id) AS weight" +
            "  FROM includes" +
            " WHERE resolution = ?" +
            "   AND resolved_repo_id IS NOT NULL" +
            "   AND resolved_repo_id != repo_id" +
            " GROUP BY repo_id, resolved_repo_id", Indexing.Resolve.Resolution.Resolved);
        using var tx = conn.BeginTransaction();
        Sql.Exec(conn, "DELETE FROM repo_deps");
        foreach (var r in rows)
            Sql.Exec(conn, "INSERT INTO repo_deps (from_repo_id, to_repo_id, weight) VALUES (?, ?, ?)",
                r["from_repo_id"], r["to_repo_id"], r["weight"]);
        tx.Commit();
        return rows.Count;
    }
}
