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
