using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Server;

/// <summary>
/// Admin → Indexing → repositories: what GitLab lists and what is chosen for the index, each
/// repository's state (and how far a run is with it), schedule, branches and log; changing
/// one or many at once; updating them now.
/// </summary>
public static partial class ArgusServer
{
    static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Most repositories one batch changes.</summary>
    public const int BatchLimit = 5000;

    /// <summary>What each language is called on screen.</summary>
    static readonly Dictionary<string, string> LanguageNames = new(StringComparer.Ordinal)
    {
        ["c"] = "C", ["cpp"] = "C++", ["python"] = "Python", ["csharp"] = "C#", ["typescript"] = "TypeScript",
        ["javascript"] = "JavaScript", ["markdown"] = "Markdown", ["text"] = "Text",
    };

    /// <summary>
    /// A repository's state in one word: indexing or queued (now), off (left out), failed (its last
    /// run of a branch failed), never (no branch indexed yet), stale (not checked for longer than its
    /// schedule allows), indexed.
    /// </summary>
    static string StateOf(RepoChoice c, JsonArray indexed, JsonObject? progress, bool waiting)
    {
        if (progress?["state"]?.ToString() is "fetching" or "files" or "symbols" or "embedding") return "indexing";
        if (waiting || progress?["state"]?.ToString() == "queued") return "queued";
        if (!c.Included) return "off";
        var branches = indexed.OfType<JsonObject>().ToList();
        if (branches.Any(b => b["error"] is not null || b["timed_out"]?.GetValue<bool>() == true || b["symbols_failed"]?.GetValue<bool>() == true)) return "failed";
        if (!branches.Any(b => b["sha"] is not null)) return "never";
        if (branches.Any(b => b["stale"]?.GetValue<bool>() == true)) return "stale";
        return "indexed";
    }

