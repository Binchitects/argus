using System.Globalization;
using System.Text.Json.Nodes;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>One line of a repository's log: its run (when the run started, in unix milliseconds), when, how bad, and what happened in a plain sentence.</summary>
public sealed record LogLine(long Run, double At, string Level, string Text);

/// <summary>
/// Each repository's own log (index_log): what every run did to it, in sentences a person
/// reads (what happened, how long it took, and what to do when something went wrong), and what
/// admins did to it. The run log on stdout stays the operator's; this one is kept per
/// repository, its last <see cref="KeepRuns"/> runs.
/// </summary>
public sealed class RepoLog(SqliteConnection conn, long gitlabId, long run, Func<double>? clock = null)
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    /// <summary>Runs kept per repository; older ones are dropped as new ones finish.</summary>
    public const int KeepRuns = 20;

    readonly Func<double> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

    public long GitlabId { get; } = gitlabId;
    public long Run { get; } = run;

    /// <summary>The worst level written so far: info, warning or error.</summary>
    public string Worst { get; private set; } = Info;

    public void Write(string level, string text)
    {
        if (level == Error || (level == Warning && Worst == Info)) Worst = level;
        try { Add(conn, GitlabId, Run, _clock(), level, text); }
        catch (SqliteException exc) { Console.Error.WriteLine($"could not write the log of repository {GitlabId}: {exc.Message}"); }
    }

    public void Say(string text) => Write(Info, text);
    public void Warn(string text) => Write(Warning, text);
    public void Fail(string text) => Write(Error, text);

    public static void Add(SqliteConnection conn, long gitlabId, long run, double at, string level, string text) =>
        Sql.Exec(conn, "INSERT INTO index_log (gitlab_id, run, ts, level, text) VALUES (?, ?, ?, ?, ?)", gitlabId, run, at, level, text);

    /// <summary>A line outside any run (an admin's change): its own run, now.</summary>
    public static void Note(SqliteConnection conn, long gitlabId, string text, string level = Info)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Add(conn, gitlabId, now, now / 1000.0, level, text);
    }

    /// <summary>Drops a repository's runs beyond the last <see cref="KeepRuns"/>.</summary>
    public static void Trim(SqliteConnection conn, long gitlabId) =>
        Sql.Exec(conn, """
            DELETE FROM index_log WHERE gitlab_id = ? AND run < (
              SELECT MIN(run) FROM (SELECT DISTINCT run FROM index_log WHERE gitlab_id = ? ORDER BY run DESC LIMIT ?))
            """, gitlabId, gitlabId, (long)KeepRuns);

    /// <summary>A repository's last runs, oldest line first.</summary>
    public static List<LogLine> Read(SqliteConnection conn, long gitlabId, int runs = KeepRuns) =>
        [.. Sql.Query(conn, """
            SELECT run, ts, level, text FROM index_log
             WHERE gitlab_id = ? AND run >= COALESCE((
               SELECT MIN(run) FROM (SELECT DISTINCT run FROM index_log WHERE gitlab_id = ? ORDER BY run DESC LIMIT ?)), 0)
             ORDER BY id
            """, gitlabId, gitlabId, (long)Math.Clamp(runs, 1, KeepRuns))
            .Select(r => new LogLine(r.Long("run"), r.Double("ts"), r.Str("level"), r.Str("text")))];

    public static JsonObject Json(LogLine l) => new() { ["run"] = l.Run, ["at"] = l.At, ["level"] = l.Level, ["text"] = l.Text };

    /// <summary>A duration in words: "0.4 s", "12 s", "3 min 5 s", "1 h 2 min".</summary>
    public static string Took(double seconds)
    {
        if (seconds < 10) return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, seconds):0.0} s");
        var s = (long)Math.Round(seconds);
        if (s < 60) return $"{s} s";
        if (s < 3600) return s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s";
        return (s % 3600) / 60 == 0 ? $"{s / 3600} h" : $"{s / 3600} h {(s % 3600) / 60} min";
    }

    /// <summary>"1 file", "3 files".</summary>
    public static string Count(long n, string one, string? many = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {(n == 1 ? one : many ?? one + "s")}");

    /// <summary>A commit as people see it: its short hash and its subject, when known.</summary>
    public static string Commit(string sha, string? subject) =>
        subject is { Length: > 0 } ? $"{PyStr.Prefix(sha, 8)} (\"{PyStr.Prefix(subject, 80)}\")" : PyStr.Prefix(sha, 8);
}
