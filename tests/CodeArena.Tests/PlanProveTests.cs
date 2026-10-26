using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Carry out and prove: a step's check run with run_shell is recorded on it, nothing is done that was not checked, and a step can be undone.</summary>
public sealed class PlanProveTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static readonly Func<string, bool> Commands = check => check.Split(' ')[0] is "make" or "true" or "false";

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void A_step_whose_check_is_a_command_is_done_only_once_it_passed_since_it_began_and_words_to_look_by_are_taken_at_their_word()
    {
        var plan = WorkPlan.None.Apply(Args("""{"title":"T","steps":[{"title":"Fix","check":"make test","status":"doing"},{"title":"Look","check":"The page shows the cart"}]}"""), Commands);
        var refused = Assert.Throws<PlanError>(() => plan.Apply(Args("""{"steps":[{"number":1,"status":"done"}]}"""), Commands));
        Assert.Contains("run it with run_shell (`make test`) since the step began.", refused.Message, StringComparison.Ordinal);

        // A run of another command is not its check; its own run that failed is said so.
        Assert.Same(plan, plan.Ran("make build", false, "boom", DateTimeOffset.UtcNow));
        var failed = plan.Ran(" make  test ", false, "1 failed\n[exit code 2]", DateTimeOffset.UtcNow);
        Assert.Equal(("failed", "1 failed\n[exit code 2]"), (failed.Steps[0].CheckResult, failed.Steps[0].CheckOutput));
        Assert.Contains("it failed last time", Assert.Throws<PlanError>(() => failed.Apply(Args("""{"steps":[{"number":1,"status":"done"}]}"""), Commands)).Message, StringComparison.Ordinal);

        var passed = failed.Ran("make test", true, "[exit code 0]", DateTimeOffset.UtcNow);
        var done = passed.Apply(Args("""{"steps":[{"number":1,"status":"done"},{"number":2,"status":"done"}]}"""), Commands);
        Assert.Equal(["done", "done"], done.Steps.Select(s => s.Status));
        // Begun again, its check counts from then.
        Assert.Null(done.Apply(Args("""{"steps":[{"number":1,"status":"doing"}]}"""), Commands).Steps[0].CheckResult);
    }

    [Fact]
    public async Task Carried_out_the_checks_run_are_recorded_a_failed_one_keeps_the_step_open_and_a_step_is_undone_with_its_files()
    {
        // One call a round, each after the result of the one before.
        var rounds = new Queue<Reply>([
            Reply.Call(("plan", """{"title":"Fix","steps":[{"title":"Write a","check":"true","files":["a.txt"],"status":"doing"},{"title":"Prove it","check":"false"}]}""")),
            Reply.Call(("write_file", """{"path":"a.txt","content":"one\n"}""")),
            Reply.Call(("run_shell", """{"command":"true"}""")),
            Reply.Call(("plan", """{"steps":[{"number":1,"status":"done"},{"number":2,"status":"doing"}]}""")),
            Reply.Call(("run_shell", """{"command":"false"}""")),
            Reply.Call(("plan", """{"steps":[{"number":2,"status":"done"}]}""")),
        ]);
        var first = true;
        _gateway.Answer = req =>
        {
            var role = req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>();
            if (rounds.Count > 0 && (role == "tool" || (first && FakeGateway.Last(req) == "go")))
            {
                first = false;
                return rounds.Dequeue();
            }
            return Reply.Say("ok");
        };
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("go\n/plan\n/plan undo 1\n/plan undo 1\n/exit\n", "chat", "--mode", "yolo"));
        var results = _gateway.Requests.Last(r => FakeGateway.History(r).Any(m => m!["role"]!.GetValue<string>() == "tool"))["messages"]!.AsArray()
            .Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>()).ToList();
        // Step 1's check passed, so it is done; step 2's failed, so done is refused and it stays doing.
        Assert.Contains(results, r => r.StartsWith("Plan: Fix", StringComparison.Ordinal) && r.Contains("1. [done] Write a\n   check: true (passed)", StringComparison.Ordinal));
        Assert.Contains(results, r => r.Contains("Step 2 is done only once its check passed", StringComparison.Ordinal) && r.Contains("it failed last time", StringComparison.Ordinal));
        Assert.Contains("◐ 2. Prove it  ✓ false failed", h.Out, StringComparison.Ordinal);

        // Undone: a.txt as before step 1 began (it was not there), both steps to do again, and the agent told.
        Assert.False(File.Exists(Path.Combine(h.Work, "a.txt")));
        Assert.Contains("I undid steps 1, 2 of the plan: a.txt is as before it began. What commands changed is not undone. The steps are to do again.", h.Out, StringComparison.Ordinal);
        Assert.Contains("☐ 1. Write a", h.Out, StringComparison.Ordinal);
        Assert.Contains("No step has begun in this run: there is nothing to undo.", h.Err, StringComparison.Ordinal);
    }
}

