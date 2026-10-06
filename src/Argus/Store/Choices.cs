using System.Globalization;
using Argus.Indexing;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>A repository GitLab listed, and what the admin chose for it.</summary>
/// <param name="Instance">The GitLab it was last listed from (scheme, host and port).</param>
/// <param name="CreatedAt">When GitLab says the project was made: the same id with another time is another project.</param>
/// <param name="Schedule">Its own schedule (<see cref="RepoSchedule"/>); "" follows the default for every repository.</param>
/// <param name="ScheduledAt">When its schedule last started a run of it, or was set: the next time counts from there.</param>
public sealed record RepoChoice(long GitlabId, string Path, string DefaultBranch, string HttpUrl, bool Included, IReadOnlyList<string> Branches,
    long? SeenAt, long? ChangedAt, string? Instance = null, string? CreatedAt = null, string Schedule = "", long? ScheduledAt = null)
{
    /// <summary>The group (namespace) it is in: its path without the last part.</summary>
    public string Group => Path.LastIndexOf('/') is var i and > 0 ? Path[..i] : "";

    /// <summary>Its name: the last part of its path.</summary>
    public string Name => Path.LastIndexOf('/') is var i and >= 0 ? Path[(i + 1)..] : Path;
}

/// <summary>What a GitLab listing changed: repositories new to the index, ones found under a new id, ones moved out of the way of another.</summary>
public sealed record Listing(int New, int Moved, int SetAside);

/// <summary>
/// Which repositories are indexed, which branches besides each one's default, and on what
/// schedule (repo_choices). Every repository GitLab lists gets a row; a new one is in or out
/// as `index.new_repos` says (in, unless set otherwise).
///
/// A repository is its project in one GitLab: the id GitLab gives it, for as long as it is
/// that project. A listing (<see cref="Record"/>) finds each project's row by its id when it is
/// the same project, else by its path, so a new token, another account, or a GitLab set up anew
/// never makes a second copy of a repository Argus already knows.
/// </summary>
public static class Choices
{
    public const string NewReposKey = "index.new_repos";
    /// <summary>The schedule of every repository that has none of its own.</summary>
    public const string DefaultScheduleKey = "index.repo_schedule";
    /// <summary>The time zone daily and weekly schedules are in.</summary>
    public const string ScheduleZoneKey = "index.schedule_tz";
    /// <summary>When GitLab last listed the repositories: a row seen before that is one GitLab no longer lists.</summary>
    public const string ListedAtKey = "repos.listed_at";

    static string? Meta(SqliteConnection conn, string key) => Sql.One(conn, "SELECT value FROM argus_meta WHERE key = ?", key)?.Str("value");

