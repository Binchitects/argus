using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Settings;
using Microsoft.Extensions.Options;

namespace Llm.Api.Schedules;

/// <summary>What started a run besides the schedule: the event in words, for the question, where in GitLab to answer, if anywhere, and its facts.</summary>
public sealed record TriggerEvent(string Text, GitLabTarget? Reply, JsonObject? Facts = null);

/// <summary>A merge request, an issue or a commit in a GitLab project (its ID), to comment on.</summary>
public sealed record GitLabTarget(long Project, string Kind, string Id);

/// <summary>Configuration section "GitLab": where tasks read events' details and comment. The bot's token is its own, never Argus's read-only one.</summary>
public sealed class GitLabOptions
{
    /// <summary>GitLab's address, as the app reaches it; empty: Argus's (GITLAB_URL).</summary>
    public string? Url { get; set; }
    /// <summary>A bot account's token (scope api): reads a merge request's changes and a job's log, and writes the comments.</summary>
    public string? BotToken { get; set; }
}

/// <summary>The app's own GitLab account for triggers: reads what an event's payload leaves out, and comments the answer back.</summary>
public sealed class GitLabBot(IHttpClientFactory http, IOptionsMonitor<GitLabOptions> options, IOptions<Operations.ArgusOptions> argus)
{
    public const string Client = "gitlab-bot";

    private string? BaseUrl => (string.IsNullOrWhiteSpace(options.CurrentValue.Url) ? argus.Value.GitlabUrl : options.CurrentValue.Url)?.TrimEnd('/');

