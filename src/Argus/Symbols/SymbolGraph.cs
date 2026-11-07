using System.Diagnostics;
using Argus.Util;
using CodeArena;
using Microsoft.Data.Sqlite;

namespace Argus.Symbols;

/// <summary>
/// The symbol graph of one repository's checkout: every use in its C#, TypeScript, JavaScript and Python code tied to the
/// definition it names, and each type's bases, by Code Arena's own scanners and resolution (linked: the IDE, the agent and
/// Argus agree). Built when the repository is indexed, from the files of its working tree; nothing is compiled or
/// installed. Each edge says how it was tied (<see cref="Evidence"/>) and how sure that is.
/// </summary>
public static class SymbolGraph
{
    /// <summary>The starting confidence of each evidence letter (design §2): scope or import, type, the only name, a guess (split among its two or three).</summary>
    public static double Prior(string evidence, int candidates = 1) => evidence switch
    {
        "S" => 0.95,
        "T" => 0.90,
        "N" => 0.70,
        _ => 0.6 / Math.Max(1, candidates),
    };

    /// <summary>The graph's own version: a change builds every repository's again.</summary>
    public const string Version = "1";

    private static string Key(long repoId) => $"symbol_graph:{repoId}";

    /// <summary>Whether the repository's graph was built at this commit by this version.</summary>
    public static bool IsCurrent(SqliteConnection conn, long repoId, string sha) =>
        Sql.One(conn, "SELECT value FROM argus_meta WHERE key = ?", Key(repoId))?.Str("value") == $"{Version}:{sha}";

    public static void Record(SqliteConnection conn, long repoId, string sha) =>
        Sql.Exec(conn, "INSERT INTO argus_meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", Key(repoId), $"{Version}:{sha}");

    /// <summary>
    /// The graph made again when it is not current, from the paths the index keeps for the repository; what it found is
    /// said in its log. A failure is said and leaves the index as it is: the graph is never a reason for an index to fail.
    /// </summary>
    public static string? Refresh(SqliteConnection conn, long repoId, string tree, string sha, CancellationToken ct = default)
    {
        if (!Enabled || IsCurrent(conn, repoId, sha))
        {
            return null;
        }
        try
        {
            var paths = Sql.Query(conn, "SELECT path FROM files WHERE repo_id = ?", repoId).Select(r => r.Str("path")).ToList();
            var (uses, bases, took) = Build(conn, repoId, tree, paths, ct);
            Record(conn, repoId, sha);
            // A repository with no code the graph reads (C#, TypeScript, JavaScript, Python) has nothing to say.
            return uses + bases == 0 ? null : $"symbol graph: {uses} uses and {bases} bases tied to their definitions in {took.TotalSeconds:0.0} s";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException or ArgumentException)
        {
            return $"symbol graph not built ({e.GetType().Name}: {e.Message})";
        }
    }

    /// <summary>Whether it is built at all: ARGUS_SYMBOL_GRAPH=0 turns it off (no table rows, no tool).</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("ARGUS_SYMBOL_GRAPH") != "0";

    internal static string Letter(Evidence? e) => e switch { CodeArena.Evidence.Scope => "S", CodeArena.Evidence.Type => "T", CodeArena.Evidence.Name => "N", _ => "G" };

    /// <summary>The repository's edges made again from its checkout at <paramref name="tree"/>: how many uses and bases, and how long it took.</summary>
    public static (int Uses, int Bases, TimeSpan Took) Build(SqliteConnection conn, long repoId, string tree, IEnumerable<string> paths, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var listed = new List<CodeFile>();
        foreach (var path in paths)
        {
            var lang = CodeFiles.LanguageOf(path);
            if (CodeGraph.Family(lang) is null)
            {
                continue;
            }
            var full = Path.Combine(tree, path);
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > CodeFiles.MostSymbolBytes)
            {
                continue;
            }
            listed.Add(new CodeFile(path, full, lang, info.Length, info.LastWriteTimeUtc, CodeFiles.TagsOf(path)));
        }
        var index = new CodeIndex(tree, "");
        index.Sync(listed, ct);
        var graph = index.Graph();
        var uses = 0;
        var bases = 0;
        using var tx = conn.BeginTransaction();
        Sql.Exec(conn, "DELETE FROM sym_edges WHERE repo_id = ?", repoId);
        using var insert = Sql.Command(conn,
            "INSERT INTO sym_edges (repo_id, kind, from_path, from_symbol, from_name, from_line, to_path, to_symbol, to_name, to_line, evidence, confidence) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            new object?[12]);
        void Row(string kind, string fromPath, string fromSymbol, string fromName, int fromLine, SymbolRef to, string evidence, double confidence)
        {
            object?[] values = [repoId, kind, fromPath, fromSymbol, fromName, (long)fromLine, to.File.Path, to.Symbol.Qualified, to.Symbol.Name, (long)to.Symbol.Start, evidence, Math.Round(confidence, 4)];
            for (var i = 0; i < values.Length; i++)
            {
                insert.Parameters[i].Value = values[i];
            }
            insert.ExecuteNonQuery();
        }
        foreach (var file in index.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var edge in graph.EdgesIn(file))
            {
                if (edge.To.Count is 0 or > 3)
                {
                    continue;
                }
                var letter = edge.To.Count > 1 ? "G" : Letter(edge.Evidence);
                foreach (var to in edge.To)
                {
                    // A use inside its own definition (recursion aside, its name in its own lines) says nothing of who uses it.
                    if (edge.From is { } from && from.Qualified == to.Symbol.Qualified && to.File.Path == edge.Path && edge.Line == to.Symbol.Start)
                    {
                        continue;
                    }
                    Row(edge.Call ? "calls" : "references", edge.Path, edge.From?.Qualified ?? "", edge.From?.Name ?? "", edge.Line, to, letter, Prior(letter, edge.To.Count));
                    uses++;
                }
            }
            foreach (var symbol in file.Symbols)
            {
                foreach (var b in graph.BasesOfType(new SymbolRef(file, symbol.Symbol)))
                {
                    Row("inherits", file.Path, symbol.Symbol.Qualified, symbol.Symbol.Name, symbol.Symbol.Start, b, "S", Prior("S"));
                    bases++;
                }
            }
        }
        tx.Commit();
        return (uses, bases, clock.Elapsed);
    }
}
