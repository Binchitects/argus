using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>The terminal's /tasks, /goal and /assistant.</summary>
internal sealed partial class Repl
{
    /// <summary>
    /// /tasks: the session's sub-agents and background tasks, one line each; /tasks N shows one's transcript; stop N stops
    /// one; new [--edits] [--assistant NAME] TASK starts one in the background.
    /// </summary>
    private async Task TasksCommandAsync(string arg, CancellationToken ct)
    {
        var words = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (words.FirstOrDefault()?.ToLowerInvariant())
        {
            case null:
                var all = rt.Tasks.All;
                if (all.Count == 0)
                {
                    Ui.Info("No tasks in this session. The agent starts sub-agents (task); /tasks new TASK starts one of yours in the background.");
                    return;
                }
                foreach (var t in all)
                {
                    Ui.Line(TaskLine(t));
                }
                Ui.Info("/tasks N shows one · /tasks stop N stops one · /tasks new [--edits] [--assistant NAME] TASK starts one in the background");
                return;
            case "stop" when words.Length > 1:
                var id = Id(words[1]);
                Ui.Line(rt.Tasks.StopTask(id) ? $"Stopping {id}." : $"{id} is not at work (/tasks lists them).");
                return;
            case "new" when words.Length > 1:
                await NewTaskAsync(words[1], ct);
                return;
            default:
                if (rt.Tasks.Find(Id(arg)) is not { } task)
                {
                    Ui.Error($"There is no task {arg}: /tasks lists them.");
                    return;
                }
                ShowTask(task);
                return;
        }
    }

    /// <summary>t3, 3 or #3: the task's id.</summary>
    private static string Id(string word) => word.TrimStart('#') is var w && int.TryParse(w, out var n) ? $"t{n}" : w;

    /// <summary>A task in a line: its number, state, title, time, tokens, and what it does now.</summary>
    private string TaskLine(AgentTask t)
    {
        var o = TaskBoard.ToJson(t, whole: false);
        var mark = t.Status switch
        {
            AgentTask.Running => Ui.Cyan("◐"),
            AgentTask.Queued => Ui.Dim("○"),
            AgentTask.Done => Ui.Green("✓"),
            AgentTask.Stopped => Ui.Yellow("■"),
            _ => Ui.Red("✗"),
        };
        var tokens = o["tokens"] as JsonObject;
        var spent = (tokens?.Long("prompt") ?? 0) + (tokens?.Long("completion") ?? 0);
        var facts = new List<string> { t.Status };
        if (o.Long("ms") is { } ms)
        {
            facts.Add(Fmt.Duration(TimeSpan.FromMilliseconds(ms)));
        }
        if (spent > 0)
        {
            facts.Add($"{Fmt.Tokens(spent)} tokens");
        }
        if (t.Kind == AgentTask.Background)
        {
            facts.Add("yours");
        }
        if (t.As is { } who)
        {
            facts.Add($"as {who}");
        }
        if (t.Writes)
        {
            facts.Add("changes files");
        }
        var doing = o.Str("doing") is { } d ? Ui.Dim(" · " + d) : "";
        return $"  {mark} {Ui.Bold(t.Id)} {Fmt.OneLine(t.Title, 60)} {Ui.Dim("(" + string.Join(", ", facts) + ")")}{doing}";
    }

