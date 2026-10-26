using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A step of the agent's plan: what is done in it, its status, what done means (<see cref="Check"/>: a command whose
/// success shows it, or what to look at), the files it changes, the steps it waits for, and how its check went.
/// </summary>
internal sealed record WorkStep(int Number, string Title, string Status = WorkStep.Todo, string? Check = null, IReadOnlyList<string>? Files = null,
    IReadOnlyList<int>? DependsOn = null, string? Notes = null, string? CheckResult = null, string? CheckOutput = null, DateTimeOffset? CheckedAt = null)
{
    public const string Todo = "todo";
    public const string Doing = "doing";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public static readonly string[] States = [Todo, Doing, Blocked, Done];

    public IReadOnlyList<string> Files { get; init; } = Files ?? [];
    public IReadOnlyList<int> DependsOn { get; init; } = DependsOn ?? [];
}

/// <summary>A plan refused: the words say what to change.</summary>
internal sealed class PlanError(string message) : Exception(message);

/// <summary>
/// The agent's working plan (the plan tool): a goal and its steps, kept with the session (every compaction keeps it: it is
/// said again after each summary), shown in the terminal and the IDE, and kept in Arena with the session's chat, on the
/// Plans board. The same rules as Arena's plans: steps numbered from 1, each waits only for steps there, never in a circle.
/// </summary>
internal sealed record WorkPlan(string Title, string? Goal, IReadOnlyList<WorkStep> Steps)
{
    /// <summary>Made in plan mode: the person reviews it (approve, change a step, another way, reject) before it is carried out.</summary>
    public const string Proposed = "proposed";
    /// <summary>The person approved it: the agent carries it out.</summary>
    public const string Approved = "approved";

    public const int MostSteps = 200;

    /// <summary>Where its review stands: <see cref="Proposed"/>, <see cref="Approved"/>, or null (made outside plan mode).</summary>
    public string? Review { get; init; }

    /// <summary>What a second reading with fresh eyes said of it before the person's review ("" : it held); null: not read again.</summary>
    public string? Critique { get; init; }
    public const int MostFiles = 50;

    public static readonly WorkPlan None = new("", null, []);

    public bool IsEmpty => Steps.Count == 0 && Title.Length == 0;

    public WorkStep? Current => Steps.FirstOrDefault(s => s.Status == WorkStep.Doing);

