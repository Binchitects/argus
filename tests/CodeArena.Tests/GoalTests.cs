using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Goal mode: the agent works turn after turn until the goal's check passes (a command, or a condition a fresh reader looks
/// at), says how it checked, and stops when its budget is spent or it makes no progress; paused, resumed, cancelled; kept
/// with the session and sent to Arena with its chat.
/// </summary>
public sealed class GoalTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static bool IsSub(JsonObject req) => FakeGateway.System(req).Contains("You are a sub-agent", StringComparison.Ordinal);

    /// <summary>The goal turn a main request belongs to (1 is the first), from the message that started it; 0: none.</summary>
    private static int GoalTurn(JsonObject req)
    {
        var asked = req["messages"]!.AsArray().Select(m => m!["content"]?.ToString() ?? "").LastOrDefault(c => c.StartsWith("Goal", StringComparison.Ordinal));
        return asked is null ? 0 : asked.StartsWith("Goal: ", StringComparison.Ordinal) ? 1 : int.Parse(asked["Goal (turn ".Length..asked.IndexOf(')')]);
    }

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task The_agent_keeps_working_until_the_persons_command_check_passes_and_says_how_it_checked()
    {
        using var h = new Harness(_gateway, _mcp);
        // The first turn only looks; the second writes the file the check wants.
        _gateway.Answer = req =>
        {
            var turn = GoalTurn(req);
            if (req["messages"]!.AsArray()[^1]!["role"]!.ToString() == "tool")
            {
                return Reply.Say(turn == 1 ? "I looked around." : "Wrote done.txt.");
            }
            return turn == 1 ? Reply.Call(("list_dir", """{"path":"."}""")) : Reply.Call(("write_file", """{"path":"done.txt","content":"ok\n"}"""));
        };

        Assert.Equal(0, await h.Run("/goal make done.txt --check \"test -f done.txt\"\n/goal\n/exit\n", "chat", "--mode", "auto-edit"));

        Assert.True(File.Exists(Path.Combine(h.Work, "done.txt")));
        Assert.Contains("Goal, turn 1", h.Out);
        Assert.Contains("The goal is not met yet (`test -f done.txt` failed): the agent carries on.", h.Out);
        Assert.Contains("Goal met: `test -f done.txt` succeeded (exit code 0), after 2 turns", h.Out);
        Assert.Contains("Goal (met): make done.txt", h.Out);
        // The second turn was told the check had not passed yet.
        var second = _gateway.Requests.First(r => GoalTurn(r) == 2);
        Assert.Contains("The check did not pass yet:\nExit code 1.", FakeGateway.Last(second));
        // No third turn.
        Assert.DoesNotContain(_gateway.Requests, r => GoalTurn(r) == 3);
    }

    [Fact]
    public async Task Without_a_check_of_the_persons_the_agent_writes_one_down_first_and_it_is_asked_as_a_command()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req =>
        {
            var tools = FakeGateway.ToolNames(req);
            var results = req["messages"]!.AsArray().Count(m => m!["role"]!.ToString() == "tool");
            return results switch
            {
                0 => Reply.Call(("goal_check", """{"command":"test -f done.txt"}""")),
                1 => Reply.Call(("write_file", """{"path":"done.txt","content":"ok\n"}""")),
                _ => Reply.Say("Done."),
            };
        };
        // Yolo: the command check runs without asking; it would ask in auto-edit.
        Assert.Equal(0, await h.Run("/goal make done.txt\n/exit\n", "chat", "--mode", "yolo"));

        var first = _gateway.Requests[0];
        Assert.Contains("goal_check", FakeGateway.ToolNames(first));
        Assert.Contains("First, before any change, write down how it will be checked with goal_check", FakeGateway.Last(first));
        Assert.Contains("Goal met: `test -f done.txt` succeeded (exit code 0), after 1 turn", h.Out);
        // Gone once the goal ended: the requests after it are as they were.
        Assert.Equal(0, await h.Run("", "-p", "hi", "--continue"));
        Assert.DoesNotContain("goal_check", FakeGateway.ToolNames(_gateway.Requests[^1]));

        // In auto-edit the check, a command, asks first: declined, the goal has none.
        File.Delete(Path.Combine(h.Work, "done.txt"));
        Assert.Equal(0, await h.Run("/goal make done.txt --steps 4\nn\n/exit\n", "chat", "--mode", "auto-edit"));
        Assert.Contains("Allow goal_check?", h.Out);
    }

    [Fact]
    public async Task A_condition_is_checked_by_a_fresh_reader()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => IsSub(req)
            ? Reply.Say(File.Exists(Path.Combine(h.Work, "done.txt")) ? "MET\nThe file done.txt is there." : "NOT MET\nNo done.txt yet.")
            : FakeGateway.HasToolResults(req) ? Reply.Say("Written.") : Reply.Call(("write_file", """{"path":"done.txt","content":"ok\n"}"""));

        Assert.Equal(0, await h.Run("/goal make done.txt --until \"a file done.txt exists\"\n/exit\n", "chat", "--mode", "auto-edit"));

        Assert.Contains("Goal met: a fresh reader found that this holds: a file done.txt exists (The file done.txt is there.), after 1 turn", h.Out);
        var reader = _gateway.Requests.Single(IsSub);
        Assert.Contains("a file done.txt exists", reader["messages"]![1]!["content"]!.ToString());
        Assert.DoesNotContain("write_file", FakeGateway.ToolNames(reader));
    }

    [Fact]
    public async Task The_goal_stops_when_its_budget_of_steps_is_spent_and_when_the_agent_does_nothing()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.HasToolResults(req) && req["messages"]!.AsArray()[^1]!["role"]!.ToString() == "tool"
            ? Reply.Say("Not there yet.")
            : Reply.Call(("list_dir", """{"path":"."}"""));
        Assert.Equal(0, await h.Run("/goal make done.txt --check \"test -f done.txt\" --steps 5\n/exit\n", "chat", "--mode", "auto-edit"));
        Assert.Contains("The goal is not met and its 5 steps are used", h.Out);
        // At most a step past the budget: the turn running when it was spent is stopped.
        Assert.InRange(_gateway.Requests.Count, 5, 6);

        // A model that only talks: three turns with nothing done, and the goal stops.
        var before = _gateway.Requests.Count;
        _gateway.Answer = _ => Reply.Say("I will do it soon.");
        Assert.Equal(0, await h.Run("/goal make done.txt --check \"test -f done.txt\"\n/exit\n", "chat", "--mode", "auto-edit"));
        Assert.Contains("The agent ended 3 turns in a row without doing anything while the check still fails: the goal stopped.", h.Out);
        Assert.Equal(3, _gateway.Requests.Count - before);
    }

    [Fact]
    public async Task A_goal_is_refused_in_plan_mode_and_with_a_budget_out_of_bounds()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/goal fix it\n/goal fix it --steps 0\n/exit\n", "chat", "--mode", "plan"));
        Assert.Contains("Plan mode is read-only: switch the mode", h.Out + h.Err);
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task In_the_IDE_the_goal_runs_as_turns_of_its_own_pauses_resumes_is_cancelled_and_reaches_the_chat_in_Arena()
    {
        using var h = new Harness(_gateway, _mcp);
        var hold = new SemaphoreSlim(0);
        _gateway.Answer = req =>
        {
            if (GoalTurn(req) == 0)
            {
                return Reply.Say("Hello.");
            }
            // Each goal turn waits for the test to let it end.
            hold.Wait(TimeSpan.FromSeconds(20));
            return Reply.Say("Working on it.");
        };
        await using var web = await WebRun.StartAsync(h, "--mode", "auto-edit");
        // A chat in Arena first, for the goal to go to.
        await (await web.SendAsync("hi")).RestAsync();

        var set = await web.PostAsync("/api/goal", new JsonObject
        {
            ["text"] = "make done.txt", ["check"] = new JsonObject { ["kind"] = "command", ["text"] = "test -f done.txt" },
            ["budget"] = new JsonObject { ["steps"] = 50, ["minutes"] = 10, ["tokens"] = 1_000_000 },
        });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/goal", new JsonObject { ["text"] = "another" })).StatusCode);
        await Until(async () => (await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>(), "the goal's first turn runs");
        // Paused while its turn runs: the turn ends, and no other starts.
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/goal/pause", new JsonObject())).StatusCode);
        hold.Release();
        await Until(async () => !(await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>(), "the turn ends");
        var goal = (await web.GetJsonAsync("/api/state"))["goal"]!;
        Assert.Equal(("paused", 1), (goal["status"]!.ToString(), goal["turns"]!.GetValue<int>()));
        await Task.Delay(300);
        Assert.Equal(1, _gateway.Requests.Count(r => GoalTurn(r) > 0));

        // Resumed: the next turn, told the check did not pass yet; then cancelled while it runs.
        File.WriteAllText(Path.Combine(h.Work, "unrelated.txt"), "x");
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/goal/resume", new JsonObject())).StatusCode);
        await Until(() => Task.FromResult(_gateway.Requests.Any(r => GoalTurn(r) == 2)), "the goal's second turn");
        using (var cancel = new HttpRequestMessage(HttpMethod.Delete, "/api/goal") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") })
        {
            Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(cancel)).StatusCode);
        }
        hold.Release();
        await Until(async () => !(await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>(), "the cancelled turn ends");
        goal = (await web.GetJsonAsync("/api/goal"))["goal"]!;
        Assert.Equal(("cancelled", "Cancelled by the person."), (goal["status"]!.ToString(), goal["outcome"]!.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/goal/resume", new JsonObject())).StatusCode);

        // Arena has the goal as it went, with the session's chat.
        await Until(() => Task.FromResult(_mcp.Chats.GoalsSent().LastOrDefault().Goal?["status"]?.ToString() == "cancelled"), "the cancelled goal is in Arena");
        var chat = _mcp.Chats.All.Single();
        Assert.All(_mcp.Chats.GoalsSent(), g => Assert.Equal(chat.Id, g.Chat));
        Assert.Contains(_mcp.Chats.GoalsSent(), g => g.Goal?["status"]?.ToString() == "paused");
        Assert.Equal("test -f done.txt", _mcp.Chats.GoalsSent()[^1].Goal!["check"]!["text"]!.ToString());
    }

    [Fact]
    public async Task A_goal_is_kept_with_the_session_with_how_it_ended()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = _ => Reply.Say("Nothing yet.");
        Assert.Equal(0, await h.Run("/goal make done.txt --check \"test -f done.txt\"\n/exit\n", "chat", "--mode", "auto-edit"));
        var id = Path.GetFileNameWithoutExtension(Directory.GetFiles(h.Paths.SessionsDir).Single());
        Assert.Equal(0, await h.Run("/goal\n/exit\n", "chat", "--resume", id, "--mode", "auto-edit"));
        Assert.Contains("Goal (stopped: no progress): make done.txt", h.Out);
        Assert.Contains("Check: `test -f done.txt` succeeds", h.Out);
        Assert.Contains("Used: 3 turns", h.Out);
    }
}
