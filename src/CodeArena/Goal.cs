using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>How a goal is checked: a command that succeeds only once it is met (the tests), or a condition a fresh reader looks at.</summary>
internal sealed record GoalCheck(string Kind, string Text)
{
    public const string Command = "command";
    public const string Condition = "condition";

    public string Describe() => Kind == Command ? $"`{Text}` succeeds" : $"this holds: {Text}";
}

/// <summary>What a goal may spend before it stops: steps (requests to the model), minutes of work, tokens.</summary>
internal sealed record GoalBudget(int Steps = GoalBudget.DefaultSteps, int Minutes = GoalBudget.DefaultMinutes, long Tokens = GoalBudget.DefaultTokens)
{
    public const int DefaultSteps = 200;
    public const int DefaultMinutes = 60;
    public const long DefaultTokens = 5_000_000;
    public const int MostSteps = 5_000;
    public const int MostMinutes = 24 * 60;
    public const long MostTokens = 500_000_000;

    /// <summary>Null when it is within bounds, else why not.</summary>
    public string? Wrong() =>
        Steps is < 1 or > MostSteps ? $"Steps are 1 to {MostSteps:N0}."
        : Minutes is < 1 or > MostMinutes ? $"Minutes are 1 to {MostMinutes:N0}."
        : Tokens is < 10_000 or > MostTokens ? $"Tokens are 10,000 to {MostTokens:N0}."
        : null;

    public string Describe() => $"{Steps:N0} steps, {Minutes:N0} minutes, {Fmt.Tokens(Tokens)} tokens";
}

/// <summary>
/// Goal mode: the agent keeps working, turn after turn, until a goal is met as its check says (a command's exit code, or a
/// condition read by a fresh sub-agent), within the budget the person set; then it says how it checked. The person pauses,
/// resumes or cancels it. Kept in the session (a resumed one goes on from where it was) and sent to Arena with the chat.
/// </summary>
internal sealed class GoalRun
{
    public const string Working = "working";
    public const string Paused = "paused";
    public const string Met = "met";
    public const string OutOfBudget = "budget";
    public const string Stuck = "stuck";
    public const string Cancelled = "cancelled";

    /// <summary>Turns in a row with no tool call and the check not passing: the agent is not getting on, so the goal stops.</summary>
    public const int MostIdleTurns = 3;
    /// <summary>Turns that may go by before the agent wrote its check down: it is reminded at each, and the goal stops past it.</summary>
    public const int MostTurnsUnchecked = 3;