/// <summary>Long tasks across sessions: a plan an earlier session left in progress is carried on by the next one.</summary>
public sealed class PlanResumeTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public PlanResumeTests() => _gateway.Answer = req =>
        req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" && FakeGateway.Last(req) == "plan it"
            ? Reply.Call(("plan", """{"title":"Big refactor","steps":[{"title":"Split the module","status":"done"},{"title":"Move the callers","status":"doing","notes":"half of them moved"},{"title":"Delete the old one"}]}"""))
            : Reply.Say("ok");

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public async Task The_next_session_of_the_folder_is_told_of_the_plan_in_progress_and_carries_it_on_with_its_progress_and_notes()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("plan it\n/exit\n", "chat"));

        // A new session: told, and /plan resume carries it on; the agent hears where it stands.
        Assert.Equal(0, await h.Run("/plan resume\nwhere are we\n/plan resume\n/exit\n", "chat"));
        Assert.Contains("An earlier session's plan is in progress here: \"Big refactor\", 1 of 3 steps done", h.Out, StringComparison.Ordinal);
        Assert.Contains("Carried on: \"Big refactor\"", h.Out, StringComparison.Ordinal);
        var told = FakeGateway.History(_gateway.Requests[^1]).Select(m => m!["content"]?.ToString() ?? "").ToList();
        Assert.Contains(told, m => m.StartsWith("The plan an earlier session of this folder left in progress", StringComparison.Ordinal)
            && m.Contains("2. [doing] Move the callers", StringComparison.Ordinal) && m.Contains("notes: half of them moved", StringComparison.Ordinal)
            && m.Contains("see what that session changed", StringComparison.Ordinal));
        // Now its own: not carried on twice.
        Assert.Contains("This session has a plan of its own", h.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_Arena_with_plan_starts_a_session_that_carries_it_on_and_a_finished_plan_is_not_offered()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("plan it\n/exit\n", "chat"));
        Assert.Equal(0, await h.Run("", "-p", "go on", "--plan"));
        Assert.Contains(FakeGateway.History(_gateway.Requests[^1]), m => (m!["content"]?.ToString() ?? "").StartsWith("The plan an earlier session", StringComparison.Ordinal));
        Assert.Equal("go on", FakeGateway.Last(_gateway.Requests[^1]));

        using var done = new Harness(_gateway, _mcp);
        _gateway.Answer = req => req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" && FakeGateway.Last(req) == "plan it"
            ? Reply.Call(("plan", """{"title":"Small","steps":[{"title":"Only step","status":"done"}]}""")) : Reply.Say("ok");
        Assert.Equal(0, await done.Run("plan it\n/exit\n", "chat"));
        Assert.Equal(0, await done.Run("/exit\n", "chat"));
        Assert.DoesNotContain("plan is in progress here", done.Out, StringComparison.Ordinal);
    }
}