    public bool Ready => BaseUrl is { Length: > 0 } && options.CurrentValue.BotToken is { Length: > 0 };

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}/api/v4{path}");
        request.Headers.Add("PRIVATE-TOKEN", options.CurrentValue.BotToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>A GET of the API, as text; null when the bot is not set up or GitLab says no.</summary>
    public async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        if (!Ready)
        {
            return null;
        }
        try
        {
            using var request = Request(HttpMethod.Get, path);
            using var response = await http.CreateClient(Client).SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>A GET of the API with GitLab's status: 0 when the bot is not set up or GitLab cannot be reached (company knowledge tells "gone" from "unreachable").</summary>
    public async Task<(int Status, string? Body)> ReadAsync(string path, CancellationToken ct)
    {
        if (!Ready)
        {
            return (0, null);
        }
        try
        {
            using var request = Request(HttpMethod.Get, path);
            using var response = await http.CreateClient(Client).SendAsync(request, ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (0, null);
        }
    }

    /// <summary>Comments on a merge request, an issue or a commit; throws with GitLab's reason when it cannot.</summary>
    public Task CommentAsync(GitLabTarget target, string body, CancellationToken ct) =>
        NoteAsync(target.Project.ToString(CultureInfo.InvariantCulture), target.Kind, target.Id, body, ct);

    /// <summary>Comments in a project (its ID, or its path escaped) on a merge request, an issue or a commit.</summary>
    public async Task NoteAsync(string project, string kind, string id, string body, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, kind == "commits" ? $"/projects/{project}/repository/commits/{id}/comments" : $"/projects/{project}/{kind}/{id}/notes",
            new JsonObject { [kind == "commits" ? "note" : "body"] = body }, ct);

    /// <summary>A change through the API (a comment, labels, an issue): GitLab's answer; throws with GitLab's reason when it says no.</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken ct)
    {
        if (!Ready)
        {
            throw new InvalidOperationException("the GitLab bot is not set up (Settings → Scheduled tasks)");
        }
        using var request = Request(method, path);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.CreateClient(Client).SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab said {(int)response.StatusCode} {response.ReasonPhrase}{(text.Length is > 0 and < 300 ? $": {text}" : "")}");
        }
        try
        {
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which of a GitLab event's kind a trigger takes: the branches (globs, comma separated: main, release/*) a merge request
/// targets or a push, a pipeline, a job or a deployment ran on; the labels a merge request or issue has one of; the words a
/// comment holds (a mention, @arena).
/// </summary>
public sealed record GitLabFilter(string? Branches = null, string? Labels = null, string? Words = null);

/// <summary>Events that run a task: GitLab's (merge requests, pipelines, jobs, issues, comments, pushes, tags, releases, deployments, wiki pages), or any JSON a system posts.</summary>
public static class Triggers
{
    public const string Schedule = "schedule";
    public const string Webhook = "webhook";
    public const string GitLab = "gitlab";
    public static readonly string[] Kinds = [Schedule, Webhook, GitLab];

    public static readonly string[] GitLabEvents =
    [
        "merge_request", "merge_request_merged", "merge_request_approved", "merge_request_closed", "pipeline_failed", "pipeline_succeeded", "job_failed",
        "issue", "issue_closed", "comment", "push", "tag", "release", "deployment", "wiki_page",
    ];

    /// <summary>What a comment of the app's own ends with: a comment that has it does not start anything (no loops).</summary>
    public const string Signature = "by Argus Arena for";

    private const int DiffChars = 30_000;
    private const int LogChars = 4_000;

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";

    private static List<string> List(string? text) =>
        [.. (text ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Whether a branch is one the filter takes (a glob each: * any characters); no filter takes every branch.</summary>
    public static bool BranchTaken(string? branches, string branch) =>
        List(branches) is not { Count: > 0 } globs || globs.Any(g => System.Text.RegularExpressions.Regex.IsMatch(branch,
            "^" + System.Text.RegularExpressions.Regex.Escape(g).Replace("\\*", ".*", StringComparison.Ordinal) + "$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));

    private static List<string> LabelsOf(JsonObject e) =>
        [.. (e["labels"] as JsonArray ?? e["object_attributes"]?["labels"] as JsonArray ?? []).OfType<JsonObject>().Select(l => Str(l["title"])).Where(l => l.Length > 0)];

    private static bool LabelTaken(string? labels, List<string> has) =>
        List(labels) is not { Count: > 0 } wanted || wanted.Any(w => has.Contains(w, StringComparer.OrdinalIgnoreCase));

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>
    /// GitLab's event, when the trigger takes it: its words (with a merge request's changes, or the end of a failed job's
    /// log), where to answer, and its facts (kind, project, branch, author, title, link, labels) for a workflow's steps.
    /// </summary>
    public static async Task<TriggerEvent?> FromGitLabAsync(JsonObject e, IReadOnlyCollection<string> wanted, GitLabBot bot, CancellationToken ct, GitLabFilter? filter = null)
    {
        filter ??= new GitLabFilter();
        var project = e["project"] as JsonObject;
        var projectId = project?["id"]?.GetValue<long>() ?? e["project_id"]?.GetValue<long>() ?? 0;
        var path = Str(project?["path_with_namespace"]) is { Length: > 0 } p ? p : "the project";
        var attrs = e["object_attributes"] as JsonObject ?? [];
        var who = Str(e["user"]?["name"]) is { Length: > 0 } n ? n : Str(e["user_name"]) is { Length: > 0 } un ? un : "someone";
        JsonObject Facts(string kind, string? branch, string? title, string? url, string? iid = null) => new()
        {
            ["event"] = kind, ["project"] = path, ["projectId"] = projectId, ["branch"] = branch, ["title"] = title, ["url"] = url, ["iid"] = iid,
            ["author"] = who, ["labels"] = new JsonArray([.. LabelsOf(e).Select(l => (JsonNode?)JsonValue.Create(l))]),
        };
        string Ref(string r) => r.StartsWith("refs/heads/", StringComparison.Ordinal) ? r["refs/heads/".Length..] : r.StartsWith("refs/tags/", StringComparison.Ordinal) ? r["refs/tags/".Length..] : r;
        switch (Str(e["object_kind"]))
        {
            case "merge_request":
            {
                var action = Str(attrs["action"]);
                // Opened, opened again, new commits (not a title edit); merged; approved; closed.
                var kind = action switch
                {
                    "open" or "reopen" => "merge_request",
                    "update" when attrs["oldrev"] is not null => "merge_request",
                    "merge" => "merge_request_merged",
                    "approved" or "approval" => "merge_request_approved",
                    "close" => "merge_request_closed",
                    _ => null,
                };
                var target = Str(attrs["target_branch"]);
                if (kind is null || !wanted.Contains(kind) || !BranchTaken(filter.Branches, target) || !LabelTaken(filter.Labels, LabelsOf(e)))
                {
                    return null;
                }
                var iid = attrs["iid"]?.GetValue<long>() ?? 0;
                var said = action switch
                {
                    "update" => "updated with new commits", "open" => "opened", "reopen" => "reopened", "merge" => "merged", "close" => "closed", _ => "approved",
                };
                var text = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"GitLab event: merge request !{iid} was {said} by {who} in {path}: ")
                    .Append(CultureInfo.InvariantCulture, $"\"{attrs["title"]}\" ({attrs["source_branch"]} → {target}), {attrs["url"]}\n\n")
                    .Append("Its description:\n").Append(Str(attrs["description"]) is { Length: > 0 } d ? d : "(none)").Append('\n');
                if (kind == "merge_request" && await bot.GetAsync($"/projects/{projectId}/merge_requests/{iid}/changes", ct) is { } json && JsonNode.Parse(json)?["changes"] is JsonArray changes)
                {
                    var diff = string.Join("\n", changes.OfType<JsonObject>().Select(c => $"--- {c["old_path"]}\n+++ {c["new_path"]}\n{c["diff"]}"));
                    text.Append("\nIts changes:\n```diff\n").Append(Cut(diff, DiffChars)).Append("\n```\n");
                }
                return new TriggerEvent(text.ToString(), new GitLabTarget(projectId, "merge_requests", iid.ToString(CultureInfo.InvariantCulture)),
                    Facts(kind, target, Str(attrs["title"]), Str(attrs["url"]), iid.ToString(CultureInfo.InvariantCulture)));
            }
            case "pipeline":
            {
                var status = Str(attrs["status"]);
                var kind = status switch { "failed" => "pipeline_failed", "success" => "pipeline_succeeded", _ => null };
                var branch = Str(attrs["ref"]);
                if (kind is null || !wanted.Contains(kind) || !BranchTaken(filter.Branches, branch))
                {
                    return null;
                }
                var sha = Str(attrs["sha"]);
                var failed = (e["builds"] as JsonArray ?? []).OfType<JsonObject>().Where(b => Str(b["status"]) == "failed").ToList();
                var text = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"GitLab event: pipeline #{attrs["id"]} {(kind == "pipeline_failed" ? "failed" : "passed")} on {branch} ({Short(sha)}) in {path}. ");
                if (kind == "pipeline_failed")
                {
                    text.Append(CultureInfo.InvariantCulture, $"Failed jobs: {string.Join(", ", failed.Select(b => $"{b["name"]} (stage {b["stage"]})"))}.\n");
                    foreach (var job in failed.Take(3))
                    {
                        if (await bot.GetAsync($"/projects/{projectId}/jobs/{job["id"]}/trace", ct) is { } log)
                        {
                            text.Append(CultureInfo.InvariantCulture, $"\nThe end of {job["name"]}'s log:\n```\n{(log.Length > LogChars ? log[^LogChars..] : log)}\n```\n");
                        }
                    }
                }
                var mr = e["merge_request"]?["iid"]?.GetValue<long>();
                var url = Str(project?["web_url"]) is { Length: > 0 } web ? $"{web}/-/pipelines/{attrs["id"]}" : null;
                return new TriggerEvent(text.ToString(), mr is { } m
                    ? new GitLabTarget(projectId, "merge_requests", m.ToString(CultureInfo.InvariantCulture))
                    : sha.Length > 0 ? new GitLabTarget(projectId, "commits", sha) : null,
                    Facts(kind, branch, $"Pipeline #{attrs["id"]}", url, mr?.ToString(CultureInfo.InvariantCulture)));
            }
            case "build":
            {
                var branch = Str(e["ref"]);
                if (Str(e["build_status"]) != "failed" || !wanted.Contains("job_failed") || !BranchTaken(filter.Branches, branch))
                {
                    return null;
                }
                var sha = Str(e["sha"]) is { Length: > 0 } s1 ? s1 : Str(e["commit"]?["sha"]);
                var text = new StringBuilder($"GitLab event: job {e["build_name"]} (stage {e["build_stage"]}) failed on {branch} ({Short(sha)}) in {path}: {e["build_failure_reason"]}.\n");
                if (await bot.GetAsync($"/projects/{projectId}/jobs/{e["build_id"]}/trace", ct) is { } log)
                {
                    text.Append(CultureInfo.InvariantCulture, $"\nThe end of its log:\n```\n{(log.Length > LogChars ? log[^LogChars..] : log)}\n```\n");
                }
                var url = Str(e["repository"]?["homepage"]) is { Length: > 0 } home ? $"{home}/-/jobs/{e["build_id"]}" : null;
                return new TriggerEvent(text.ToString(), sha.Length > 0 ? new GitLabTarget(projectId, "commits", sha) : null, Facts("job_failed", branch, Str(e["build_name"]), url));
            }
            case "issue" or "work_item":
            {
                var action = Str(attrs["action"]);
                var kind = action switch { "open" or "reopen" => "issue", "close" => "issue_closed", _ => null };
                if (kind is null || !wanted.Contains(kind) || !LabelTaken(filter.Labels, LabelsOf(e)))
                {
                    return null;
                }
                var iid = attrs["iid"]?.GetValue<long>() ?? 0;
                return new TriggerEvent(
                    $"GitLab event: issue #{iid} was {(action == "close" ? "closed" : action == "open" ? "opened" : "reopened")} by {who} in {path}: \"{attrs["title"]}\", {attrs["url"]}\n\nIts description:\n{(Str(attrs["description"]) is { Length: > 0 } d ? d : "(none)")}\n",
                    new GitLabTarget(projectId, "issues", iid.ToString(CultureInfo.InvariantCulture)),
                    Facts(kind, null, Str(attrs["title"]), Str(attrs["url"]), iid.ToString(CultureInfo.InvariantCulture)));
            }
            case "note":
            {
                var note = Str(attrs["note"]);
                // A system note (a label added), or the app's own comment, starts nothing.
                if (!wanted.Contains("comment") || attrs["system"]?.GetValue<bool>() == true || note.Contains(Signature, StringComparison.Ordinal)
                    || (filter.Words is { Length: > 0 } words && !List(words).Any(w => note.Contains(w, StringComparison.OrdinalIgnoreCase))))
                {
                    return null;
                }
                var (on, target, title, iid) = Str(attrs["noteable_type"]) switch
                {
                    "MergeRequest" => ($"merge request !{e["merge_request"]?["iid"]}", new GitLabTarget(projectId, "merge_requests", Str(e["merge_request"]?["iid"])), Str(e["merge_request"]?["title"]), Str(e["merge_request"]?["iid"])),
                    "Issue" => ($"issue #{e["issue"]?["iid"]}", new GitLabTarget(projectId, "issues", Str(e["issue"]?["iid"])), Str(e["issue"]?["title"]), Str(e["issue"]?["iid"])),
                    "Commit" => ($"commit {Short(Str(e["commit"]?["id"]))}", new GitLabTarget(projectId, "commits", Str(e["commit"]?["id"])), Str(e["commit"]?["message"]).Split('\n')[0], null),
                    _ => ("a snippet", (GitLabTarget?)null, "", null),
                };
                if (target is not null && !LabelTaken(filter.Labels, LabelsOf(e["merge_request"] as JsonObject ?? e["issue"] as JsonObject ?? [])))
                {
                    return null;
                }
                var about = e["merge_request"]?["description"] ?? e["issue"]?["description"];
                return new TriggerEvent(
                    $"GitLab event: {who} commented on {on} in {path} (\"{title}\"), {attrs["url"]}:\n\n{note}\n" + (Str(about) is { Length: > 0 } d ? $"\nIts description:\n{d}\n" : ""),
                    target, Facts("comment", Str(e["merge_request"]?["target_branch"]) is { Length: > 0 } b ? b : null, title, Str(attrs["url"]), iid));
            }
            case "push":
            {
                var branch = Ref(Str(e["ref"]));
                var after = Str(e["after"]);
                // A branch deleted (its new commit all zeros) is no push to read.
                if (!wanted.Contains("push") || after.Trim('0').Length == 0 || !BranchTaken(filter.Branches, branch))
                {
                    return null;
                }
                var commits = (e["commits"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var text = new StringBuilder($"GitLab event: {who} pushed {e["total_commits_count"] ?? commits.Count} commit(s) to {branch} in {path}:\n");
                foreach (var c in commits.TakeLast(20))
                {
                    text.Append(CultureInfo.InvariantCulture, $"- {Short(Str(c["id"]))} {Str(c["message"]).Split('\n')[0]} ({c["author"]?["name"]})\n");
                }
                var url = Str(project?["web_url"]) is { Length: > 0 } web ? $"{web}/-/compare/{Short(Str(e["before"]))}...{Short(after)}" : null;
                return new TriggerEvent(text.ToString(), new GitLabTarget(projectId, "commits", after), Facts("push", branch, Str(commits.LastOrDefault()?["message"]).Split('\n')[0], url));
            }
            case "tag_push":
            {
                var tag = Ref(Str(e["ref"]));
                var sha = Str(e["checkout_sha"]);
                if (!wanted.Contains("tag") || sha.Length == 0)
                {
                    return null;
                }
                var url = Str(project?["web_url"]) is { Length: > 0 } web ? $"{web}/-/tags/{tag}" : null;
                return new TriggerEvent($"GitLab event: {who} pushed the tag {tag} ({Short(sha)}) in {path}.{(Str(e["message"]) is { Length: > 0 } m ? $" Its message: {m}" : "")}\n",
                    new GitLabTarget(projectId, "commits", sha), Facts("tag", tag, tag, url));
            }
            case "release":
            {
                var action = Str(e["action"]);
                if (!wanted.Contains("release") || action is not ("create" or "update"))
                {
                    return null;
                }
                return new TriggerEvent(
                    $"GitLab event: release {e["name"]} ({e["tag"]}) was {(action == "create" ? "made" : "changed")} in {path}, {e["url"]}\n\nIts notes:\n{(Str(e["description"]) is { Length: > 0 } d ? d : "(none)")}\n",
                    null, Facts("release", Str(e["tag"]), Str(e["name"]), Str(e["url"])));
            }
            case "deployment":
            {
                var status = Str(e["status"]);
                var branch = Str(e["ref"]);
                if (!wanted.Contains("deployment") || status is not ("success" or "failed") || !BranchTaken(filter.Branches, branch))
                {
                    return null;
                }
                var sha = Str(e["sha"]);
                return new TriggerEvent($"GitLab event: the deployment to {e["environment"]} of {branch} ({Short(sha)}) {(status == "success" ? "succeeded" : "failed")} in {path}, {e["deployable_url"]}\n",
                    sha.Length > 0 ? new GitLabTarget(projectId, "commits", sha) : null, Facts("deployment", branch, $"{e["environment"]}: {status}", Str(e["deployable_url"])));
            }
            case "wiki_page":
            {
                var action = Str(attrs["action"]);
                if (!wanted.Contains("wiki_page") || action is not ("create" or "update"))
                {
                    return null;
                }
                return new TriggerEvent(
                    $"GitLab event: the wiki page \"{attrs["title"]}\" was {(action == "create" ? "made" : "changed")} by {who} in {path}, {attrs["url"]}\n\n{Cut(Str(attrs["content"]), DiffChars)}\n",
                    null, Facts("wiki_page", null, Str(attrs["title"]), Str(attrs["url"])));
            }
            default:
                return null;
        }
    }

    /// <summary>Any system's JSON, as it came, for the question.</summary>
    public static TriggerEvent FromJson(string body) => new($"An event came in:\n```json\n{Cut(body, DiffChars)}\n```\n", null);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "\n[cut to fit]";
}
