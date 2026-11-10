using System.Text.Json.Nodes;
using CodeIntel.Util;
using Microsoft.Data.Sqlite;

namespace CodeIntel.Store;

/// <summary>
/// The symbol graph inside each repository (Symbols/SymbolGraph.cs): who calls a symbol, what it calls, what extends or
/// implements a type and what it extends. Like every read of the index, the caller's allowlist is in the SQL of each step;
/// a walk never leaves the repository it starts in (the graph is per repository), and never shows a repository the
/// caller may not read.
/// </summary>
public static class SymbolQueries
{
    public static readonly string[] Relations = ["callers", "callees", "references", "implementations", "bases"];

    /// <summary>Below this an edge is a guess (two or three definitions of the name): listed only with possible.</summary>
    public const double Walked = 0.5;

    public const int MostTargets = 5;

    /// <summary>
    /// A symbol's neighbours by <paramref name="relation"/>, <paramref name="depth"/> steps out (1 to 3), at most
    /// <paramref name="limit"/> each step and target. <paramref name="symbol"/> is a name (Save), a qualified name or its
    /// tail (Orders.Save), or path#Qualified; several definitions of it answer one by one (at most five).
    /// </summary>
    public static JsonObject Walk(IReadOnlyList<long> allowed, SqliteConnection conn, string symbol, string relation, string? repo = null,
        int depth = 1, int limit = 50, bool possible = false)
    {
        if (!Relations.Contains(relation))
        {
            throw new QueryError($"relation is one of {string.Join(", ", Relations)}, not {relation}.");
        }
        var list = Queries.AllowJson(allowed);
        depth = Math.Clamp(depth, 1, 3);
        limit = Math.Clamp(limit, 1, 200);
        var min = possible ? 0.0 : Walked;
        var targets = Targets(conn, list, symbol.Trim(), repo);
        var answers = new JsonArray();
        foreach (var t in targets.Take(MostTargets))
        {
            var steps = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal) { t.Symbol };
            var frontier = new List<string> { t.Symbol };
            var truncated = false;
            for (var level = 1; level <= depth && frontier.Count > 0; level++)
            {
                var next = new List<string>();
                foreach (var at in frontier)
                {
                    var (sql, side) = relation switch
                    {
                        "callers" => ("to_symbol = ? AND kind = 'calls'", "from"),
                        "references" => ("to_symbol = ? AND kind IN ('calls', 'references')", "from"),
                        "callees" => ("from_symbol = ? AND kind = 'calls'", "to"),
                        "implementations" => ("to_symbol = ? AND kind = 'inherits'", "from"),
                        _ => ("from_symbol = ? AND kind = 'inherits'", "to"),
                    };
                    var rows = Sql.Query(conn,
                        "SELECT kind, from_path, from_symbol, from_line, to_path, to_symbol, to_line, evidence, confidence FROM sym_edges " +
                        $"WHERE repo_id = ? AND repo_id {Queries.InAllowed} AND {sql} AND confidence >= ? ORDER BY confidence DESC, from_path, from_line LIMIT ?",
                        t.RepoId, list, at, min, (long)limit + 1);
                    if (rows.Count > limit)
                    {
                        truncated = true;
                        rows = rows.Take(limit).ToList();
                    }
                    foreach (var r in rows)
                    {
                        var name = side == "from" ? r.Str("from_symbol") : r.Str("to_symbol");
                        steps.Add(new JsonObject
                        {
                            ["depth"] = level,
                            ["symbol"] = name.Length == 0 ? "(top of the file)" : name,
                            ["path"] = side == "from" ? r.Str("from_path") : r.Str("to_path"),
                            ["line"] = side == "from" ? r.Long("from_line") : r.Long("to_line"),
                            ["via"] = level == 1 ? null : at,
                            ["kind"] = r.Str("kind"),
                            ["evidence"] = r.Str("evidence"),
                            ["confidence"] = r.Double("confidence"),
                        });
                        if (name.Length > 0 && seen.Add(name))
                        {
                            next.Add(name);
                        }
                    }
                }
                frontier = next;
            }
            answers.Add(new JsonObject
            {
                ["repo"] = t.Repo,
                ["repo_id"] = t.RepoId,
                ["symbol"] = t.Symbol,
                ["path"] = t.Path,
                ["line"] = t.Line,
                ["relation"] = relation,
                ["found"] = steps.Count,
                ["truncated"] = truncated,
                ["results"] = steps,
            });
        }
        return new JsonObject
        {
            ["symbol"] = symbol,
            ["definitions"] = targets.Count,
            ["answers"] = answers,
            ["note"] = targets.Count == 0
                ? "In the repositories you may read, nothing uses a definition of that name, and it uses nothing (the symbol graph covers C#, TypeScript, JavaScript and Python; find_symbol says whether it is defined at all)."
                : targets.Count > MostTargets ? $"{targets.Count} definitions match; the first {MostTargets} are answered: name it as Type.Member or path#Qualified, or pass repo." : null,
            ["evidence"] = "S: by scope or import; T: by the receiver's declared type; N: the only definition of the name; G: a guess among two or three (shown with possible).",
        };
    }

    private sealed record Target(long RepoId, string Repo, string Symbol, string Path, long Line);

    private static List<Target> Targets(SqliteConnection conn, string allowed, string symbol, string? repo)
    {
        string? path = null;
        var name = symbol;
        if (symbol.IndexOf('#') is var hash and > 0)
        {
            path = symbol[..hash];
            name = symbol[(hash + 1)..];
        }
        var last = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        var rows = Sql.Query(conn,
            "SELECT DISTINCT e.repo_id, r.path_with_namespace, e.to_symbol AS sym, e.to_path AS path, e.to_line AS line FROM sym_edges e JOIN repos r ON r.id = e.repo_id " +
            $"WHERE e.repo_id {Queries.InAllowed} AND e.to_name = ? " +
            "UNION SELECT DISTINCT e.repo_id, r.path_with_namespace, e.from_symbol, e.from_path, e.from_line FROM sym_edges e JOIN repos r ON r.id = e.repo_id " +
            $"WHERE e.repo_id {Queries.InAllowed} AND e.from_name = ?",
            allowed, last, allowed, last);
        return rows
            .Select(r => new Target(r.Long("repo_id"), r.Str("path_with_namespace"), r.Str("sym"), r.Str("path"), r.Long("line")))
            .Where(t => t.Symbol == name || t.Symbol.EndsWith("." + name, StringComparison.Ordinal))
            .Where(t => path is null || t.Path == path)
            .Where(t => repo is null || string.Equals(t.Repo, repo, StringComparison.OrdinalIgnoreCase) || t.RepoId.ToString() == repo)
            .GroupBy(t => (t.RepoId, t.Symbol, t.Path)).Select(g => g.MinBy(t => t.Line)!)
            .OrderBy(t => t.Repo, StringComparer.Ordinal).ThenBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.Line).ToList();
    }
}
