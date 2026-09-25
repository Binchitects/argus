using System.Text.Json.Nodes;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Indexing;

/// <summary>Health indicators computed from the index (argus/kpi.py).</summary>
public static class Kpi
{
    public static readonly HashSet<string> LowerIsBetter = new(StringComparer.Ordinal)
    {
        "repos_never_indexed", "repos_unhealthy", "ambiguous_include_rate",
        "median_repo_age_hours", "stalest_repo_hours", "mb_per_1k_files",
    };

    public static JsonObject Collect(SqliteConnection conn, string? dbPath = null, long? now = null)
    {
        long t = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long Scalar(string sql) => Sql.Scalar(conn, sql) is { } v ? Convert.ToInt64(v) : 0;

        var repos = Scalar("SELECT COUNT(*) FROM repos");
        var files = Scalar("SELECT COUNT(*) FROM files");
        var symbols = Scalar("SELECT COUNT(*) FROM symbols");
        var includes = Scalar("SELECT COUNT(*) FROM includes");
        var indexed = Scalar("SELECT COUNT(*) FROM repos WHERE last_indexed_at IS NOT NULL");
        var unhealthy = Scalar("SELECT COUNT(*) FROM repos WHERE last_run_error IS NOT NULL    OR last_run_timed_out = 1 OR last_run_symbols_failed = 1");
        var ages = Sql.Query(conn, "SELECT last_indexed_at FROM repos WHERE last_indexed_at IS NOT NULL")
            .Select(r => (t - Convert.ToInt64(r[0])) / 3600.0).Order().ToList();
        var resolution = Sql.Query(conn, "SELECT resolution, COUNT(*) AS n FROM includes GROUP BY resolution")
            .Where(r => r["resolution"] is not null).ToDictionary(r => r.Str("resolution"), r => r.Long("n"));
        var resolved = resolution.GetValueOrDefault("resolved", 0);
        var ambiguous = resolution.GetValueOrDefault("ambiguous", 0);
        double? sizeMb = null;
        if (dbPath is not null && File.Exists(dbPath))
            sizeMb = Math.Round(new FileInfo(dbPath).Length / (1024.0 * 1024.0), 1, MidpointRounding.ToEven);

        return new JsonObject
        {
            ["repos"] = repos,
            ["repos_indexed"] = indexed,
            ["repos_never_indexed"] = repos - indexed,
            ["files"] = files,
            ["symbols"] = symbols,
            ["symbols_per_1k_files"] = files != 0 ? Math.Round((double)symbols / files * 1000, 1, MidpointRounding.ToEven) : null,
            ["repos_unhealthy"] = unhealthy,
            ["median_repo_age_hours"] = R1(Median(ages)),
            ["stalest_repo_hours"] = ages.Count > 0 ? R1(ages.Max()) : null,
            ["includes"] = includes,
            ["resolved_include_rate"] = Rate(resolved, includes),
            ["ambiguous_include_rate"] = Rate(ambiguous, includes),
            ["cross_repo_edges"] = Scalar("SELECT COUNT(*) FROM repo_deps"),
            ["index_mb"] = sizeMb,
            ["mb_per_1k_files"] = sizeMb is > 0 && files != 0 ? R1(sizeMb.Value / files * 1000) : null,
            ["collected_at"] = t,
        };
    }

    static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    static double? Rate(long part, long whole) => whole != 0 ? Math.Round((double)part / whole, 4, MidpointRounding.ToEven) : null;
    static double? R1(double? value) => value is null ? null : Math.Round(value.Value, 1, MidpointRounding.ToEven);
}
