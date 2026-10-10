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
/// Every read an MCP tool makes of the private index.
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
        // a disallowed repo is never fetched.
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

            var definitions = Definitions(conn, fileRows.Select(f => f.Long("file_id")).ToList(), name);
            foreach (var frow in fileRows)
            {
                var defLines = definitions.GetValueOrDefault(frow.Long("file_id")) ?? [];
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

    /// <summary>The lines each file defines a symbol of this name at, in one query per chunk of files.</summary>
    static Dictionary<long, HashSet<long>> Definitions(SqliteConnection conn, List<long> fileIds, string name)
    {
        var lines = new Dictionary<long, HashSet<long>>();
        foreach (var chunk in Chunks(fileIds, 1))
        {
            foreach (var r in Sql.QueryList(conn, $"SELECT file_id, line FROM symbols WHERE name = ? AND file_id IN ({Sql.Marks(chunk.Count)})",
                         new object?[] { name }.Concat(chunk.Cast<object?>()).ToArray()))
            {
                if (!lines.TryGetValue(r.Long("file_id"), out var set)) lines[r.Long("file_id")] = set = [];
                set.Add(r.Long("line"));
            }
        }
        return lines;
    }

    /// <summary>A line that names a symbol: its file, repository, path, line, text, and whether the file defines it there.</summary>
    public sealed record Hit(long FileId, long RepoId, string Repo, string Path, long Line, string Context, bool Definition, bool FileDefines);

    /// <summary>
    /// The lines of these files that name a symbol in code, not in a comment or a string (those are counted as skipped).
    /// The full-text index finds the files; each is read with its comments and strings blanked.
    /// </summary>
    public static (List<Hit> Hits, int Skipped) ReferencesInFiles(SqliteConnection conn, IReadOnlyCollection<long> fileIds, string name)
    {
        var hits = new List<Hit>();
        var skipped = 0;
        if (fileIds.Count == 0 || string.IsNullOrWhiteSpace(name)) return (hits, 0);
        var pattern = new Regex(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);
        var ftsQuery = "\"" + name.Replace("\"", "\"\"") + "\"";
        foreach (var chunk in Chunks([.. fileIds.Distinct()], 1))
        {
            List<Row> rows;
            try
            {
                rows = Sql.QueryList(conn,
                    "SELECT f.id AS file_id, f.repo_id, r.path_with_namespace AS repo, f.path, f.lang, f.content" +
                    "  FROM files_fts JOIN files f ON f.id = files_fts.rowid JOIN repos r ON r.id = f.repo_id" +
                    $" WHERE files_fts MATCH ? AND f.id IN ({Sql.Marks(chunk.Count)})",
                    new object?[] { ftsQuery }.Concat(chunk.Cast<object?>()).ToArray());
            }
            catch (SqliteException)
            {
                rows = [];
            }
            var definitions = Definitions(conn, rows.Select(f => f.Long("file_id")).ToList(), name);
            foreach (var row in rows)
            {
                var content = row.Str("content");
                var lines = PyStr.SplitLines(content);
                var code = CodeText.Reads(row.StrOrNull("lang")) ? PyStr.SplitLines(CodeText.Blank(row.StrOrNull("lang"), content)) : lines;
                var defines = definitions.GetValueOrDefault(row.Long("file_id")) ?? [];
                for (var i = 0; i < lines.Count; i++)
                {
                    if (!pattern.IsMatch(lines[i])) continue;
                    if (i >= code.Count || !pattern.IsMatch(code[i]))
                    {
                        skipped++;
                        continue;
                    }
                    hits.Add(new Hit(row.Long("file_id"), row.Long("repo_id"), row.Str("repo"), row.Str("path"), i + 1, lines[i], defines.Contains(i + 1), defines.Count > 0));
                }
            }
        }
        return (hits, skipped);
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
    /// <summary>
    /// How many readable repositories build on each one (likely or strong build links of their own code), counted at its
    /// default branch and given to its branches' rows too.
    /// </summary>
    static Dictionary<long, long> InDegree(SqliteConnection conn, List<long> allowed)
    {
        var counts = new Dictionary<long, long>();
        if (allowed.Count == 0) return counts;
        var readable = allowed.ToHashSet();
        var project = new Dictionary<long, long>();
        foreach (var r in Sql.Query(conn, "SELECT id, gitlab_id FROM repos")) project[r.Long("id")] = r.Long("gitlab_id");
        var dependents = new Dictionary<long, HashSet<long>>();
        foreach (var e in Sql.Query(conn, "SELECT from_repo_id, to_repo_id FROM repo_edges WHERE layer = 'build' AND scope = 'main' AND confidence >= ?", LinkKinds.Walked))
        {
            if (!readable.Contains(e.Long("from_repo_id")) || !project.TryGetValue(e.Long("to_repo_id"), out var to)) continue;
            if (!dependents.TryGetValue(to, out var set)) dependents[to] = set = [];
            set.Add(e.Long("from_repo_id"));
        }
        foreach (var id in allowed)
        {
            if (project.TryGetValue(id, out var p) && dependents.TryGetValue(p, out var set)) counts[id] = set.Count;
        }
        return counts;
    }

    static string RepoName(SqliteConnection conn, long repoId) =>
        Sql.One(conn, "SELECT path_with_namespace FROM repos WHERE id = ?", repoId)?.Str("path_with_namespace") ?? "";

    // --- impact_of ----------------------------------------------------------------

    public const int DefaultImpactDepth = 3;
    public const int DefaultImpactLimit = 200;

    /// <summary>
    /// Files affected by changing one file, transitively, traversing only allowed repos: what #includes it, and what
    /// imports what it declares (its C# namespace, its Java package and type, its Python module, its Go package, its
    /// .proto, its npm package), in its own repository and in the default branches of the others. Each file says the
    /// depth it was reached at and how (via).
    /// </summary>
    public static JsonObject ImpactOf(IReadOnlyList<long> allowed, SqliteConnection conn, long repoId, string path,
        long maxDepth = DefaultImpactDepth, int limit = DefaultImpactLimit)
    {
        var ids = Ids(allowed);
        if (ids.Count == 0 || !ids.Contains(repoId) || string.IsNullOrEmpty(path)) return [];
        var target = Sql.One(conn, "SELECT id, path FROM files WHERE repo_id = ? AND path = ?", repoId, path);
        if (target is null) return [];
        long depth = Math.Max(1, Math.Min(maxDepth, 10));
        var allowedSet = ids.ToHashSet();
        // Imports are read in the file's own repository and the other projects' default branches (links are made there).
        var gitlab = Sql.Query(conn, "SELECT id, gitlab_id, branch = default_branch AS is_default FROM repos")
            .ToDictionary(r => r.Long("id"), r => (Project: r.Long("gitlab_id"), Default: r.Long("is_default") != 0));
        var ownProject = gitlab[repoId].Project;
        bool Reads(long repo) => allowedSet.Contains(repo) && (repo == repoId || gitlab.TryGetValue(repo, out var g) && g.Default && g.Project != ownProject);

        var reached = new Dictionary<long, (long Depth, string Via)>();
        var frontier = new List<long> { target.Long("id") };
        var seen = new HashSet<long> { target.Long("id") };
        for (var level = 1; level <= depth && frontier.Count > 0 && reached.Count <= limit; level++)
        {
            var next = new List<long>();
            void Reach(long file, string via)
            {
                if (!seen.Add(file)) return;
                reached[file] = (level, via);
                next.Add(file);
            }
            foreach (var file in frontier)
            {
                foreach (var i in Sql.Query(conn, "SELECT file_id, repo_id, raw FROM includes WHERE resolved_file_id = ? AND resolution = 'resolved'", file))
                    if (allowedSet.Contains(i.Long("repo_id"))) Reach(i.Long("file_id"), "#include " + i.Str("raw"));
                foreach (var (importer, importing, via) in Importers(conn, file))
                    if (Reads(importer)) Reach(importing, via);
            }
            frontier = next;
        }

        var rows = reached.Count == 0 ? [] : reached.Keys.Chunk(500).SelectMany(chunk => Sql.Query(conn,
                $"SELECT f.id, f.path, p.path_with_namespace FROM files f JOIN repos p ON p.id = f.repo_id WHERE f.id IN ({string.Join(",", chunk)})"))
            .Select(r => (Id: r.Long("id"), Path: r.Str("path"), Repo: r.Str("path_with_namespace"), reached[r.Long("id")].Depth, reached[r.Long("id")].Via))
            .OrderBy(r => r.Depth).ThenBy(r => r.Repo, StringComparer.Ordinal).ThenBy(r => r.Path, StringComparer.Ordinal).ToList();
        bool truncated = rows.Count > limit;
        rows = rows.Take(limit).ToList();

        var byRepo = new JsonObject();
        foreach (var row in rows)
        {
            if (byRepo[row.Repo] is not JsonArray arr) byRepo[row.Repo] = arr = [];
            arr.Add(new JsonObject { ["path"] = row.Path, ["depth"] = row.Depth, ["via"] = row.Via });
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

    /// <summary>
    /// The files that import what one file declares, each with its repository and how it imports it: the uses (file_decls)
    /// that name the file's C# namespace, its Java type or package, its Python module (or the package it is imported
    /// from), its Go package, its .proto or its npm package.
    /// </summary>
    static IEnumerable<(long Repo, long File, string Via)> Importers(SqliteConnection conn, long fileId)
    {
        var file = Sql.One(conn, "SELECT repo_id, path, lang FROM files WHERE id = ?", fileId);
        if (file is null) yield break;
        var (repo, path, lang) = (file.Long("repo_id"), file.Str("path"), file.StrOrNull("lang"));
        var provides = Sql.Query(conn, "SELECT kind, name FROM file_decls WHERE file_id = ? AND role = 'provides'", fileId).Select(r => (Kind: r.Str("kind"), Name: r.Str("name"))).ToList();
        var asks = new List<(string Sql, object?[] Args, Func<string, bool>? Keep)>();
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (var (kind, name) in provides)
        {
            switch (kind)
            {
                case "cs":
                    asks.Add(("kind = 'cs' AND name = ?", [name], null));
                    // using static A.B.Type; using X = A.B.Type.
                    asks.Add(("kind = 'cs-type' AND (name = ? OR (name >= ? AND name < ?))", [name, .. Under(name + ".")],
                        n => n == name || !n[(name.Length + 1)..].Contains('.')));
                    break;
                case "java":
                    asks.Add(("kind = 'java-package' AND name = ?", [name], null));
                    asks.Add(("kind = 'java' AND (name >= ? AND name < ?)", [.. Under(name + ".")],
                        // The type, its nested types, or (Kotlin) a top-level function one segment past the package.
                        n => n == $"{name}.{stem}" || n.StartsWith($"{name}.{stem}.", StringComparison.Ordinal)
                             || lang == "kotlin" && n[(name.Length + 1)..] is var rest && !rest.Contains('.') && rest.Length > 0 && char.IsLower(rest[0])));
                    break;
                case "proto":
                    asks.Add(("kind = 'proto' AND (name = ? OR ? LIKE '%/' || name)", [name, name], null));
                    break;
            }
        }
        if (lang == "python")
        {
            var pyFiles = Sql.Query(conn, "SELECT name FROM file_decls WHERE repo_id = ? AND role = 'provides' AND kind = 'py-file'", repo).Select(r => r.Str("name")).ToList();
            foreach (var (module, _) in Graph.PythonModulesAt(pyFiles).Where(m => m.File == path))
            {
                asks.Add(("kind = 'py' AND (name = ? OR (name >= ? AND name < ?))", [module, .. Under(module + ".")], null));
                // from package import module
                if (module.LastIndexOf('.') is var dot and > 0) asks.Add(("kind = 'py' AND name = ?", [module[..dot]], null));
            }
        }
        if (lang == "go")
        {
            // The module whose go.mod is nearest above it, and the package its folder is.
            var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            var module = Sql.Query(conn, "SELECT d.name, f.path FROM file_decls d JOIN files f ON f.id = d.file_id WHERE d.repo_id = ? AND d.role = 'provides' AND d.kind = 'go'", repo)
                .Select(r => (Name: r.Str("name"), Dir: r.Str("path").Contains('/') ? r.Str("path")[..r.Str("path").LastIndexOf('/')] : ""))
                .Where(m => m.Dir == "" || dir == m.Dir || dir.StartsWith(m.Dir + "/", StringComparison.Ordinal)).OrderByDescending(m => m.Dir.Length).FirstOrDefault();
            if (module.Name is { } goModule)
            {
                var package = dir.Length > module.Dir.Length ? goModule + "/" + dir[(module.Dir.Length == 0 ? 0 : module.Dir.Length + 1)..] : goModule;
                asks.Add(("kind = 'go' AND name = ?", [package], null));
            }
        }
        if (lang is "typescript" or "javascript")
        {
            // The npm package of the nearest package.json above it.
            var package = Sql.Query(conn, "SELECT d.name, f.path FROM file_decls d JOIN files f ON f.id = d.file_id WHERE d.repo_id = ? AND d.role = 'provides' AND d.kind = 'npm'", repo)
                .Select(r => (Name: r.Str("name"), Dir: r.Str("path").Contains('/') ? r.Str("path")[..r.Str("path").LastIndexOf('/')] : ""))
                .Where(m => m.Dir == "" || path.StartsWith(m.Dir + "/", StringComparison.Ordinal)).OrderByDescending(m => m.Dir.Length).FirstOrDefault();
            if (package.Name is { } npm) asks.Add(("kind = 'npm' AND (name = ? OR (name >= ? AND name < ?))", [npm, .. Under(npm + "/")], null));
        }
        foreach (var (where, args, keep) in asks)
        {
            foreach (var u in Sql.Query(conn, $"SELECT DISTINCT repo_id, file_id, kind, name FROM file_decls WHERE role = 'uses' AND {where}", args))
            {
                if (u.Long("file_id") == fileId || keep is not null && !keep(u.Str("name"))) continue;
                yield return (u.Long("repo_id"), u.Long("file_id"), $"{u.Str("kind")} {u.Str("name")}");
            }
        }
    }

    /// <summary>The names under a prefix as an index range: from the prefix to the prefix with its last character one higher.</summary>
    static object?[] Under(string prefix) => [prefix, prefix[..^1] + (char)(prefix[^1] + 1)];

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
        var estate = GraphQueries.Load(allowed, conn);

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
            // Linked by packages, imports, #includes, submodules, CI includes and images (GraphQueries).
            var (uses, usedBy) = GraphQueries.Neighbours(estate, repoId);
            item["depends_on"] = uses;
            item["depended_on_by"] = usedBy;
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

}