    /// <summary>A task's transcript: what it was asked, each of its tool calls, and its report.</summary>
    private void ShowTask(AgentTask t)
    {
        Ui.Line(TaskLine(t));
        Ui.Line(Ui.Dim("  Asked: ") + Fmt.Head(t.Prompt, 6));
        var work = TaskBoard.ToJson(t, whole: true)["work"] as JsonObject;
        foreach (var step in (work?["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var args = Json.ParseObject(step.Str("arguments"));
            Ui.Line($"  {Ui.Magenta("●")} {Ui.Bold(step.Str("name") ?? "")} {Ui.Dim(Fmt.OneLine(args is null ? "" : Json.Line(args), 100))}");
            if (step.Str("result") is { } result)
            {
                Ui.Line("    " + (step.Bool("isError") == true ? Ui.Red(Fmt.OneLine(result, 140)) : Ui.Dim(Fmt.OneLine(result, 140))));
            }
        }
        if (t.Over)
        {
            Ui.Line(t.Status == AgentTask.Done ? Fmt.Indent(t.Report ?? "", "  ") : Ui.Yellow("  " + (t.Error ?? t.Status)));
        }
    }

    private async Task NewTaskAsync(string rest, CancellationToken ct)
    {
        var edits = false;
        string? assistant = null;
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && words[0].StartsWith("--", StringComparison.Ordinal))
        {
            switch (words[0])
            {
                case "--edits":
                    edits = true;
                    words.RemoveAt(0);
                    break;
                case "--assistant" when words.Count > 1:
                    assistant = words[1];
                    words.RemoveRange(0, 2);
                    break;
                default:
                    Ui.Error($"/tasks new takes --edits and --assistant NAME, not {words[0]}.");
                    return;
            }
        }
        var prompt = string.Join(' ', words);
        if (prompt.Length == 0)
        {
            Ui.Error("Say what the task is: /tasks new find where the config is read.");
            return;
        }
        if (edits && rt.Permissions.Mode == Mode.Plan)
        {
            Ui.Error("Plan mode is read-only: a task that changes files needs another mode.");
            return;
        }
        try
        {
            var task = await rt.StartBackgroundAsync(prompt, null, assistant, null, edits, ct);
            Ui.Line($"Started {task.Id} in the background: /tasks {task.Id[1..]} shows it; its report reaches the agent with your next message.");
        }
        catch (ToolError e)
        {
            Ui.Error(e.Message);
        }
    }

    /// <summary>A background task the person started ended: said above the prompt.</summary>
    private void TaskEnded(AgentTask t)
    {
        if (t.Kind == AgentTask.Background && t.Over)
        {
            Ui.Background(Ui.Dim($"Task {t.Id} ({Fmt.OneLine(t.Title, 50)}) {(t.Status == AgentTask.Done ? "is done" : t.Status)}: /tasks {t.Id[1..]} shows it."));
        }
    }

    /// <summary>
    /// /goal TEXT [--check COMMAND | --until CONDITION] [--steps N] [--minutes N] [--tokens N]: the agent works until it is
    /// met; /goal shows it; pause, resume, cancel.
    /// </summary>
    private void GoalCommand(string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "":
                Ui.Line(rt.DescribeGoal());
                if (rt.Goal is null)
                {
                    Ui.Info("/goal TEXT [--check COMMAND | --until CONDITION] [--steps N] [--minutes N] [--tokens N] [--until-met] · /goal pause · resume · cancel · next · drop N");
                }
                return;
            case "pause":
                Ui.Line(rt.PauseGoal() ?? "Paused: the agent stops after this turn. /goal resume carries it on.");
                return;
            case "resume":
                Ui.Line(rt.ResumeGoal() ?? "Resumed: the agent carries on toward the goal.");
                return;
            case "cancel":
                Ui.Line(rt.CancelGoal() ?? "The goal is cancelled.");
                return;
            case "next":
                Ui.Line(rt.NextGoal() is { } refused ? refused : rt.DescribeGoal());
                return;
        }
        if (arg.StartsWith("drop ", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Line(int.TryParse(arg[5..].Trim(), out var place) ? rt.DropGoal(place) ?? $"Goal {place} is dropped." : "/goal drop N drops the Nth goal waiting.");
            return;
        }
        if (ParseGoal(arg) is not { } parsed)
        {
            return;
        }
        if (rt.StartGoal(parsed.Text, parsed.Check, parsed.Budget) is { } wrong)
        {
            Ui.Error(wrong);
            return;
        }
        Ui.Line(rt.DescribeGoal());
        Ui.Info(rt.Goal?.Text == parsed.Text
            ? "The agent works on it now, turn after turn, until the check passes or the budget is spent. Ctrl+C pauses it; /goal cancel gives it up."
            : "It waits its turn: it starts once the goals before it are met.");
    }

    /// <summary>/loop: the loops; /loop EVERY [--edits] PROMPT starts one; /loop stop N stops one.</summary>
    private void LoopCommand(string arg)
    {
        var words = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            Ui.Line(rt.DescribeLoops());
            return;
        }
        if (words[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Line(words.Length > 1 && rt.StopLoop(words[1]) ? $"Loop {words[1]} is stopped." : "/loop stop N stops loop N (/loop lists them).");
            return;
        }
        if (Runtime.ParseEvery(words[0]) is not { } every || words.Length < 2)
        {
            Ui.Error("/loop EVERY [--edits] PROMPT: every 30m, 2h or 1d, a prompt run as a background agent.");
            return;
        }
        var rest = words[1];
        var edits = rest.StartsWith("--edits ", StringComparison.Ordinal);
        if (edits)
        {
            rest = rest["--edits ".Length..];
        }
        var (loop, wrong) = rt.StartLoop(every, rest, edits);
        if (loop is null)
        {
            Ui.Error(wrong!);
            return;
        }
        Ui.Line($"Loop {loop.Id}: every {Runtime.EveryText(every)}, the first run now, each a background task (/tasks). /loop stop {loop.Id[1..]} stops it; it ends with the session.");
    }

    /// <summary>The goal's words and options, or null (said why).</summary>
    private (string Text, GoalCheck? Check, GoalBudget Budget)? ParseGoal(string arg)
    {
        var options = new[] { "--check", "--until", "--steps", "--minutes", "--tokens" };
        var text = new List<string>();
        GoalCheck? check = null;
        var budget = new GoalBudget();
        var words = RunConfigs.Words(arg);
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w is "--until-met" or "--forever")
            {
                budget = budget with { UntilMet = true };
                continue;
            }
            if (!options.Contains(w))
            {
                text.Add(w);
                continue;
            }
            if (i + 1 >= words.Count)
            {
                Ui.Error($"{w} needs a value.");
                return null;
            }
            var value = words[++i];
            switch (w)
            {
                case "--check":
                    check = new GoalCheck(GoalCheck.Command, value);
                    break;
                case "--until":
                    check = new GoalCheck(GoalCheck.Condition, value);
                    break;
                case "--steps" when int.TryParse(value, out var steps):
                    budget = budget with { Steps = steps };
                    break;
                case "--minutes" when int.TryParse(value, out var minutes):
                    budget = budget with { Minutes = minutes };
                    break;
                case "--tokens" when long.TryParse(value.Replace("_", "").Replace(",", ""), out var tokens):
                    budget = budget with { Tokens = tokens };
                    break;
                default:
                    Ui.Error($"{w} takes a number, not {value}.");
                    return null;
            }
        }
        return (string.Join(' ', text), check, budget);
    }

    /// <summary>The goal's next turn, and its check after it: what the loop runs while a goal is being worked on.</summary>
    private async Task GoalTurnAsync(string prompt, CancellationToken ct)
    {
        var goal = rt.Goal!;
        Ui.Line();
        Ui.Line(Ui.Cyan("◎ ") + Ui.Bold($"Goal, turn {goal.Turns + 1}") + Ui.Dim($" · {goal.Usage()}"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var turn = new Spend();
        bool stopped;
        using (var limit = rt.GoalLimit(turn))
        {
            stopped = await TurnAsync(prompt, ct, turn, limit.Token);
        }
        if (rt.Goal is { Status: GoalRun.Working, Check: { } check } && !stopped)
        {
            Ui.Info(check.Kind == GoalCheck.Command ? $"Checking the goal: {check.Text}" : "Checking the goal: a fresh reader looks at it…");
        }
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        try
        {
            var said = await rt.AfterGoalTurnAsync(turn, clock.Elapsed, stopped, linked.Token);
            if (said.Length > 0)
            {
                Ui.Line((rt.Goal?.Status == GoalRun.Met ? Ui.Green("✓ ") : Ui.Cyan("◎ ")) + said);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            rt.PauseGoal();
            Ui.Warn("Stopped while the goal was checked: it is paused. /goal resume carries it on.");
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException or IOException or McpException)
        {
            rt.PauseGoal();
            Ui.Error($"The goal's check could not be made ({e.Message}): it is paused. /goal resume carries it on.");
        }
        finally
        {
            rt.Env.Cancel.EndTurn();
        }
    }
}
