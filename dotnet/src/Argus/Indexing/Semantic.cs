using Argus.Packs;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Indexing;

/// <summary>
/// The semantic layer over PRIVATE code: one vector per
/// public symbol, built from its kind, name, doc comment, scope, signature and
/// path -- never a function body -- and stored in the pack layout so it is
/// scored exactly as pack chunks are.
/// </summary>
public static class Semantic
{
    public const int EmbedFlush = 256;
    public const string EmbedTextVersion = "2";

    static string CreateVecBin => $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS vec_symbols_bin USING vec0(
          symbol_id INTEGER PRIMARY KEY, embedding bit[{Embed.Dim}]
        )
        """;

    static string CreateVecI8 => $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS vec_symbols_i8 USING vec0(
          symbol_id INTEGER PRIMARY KEY, embedding int8[{Embed.Dim}]
        )
        """;

    const string Candidates = """
        SELECT s.id, s.repo_id, s.name, s.kind, s.signature, s.scope, f.path, s.doc
          FROM symbols s
          JOIN files f ON f.id = s.file_id
         WHERE s.is_public = 1
           AND ((s.signature IS NOT NULL AND s.signature <> '')
                OR (s.doc IS NOT NULL AND s.doc <> ''))
           AND NOT EXISTS (
               SELECT 1 FROM symbol_embeddings e
                WHERE e.symbol_id = s.id AND e.model = ? AND e.dim = ?
                  AND e.text_version = ?
           )
        """;

    public static void EnsureVecTables(SqliteConnection conn)
    {
        PackFormat.LoadVecExtension(conn);
        Sql.Exec(conn, CreateVecBin);
        Sql.Exec(conn, CreateVecI8);
    }

    public static string EmbedTextFor(string name, string kind, string? signature, string? scope, string? path, string? doc = "")
    {
        var parts = new List<string> { PyStr.Strip($"{kind} {name}") };
        if (!string.IsNullOrEmpty(doc)) parts.Add(doc);
        if (!string.IsNullOrEmpty(scope)) parts.Add($"in {scope}");
        if (!string.IsNullOrEmpty(signature)) parts.Add(signature);
        if (!string.IsNullOrEmpty(path)) parts.Add(path.Replace('/', ' ').Replace('_', ' '));
        return string.Join(" -- ", parts.Where(p => p.Length > 0));
    }

    public static int BuildSymbolEmbeddings(SqliteConnection conn, Func<IReadOnlyList<string>, List<double[]>>? embedFn = null,
        long? limit = null, Action<int, int>? progress = null)
    {
        embedFn ??= texts => Embed.EmbedBatch(texts);
        var model = Embed.Model;
        var dim = Embed.Dim;
        EnsureVecTables(conn);
        PruneOrphans(conn);
        var sql = Candidates + (limit is > 0 ? " LIMIT ?" : "");
        object?[] args = limit is > 0 ? [model, (long)dim, EmbedTextVersion, limit.Value] : [model, (long)dim, EmbedTextVersion];
        var rows = Sql.QueryList(conn, sql, args);
        if (rows.Count == 0) return 0;

        int done = 0;
        for (int start = 0; start < rows.Count; start += EmbedFlush)
        {
            var batch = rows.Skip(start).Take(EmbedFlush).ToList();
            var texts = batch.Select(r => EmbedTextFor(r.Str("name"), r.Str("kind"), r.StrOrNull("signature"),
                r.StrOrNull("scope"), r.StrOrNull("path"), r.StrOrNull("doc") ?? "")).ToList();
            var vectors = embedFn(texts);
            if (vectors.Count != batch.Count)
                throw new InvalidOperationException($"embedder returned {vectors.Count} vectors for {batch.Count} inputs");
            using (var tx = conn.BeginTransaction())
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var symbolId = batch[i].Long("id");
                    Sql.Exec(conn,
                        "INSERT OR REPLACE INTO symbol_embeddings (symbol_id, repo_id, embed_text, model, dim, text_version) VALUES (?, ?, ?, ?, ?, ?)",
                        symbolId, batch[i].Long("repo_id"), texts[i], model, (long)dim, EmbedTextVersion);
                    Sql.Exec(conn, "DELETE FROM vec_symbols_bin WHERE symbol_id = ?", symbolId);
                    Sql.Exec(conn, "DELETE FROM vec_symbols_i8 WHERE symbol_id = ?", symbolId);
                    Sql.Exec(conn, "INSERT INTO vec_symbols_bin (symbol_id, embedding) VALUES (?, vec_bit(?))", symbolId, Quantize.ToBits(vectors[i]));
                    Sql.Exec(conn, "INSERT INTO vec_symbols_i8 (symbol_id, embedding) VALUES (?, vec_int8(?))", symbolId, Quantize.ToInt8(vectors[i]));
                }
                tx.Commit();
            }
            done += batch.Count;
            progress?.Invoke(done, rows.Count);
        }
        return done;
    }

    /// <summary>Drop vectors whose symbol row is gone (vec0 tables have no cascade).</summary>
    public static int PruneOrphans(SqliteConnection conn)
    {
        var live = new HashSet<long>(Sql.Query(conn, "SELECT symbol_id FROM symbol_embeddings").Select(r => Convert.ToInt64(r[0])));
        int dropped = 0;
        foreach (var table in new[] { "vec_symbols_bin", "vec_symbols_i8" })
        {
            List<long> present;
            try { present = Sql.Query(conn, $"SELECT symbol_id FROM {table}").Select(r => Convert.ToInt64(r[0])).ToList(); }
            catch (SqliteException) { continue; }
            foreach (var id in present.Where(id => !live.Contains(id)))
            {
                Sql.Exec(conn, $"DELETE FROM {table} WHERE symbol_id = ?", id);
                dropped++;
            }
        }
        return dropped;
    }

    public static long StaleCount(SqliteConnection conn) =>
        Convert.ToInt64(Sql.Scalar(conn,
            "SELECT count(*) FROM symbol_embeddings WHERE model <> ? OR dim <> ? OR text_version IS NOT ?",
            Embed.Model, (long)Embed.Dim, EmbedTextVersion));
}
