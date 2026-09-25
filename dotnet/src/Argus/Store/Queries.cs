using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.Indexing;
using Argus.Packs;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>
/// A query could not be satisfied as given (e.g. bad FTS5 syntax). The message is
/// prompt text, written to be actionable, and is surfaced verbatim.
/// </summary>
public class QueryError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Every read an MCP tool makes of the private index (argus/store/queries.py).
///
/// Every public query takes the caller's allowlist as its FIRST argument. That is
/// the access-control mechanism, not a convention: there is no way to call one of
/// these without saying whose repositories may be read. An empty allowlist means
/// "return nothing", never "skip the filter".
/// </summary>
public static class Queries
{
    /// <summary>Conservative host-parameter ceiling: under SQLite's documented default of 999.</summary>
    public const int SqliteMaxVars = 900;

    static List<long> Ids(IReadOnlyList<long> allowed) => allowed.ToList();

    /// <summary>Split the allowlist so no statement exceeds SQLite's parameter limit.</summary>
    public static List<List<long>> Chunks(IReadOnlyList<long> ids, int reserve)
    {
        int size = Math.Max(1, SqliteMaxVars - reserve);
        var output = new List<List<long>>();
        for (int i = 0; i < ids.Count; i += size) output.Add(ids.Skip(i).Take(size).ToList());
        if (output.Count == 0) output.Add([]);
        return output;
    }

    static object?[] Args(IEnumerable<long> ids, params object?[] tail) => ids.Cast<object?>().Concat(tail).ToArray();
    static object?[] Args(params object?[] head) => head;

    /// <summary>The raw SQLite message inside a SqliteException, as Python's sqlite3 reports it.</summary>
    public static string SqliteMessage(SqliteException exc)
    {
        var m = Regex.Match(exc.Message, @"^SQLite Error \d+: '(.*)'\.?$", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : exc.Message;
    }

    // --- symbol contracts -------------------------------------------------------

    static readonly Regex SourceIdentRe = new(@"\b[A-Za-z_][A-Za-z0-9_]{4,}\b", RegexOptions.CultureInvariant);

    static readonly HashSet<string> IdentNoise = new(PyStr.SplitWhitespace("""
        break case catch class const continue default delete double
        else enum extern false final float friend inline namespace operator private
        protected public register return short signed sizeof static struct switch
        template throw true typedef typename union unsigned using virtual void
        volatile while alignas constexpr decltype explicit mutable noexcept nullptr
        printf memcpy memset malloc free strlen strcpy sprintf assert include define
        ifdef ifndef endif pragma
        """), StringComparer.Ordinal);

    /// <summary>Where every in-house symbol a file names is actually defined.</summary>
    public static List<JsonObject> SymbolContracts(IReadOnlyList<long> allowed, SqliteConnection conn, string source, int limit = 40)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Match m in SourceIdentRe.Matches(source ?? ""))
        {
            var name = m.Value;
            if (IdentNoise.Contains(name.ToLowerInvariant())) continue;
            if (counts.TryGetValue(name, out var c)) counts[name] = c + 1;
            else { counts[name] = 1; order.Add(name); }
        }
        if (counts.Count == 0) return [];

