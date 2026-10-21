using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Util;

namespace Argus.CodeHosts;

/// <summary>
/// GitHub and Gitea besides GitLab: where their APIs are (ARGUS_GITHUB_API, by default GitHub's own, "off" for none;
/// ARGUS_GITEA_URL, none by default). Their projects are named "github:owner/repo" and "gitea:owner/repo", and read
/// with the asking person's own read-only token (the app passes it): what they may read there is what Argus reads.
/// </summary>
public static class ForeignHosts
{
    public const string GitHub = "github";
    public const string Gitea = "gitea";
    public const string GitHubApiEnv = "ARGUS_GITHUB_API";
    public const string GiteaUrlEnv = "ARGUS_GITEA_URL";

    /// <summary>The header the app passes each host's token in: x-llm-github-token, x-llm-gitea-token.</summary>
    public static string TokenHeader(string kind) => $"x-llm-{kind}-token";

    public static readonly string[] Kinds = [GitHub, Gitea];

    /// <summary>The host's API address; null when it is off.</summary>
    public static string? ApiUrl(string kind)
    {
        if (kind == GitHub)
        {
            var url = Environment.GetEnvironmentVariable(GitHubApiEnv)?.Trim();
            return url is "off" or "0" or "false" ? null : string.IsNullOrEmpty(url) ? "https://api.github.com" : url.TrimEnd('/');
        }
        var gitea = Environment.GetEnvironmentVariable(GiteaUrlEnv)?.Trim();
        return string.IsNullOrEmpty(gitea) ? null : gitea.TrimEnd('/') + "/api/v1";
    }

    public static string Title(string kind) => kind == GitHub ? "GitHub" : kind == Gitea ? "Gitea" : "GitLab";

    /// <summary>"github:owner/repo" as (github, owner/repo); null for a GitLab path.</summary>
    public static (string Kind, string Name)? Split(string project)
    {
        var colon = project.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return null;
        var kind = project[..colon].Trim().ToLowerInvariant();
        var name = project[(colon + 1)..].Trim().Trim('/');
        return Kinds.Contains(kind) && name.Count(c => c == '/') == 1 && !name.Contains("..", StringComparison.Ordinal) ? (kind, name) : null;
    }

    static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    /// <summary>One client per API address, kept: the person's token goes on each request, never on the client.</summary>
    internal static HttpClient ClientFor(string apiUrl) => Clients.GetOrAdd(apiUrl, _ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    /// <summary>A host reading as the person with their token; <paramref name="client"/> for tests.</summary>
    public static RestHost For(string kind, string apiUrl, string token, HttpClient? client = null) =>
        kind == GitHub ? new GitHubHost(apiUrl, token, client ?? ClientFor(apiUrl)) : new GiteaHost(apiUrl, token, client ?? ClientFor(apiUrl));
}

/// <summary>A repository on GitHub or Gitea as the person sees it: its id, full name, default branch and clone address.</summary>
public sealed record ForeignRepo(long Id, string FullName, string DefaultBranch, string CloneUrl);

/// <summary>
/// GitHub's and Gitea's REST APIs, as the person (their token): every request is a GET through <see cref="Json"/>,
/// so nothing here can write to the host. Projects are by id in <see cref="ICodeHost"/>; the host knows each id's
/// full name once <see cref="Repo"/> or <see cref="Remember"/> told it.
/// </summary>
public abstract class RestHost(string apiUrl, string token, HttpClient client) : ICodeHost
{
    const int PerPage = 50;
    const int MaxPages = 60;

    readonly ConcurrentDictionary<long, string> _names = new();

    /// <summary>The person's token: sent on each request, never kept beyond this host (one request's).</summary>
    protected string Token { get; } = token;

    public abstract string Kind { get; }
    protected string Title => ForeignHosts.Title(Kind);
    protected abstract string Authorization { get; }

    public void Remember(long id, string fullName) => _names[id] = fullName;

    protected string Name(long project) => _names.TryGetValue(project, out var n) ? n : throw new CodeHostError($"{Title} repository {project} is not known here", 404);

