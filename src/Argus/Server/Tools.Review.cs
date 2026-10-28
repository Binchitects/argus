using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.Access;
using Argus.CodeHosts;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;

namespace Argus.Server;

/// <summary>GitLab could not answer; the message says so, and that the call is not the agent's to fix.</summary>
public sealed class HostUnavailable(string message, Exception? inner = null) : ToolError(message, inner);

/// <summary>
/// The merge request and commit tools: a project's merge requests, their diffs, versions, commits,
/// discussions and CI, comparisons, and its files and text at any commit. Each one first checks that the
/// person may read the project in GitLab (a member at Reporter or above), whether Argus indexes it or not,
/// and only then asks GitLab or the project's mirror. GitLab is read with Argus's read-only service
/// credential; nothing here writes to GitLab.
/// </summary>
public sealed partial class Tools
{
    public static readonly IReadOnlySet<string> ReviewTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "mr_list", "mr_get", "mr_diff", "mr_commits", "mr_discussions", "ci_status", "job_log", "compare_refs", "file_at", "grep_at",
        "issue_list", "issue_get", "wiki_list", "wiki_get", "deps_check", "sbom", "scan_at",
    };

    /// <summary>The most lock files and manifests one deps_check reads.</summary>
    public const int MostManifests = 400;
    /// <summary>The most files one scan_at narrowed to `paths` sends.</summary>
    public const int MostScanPaths = 500;

    /// <summary>The most diff text one answer carries; the files past it are listed without their text.</summary>
    public const int DiffBudget = 150_000;
    /// <summary>The most of a file one answer carries, as get_file.</summary>
    public const int FileBudget = 64 * 1024;
    const int DescriptionBudget = 20_000;

    ICodeHost Host => _host.Value;

    /// <summary>A project Argus knows (listed by GitLab, indexed or not): its GitLab id, path, default branch and clone URL.</summary>
    /// <param name="Kind">gitlab, github or gitea.</param>
    /// <param name="Name">On GitHub and Gitea, owner/repo (its <paramref name="Path"/> is "github:owner/repo").</param>
    public sealed record KnownProject(long GitlabId, string Path, string DefaultBranch, string HttpUrl, string Kind = "gitlab", string? Name = null)
    {
        public bool Foreign => Kind != "gitlab";
        public string HostTitle => ForeignHosts.Title(Kind);
    }

    /// <summary>The host the project is on: GitLab with Argus's service credential, or GitHub or Gitea with the person's own token.</summary>
    ICodeHost HostOf(KnownProject p, Identity who)
    {
        if (!p.Foreign) return Host;
        var host = ForeignHosts.For(p.Kind, ForeignHosts.ApiUrl(p.Kind)!, who.HostTokens[p.Kind]);
        host.Remember(p.GitlabId, p.Name!);
        return host;
    }

    /// <summary>A GitHub or Gitea repository the person may read, with their own token there; else why not.</summary>
    static KnownProject ForeignReadable(Identity who, string kind, string name)
    {
        var title = ForeignHosts.Title(kind);
        if (ForeignHosts.ApiUrl(kind) is not { } api)
            throw new NotFoundError($"{title} is not set up for Argus here, so {kind}:{name} cannot be read. An admin sets it ({(kind == ForeignHosts.Gitea ? ForeignHosts.GiteaUrlEnv : ForeignHosts.GitHubApiEnv)}).");
        if (!who.HostTokens.TryGetValue(kind, out var token))
            throw new AccessNotice($"Reading {title} needs your own {title} token: link a read-only one in Arena (Account → Code hosts), then ask again.");
        ForeignRepo? repo;
        try { repo = ForeignHosts.For(kind, api, token).Repo(name); }
        catch (CodeHostError exc) when (exc.Status is 401 or 403)
        {
            throw new AccessNotice($"{title} refused your token for {name}: link one that can read it (Account → Code hosts).");
        }
        catch (CodeHostError exc)
        {
            throw new HostUnavailable($"{title} could not answer for {name} ({exc.Message}). This is not something a different query fixes; tell the person asking.", exc);
        }
        return repo is null
            ? throw new NotFoundError($"{title} has no repository {name} that your token can read. Give it as {kind}:owner/repo.")
            : new KnownProject(repo.Id, $"{kind}:{repo.FullName}", repo.DefaultBranch, repo.CloneUrl, kind, repo.FullName);
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The project by its path (group/name, any case) or its GitLab id, among those GitLab listed; null when there is none.</summary>
    public static KnownProject? FindProject(Microsoft.Data.Sqlite.SqliteConnection conn, string project)
    {
        var asked = PyStr.Strip(project).Trim('/');
        if (asked.Length == 0) return null;
        var byId = long.TryParse(asked, out var id);
        var where = byId ? "gitlab_id = ?" : "lower(path_with_namespace) = lower(?)";
        object arg = byId ? id : asked;
        var row = Sql.One(conn, $"SELECT gitlab_id, path_with_namespace, default_branch, http_url FROM repo_choices WHERE {where}", arg)
                  ?? Sql.One(conn, $"SELECT gitlab_id, path_with_namespace, default_branch, http_url FROM repos WHERE {where} ORDER BY id LIMIT 1", arg);
        return row is null ? null : new KnownProject(row.Long("gitlab_id"), row.Str("path_with_namespace"), row.Str("default_branch"), row.Str("http_url"));
    }

    /// <summary>
    /// The project, when the person may read it. One they may not read answers as one that does not exist,
    /// unless access notices are on (as for the code tools): then it is named, with whom to ask.
    /// </summary>
    public KnownProject Readable(Identity who, string project)
    {
        if (ForeignHosts.Split(PyStr.Strip(project)) is { } foreign) return ForeignReadable(who, foreign.Kind, foreign.Name);
        var known = RunReadonly(conn => FindProject(conn, project));
        // Asked outside the index's connection: GitLab may be asked here, and may be unwell.
        if (known is not null && who.MayRead(known.GitlabId)) return known;
        if (known is not null && _notices is not null)
        {
            string ask;
            try
            {
                var people = _notices.Maintainers(known.GitlabId);
                ask = people.Count > 0 ? $" Its maintainers: {string.Join(", ", people)}." : " No maintainer is listed for it.";
            }
            catch (GitLabUnavailable) { ask = " Its maintainers cannot be listed right now."; }
            throw new AccessNotice(
                $"You cannot read {known.Path} in GitLab, so Argus does not answer about it.{ask} Tell the person asking that they do not " +
                "have access, and that a maintainer can add them in GitLab with at least Reporter access; Argus picks the change up within 10 minutes.");
        }
        throw new NotFoundError(
            $"No project {PyStr.Repr(project)} that you can read is known to Argus. Give its path as GitLab shows it (group/project) " +
            "or its GitLab id; mr_list and index_status take the same.");
    }

    /// <summary>
    /// A call to GitLab about <paramref name="what"/> (said without an article: "merge request !4 in group/app"):
    /// a 404 says what was not found, a 400 what GitLab refused, and any other failure not to retry.
    /// </summary>
    static T Hosted<T>(KnownProject p, string what, Func<T> call)
    {
        var host = p.HostTitle;
        try { return call(); }
        catch (CodeHostError exc) when (exc.Status == 404) { throw new NotFoundError($"{host} has no {what}."); }
        catch (CodeHostError exc) when (exc.Status is 400 or 501) { throw new ToolError($"{host} refused the request for {what}: {exc.Message}", exc); }
        catch (CodeHostError exc) when (exc.Status == 403 && p.Foreign) { throw new AccessNotice($"{exc.Message}."); }
        catch (CodeHostError exc)
        {
            throw new HostUnavailable($"{host} could not answer for {what} ({exc.Message}). This is not something a different query fixes; tell the person asking.", exc);
        }
    }

    /// <summary>Forget the kept heads of merge requests no longer open (best effort: a failure here never fails the answer).</summary>
    void ForgetClosed(KnownProject p, IEnumerable<ChangeRequest> changes)
    {
        if (p.Foreign) return;
        try
        {
            var closed = changes.Where(c => !c.Open).Select(c => c.Number).ToList();
            if (closed.Count == 0) return;
            var kept = CodeAt.Kept(DbPath, p.GitlabId);
            if (closed.Any(kept.Contains))
                CodeAt.Forget(DbPath, Mirror.MirrorPath(_cfg.Index, p.GitlabId), p.GitlabId, closed.Where(kept.Contains));
        }
        catch (Exception exc) when (exc is Microsoft.Data.Sqlite.SqliteException or GitError or IOException)
        {
            Console.Error.WriteLine($"could not drop merge request heads of {p.Path}: {exc.Message}");
        }
    }

    static JsonArray Arr(IEnumerable<JsonNode?> items) => new([.. items]);

    /// <summary>
    /// Files with their text until the budget is spent; the rest named, without it. A first file larger than
    /// the whole budget comes with as much of its text as fits, so asking for it alone always shows it.
    /// </summary>
    static (JsonArray Files, int Omitted) Budgeted(IEnumerable<FileDiff> files)
    {
        var output = new JsonArray();
        int spent = 0, omitted = 0;
        foreach (var f in files)
        {
            var fits = spent + f.Diff.Length <= DiffBudget;
            var row = f.ToJson(withText: fits);
            if (fits) spent += f.Diff.Length;
            else if (spent == 0)
            {
                row["diff"] = f.Diff[..DiffBudget];
                row["truncated"] = true;
                spent = DiffBudget;
            }
            else
            {
                row["omitted"] = "over this answer's size: ask for it alone with path";
                omitted++;
            }
            output.Add(row);
        }
        return (output, omitted);
    }

    /// <summary>A path filter: the exact path (old or new) or a glob such as src/*.cs.</summary>
    static bool PathMatches(FileDiff f, string? path) =>
        string.IsNullOrEmpty(path) || f.NewPath == path || f.OldPath == path || Fnmatch.MatchCase(f.NewPath, path) || Fnmatch.MatchCase(f.OldPath, path);

    static string CheckRev(string name, string rev)
    {
        rev = PyStr.Strip(rev);
        if (!CodeAt.ValidRev(rev))
            throw new ToolError($"{name} {PyStr.Repr(rev)} is not a branch, tag or commit id.");
        return rev;
    }

    // --- merge requests ----------------------------------------------------------------------

    public JsonNode MrList(Identity who, string project, string state, string? updatedAfter, string? author, long limit)
    {
        var p = Readable(who, project);
        state = PyStr.Strip(state).ToLowerInvariant();
        if (state is not ("opened" or "closed" or "merged" or "locked" or "all"))
            throw new ToolError($"state is one of opened, closed, merged, locked, all; not {PyStr.Repr(state)}.");
        var changes = Hosted(p, $"merge requests of {p.Path}", () => HostOf(p, who).ListChanges(p.GitlabId, state, updatedAfter, author, (int)Math.Clamp(limit, 1, 100)));
        ForgetClosed(p, changes);
        return Arr(changes.Select(c => (JsonNode?)c.ToSummary()));
    }

    public JsonNode MrGet(Identity who, string project, long iid)
    {
        var p = Readable(who, project);
        var change = Hosted(p, $"merge request !{iid} in {p.Path}", () => HostOf(p, who).GetChange(p.GitlabId, iid))
                     ?? throw new NotFoundError($"GitLab has no merge request !{iid} in {p.Path}.");
        var versions = Hosted(p, $"versions of !{iid} in {p.Path}", () => HostOf(p, who).Versions(p.GitlabId, iid));
        ForgetClosed(p, [change]);
        var o = change.ToJson(DescriptionBudget);
        o["project"] = p.Path;
        o["versions"] = Arr(versions.Select(v => (JsonNode?)v.ToJson()));
        return o;
    }

    public JsonNode MrDiff(Identity who, string project, long iid, string? path, string? sinceSha, long? version)
    {
        var p = Readable(who, project);
        if (sinceSha is not null && version is not null) throw new ToolError("Give since_sha or version, not both.");
        var change = Hosted(p, $"merge request !{iid} in {p.Path}", () => HostOf(p, who).GetChange(p.GitlabId, iid))
                     ?? throw new NotFoundError($"GitLab has no merge request !{iid} in {p.Path}.");
        IReadOnlyList<FileDiff> files;
        var o = new JsonObject { ["project"] = p.Path, ["iid"] = iid };
        if (sinceSha is not null && p.Kind == ForeignHosts.Gitea)
        {
            // Gitea cannot compare two commits: the whole diff, said so.
            o["since_sha_ignored"] = "Gitea's API cannot compare two commits, so this is the whole diff.";
            sinceSha = null;
        }
        if (sinceSha is not null)
        {
            var since = CheckRev("since_sha", sinceSha);
            var head = change.Refs?.HeadSha ?? change.Sha ?? throw new NotFoundError($"merge request !{iid} has no head commit yet.");
            // What the merge request's own files became since that commit: a straight diff, so a commit of the
            // target branch merged in meanwhile is not shown as the author's.
            var touched = Hosted(p, $"diff of !{iid} in {p.Path}", () => HostOf(p, who).Diff(p.GitlabId, iid)).SelectMany(f => new[] { f.OldPath, f.NewPath }).ToHashSet(StringComparer.Ordinal);
            var compared = Hosted(p, $"comparison of {since} with {head} in {p.Path}", () => HostOf(p, who).Compare(p.GitlabId, since, head, straight: true));
            files = compared.Diffs.Where(f => touched.Contains(f.NewPath) || touched.Contains(f.OldPath)).ToList();
            o["since_sha"] = since;
            o["head_sha"] = head;
        }
        else
        {
            files = Hosted(p, version is null ? $"diff of !{iid} in {p.Path}" : $"version {version} of !{iid} in {p.Path}", () => HostOf(p, who).Diff(p.GitlabId, iid, version));
            if (version is not null)
            {
                var v = Hosted(p, $"versions of !{iid} in {p.Path}", () => HostOf(p, who).Versions(p.GitlabId, iid)).FirstOrDefault(x => x.Id == version);
                o["version"] = version;
                if (v is not null) o["diff_refs"] = new DiffRefs(v.BaseSha, v.StartSha, v.HeadSha).ToJson();
            }
            else o["diff_refs"] = change.Refs?.ToJson();
        }
        var (rows, omitted) = Budgeted(files.Where(f => PathMatches(f, path)));
        o["files"] = rows;
        if (omitted > 0) o["omitted_files"] = omitted;
        return o;
    }

    public JsonNode MrCommits(Identity who, string project, long iid)
    {
        var p = Readable(who, project);
        return Arr(Hosted(p, $"merge request !{iid} in {p.Path}", () => HostOf(p, who).Commits(p.GitlabId, iid)).Select(c => (JsonNode?)c.ToJson()));
    }

    public JsonNode MrDiscussions(Identity who, string project, long iid, bool includeSystem)
    {
        var p = Readable(who, project);
        var threads = Hosted(p, $"merge request !{iid} in {p.Path}", () => HostOf(p, who).Discussions(p.GitlabId, iid));
        return Arr(threads.Where(d => includeSystem || !d.System).Select(d => (JsonNode?)new JsonObject
        {
            ["id"] = d.Id, ["resolvable"] = d.Resolvable, ["resolved"] = d.Resolved,
            ["notes"] = Arr(d.Notes.Where(n => includeSystem || !n.System).Select(n => (JsonNode?)n.ToJson())),
        }));
    }

    // --- issues and the wiki: what people asked for, reported and wrote down -------------------

    /// <summary>The most of a wiki page one answer carries.</summary>
    public const int WikiBudget = 40_000;

    public JsonNode IssueList(Identity who, string project, string state, string? search, string? labels, long limit)
    {
        var p = Readable(who, project);
        state = PyStr.Strip(state).ToLowerInvariant();
        if (state is not ("opened" or "closed" or "all")) throw new ToolError($"state is one of opened, closed, all; not {PyStr.Repr(state)}.");
        var issues = Hosted(p, $"issues of {p.Path}", () => HostOf(p, who).ListIssues(p.GitlabId, state, search, labels, (int)Math.Clamp(limit, 1, 100)));
        return Arr(issues.Select(i => (JsonNode?)i.ToSummary()));
    }

    public JsonNode IssueGet(Identity who, string project, long iid)
    {
        var p = Readable(who, project);
        var host = HostOf(p, who);
        var issue = Hosted(p, $"issue #{iid} in {p.Path}", () => host.GetIssue(p.GitlabId, iid)) ?? throw new NotFoundError($"{p.HostTitle} has no issue #{iid} in {p.Path}.");
        var notes = Hosted(p, $"comments of issue #{iid} in {p.Path}", () => host.IssueNotes(p.GitlabId, iid));
        var o = issue.ToJson(DescriptionBudget);
        o["project"] = p.Path;
        o["notes"] = Arr(notes.Where(n => !n.System).Select(n => (JsonNode?)new JsonObject
        {
            ["author"] = n.Author, ["created_at"] = n.CreatedAt, ["body"] = n.Body.Length > 4000 ? n.Body[..4000] : n.Body,
        }));
        return o;
    }

    public JsonNode WikiList(Identity who, string project)
    {
        var p = Readable(who, project);
        var pages = Hosted(p, $"the wiki of {p.Path}", () => HostOf(p, who).WikiPages(p.GitlabId));
        return new JsonObject { ["project"] = p.Path, ["pages"] = Arr(pages.Select(w => (JsonNode?)w.ToSummary())) };
    }

    public JsonNode WikiGet(Identity who, string project, string slug)
    {
        var p = Readable(who, project);
        if (string.IsNullOrWhiteSpace(slug)) throw new ToolError("Give the page's slug, as wiki_list shows it.");
        var page = Hosted(p, $"wiki page {slug} of {p.Path}", () => HostOf(p, who).GetWikiPage(p.GitlabId, slug.Trim()))
                   ?? throw new NotFoundError($"{p.HostTitle} has no wiki page {slug} in {p.Path}.");
        var o = page.ToJson(WikiBudget);
        o["project"] = p.Path;
        return o;
    }

    // --- CI -----------------------------------------------------------------------------------

    public JsonNode CiStatus(Identity who, string project, long? iid, string? sha)
    {
        var p = Readable(who, project);
        if (iid is null && string.IsNullOrWhiteSpace(sha)) throw new ToolError("Give iid (a merge request) or sha (a commit).");
        var commit = sha is null ? null : CheckRev("sha", sha);
        var what = iid is { } n ? $"merge request !{n} in {p.Path}" : $"commit {commit} in {p.Path}";
        // One host for both calls: Gitea's jobs are the statuses of the commit its pipeline was read for.
        var host = HostOf(p, who);
        var pipelines = Hosted(p, what, () => host.Pipelines(p.GitlabId, iid, commit));
        if (pipelines.Count == 0) return new JsonObject { ["project"] = p.Path, ["pipeline"] = null, ["note"] = $"No pipeline ran for {what}." };
        var latest = pipelines[0];
        var jobs = Hosted(p, $"jobs of pipeline {latest.Id} in {p.Path}", () => host.Jobs(p.GitlabId, latest.Id));
        return new JsonObject
        {
            ["project"] = p.Path,
            ["pipeline"] = latest.ToJson(),
            ["jobs"] = Arr(jobs.Select(j => (JsonNode?)j.ToJson())),
            ["failed_jobs"] = Arr(jobs.Where(j => j.Status == "failed" && !j.AllowFailure).Select(j => (JsonNode?)new JsonObject { ["id"] = j.Id, ["name"] = j.Name })),
            ["earlier_pipelines"] = Arr(pipelines.Skip(1).Take(5).Select(x => (JsonNode?)x.ToJson())),
        };
    }

    /// <summary>A job's log as a person reads it: colours, GitLab's section markers and overwritten progress lines taken out.</summary>
    public static string CleanLog(string log)
    {
        var text = LogNoise().Replace(log, "");
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.LastIndexOf('\r') is var i and >= 0 ? l[(i + 1)..] : l);
        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]|section_(?:start|end):\d+:[A-Za-z0-9_.\-\[\]=,]*\r?", RegexOptions.CultureInvariant)]
    private static partial Regex LogNoise();

    public JsonNode JobLog(Identity who, string project, long jobId, long tailChars)
    {
        var p = Readable(who, project);
        var log = Hosted(p, $"job {jobId} in {p.Path}", () => HostOf(p, who).JobLog(p.GitlabId, jobId)) ?? throw new NotFoundError($"Job {jobId} in {p.Path} has no log.");
        // Masked whole, then cut: a secret the cut runs through is still taken out.
        var clean = SecretMask.Mask(CleanLog(log));
        var tail = (int)Math.Clamp(tailChars, 1_000, 200_000);
        var cut = clean.Length > tail;
        var text = cut ? clean[^tail..] : clean;
        if (cut && text.IndexOf('\n') is var nl and >= 0 && nl < text.Length - 1) text = text[(nl + 1)..];
        return new JsonObject { ["project"] = p.Path, ["job_id"] = jobId, ["chars"] = clean.Length, ["truncated"] = cut, ["log"] = text };
    }

    // --- comparisons and code at a commit ---------------------------------------------------------

    public JsonNode CompareRefs(Identity who, string project, string from, string to, string? path, bool straight)
    {
        var p = Readable(who, project);
        var a = CheckRev("from", from);
        var b = CheckRev("to", to);
        var compared = Hosted(p, $"comparison of {a} with {b} in {p.Path}", () => HostOf(p, who).Compare(p.GitlabId, a, b, straight));
        var (rows, omitted) = Budgeted(compared.Diffs.Where(f => PathMatches(f, path)));
        var o = new JsonObject
        {
            ["project"] = p.Path, ["from"] = a, ["to"] = b, ["straight"] = straight,
            ["commits"] = Arr(compared.Commits.Take(100).Select(c => (JsonNode?)c.ToJson())),
            ["files"] = rows,
        };
        if (compared.Commits.Count > 100) o["more_commits"] = compared.Commits.Count - 100;
        if (omitted > 0) o["omitted_files"] = omitted;
        if (compared.TimedOut) o["timed_out"] = true;
        return o;
    }

    /// <summary>The commit a call is about, in the project's mirror: a merge request's head, else a branch, tag or commit (the default branch when none).</summary>
    (string Mirror, string Sha, string Label) At(KnownProject p, string? rev, long? iid)
    {
        var mirror = CodeAt.Mirror(_cfg.Index, p.GitlabId, p.HttpUrl);
        if (iid is { } n)
        {
            var head = CodeAt.Change(_cfg.Index, _cfg.GitLab, DbPath, mirror, Host, p.GitlabId, n, Now())
                       ?? throw new NotFoundError($"GitLab has no merge request !{n} in {p.Path}.");
            if (rev is null) return (mirror, head, $"!{n}");
            var asked = CheckRev("ref", rev);
            var sha = CodeAt.Resolve(mirror, asked) ?? CodeAt.Rev(_cfg.Index, _cfg.GitLab, mirror, asked)
                      ?? throw new NotFoundError($"{p.Path} has no commit {asked} that Argus can fetch (only what its branches, tags and merge requests reach).");
            return (mirror, sha, asked);
        }
        var name = CheckRev("ref", rev ?? p.DefaultBranch);
        var at = CodeAt.Rev(_cfg.Index, _cfg.GitLab, mirror, name)
                 ?? throw new NotFoundError($"{p.Path} has no branch, tag or commit {name} that Argus can fetch (only what its branches, tags and merge requests reach).");
        return (mirror, at, name);
    }

    static string CleanPath(string path)
    {
        var p = PyStr.Strip(path).Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.TrimStart('/');
        if (p.Length == 0 || p.Contains('\0') || p.Split('/').Any(s => s == ".."))
            throw new ToolError($"path {PyStr.Repr(path)} is not a file path in the repository.");
        return p;
    }

    public JsonNode FileAt(Identity who, string project, string path, string? rev, long? iid, long? startLine, long? endLine)
    {
        var p = Readable(who, project);
        var file = CleanPath(path);
        string sha, label;
        FileAtCommit? found;
        try
        {
            // GitHub and Gitea have no mirror here: their API, as the person.
            if (p.Foreign) throw new GitError($"{p.Path} is read through {p.HostTitle}'s API");
            string mirror;
            (mirror, sha, label) = At(p, rev, iid);
            found = CodeAt.Show(mirror, sha, file);
        }
        catch (Exception exc) when (exc is GitError or IOException or System.ComponentModel.Win32Exception)
        {
            // No mirror to read (git failed, or is missing): the host's API, at the merge request's head or the ref asked.
            if (!p.Foreign) Console.Error.WriteLine($"file_at reads {p.Path} through GitLab's API: {exc.Message}");
            label = iid is { } n ? $"!{n}" : CheckRev("ref", rev ?? p.DefaultBranch);
            sha = iid is { } m
                ? rev is null
                    ? Hosted(p, $"merge request !{m} in {p.Path}", () => HostOf(p, who).GetChange(p.GitlabId, m))?.Sha ?? throw new NotFoundError($"{p.HostTitle} has no merge request !{m} in {p.Path}.")
                    : CheckRev("ref", rev)
                : label;
            var at = sha;
            var text = Hosted(p, $"{file} at {label} in {p.Path}", () => HostOf(p, who).FileAt(p.GitlabId, at, file));
            found = text is null ? null : new FileAtCommit(text, Encoding.UTF8.GetByteCount(text), text.Contains('\0'), false);
        }
        if (found is null) throw new NotFoundError($"{p.Path} has no file {PyStr.Repr(file)} at {label} ({PyStr.Prefix(sha, 12)}).");
        var o = new JsonObject { ["project"] = p.Path, ["ref"] = label, ["sha"] = sha, ["path"] = file, ["size"] = found.Size };
        if (found.TooLarge || found.Binary)
        {
            o["content"] = null;
            o[found.Binary ? "binary" : "too_large"] = true;
            return o;
        }
        var lines = found.Text.Split('\n');
        var total = found.Text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var first = (int)Math.Clamp(startLine ?? 1, 1, Math.Max(total, 1));
        var last = (int)Math.Clamp(endLine ?? total, first, Math.Max(total, 1));
        // Each line with its own end, so the whole file comes back exactly as it is.
        var content = string.Join("\n", lines.Skip(first - 1).Take(last - first + 1)) + (last < lines.Length ? "\n" : "");
        var truncated = content.Length > FileBudget;
        o["start_line"] = first;
        o["end_line"] = last;
        o["total_lines"] = total;
        o["content"] = truncated ? content[..FileBudget] : content;
        o["truncated"] = truncated;
        return o;
    }

    public JsonNode GrepAt(Identity who, string project, string pattern, string? rev, long? iid, string? path, bool ignoreCase, bool fixedText, long limit)
    {
        var p = Readable(who, project);
        if (p.Foreign) throw new ToolError($"grep_at searches Argus's copies of GitLab projects; {p.Path} has none: read its files with file_at.");
        if (string.IsNullOrEmpty(pattern) || pattern.Length > 500) throw new ToolError("pattern is 1 to 500 characters.");
        var pathspec = string.IsNullOrWhiteSpace(path) ? null : PyStr.Strip(path);
        try
        {
            var (mirror, sha, label) = At(p, rev, iid);
            var (hits, more) = CodeAt.Grep(mirror, sha, pattern, pathspec, ignoreCase, fixedText, (int)Math.Clamp(limit, 1, 500));
            return new JsonObject
            {
                ["project"] = p.Path, ["ref"] = label, ["sha"] = sha,
                ["matches"] = Arr(hits.Select(h => (JsonNode?)new JsonObject { ["path"] = h.Path, ["line"] = h.Line, ["text"] = h.Text })),
                ["more"] = more,
            };
        }
        catch (GitError exc)
        {
            throw new ToolError($"git could not search {p.Path}: {exc.Message}", exc);
        }
    }

    /// <summary>The project's packages at a commit, from its lock files and manifests (vendored code left out), and the commit read.</summary>
    (List<Deps.Dependency> Dependencies, string Sha, string Label) ReadDependencies(string who, Identity identity, string project, string? rev, long? iid)
    {
        var p = Readable(identity, project);
        if (p.Foreign) throw new ToolError($"{who} reads Argus's copies of GitLab projects; {p.Path} has none.");
        try
        {
            var (mirror, sha, label) = At(p, rev, iid);
            var files = CodeAt.ListFiles(mirror, sha).Where(f => Deps.Manifests.Reads(f) && !Deps.Manifests.Vendored().IsMatch(f)).Take(MostManifests).ToList();
            var texts = files.Select(f => (Path: f, File: CodeAt.Show(mirror, sha, f)))
                .Where(x => x.File is { TooLarge: false, Binary: false }).Select(x => (x.Path, x.File!.Text));
            return (Deps.DepsCheck.Dependencies(texts), sha, label);
        }
        catch (GitError exc)
        {
            throw new ToolError($"git could not read {p.Path}: {exc.Message}", exc);
        }
    }

    /// <summary>The project's packages at a ref matched against the advisories packs: the vulnerable ones, how each comes in, and the upgrade that fixes it.</summary>
    public JsonNode DepsCheckAt(Identity who, string project, string? rev, long? iid)
    {
        var (dependencies, sha, label) = ReadDependencies("deps_check", who, project, rev, iid);
        var result = RunPacks(opened => (JsonNode)Deps.DepsCheck.Check(dependencies, opened)).AsObject();
        result["project"] = project;
        result["ref"] = label;
        result["sha"] = sha;
        return result;
    }

    /// <summary>The project's packages at a ref as a CycloneDX bill of materials.</summary>
    public JsonNode SbomAt(Identity who, string project, string? rev, long? iid)
    {
        var (dependencies, sha, _) = ReadDependencies("sbom", who, project, rev, iid);
        return Deps.DepsCheck.CycloneDx(project, sha, dependencies, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The security scanners on a project at a ref (the scanners feature): its files, and its history for gitleaks,
    /// sent from the project's mirror; what they found, each finding with the scanners and rules that found it.
    /// </summary>
    public JsonNode ScanAt(Identity who, string project, string? rev, long? iid, bool history, string? tools, string? paths = null)
    {
        var p = Readable(who, project);
        if (p.Foreign) throw new ToolError($"scan_at scans Argus's copies of GitLab projects; {p.Path} has none.");
        var asked = string.IsNullOrWhiteSpace(tools) ? null : tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var only = string.IsNullOrWhiteSpace(paths) ? null : paths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(CleanPath).Distinct().ToList();
        if (only is { Count: > MostScanPaths }) throw new ToolError($"paths names at most {MostScanPaths} files.");
        try
        {
            var (mirror, sha, label) = At(p, rev, iid);
            var present = only is null ? null : CodeAt.ListFiles(mirror, sha).ToHashSet(StringComparer.Ordinal);
            if (only is not null) only = [.. only.Where(present!.Contains)];
            if (only is { Count: 0 })
                return new JsonObject { ["project"] = p.Path, ["ref"] = label, ["sha"] = sha, ["tools"] = new JsonArray(), ["findings"] = new JsonArray(), ["more"] = 0, ["note"] = "None of the files named is at that commit." };
            var result = Scanning.Scanners.Scan(mirror, sha, history, asked, only);
            result["project"] = p.Path;
            result["ref"] = label;
            result["sha"] = sha;
            return result;
        }
        catch (GitError exc)
        {
            throw new ToolError($"git could not read {p.Path}: {exc.Message}", exc);
        }
    }

    // --- dispatch ---------------------------------------------------------------------------

    JsonNode? DispatchReview(string tool, ToolRuntime.Args a, Identity who)
    {
        var project = a.Str("project");
        JsonObject Audited(JsonObject args)
        {
            args["project"] = project;
            return args;
        }
        return tool switch
        {
            "mr_list" => WithAudit(who, tool,
                Audited(new JsonObject { ["state"] = a.OptStr("state"), ["updated_after"] = a.OptStr("updated_after"), ["author"] = a.OptStr("author"), ["limit"] = a.Int("limit") }),
                () => MrList(who, project, a.OptStr("state") ?? "opened", a.OptStr("updated_after"), a.OptStr("author"), a.Int("limit"))),
            "mr_get" => WithAudit(who, tool, Audited(new JsonObject { ["iid"] = a.Int("iid") }), () => MrGet(who, project, a.Int("iid"))),
            "mr_diff" => WithAudit(who, tool,
                Audited(new JsonObject { ["iid"] = a.Int("iid"), ["path"] = a.OptStr("path"), ["since_sha"] = a.OptStr("since_sha"), ["version"] = a.OptInt("version") }),
                () => MrDiff(who, project, a.Int("iid"), a.OptStr("path"), a.OptStr("since_sha"), a.OptInt("version"))),
            "mr_commits" => WithAudit(who, tool, Audited(new JsonObject { ["iid"] = a.Int("iid") }), () => MrCommits(who, project, a.Int("iid"))),
            "mr_discussions" => WithAudit(who, tool, Audited(new JsonObject { ["iid"] = a.Int("iid"), ["include_system"] = a.Bool("include_system") }),
                () => MrDiscussions(who, project, a.Int("iid"), a.Bool("include_system"))),
            "ci_status" => WithAudit(who, tool, Audited(new JsonObject { ["iid"] = a.OptInt("iid"), ["sha"] = a.OptStr("sha") }),
                () => CiStatus(who, project, a.OptInt("iid"), a.OptStr("sha"))),
            "job_log" => WithAudit(who, tool, Audited(new JsonObject { ["job_id"] = a.Int("job_id"), ["tail_chars"] = a.Int("tail_chars") }),
                () => JobLog(who, project, a.Int("job_id"), a.Int("tail_chars"))),
            "compare_refs" => WithAudit(who, tool,
                Audited(new JsonObject { ["from"] = a.Str("from"), ["to"] = a.Str("to"), ["path"] = a.OptStr("path"), ["straight"] = a.Bool("straight") }),
                () => CompareRefs(who, project, a.Str("from"), a.Str("to"), a.OptStr("path"), a.Bool("straight"))),
            "file_at" => WithAudit(who, tool,
                Audited(new JsonObject
                {
                    ["path"] = a.Str("path"), ["ref"] = a.OptStr("ref"), ["iid"] = a.OptInt("iid"), ["start_line"] = a.OptInt("start_line"), ["end_line"] = a.OptInt("end_line"),
                }),
                () => FileAt(who, project, a.Str("path"), a.OptStr("ref"), a.OptInt("iid"), a.OptInt("start_line"), a.OptInt("end_line"))),
            "grep_at" => WithAudit(who, tool,
                Audited(new JsonObject
                {
                    ["pattern"] = PyStr.Prefix(a.Str("pattern"), 200), ["ref"] = a.OptStr("ref"), ["iid"] = a.OptInt("iid"), ["path"] = a.OptStr("path"),
                    ["ignore_case"] = a.Bool("ignore_case"), ["fixed"] = a.Bool("fixed"), ["limit"] = a.Int("limit"),
                }),
                () => GrepAt(who, project, a.Str("pattern"), a.OptStr("ref"), a.OptInt("iid"), a.OptStr("path"), a.Bool("ignore_case"), a.Bool("fixed"), a.Int("limit"))),
            "deps_check" => WithAudit(who, tool, Audited(new JsonObject { ["ref"] = a.OptStr("ref"), ["iid"] = a.OptInt("iid") }),
                () => DepsCheckAt(who, project, a.OptStr("ref"), a.OptInt("iid"))),
            "sbom" => WithAudit(who, tool, Audited(new JsonObject { ["ref"] = a.OptStr("ref"), ["iid"] = a.OptInt("iid") }),
                () => SbomAt(who, project, a.OptStr("ref"), a.OptInt("iid"))),
            "scan_at" => WithAudit(who, tool,
                Audited(new JsonObject { ["ref"] = a.OptStr("ref"), ["iid"] = a.OptInt("iid"), ["history"] = a.Bool("history"), ["tools"] = a.OptStr("tools"), ["paths"] = PyStr.Prefix(a.OptStr("paths") ?? "", 200) }),
                () => ScanAt(who, project, a.OptStr("ref"), a.OptInt("iid"), a.Bool("history"), a.OptStr("tools"), a.OptStr("paths"))),
            "issue_list" => WithAudit(who, tool,
                Audited(new JsonObject { ["state"] = a.OptStr("state"), ["search"] = a.OptStr("search"), ["labels"] = a.OptStr("labels"), ["limit"] = a.Int("limit") }),
                () => IssueList(who, project, a.OptStr("state") ?? "opened", a.OptStr("search"), a.OptStr("labels"), a.Int("limit"))),
            "issue_get" => WithAudit(who, tool, Audited(new JsonObject { ["iid"] = a.Int("iid") }), () => IssueGet(who, project, a.Int("iid"))),
            "wiki_list" => WithAudit(who, tool, Audited([]), () => WikiList(who, project)),
            "wiki_get" => WithAudit(who, tool, Audited(new JsonObject { ["slug"] = a.Str("slug") }), () => WikiGet(who, project, a.Str("slug"))),
            _ => throw new ToolError($"Unknown tool: {tool}"),
        };
    }
}