    static void SetMeta(SqliteConnection conn, string key, string value) =>
        Sql.Exec(conn, "INSERT INTO argus_meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value);

    /// <summary>Whether a repository GitLab lists for the first time is indexed.</summary>
    public static bool NewReposIncluded(SqliteConnection conn) => Meta(conn, NewReposKey) != "exclude";

    public static void SetNewReposIncluded(SqliteConnection conn, bool included) => SetMeta(conn, NewReposKey, included ? "include" : "exclude");

    /// <summary>The schedule of every repository without one of its own (with each pass, until set).</summary>
    public static RepoSchedule DefaultSchedule(SqliteConnection conn) => RepoSchedule.Parse(Meta(conn, DefaultScheduleKey));

    /// <summary>A new default schedule, for every repository that follows it; it counts as <see cref="SetSchedule"/> says.</summary>
    public static void SetDefaultSchedule(SqliteConnection conn, RepoSchedule schedule, long now)
    {
        using var tx = conn.BeginTransaction();
        SetMeta(conn, DefaultScheduleKey, schedule.ToString());
        Sql.Exec(conn, "UPDATE repo_choices SET scheduled_at = ? WHERE schedule = ''", schedule.Kind == ScheduleKind.Hours ? null : now);
        tx.Commit();
    }

    /// <summary>The IANA zone daily and weekly schedules are in ("UTC" until set).</summary>
    public static string ScheduleZone(SqliteConnection conn) => Meta(conn, ScheduleZoneKey) is { Length: > 0 } z ? z : "UTC";

    public static void SetScheduleZone(SqliteConnection conn, string zone) => SetMeta(conn, ScheduleZoneKey, zone);

    /// <summary>When GitLab last listed the repositories; null before the first listing.</summary>
    public static long? ListedAt(SqliteConnection conn) =>
        long.TryParse(Meta(conn, ListedAtKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) ? at : null;

    /// <summary>A path as GitLab compares them: without case.</summary>
    static string Key(string path) => path.ToLowerInvariant();

    /// <summary>
    /// What GitLab lists now: each project's row found (by id when it is the same project, else by
    /// path), new ones made (in or out by the policy), and known ones given their current name,
    /// default branch and clone URL.
    /// </summary>
    /// <param name="instance">The GitLab listed (<see cref="GitLab.Instance"/>); null when unknown (taken as the one the rows came from).</param>
    public static Listing Record(SqliteConnection conn, IReadOnlyList<Project> projects, long now, string? instance = null)
    {
        var include = NewReposIncluded(conn) ? 1L : 0L;
        using var tx = conn.BeginTransaction();
        var rows = List(conn);
        var byId = rows.ToDictionary(r => r.GitlabId);
        var listedIds = projects.Select(p => p.GitlabId).ToHashSet();
        var listedPath = new Dictionary<string, long>();
        foreach (var p in projects) listedPath.TryAdd(Key(p.PathWithNamespace), p.GitlabId);

        // The same project under its id: the same path, or (renamed) the same creation time;
        // without creation times, the same GitLab, unless its path is listed under another id.
        bool SameProject(RepoChoice r, Project p)
        {
            if (Key(r.Path) == Key(p.PathWithNamespace)) return true;
            if (r.CreatedAt is { } made && p.CreatedAt is { } listed) return made == listed;
            if (r.Instance is not null && instance is not null && r.Instance != instance) return false;
            return !(listedPath.TryGetValue(Key(r.Path), out var other) && other != p.GitlabId);
        }

        var match = new Dictionary<long, RepoChoice>();
        var used = new HashSet<long>();
        foreach (var p in projects)
        {
            if (byId.TryGetValue(p.GitlabId, out var r) && !used.Contains(r.GitlabId) && SameProject(r, p))
            {
                match[p.GitlabId] = r;
                used.Add(r.GitlabId);
            }
        }
        // A repository GitLab lists under another id than before (a new token on a GitLab set up
        // anew, a server moved): found by its path, the copy listed last when there are several.
        foreach (var p in projects)
        {
            if (match.ContainsKey(p.GitlabId)) continue;
            var r = rows.Where(x => !used.Contains(x.GitlabId) && Key(x.Path) == Key(p.PathWithNamespace))
                .OrderByDescending(x => x.SeenAt ?? 0).ThenByDescending(x => x.GitlabId).FirstOrDefault();
            if (r is null) continue;
            match[p.GitlabId] = r;
            used.Add(r.GitlabId);
        }

        // Ids no project holds: below every id there is (GitLab's are positive).
        var free = Math.Min(0L, Convert.ToInt64(Sql.Scalar(conn,
            "SELECT MIN(id) FROM (SELECT MIN(gitlab_id) AS id FROM repo_choices UNION ALL SELECT MIN(gitlab_id) FROM repos)") ?? 0L)) - 1;
        // A row whose id GitLab now gives another project (another GitLab's): out of its way, kept as one GitLab no longer lists.
        var aside = rows.Where(r => !used.Contains(r.GitlabId) && listedIds.Contains(r.GitlabId)).ToList();
        foreach (var r in aside) Rekey(conn, r.GitlabId, free--);
        // The moves, through ids nobody holds, so one move never lands on a row another is leaving.
        var moves = match.Where(m => m.Value.GitlabId != m.Key).Select(m => (To: m.Key, From: m.Value.GitlabId, Via: free--)).ToList();
        foreach (var (_, from, via) in moves) Rekey(conn, from, via);
        foreach (var (to, _, via) in moves) Rekey(conn, via, to);

        foreach (var p in projects)
        {
            Sql.Exec(conn, """
                INSERT INTO repo_choices (gitlab_id, path_with_namespace, default_branch, http_url, included, seen_at, instance, created_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(gitlab_id) DO UPDATE SET
                  path_with_namespace = excluded.path_with_namespace,
                  default_branch      = excluded.default_branch,
                  http_url            = excluded.http_url,
                  seen_at             = excluded.seen_at,
                  instance            = COALESCE(excluded.instance, repo_choices.instance),
                  created_at          = COALESCE(excluded.created_at, repo_choices.created_at)
                """, p.GitlabId, p.PathWithNamespace, p.DefaultBranch, p.HttpUrl, include, now, instance, p.CreatedAt);
        }
        SetMeta(conn, ListedAtKey, now.ToString(CultureInfo.InvariantCulture));
        // Permissions cached per token named index rows by the projects they were under.
        if (moves.Count + aside.Count > 0) Sql.Exec(conn, "DELETE FROM acl_cache");
        tx.Commit();
        return new Listing(projects.Count(p => !match.ContainsKey(p.GitlabId)), moves.Count, aside.Count);
    }

    /// <summary>
    /// A repository's row, its index rows and its log under another project id. An index row
    /// already there for the same branch (none, but for a row left without its choice) gives way
    /// to the newer good one: indexed at a commit, most recently.
    /// </summary>
    static void Rekey(SqliteConnection conn, long from, long to)
    {
        var losers = new List<long>();
        foreach (var a in Sql.Query(conn, "SELECT id, branch, last_indexed_sha, last_indexed_at, last_run_at FROM repos WHERE gitlab_id = ?", from))
        {
            if (Sql.One(conn, "SELECT id, branch, last_indexed_sha, last_indexed_at, last_run_at FROM repos WHERE gitlab_id = ? AND branch = ?", to, a.Str("branch")) is not { } b)
                continue;
            losers.Add(Rank(a).CompareTo(Rank(b)) >= 0 ? b.Long("id") : a.Long("id"));
        }
        Writes.DeleteReposIn(conn, losers);
        Sql.Exec(conn, "UPDATE repo_choices SET gitlab_id = ? WHERE gitlab_id = ?", to, from);
        Sql.Exec(conn, "UPDATE repos SET gitlab_id = ? WHERE gitlab_id = ?", to, from);
        Sql.Exec(conn, "UPDATE index_log SET gitlab_id = ? WHERE gitlab_id = ?", to, from);
    }

    static (int, long, long) Rank(Row r) => (r.StrOrNull("last_indexed_sha") is null ? 0 : 1, r.LongOrNull("last_indexed_at") ?? 0, r.LongOrNull("last_run_at") ?? 0);

    public static List<RepoChoice> List(SqliteConnection conn) =>
        [.. Sql.Query(conn, "SELECT * FROM repo_choices ORDER BY path_with_namespace").Select(Of)];

    public static RepoChoice? Find(SqliteConnection conn, long gitlabId) =>
        Sql.One(conn, "SELECT * FROM repo_choices WHERE gitlab_id = ?", gitlabId) is { } row ? Of(row) : null;

    /// <summary>The repository at this path (GitLab's paths, without case), if Argus knows one.</summary>
    public static RepoChoice? FindByPath(SqliteConnection conn, string path) =>
        Sql.One(conn, "SELECT * FROM repo_choices WHERE lower(path_with_namespace) = lower(?) ORDER BY COALESCE(seen_at, 0) DESC LIMIT 1", path) is { } row ? Of(row) : null;

    static RepoChoice Of(Row r) => new(r.Long("gitlab_id"), r.Str("path_with_namespace"), r.Str("default_branch"), r.Str("http_url"),
        r.Long("included") != 0, Split(r.Str("branches")), r.LongOrNull("seen_at"), r.LongOrNull("changed_at"),
        r.Get("instance") as string, r.Get("created_at") as string, r.Get("schedule") as string ?? "", r.Has("scheduled_at") ? r.LongOrNull("scheduled_at") : null);

    public static IReadOnlyList<string> Split(string branches) =>
        [.. branches.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>An admin's choice for one repository; null leaves that part as it was. False when there is no such repository.</summary>
    public static bool Set(SqliteConnection conn, long gitlabId, bool? included, IReadOnlyList<string>? branches, long now)
    {
        if (Find(conn, gitlabId) is not { } current) return false;
        Sql.Exec(conn, "UPDATE repo_choices SET included = ?, branches = ?, changed_at = ? WHERE gitlab_id = ?",
            (included ?? current.Included) ? 1L : 0L, string.Join('\n', branches ?? current.Branches), now, gitlabId);
        return true;
    }

    /// <summary>
    /// A repository's own schedule ("" follows the default). A time of day counts from now; every N
    /// hours counts from its last check (at once, when it was never checked). False when there is no such repository.
    /// </summary>
    public static bool SetSchedule(SqliteConnection conn, long gitlabId, string schedule, long now)
    {
        var effective = schedule.Length > 0 ? RepoSchedule.Parse(schedule) : DefaultSchedule(conn);
        return Sql.Exec(conn, "UPDATE repo_choices SET schedule = ?, scheduled_at = ?, changed_at = ? WHERE gitlab_id = ?",
            schedule, effective.Kind == ScheduleKind.Hours ? null : now, now, gitlabId) > 0;
    }

    /// <summary>The schedule a repository runs on: its own, or the default.</summary>
    public static RepoSchedule Effective(RepoChoice choice, RepoSchedule defaultSchedule) =>
        choice.Schedule.Length > 0 ? RepoSchedule.Parse(choice.Schedule) : defaultSchedule;

    /// <summary>Whether a repository is indexed: its choice, or the policy for one never seen.</summary>
    public static bool Included(SqliteConnection conn, long gitlabId) =>
        Find(conn, gitlabId)?.Included ?? NewReposIncluded(conn);

    /// <summary>Takes a repository out of the index (its files and symbols with it). How many index rows went.</summary>
    public static int Drop(SqliteConnection conn, long gitlabId) =>
        Writes.DeleteRepos(conn, [.. Sql.Query(conn, "SELECT id FROM repos WHERE gitlab_id = ?", gitlabId).Select(r => r.Long("id"))]);

    /// <summary>A repository GitLab no longer lists, forgotten: its row, its index and its log. How many index rows went.</summary>
    public static int Forget(SqliteConnection conn, long gitlabId)
    {
        var removed = Drop(conn, gitlabId);
        Sql.Exec(conn, "DELETE FROM repo_choices WHERE gitlab_id = ?", gitlabId);
        Sql.Exec(conn, "DELETE FROM index_log WHERE gitlab_id = ?", gitlabId);
        return removed;
    }

    /// <summary>When each repository was last checked (the latest run over its branches), by project id.</summary>
    public static Dictionary<long, long> LastChecks(SqliteConnection conn) =>
        Sql.Query(conn, "SELECT gitlab_id, MAX(last_run_at) AS at FROM repos WHERE last_run_at IS NOT NULL GROUP BY gitlab_id")
            .ToDictionary(r => r.Long("gitlab_id"), r => r.Long("at"));

    /// <summary>
    /// When a repository's schedule next starts a run of it; null when Argus's scheduler does not run it.
    /// Every N hours: N hours after its last check or the schedule's last start, whichever is later
    /// (now, when neither ever happened). A time of day: the first one after the schedule's last start
    /// (or its last check, before the schedule ever ran it; or now).
    /// </summary>
    public static DateTimeOffset? NextRun(RepoChoice choice, RepoSchedule schedule, TimeZoneInfo zone, long? lastCheck, long now)
    {
        if (!choice.Included || !schedule.Timed) return null;
        DateTimeOffset At(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (schedule.Kind == ScheduleKind.Hours)
            return Math.Max(lastCheck ?? long.MinValue, choice.ScheduledAt ?? long.MinValue) is var from && from == long.MinValue
                ? At(now)
                : At(from) + TimeSpan.FromHours(schedule.Hours);
        return schedule.Next(At(choice.ScheduledAt ?? lastCheck ?? now), null, zone);
    }

    /// <summary>The indexed repositories whose own schedule (or the default) is due by <paramref name="now"/>.</summary>
    public static List<RepoChoice> Due(SqliteConnection conn, long now)
    {
        var fallback = DefaultSchedule(conn);
        var (zone, _) = RepoSchedule.Zone(ScheduleZone(conn));
        var checks = LastChecks(conn);
        var due = new List<RepoChoice>();
        foreach (var c in List(conn))
        {
            var schedule = Effective(c, fallback);
            if (!c.Included || !schedule.Timed) continue;
            // A time of day with nothing to count from yet (never checked, never run by a schedule): from now.
            if (c.ScheduledAt is null && !checks.ContainsKey(c.GitlabId) && schedule.Kind != ScheduleKind.Hours)
            {
                MarkScheduled(conn, [c.GitlabId], now);
                continue;
            }
            if (NextRun(c, schedule, zone, checks.TryGetValue(c.GitlabId, out var at) ? at : null, now) is { } next && next.ToUnixTimeSeconds() <= now)
                due.Add(c);
        }
        return due;
    }

    /// <summary>The schedule started (or skipped) a run of these: their next time counts from now.</summary>
    public static void MarkScheduled(SqliteConnection conn, IReadOnlyList<long> gitlabIds, long now)
    {
        if (gitlabIds.Count == 0) return;
        Sql.ExecList(conn, $"UPDATE repo_choices SET scheduled_at = ? WHERE gitlab_id IN ({Sql.Marks(gitlabIds.Count)})",
            [now, .. gitlabIds.Cast<object?>()]);
    }
}
