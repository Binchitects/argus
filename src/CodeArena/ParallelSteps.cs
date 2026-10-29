using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Steps at once (run_steps): steps of the plan that wait for nothing not done run together. A step that changes no
/// file (research) goes to a read-only sub-agent; one that changes files goes to a worker in a git worktree of its own,
/// made from the project as it is now: it edits there, runs no command but its step's check, and its change comes back
/// once that check passed, merged into the project (git apply --3way), each file kept for /rewind. Each step ends done
/// or blocked, with why in its notes.
/// </summary>
internal sealed partial class Runtime
{
    /// <summary>The most steps run at once.</summary>
    public const int MostAtOnce = 4;

    /// <summary>The steps a worker or a sub-agent runs as, and what came of each.</summary>
    private sealed record StepRun(WorkStep Step, bool Done, string Said, string Report);

    public async Task<string> RunStepsAsync(IReadOnlyList<int> numbers, CancellationToken ct)
    {
        var inputs = _inputs ?? throw new InvalidOperationException("Code Arena has not started yet.");
        var plan = Context.Plan;
        if (plan.Review == WorkPlan.Proposed)
        {
            throw new ToolError("The plan waits for the person's review: it is carried out once approved.");
        }
        var asked = numbers.Distinct().ToList();
        if (asked.Count < 2 || asked.Count > MostAtOnce)
        {
            throw new ToolError($"run_steps runs 2 to {MostAtOnce} steps at once; carry one step out yourself.");
        }
        var steps = new List<WorkStep>();
        foreach (var n in asked)
        {
            var step = plan.Steps.FirstOrDefault(s => s.Number == n) ?? throw new ToolError($"The plan has no step {n}.");
            if (step.Status is WorkStep.Done or WorkStep.Doing)
            {
                throw new ToolError($"Step {n} is {step.Status} already.");
            }
            if (step.DependsOn.FirstOrDefault(d => plan.Steps.FirstOrDefault(s => s.Number == d)?.Status != WorkStep.Done) is var waits and > 0)
            {
                throw new ToolError($"Step {n} waits for step {waits}, which is not done: run only steps that wait for nothing not done.");
            }
            steps.Add(step);
        }
        var changing = steps.Where(s => s.Files.Count > 0).ToList();
        if (changing.Count > 0)
        {
            if (Permissions.Mode is not (Mode.AutoEdit or Mode.Yolo))
            {
                throw new ToolError($"Steps that change files ({string.Join(", ", changing.Select(s => s.Number))}) run at once only in auto-edit or yolo mode, where edits run without asking: carry them out one at a time.");
            }
            if (SystemPrompt.GitRoot(Workspace.Root) is null)
            {
                throw new ToolError("Steps that change files run at once in git worktrees: this folder is not a git repository. Carry them out one at a time.");
            }
        }
        Ui.Info($"Running steps {string.Join(", ", asked)} at once{(changing.Count > 0 ? $": {string.Join(", ", changing.Select(s => s.Number))} each in a worktree of its own" : "")}…");
        var basis = changing.Count > 0 ? await BasisAsync(ct) : null;
        // Doing, all of them, while they run (the one-at-a-time rule is the agent's own; these are run together).
        SetPlan(plan with { Steps = [.. plan.Steps.Select(s => asked.Contains(s.Number) ? s with { Status = WorkStep.Doing } : s)] });
        var runs = await Task.WhenAll(steps.Select(s => s.Files.Count == 0 ? ResearchAsync(inputs, plan, s, ct) : WorkerAsync(inputs, plan, s, basis!, ct)));

        // Each step as it ended: done, or blocked with why.
        var now = Context.Plan;
        SetPlan(now with
        {
            Steps = [.. now.Steps.Select(s => runs.FirstOrDefault(r => r.Step.Number == s.Number) is { } r
                ? r.Step with { Status = r.Done ? WorkStep.Done : WorkStep.Blocked, Notes = Note(r.Step.Notes, r.Said) }
                : s)],
        });
        var report = new StringBuilder();
        foreach (var r in runs)
        {
            report.Append($"Step {r.Step.Number} ({r.Step.Title}): {(r.Done ? "done" : "blocked")}. {r.Said}\n");
            if (r.Report.Trim() is { Length: > 0 } said)
            {
                report.Append(said.Length > 8000 ? said[..8000] + "…" : said).Append('\n');
            }
            report.Append('\n');
        }
        report.Append(Context.Plan.Text());
        return report.ToString().Trim();
    }

