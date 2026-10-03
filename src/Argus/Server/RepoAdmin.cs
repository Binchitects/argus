using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;

namespace Argus.Server;

/// <summary>
/// Admin → Indexing → repositories: what GitLab lists and what is chosen for the index,
/// each repository's branches (indexed, and as GitLab has them now), and updating one.
/// </summary>
public static partial class ArgusServer
{
    static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Every repository GitLab listed, what is chosen for it, and its indexed branches with their commits.</summary>
    static JsonObject ReposView(ArgusConfig cfg)
    {
        using var conn = Db.Open(cfg.Index.DbPath);
        var counts = new Dictionary<long, (long Files, long Symbols)>();
        foreach (var row in Sql.Query(conn, "SELECT repo_id, COUNT(*) AS n FROM files GROUP BY repo_id"))
            counts[row.Long("repo_id")] = (row.Long("n"), 0);
        foreach (var row in Sql.Query(conn, "SELECT repo_id, COUNT(*) AS n FROM symbols GROUP BY repo_id"))
            counts[row.Long("repo_id")] = (counts.GetValueOrDefault(row.Long("repo_id")).Files, row.Long("n"));
        var staleAfter = Metrics.StaleAfter();
        var now = NowSeconds();
        var indexed = Sql.Query(conn,
                "SELECT id, gitlab_id, branch, default_branch, last_indexed_sha, last_indexed_message, last_indexed_commit_at," +
                "       last_indexed_at, last_run_at, last_run_timed_out, last_run_symbols_failed, last_run_error" +
                "  FROM repos ORDER BY (branch = default_branch) DESC, branch")
            .GroupBy(r => r.Long("gitlab_id"))
            .ToDictionary(g => g.Key, g => new JsonArray([.. g.Select(r =>
            {
                var (files, symbols) = counts.GetValueOrDefault(r.Long("id"));
                var lastRun = r.LongOrNull("last_run_at");
                return (JsonNode)new JsonObject
                {
                    ["branch"] = r.Str("branch"), ["default"] = r.Str("branch") == r.Str("default_branch"),
                    ["sha"] = r.StrOrNull("last_indexed_sha"), ["message"] = r.StrOrNull("last_indexed_message"),
                    ["committed_at"] = r.LongOrNull("last_indexed_commit_at"), ["indexed_at"] = r.LongOrNull("last_indexed_at"),
                    ["last_run_at"] = lastRun, ["stale"] = lastRun is null || now - lastRun > staleAfter,
                    ["timed_out"] = r.Long("last_run_timed_out") != 0, ["symbols_failed"] = r.Long("last_run_symbols_failed") != 0,
                    ["error"] = r.StrOrNull("last_run_error"), ["files"] = files, ["symbols"] = symbols,
                };
            })]));
        var repos = new JsonArray();
        foreach (var c in Choices.List(conn))
        {
            repos.Add(new JsonObject
            {
                ["gitlab_id"] = c.GitlabId, ["repo"] = c.Path, ["default_branch"] = c.DefaultBranch, ["included"] = c.Included,
                ["branches"] = new JsonArray([.. c.Branches.Select(b => (JsonNode)b)]), ["seen_at"] = c.SeenAt, ["changed_at"] = c.ChangedAt,
                ["indexed"] = indexed.GetValueOrDefault(c.GitlabId) ?? new JsonArray(),
            });
        }
        return new JsonObject
        {
            ["new_repos"] = Choices.NewReposIncluded(conn) ? "include" : "exclude",
            ["global_branches"] = new JsonArray([.. cfg.Index.Branches.Select(b => (JsonNode)b)]),
            ["repos"] = repos,
        };
    }

    static void MapRepoAdmin(WebApplication app, ArgusConfig cfg, Jobs jobs)
    {
        app.MapGet(AdminPrefix + "repos", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            try { return Json(ReposView(cfg)); }
            catch (Exception exc) { return Json(new JsonObject { ["error"] = Short(exc) }, 500); }
        });

