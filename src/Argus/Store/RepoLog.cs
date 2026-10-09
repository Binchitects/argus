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
/// repository: its last <see cref="KeepRuns"/> runs that did something (read files, failed,
/// warned), the latest run that found nothing new, and the last <see cref="KeepNotes"/> changes admins made.
/// </summary>
public sealed class RepoLog(SqliteConnection conn, long gitlabId, long run, Func<double>? clock = null)
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    /// <summary>Runs kept per repository; older ones are dropped as new ones finish.</summary>
    public const int KeepRuns = 20;
    /// <summary>Admins' changes kept per repository, apart from its runs.</summary>
    public const int KeepNotes = 20;

    /// <summary>The kinds of line: of a run, of a run that found nothing new, an admin's change.</summary>
    public const string RunKind = "run";
    public const string QuietKind = "quiet";
    public const string NoteKind = "note";

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

    public static void Add(SqliteConnection conn, long gitlabId, long run, double at, string level, string text, string kind = RunKind) =>
        Sql.Exec(conn, "INSERT INTO index_log (gitlab_id, run, kind, ts, level, text) VALUES (?, ?, ?, ?, ?, ?)", gitlabId, run, kind, at, level, text);

    /// <summary>This run found nothing new (every branch up to date, nothing warned): only the latest such run is kept.</summary>
    public void Quiet()
    {
        if (Worst != Info) return;
        try { Sql.Exec(conn, "UPDATE index_log SET kind = ? WHERE gitlab_id = ? AND run = ? AND kind = ?", QuietKind, GitlabId, Run, RunKind); }
        catch (SqliteException exc) { Console.Error.WriteLine($"could not write the log of repository {GitlabId}: {exc.Message}"); }
    }

    /// <summary>A line outside any run (an admin's change): its own, now; kept apart from the runs.</summary>
    public static void Note(SqliteConnection conn, long gitlabId, string text, string level = Info)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Add(conn, gitlabId, now, now / 1000.0, level, text, NoteKind);
        TrimNotes(conn, gitlabId);
    }

    /// <summary>
    /// Drops a repository's runs that found nothing new but the latest, its runs beyond the last
    /// <see cref="KeepRuns"/>, and admins' changes beyond the last <see cref="KeepNotes"/>.
    /// </summary>
    public static void Trim(SqliteConnection conn, long gitlabId)
    {
        Sql.Exec(conn, """
            DELETE FROM index_log WHERE gitlab_id = ? AND kind = ? AND run < (
              SELECT MAX(run) FROM index_log WHERE gitlab_id = ? AND kind = ?)
            """, gitlabId, QuietKind, gitlabId, QuietKind);
        Sql.Exec(conn, """
            DELETE FROM index_log WHERE gitlab_id = ? AND kind <> ? AND run < (
              SELECT MIN(run) FROM (SELECT DISTINCT run FROM index_log WHERE gitlab_id = ? AND kind <> ? ORDER BY run DESC LIMIT ?))
            """, gitlabId, NoteKind, gitlabId, NoteKind, (long)KeepRuns);
        TrimNotes(conn, gitlabId);
    }

    static void TrimNotes(SqliteConnection conn, long gitlabId) =>
        Sql.Exec(conn, """
            DELETE FROM index_log WHERE gitlab_id = ? AND kind = ? AND id < (
              SELECT MIN(id) FROM (SELECT id FROM index_log WHERE gitlab_id = ? AND kind = ? ORDER BY id DESC LIMIT ?))
            """, gitlabId, NoteKind, gitlabId, NoteKind, (long)KeepNotes);

    /// <summary>A repository's last runs and the last as many changes admins made, oldest line first.</summary>
    public static List<LogLine> Read(SqliteConnection conn, long gitlabId, int runs = KeepRuns)
    {
        var n = (long)Math.Clamp(runs, 1, Math.Max(KeepRuns, KeepNotes));
        return [.. Sql.Query(conn, """
            SELECT run, ts, level, text FROM index_log
             WHERE gitlab_id = ? AND (
               (kind <> ? AND run >= COALESCE((
                  SELECT MIN(run) FROM (SELECT DISTINCT run FROM index_log WHERE gitlab_id = ? AND kind <> ? ORDER BY run DESC LIMIT ?)), 0))
               OR (kind = ? AND id >= COALESCE((
                  SELECT MIN(id) FROM (SELECT id FROM index_log WHERE gitlab_id = ? AND kind = ? ORDER BY id DESC LIMIT ?)), 0)))
             ORDER BY id
            """, gitlabId, NoteKind, gitlabId, NoteKind, n, NoteKind, gitlabId, NoteKind, n)
            .Select(r => new LogLine(r.Long("run"), r.Double("ts"), r.Str("level"), r.Str("text")))];
    }

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
