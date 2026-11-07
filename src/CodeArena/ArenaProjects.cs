using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>A project as the terminal shows it: its numbers for a time, its open goals and tasks, its budget.</summary>
internal sealed record ProjectSummary(JsonObject Project, JsonObject? Report, string? ReportProblem);

/// <summary>
/// Arena's projects for the terminal (/project, /projects, /project new): the same routes as Arena's page and the IDE's
/// project tab, with the person's key. Arena decides there what the person may see and do; nothing is decided here.
/// </summary>
internal static class ArenaProjects
{
    /// <summary>The days /project's numbers cover.</summary>
    public const int Days = 7;

    /// <summary>A call to Arena's project routes: its answer (null with why when it failed or was refused).</summary>
    public static async Task<(JsonNode? Body, string? Problem)> CallAsync(Config config, HttpClient http, HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        if (config.Url is not { Length: > 0 } url || config.ApiKey is not { Length: > 0 } key)
        {
            return (null, "Projects are in Arena: sign in first (code-arena login).");
        }
        try
        {
            using var req = ChatSync.Request(method, url.TrimEnd('/') + path, key, body);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(WorkProjects.Limit);
            using var answer = await http.SendAsync(req, limit.Token);
            var text = await answer.Content.ReadAsStringAsync(limit.Token);
            if (answer.IsSuccessStatusCode)
            {
                return (text.Length == 0 ? new JsonObject() : JsonNode.Parse(text), null);
            }
            // Arena's own words when it gave them (its page says the same).
            var detail = Json.ParseObject(text) is { } said ? said.Str("error") ?? said.Str("detail") : null;
            return (null, detail ?? answer.StatusCode switch
            {
                HttpStatusCode.NotFound => "Arena has no such project for you.",
                HttpStatusCode.Forbidden => "Arena does not let you do that.",
                _ => $"Arena answered {(int)answer.StatusCode}.",
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            return (null, $"{url} did not answer.");
        }
    }

    /// <summary>Every project the person sees (theirs and the global ones), as Arena's Projects page lists them.</summary>
    public static async Task<(List<JsonObject>? Projects, string? Problem)> ListAsync(Config config, HttpClient http, CancellationToken ct)
    {
        var (body, problem) = await CallAsync(config, http, HttpMethod.Get, "/api/projects", null, ct);
        return body is JsonArray list ? ([.. list.OfType<JsonObject>()], null) : (null, problem ?? "Arena gave no list.");
    }

    /// <summary>The project with its goals and tasks, and its report for the last <see cref="Days"/> days.</summary>
    public static async Task<(ProjectSummary? Summary, string? Problem)> SummaryAsync(Config config, HttpClient http, string id, DateTimeOffset now, CancellationToken ct)
    {
        var from = Uri.EscapeDataString(now.AddDays(-Days).ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(now.ToString("O", CultureInfo.InvariantCulture));
        var project = CallAsync(config, http, HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(id)}", null, ct);
        var report = CallAsync(config, http, HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(id)}/report?from={from}&to={to}", null, ct);
        var (p, problem) = await project;
        var (r, reportProblem) = await report;
        return p is JsonObject found ? (new ProjectSummary(found, r as JsonObject, reportProblem), null) : (null, problem);
    }

    /// <summary>A new project of the person's (they are its owner), when Arena lets them make one: its id, else why not.</summary>
    public static async Task<(string? Id, string? Problem)> CreateAsync(Config config, HttpClient http, string name, CancellationToken ct)
    {
        var (body, problem) = await CallAsync(config, http, HttpMethod.Post, "/api/projects", new JsonObject { ["name"] = name.Trim() }, ct);
        return body?["id"]?.GetValue<string>() is { Length: > 0 } id ? (id, null)
            : (null, problem == "Arena does not let you do that." ? "Making projects is not among what you may do in Arena: ask an admin." : problem ?? "Arena made none.");
    }

    /// <summary>The lines /project shows: the project, its numbers for the last days, its budget, its open goals and tasks.</summary>
    public static List<string> Describe(ProjectSummary s)
    {
        var p = s.Project;
        var lines = new List<string>();
        var marks = new[] { p.Str("role"), p["global"]?.GetValue<bool>() == true ? "global" : null, p.Str("status") == "archived" ? "archived" : null }.OfType<string>();
        lines.Add($"Project: {p.Str("name")} ({string.Join(", ", marks)}) — what this session uses counts to it.");
        if (s.Report is { } r && r["totals"] is JsonObject t)
        {
            long N(string k) => t[k]?.GetValue<long>() ?? 0;
            var cost = t["cost"]?.GetValue<decimal>() ?? 0;
            lines.Add($"  Last {Days} days: {Money(cost)} · {Fmt.Tokens(N("input") + N("cached") + N("output"))} tokens · {N("requests"):N0} requests · "
                + $"{N("people")} {(N("people") == 1 ? "person" : "people")} · {Hours(N("activeSeconds"))} active · {Hours(N("agentSeconds"))} the models'");
            var models = (r["models"] as JsonArray ?? []).OfType<JsonObject>().Take(3).Select(m => $"{m.Str("name")} {Money(m["cost"]?.GetValue<decimal>() ?? 0)}").ToList();
            if (models.Count > 0)
            {
                lines.Add($"  Models: {string.Join(", ", models)}");
            }
            if (r["budget"] is JsonObject b)
            {
                lines.Add($"  Budget this month: {Money(b["spent"]?.GetValue<decimal>() ?? 0)} of {Money(b["monthly"]?.GetValue<decimal>() ?? 0)} ({b["percent"]?.GetValue<double>() ?? 0:0}%)"
                    + (b["stopAtBudget"]?.GetValue<bool>() == true ? ", its chats stop at it" : ""));
            }
        }
        else if (s.ReportProblem is { } why)
        {
            lines.Add($"  Its numbers: {why}");
        }
        var goals = (p["goals"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var open = goals.Where(g => g.Str("status") is "planned" or "doing").ToList();
        lines.Add(goals.Count == 0 ? "  No goals yet." : $"  Goals: {goals.Count(g => g.Str("status") == "done")} of {goals.Count(g => g.Str("status") != "dropped")} done; open:");
        foreach (var g in open)
        {
            var due = g.Str("dueOn") is { Length: > 0 } d && DateOnly.TryParse(d, CultureInfo.InvariantCulture, out var day) ? $", due {day.ToString("d MMM", CultureInfo.InvariantCulture)}" : "";
            lines.Add($"    ◦ {g.Str("title")} ({g.Str("status")}{due})");
        }
        if (goals.Count > 0 && open.Count == 0)
        {
            lines.Add("    none");
        }
        var tasks = (p["tasks"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (tasks.Count > 0)
        {
            lines.Add($"  Tasks: {tasks.Count(x => x["done"]?.GetValue<bool>() == true)} of {tasks.Count} done.");
        }
        return lines;
    }

    /// <summary>"$1.23"; under a cent, "$0.004".</summary>
    public static string Money(decimal d) => d is > 0 and < 0.01m ? "$" + d.ToString("0.###", CultureInfo.InvariantCulture) : "$" + d.ToString("#,0.00", CultureInfo.InvariantCulture);

    /// <summary>Seconds as Arena's page says them: 12 min, 3 h 05 min.</summary>
    public static string Hours(long seconds)
    {
        var minutes = (long)Math.Round(seconds / 60.0);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }
}