        // Ask GitLab now (an index pass does too): new repositories appear, renamed ones get their names.
        app.MapPost(AdminPrefix + "repos/discover", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            try
            {
                var projects = GitLab.ListProjects(cfg.GitLab);
                using (var conn = Db.Open(cfg.Index.DbPath)) Choices.Record(conn, projects, NowSeconds());
                AuditLog.Event("repos_discovered", new JsonObject { ["count"] = projects.Count });
                return Json(ReposView(cfg));
            }
            catch (Exception exc) when (exc is GitLabError or HttpRequestException or TaskCanceledException or CredentialError)
            {
                return Json(new JsonObject { ["error"] = $"GitLab did not list the repositories: {exc.Message}" }, 502);
            }
        });

        // In or out of the index, and the branches indexed besides the default one.
        app.MapMethods(AdminPrefix + "repos/{gitlabId:long}", ["PATCH"], async (long gitlabId, HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            bool? included = body["included"] is JsonValue v && v.TryGetValue<bool>(out var inc) ? inc : null;
            List<string>? branches = body["branches"] is JsonArray list
                ? [.. list.Select(b => b?.ToString().Trim() ?? "").Where(b => b.Length > 0).Distinct(StringComparer.Ordinal)]
                : null;
            if (branches is { Count: > 50 }) return Json(new JsonObject { ["error"] = "at most 50 branches or patterns per repository" }, 400);
            if (branches?.FirstOrDefault(b => b.Length > 200 || b.Contains('\n')) is { } bad) return Json(new JsonObject { ["error"] = $"not a branch name or pattern: {bad}" }, 400);
            using var conn = Db.Open(cfg.Index.DbPath);
            if (!Choices.Set(conn, gitlabId, included, branches, NowSeconds())) return Json(new JsonObject { ["error"] = "no such repository" }, 404);
            var choice = Choices.Find(conn, gitlabId)!;
            // Out: it leaves the index now, unless a pass is writing (then at the next one).
            var removed = 0;
            var running = jobs.IndexJobSnapshot()["state"]?.ToString() == "running";
            if (!choice.Included && !running) removed = Choices.Drop(conn, gitlabId);
            AuditLog.Event("repo_choice", new JsonObject
            {
                ["repo"] = choice.Path, ["included"] = choice.Included, ["branches"] = new JsonArray([.. choice.Branches.Select(b => (JsonNode)b)]),
            });
            return Json(new JsonObject
            {
                ["status"] = "saved", ["repo"] = choice.Path, ["included"] = choice.Included, ["removed"] = removed,
                ["deferred"] = !choice.Included && running,
            });
        });

        // Its index removed now (every branch's files, symbols and chunks): left out of the
        // index too, or kept in, to be built anew by the next pass or an update.
        app.MapPost(AdminPrefix + "repos/{gitlabId:long}/index/remove", async (long gitlabId, HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var leaveOut = body["leave_out"] is JsonValue v && v.TryGetValue<bool>(out var l) && l;
            if (jobs.IndexJobSnapshot()["state"]?.ToString() == "running")
            {
                return Json(new JsonObject { ["error"] = "an index pass is running: remove the index when it ends" }, 409);
            }
            using var conn = Db.Open(cfg.Index.DbPath);
            if (Choices.Find(conn, gitlabId) is not { } choice) return Json(new JsonObject { ["error"] = "no such repository" }, 404);
            if (leaveOut) Choices.Set(conn, gitlabId, false, null, NowSeconds());
            var removed = Choices.Drop(conn, gitlabId);
            AuditLog.Event("repo_index_removed", new JsonObject { ["repo"] = choice.Path, ["left_out"] = leaveOut, ["rows"] = removed });
            return Json(new JsonObject { ["status"] = "removed", ["repo"] = choice.Path, ["removed"] = removed, ["included"] = !leaveOut && choice.Included });
        });

        // A repository's branches as GitLab has them now, with each one's latest commit.
        app.MapGet(AdminPrefix + "repos/{gitlabId:long}/branches", (long gitlabId, HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            try
            {
                var branches = GitLab.ListBranches(cfg.GitLab, gitlabId);
                return Json(new JsonArray([.. branches.OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name, StringComparer.Ordinal).Select(b => (JsonNode)new JsonObject
                {
                    ["name"] = b.Name, ["sha"] = b.Sha, ["message"] = b.Title, ["committed_at"] = b.CommittedAt, ["default"] = b.IsDefault, ["protected"] = b.IsProtected,
                })]));
            }
            catch (Exception exc) when (exc is GitLabError or HttpRequestException or TaskCanceledException or CredentialError)
            {
                return Json(new JsonObject { ["error"] = $"GitLab did not list the branches: {exc.Message}" }, 502);
            }
        });

        // Whether a repository GitLab lists for the first time is indexed.
        app.MapPut(AdminPrefix + "repos/settings", async (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var policy = body["new_repos"]?.ToString();
            if (policy is not ("include" or "exclude")) return Json(new JsonObject { ["error"] = "new_repos is include or exclude" }, 400);
            using (var conn = Db.Open(cfg.Index.DbPath)) Choices.SetNewReposIncluded(conn, policy == "include");
            AuditLog.Event("repo_policy", new JsonObject { ["new_repos"] = policy });
            return Json(new JsonObject { ["status"] = "saved", ["new_repos"] = policy });
        });
    }
}
