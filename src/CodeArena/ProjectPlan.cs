using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The plan of the Arena project the session is in (/project): its goals and tasks, read at each turn, and project_plan,
/// project_goal and project_task for the model to keep it up to date as the work goes, as a chat in the project does.
/// Arena says what the model is told and keeps the plan; it needs the session synced to Arena.
/// </summary>
internal static class SessionProjectPlan
{
    public const string PlanName = "project_plan", GoalName = "project_goal", TaskName = "project_task";

    public static readonly string[] Names = [PlanName, GoalName, TaskName];

    public static IEnumerable<ToolDef> Tools()
    {
        yield return Tool(PlanName, "The plan of the project this session is in, as it is now: its goals with their states and tasks, by id.", """
            {"type":"object","properties":{}}
            """, _ => "the plan");
        yield return Tool(GoalName, "Adds a goal of the session's project (no id), or changes one (its id): its title, state, description, due date. Keep each goal to an aim the person set out.", """
            {"type":"object","properties":{
              "id":{"type":"string","description":"The goal's id, to change it; leave out to add one"},
              "title":{"type":"string","description":"What it is, in a line (1 to 200 characters)"},
              "status":{"type":"string","enum":["planned","doing","done","dropped"],"description":"Its state"},
              "description":{"type":"string","description":"More on it: what done means, how it is checked"},
              "due":{"type":"string","description":"When it is due, 2026-12-31"}}}
            """, a => a.Str("title") ?? a.Str("id") ?? "a goal");
        yield return Tool(TaskName, "Adds a task of the session's project (no id), under a goal or not, or changes one (its id): its title, its goal, done or not.", """
            {"type":"object","properties":{
              "id":{"type":"string","description":"The task's id, to change it; leave out to add one"},
              "title":{"type":"string","description":"What to do, in a line (1 to 300 characters)"},
              "goal":{"type":"string","description":"The id of the goal it is part of"},
              "done":{"type":"boolean","description":"true when it is done; false when it is not after all"}}}
            """, a => a.Str("title") ?? a.Str("id") ?? "a task");
    }

    private static ToolDef Tool(string name, string description, string schema, Func<JsonObject, string> summary) => new()
    {
        Name = name,
        Kind = ToolKind.Read,
        Description = description,
        Parameters = LocalTools.Schema(schema),
        Summary = summary,
        Run = (a, c, ct) => c.ProjectPlan is { } call
            ? call(name, a, ct)
            : Task.FromResult(new ToolResult("This session is in no Arena project (/project puts it in one).", Error: true)),
    };
}

/// <summary>The session's project plan: read from Arena at each turn, and the model's calls sent there.</summary>
internal sealed partial class Runtime
{
    // What Arena says the model is told of the session's project plan (null: in no project, or no chat in Arena yet).
    private string? _projectPlan;

    /// <summary>The project plan's instructions for the model, as Arena last said (empty: none).</summary>
    public string ProjectPlanText => Volatile.Read(ref _projectPlan) ?? "";

    /// <summary>The plan read again from Arena (the web page or another session may have changed it); as it was when Arena does not answer.</summary>
    public async Task RefreshProjectPlanAsync(CancellationToken ct)
    {
        try
        {
            if (Sync?.ChatCallAsync(HttpMethod.Get, "/project-work", null, ct) is not { } call || await call is not { } answer || (int)answer.Status >= 500)
            {
                return;
            }
            Volatile.Write(ref _projectPlan, answer.Status == HttpStatusCode.OK ? (answer.Body as JsonObject)?.Str("instructions") : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            // Arena away: the plan as last read.
        }
    }

    /// <summary>project_plan, project_goal or project_task: sent to Arena, which keeps the plan.</summary>
    private async Task<ToolResult> CallProjectPlanAsync(string function, JsonObject args, CancellationToken ct)
    {
        try
        {
            if (Sync?.ChatCallAsync(HttpMethod.Post, "/project-work/" + function, args, ct) is not { } call || await call is not { } answer)
            {
                return new ToolResult("This session has no chat in Arena yet, so no project: say something first.", Error: true);
            }
            if ((int)answer.Status >= 400)
            {
                return new ToolResult(SessionChecklist.Why(answer.Status, answer.Body), Error: true);
            }
            var body = answer.Body as JsonObject;
            if (body?.Bool("error") != true && function != SessionProjectPlan.PlanName)
            {
                // The plan changed: what the model is told from the next request is the new one.
                _ = Task.Run(() => RefreshProjectPlanAsync(CancellationToken.None));
            }
            return new ToolResult(body?.Str("text") ?? "", Error: body?.Bool("error") == true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ToolResult($"Arena did not answer: {e.Message}", Error: true);
        }
    }
}