        var output = new List<JsonObject>();
        foreach (var name in counts.Keys.OrderBy(n => -counts[n]).ThenBy(n => n, StringComparer.Ordinal))
        {
            var rows = FindSymbol(allowed, conn, name, limit: 1);
            if (rows.Count == 0) continue;
            var row = rows[0];
            output.Add(new JsonObject
            {
                ["name"] = PyJson.From(row["name"]),
                ["kind"] = PyJson.From(row["kind"]),
                ["repo"] = PyJson.From(row["path_with_namespace"]),
                ["repo_id"] = PyJson.From(row["repo_id"]),
                ["path"] = PyJson.From(row["path"]),
                ["line"] = PyJson.From(row["line"]),
                ["signature"] = PyJson.From(row["signature"]),
                ["scope"] = PyJson.From(row["scope"]),
                ["doc"] = PyJson.From(row["doc"]),
                ["is_public"] = row.Long("is_public") != 0,
                ["mentions"] = counts[name],
            });
            if (output.Count >= limit) break;
        }
        return output;
    }

    // --- lookups ----------------------------------------------------------------

    public static List<Row> FindSymbol(IReadOnlyList<long> allowed, SqliteConnection conn, string name, string? kind = null, int limit = 50)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        int reserve = kind is not null ? 2 : 1;
        var rows = new List<Row>();
        foreach (var chunk in Chunks(ids, reserve))
        {
            var sql =
                "SELECT s.repo_id, r.path_with_namespace, f.path, s.name, s.kind," +
                "       s.line, s.end_line, s.signature, s.scope, s.is_public, s.doc" +
                "  FROM symbols s" +
                "  JOIN files f ON f.id = s.file_id" +
                "  JOIN repos r ON r.id = s.repo_id" +
                $" WHERE s.repo_id IN ({Sql.Marks(chunk.Count)}) AND s.name = ?";
            var args = Args(chunk, name).ToList();
            if (kind is not null) { sql += " AND s.kind = ?"; args.Add(kind); }
            rows.AddRange(Sql.QueryList(conn, sql, args));
        }
        return rows
            .OrderBy(r => -r.Long("is_public"))
            .ThenBy(r => r.Str("path_with_namespace"), StringComparer.Ordinal)
            .ThenBy(r => r.Str("path"), StringComparer.Ordinal)
            .Take(limit).ToList();
    }

    public static List<Row> SearchCode(IReadOnlyList<long> allowed, SqliteConnection conn, string query, int limit = 50)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        var rows = new List<Row>();
        foreach (var chunk in Chunks(ids, 1))
        {
            try
            {
                rows.AddRange(Sql.QueryList(conn,
                    "SELECT f.repo_id, r.path_with_namespace, f.path," +
                    "       files_fts.rank AS rank," +
                    "       snippet(files_fts, 1, '[', ']', '…', 16) AS snippet" +
                    "  FROM files_fts" +
                    "  JOIN files f ON f.id = files_fts.rowid" +
                    "  JOIN repos r ON r.id = f.repo_id" +
                    $" WHERE files_fts MATCH ? AND f.repo_id IN ({Sql.Marks(chunk.Count)})",
                    Args(new object?[] { query }.Concat(chunk.Cast<object?>()).ToArray())));
            }
            catch (SqliteException exc)
            {
                throw new QueryError(
                    $"That search syntax is not valid ({SqliteMessage(exc)}). Try plain terms " +
                    "without quotes or operators, e.g. DecodeFrame, or use " +
                    "regex=True.", exc);
            }
        }
        return rows.OrderBy(r => r.Double("rank")).Take(limit).ToList();
    }

    public static JsonObject? GetFile(IReadOnlyList<long> allowed, SqliteConnection conn, long repoId, string path, int maxBytes = 65536)
    {
        var ids = Ids(allowed);
        // A point lookup: the membership test runs BEFORE the query, so a row from
        // a disallowed repo is never fetched (see queries.py).
        if (!ids.Contains(repoId)) return null;
        var row = Sql.One(conn,
            "SELECT f.repo_id, r.path_with_namespace, f.path, f.lang, f.size, f.content" +
            "  FROM files f JOIN repos r ON r.id = f.repo_id" +
            " WHERE f.repo_id = ? AND f.path = ?", repoId, path);
        if (row is null) return null;
        var content = row.Str("content");
        bool truncated = PyStr.Len(content) > maxBytes;
        if (truncated) content = PyStr.Prefix(content, maxBytes);
        return new JsonObject
        {
            ["repo_id"] = PyJson.From(row["repo_id"]),
            ["path_with_namespace"] = PyJson.From(row["path_with_namespace"]),
            ["path"] = PyJson.From(row["path"]),
            ["lang"] = PyJson.From(row["lang"]),
            ["size"] = PyJson.From(row["size"]),
            ["content"] = content,
            ["truncated"] = truncated,
        };
    }

    public static List<Row> IndexStatus(IReadOnlyList<long> allowed, SqliteConnection conn)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        var rows = new List<Row>();
        foreach (var chunk in Chunks(ids, 0))
            rows.AddRange(Sql.QueryList(conn,
                "SELECT r.id AS repo_id, r.path_with_namespace," +
                "       r.branch, r.default_branch, r.last_indexed_sha," +
                "       r.last_indexed_at, r.last_run_timed_out, r.last_run_symbols_failed," +
                "       r.last_run_at, r.last_run_error," +
                "       (SELECT COUNT(*) FROM files   WHERE repo_id = r.id) AS files," +
                "       (SELECT COUNT(*) FROM symbols WHERE repo_id = r.id) AS symbols," +
                "       (SELECT COUNT(*) FROM index_errors WHERE repo_id = r.id) AS errors," +
                "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id" +
                "         AND resolution = 'resolved')  AS includes_resolved," +
                "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id" +
                "         AND resolution = 'external')  AS includes_external," +
                "       (SELECT COUNT(*) FROM includes WHERE repo_id = r.id" +
                "         AND resolution = 'ambiguous') AS includes_ambiguous," +
                "       COALESCE((SELECT CASE WHEN json_valid(reason)" +
                "                             THEN json_array_length(" +
                "                                      json_extract(reason, '$.paths'))" +
                "                        END" +
                "                   FROM index_queue WHERE repo_id = r.id), 0)" +
                "         AS queued_retries" +
                "  FROM repos r" +
                $" WHERE r.id IN ({Sql.Marks(chunk.Count)})",
                chunk.Cast<object?>().ToArray()));
        return rows.OrderBy(r => r.Str("path_with_namespace"), StringComparer.Ordinal).ToList();
    }

    /// <summary>Lexical occurrences of <paramref name="name"/>, flagging the ones ctags knows as definitions.</summary>
    public static List<JsonObject> FindReferences(IReadOnlyList<long> allowed, SqliteConnection conn, string name, int limit = 100)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        var pattern = new Regex(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);
        var ftsQuery = "\"" + name.Replace("\"", "\"\"") + "\"";

        var results = new List<(string Repo, string Path, long Line, JsonObject Row)>();
        foreach (var chunk in Chunks(ids, 1))
        {
            List<Row> fileRows;
            try
            {
                fileRows = Sql.QueryList(conn,
                    "SELECT f.id AS file_id, r.path_with_namespace AS repo," +
                    "       f.path, f.content" +
                    "  FROM files_fts" +
                    "  JOIN files f ON f.id = files_fts.rowid" +
                    "  JOIN repos r ON r.id = f.repo_id" +
                    $" WHERE files_fts MATCH ? AND f.repo_id IN ({Sql.Marks(chunk.Count)})",
                    new object?[] { ftsQuery }.Concat(chunk.Cast<object?>()).ToArray());
            }
            catch (SqliteException)
            {
                fileRows = [];
            }

            foreach (var frow in fileRows)
            {
                var defLines = new HashSet<long>(
                    Sql.Query(conn, "SELECT line FROM symbols WHERE file_id = ? AND name = ?", frow["file_id"], name)
                        .Select(r => r.Long("line")));
                var lines = PyStr.SplitLines(frow.Str("content"));
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!pattern.IsMatch(lines[i])) continue;
                    long lineno = i + 1;
                    results.Add((frow.Str("repo"), frow.Str("path"), lineno, new JsonObject
                    {
                        ["repo"] = frow.Str("repo"),
                        ["path"] = frow.Str("path"),
                        ["line"] = lineno,
                        ["context"] = lines[i],
                        ["is_definition"] = defLines.Contains(lineno),
                    }));
                }
            }
        }
        return results
            .OrderBy(r => r.Repo, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .Take(limit).Select(r => r.Row).ToList();
    }

    /// <summary>Dependencies and dependents of <paramref name="repoId"/>, filtered to the allowlist.</summary>
    public static JsonObject RepoMap(IReadOnlyList<long> allowed, SqliteConnection conn, long repoId)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0 || !ids.Contains(repoId)) return [];
        var row = Sql.One(conn, "SELECT id, path_with_namespace FROM repos WHERE id = ?", repoId);
        if (row is null) return [];

        JsonArray Edges(string sqlTemplate)
        {
            var output = new List<(long Other, string Path, long Weight)>();
            foreach (var chunk in Chunks(ids, 1))
            {
                var sql = sqlTemplate.Replace("{marks}", Sql.Marks(chunk.Count));
                foreach (var r in Sql.QueryList(conn, sql, new object?[] { repoId }.Concat(chunk.Cast<object?>()).ToArray()))
                    output.Add((r.Long("other_id"), r.Str("path_with_namespace"), r.Long("weight")));
            }
            var arr = new JsonArray();
            foreach (var e in output.OrderBy(e => -e.Weight).ThenBy(e => e.Path, StringComparer.Ordinal))
                arr.Add(new JsonObject { ["repo_id"] = e.Other, ["path_with_namespace"] = e.Path, ["weight"] = e.Weight });
            return arr;
        }

        return new JsonObject
        {
            ["repo"] = new JsonObject { ["repo_id"] = row.Long("id"), ["path_with_namespace"] = row.Str("path_with_namespace") },
            ["depends_on"] = Edges(
                "SELECT d.to_repo_id AS other_id, r.path_with_namespace, d.weight" +
                "  FROM repo_deps d JOIN repos r ON r.id = d.to_repo_id" +
                " WHERE d.from_repo_id = ? AND d.to_repo_id IN ({marks})"),
            ["depended_on_by"] = Edges(
                "SELECT d.from_repo_id AS other_id, r.path_with_namespace, d.weight" +
                "  FROM repo_deps d JOIN repos r ON r.id = d.from_repo_id" +
                " WHERE d.to_repo_id = ? AND d.from_repo_id IN ({marks})"),
        };
    }

    // --- which_repo ---------------------------------------------------------------

    static readonly Dictionary<string, (double Direct, double Lexical, double Central)> Weights = new()
    {
        [Indexing.WhichRepo.Shape.Diff] = (1.0, 0.0, 0.0),
        [Indexing.WhichRepo.Shape.Stack] = (1.0, 0.1, 0.0),
        [Indexing.WhichRepo.Shape.Symbol] = (1.0, 0.2, 0.0),
        [Indexing.WhichRepo.Shape.Prose] = (0.5, 1.0, 0.3),
    };

    const double FloorRatio = 0.35;
    const double VendoredEvidenceWeight = 0.2;
    const int LexicalSmoothing = 10;

    /// <summary>Rank repos a change probably belongs in, with the evidence for each.</summary>
    public static List<JsonObject> WhichRepo(IReadOnlyList<long> allowedIds, SqliteConnection conn, string description, int limit = 5)
    {
        var ids = Ids(allowedIds);
        if (ids.Count == 0 || PyStr.Strip(description).Length == 0) return [];

        // Python iterates a set here; the final ordering is fully determined by
        // the sort below, so the iteration order does not reach the result.
        var allowed = new HashSet<long>(ids);
        var allowedList = allowed.ToList();
        var shape = Indexing.WhichRepo.DetectShape(description);
        var weights = Weights[shape];

        var direct = new Dictionary<long, List<string>>();
        var directWeight = new Dictionary<long, double>();
        var lexical = new Dictionary<long, double>();
        var (repoNamesById, repoNames) = RepoBasenames(conn, allowedList);

        void Record(long repoId, string path, string descriptionText, long storedVendored)
        {
            bool vendored = storedVendored != 0 ||
                Resolve.IsVendoredCopy(path, repoNamesById.GetValueOrDefault(repoId, ""), repoNames);
            if (!direct.TryGetValue(repoId, out var list)) direct[repoId] = list = [];
            list.Add(vendored ? $"{descriptionText} (vendored copy)" : descriptionText);
            directWeight[repoId] = directWeight.GetValueOrDefault(repoId, 0.0) + (vendored ? VendoredEvidenceWeight : 1.0);
        }

        foreach (var path in Indexing.WhichRepo.ExtractPaths(description))
            foreach (var row in FilesNamed(conn, allowedList, path))
                Record(row.Long("repo_id"), row.Str("path"), $"file {row.Str("path")}", row.Long("is_vendored"));

        foreach (var name in Indexing.WhichRepo.ExtractSymbols(description).Take(10))
            foreach (var row in FindSymbol(allowedList, conn, name, limit: 20))
                Record(row.Long("repo_id"), row.Str("path"),
                    $"{row.Str("kind")} {row.Str("name")} at {row.Str("path")}:{row.Str("line")}",
                    VendoredFlag(conn, row.Long("repo_id"), row.Str("path")));

        if (weights.Lexical != 0)
        {
            try
            {
                foreach (var (repoId, n) in LexicalMatchCounts(conn, allowedList, description))
                    lexical[repoId] = n;
            }
            catch (QueryError)
            {
                // Degrade to "no lexical evidence": direct hits are unaffected.
            }
        }

        if (direct.Count == 0 && lexical.Count == 0) return [];

        var fileCounts = lexical.Count > 0 ? RepoFileCounts(conn, allowedList) : new Dictionary<long, long>();
        var densities = lexical.ToDictionary(kv => kv.Key, kv => kv.Value / (fileCounts.GetValueOrDefault(kv.Key, 0) + LexicalSmoothing));

        double bestLex = densities.Count > 0 ? densities.Values.Max() : 0.0;
        if (bestLex == 0) bestLex = 1.0;
        var centrality = weights.Central != 0 ? InDegree(conn, allowedList) : new Dictionary<long, long>();
        long maxCentral = centrality.Count > 0 ? centrality.Values.Max() : 0;
        if (maxCentral == 0) maxCentral = 1;

        var scored = new List<(double Score, string Name, JsonObject Row)>();
        foreach (var repoId in allowedList)
        {
            var hits = direct.GetValueOrDefault(repoId) ?? [];
            double lex = densities.GetValueOrDefault(repoId, 0.0) / bestLex;
            if (hits.Count == 0 && lex < FloorRatio) continue;

            double strength = Math.Min(directWeight.GetValueOrDefault(repoId, 0.0), 5.0) / 5.0;
            double score = weights.Direct * strength + weights.Lexical * lex;
            if (hits.Count == 0)
                score -= weights.Central * ((double)centrality.GetValueOrDefault(repoId, 0) / maxCentral);
            if (score <= 0) continue;

            var why = new JsonArray();
            if (hits.Count > 0) foreach (var h in hits.Take(5)) why.Add(h);
            else why.Add($"lexical match on {Math.Round(lexical.GetValueOrDefault(repoId, 0), MidpointRounding.ToEven):0} file(s)");
            var repoName = RepoName(conn, repoId);
            scored.Add((score, repoName, new JsonObject
            {
                ["repo_id"] = repoId,
                ["path_with_namespace"] = repoName,
                ["confidence"] = Math.Round(Math.Min(score, 1.0), 3, MidpointRounding.ToEven),
                ["shape"] = shape,
                ["why"] = why,
            }));
        }
        return scored.OrderBy(s => -s.Score).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(limit).Select(s => s.Row).ToList();
    }

    static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("_", "\\_").Replace("%", "\\%");

    static List<Row> FilesNamed(SqliteConnection conn, List<long> allowed, string path)
    {
        var rows = new List<Row>();
        var escaped = EscapeLike(path);
        var leaf = PyStr.AfterLast(path, '/');
        foreach (var chunk in Chunks(allowed, 3))
            rows.AddRange(Sql.QueryList(conn,
                $"SELECT repo_id, path, is_vendored FROM files WHERE repo_id IN ({Sql.Marks(chunk.Count)})" +
                "  AND basename = ?" +
                "  AND (path = ? OR path LIKE '%/' || ? ESCAPE '\\')",
                Args(chunk, leaf, path, escaped)));
        return rows;
    }

    static long VendoredFlag(SqliteConnection conn, long repoId, string path)
    {
        var row = Sql.One(conn, "SELECT is_vendored FROM files WHERE repo_id = ? AND path = ?", repoId, path);
        return row?.Long("is_vendored") ?? 0;
    }

    static (Dictionary<long, string>, HashSet<string>) RepoBasenames(SqliteConnection conn, List<long> allowed)
    {
        var byId = new Dictionary<long, string>();
        foreach (var chunk in Chunks(allowed, 0))
            foreach (var row in Sql.QueryList(conn, $"SELECT id, path_with_namespace FROM repos WHERE id IN ({Sql.Marks(chunk.Count)})",
                         chunk.Cast<object?>().ToArray()))
                byId[row.Long("id")] = PyStr.AfterLast(row.Str("path_with_namespace"), '/');
        return (byId, new HashSet<string>(byId.Values, StringComparer.Ordinal));
    }

    static Dictionary<long, long> RepoFileCounts(SqliteConnection conn, List<long> allowed)
    {
        var counts = new Dictionary<long, long>();
        foreach (var chunk in Chunks(allowed, 0))
            foreach (var row in Sql.QueryList(conn,
                         $"SELECT repo_id, COUNT(*) AS n FROM files WHERE repo_id IN ({Sql.Marks(chunk.Count)}) GROUP BY repo_id",
                         chunk.Cast<object?>().ToArray()))
                counts[row.Long("repo_id")] = row.Long("n");
        return counts;
    }

    /// <summary>Per-repo count of files matching the query, restricted to the allowlist (no bm25).</summary>
    static Dictionary<long, long> LexicalMatchCounts(SqliteConnection conn, List<long> allowed, string query)
    {
        var counts = new Dictionary<long, long>();
        if (allowed.Count == 0) return counts;
        foreach (var chunk in Chunks(allowed, 1))
        {
            List<Row> rows;
            try
            {
                rows = Sql.QueryList(conn,
                    "SELECT f.repo_id AS repo_id, COUNT(*) AS n" +
                    "  FROM files_fts" +
                    "  JOIN files f ON f.id = files_fts.rowid" +
                    $" WHERE files_fts MATCH ? AND f.repo_id IN ({Sql.Marks(chunk.Count)})" +
                    " GROUP BY f.repo_id",
                    new object?[] { query }.Concat(chunk.Cast<object?>()).ToArray());
            }
            catch (SqliteException exc)
            {
                throw new QueryError(
                    $"That search syntax is not valid ({SqliteMessage(exc)}). Try plain terms " +
                    "without quotes or operators, e.g. DecodeFrame, or use " +
                    "regex=True.", exc);
            }
            foreach (var row in rows)
                counts[row.Long("repo_id")] = counts.GetValueOrDefault(row.Long("repo_id"), 0) + row.Long("n");
        }
        return counts;
    }

    /// <summary>In-degree counting only edges whose BOTH endpoints are in the allowlist.</summary>
    static Dictionary<long, long> InDegree(SqliteConnection conn, List<long> allowed)
    {
        var counts = new Dictionary<long, long>();
        if (allowed.Count == 0) return counts;
        var chunks = Chunks(allowed, SqliteMaxVars / 2);
        foreach (var from in chunks)
            foreach (var to in chunks)
                foreach (var row in Sql.QueryList(conn,
                             "SELECT to_repo_id, COUNT(*) AS n FROM repo_deps" +
                             $" WHERE from_repo_id IN ({Sql.Marks(from.Count)})" +
                             $"   AND to_repo_id IN ({Sql.Marks(to.Count)})" +
                             " GROUP BY to_repo_id",
                             from.Concat(to).Cast<object?>().ToArray()))
                    counts[row.Long("to_repo_id")] = counts.GetValueOrDefault(row.Long("to_repo_id"), 0) + row.Long("n");
        return counts;
    }

    static string RepoName(SqliteConnection conn, long repoId) =>
        Sql.One(conn, "SELECT path_with_namespace FROM repos WHERE id = ?", repoId)?.Str("path_with_namespace") ?? "";

    // --- impact_of ----------------------------------------------------------------

    public const int DefaultImpactDepth = 3;
    public const int DefaultImpactLimit = 200;

    /// <summary>Files affected by changing one file, transitively, traversing only allowed repos.</summary>
    public static JsonObject ImpactOf(IReadOnlyList<long> allowed, SqliteConnection conn, long repoId, string path,
        long maxDepth = DefaultImpactDepth, int limit = DefaultImpactLimit)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0 || !ids.Contains(repoId) || string.IsNullOrEmpty(path)) return [];
        var target = Sql.One(conn, "SELECT id, path FROM files WHERE repo_id = ? AND path = ?", repoId, path);
        if (target is null) return [];
        long depth = Math.Max(1, Math.Min(maxDepth, 10));

        var rows = Sql.Query(conn, """
            WITH RECURSIVE
            allowed(repo_id) AS (SELECT value FROM json_each(?)),
            reached(file_id, depth) AS (
                SELECT i.file_id, 1
                  FROM includes i
                  JOIN allowed a ON a.repo_id = i.repo_id
                 WHERE i.resolved_file_id = ? AND i.resolution = 'resolved'
                UNION
                SELECT i.file_id, r.depth + 1
                  FROM includes i
                  JOIN reached r ON i.resolved_file_id = r.file_id
                  JOIN allowed a ON a.repo_id = i.repo_id
                 WHERE i.resolution = 'resolved' AND r.depth < ?
            )
            SELECT f.path, p.path_with_namespace, p.id AS repo_id, MIN(r.depth) AS depth
              FROM reached r
              JOIN files f ON f.id = r.file_id
              JOIN repos p ON p.id = f.repo_id
             GROUP BY r.file_id
             ORDER BY depth, p.path_with_namespace, f.path
             LIMIT ?
            """, "[" + string.Join(", ", ids) + "]", target["id"], depth, (long)limit + 1);

        bool truncated = rows.Count > limit;
        rows = rows.Take(limit).ToList();

        var byRepo = new JsonObject();
        foreach (var row in rows)
        {
            var key = row.Str("path_with_namespace");
            if (byRepo[key] is not JsonArray arr) byRepo[key] = arr = [];
            arr.Add(new JsonObject { ["path"] = row.Str("path"), ["depth"] = row.Long("depth") });
        }
        return new JsonObject
        {
            ["file"] = new JsonObject { ["repo_id"] = repoId, ["path"] = target.Str("path") },
            ["affected_files"] = rows.Count,
            ["affected_repos"] = byRepo.Count,
            ["max_depth"] = depth,
            ["truncated"] = truncated,
            ["by_repo"] = byRepo,
        };
    }

    // --- branches -----------------------------------------------------------------

    /// <summary>Narrow an allowlist to one branch per project (null: each default branch).</summary>
    public static List<long> ScopeToBranch(IReadOnlyList<long> allowed, SqliteConnection conn, string? branch = null)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        var output = new List<long>();
        foreach (var chunk in Chunks(ids, 1))
        {
            var rows = branch is null
                ? Sql.QueryList(conn, $"SELECT id FROM repos WHERE id IN ({Sql.Marks(chunk.Count)})  AND branch = default_branch",
                    chunk.Cast<object?>().ToArray())
                : Sql.QueryList(conn, $"SELECT id FROM repos WHERE id IN ({Sql.Marks(chunk.Count)}) AND branch = ?",
                    Args(chunk, branch));
            output.AddRange(rows.Select(r => Convert.ToInt64(r[0])));
        }
        return output;
    }

    /// <summary>Branch names the caller may ask for, default first. An error-message helper.</summary>
    public static List<string> BranchesAvailable(IReadOnlyList<long> allowed, SqliteConnection conn)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var chunk in Chunks(ids, 0))
            foreach (var row in Sql.QueryList(conn,
                         $"SELECT branch, (branch = default_branch) AS is_default  FROM repos WHERE id IN ({Sql.Marks(chunk.Count)})",
                         chunk.Cast<object?>().ToArray()))
                seen[row.Str("branch")] = Math.Max(seen.GetValueOrDefault(row.Str("branch"), 0), row.Long("is_default"));
        return seen.Keys.OrderBy(b => -seen[b]).ThenBy(b => b, StringComparer.Ordinal).ToList();
    }

    // --- semantic search ----------------------------------------------------------

    public const int SemanticCoarse = 600;

    /// <summary>Find symbols by MEANING, restricted to repos the caller may see.</summary>
    public static List<JsonObject> SemanticSearch(IReadOnlyList<long> allowed, SqliteConnection conn, IReadOnlyList<double> queryVec,
        int limit = 10, int coarse = SemanticCoarse)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];

        if (PgVector.Enabled())
        {
            try
            {
                var rankedPg = PgVector.Search(queryVec, ids, Math.Max(limit, 1), Math.Max(coarse, 1));
                return HydrateSemantic(conn, ids, rankedPg.ToDictionary(p => p.Id, p => p.Score), rankedPg.Select(p => p.Id).ToList());
            }
            catch (Exception exc)
            {
                Console.Error.WriteLine($"pgvector search failed; falling back to sqlite-vec: {exc.Message}");
            }
        }

        var candidates = Sql.Query(conn,
            "SELECT symbol_id FROM vec_symbols_bin WHERE embedding MATCH vec_bit(?) AND k = ?",
            Quantize.ToBits(queryVec), (long)Math.Max(coarse, 1));
        if (candidates.Count == 0) return [];

        var symbolIds = candidates.Select(r => Convert.ToInt64(r[0])).ToList();
        var allowedVectors = new List<(long, byte[])>();
        foreach (var chunk in Chunks(symbolIds, 0))
            foreach (var row in Sql.QueryList(conn,
                         "SELECT v.symbol_id AS symbol_id, v.embedding AS embedding" +
                         "  FROM vec_symbols_i8 v" +
                         "  JOIN symbol_embeddings e ON e.symbol_id = v.symbol_id" +
                         $" WHERE v.symbol_id IN ({Sql.Marks(chunk.Count)})" +
                         $"   AND e.repo_id IN ({Sql.Marks(ids.Count)})",
                         chunk.Concat(ids).Cast<object?>().ToArray()))
                allowedVectors.Add((row.Long("symbol_id"), row.Bytes("embedding") ?? []));
        if (allowedVectors.Count == 0) return [];

        var ranked = Quantize.Rescore(queryVec, allowedVectors).Take(Math.Max(limit, 1)).ToList();
        var byId = new Dictionary<long, double>();
        var order = new List<long>();
        foreach (var (id, score) in ranked) { if (byId.TryAdd(id, score)) order.Add(id); else byId[id] = score; }
        if (byId.Count == 0) return [];
        return HydrateSemantic(conn, ids, byId, order);
    }

    static List<JsonObject> HydrateSemantic(SqliteConnection conn, List<long> ids, Dictionary<long, double> byId, List<long> order)
    {
        if (byId.Count == 0) return [];
        var keys = order.Where(byId.ContainsKey).Distinct().ToList();
        var rows = Sql.QueryList(conn,
            "SELECT s.id AS symbol_id, s.repo_id, r.path_with_namespace," +
            "       f.path, s.name, s.kind, s.signature, s.scope," +
            "       s.line, s.end_line, s.is_public, s.doc" +
            "  FROM symbols s" +
            "  JOIN files f ON f.id = s.file_id" +
            "  JOIN repos r ON r.id = s.repo_id" +
            $" WHERE s.id IN ({Sql.Marks(keys.Count)}) AND s.repo_id IN ({Sql.Marks(ids.Count)})",
            keys.Concat(ids).Cast<object?>().ToArray());
        var output = new List<(double, JsonObject)>();
        foreach (var row in rows)
        {
            var obj = row.ToJson();
            var score = Math.Round(byId[row.Long("symbol_id")], 6, MidpointRounding.ToEven);
            obj["score"] = score;
            output.Add((score, obj));
        }
        return output.OrderBy(p => -p.Item1).Select(p => p.Item2).ToList();
    }

    // --- application knowledge ----------------------------------------------------

    public const int ReadmeChars = 1200;
    public const int MaxDirs = 12;
    public const int MaxKeySymbols = 15;
    public const int MaxOverviewRepos = 40;
    const string RootLabel = "(root)";

    static string ReadmeFor(SqliteConnection conn, long repoId)
    {
        var row = Sql.One(conn,
            "SELECT path, content FROM files" +
            " WHERE repo_id = ? AND lower(path) LIKE '%readme%'" +
            "   AND path NOT LIKE '%/%/%'" +
            " ORDER BY length(path), path LIMIT 1", repoId);
        if (row is null || row.Str("content").Length == 0) return "";
        var text = PyStr.Strip(row.Str("content"));
        return PyStr.Prefix(text, ReadmeChars) + (PyStr.Len(text) > ReadmeChars ? "…" : "");
    }

    static JsonArray Layout(SqliteConnection conn, long repoId)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in Sql.Query(conn, "SELECT path FROM files WHERE repo_id = ? AND is_vendored = 0", repoId))
        {
            var path = row.Str("path");
            var top = path.Contains('/') ? path.Split('/', 2)[0] : RootLabel;
            counts[top] = counts.GetValueOrDefault(top, 0) + 1;
        }
        var arr = new JsonArray();
        foreach (var (name, n) in counts.OrderBy(kv => -kv.Value).ThenBy(kv => kv.Key == RootLabel ? 0 : 1)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(MaxDirs))
            arr.Add(new JsonObject { ["path"] = name, ["files"] = n });
        return arr;
    }

    static List<Row> KeySymbols(SqliteConnection conn, IReadOnlyList<long> repoIds, int limit = MaxKeySymbols)
    {
        if (repoIds.Count == 0) return [];
        return Sql.QueryList(conn,
            "SELECT s.name, s.kind, s.signature, s.doc, f.path, r.path_with_namespace," +
            "       s.repo_id" +
            "  FROM symbols s" +
            "  JOIN files f ON f.id = s.file_id" +
            "  JOIN repos r ON r.id = s.repo_id" +
            $" WHERE s.repo_id IN ({Sql.Marks(repoIds.Count)}) AND s.is_public = 1" +
            "   AND s.doc IS NOT NULL AND s.doc <> ''" +
            " ORDER BY r.path_with_namespace, f.path, s.line" +
            " LIMIT ?", repoIds.Cast<object?>().Append((long)limit).ToArray());
    }

    /// <summary>What this estate contains: per repository, what it IS.</summary>
    public static JsonObject RepoOverview(IReadOnlyList<long> allowed, SqliteConnection conn, string? repo = null)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0) return [];
        var where = $" WHERE r.id IN ({Sql.Marks(ids.Count)})";
        var args = ids.Cast<object?>().ToList();
        if (!string.IsNullOrEmpty(repo)) { where += " AND r.path_with_namespace = ?"; args.Add(repo); }
        var cap = string.IsNullOrEmpty(repo) ? $" LIMIT {MaxOverviewRepos + 1}" : "";
        var rows = Sql.QueryList(conn,
            "SELECT r.id AS repo_id, r.path_with_namespace, r.branch," +
            "       r.default_branch, r.last_run_at, r.last_indexed_at," +
            "       (SELECT COUNT(*) FROM files f WHERE f.repo_id = r.id) AS files," +
            "       (SELECT COUNT(*) FROM symbols s WHERE s.repo_id = r.id) AS symbols," +
            "       (SELECT COUNT(*) FROM symbols s WHERE s.repo_id = r.id" +
            "         AND s.is_public = 1) AS public_symbols," +
            "       (SELECT COUNT(*) FROM symbols s WHERE s.repo_id = r.id" +
            "         AND s.doc IS NOT NULL AND s.doc <> '') AS documented_symbols" +
            $"  FROM repos r{where}" +
            $" ORDER BY r.path_with_namespace, r.branch{cap}", args);
        bool truncated = rows.Count > MaxOverviewRepos;
        rows = rows.Take(MaxOverviewRepos).ToList();

        var items = new List<JsonObject>();
        foreach (var row in rows)
        {
            var repoId = row.Long("repo_id");
            var item = row.ToJson();
            item["readme"] = ReadmeFor(conn, repoId);
            item["layout"] = Layout(conn, repoId);
            var langs = new JsonArray();
            foreach (var r in Sql.Query(conn,
                         "SELECT lang, COUNT(*) AS n FROM files WHERE repo_id = ?" +
                         " GROUP BY lang ORDER BY n DESC LIMIT 8", repoId))
                langs.Add(new JsonObject { ["lang"] = r.StrOrNull("lang") is { Length: > 0 } l ? l : "?", ["files"] = r.Long("n") });
            item["langs"] = langs;
            item["depends_on"] = EdgeList(conn,
                "SELECT r.path_with_namespace, d.weight FROM repo_deps d" +
                "  JOIN repos r ON r.id = d.to_repo_id" +
                " WHERE d.from_repo_id = ? AND d.to_repo_id IN" +
                $" ({Sql.Marks(ids.Count)})" +
                " ORDER BY d.weight DESC LIMIT 10", repoId, ids);
            item["depended_on_by"] = EdgeList(conn,
                "SELECT r.path_with_namespace, d.weight FROM repo_deps d" +
                "  JOIN repos r ON r.id = d.from_repo_id" +
                " WHERE d.to_repo_id = ? AND d.from_repo_id IN" +
                $" ({Sql.Marks(ids.Count)})" +
                " ORDER BY d.weight DESC LIMIT 10", repoId, ids);
            items.Add(item);
        }

        var described = items.Select(i => i["repo_id"]!.GetValue<long>()).ToList();
        var keys = KeySymbols(conn, described);
        var byRepo = new Dictionary<long, JsonArray>();
        foreach (var symbol in keys)
        {
            var rid = symbol.Long("repo_id");
            if (!byRepo.TryGetValue(rid, out var arr)) byRepo[rid] = arr = [];
            arr.Add(symbol.ToJson());
        }
        foreach (var item in items)
            item["key_symbols"] = byRepo.TryGetValue(item["repo_id"]!.GetValue<long>(), out var arr) ? arr : new JsonArray();

        var list = new JsonArray();
        foreach (var i in items) list.Add(i);
        return new JsonObject { ["repos"] = list, ["truncated"] = truncated, ["shown"] = items.Count };
    }

    static JsonArray EdgeList(SqliteConnection conn, string sql, long repoId, List<long> ids)
    {
        var arr = new JsonArray();
        foreach (var r in Sql.QueryList(conn, sql, new object?[] { repoId }.Concat(ids.Cast<object?>()).ToArray()))
            arr.Add(new JsonObject { ["repo"] = r.Str("path_with_namespace"), ["weight"] = r.Long("weight") });
        return arr;
    }
}