    /// <summary>A step's notes with what came of it added, at most 4,000 characters.</summary>
    private static string Note(string? notes, string said) => (notes is null ? said : notes + "\n" + said) is var all && all.Length > 4000 ? all[..4000] : all;

    /// <summary>A step that changes no file: a read-only sub-agent finds out, and its report is the step's.</summary>
    private async Task<StepRun> ResearchAsync(SystemPrompt.Inputs inputs, WorkPlan plan, WorkStep step, CancellationToken ct)
    {
        try
        {
            var report = await RunSubAgentAsync(inputs, $"step {step.Number}", StepPrompt(plan, step, worktree: null), null, ct);
            return new StepRun(step, true, "Found out by a sub-agent: its report is below.", report);
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException or IOException or ToolError)
        {
            return new StepRun(step, false, $"Its sub-agent could not finish: {e.Message}", "");
        }
    }

    /// <summary>
    /// A step that changes files: a worker in a worktree of its own (a copy of the project as it is now) carries it out;
    /// its check runs there; once it passed, the change is merged into the project.
    /// </summary>
    private async Task<StepRun> WorkerAsync(SystemPrompt.Inputs inputs, WorkPlan plan, WorkStep step, string basis, CancellationToken ct)
    {
        var tree = Path.Combine(Path.GetTempPath(), "code-arena-steps", Session.Id, $"step-{step.Number}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Path.GetDirectoryName(tree)!);
        var made = await GitAsync(Workspace.Root, ct, "worktree", "add", "--detach", tree, basis);
        if (made.ExitCode != 0)
        {
            return new StepRun(step, false, $"Its worktree could not be made: {Fmt.OneLine(made.Output, 300)}", "");
        }
        var keep = false;
        try
        {
            var workspace = new Workspace(tree, []);
            var quiet = new Ui(new StringReader(""), TextWriter.Null, TextWriter.Null, color: false, canAsk: false);
            var sandbox = Sandbox == Sandbox.None ? Sandbox.None : Sandbox.Choose(Config, workspace, Env.Paths, Env.Env, $"{Session.Id}-step-{step.Number}");
            WorkStep ran = step;
            var context = new ToolContext
            {
                Workspace = workspace, Ui = quiet, Shell = Config.Shell, Sandbox = sandbox, IsCommand = check => IsCommand(check, tree),
                CommandRan = (command, passed, output) => ran = new WorkPlan("", null, [ran]).Ran(command, passed, output, DateTimeOffset.UtcNow).Steps[0],
            };
            // It edits its worktree freely, and runs no command but its own check: anything else is declined (it cannot ask).
            var permissions = new Permissions(quiet, Mode.AutoEdit)
            {
                Workspace = workspace, Rules = Permissions.Rules, Guard = Permissions.Guard,
                Asker = (q, _) => Task.FromResult(q.Tool.Kind == ToolKind.Shell && step.Check is { } check && Same(q.Args.Str("command"), check) && q.Risk is null ? Approval.Yes : Approval.No),
            };
            var worker = new Agent
            {
                Gateway = Gateway,
                // The code index is of the folder, not of the step's worktree: grep there.
                Tools = new ToolBox(Tools.All.Where(t => t.Kind != ToolKind.Agent && t.Name is not ("stop_command" or "command_output" or "ask_agent") && !CodeIndexTools.Names.Contains(t.Name))),
                Permissions = permissions,
                Context = context,
                Ui = quiet,
                Model = Model,
                SystemPrompt = () => SystemPrompt.Build(inputs, subAgent: true) + $"\n\nYou work in a worktree of your own at {tree}: a copy of the project, apart from the person's. Change only what your step needs.",
                Total = Total,
                Depth = 1,
                Stream = false,
                MaxSteps = 80,
                Compaction = Compaction,
            };
            var report = await worker.RunAsync(StepPrompt(plan, step, tree), Turn ?? new Spend(), ct);
            // Its check, run here once more on what it left: done only when it passes.
            if (step.Check is { } command && IsCommand(command, tree))
            {
                var run = await Proc.RunAsync(LocalTools.ShellCommand(command, context), TimeSpan.FromMinutes(10), ct);
                ran = new WorkPlan("", null, [ran]).Ran(command, run.ExitCode == 0 && !run.TimedOut, run.Output.TrimEnd(), DateTimeOffset.UtcNow).Steps[0];
                if (ran.CheckResult != "passed")
                {
                    keep = true;
                    return new StepRun(ran, false, $"Its check `{command}` failed in its worktree ({tree}, kept for you to look at): {Fmt.OneLine(Tail(run.Output, 400), 400)}", report);
                }
            }
            var merged = await MergeAsync(tree, basis, step, ct);
            keep = merged is not null;
            return merged is null
                ? new StepRun(ran, true, step.Check is null ? "Changed in its worktree and merged into the project." : $"Changed in its worktree, its check `{step.Check}` passed, and merged into the project.", report)
                : new StepRun(ran, false, merged, report);
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException or IOException or ToolError or InvalidOperationException)
        {
            keep = true;
            return new StepRun(step, false, $"Its worker could not finish ({e.Message}); its worktree is kept: {tree}.", "");
        }
        finally
        {
            if (!keep)
            {
                await GitAsync(Workspace.Root, CancellationToken.None, "worktree", "remove", "--force", tree);
            }
        }
    }

    /// <summary>The project as it is now, as a commit (git stash create keeps what is changed and not committed), else HEAD.</summary>
    private async Task<string> BasisAsync(CancellationToken ct)
    {
        var stash = await GitAsync(Workspace.Root, ct, "stash", "create");
        if (stash.ExitCode == 0 && stash.Output.Trim() is { Length: > 0 } commit)
        {
            return commit;
        }
        var head = await GitAsync(Workspace.Root, ct, "rev-parse", "HEAD");
        return head.ExitCode == 0 ? head.Output.Trim() : throw new ToolError("The repository has no commit yet: steps that change files run at once from one.");
    }

    /// <summary>The worker's change, from the basis, applied to the project (each file kept for /rewind first); null when merged, else why not.</summary>
    private async Task<string?> MergeAsync(string tree, string basis, WorkStep step, CancellationToken ct)
    {
        await GitAsync(tree, ct, "add", "-A");
        var diff = await GitAsync(tree, ct, "diff", "--cached", "--binary", basis);
        if (diff.ExitCode != 0)
        {
            return $"Its change could not be read: {Fmt.OneLine(diff.Output, 300)}";
        }
        if (diff.Output.Trim().Length == 0)
        {
            return null;
        }
        var names = await GitAsync(tree, ct, "diff", "--cached", "--name-only", basis);
        foreach (var name in names.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Checkpoints.BeforeWrite(Path.GetFullPath(Path.Combine(Workspace.Root, name.Trim())));
        }
        var applied = await GitInAsync(Workspace.Root, diff.Output, ct, "apply", "--3way", "--binary", "--whitespace=nowarn");
        return applied.ExitCode == 0 ? null
            : $"Its change does not merge into the project as it is now ({Fmt.OneLine(Tail(applied.Output, 400), 400)}); its worktree is kept: {tree}.";
    }

    private static string StepPrompt(WorkPlan plan, WorkStep step, string? worktree) =>
        $"Carry out step {step.Number} of this plan, and only it: {step.Title}.\n"
        + (step.Files.Count > 0 ? $"Its files: {string.Join(", ", step.Files)}.\n" : "")
        + (step.Symbols.Count > 0 ? $"What it changes: {string.Join(", ", step.Symbols)}.\n" : "")
        + (step.Notes is { } notes ? $"Its notes: {notes}\n" : "")
        + (step.Check is { } check
            ? worktree is null ? $"Done means: {check}\n" : $"Done means: {check}. When you have changed what it needs, run its check with run_shell exactly as written: `{check}` (no other command runs here).\n"
            : "")
        + (worktree is null ? "You cannot change files: find out what the step asks, and report it plainly with the paths and lines it rests on.\n"
            : "Change the files with edit_file and write_file. At the end, report in a few lines what you changed and how the check went.\n")
        + $"\nThe whole plan, for what is around your step:\n{plan.Text()}";

    private static bool Same(string? command, string check) =>
        command is not null && string.Join(' ', command.Trim().Trim('`').Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            == string.Join(' ', check.Trim().Trim('`').Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string Tail(string text, int most) => text.Length <= most ? text : text[^most..];

    /// <summary>git in a folder, its output and error together.</summary>
    private static Task<Proc.Result> GitAsync(string dir, CancellationToken ct, params string[] args) => GitInAsync(dir, null, ct, args);

    /// <summary>git in a folder, <paramref name="stdin"/> fed to it when given.</summary>
    private static async Task<Proc.Result> GitInAsync(string dir, string? stdin, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        if (stdin is null)
        {
            return await Proc.RunAsync(psi, TimeSpan.FromMinutes(2), ct);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        return new Proc.Result(p.ExitCode, (await output) + (await error), false);
    }
}
