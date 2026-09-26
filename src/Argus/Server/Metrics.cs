using System.Globalization;
using System.Reflection;
using System.Text;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Server;

/// <summary>
/// Prometheus metrics for the index, so it cannot go stale unnoticed
///. The exporter decides what "stale" means
/// (ARGUS_INDEX_STALE_AFTER, default 3600s) and exports the raw timestamps too.
///
/// One difference from the Python exposition, deliberately: HELP and TYPE are
/// written once per metric family rather than once per sample. Prometheus's
/// scraper tolerates the repetition, but the strict text-format parsers
/// (client_golang's expfmt, promtool) reject a second HELP line for a family.
/// </summary>
public static class Metrics
{
    public const int DefaultStaleAfter = 3600;
    public const string Prefix = "argus";

    public static int StaleAfter() =>
        int.TryParse(Environment.GetEnvironmentVariable("ARGUS_INDEX_STALE_AFTER"), out var v) ? v : DefaultStaleAfter;

    public static string Version =>
        Environment.GetEnvironmentVariable("ARGUS_VERSION") is { Length: > 0 } v
            ? v
            : (typeof(Metrics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0+unknown").Split('+')[0];

    public static string Escape(object? value) =>
        (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    public sealed record RepoState(string Repo, string Branch, bool IsDefault, long? LastRunAt, long? LastIndexedAt, double? AgeSeconds,
        bool Stale, bool TimedOut, bool SymbolsFailed, string? Error, long Files, long Symbols);

    public sealed record Snapshot(double Now, int StaleAfterSeconds, List<RepoState> Repos, int StaleRepos, int ErroredRepos, string Version);

    public static Snapshot Take(string dbPath, double? now = null)
    {
        double t = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        int limit = StaleAfter();
        var repos = new List<RepoState>();
        Db.Init();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        var counts = new Dictionary<long, Dictionary<string, long>>();
        foreach (var table in new[] { "files", "symbols" })
            foreach (var row in Sql.Query(conn, $"SELECT repo_id, COUNT(*) AS n FROM {table} GROUP BY repo_id"))
            {
                var id = row.Long("repo_id");
                if (!counts.TryGetValue(id, out var d)) counts[id] = d = new();
                d[table] = row.Long("n");
            }
        foreach (var row in Sql.Query(conn,
                     "SELECT id, path_with_namespace, branch, default_branch," +
                     "       last_run_at, last_indexed_at, last_run_timed_out," +
                     "       last_run_symbols_failed, last_run_error" +
                     "  FROM repos ORDER BY path_with_namespace, branch"))
        {
            var lastRun = row.LongOrNull("last_run_at");
            double? age = lastRun is null ? null : Math.Max(0.0, t - lastRun.Value);
            var byTable = counts.GetValueOrDefault(row.Long("id")) ?? new();
            var branch = row.StrOrNull("branch") is { Length: > 0 } b ? b : row.Str("default_branch");
            repos.Add(new RepoState(
                row.Str("path_with_namespace"), branch, branch == row.Str("default_branch"),
                lastRun, row.LongOrNull("last_indexed_at"), age,
                age is null || age > limit,
                row.Long("last_run_timed_out") != 0, row.Long("last_run_symbols_failed") != 0,
                row.StrOrNull("last_run_error"),
                byTable.GetValueOrDefault("files", 0), byTable.GetValueOrDefault("symbols", 0)));
        }
        return new Snapshot(t, limit, repos, repos.Count(r => r.Stale), repos.Count(r => !string.IsNullOrEmpty(r.Error)), Version);
    }

    sealed class Exposition
    {
        readonly StringBuilder _sb = new();
        readonly HashSet<string> _described = new(StringComparer.Ordinal);

        public void Line(string name, object value, IReadOnlyList<(string, object?)>? labels = null, string help = "", string kind = "gauge")
        {
            if (help.Length > 0 && _described.Add(name))
            {
                _sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
                _sb.Append("# TYPE ").Append(name).Append(' ').Append(kind).Append('\n');
            }
            _sb.Append(name);
            if (labels is { Count: > 0 })
                _sb.Append('{').Append(string.Join(",", labels.Select(l => $"{l.Item1}=\"{Escape(l.Item2)}\""))).Append('}');
            _sb.Append(' ').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
        }

        public void Comment(string text) => _sb.Append(text).Append('\n');
        public override string ToString() => _sb.ToString();
    }

    /// <summary>What to emit when the index cannot be read at all: still a 200 and a valid scrape.</summary>
    public static string RenderError(Exception exc)
    {
        var x = new Exposition();
        x.Line($"{Prefix}_index_build_info", 0, [("version", "unknown")], "Argus build, always 1");
        x.Line($"{Prefix}_index_scrape_ok", 0, help: "1 when this scrape read the index successfully");
        x.Comment($"# argus could not read the index: {PythonName(exc)}");
        return x.ToString();
    }

    /// <summary>The Python exception class a failure corresponds to, for operator-facing text.</summary>
    static string PythonName(Exception exc) => exc switch
    {
        SqliteException => "OperationalError",
        _ => exc.GetType().Name,
    };

    public static string Render(string dbPath)
    {
        var snap = Take(dbPath);
        var x = new Exposition();
        x.Line($"{Prefix}_index_build_info", 1, [("version", snap.Version)], "Argus build, always 1");
        x.Line($"{Prefix}_index_scrape_ok", 1, help: "1 when this scrape read the index successfully");
        x.Line($"{Prefix}_index_repos", snap.Repos.Count, help: "Repositories (at one branch each) in the index");
        x.Line($"{Prefix}_index_stale_repos", snap.StaleRepos, help: $"No successful pass within {snap.StaleAfterSeconds}s, or never indexed");
        x.Line($"{Prefix}_index_errored_repos", snap.ErroredRepos, help: "Repositories whose last pass recorded an error");
        x.Line($"{Prefix}_index_stale_after_seconds", snap.StaleAfterSeconds, help: "Threshold this build applies to the stale gauges");
        // Family-major, not repo-major: the text format requires every sample of a
        // family to be contiguous, which the Python exposition (one block per
        // repo) does not guarantee once there are two repositories.
        var families = new (string Name, string Help, Func<RepoState, object> Value)[]
        {
            ($"{Prefix}_index_last_run_timestamp_seconds", "Unix time of the last pass, 0 if it has never run", r => r.LastRunAt ?? 0),
            ($"{Prefix}_index_last_indexed_timestamp_seconds", "Unix time of the last pass that changed the index", r => r.LastIndexedAt ?? 0),
            ($"{Prefix}_index_files", "Files indexed in this repository", r => r.Files),
            ($"{Prefix}_index_symbols", "Symbols indexed in this repository", r => r.Symbols),
            ($"{Prefix}_index_stale", "1 when this repository has gone stale", r => r.Stale ? 1 : 0),
            ($"{Prefix}_index_timed_out", "1 when the last pass hit its time budget", r => r.TimedOut ? 1 : 0),
            ($"{Prefix}_index_symbols_failed", "1 when symbol extraction failed on the last pass", r => r.SymbolsFailed ? 1 : 0),
            ($"{Prefix}_index_errored", "1 when the last pass recorded an error", r => string.IsNullOrEmpty(r.Error) ? 0 : 1),
        };
        foreach (var (name, help, value) in families)
            foreach (var r in snap.Repos)
                x.Line(name, value(r), [("repo", r.Repo), ("branch", r.Branch)], help);
        return x.ToString();
    }
}
