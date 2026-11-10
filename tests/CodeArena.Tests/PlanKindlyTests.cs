using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// The plan tool takes a call as models write it: a plan written whole with its steps numbered, other agents' status words and
/// field names, the steps as a string, a step begun before the last one was closed, and a check run inside a longer command.
/// </summary>
public sealed class PlanKindlyTests
{
    private static readonly Func<string, bool> Commands = check => check.Split(' ')[0] is "make" or "dotnet";

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void A_plan_written_whole_with_numbered_steps_and_other_agents_words_is_taken_and_said_back()
    {
        var plan = WorkPlan.None.Apply(Args("""
            {"title":"Rename","steps":[
              {"id":1,"description":"Find the callers","status":"completed"},
              {"step":"2","content":"Rename it","status":"in_progress","dependsOn":[1],"check":"make test"},
              {"number":3,"title":"Docs","status":"pending"}]}
            """), Commands, out var mended);
        Assert.Equal(["1 done Find the callers", "2 doing Rename it", "3 todo Docs"], plan.Steps.Select(s => $"{s.Number} {s.Status} {s.Title}"));
        Assert.Equal([1], plan.Steps[1].DependsOn);
        Assert.Empty(mended);

        // A number past the plan's with no title is still no step: said so, with the steps it has.
        var none = Assert.Throws<PlanError>(() => plan.Apply(Args("""{"steps":[{"number":9,"status":"done"}]}"""), Commands));
        Assert.Contains("its steps are 1, 2, 3", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_steps_as_a_string_are_read_and_a_step_begun_while_another_was_doing_sends_that_one_back_to_wait()
    {
        var plan = WorkPlan.None.Apply(Args("""{"title":"T","steps":"[{\"title\":\"One\",\"status\":\"doing\"},{\"title\":\"Two\"}]"}"""), Commands, out var mended);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Contains(mended, m => m.Contains("string of JSON", StringComparison.Ordinal));

        plan = plan.Apply(Args("""{"steps":[{"number":2,"status":"doing"}]}"""), Commands, out mended);
        Assert.Equal(["todo", "doing"], plan.Steps.Select(s => s.Status));
        Assert.Contains(mended, m => m.StartsWith("step 1 was doing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_check_run_inside_a_longer_command_counts_for_its_step()
    {
        var plan = WorkPlan.None.Apply(Args("""{"title":"T","steps":[{"title":"Test","check":"dotnet test","status":"doing"}]}"""), Commands);
        plan = plan.Ran("cd src && dotnet test 2>&1", true, "Passed!", DateTimeOffset.UtcNow);
        Assert.Equal("passed", plan.Steps[0].CheckResult);
        Assert.Equal("done", plan.Apply(Args("""{"steps":[{"number":1,"status":"done"}]}"""), Commands).Steps[0].Status);
        // Another command that only begins the same is not it.
        Assert.False(WorkPlan.Runs("dotnet test-other", "dotnet test"));
        Assert.True(WorkPlan.Runs("make lint; make test | tail -5", "make test"));
    }
}
