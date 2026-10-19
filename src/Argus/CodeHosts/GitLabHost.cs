using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Util;

namespace Argus.CodeHosts;

/// <summary>
/// GitLab's merge requests, read with Argus's service credential (read_api and read_repository, Reporter
/// on each project). Every request is a GET through <see cref="Credentials.Authorized"/>: this class has
/// no other way to send one, so it cannot write to GitLab whatever a caller asks of it.
/// </summary>
public sealed class GitLabHost(GitLabConfig cfg, HttpClient? client = null) : ICodeHost
{
    const int PerPage = 100;
    /// <summary>At most this many pages of any one list: 10,000 diffs, commits or notes.</summary>
    const int MaxPages = 100;

    readonly Lazy<HttpClient> _client = new(() => client ?? Tls.ClientFor(cfg, 30.0));

    public string Kind => "gitlab";

    public string ChangeRef(long number) => $"refs/merge-requests/{number}/head";

    HttpResult Get(string path, IEnumerable<(string, string)>? query = null)
    {
        try
        {
            return Credentials.Authorized(cfg, auth => Tls.Get(_client.Value, $"{cfg.Url}/api/v4{path}", auth, query));
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or CredentialError)
        {
            throw new CodeHostError($"GitLab could not be reached at {cfg.Redacted()}: {exc.Message}", 0, exc);
        }
    }

    /// <summary>The answer as JSON; null for a 404; a <see cref="CodeHostError"/> for any other failure.</summary>
    JsonNode? Json(string path, IEnumerable<(string, string)>? query = null)
    {
        var resp = Get(path, query);
        if (resp.Status == 404) return null;
        if (resp.Status != 200) throw new CodeHostError($"GitLab answered {resp.Status} for GET {path}: {PyStr.Prefix(resp.Text, 200)}", resp.Status);
        try { return resp.Json(); }
        catch (JsonException exc) { throw new CodeHostError($"GitLab's answer to GET {path} is not JSON: {PyStr.Prefix(resp.Text, 200)}", resp.Status, exc); }
    }

    /// <summary>Every page of a list, up to <paramref name="limit"/> items; null when GitLab has no such list (404).</summary>
    List<JsonObject>? Pages(string path, IEnumerable<(string, string)> query, int limit = int.MaxValue)
    {
        var items = new List<JsonObject>();
        var size = Math.Min(PerPage, Math.Max(1, limit));
        for (var page = 1; page <= MaxPages && items.Count < limit; page++)
        {
            var node = Json(path, [.. query, ("per_page", size.ToString(CultureInfo.InvariantCulture)), ("page", page.ToString(CultureInfo.InvariantCulture))]);
            if (node is null) return page == 1 ? null : items;
            if (node is not JsonArray batch) throw new CodeHostError($"GitLab's answer to GET {path} is not a list.");
            items.AddRange(batch.OfType<JsonObject>());
            if (batch.Count < size) break;
        }
        return items.Count > limit ? items.GetRange(0, limit) : items;
    }

    static string Project(long project) => $"/projects/{project}";