    public required string Text { get; init; }
    public GoalCheck? Check { get; set; }
    /// <summary>"person": set with the goal; "agent": written down by the agent with goal_check.</summary>
    public string? CheckBy { get; set; }
    public GoalBudget Budget { get; init; } = new();
    public string Status { get; set; } = Working;
    public int Turns { get; set; }
    public int Steps { get; set; }
    public long Tokens { get; set; }
    /// <summary>The time spent working on it (paused time not counted).</summary>
    public long Ms { get; set; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The last check: whether it passed, what it said (the end of a command's output, or the reader's words), when.</summary>
    public bool? Passed { get; set; }
    public string? Said { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    /// <summary>How it ended, in words: how it was checked, or why it stopped.</summary>
    public string? Outcome { get; set; }
    public int IdleTurns { get; set; }

    public bool Active => Status is Working or Paused;

    /// <summary>What is left of the budget, or null when some of it is spent.</summary>
    public string? Spent() =>
        Steps >= Budget.Steps ? $"its {Budget.Steps:N0} steps are used"
        : Ms >= Budget.Minutes * 60_000L ? $"its {Budget.Minutes:N0} minutes are used"
        : Tokens >= Budget.Tokens ? $"its {Fmt.Tokens(Budget.Tokens)} tokens are used"
        : null;

    public string Usage() => $"{Turns} {(Turns == 1 ? "turn" : "turns")}, {Steps:N0} of {Budget.Steps:N0} steps, {Fmt.Duration(TimeSpan.FromMilliseconds(Ms))} of {Budget.Minutes} minutes, " +
        $"{Fmt.Tokens(Tokens)} of {Fmt.Tokens(Budget.Tokens)} tokens";

    public JsonObject ToJson() => new()
    {
        ["text"] = Text,
        ["check"] = Check is null ? null : new JsonObject { ["kind"] = Check.Kind, ["text"] = Check.Text, ["by"] = CheckBy },
        ["budget"] = new JsonObject { ["steps"] = Budget.Steps, ["minutes"] = Budget.Minutes, ["tokens"] = Budget.Tokens },
        ["status"] = Status,
        ["turns"] = Turns,
        ["steps"] = Steps,
        ["tokens"] = Tokens,
        ["ms"] = Ms,
        ["started"] = Started.ToString("o"),
        ["updated"] = Updated.ToString("o"),
        ["passed"] = Passed,
        ["said"] = Said,
        ["checkedAt"] = CheckedAt?.ToString("o"),
        ["outcome"] = Outcome,
        ["idle"] = IdleTurns,
    };

    public static GoalRun? FromJson(JsonObject? o)
    {
        if (o?.Str("text") is not { Length: > 0 } text)
        {
            return null;
        }
        var b = o["budget"] as JsonObject;
        var c = o["check"] as JsonObject;
        return new GoalRun
        {
            Text = text,
            Check = c?.Str("kind") is { } kind && c.Str("text") is { Length: > 0 } t ? new GoalCheck(kind, t) : null,
            CheckBy = c?.Str("by"),
            Budget = new GoalBudget(b?.Int("steps") ?? GoalBudget.DefaultSteps, b?.Int("minutes") ?? GoalBudget.DefaultMinutes, b?.Long("tokens") ?? GoalBudget.DefaultTokens),
            Status = o.Str("status") ?? Paused,
            Turns = o.Int("turns") ?? 0,
            Steps = o.Int("steps") ?? 0,
            Tokens = o.Long("tokens") ?? 0,
            Ms = o.Long("ms") ?? 0,
            Started = DateTimeOffset.TryParse(o.Str("started"), out var started) ? started : DateTimeOffset.UtcNow,
            Updated = DateTimeOffset.TryParse(o.Str("updated"), out var updated) ? updated : DateTimeOffset.UtcNow,
            Passed = o.Bool("passed"),
            Said = o.Str("said"),
            CheckedAt = DateTimeOffset.TryParse(o.Str("checkedAt"), out var at) ? at : null,
            Outcome = o.Str("outcome"),
            IdleTurns = o.Int("idle") ?? 0,
        };
    }
}

/// <summary>goal_check: the agent writes down how its goal will be checked, before it works on it.</summary>
internal static class GoalTools
{
    public const string CheckName = "goal_check";

    public static ToolDef Check() => new()
    {
        Name = CheckName,
        Kind = ToolKind.Read,
        // A command check runs at the end of each turn: it asks as a command does.
        KindOf = a => a.Str("command") is { Length: > 0 } ? ToolKind.Shell : ToolKind.Read,
        Description = "Goal mode: write down how the goal will be checked, before you work on it. Give a command that succeeds (exit code 0) only when the goal is met " +
            "(the tests, a build, a script), or else a condition that can be read in the code and its files. Code Arena runs the command (or has a fresh reader look at " +
            "the condition) at the end of each of your turns, and the goal ends once it passes. Set it once; change it only if it cannot work as written.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "command":{"type":"string","description":"A command run in the working directory; it passes on exit code 0."},
              "condition":{"type":"string","description":"Instead of a command: what must be true, concrete enough to check by reading."}}}
            """),
        Summary = a => a.Str("command") is { Length: > 0 } c ? Fmt.OneLine(c, 80) : Fmt.OneLine(a.Str("condition"), 80),
        Run = (a, c, ct) => Task.FromResult(c.GoalCheck is { } set ? set(a) : new ToolResult("No goal is set in this session.", Error: true)),
    };
}

/// <summary>The session's goal: set, worked on turn after turn, checked, paused, resumed, cancelled.</summary>
internal sealed partial class Runtime
{
    private GoalRun? _goal;
    // Set while a goal's turn runs: when its budget runs out mid-turn, the turn is stopped.
    private CancellationTokenSource? _goalLimit;
    private volatile bool _budgetHit;

    /// <summary>The session's goal (null: none set, or the last one ended and was cleared).</summary>
    public GoalRun? Goal => Volatile.Read(ref _goal);

    /// <summary>How long a command check may run.</summary>
    public static TimeSpan GoalCheckTime { get; set; } = TimeSpan.FromMinutes(15);

    private void SaveGoal()
    {
        if (Goal is { } goal)
        {
            goal.Updated = DateTimeOffset.UtcNow;
        }
        Session.Goal(Goal?.ToJson());
    }

    private void RestoreGoal(JsonObject? kept)
    {
        var goal = GoalRun.FromJson(kept);
        // A goal that was working when the session closed waits for the person: resume carries it on.
        if (goal is { Status: GoalRun.Working })
        {
            goal.Status = GoalRun.Paused;
        }
        Volatile.Write(ref _goal, goal);
        OfferGoalCheck(goal is { Active: true, Check: null });
    }

    /// <summary>goal_check is offered while a goal has no check yet; the requests' start changes only then.</summary>
    private void OfferGoalCheck(bool on)
    {
        if (Tools is null)
        {
            return;
        }
        Tools.Update(all => on
            ? all.Any(t => t.Name == GoalTools.CheckName) ? all : all.Append(GoalTools.Check())
            : all.Where(t => t.Name != GoalTools.CheckName));
    }

    /// <summary>
    /// A goal set (the IDE's Goal, /goal): worked on from the next turn, which the front end starts. Its check, when the
    /// person gives one; else the agent writes one down first. Null when it is set, else why not.
    /// </summary>
    public string? StartGoal(string text, GoalCheck? check, GoalBudget budget)
    {
        if (text.Trim().Length is 0 or > 4000)
        {
            return "Say the goal in 1 to 4,000 characters.";
        }
        if (budget.Wrong() is { } wrong)
        {
            return wrong;
        }
        if (check is { } c && (c.Kind is not (GoalCheck.Command or GoalCheck.Condition) || c.Text.Trim().Length is 0 or > 2000))
        {
            return "A check is a command or a condition, of 1 to 2,000 characters.";
        }
        if (Goal is { Active: true })
        {
            return "A goal is set already: cancel it first (/goal cancel, or Cancel in the IDE).";
        }
        if (Permissions.Mode == Mode.Plan)
        {
            return "Plan mode is read-only: switch the mode (/mode auto-edit, or yolo) to work toward a goal.";
        }
        var goal = new GoalRun { Text = text.Trim(), Check = check is null ? null : check with { Text = check.Text.Trim() }, CheckBy = check is null ? null : "person", Budget = budget };
        Volatile.Write(ref _goal, goal);
        OfferGoalCheck(check is null);
        SaveGoal();
        return null;
    }

    /// <summary>What goal_check does: the agent's check is written down (it asked as a command does, when it is one).</summary>
    private ToolResult SetGoalCheck(JsonObject a)
    {
        if (Goal is not { Active: true } goal)
        {
            return new ToolResult("No goal is being worked on in this session.", Error: true);
        }
        if (goal.CheckBy == "person")
        {
            return new ToolResult($"The person set the check: {goal.Check!.Describe()}. Work toward it.", Error: true);
        }
        var command = a.Str("command")?.Trim();
        var condition = a.Str("condition")?.Trim();
        if (command is { Length: > 2000 } || condition is { Length: > 2000 })
        {
            return new ToolResult("A check has at most 2,000 characters.", Error: true);
        }
        GoalCheck? check = command is { Length: > 0 } ? new GoalCheck(GoalCheck.Command, command) : condition is { Length: > 0 } ? new GoalCheck(GoalCheck.Condition, condition) : null;
        if (check is null)
        {
            return new ToolResult("Give a command, or a condition.", Error: true);
        }
        goal.Check = check;
        goal.CheckBy = "agent";
        SaveGoal();
        return new ToolResult($"Written down: the goal is met once {check.Describe()}. Code Arena checks it at the end of each of your turns. Now work toward it.");
    }

    public string? PauseGoal()
    {
        if (Goal is not { Status: GoalRun.Working } goal)
        {
            return "No goal is being worked on.";
        }
        goal.Status = GoalRun.Paused;
        SaveGoal();
        return null;
    }

    public string? ResumeGoal()
    {
        if (Goal is not { Status: GoalRun.Paused } goal)
        {
            return Goal is { Status: GoalRun.Working } ? "The goal is being worked on already." : "No goal is paused.";
        }
        if (Permissions.Mode == Mode.Plan)
        {
            return "Plan mode is read-only: switch the mode to carry the goal on.";
        }
        if (goal.Spent() is { } spent)
        {
            return $"The goal's budget is spent ({spent}): set it again with a bigger one.";
        }
        goal.Status = GoalRun.Working;
        goal.IdleTurns = 0;
        SaveGoal();
        return null;
    }

    /// <summary>The goal given up: the turn working on it, if one runs, is the front end's to stop.</summary>
    public string? CancelGoal()
    {
        if (Goal is not { Active: true } goal)
        {
            return "No goal is set.";
        }
        goal.Status = GoalRun.Cancelled;
        goal.Outcome = "Cancelled by the person.";
        OfferGoalCheck(false);
        SaveGoal();
        _goalLimit?.Cancel();
        return null;
    }

    /// <summary>The message the next goal turn starts with (null: no goal is being worked on).</summary>
    public string? GoalPrompt()
    {
        if (Goal is not { Status: GoalRun.Working } goal)
        {
            return null;
        }
        var budget = $"Budget left: {goal.Budget.Steps - goal.Steps:N0} steps, {Math.Max(0, goal.Budget.Minutes - goal.Ms / 60_000):N0} minutes, " +
                     $"{Fmt.Tokens(Math.Max(0, goal.Budget.Tokens - goal.Tokens))} tokens.";
        var check = goal.Check is { } c
            ? $"It is checked once you end your turn: {c.Describe()}{(goal.CheckBy == "person" ? " (the person's check)" : " (your check)")}."
            : $"First, before any change, write down how it will be checked with {GoalTools.CheckName}: a command that succeeds only when it is met (the tests, a build), or a condition to read. Then work.";
        if (goal.Turns == 0)
        {
            return $"Goal: {goal.Text}\n\nWork toward this goal until it is met, across as many turns as it takes: Code Arena carries on after each turn until the check passes. {check} {budget}\n" +
                   "End your turn when you believe it is met, or when you need the check's word on where it stands.";
        }
        var last = goal.Passed == false && goal.Said is { Length: > 0 } said ? $"The check did not pass yet:\n{said}\n\n" : "";
        return $"Goal (turn {goal.Turns + 1}): {goal.Text}\n\n{last}The goal is not met yet: carry on toward it. {check} {budget}";
    }

    /// <summary>
    /// A goal turn's own stop: its budget left, as time and (watched while it runs) as steps and tokens. The front end links
    /// it with the person's Stop and disposes it when the turn ends.
    /// </summary>
    public CancellationTokenSource GoalLimit(Spend turn)
    {
        var goal = Goal!;
        _budgetHit = false;
        var limit = new CancellationTokenSource();
        var left = TimeSpan.FromMilliseconds(Math.Max(1, goal.Budget.Minutes * 60_000L - goal.Ms));
        limit.CancelAfter(left);
        limit.Token.Register(() => _budgetHit = Goal?.Status == GoalRun.Working);
        _ = Task.Run(async () =>
        {
            while (!limit.IsCancellationRequested)
            {
                if (goal.Steps + Volatile.Read(ref turn.Requests) >= goal.Budget.Steps || goal.Tokens + Interlocked.Read(ref turn.Prompt) + Interlocked.Read(ref turn.Completion) >= goal.Budget.Tokens)
                {
                    try
                    {
                        await limit.CancelAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The turn ended meanwhile.
                    }
                    return;
                }
                try
                {
                    await Task.Delay(250, limit.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }, CancellationToken.None);
        _goalLimit = limit;
        return limit;
    }

    /// <summary>
    /// A goal turn ended (<paramref name="stopped"/>: the person stopped it): what it spent is counted, the check is run, and the
    /// goal is met, carries on, or stops (its budget, no progress, the person). What to tell the person, in a line or two.
    /// </summary>
    public async Task<string> AfterGoalTurnAsync(Spend turn, TimeSpan took, bool stopped, CancellationToken ct)
    {
        _goalLimit = null;
        if (Goal is not { } goal)
        {
            return "";
        }
        goal.Turns++;
        goal.Steps += turn.Requests;
        goal.Tokens += turn.Prompt + turn.Completion;
        goal.Ms += (long)took.TotalMilliseconds;
        if (goal.Status == GoalRun.Cancelled)
        {
            SaveGoal();
            return $"Goal cancelled after {goal.Usage()}.";
        }
        var budgetHit = _budgetHit;
        _budgetHit = false;
        if (goal.Status == GoalRun.Paused)
        {
            // Paused while the turn ran: it ended its turn, and waits for the person.
            SaveGoal();
            return $"The goal is paused ({goal.Usage()}). Resume carries it on.";
        }
        if (stopped && !budgetHit)
        {
            goal.Status = GoalRun.Paused;
            SaveGoal();
            return $"Stopped: the goal is paused ({goal.Usage()}). Resume carries it on.";
        }
        if (goal.Check is null)
        {
            if (goal.Turns >= GoalRun.MostTurnsUnchecked)
            {
                return End(goal, GoalRun.Stuck, $"The agent did not write down how to check the goal in {goal.Turns} turns: it stopped. Set it again with a check of your own.");
            }
            SaveGoal();
            return goal.Spent() is { } spentBefore
                ? End(goal, GoalRun.OutOfBudget, $"The goal's budget is spent ({spentBefore}) before it had a check.")
                : "The goal has no check yet: the agent is asked to write one down.";
        }
        var (passed, said) = await RunGoalCheckAsync(goal.Check, ct);
        goal.Passed = passed;
        goal.Said = said;
        goal.CheckedAt = DateTimeOffset.UtcNow;
        if (passed)
        {
            var how = goal.Check.Kind == GoalCheck.Command
                ? $"`{goal.Check.Text}` succeeded (exit code 0)"
                : $"a fresh reader found that this holds: {goal.Check.Text}{(said.Length > 0 ? $" ({Fmt.OneLine(said, 300)})" : "")}";
            return End(goal, GoalRun.Met, $"Goal met: {how}, after {goal.Usage()}.");
        }
        if ((goal.Spent() ?? (budgetHit ? "its budget is used" : null)) is { } spent)
        {
            return End(goal, GoalRun.OutOfBudget, $"The goal is not met and {spent}: it stopped after {goal.Usage()}. The last check said: {Fmt.OneLine(said, 300)}");
        }
        goal.IdleTurns = turn.Tools.IsEmpty ? goal.IdleTurns + 1 : 0;
        if (goal.IdleTurns >= GoalRun.MostIdleTurns)
        {
            return End(goal, GoalRun.Stuck, $"The agent ended {goal.IdleTurns} turns in a row without doing anything while the check still fails: the goal stopped. The last check said: {Fmt.OneLine(said, 300)}");
        }
        SaveGoal();
        return $"The goal is not met yet ({(goal.Check.Kind == GoalCheck.Command ? $"`{goal.Check.Text}` failed" : "the condition does not hold")}): the agent carries on. {goal.Usage()}.";
    }

    private string End(GoalRun goal, string status, string outcome)
    {
        goal.Status = status;
        goal.Outcome = outcome;
        OfferGoalCheck(false);
        SaveGoal();
        return outcome;
    }

    /// <summary>The check, run now: a command's exit code and the end of its output; a condition read by a fresh sub-agent.</summary>
    private async Task<(bool Passed, string Said)> RunGoalCheckAsync(GoalCheck check, CancellationToken ct)
    {
        if (check.Kind == GoalCheck.Command)
        {
            try
            {
                var run = await Proc.RunAsync(LocalTools.ShellCommand(check.Text, Context), GoalCheckTime, ct);
                var tail = Fmt.Tail(run.Output, 30);
                return run.TimedOut
                    ? (false, $"It ran past {Fmt.Duration(GoalCheckTime)} and was stopped.\n{tail}")
                    : (run.ExitCode == 0, $"Exit code {run.ExitCode}.{(tail.Length > 0 ? "\n" + tail : "")}");
            }
            catch (Exception e) when (e is ToolError or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return (false, $"It could not run: {e.Message}");
            }
        }
        var report = await RunSubAgentAsync(Inputs, "checking the goal",
            "Check whether this condition holds in the working directory now, by reading the code and its files (change nothing):\n\n" + check.Text +
            "\n\nAnswer with MET or NOT MET on the first line, then in a few lines what you looked at and why.", null, ct);
        var first = report.TrimStart().Split('\n', 2);
        var verdict = first[0].Trim().TrimEnd('.', ':').ToUpperInvariant();
        var why = first.Length > 1 ? first[1].Trim() : "";
        return (verdict.StartsWith("MET", StringComparison.Ordinal), why.Length > 0 ? why : report.Trim());
    }

    /// <summary>The goal in a few lines: for /goal, and the IDE's tooltip.</summary>
    public string DescribeGoal()
    {
        if (Goal is not { } goal)
        {
            return "No goal is set. /goal TEXT sets one: the agent works until it is met.";
        }
        var state = goal.Status switch
        {
            GoalRun.Working => "being worked on",
            GoalRun.Paused => "paused",
            GoalRun.Met => "met",
            GoalRun.OutOfBudget => "stopped: its budget is spent",
            GoalRun.Stuck => "stopped: no progress",
            _ => "cancelled",
        };
        var lines = new List<string>
        {
            $"Goal ({state}): {goal.Text}",
            $"  Check: {(goal.Check is { } c ? c.Describe() + (goal.CheckBy == "agent" ? " (written down by the agent)" : "") : "not written down yet")}",
            $"  Budget: {goal.Budget.Describe()}",
            $"  Used: {goal.Usage()}",
        };
        if (goal.CheckedAt is not null && goal.Said is { Length: > 0 } said)
        {
            lines.Add($"  Last check ({(goal.Passed == true ? "passed" : "failed")}): {Fmt.OneLine(said, 200)}");
        }
        if (goal.Outcome is { } outcome)
        {
            lines.Add($"  {outcome}");
        }
        return string.Join("\n", lines);
    }
}
