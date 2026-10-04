using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Settings;
using Microsoft.Extensions.Options;

namespace Llm.Api.Schedules;

/// <summary>What started a run besides the schedule: the event in words, for the question, and where in GitLab to answer, if anywhere.</summary>
public sealed record TriggerEvent(string Text, GitLabTarget? Reply);

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

    /// <summary>Comments on a merge request, an issue or a commit; throws with GitLab's reason when it cannot.</summary>
    public async Task CommentAsync(GitLabTarget target, string body, CancellationToken ct)
    {
        if (!Ready)
        {
            throw new InvalidOperationException("the GitLab bot is not set up (Settings → Scheduled tasks)");
        }
        var path = target.Kind == "commits"
            ? $"/projects/{target.Project}/repository/commits/{target.Id}/comments"
            : $"/projects/{target.Project}/{target.Kind}/{target.Id}/notes";
        using var request = Request(HttpMethod.Post, path);
        request.Content = new StringContent(new JsonObject { [target.Kind == "commits" ? "note" : "body"] = body }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.CreateClient(Client).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab said {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }
}

/// <summary>Events that run a task: GitLab's (a merge request, a failed pipeline, an issue), or any JSON a system posts.</summary>
public static class Triggers
{
    public const string Schedule = "schedule";
    public const string Webhook = "webhook";
    public const string GitLab = "gitlab";
    public static readonly string[] Kinds = [Schedule, Webhook, GitLab];
    public static readonly string[] GitLabEvents = ["merge_request", "pipeline_failed", "issue"];

    private const int DiffChars = 30_000;
    private const int LogChars = 4_000;

    /// <summary>GitLab's event, when the task takes it: its words (with a merge request's changes, or the end of each failed job's log), and where to answer.</summary>
    public static async Task<TriggerEvent?> FromGitLabAsync(JsonObject e, IReadOnlyCollection<string> wanted, GitLabBot bot, CancellationToken ct)
    {
        var project = e["project"] as JsonObject;
        var projectId = project?["id"]?.GetValue<long>() ?? 0;
        var path = project?["path_with_namespace"]?.GetValue<string>() ?? "the project";
        var attrs = e["object_attributes"] as JsonObject ?? [];
        var who = e["user"]?["name"]?.GetValue<string>() ?? "someone";
        switch (e["object_kind"]?.GetValue<string>())
        {
            case "merge_request" when wanted.Contains("merge_request"):
            {
                var action = attrs["action"]?.GetValue<string>();
                // A new merge request, one opened again, or new commits in it; not a title edit or a merge.
                if (action is not ("open" or "reopen") && !(action == "update" && attrs["oldrev"] is not null))
                {
                    return null;
                }
                var iid = attrs["iid"]?.GetValue<long>() ?? 0;
                var text = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"GitLab event: merge request !{iid} was {(action == "update" ? "updated with new commits" : action == "open" ? "opened" : "reopened")} by {who} in {path}: ")
                    .Append(CultureInfo.InvariantCulture, $"\"{attrs["title"]}\" ({attrs["source_branch"]} → {attrs["target_branch"]}), {attrs["url"]}\n\n")
                    .Append("Its description:\n").Append(attrs["description"]?.GetValue<string>() is { Length: > 0 } d ? d : "(none)").Append('\n');
                if (await bot.GetAsync($"/projects/{projectId}/merge_requests/{iid}/changes", ct) is { } json && JsonNode.Parse(json)?["changes"] is JsonArray changes)
                {
                    var diff = string.Join("\n", changes.OfType<JsonObject>().Select(c => $"--- {c["old_path"]}\n+++ {c["new_path"]}\n{c["diff"]}"));
                    text.Append("\nIts changes:\n```diff\n").Append(Cut(diff, DiffChars)).Append("\n```\n");
                }
                return new TriggerEvent(text.ToString(), new GitLabTarget(projectId, "merge_requests", iid.ToString(CultureInfo.InvariantCulture)));
            }
            case "pipeline" when wanted.Contains("pipeline_failed"):
            {
                if (attrs["status"]?.GetValue<string>() != "failed")
                {
                    return null;
                }
                var failed = (e["builds"] as JsonArray ?? []).OfType<JsonObject>().Where(b => b["status"]?.GetValue<string>() == "failed").ToList();
                var text = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"GitLab event: pipeline #{attrs["id"]} failed on {attrs["ref"]} ({attrs["sha"]?.GetValue<string>()?[..Math.Min(8, attrs["sha"]!.GetValue<string>().Length)]}) in {path}. ")
                    .Append(CultureInfo.InvariantCulture, $"Failed jobs: {string.Join(", ", failed.Select(b => $"{b["name"]} (stage {b["stage"]})"))}.\n");
                foreach (var job in failed.Take(3))
                {
                    if (await bot.GetAsync($"/projects/{projectId}/jobs/{job["id"]}/trace", ct) is { } log)
                    {
                        text.Append(CultureInfo.InvariantCulture, $"\nThe end of {job["name"]}'s log:\n```\n{(log.Length > LogChars ? log[^LogChars..] : log)}\n```\n");
                    }
                }
                var mr = e["merge_request"]?["iid"]?.GetValue<long>();
                return new TriggerEvent(text.ToString(), mr is { } m
                    ? new GitLabTarget(projectId, "merge_requests", m.ToString(CultureInfo.InvariantCulture))
                    : attrs["sha"]?.GetValue<string>() is { } sha ? new GitLabTarget(projectId, "commits", sha) : null);
            }
            case "issue" when wanted.Contains("issue"):
            {
                if (attrs["action"]?.GetValue<string>() is not ("open" or "reopen"))
                {
                    return null;
                }
                var iid = attrs["iid"]?.GetValue<long>() ?? 0;
                return new TriggerEvent(
                    $"GitLab event: issue #{iid} was opened by {who} in {path}: \"{attrs["title"]}\", {attrs["url"]}\n\nIts description:\n{(attrs["description"]?.GetValue<string>() is { Length: > 0 } d ? d : "(none)")}\n",
                    new GitLabTarget(projectId, "issues", iid.ToString(CultureInfo.InvariantCulture)));
            }
            default:
                return null;
        }
    }

    /// <summary>Any system's JSON, as it came, for the question.</summary>
    public static TriggerEvent FromJson(string body) => new($"An event came in:\n```json\n{Cut(body, DiffChars)}\n```\n", null);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "\n[cut to fit]";
}
