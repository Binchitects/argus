namespace CodeArena.Tests;

/// <summary>The sub-agents running at once: as many as Arena says for the person, the rest waiting their turn.</summary>
public sealed class TaskBoardLimitTests
{
    [Fact]
    public async Task At_one_at_once_a_second_sub_agent_waits_for_the_first_and_a_larger_limit_lets_it_start()
    {
        var board = new TaskBoard { AtOnce = 1 };
        var first = board.Add("agent", "one", "p", null, null, "m", writes: false, []);
        var second = board.Add("agent", "two", "p", null, null, "m", writes: false, []);
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var a = board.RunAsync(first, async _ => { started.SetResult(); await release.Task; return "a"; }, CancellationToken.None);
        await started.Task;
        var secondStarted = new TaskCompletionSource();
        var b = board.RunAsync(second, _ => { secondStarted.SetResult(); return Task.FromResult("b"); }, CancellationToken.None);
        // Held: only one at once.
        Assert.False(await Task.WhenAny(secondStarted.Task, Task.Delay(300)) == secondStarted.Task);
        Assert.Equal(AgentTask.Queued, second.Status);
        // Arena raises it: the second starts while the first still runs.
        board.AtOnce = 2;
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("b", await b);
        release.SetResult();
        Assert.Equal("a", await a);
    }

    [Fact]
    public void The_task_tool_tells_the_model_how_many_sub_agents_run_at_once_as_the_limit_changes()
    {
        var board = new TaskBoard();
        var task = LocalTools.SubAgentTool(() => board.AtOnce);
        var steps = LocalTools.RunStepsTool(() => board.AtOnce);
        static string Said(ToolDef t) => t.Spec()["function"]!["description"]!.GetValue<string>();
        Assert.Contains($"Here {TaskBoard.MostAtOnce} sub-agents run at once", Said(task), StringComparison.Ordinal);
        Assert.Contains("up to 4 task calls in one reply", Said(task), StringComparison.Ordinal);
        // Arena's admins give the person 12: the next request says so.
        board.AtOnce = 12;
        Assert.Contains("Here 12 sub-agents run at once", Said(task), StringComparison.Ordinal);
        Assert.Contains("Here 2 to 12 steps at once", Said(steps), StringComparison.Ordinal);
        board.AtOnce = 1;
        Assert.Contains("one sub-agent runs at a time", Said(task), StringComparison.Ordinal);
        Assert.Contains("Here 2 to 2 steps at once", Said(steps), StringComparison.Ordinal);
        // The person's own words for the tool keep the limit after them.
        var mine = task.Copy();
        mine.Description = "Delegate.";
        Assert.StartsWith("Delegate. Here one sub-agent", Said(mine), StringComparison.Ordinal);
    }
}
