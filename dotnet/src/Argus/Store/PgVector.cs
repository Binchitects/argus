using System.Globalization;
using System.Text;
using Npgsql;

namespace Argus.Store;

/// <summary>
/// The optional pgvector backend for symbol embeddings.
///
/// Opt-in with <c>ARGUS_VECTOR_BACKEND=pgvector</c> and <c>ARGUS_PG_DSN</c>. One
/// statement resolves the coarse Hamming pass, the cosine rerank AND the ACL, and
/// hands back the same (symbol_id, score) pairs the sqlite-vec path builds.
/// </summary>
public static class PgVector
{
    const string BackendEnv = "ARGUS_VECTOR_BACKEND";
    const string DsnEnv = "ARGUS_PG_DSN";
    const int EfMax = 1000;
    const int EfMin = 40;

    public static bool Enabled() =>
        (Environment.GetEnvironmentVariable(BackendEnv) ?? "").Trim().ToLowerInvariant() == "pgvector"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DsnEnv));

    static NpgsqlDataSource? _source;
    static readonly Lock Gate = new();

    static NpgsqlDataSource Source()
    {
        lock (Gate)
        {
            if (_source is not null) return _source;
            var dsn = (Environment.GetEnvironmentVariable(DsnEnv) ?? "").Trim();
            if (dsn.Length == 0) throw new InvalidOperationException($"{DsnEnv} is not set but {BackendEnv}=pgvector");
            _source = NpgsqlDataSource.Create(ToNpgsql(dsn));
            return _source;
        }
    }

    /// <summary>Accept a libpq URI (postgresql://user:pass@host:port/db) as psycopg does.</summary>
    public static string ToNpgsql(string dsn)
    {
        if (!dsn.StartsWith("postgres://", StringComparison.Ordinal) && !dsn.StartsWith("postgresql://", StringComparison.Ordinal))
            return dsn;
        var uri = new Uri(dsn);
        var b = new NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432, Database = uri.AbsolutePath.TrimStart('/') };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            b.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1) b.Password = Uri.UnescapeDataString(parts[1]);
        }
        return b.ConnectionString;
    }

    public static string VecLiteral(IReadOnlyList<double> vec)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < vec.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vec[i].ToString("F6", CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    public static string BitLiteral(IReadOnlyList<double> vec)
    {
        var sb = new StringBuilder(vec.Count);
        foreach (var x in vec) sb.Append(x > 0 ? '1' : '0');
        return sb.ToString();
    }

    public static void EnsureSchema(int dim)
    {
        using var conn = Source().OpenConnection();
        foreach (var sql in new[]
                 {
                     "CREATE EXTENSION IF NOT EXISTS vector",
                     $"""
                      CREATE TABLE IF NOT EXISTS symbol_embeddings (
                        symbol_id  bigint PRIMARY KEY,
                        repo_id    integer NOT NULL,
                        embed_text text    NOT NULL,
                        model      text    NOT NULL,
                        dim        integer NOT NULL,
                        text_version text,
                        embedding  vector({dim}) NOT NULL,
                        bits       bit({dim})    NOT NULL,
                        updated_at timestamptz NOT NULL DEFAULT now()
                      )
                      """,
                     "ALTER TABLE symbol_embeddings ADD COLUMN IF NOT EXISTS text_version text",
                     "CREATE INDEX IF NOT EXISTS idx_symemb_repo ON symbol_embeddings (repo_id)",
                     "CREATE INDEX IF NOT EXISTS idx_symemb_model ON symbol_embeddings (model, dim)",
                     "CREATE INDEX IF NOT EXISTS idx_symemb_bits ON symbol_embeddings USING hnsw (bits bit_hamming_ops)",
                 })
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }
    }

    public static int Upsert(IEnumerable<(long SymbolId, long RepoId, string Text, string Model, int Dim, string TextVersion, IReadOnlyList<double> Vec)> rows)
    {
        using var conn = Source().OpenConnection();
        int n = 0;
        foreach (var r in rows)
        {
            using var cmd = new NpgsqlCommand(
                "INSERT INTO symbol_embeddings (symbol_id, repo_id, embed_text, model, dim, text_version, " +
                " embedding, bits) VALUES (@a,@b,@c,@d,@e,@f,@g::vector,@h::bit varying) " +
                "ON CONFLICT (symbol_id) DO UPDATE SET " +
                "  repo_id=EXCLUDED.repo_id, embed_text=EXCLUDED.embed_text, " +
                "  model=EXCLUDED.model, dim=EXCLUDED.dim, " +
                "  text_version=EXCLUDED.text_version, " +
                "  embedding=EXCLUDED.embedding, bits=EXCLUDED.bits, " +
                "  updated_at=now()", conn);
            cmd.Parameters.AddWithValue("a", r.SymbolId);
            cmd.Parameters.AddWithValue("b", (int)r.RepoId);
            cmd.Parameters.AddWithValue("c", r.Text);
            cmd.Parameters.AddWithValue("d", r.Model);
            cmd.Parameters.AddWithValue("e", r.Dim);
            cmd.Parameters.AddWithValue("f", r.TextVersion);
            cmd.Parameters.AddWithValue("g", VecLiteral(r.Vec));
            cmd.Parameters.AddWithValue("h", BitLiteral(r.Vec));
            cmd.ExecuteNonQuery();
            n++;
        }
        return n;
    }

    public static int DeleteRepo(long repoId)
    {
        using var conn = Source().OpenConnection();
        using var cmd = new NpgsqlCommand("DELETE FROM symbol_embeddings WHERE repo_id = @r", conn);
        cmd.Parameters.AddWithValue("r", (int)repoId);
        return cmd.ExecuteNonQuery();
    }

    public static List<(long Id, double Score)> Search(IReadOnlyList<double> queryVec, IReadOnlyList<long> allowedRepoIds, int limit = 10, int coarse = 512)
    {
        var ids = allowedRepoIds.Distinct().Select(i => (int)i).ToArray();
        if (ids.Length == 0) return [];
        limit = Math.Max(limit, 1);
        coarse = Math.Max(coarse, limit);
        using var conn = Source().OpenConnection();
        void Set(string sql) { using var c = new NpgsqlCommand(sql, conn); c.ExecuteNonQuery(); }
        Set($"SET hnsw.ef_search = {Math.Min(Math.Max(coarse, EfMin), EfMax)}");
        Set("SET hnsw.iterative_scan = relaxed_order");
        Set($"SET hnsw.max_scan_tuples = {Math.Min(Math.Max(coarse * 40, 20000), 400000)}");
        using var cmd = new NpgsqlCommand("""
            WITH candidates AS (
              SELECT symbol_id
                FROM symbol_embeddings
               WHERE repo_id = ANY(@repos)
               ORDER BY bits <~> @bits::bit varying
               LIMIT @coarse
            )
            SELECT e.symbol_id, 1 - (e.embedding <=> @vec::vector) AS score
              FROM symbol_embeddings e
              JOIN candidates c USING (symbol_id)
             ORDER BY score DESC
             LIMIT @limit
            """, conn);
        cmd.Parameters.AddWithValue("repos", ids);
        cmd.Parameters.AddWithValue("bits", BitLiteral(queryVec));
        cmd.Parameters.AddWithValue("coarse", coarse);
        cmd.Parameters.AddWithValue("vec", VecLiteral(queryVec));
        cmd.Parameters.AddWithValue("limit", limit);
        using var reader = cmd.ExecuteReader();
        var output = new List<(long, double)>();
        while (reader.Read()) output.Add((reader.GetInt64(0), reader.GetDouble(1)));
        return output;
    }
}
