using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The IDE's Tasks panel, Goal and Assistant: the session's sub-agents and background tasks (each with its transcript, to
/// stop), the goal the agent works toward (set, paused, resumed, cancelled), and the Arena assistant the session works as.
/// </summary>
internal sealed partial class WebApp
{
    private const string TasksPath = "/api/agent-tasks";

    /// <summary>Handles the panel's calls; false when the request is not one of them.</summary>
    private async Task<bool> TasksAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        switch (req.Method, req.Path)
        {
            case ("GET", TasksPath):
                await res.JsonAsync(200, new JsonObject
                {
                    ["tasks"] = new JsonArray([.. _rt.Tasks.All.Select(t => (JsonNode)TaskBoard.ToJson(t, whole: false))]),
                    ["running"] = _rt.Tasks.RunningCount,
                    ["mostAtOnce"] = _rt.Tasks.AtOnce,
                }, ct);
                return true;
            case ("POST", TasksPath):
                await StartTaskAsync(req, res, ct);
                return true;
            case ("GET", var one) when one.StartsWith(TasksPath + "/", StringComparison.Ordinal):
                if (_rt.Tasks.Find(Uri.UnescapeDataString(one[(TasksPath.Length + 1)..])) is not { } task)
                {
                    await res.ErrorAsync(404, "not_found", "There is no such task in this session.", ct);
                    return true;
                }
                await res.JsonAsync(200, TaskBoard.ToJson(task, whole: true), ct);
                return true;
            case ("POST", var stop) when stop.StartsWith(TasksPath + "/", StringComparison.Ordinal) && stop.EndsWith("/stop", StringComparison.Ordinal):
                var id = Uri.UnescapeDataString(stop[(TasksPath.Length + 1)..^"/stop".Length]);
                if (_rt.Tasks.Find(id) is null)
                {
                    await res.ErrorAsync(404, "not_found", "There is no such task in this session.", ct);
                }
                else if (!_rt.Tasks.StopTask(id))
                {
                    await res.ErrorAsync(409, "over", "That task has ended already.", ct);
                }
                else
                {
                    await res.NoContentAsync(ct);
                }
                return true;
            case ("GET", "/api/goal"):
                await res.JsonAsync(200, new JsonObject { ["goal"] = _rt.Goal?.ToJson() }, ct);
                return true;
            case ("POST", "/api/goal"):
                await SetGoalAsync(req, res, ct);
                return true;
            case ("POST", "/api/goal/pause"):
                await GoalAnswerAsync(_rt.PauseGoal(), res, ct);
                return true;
            case ("POST", "/api/goal/resume"):
                await GoalAnswerAsync(_rt.ResumeGoal(), res, ct);
                StartNext();
                return true;
            case ("DELETE", "/api/goal"):
                await GoalAnswerAsync(_rt.CancelGoal(), res, ct);
                return true;
            case ("GET", "/api/assistants"):
                var list = await _rt.AssistantsAsync(ct);
                await res.JsonAsync(200, new JsonObject
                {
                    // Null: Code Arena is not signed in to Arena, or Arena did not answer.
                    ["assistants"] = list is null ? null : new JsonArray([.. list.Select(a => (JsonNode)a.ToJson())]),
                    ["current"] = _rt.Assistant?.Id,
                }, ct);
                return true;
            case ("POST", "/api/assistant"):
                await UseAssistantAsync(req, res, ct);
                return true;
        }
        return false;
    }

    /// <summary>A task the person starts: what to do, and as whom, on which model, whether it may change files.</summary>
    private async Task StartTaskAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        var body = req.Json();
        var prompt = body?.Str("prompt")?.Trim() ?? "";
        if (prompt.Length is 0 or > 20_000)
        {
            await res.ErrorAsync(400, "invalid", "Say what the task is, in at most 20,000 characters.", ct);
            return;
        }
        var edits = body?.Bool("edits") == true;
        if (edits && _rt.Permissions.Mode == Mode.Plan)
        {
            await res.ErrorAsync(409, "plan", "Plan mode is read-only: a task that changes files needs another mode.", ct);
            return;
        }
        try
        {
            var task = await _rt.StartBackgroundAsync(prompt, body?.Str("title")?.Trim(), body?.Str("assistant"), body?.Str("model"), edits, ct);
            await res.JsonAsync(201, TaskBoard.ToJson(task, whole: false), ct);
        }
        catch (ToolError e)
        {
            await res.ErrorAsync(400, "invalid", e.Message, ct);
        }
    }

    private async Task SetGoalAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        var body = req.Json();
        var b = body?["budget"] as JsonObject;
        var budget = new GoalBudget(b?.Int("steps") ?? GoalBudget.DefaultSteps, b?.Int("minutes") ?? GoalBudget.DefaultMinutes, b?.Long("tokens") ?? GoalBudget.DefaultTokens);
        var c = body?["check"] as JsonObject;
        var check = c?.Str("kind") is { } kind && c.Str("text") is { } text ? new GoalCheck(kind, text) : null;
        if (_rt.StartGoal(body?.Str("text") ?? "", check, budget) is { } wrong)
        {
            await res.ErrorAsync(400, "invalid", wrong, ct);
            return;
        }
        await res.JsonAsync(200, new JsonObject { ["goal"] = _rt.Goal?.ToJson() }, ct);
        // The goal's first turn starts now when nothing runs, else when the answer running ends.
        StartNext();
    }

    private async Task GoalAnswerAsync(string? wrong, HttpResponse res, CancellationToken ct)
    {
        if (wrong is not null)
        {
            await res.ErrorAsync(409, "goal", wrong, ct);
            return;
        }
        await res.JsonAsync(200, new JsonObject { ["goal"] = _rt.Goal?.ToJson() }, ct);
    }

    private async Task UseAssistantAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        if (Busy())
        {
            await res.ErrorAsync(409, "busy", "An answer is being written: choose the assistant once it ends.", ct);
            return;
        }
        var id = req.Json()?.Str("id");
        ArenaAssistant? assistant = null;
        if (id is { Length: > 0 } && (assistant = await _rt.FindAssistantAsync(id, ct)) is null)
        {
            await res.ErrorAsync(404, "not_found", "You have no such assistant in Arena (any more).", ct);
            return;
        }
        var said = _rt.UseAssistant(assistant);
        await res.JsonAsync(200, new JsonObject { ["said"] = said, ["state"] = State() }, ct);
    }
}