    protected HttpResult Get(string path, IEnumerable<(string, string)>? query = null, string accept = "application/json")
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = Authorization, ["Accept"] = accept, ["User-Agent"] = "argus" };
        try
        {
            return Tls.Get(client, apiUrl + path, headers, query);
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            throw new CodeHostError($"{Title} could not be reached: {exc.Message}", 0, exc);
        }
    }

    /// <summary>The answer as JSON; null for a 404; a <see cref="CodeHostError"/> for any other failure (401 and 403 say the token cannot).</summary>
    protected JsonNode? Json(string path, IEnumerable<(string, string)>? query = null)
    {
        var resp = Get(path, query);
        if (resp.Status == 404) return null;
        if (resp.Status is 401 or 403)
            throw new CodeHostError($"{Title} refused your token for GET {path} ({resp.Status}): link a token that can read it, in Arena under Account → Code hosts", 403);
        if (resp.Status != 200) throw new CodeHostError($"{Title} answered {resp.Status} for GET {path}: {PyStr.Prefix(resp.Text, 200)}", resp.Status);
        try { return resp.Json(); }
        catch (JsonException exc) { throw new CodeHostError($"{Title}'s answer to GET {path} is not JSON", resp.Status, exc); }
    }

    protected List<JsonObject> Pages(string path, IEnumerable<(string, string)> query, int limit = int.MaxValue, string perPage = "per_page", Func<JsonNode, JsonArray?>? items = null)
    {
        var all = new List<JsonObject>();
        var size = Math.Min(PerPage, Math.Max(1, limit));
        for (var page = 1; page <= MaxPages && all.Count < limit; page++)
        {
            var node = Json(path, [.. query, (perPage, size.ToString(CultureInfo.InvariantCulture)), ("page", page.ToString(CultureInfo.InvariantCulture))]);
            if (node is null) throw new CodeHostError($"{Title} has no {path}", 404);
            var batch = items is null ? node as JsonArray : items(node);
            if (batch is null) throw new CodeHostError($"{Title}'s answer to GET {path} is not a list");
            all.AddRange(batch.OfType<JsonObject>());
            if (batch.Count < size) break;
        }
        return all.Count > limit ? all.GetRange(0, limit) : all;
    }

    protected static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n ? n.ToJsonString() : null;
    protected static long Long(JsonNode? node) => node is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : long.TryParse(Str(v), out l) ? l : 0) : 0;
    protected static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    protected static string Login(JsonNode? node) => Str(node?["login"]) ?? Str(node?["username"]) ?? "";
    protected static IReadOnlyList<string> Logins(JsonNode? node) => (node as JsonArray ?? []).Select(Login).Where(l => l.Length > 0).ToList();

    /// <summary>The repository as the person may read it; null when there is none they may (the host answers 404 for both).</summary>
    public ForeignRepo? Repo(string fullName)
    {
        if (Json($"/repos/{fullName}") is not JsonObject r) return null;
        var repo = new ForeignRepo(Long(r["id"]), Str(r["full_name"]) ?? fullName, Str(r["default_branch"]) ?? "main", Str(r["clone_url"]) ?? "");
        Remember(repo.Id, repo.FullName);
        return repo;
    }

    /// <summary>Who the token is: its login.</summary>
    public string Whoami() => Json("/user") is JsonObject u ? Login(u) : throw new CodeHostError($"{Title} does not know this token", 401);

    protected static string State(JsonObject pr) =>
        Str(pr["state"]) == "open" ? "opened" : Bool(pr["merged"]) || Str(pr["merged_at"]) is { Length: > 0 } ? "merged" : "closed";

    protected ChangeRequest Change(JsonObject pr, bool draft)
    {
        var head = pr["head"] as JsonObject;
        var target = pr["base"] as JsonObject;
        var headSha = Str(head?["sha"]);
        var baseSha = Str(pr["merge_base"]) ?? Str(target?["sha"]);
        return new ChangeRequest(Long(pr["number"]), Str(pr["title"]) ?? "", State(pr), draft, Login(pr["user"]), Str(head?["ref"]) ?? "", Str(target?["ref"]) ?? "",
            headSha, Str(pr["html_url"]), Str(pr["created_at"]), Str(pr["updated_at"]))
        {
            Description = Str(pr["body"]) ?? "",
            Refs = headSha is null || baseSha is null ? null : new DiffRefs(baseSha, baseSha, headSha),
            Labels = (pr["labels"] as JsonArray ?? []).Select(l => Str(l?["name"]) ?? "").Where(l => l.Length > 0).ToList(),
            Reviewers = Logins(pr["requested_reviewers"]),
            Assignees = Logins(pr["assignees"]),
            MergeStatus = Str(pr["mergeable_state"]) ?? (pr["mergeable"] is JsonValue m ? (Bool(m) ? "mergeable" : "conflict") : null),
            HasConflicts = pr["mergeable"] is JsonValue c && c.TryGetValue<bool>(out var ok) ? !ok : null,
            MergedAt = Str(pr["merged_at"]),
            ClosedAt = Str(pr["closed_at"]),
            ChangesCount = pr["changed_files"] is JsonValue n ? Long(n).ToString(CultureInfo.InvariantCulture) : null,
        };
    }

    protected abstract bool IsDraft(JsonObject pr);

    public IReadOnlyList<ChangeRequest> ListChanges(long project, string state, string? updatedAfter, string? author, int limit)
    {
        // The host lists open, closed (merged or not) and all; merged and closed are told apart here.
        var asked = state switch { "opened" or "locked" => "open", "all" => "all", _ => "closed" };
        var rows = Pages($"/repos/{Name(project)}/pulls", ListQuery(asked), Math.Clamp(limit, 1, 100) * (state is "merged" or "closed" ? 3 : 1), PageSize);
        var since = updatedAfter is { Length: > 0 } && DateTimeOffset.TryParse(updatedAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        return rows.Select(pr => Change(pr, IsDraft(pr)))
            .Where(c => state is "all" or "opened" or "locked" || c.State == state)
            .Where(c => since is null || (DateTimeOffset.TryParse(c.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u) && u > since))
            .Where(c => string.IsNullOrEmpty(author) || string.Equals(c.Author, author, StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToList();
    }

    protected abstract IEnumerable<(string, string)> ListQuery(string state);
    protected virtual string PageSize => "per_page";

    public ChangeRequest? GetChange(long project, long number) =>
        Json($"/repos/{Name(project)}/pulls/{number}") is JsonObject pr ? Change(pr, IsDraft(pr)) : null;

    /// <summary>One version: the head as it is now (neither host keeps the pushes as GitLab does).</summary>
    public IReadOnlyList<ChangeVersion> Versions(long project, long number) =>
        GetChange(project, number) is { Refs: { } r } c ? [new ChangeVersion(1, r.HeadSha, r.BaseSha, r.StartSha, c.UpdatedAt)] : [];

    public abstract IReadOnlyList<FileDiff> Diff(long project, long number, long? version = null);

    public IReadOnlyList<CommitSummary> Commits(long project, long number) =>
        Pages($"/repos/{Name(project)}/pulls/{number}/commits", [], 250, PageSize).Select(Commit).ToList();

    protected static CommitSummary Commit(JsonObject c)
    {
        var message = Str(c["commit"]?["message"]) ?? "";
        return new CommitSummary(Str(c["sha"]) ?? "", message.Split('\n')[0], message, Str(c["commit"]?["author"]?["name"]) ?? Login(c["author"]), Str(c["commit"]?["author"]?["date"]));
    }

    /// <summary>The conversation's comments, each its own thread (neither host resolves them as GitLab does).</summary>
    public IReadOnlyList<Discussion> Discussions(long project, long number) =>
        Pages($"/repos/{Name(project)}/issues/{number}/comments", [], 500, PageSize)
            .Select(c => new Discussion(Str(c["id"]) ?? "", [new Note(Long(c["id"]), Login(c["user"]), Str(c["body"]) ?? "", Str(c["created_at"]), false, false, false, null)]))
            .ToList();

    public abstract IReadOnlyList<Pipeline> Pipelines(long project, long? number, string? sha);
    public abstract IReadOnlyList<CiJob> Jobs(long project, long pipeline);
    public abstract string? JobLog(long project, long job);
    public abstract Comparison Compare(long project, string from, string to, bool straight = false);

    public string? FileAt(long project, string sha, string path)
    {
        var resp = Get($"/repos/{Name(project)}/{RawPath(Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal), sha)}", accept: "application/vnd.github.raw");
        if (resp.Status == 404) return null;
        if (resp.Status != 200) throw new CodeHostError($"{Title} answered {resp.Status} for {path} at {sha}", resp.Status);
        return resp.Text;
    }

    protected abstract string RawPath(string path, string sha);

    public abstract string ChangeRef(long number);

    /// <summary>A CI state in GitLab's words, which the tools and the app read: success, failed, canceled, running, pending, skipped.</summary>
    protected static string CiState(string? status, string? conclusion) => (conclusion ?? status) switch
    {
        "success" => "success",
        "failure" or "error" or "timed_out" or "startup_failure" => "failed",
        "cancelled" or "canceled" => "canceled",
        "skipped" or "neutral" => "skipped",
        "in_progress" or "running" => "running",
        _ => "pending",
    };
}

/// <summary>GitHub (or GitHub Enterprise at ARGUS_GITHUB_API): pull requests, Actions runs and compare.</summary>
public sealed class GitHubHost(string apiUrl, string token, HttpClient client) : RestHost(apiUrl, token, client)
{
    public override string Kind => ForeignHosts.GitHub;
    protected override string Authorization => $"Bearer {Token}";
    protected override bool IsDraft(JsonObject pr) => Bool(pr["draft"]);
    protected override IEnumerable<(string, string)> ListQuery(string state) => [("state", state), ("sort", "updated"), ("direction", "desc")];
    protected override string RawPath(string path, string sha) => $"contents/{path}?ref={Uri.EscapeDataString(sha)}";
    public override string ChangeRef(long number) => $"refs/pull/{number}/head";

    static FileDiff File(JsonObject f)
    {
        var status = Str(f["status"]);
        var patch = Str(f["patch"]);
        var name = Str(f["filename"]) ?? "";
        return new FileDiff(Str(f["previous_filename"]) ?? name, name, status == "added", status == "removed", status == "renamed", patch is null ? "" : patch + "\n")
        {
            // No patch: binary, or larger than GitHub shows.
            TooLarge = patch is null && Long(f["changes"]) > 0,
            Binary = patch is null && Long(f["changes"]) == 0 && status != "renamed",
        };
    }

    public override IReadOnlyList<FileDiff> Diff(long project, long number, long? version = null) =>
        Pages($"/repos/{Name(project)}/pulls/{number}/files", [], 3000).Select(File).ToList();

    public override IReadOnlyList<Pipeline> Pipelines(long project, long? number, string? sha)
    {
        var at = sha ?? (number is { } n ? GetChange(project, n)?.Sha : null);
        if (at is null) return [];
        var runs = Json($"/repos/{Name(project)}/actions/runs", [("head_sha", at), ("per_page", "20")])?["workflow_runs"] as JsonArray ?? [];
        return runs.OfType<JsonObject>().Select(r => new Pipeline(Long(r["id"]), CiState(Str(r["status"]), Str(r["conclusion"])), Str(r["head_sha"]), Str(r["head_branch"]),
            Str(r["html_url"]), Str(r["created_at"]), Str(r["updated_at"]))).ToList();
    }

    public override IReadOnlyList<CiJob> Jobs(long project, long pipeline) =>
        Pages($"/repos/{Name(project)}/actions/runs/{pipeline}/jobs", [], 200, items: n => n["jobs"] as JsonArray).Select(j =>
        {
            double? took = DateTimeOffset.TryParse(Str(j["started_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s)
                && DateTimeOffset.TryParse(Str(j["completed_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? (e - s).TotalSeconds : null;
            return new CiJob(Long(j["id"]), Str(j["name"]) ?? "", Str(j["workflow_name"]) ?? "", CiState(Str(j["status"]), Str(j["conclusion"])), null, false, took, Str(j["html_url"]));
        }).ToList();

    public override string? JobLog(long project, long job)
    {
        // GitHub answers with a redirect to the log's storage, which the client follows (without the token).
        var resp = Get($"/repos/{Name(project)}/actions/jobs/{job}/logs", accept: "application/vnd.github+json");
        return resp.Status == 200 ? resp.Text : resp.Status == 404 ? null : throw new CodeHostError($"GitHub answered {resp.Status} for job {job}'s log", resp.Status);
    }

    public override Comparison Compare(long project, string from, string to, bool straight = false)
    {
        var node = Json($"/repos/{Name(project)}/compare/{Uri.EscapeDataString(from)}{(straight ? ".." : "...")}{Uri.EscapeDataString(to)}") as JsonObject
                   ?? throw new CodeHostError($"GitHub cannot compare {from} with {to}", 404);
        var commits = (node["commits"] as JsonArray ?? []).OfType<JsonObject>().Select(Commit).ToList();
        var files = (node["files"] as JsonArray ?? []).OfType<JsonObject>().Select(File).ToList();
        return new Comparison(commits, files, false);
    }
}

/// <summary>Gitea (at ARGUS_GITEA_URL): pull requests with their diff, commit statuses as CI. Comparing two refs is GitHub's and GitLab's only.</summary>
public sealed class GiteaHost(string apiUrl, string token, HttpClient client) : RestHost(apiUrl, token, client)
{
    public override string Kind => ForeignHosts.Gitea;
    protected override string Authorization => $"token {Token}";
    protected override string PageSize => "limit";

    // Gitea marks a draft by its title's prefix (WIP:, [WIP]); newer versions say draft too.
    protected override bool IsDraft(JsonObject pr) => Bool(pr["draft"]) ||
        (Str(pr["title"]) ?? "").TrimStart() is var t && (t.StartsWith("WIP:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[WIP]", StringComparison.OrdinalIgnoreCase));

    protected override IEnumerable<(string, string)> ListQuery(string state) => [("state", state), ("sort", "recentupdate")];
    protected override string RawPath(string path, string sha) => $"raw/{path}?ref={Uri.EscapeDataString(sha)}";
    public override string ChangeRef(long number) => $"refs/pull/{number}/head";

    public override IReadOnlyList<FileDiff> Diff(long project, long number, long? version = null)
    {
        var resp = Get($"/repos/{Name(project)}/pulls/{number}.diff", accept: "text/plain");
        if (resp.Status == 404) throw new CodeHostError($"Gitea has no pull request {number}", 404);
        if (resp.Status != 200) throw new CodeHostError($"Gitea answered {resp.Status} for pull request {number}'s diff", resp.Status);
        return UnifiedDiff.Parse(resp.Text);
    }

    /// <summary>The commit's statuses as one pipeline (id 0), its state the combined one.</summary>
    public override IReadOnlyList<Pipeline> Pipelines(long project, long? number, string? sha)
    {
        var at = sha ?? (number is { } n ? GetChange(project, n)?.Sha : null);
        if (at is null || Json($"/repos/{Name(project)}/commits/{at}/status") is not JsonObject combined || (combined["statuses"] as JsonArray ?? []).Count == 0) return [];
        _shas[project] = at;
        return [new Pipeline(0, CiState(Str(combined["state"]), null), at, null, null, null, null)];
    }

    readonly ConcurrentDictionary<long, string> _shas = new();

    public override IReadOnlyList<CiJob> Jobs(long project, long pipeline)
    {
        if (!_shas.TryGetValue(project, out var sha)) return [];
        var combined = Json($"/repos/{Name(project)}/commits/{sha}/status") as JsonObject;
        return (combined?["statuses"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => new CiJob(Long(s["id"]), Str(s["context"]) ?? "", "", CiState(Str(s["status"]) ?? Str(s["state"]), null), Str(s["description"]), false, null, Str(s["target_url"])))
            .ToList();
    }

    public override string? JobLog(long project, long job) => null;

    public override Comparison Compare(long project, string from, string to, bool straight = false) =>
        throw new CodeHostError("Gitea's API does not give the diff of two refs: review its pull request instead", 501);
}
