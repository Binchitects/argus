using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The agent's plan goes to Arena with the session's chat: on the Plans board, as it changes, and dropped when cleared.</summary>
public sealed class PlanSyncTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds:0} s: {what}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task The_plan_reaches_the_sessions_chat_in_Arena_after_its_messages_each_time_it_changes_and_is_dropped_when_cleared()
    {
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "plan it" when req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" => Reply.Call(("plan", """
                {"title":"Rename parse","goal":"parse_frame everywhere","steps":[{"title":"Find the callers","status":"doing","check":"grep -rn parse src"},{"title":"Rename","files":["src/a.c"],"depends_on":[1]}]}
                """)),
            "go on" when req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" => Reply.Call(("plan", """{"steps":[{"number":1,"status":"blocked","notes":"src is not there yet"},{"number":2,"status":"doing"}]}""")),
            "start again" when req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" => Reply.Call(("plan", """{"clear":true}""")),
            _ => Reply.Say("ok"),
        };
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        await (await web.SendAsync("plan it")).RestAsync();
        await Until(() => _mcp.Chats.PlansSent().Count == 1, "the plan is in Arena");
        var chat = _mcp.Chats.All.Single();
        var (to, plan) = _mcp.Chats.PlansSent()[0];
        Assert.Equal(chat.Id, to);
        // Sent once the chat has the messages that made it.
        Assert.Contains(chat.Messages, m => m["role"]?.ToString() == "tool");
        Assert.Equal(("Rename parse", "parse_frame everywhere"), (plan!["title"]!.ToString(), plan["goal"]!.ToString()));
        Assert.Equal(Path.GetFileName(h.Work.TrimEnd('/')), plan["place"]!.ToString());
        var steps = plan["steps"]!.AsArray();
        Assert.Equal(("doing", "grep -rn parse src"), (steps[0]!["status"]!.ToString(), steps[0]!["check"]!.ToString()));
        Assert.Equal(["src/a.c"], steps[1]!["files"]!.AsArray().Select(f => f!.ToString()));
        Assert.Equal([1], steps[1]!["dependsOn"]!.AsArray().Select(d => d!.GetValue<int>()));

        await (await web.SendAsync("go on")).RestAsync();
        await Until(() => _mcp.Chats.PlansSent().Count == 2, "the changed plan is in Arena");
        Assert.Equal(["blocked", "doing"], _mcp.Chats.PlansSent()[1].Plan!["steps"]!.AsArray().Select(s => s!["status"]!.ToString()));

        await (await web.SendAsync("start again")).RestAsync();
        await Until(() => _mcp.Chats.PlansSent().Count == 3, "the plan is dropped in Arena");
        Assert.Null(_mcp.Chats.PlansSent()[2].Plan);
        // Nothing sent again while it does not change.
        await (await web.SendAsync("hello")).RestAsync();
        await Task.Delay(500);
        Assert.Equal(3, _mcp.Chats.PlansSent().Count);
    }
}