    /// <summary>A list GitLab answered 404 for: the thing it belongs to does not exist.</summary>
    static CodeHostError Missing(string what) => new($"GitLab has no {what}", 404);

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n ? n.ToJsonString() : null;
    static long Long(JsonNode? node) => node is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : long.TryParse(Str(v), out l) ? l : 0) : 0;
    static long? OptLong(JsonNode? node) => node is JsonValue ? Long(node) : null;
    static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string User(JsonNode? node) => Str(node?["username"]) ?? "";

    static ChangeRequest Change(JsonObject m)
    {
        var refs = m["diff_refs"] as JsonObject;
        var pipeline = m["head_pipeline"] as JsonObject;
        return new ChangeRequest(Long(m["iid"]), Str(m["title"]) ?? "", Str(m["state"]) ?? "", Bool(m["draft"]) || Bool(m["work_in_progress"]),
            User(m["author"]), Str(m["source_branch"]) ?? "", Str(m["target_branch"]) ?? "", Str(m["sha"]), Str(m["web_url"]),
            Str(m["created_at"]), Str(m["updated_at"]))
        {
            Description = Str(m["description"]),
            Refs = refs is null || Str(refs["head_sha"]) is null ? null
                : new DiffRefs(Str(refs["base_sha"]) ?? "", Str(refs["start_sha"]) ?? "", Str(refs["head_sha"]) ?? ""),
            Labels = (m["labels"] as JsonArray ?? []).Select(l => l is JsonObject o ? Str(o["name"]) ?? "" : Str(l) ?? "").Where(l => l.Length > 0).ToList(),
            Reviewers = (m["reviewers"] as JsonArray ?? []).Select(User).Where(u => u.Length > 0).ToList(),
            Assignees = (m["assignees"] as JsonArray ?? []).Select(User).Where(u => u.Length > 0).ToList(),
            MergeStatus = Str(m["detailed_merge_status"]) ?? Str(m["merge_status"]),
            HasConflicts = m["has_conflicts"] is JsonValue ? Bool(m["has_conflicts"]) : null,
            HeadPipeline = pipeline is null ? null
                : new PipelineRef(Long(pipeline["id"]), Str(pipeline["status"]) ?? "", Str(pipeline["sha"]), Str(pipeline["ref"]), Str(pipeline["web_url"])),
            MergedAt = Str(m["merged_at"]),
            ClosedAt = Str(m["closed_at"]),
            ChangesCount = Str(m["changes_count"]),
        };
    }

    static FileDiff FileOf(JsonObject d)
    {
        var text = Str(d["diff"]) ?? "";
        return new FileDiff(Str(d["old_path"]) ?? "", Str(d["new_path"]) ?? "", Bool(d["new_file"]), Bool(d["deleted_file"]), Bool(d["renamed_file"]), text)
        {
            TooLarge = Bool(d["too_large"]) || (Bool(d["collapsed"]) && text.Length == 0),
            Binary = text.StartsWith("Binary files ", StringComparison.Ordinal),
        };
    }

    static CommitSummary CommitOf(JsonObject c) =>
        new(Str(c["id"]) ?? "", Str(c["title"]) ?? "", Str(c["message"]) ?? "", Str(c["author_name"]) ?? "", Str(c["authored_date"]));

    public IReadOnlyList<ChangeRequest> ListChanges(long project, string state, string? updatedAfter, string? author, int limit)
    {
        var query = new List<(string, string)> { ("state", state), ("order_by", "updated_at"), ("sort", "desc") };
        if (!string.IsNullOrEmpty(updatedAfter)) query.Add(("updated_after", updatedAfter));
        if (!string.IsNullOrEmpty(author)) query.Add(("author_username", author));
        return (Pages($"{Project(project)}/merge_requests", query, limit) ?? throw Missing($"project {project}")).Select(Change).ToList();
    }

    public ChangeRequest? GetChange(long project, long number) =>
        Json($"{Project(project)}/merge_requests/{number}") is JsonObject m ? Change(m) : null;

    public IReadOnlyList<ChangeVersion> Versions(long project, long number) =>
        (Pages($"{Project(project)}/merge_requests/{number}/versions", []) ?? throw Missing($"merge request !{number}"))
        .Select(v => new ChangeVersion(Long(v["id"]), Str(v["head_commit_sha"]) ?? "", Str(v["base_commit_sha"]) ?? "", Str(v["start_commit_sha"]) ?? "",
            Str(v["created_at"])))
        .ToList();

    public IReadOnlyList<FileDiff> Diff(long project, long number, long? version = null)
    {
        var mr = $"{Project(project)}/merge_requests/{number}";
        if (version is { } v)
            return Json($"{mr}/versions/{v}") is JsonObject one
                ? (one["diffs"] as JsonArray ?? []).OfType<JsonObject>().Select(FileOf).ToList()
                : throw new CodeHostError($"merge request !{number} has no version {v}", 404);
        // /diffs pages the files (GitLab 15.7 and later); an older GitLab has only /changes.
        if (Pages($"{mr}/diffs", []) is { } files) return files.Select(FileOf).ToList();
        return Json($"{mr}/changes") is JsonObject changes
            ? (changes["changes"] as JsonArray ?? []).OfType<JsonObject>().Select(FileOf).ToList()
            : throw new CodeHostError($"no merge request !{number}", 404);
    }

    public IReadOnlyList<CommitSummary> Commits(long project, long number) =>
        (Pages($"{Project(project)}/merge_requests/{number}/commits", []) ?? throw Missing($"merge request !{number}")).Select(CommitOf).ToList();

    public IReadOnlyList<Discussion> Discussions(long project, long number) =>
        (Pages($"{Project(project)}/merge_requests/{number}/discussions", []) ?? throw Missing($"merge request !{number}"))
        .Select(d => new Discussion(Str(d["id"]) ?? "", (d["notes"] as JsonArray ?? []).OfType<JsonObject>().Select(n =>
        {
            var p = n["position"] as JsonObject;
            return new Note(Long(n["id"]), User(n["author"]), Str(n["body"]) ?? "", Str(n["created_at"]), Bool(n["system"]), Bool(n["resolvable"]),
                Bool(n["resolved"]),
                p is null ? null : new NotePosition(Str(p["base_sha"]), Str(p["start_sha"]), Str(p["head_sha"]), Str(p["old_path"]), Str(p["new_path"]),
                    OptLong(p["old_line"]), OptLong(p["new_line"])));
        }).ToList()))
        .ToList();

    public IReadOnlyList<Pipeline> Pipelines(long project, long? number, string? sha)
    {
        var rows = number is { } n
            ? Pages($"{Project(project)}/merge_requests/{n}/pipelines", [], 20)
            : Pages($"{Project(project)}/pipelines", [("sha", sha ?? ""), ("order_by", "id"), ("sort", "desc")], 20);
        return (rows ?? throw Missing(number is { } m ? $"merge request !{m}" : $"project {project}"))
            .Select(p => new Pipeline(Long(p["id"]), Str(p["status"]) ?? "", Str(p["sha"]), Str(p["ref"]), Str(p["web_url"]), Str(p["created_at"]), Str(p["updated_at"])))
            .OrderByDescending(p => p.Id)
            .ToList();
    }

    public IReadOnlyList<CiJob> Jobs(long project, long pipeline) =>
        (Pages($"{Project(project)}/pipelines/{pipeline}/jobs", [("include_retried", "false")]) ?? throw Missing($"pipeline {pipeline}"))
        .Select(j => new CiJob(Long(j["id"]), Str(j["name"]) ?? "", Str(j["stage"]) ?? "", Str(j["status"]) ?? "", Str(j["failure_reason"]),
            Bool(j["allow_failure"]), j["duration"] is JsonValue d && d.TryGetValue<double>(out var s) ? s : null, Str(j["web_url"])))
        .ToList();

    public string? JobLog(long project, long job)
    {
        var resp = Get($"{Project(project)}/jobs/{job}/trace");
        if (resp.Status == 404) return null;
        if (resp.Status != 200) throw new CodeHostError($"GitLab answered {resp.Status} for job {job}'s log", resp.Status);
        return resp.Text;
    }

    public Comparison Compare(long project, string from, string to, bool straight = false)
    {
        var node = Json($"{Project(project)}/repository/compare", [("from", from), ("to", to), ("straight", straight ? "true" : "false")])
                   ?? throw new CodeHostError($"GitLab could not compare {from} with {to}", 404);
        return new Comparison(
            (node["commits"] as JsonArray ?? []).OfType<JsonObject>().Select(CommitOf).ToList(),
            (node["diffs"] as JsonArray ?? []).OfType<JsonObject>().Select(FileOf).ToList(),
            Bool(node["compare_timeout"]));
    }

    public string? FileAt(long project, string sha, string path)
    {
        var resp = Get($"{Project(project)}/repository/files/{Uri.EscapeDataString(path)}/raw", [("ref", sha)]);
        if (resp.Status == 404) return null;
        if (resp.Status != 200) throw new CodeHostError($"GitLab answered {resp.Status} for {path} at {sha}", resp.Status);
        return resp.Text;
    }
}