    /// <summary>
    /// The plan with the plan tool's changes: its title and goal, and steps (with a number: that step changed, or taken out
    /// with remove; without: one added at the end); clear starts again from nothing.
    /// </summary>
    public WorkPlan Apply(JsonObject args)
    {
        var plan = args.Bool("clear") == true ? None : this;
        var title = args.Str("title")?.Trim() is { Length: > 0 } t ? Cut(t, 200) : plan.Title;
        var goal = args.Str("goal") is { } g ? (g.Trim().Length == 0 ? null : Cut(g.Trim(), 4000)) : plan.Goal;
        var steps = plan.Steps.ToList();
        foreach (var change in (args["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            WorkStep step;
            var number = change.Int("number");
            if (number is { } n)
            {
                var at = steps.FindIndex(s => s.Number == n);
                if (at < 0)
                {
                    throw new PlanError($"The plan has no step {n}: leave out number to add a step.");
                }
                if (change.Bool("remove") == true)
                {
                    steps.RemoveAt(at);
                    for (var i = 0; i < steps.Count; i++)
                    {
                        steps[i] = steps[i] with { DependsOn = [.. steps[i].DependsOn.Where(d => d != n)] };
                    }
                    continue;
                }
                step = steps[at];
            }
            else
            {
                if (string.IsNullOrWhiteSpace(change.Str("title")))
                {
                    throw new PlanError("A new step needs a title.");
                }
                step = new WorkStep(steps.Count == 0 ? 1 : steps.Max(s => s.Number) + 1, "");
            }
            if (change.Str("title")?.Trim() is { Length: > 0 } title2)
            {
                step = step with { Title = Cut(title2, 300) };
            }
            if (change.Str("status") is { } status)
            {
                step = WorkStep.States.Contains(status) ? step with { Status = status }
                    : throw new PlanError($"A step's status is one of {string.Join(", ", WorkStep.States)}, not \"{status}\".");
            }
            if (change.Str("check") is { } check)
            {
                step = step with { Check = check.Trim().Length == 0 ? null : Cut(check.Trim(), 1000), CheckResult = null, CheckOutput = null, CheckedAt = null };
            }
            if (change["files"] is JsonArray files)
            {
                step = step with
                {
                    Files = [.. files.OfType<JsonValue>().Select(f => f.ToString().Trim().Replace('\\', '/')).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).Take(MostFiles).Select(f => Cut(f, 300))],
                };
            }
            if (change["depends_on"] is JsonArray deps)
            {
                step = step with { DependsOn = [.. deps.OfType<JsonValue>().Select(d => d.TryGetValue<int>(out var x) ? x : 0).Distinct().Order()] };
            }
            if (change.Str("notes") is { } notes)
            {
                step = step with { Notes = notes.Trim().Length == 0 ? null : Cut(notes.Trim(), 4000) };
            }
            if (number is { } same)
            {
                steps[steps.FindIndex(s => s.Number == same)] = step;
            }
            else
            {
                steps.Add(step);
            }
        }
        var made = new WorkPlan(title.Length == 0 && steps.Count > 0 ? "Plan" : title, goal, steps) { Review = plan.Review, Critique = plan.Critique };
        made.Validate();
        return made;
    }

    /// <summary>A step's check ran: how it went, the end of what it said.</summary>
    public WorkPlan Checked(int number, bool passed, string output, DateTimeOffset at) =>
        this with { Steps = [.. Steps.Select(s => s.Number == number ? s with { CheckResult = passed ? "passed" : "failed", CheckOutput = Tail(output, 4000), CheckedAt = at } : s)] };

    private void Validate()
    {
        if (Steps.Count > MostSteps)
        {
            throw new PlanError($"A plan holds at most {MostSteps} steps.");
        }
        var numbers = Steps.Select(s => s.Number).ToHashSet();
        foreach (var s in Steps)
        {
            if (s.DependsOn.FirstOrDefault(d => !numbers.Contains(d) || d == s.Number) is var bad && bad != 0)
            {
                throw new PlanError($"Step {s.Number} waits for step {bad}, which {(bad == s.Number ? "is itself" : "the plan does not have")}.");
            }
        }
        if (Steps.Count(s => s.Status == WorkStep.Doing) > 1)
        {
            throw new PlanError($"One step is doing at a time: steps {string.Join(", ", Steps.Where(s => s.Status == WorkStep.Doing).Select(s => s.Number))} are.");
        }
        if (Circle() is { } circle)
        {
            throw new PlanError($"Steps {string.Join(" → ", circle)} wait for one another in a circle.");
        }
    }

    private List<int>? Circle()
    {
        var deps = Steps.ToDictionary(s => s.Number, s => s.DependsOn);
        var state = new Dictionary<int, int>();
        var path = new List<int>();
        List<int>? Visit(int n)
        {
            state[n] = 1;
            path.Add(n);
            foreach (var d in deps[n])
            {
                if (state.GetValueOrDefault(d) == 1)
                {
                    return [.. path.SkipWhile(x => x != d), d];
                }
                if (state.GetValueOrDefault(d) == 0 && Visit(d) is { } found)
                {
                    return found;
                }
            }
            state[n] = 2;
            path.RemoveAt(path.Count - 1);
            return null;
        }
        return deps.Keys.Order().Select(n => state.GetValueOrDefault(n) == 0 ? Visit(n) : null).FirstOrDefault(c => c is not null);
    }

    /// <summary>The plan for the model to read: each step on a line, with what done means and how its check went.</summary>
    public string Text()
    {
        if (IsEmpty)
        {
            return "No plan.";
        }
        var lines = new List<string> { $"Plan: {Title}" + (Review switch { Proposed => " (proposed: the person reviews it before it is carried out)", Approved => " (approved)", _ => "" })
            + (Goal is null ? "" : $"\nGoal: {Goal}") };
        foreach (var s in Steps)
        {
            var line = $"{s.Number}. [{s.Status}] {s.Title}";
            if (s.DependsOn.Count > 0)
            {
                line += $" (after {string.Join(", ", s.DependsOn)})";
            }
            if (s.Check is not null)
            {
                line += $"\n   check: {s.Check}" + (s.CheckResult is null ? "" : $" ({s.CheckResult})");
            }
            if (s.Files.Count > 0)
            {
                line += $"\n   files: {string.Join(", ", s.Files)}";
            }
            if (s.Notes is not null)
            {
                line += $"\n   notes: {Fmt.OneLine(s.Notes, 300)}";
            }
            lines.Add(line);
        }
        var done = Steps.Count(s => s.Status == WorkStep.Done);
        lines.Add($"{done} of {Steps.Count} done.");
        return string.Join('\n', lines);
    }

    /// <summary>The plan as the terminal shows it.</summary>
    public string Render(Ui ui)
    {
        if (IsEmpty)
        {
            return ui.Dim("(no plan)");
        }
        var lines = new List<string> { ui.Bold(Title) + ui.Dim($"  {Steps.Count(s => s.Status == WorkStep.Done)}/{Steps.Count} done") + (Review == Proposed ? ui.Yellow("  proposed") : "") };
        if (Goal is not null)
        {
            lines.Add(ui.Dim(Goal));
        }
        foreach (var s in Steps)
        {
            var mark = s.Status switch
            {
                WorkStep.Done => ui.Green("☒ "),
                WorkStep.Doing => ui.Yellow("◐ "),
                WorkStep.Blocked => ui.Red("⊘ "),
                _ => "☐ ",
            };
            var title = s.Status switch { WorkStep.Done => ui.Dim(s.Title), WorkStep.Doing => ui.Bold(s.Title), _ => s.Title };
            var check = s.Check is null ? "" : ui.Dim("  ✓ ") + ui.Dim(Fmt.OneLine(s.Check, 60)) + s.CheckResult switch
            {
                "passed" => ui.Green(" passed"),
                "failed" => ui.Red(" failed"),
                _ => "",
            };
            lines.Add($"{mark}{s.Number}. {title}{check}");
        }
        return string.Join('\n', lines);
    }

    public JsonObject ToJson() => new()
    {
        ["title"] = Title,
        ["goal"] = Goal,
        ["review"] = Review,
        ["critique"] = Critique,
        ["steps"] = new JsonArray([.. Steps.Select(s => (JsonNode)new JsonObject
        {
            ["number"] = s.Number, ["title"] = s.Title, ["status"] = s.Status, ["check"] = s.Check,
            ["files"] = new JsonArray([.. s.Files.Select(f => (JsonNode)f)]), ["dependsOn"] = new JsonArray([.. s.DependsOn.Select(d => (JsonNode)d)]),
            ["notes"] = s.Notes, ["checkResult"] = s.CheckResult, ["checkOutput"] = s.CheckOutput, ["checkedAt"] = s.CheckedAt?.ToString("O"),
        })]),
    };

    public static WorkPlan FromJson(JsonObject? o) => o is null ? None : new WorkPlan(o.Str("title") ?? "", o.Str("goal"),
        [.. (o["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(s => new WorkStep(s.Int("number") ?? 0, s.Str("title") ?? "", s.Str("status") ?? WorkStep.Todo,
            s.Str("check"), [.. (s["files"] as JsonArray ?? []).OfType<JsonValue>().Select(f => f.ToString())],
            [.. (s["dependsOn"] as JsonArray ?? []).OfType<JsonValue>().Select(d => d.TryGetValue<int>(out var x) ? x : 0)],
            s.Str("notes"), s.Str("checkResult"), s.Str("checkOutput"), DateTimeOffset.TryParse(s.Str("checkedAt"), out var at) ? at : null))])
    {
        Review = o.Str("review") is Proposed or Approved ? o.Str("review") : null,
        Critique = o.Str("critique"),
    };

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];
}
