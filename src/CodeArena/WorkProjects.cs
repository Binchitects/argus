using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>A project in Arena (Projects): its id and name.</summary>
internal sealed record WorkProject(string Id, string Name);

/// <summary>The projects in Arena the person may add to, and the one of this folder's repository (null: none has it).</summary>
internal sealed record WorkProjectList(List<WorkProject> Projects, string? Suggested);

/// <summary>
/// The project in Arena a session counts to: what it uses goes to the project's accounting (each request names it,
/// with its chat), and its chat in Arena is in the project. Chosen at the start (--project), with /project or in the IDE,
/// and kept for the folder so the next session there is in it too; else the project its repository is in, as Arena says.
/// </summary>
internal static class WorkProjects
{
    /// <summary>The folder's key in its <see cref="WorkspaceState"/>: the project chosen there last.</summary>
    public const string StateKey = "project";

    /// <summary>The longest Arena may take to answer: a slow Arena never holds the session's start.</summary>
    public static TimeSpan Limit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The project chosen in the folder last; null: none (or "none" chosen).</summary>
    public static WorkProject? OfFolder(WorkspaceState state) =>
        state.Read()[StateKey] is JsonObject p && p.Str("id") is { Length: > 0 } id ? new WorkProject(id, p.Str("name") ?? "") : null;

    /// <summary>Kept for the folder: its next sessions are in it too.</summary>
    public static void Remember(WorkspaceState state, WorkProject? project) =>
        state.Merge(new JsonObject { [StateKey] = project is null ? null : new JsonObject { ["id"] = project.Id, ["name"] = project.Name } });

    /// <summary>The tags each request carries: its chat in Arena and its project (Arena's ledger counts it to the project by them).</summary>
    public static IReadOnlyList<string> Tags(string? conversation, WorkProject? project) =>
        [.. new[] { conversation is { Length: > 0 } c ? "chat:" + c : null, project is null ? null : "project:" + project.Id }.OfType<string>()];

    /// <summary>The projects the person may add to, and the repository's: null, with why, when Arena cannot say.</summary>
    public static async Task<(WorkProjectList? List, string? Problem)> ListAsync(Config config, HttpClient http, string? repository, CancellationToken ct)
    {
        if (config.Url is not { Length: > 0 } url || config.ApiKey is not { Length: > 0 } key)
        {
            return (null, "Projects are in Arena: sign in first (code-arena login).");
        }
        var path = url.TrimEnd('/') + "/api/code-arena/projects" + (repository is { Length: > 0 } r ? "?repo=" + Uri.EscapeDataString(r) : "");
        try
        {
            using var req = ChatSync.Request(HttpMethod.Get, path, key, null);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Limit);
            using var answer = await http.SendAsync(req, limit.Token);
            var text = await answer.Content.ReadAsStringAsync(limit.Token);
            if (answer.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, "This Arena has no projects yet: it is older than Code Arena.");
            }
            if (!answer.IsSuccessStatusCode || Json.ParseObject(text) is not { } body)
            {
                return (null, $"Arena answered {(int)answer.StatusCode}: {Json.ParseObject(text)?.Str("detail") ?? "no projects"}.");
            }
            var projects = (body["projects"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => p.Str("id") is { Length: > 0 } id ? new WorkProject(id, p.Str("name") ?? id) : null).OfType<WorkProject>().ToList();
            return (new WorkProjectList(projects, body.Str("suggested")), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, $"{url} did not answer.");
        }
    }

    /// <summary>
    /// The project <paramref name="which"/> names: its number in the list (from 1), its id, or its name (whole, or the start of
    /// only one). Null with why when none or several are.
    /// </summary>
    public static (WorkProject? Project, string? Problem) Pick(IReadOnlyList<WorkProject> projects, string which)
    {
        var w = which.Trim();
        if (int.TryParse(w, out var n))
        {
            return n >= 1 && n <= projects.Count ? (projects[n - 1], null) : (null, $"There is no project {n}: /project lists them.");
        }
        if (projects.FirstOrDefault(p => p.Id.Equals(w, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(w, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return (exact, null);
        }
        var starting = projects.Where(p => p.Name.StartsWith(w, StringComparison.OrdinalIgnoreCase)).ToList();
        return starting.Count switch
        {
            1 => (starting[0], null),
            0 => (null, $"You may add to no project named {w}: /project lists those you may."),
            _ => (null, $"{starting.Count} projects start with {w}: {string.Join(", ", starting.Select(p => p.Name))}."),
        };
    }

    /// <summary>The session's chat in Arena put in the project (null: taken out); null when done, else why not.</summary>
    public static async Task<string?> MoveChatAsync(Config config, HttpClient http, string conversation, WorkProject? project, CancellationToken ct)
    {
        if (config.Url is not { Length: > 0 } url || config.ApiKey is not { Length: > 0 } key)
        {
            return "Not signed in to Arena.";
        }
        try
        {
            using var req = ChatSync.Request(HttpMethod.Put, $"{url.TrimEnd('/')}/api/code-arena/chats/{conversation}/project", key,
                new JsonObject { ["projectId"] = project?.Id });
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Limit);
            using var answer = await http.SendAsync(req, limit.Token);
            return answer.IsSuccessStatusCode ? null
                : $"Arena answered {(int)answer.StatusCode}: {Json.ParseObject(await answer.Content.ReadAsStringAsync(limit.Token))?.Str("detail") ?? "the chat stays where it was"}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"{url} did not answer: its chat in Arena stays where it was, and its requests count there until /project is chosen again.";
        }
    }
}
