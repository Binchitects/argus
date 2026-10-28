using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The security review's open findings in the IDE's Problems (source "security"): Arena's latest review of the folder's
/// repository (origin's project) at its branch, read with the person's key. Each carries what Fix it would ask, so the
/// panel's Fix gives the agent the weakness, its path, the data's way to it and the references. Critical and high ones
/// are errors, medium warnings, low notes. Read when the page asks (the Problems view opens, or its refresh).
/// </summary>
internal sealed class SecurityProblems(Runtime rt, Problems problems)
{
    public const string Source = "security";
    /// <summary>The longest Arena is waited for.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>What the last read said: the review (or why there is none), and how many findings it had; null before the first.</summary>
    public JsonObject? Last { get; private set; }

    /// <summary>The last read, or a first one now.</summary>
    public async Task<JsonObject> CurrentAsync(CancellationToken ct) => Last ?? await RefreshAsync(ct);

    public async Task<JsonObject> RefreshAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            Last = await ReadAsync(ct);
            return Last;
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        var root = rt.Workspace.Root;
        var project = Cli.OriginProject(root);
        var branch = Branch(root);
        JsonObject Said(string why)
        {
            problems.Found(Source, []);
            return new JsonObject { ["project"] = project, ["ref"] = branch, ["said"] = why, ["count"] = 0 };
        }
        if (rt.Config.Url is not { Length: > 0 } url || rt.Config.ApiKey is not { Length: > 0 } key)
        {
            return Said("Not signed in to Arena: its security reviews are not read here.");
        }
        if (project is null)
        {
            return Said("This folder has no origin to name its repository by.");
        }
        JsonObject? answer;
        try
        {
            using var req = ChatSync.Request(HttpMethod.Get, $"{url.TrimEnd('/')}/api/code-arena/security?project={Uri.EscapeDataString(project)}" +
                (branch is null ? "" : $"&ref={Uri.EscapeDataString(branch)}"), key, null);
            using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
            within.CancelAfter(Limit);
            using var res = await rt.Http.SendAsync(req, within.Token);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Said("This Arena does not say its security reviews (an older one).");
            }
            if (!res.IsSuccessStatusCode)
            {
                return Said($"Arena did not give its security reviews (HTTP {(int)res.StatusCode}).");
            }
            answer = Json.ParseObject(await res.Content.ReadAsStringAsync(within.Token));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Said("Arena could not be reached for its security reviews.");
        }
        if (answer?["scan"] is not JsonObject scan)
        {
            return Said($"No security review of {project}{(branch is null ? "" : $" at {branch}")} in Arena yet: start one in Code Studio → Security.");
        }
        var found = (answer["findings"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => f.Str("path") is { Length: > 0 } && f.Int("line") is not null)
            .Select(f => new Problem(f.Str("path")!, Math.Max(1, f.Int("line")!.Value), 1,
                f.Str("severity") switch { "critical" or "high" => "error", "medium" => "warning", _ => "info" },
                f.Str("cwe") is { Length: > 0 } cwe ? cwe : f.Str("rule"),
                $"{f.Str("title")} ({f.Str("severity")}{(f["exploited"]?.GetValue<bool>() == true ? ", known exploited" : "")}, security review)",
                Source, f.Str("ask")))
            .ToList();
        problems.Found(Source, found);
        return new JsonObject
        {
            ["project"] = project, ["ref"] = scan.Str("ref") ?? branch, ["count"] = found.Count, ["finishedAt"] = scan.Str("finishedAt"),
            ["commit"] = scan.Str("commit"), ["link"] = url.TrimEnd('/') + scan.Str("link"),
        };
    }

    /// <summary>The folder's branch, from .git/HEAD; null when it is not on one (a detached head) or not a git folder.</summary>
    internal static string? Branch(string root)
    {
        try
        {
            var head = File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim();
            return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head["ref: refs/heads/".Length..] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
