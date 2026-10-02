using Argus.Indexing;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Store;

/// <summary>A repository GitLab listed, and what the admin chose for it.</summary>
public sealed record RepoChoice(long GitlabId, string Path, string DefaultBranch, string HttpUrl, bool Included, IReadOnlyList<string> Branches,
    long? SeenAt, long? ChangedAt);

/// <summary>
/// Which repositories are indexed, and which branches besides each one's default
/// (repo_choices). Every repository GitLab lists gets a row; a new one is in or out
/// as `index.new_repos` says (in, unless set otherwise).
/// </summary>
public static class Choices
{
    public const string NewReposKey = "index.new_repos";

    /// <summary>Whether a repository GitLab lists for the first time is indexed.</summary>
    public static bool NewReposIncluded(SqliteConnection conn) =>
        Sql.One(conn, "SELECT value FROM argus_meta WHERE key = ?", NewReposKey)?.Str("value") != "exclude";

    public static void SetNewReposIncluded(SqliteConnection conn, bool included) =>
        Sql.Exec(conn, "INSERT INTO argus_meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            NewReposKey, included ? "include" : "exclude");

    /// <summary>What GitLab lists now: new repositories get a row (in or out by the policy), known ones their current name and default branch.</summary>
    public static void Record(SqliteConnection conn, IEnumerable<Project> projects, long now)
    {
        var include = NewReposIncluded(conn) ? 1L : 0L;
        using var tx = conn.BeginTransaction();
        foreach (var p in projects)
        {
            Sql.Exec(conn, """
                INSERT INTO repo_choices (gitlab_id, path_with_namespace, default_branch, http_url, included, seen_at)
                VALUES (?, ?, ?, ?, ?, ?)
                ON CONFLICT(gitlab_id) DO UPDATE SET
                  path_with_namespace = excluded.path_with_namespace,
                  default_branch      = excluded.default_branch,
                  http_url            = excluded.http_url,
                  seen_at             = excluded.seen_at
                """, p.GitlabId, p.PathWithNamespace, p.DefaultBranch, p.HttpUrl, include, now);
        }
        tx.Commit();
    }

    public static List<RepoChoice> List(SqliteConnection conn) =>
        [.. Sql.Query(conn, "SELECT * FROM repo_choices ORDER BY path_with_namespace").Select(Of)];

    public static RepoChoice? Find(SqliteConnection conn, long gitlabId) =>
        Sql.One(conn, "SELECT * FROM repo_choices WHERE gitlab_id = ?", gitlabId) is { } row ? Of(row) : null;

    static RepoChoice Of(Row r) => new(r.Long("gitlab_id"), r.Str("path_with_namespace"), r.Str("default_branch"), r.Str("http_url"),
        r.Long("included") != 0, Split(r.Str("branches")), r.LongOrNull("seen_at"), r.LongOrNull("changed_at"));

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

    /// <summary>Whether a repository is indexed: its choice, or the policy for one never seen.</summary>
    public static bool Included(SqliteConnection conn, long gitlabId) =>
        Find(conn, gitlabId)?.Included ?? NewReposIncluded(conn);

    /// <summary>Takes a repository out of the index (its files and symbols with it). How many index rows went.</summary>
    public static int Drop(SqliteConnection conn, long gitlabId) =>
        Writes.DeleteRepos(conn, [.. Sql.Query(conn, "SELECT id FROM repos WHERE gitlab_id = ?", gitlabId).Select(r => r.Long("id"))]);
}