    /// <summary>Every repository GitLab listed, what is chosen for it, its state, schedule and languages, and its indexed branches with their commits.</summary>
    static JsonObject ReposView(ArgusConfig cfg, Jobs jobs)
    {
        using var conn = Db.Open(cfg.Index.DbPath);
        var counts = new Dictionary<long, (long Files, long Symbols)>();
        foreach (var row in Sql.Query(conn, "SELECT repo_id, COUNT(*) AS n FROM files GROUP BY repo_id"))
            counts[row.Long("repo_id")] = (row.Long("n"), 0);
        foreach (var row in Sql.Query(conn, "SELECT repo_id, COUNT(*) AS n FROM symbols GROUP BY repo_id"))
            counts[row.Long("repo_id")] = (counts.GetValueOrDefault(row.Long("repo_id")).Files, row.Long("n"));
        var languages = Sql.Query(conn,
                "SELECT r.gitlab_id, f.lang, COUNT(*) AS n FROM files f JOIN repos r ON r.id = f.repo_id" +
                " WHERE r.branch = r.default_branch AND f.lang IS NOT NULL GROUP BY r.gitlab_id, f.lang")
            .GroupBy(r => r.Long("gitlab_id"))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Long("n")).ThenBy(r => r.Str("lang"), StringComparer.Ordinal).ToList());
        var staleAfter = Metrics.StaleAfter();
        var now = NowSeconds();
        var fallback = Choices.DefaultSchedule(conn);
        var zoneName = Choices.ScheduleZone(conn);
        var (zone, zoneProblem) = RepoSchedule.Zone(zoneName);
        var listedAt = Choices.ListedAt(conn);
        var choices = Choices.List(conn);
        var scheduleOf = choices.ToDictionary(c => c.GitlabId, c => Choices.Effective(c, fallback));
        var indexed = Sql.Query(conn,
                "SELECT id, gitlab_id, branch, default_branch, last_indexed_sha, last_indexed_message, last_indexed_commit_at," +
                "       last_indexed_at, last_run_at, last_run_timed_out, last_run_symbols_failed, last_run_error" +
                "  FROM repos ORDER BY (branch = default_branch) DESC, branch")
            .GroupBy(r => r.Long("gitlab_id"))
            .ToDictionary(g => g.Key, g => new JsonArray([.. g.Select(r =>
            {
                var (files, symbols) = counts.GetValueOrDefault(r.Long("id"));
                var lastRun = r.LongOrNull("last_run_at");
                var allowed = Metrics.StaleLimit(scheduleOf.GetValueOrDefault(g.Key, fallback), staleAfter);
                return (JsonNode)new JsonObject
                {
                    ["branch"] = r.Str("branch"), ["default"] = r.Str("branch") == r.Str("default_branch"),
                    ["sha"] = r.StrOrNull("last_indexed_sha"), ["message"] = r.StrOrNull("last_indexed_message"),
                    ["committed_at"] = r.LongOrNull("last_indexed_commit_at"), ["indexed_at"] = r.LongOrNull("last_indexed_at"),
                    ["last_run_at"] = lastRun, ["stale"] = lastRun is null || (allowed is { } a && now - lastRun > a),
                    ["timed_out"] = r.Long("last_run_timed_out") != 0, ["symbols_failed"] = r.Long("last_run_symbols_failed") != 0,
                    ["error"] = r.StrOrNull("last_run_error"), ["files"] = files, ["symbols"] = symbols,
                };
            })]));

        var job = jobs.IndexJobSnapshot();
        var running = job["state"]?.ToString() == "running";
        var byRepo = running ? (job["progress"] as JsonObject)?["by_repo"] as JsonObject : null;
        var pending = (job["pending"] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var repos = new JsonArray();
        foreach (var c in choices)
        {
            var branches = indexed.GetValueOrDefault(c.GitlabId) ?? new JsonArray();
            var schedule = scheduleOf[c.GitlabId];
            var lastRun = branches.OfType<JsonObject>().Select(b => b["last_run_at"]?.GetValue<long?>()).Max();
            var progress = byRepo?[c.Path] as JsonObject;
            var langs = languages.GetValueOrDefault(c.GitlabId) ?? [];
            var problem = branches.OfType<JsonObject>().Select(b => b["error"]?.ToString()).FirstOrDefault(e => e is not null);
            repos.Add(new JsonObject
            {
                ["gitlab_id"] = c.GitlabId, ["repo"] = c.Path, ["name"] = c.Name, ["group"] = c.Group,
                ["default_branch"] = c.DefaultBranch, ["included"] = c.Included,
                ["branches"] = new JsonArray([.. c.Branches.Select(b => (JsonNode)b)]), ["seen_at"] = c.SeenAt, ["changed_at"] = c.ChangedAt,
                // GitLab did not list it the last time it was asked (a token that cannot see it, a repository moved away or deleted).
                ["listed"] = listedAt is null || c.SeenAt is null || c.SeenAt >= listedAt,
                ["languages"] = new JsonArray([.. langs.Take(4).Select(l => (JsonNode)new JsonObject
                {
                    ["lang"] = l.Str("lang"), ["name"] = LanguageNames.GetValueOrDefault(l.Str("lang"), l.Str("lang")), ["files"] = l.Long("n"),
                })]),
                ["language"] = langs.Select(l => l.Str("lang")).FirstOrDefault(l => l is not ("markdown" or "text")) is { } main
                    ? LanguageNames.GetValueOrDefault(main, main) : null,
                ["schedule"] = c.Schedule, ["schedule_words"] = schedule.Words, ["schedule_kind"] = schedule.Kind.ToString().ToLowerInvariant(),
                ["next_run_at"] = Choices.NextRun(c, schedule, zone, lastRun, now)?.ToUnixTimeSeconds() is { } next ? Math.Max(next, now) : null,
                ["last_run_at"] = lastRun,
                ["state"] = StateOf(c, branches, progress, pending.Contains(c.Path)),
                ["problem"] = problem,
                ["progress"] = progress?.DeepClone(),
                ["indexed"] = branches,
            });
        }
        return new JsonObject
        {
            ["new_repos"] = Choices.NewReposIncluded(conn) ? "include" : "exclude",
            ["global_branches"] = new JsonArray([.. cfg.Index.Branches.Select(b => (JsonNode)b)]),
            ["schedule"] = new JsonObject
            {
                ["default"] = fallback.ToString(), ["words"] = fallback.Words, ["time_zone"] = zoneName, ["zone_problem"] = zoneProblem,
                // Argus's own pass timer, when it is on (in the platform, the app's schedule starts the passes).
                ["pass_interval"] = Jobs.IndexInterval(), ["next_pass_at"] = jobs.NextPassAt,
            },
            ["listed_at"] = listedAt,
            ["running"] = running,
            ["pending"] = new JsonArray([.. pending.Select(p => (JsonNode)p)]),
            ["repos"] = repos,
        };
    }

    /// <summary>A schedule as sent: "" (the default; for a repository only), or one <see cref="RepoSchedule"/> reads.</summary>
    static bool ReadSchedule(JsonNode? node, bool allowDefault, out string schedule, out string? problem)
    {
        schedule = (node?.ToString() ?? "").Trim().ToLowerInvariant();
        problem = null;
        if (schedule.Length == 0 && allowDefault) return true;
        if (!RepoSchedule.TryParse(schedule, out var parsed, out problem)) return false;
        schedule = parsed.ToString();
        return true;
    }

    /// <summary>One repository in or out of the index: out leaves the index now, unless a run is writing (then at the next one). The index rows removed.</summary>
    static (int Removed, bool Deferred) Include(SqliteConnection conn, Jobs jobs, RepoChoice choice, bool included, long now)
    {
        Choices.Set(conn, choice.GitlabId, included, null, now);
        if (included == choice.Included) return (0, false);
        RepoLog.Note(conn, choice.GitlabId, included ? "An admin chose it for the index: the next run indexes it." : "An admin left it out of the index.");
        if (included) return (0, false);
        if (jobs.IndexJobSnapshot()["state"]?.ToString() == "running") return (0, true);
        var removed = Choices.Drop(conn, choice.GitlabId);
        if (removed > 0) RepoLog.Note(conn, choice.GitlabId, "Its files and symbols were removed from the index.");
        return (removed, false);
    }

    static void MapRepoAdmin(WebApplication app, ArgusConfig cfg, Jobs jobs)
    {
        app.MapGet(AdminPrefix + "repos", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            try { return Json(ReposView(cfg, jobs)); }
            catch (Exception exc) { return Json(new JsonObject { ["error"] = Short(exc) }, 500); }
        });

        // Ask GitLab now (an index pass does too): new repositories appear, renamed ones get their names,
        // and one GitLab lists under a new id (another token, a GitLab set up anew) is found again, not copied.
        app.MapPost(AdminPrefix + "repos/discover", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            try
            {
                var projects = GitLab.ListProjects(cfg.GitLab);
                Listing listing;
                using (var conn = Db.Open(cfg.Index.DbPath)) listing = Choices.Record(conn, projects, NowSeconds(), GitLab.Instance(cfg.GitLab.Url));
                AuditLog.Event("repos_discovered", new JsonObject { ["count"] = projects.Count, ["new"] = listing.New, ["moved"] = listing.Moved, ["set_aside"] = listing.SetAside });
                var view = ReposView(cfg, jobs);
                view["found"] = new JsonObject { ["new"] = listing.New, ["moved"] = listing.Moved, ["set_aside"] = listing.SetAside };
                return Json(view);
            }
            catch (Exception exc) when (exc is GitLabError or HttpRequestException or TaskCanceledException or CredentialError)
            {
                return Json(new JsonObject { ["error"] = $"GitLab did not list the repositories: {exc.Message}" }, 502);
            }
        });

        // In or out of the index, the branches indexed besides the default one, and its own schedule.
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
            string? schedule = null;
            if (body.ContainsKey("schedule"))
            {
                if (!ReadSchedule(body["schedule"], true, out var s, out var problem)) return Json(new JsonObject { ["error"] = problem }, 400);
                schedule = s;
            }
            using var conn = Db.Open(cfg.Index.DbPath);
            if (Choices.Find(conn, gitlabId) is not { } before) return Json(new JsonObject { ["error"] = "no such repository" }, 404);
            var now = NowSeconds();
            if (branches is not null)
            {
                Choices.Set(conn, gitlabId, null, branches, now);
                if (!branches.SequenceEqual(before.Branches))
                    RepoLog.Note(conn, gitlabId, branches.Count == 0 ? "An admin chose to index only its default branch." : $"An admin chose its branches: {string.Join(", ", branches)}.");
            }
            if (schedule is not null && schedule != before.Schedule)
            {
                Choices.SetSchedule(conn, gitlabId, schedule, now);
                var words = schedule.Length > 0 ? RepoSchedule.Parse(schedule).Words : $"the default ({Choices.DefaultSchedule(conn).Words.ToLowerInvariant()})";
                RepoLog.Note(conn, gitlabId, $"An admin set its schedule: {words.ToLowerInvariant()}.");
            }
            var (removed, deferred) = included is { } i ? Include(conn, jobs, before, i, now) : (0, false);
            var choice = Choices.Find(conn, gitlabId)!;
            AuditLog.Event("repo_choice", new JsonObject
            {
                ["repo"] = choice.Path, ["included"] = choice.Included, ["branches"] = new JsonArray([.. choice.Branches.Select(b => (JsonNode)b)]),
                ["schedule"] = choice.Schedule,
            });
            return Json(new JsonObject
            {
                ["status"] = "saved", ["repo"] = choice.Path, ["included"] = choice.Included, ["removed"] = removed, ["deferred"] = deferred,
                ["schedule"] = choice.Schedule,
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
            RepoLog.Note(conn, gitlabId, leaveOut
                ? "An admin removed its index and left it out: its files and symbols are gone."
                : "An admin removed its index to build it anew: the next run reads it from the start.");
            AuditLog.Event("repo_index_removed", new JsonObject { ["repo"] = choice.Path, ["left_out"] = leaveOut, ["rows"] = removed });
            return Json(new JsonObject { ["status"] = "removed", ["repo"] = choice.Path, ["removed"] = removed, ["included"] = !leaveOut && choice.Included });
        });

        // A repository's log: its last runs, oldest line first, in sentences.
        app.MapGet(AdminPrefix + "repos/{gitlabId:long}/log", (long gitlabId, HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var runs = int.TryParse(request.Query["runs"].ToString(), out var n) ? n : 5;
            using var conn = Db.Open(cfg.Index.DbPath);
            if (Choices.Find(conn, gitlabId) is not { } choice) return Json(new JsonObject { ["error"] = "no such repository" }, 404);
            var job = jobs.IndexJobSnapshot();
            var progress = job["state"]?.ToString() == "running" ? ((job["progress"] as JsonObject)?["by_repo"] as JsonObject)?[choice.Path] : null;
            return Json(new JsonObject
            {
                ["gitlab_id"] = gitlabId, ["repo"] = choice.Path,
                ["lines"] = new JsonArray([.. RepoLog.Read(conn, gitlabId, runs).Select(l => (JsonNode)RepoLog.Json(l))]),
                ["progress"] = progress?.DeepClone(),
            });
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

        // Whether a repository GitLab lists for the first time is indexed; the schedule of every
        // repository without one of its own, and the time zone schedules are in.
        app.MapPut(AdminPrefix + "repos/settings", async (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var policy = body["new_repos"]?.ToString();
            if (policy is not (null or "include" or "exclude")) return Json(new JsonObject { ["error"] = "new_repos is include or exclude" }, 400);
            string? schedule = null;
            if (body["schedule"] is not null)
            {
                if (!ReadSchedule(body["schedule"], false, out var s, out var problem)) return Json(new JsonObject { ["error"] = problem }, 400);
                schedule = s;
            }
            string? zone = null;
            if (body["schedule_tz"] is not null)
            {
                zone = body["schedule_tz"]!.ToString().Trim();
                if (RepoSchedule.Zone(zone).Problem is { } problem) return Json(new JsonObject { ["error"] = problem }, 400);
                if (zone.Length == 0) zone = "UTC";
            }
            using (var conn = Db.Open(cfg.Index.DbPath))
            {
                if (policy is not null) Choices.SetNewReposIncluded(conn, policy == "include");
                if (zone is not null) Choices.SetScheduleZone(conn, zone);
                if (schedule is not null && schedule != Choices.DefaultSchedule(conn).ToString())
                    Choices.SetDefaultSchedule(conn, RepoSchedule.Parse(schedule), NowSeconds());
            }
            AuditLog.Event("repo_policy", new JsonObject { ["new_repos"] = policy, ["schedule"] = schedule, ["schedule_tz"] = zone });
            return Json(new JsonObject { ["status"] = "saved", ["new_repos"] = policy, ["schedule"] = schedule, ["schedule_tz"] = zone });
        });

        // Several repositories at once: in or out, updated now, a schedule, more branches, or removed.
        // Each one's outcome in a sentence.
        app.MapPost(AdminPrefix + "repos/batch", async (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            var body = await BodyOrEmpty(request);
            var action = body["action"]?.ToString() ?? "";
            var ids = (body["ids"] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<long>(out var id) ? id : (long?)null).OfType<long>().Distinct().ToList();
            if (ids.Count == 0) return Json(new JsonObject { ["error"] = "choose at least one repository (ids)" }, 400);
            if (ids.Count > BatchLimit) return Json(new JsonObject { ["error"] = $"at most {BatchLimit} repositories at once" }, 400);
            string schedule = "";
            List<string> patterns = [];
            switch (action)
            {
                case "include" or "exclude" or "reindex" or "remove":
                    break;
                case "schedule":
                    if (!ReadSchedule(body["schedule"], true, out schedule, out var problem)) return Json(new JsonObject { ["error"] = problem }, 400);
                    break;
                case "add_branches":
                    patterns = [.. (body["branches"] as JsonArray ?? []).Select(b => b?.ToString().Trim() ?? "").Where(b => b.Length > 0).Distinct(StringComparer.Ordinal)];
                    if (patterns.Count == 0) return Json(new JsonObject { ["error"] = "give the branches or patterns to add" }, 400);
                    if (patterns.FirstOrDefault(b => b.Length > 200 || b.Contains('\n')) is { } bad) return Json(new JsonObject { ["error"] = $"not a branch name or pattern: {bad}" }, 400);
                    break;
                default:
                    return Json(new JsonObject { ["error"] = "action is include, exclude, reindex, schedule, add_branches or remove" }, 400);
            }
            var now = NowSeconds();
            var results = new JsonArray();
            void Result(long id, string? repo, bool ok, string message) =>
                results.Add(new JsonObject { ["gitlab_id"] = id, ["repo"] = repo, ["ok"] = ok, ["message"] = message });
            var running = jobs.IndexJobSnapshot()["state"]?.ToString() == "running";
            using var conn = Db.Open(cfg.Index.DbPath);
            var listedAt = Choices.ListedAt(conn);
            var toRun = new List<RepoChoice>();
            foreach (var id in ids)
            {
                if (Choices.Find(conn, id) is not { } c)
                {
                    Result(id, null, false, "No such repository: refresh the list.");
                    continue;
                }
                switch (action)
                {
                    case "include":
                        Include(conn, jobs, c, true, now);
                        Result(id, c.Path, true, c.Included ? "Already indexed." : "Chosen: the next run indexes it, or Update now.");
                        break;
                    case "exclude":
                        var (removed, deferred) = Include(conn, jobs, c, false, now);
                        Result(id, c.Path, true, !c.Included ? "Already left out." : deferred ? "Left out: its index goes when the run going now ends." : $"Left out: {RepoLog.Count(removed, "branch", "branches")} removed from the index.");
                        break;
                    case "reindex":
                        if (!c.Included) Result(id, c.Path, false, "Not indexed: choose it for the index first.");
                        else if (listedAt is not null && c.SeenAt < listedAt) Result(id, c.Path, false, "GitLab no longer lists it, so it cannot be fetched.");
                        else toRun.Add(c);
                        break;
                    case "schedule":
                        Choices.SetSchedule(conn, id, schedule, now);
                        var words = schedule.Length > 0 ? RepoSchedule.Parse(schedule).Words : $"the default ({Choices.DefaultSchedule(conn).Words.ToLowerInvariant()})";
                        RepoLog.Note(conn, id, $"An admin set its schedule: {words.ToLowerInvariant()}.");
                        Result(id, c.Path, true, $"Schedule: {words}.");
                        break;
                    case "add_branches":
                        var added = patterns.Where(p => !c.Branches.Contains(p, StringComparer.Ordinal)).ToList();
                        if (c.Branches.Count + added.Count > 50) { Result(id, c.Path, false, "At most 50 branches or patterns per repository."); break; }
                        Choices.Set(conn, id, null, [.. c.Branches, .. added], now);
                        if (added.Count > 0) RepoLog.Note(conn, id, $"An admin added branches to index: {string.Join(", ", added)}.");
                        Result(id, c.Path, true, added.Count > 0 ? $"Also indexes {string.Join(", ", added)} from the next run." : "Had them already.");
                        break;
                    case "remove":
                        if (running) { Result(id, c.Path, false, "A run is going: remove it when the run ends."); break; }
                        var listed = listedAt is null || c.SeenAt is null || c.SeenAt >= listedAt;
                        if (listed)
                        {
                            Choices.Set(conn, id, false, null, now);
                            var rows = Choices.Drop(conn, id);
                            RepoLog.Note(conn, id, "An admin removed its index and left it out: its files and symbols are gone.");
                            Result(id, c.Path, true, $"Removed from the index ({RepoLog.Count(rows, "branch", "branches")}) and left out.");
                        }
                        else
                        {
                            var rows = Choices.Forget(conn, id);
                            Result(id, c.Path, true, $"Forgotten: GitLab no longer lists it ({RepoLog.Count(rows, "branch", "branches")} removed from the index).");
                        }
                        break;
                }
            }
            if (toRun.Count > 0)
            {
                var outcome = jobs.EnqueueRepos([.. toRun.Select(c => c.Path)], "manual");
                foreach (var c in toRun)
                    Result(c.GitlabId, c.Path, outcome.GetValueOrDefault(c.Path) != "refused", outcome.GetValueOrDefault(c.Path) switch
                    {
                        "started" => "Updating now.",
                        "queued" => "Queued: it is updated when the run going now ends.",
                        "already_queued" => "Already waiting its turn.",
                        _ => "Not queued: too many repositories are waiting. Try again when the run ends.",
                    });
            }
            AuditLog.Event("repos_batch", new JsonObject
            {
                ["action"] = action, ["count"] = ids.Count, ["ok"] = results.Count(r => r?["ok"]?.GetValue<bool>() == true),
                ["schedule"] = action == "schedule" ? schedule : null, ["branches"] = action == "add_branches" ? new JsonArray([.. patterns.Select(p => (JsonNode)p)]) : null,
            });
            return Json(new JsonObject { ["action"] = action, ["results"] = results });
        });
    }
}