/// <summary>Steps at once: research by read-only sub-agents, changes in worktrees of their own, merged back once their checks passed.</summary>
public sealed class RunStepsTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string Git(string dir, params string[] args)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = git.StandardOutput.ReadToEnd() + git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, output);
        return output;
    }

    /// <summary>What a conversation was started with: the main one's question, or a step's task.</summary>
    private static string Started(JsonObject req) =>
        FakeGateway.History(req).First(m => m["role"]!.GetValue<string>() == "user")["content"]!.ToString();

    private static int Results(JsonObject req) => FakeGateway.History(req).Count(m => m["role"]!.GetValue<string>() == "tool");

    [Fact]
    public async Task Steps_that_wait_for_nothing_run_at_once_research_reports_a_change_merges_once_its_check_passed_and_a_failed_one_is_blocked_and_kept()
    {
        _gateway.Answer = req =>
        {
            var start = Started(req);
            var results = Results(req);
            if (start.StartsWith("Carry out step 1 ", StringComparison.Ordinal))
            {
                // Research: no tool that changes anything is offered.
                Assert.DoesNotContain(req["tools"]!.AsArray(), t => t!["function"]!["name"]!.GetValue<string>() is "write_file" or "edit_file" or "run_shell");
                return Reply.Say("The parser is in a.txt, line 1.");
            }
            if (start.StartsWith("Carry out step 2 ", StringComparison.Ordinal))
            {
                return results switch
                {
                    0 => Reply.Call(("write_file", """{"path":"a.txt","content":"ONE\n"}""")),
                    1 => Reply.Call(("run_shell", """{"command":"rm -rf ."}""")),
                    2 => Reply.Call(("run_shell", """{"command":"true"}""")),
                    _ => Reply.Say("Changed a.txt; its check passed."),
                };
            }
            if (start.StartsWith("Carry out step 3 ", StringComparison.Ordinal))
            {
                return results == 0 ? Reply.Call(("write_file", """{"path":"b.txt","content":"TWO\n"}""")) : Reply.Say("Changed b.txt.");
            }
            return start == "go" && results == 0
                ? Reply.Call(("plan", """{"title":"Three at once","steps":[{"title":"Find the parser"},{"title":"Capitalise a","files":["a.txt"],"check":"true"},{"title":"Capitalise b","files":["b.txt"],"check":"false"}]}"""))
                : start == "go" && results == 1 ? Reply.Call(("run_steps", """{"steps":[1,2,3]}""")) : Reply.Say("ok");
        };
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        File.WriteAllText(Path.Combine(h.Work, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(h.Work, "b.txt"), "two\n");
        Git(h.Work, "init", "-q");
        Git(h.Work, "-c", "user.name=t", "-c", "user.email=t@example.test", "add", "-A");
        Git(h.Work, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-qm", "start");
        Assert.Equal(0, await h.Run("go\n/plan\n/exit\n", "chat", "--mode", "auto-edit"));

        // Step 2's change merged; step 3's check failed, so its change stays in its worktree.
        Assert.Equal("ONE\n", File.ReadAllText(Path.Combine(h.Work, "a.txt")));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(h.Work, "b.txt")));
        var report = FakeGateway.History(_gateway.Requests.Last(r => Started(r) == "go" && Results(r) == 2)).Last(m => m["role"]!.GetValue<string>() == "tool")["content"]!.ToString();
        Assert.Contains("Step 1 (Find the parser): done. Found out by a sub-agent: its report is below.\nThe parser is in a.txt, line 1.", report, StringComparison.Ordinal);
        Assert.Contains("Step 2 (Capitalise a): done. Changed in its worktree, its check `true` passed, and merged into the project.", report, StringComparison.Ordinal);
        Assert.Contains("Step 3 (Capitalise b): blocked. Its check `false` failed in its worktree", report, StringComparison.Ordinal);
        // The worker ran no command but its check: rm -rf was declined.
        var worker = _gateway.Requests.Last(r => Started(r).StartsWith("Carry out step 2 ", StringComparison.Ordinal));
        Assert.Contains(FakeGateway.History(worker), m => m["role"]!.GetValue<string>() == "tool" && m["content"]!.ToString().Contains("needs the person's approval", StringComparison.Ordinal) || m["content"]!.ToString().Contains("declined", StringComparison.Ordinal));
        Assert.Contains("☒ 2. Capitalise a", h.Out, StringComparison.Ordinal);
        Assert.Contains("⊘ 3. Capitalise b", h.Out, StringComparison.Ordinal);
        // Step 2's worktree is gone; step 3's is kept for the person.
        var trees = Git(h.Work, "worktree", "list");
        Assert.Contains("step-3-", trees, StringComparison.Ordinal);
        Assert.DoesNotContain("step-2-", trees, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Steps_that_wait_for_one_not_done_or_change_files_in_ask_mode_are_not_run_at_once()
    {
        _gateway.Answer = req => (Started(req), Results(req)) switch
        {
            ("go", 0) => Reply.Call(("plan", """{"title":"T","steps":[{"title":"A","files":["a.txt"]},{"title":"B","files":["b.txt"],"depends_on":[1]},{"title":"C","files":["c.txt"]}]}""")),
            ("go", 1) => Reply.Call(("run_steps", """{"steps":[1,2]}""")),
            ("go", 2) => Reply.Call(("run_steps", """{"steps":[1,3]}""")),
            _ => Reply.Say("ok"),
        };
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("go\n/exit\n", "chat"));
        var said = FakeGateway.History(_gateway.Requests[^1]).Where(m => m["role"]!.GetValue<string>() == "tool").Select(m => m["content"]!.ToString()).ToList();
        Assert.Contains(said, s => s.Contains("Step 2 waits for step 1, which is not done", StringComparison.Ordinal));
        Assert.Contains(said, s => s.Contains("run at once only in auto-edit or yolo mode", StringComparison.Ordinal));
    }
}
