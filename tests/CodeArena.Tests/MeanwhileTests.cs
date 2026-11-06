using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// "Send messages without stopping the chat, like Claude Code": what the person sends while a turn works (the IDE's box, a line
/// typed at the terminal) goes into the turn at its next step, after its tool calls, as their message; the turn goes on, and the
/// chat in Arena gets it in its place. The model here waits in its first step until the test lets it go.
/// </summary>
public sealed class MeanwhileTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _go = new();

    public void Dispose()
    {
        _go.Set();
        _gateway.Dispose();
        _mcp.Dispose();
        _go.Dispose();
    }

    /// <summary>The first step waits, then lists the folder; the next answers what the person said last.</summary>
    private void WorksOneStep() =>
        _gateway.Answer = req =>
        {
            if (FakeGateway.HasToolResults(req))
            {
                return Reply.Say("Done: " + FakeGateway.History(req).Last()!["content"]);
            }
            _asked.TrySetResult();
            _go.Wait(Deadline);
            return Reply.Call(("list_dir", """{"path":"."}"""));
        };

    private static async Task Within(Func<bool> condition)
    {
        var deadline = Task.Delay(Deadline);
        while (!condition())
        {
            Assert.True(await Task.WhenAny(deadline, Task.Delay(20)) != deadline, "not within the deadline");
        }
    }

    private static string Role(JsonNode? m) => m!["role"]!.GetValue<string>();

    private static async Task<HttpResponseMessage> SendAsync(WebRun web, HttpMethod method, string path, JsonObject body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        return await web.Http.SendAsync(request);
    }

    [Fact]
    public async Task The_IDE_takes_a_message_sent_while_a_step_runs_in_at_the_next_step_and_the_turn_goes_on()
    {
        WorksOneStep();
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        using var turn = await web.SendAsync("look around");
        await _asked.Task.WaitAsync(Deadline);
        // Sent while the model works: it waits, with an id, to be changed or taken out until it goes.
        var queued = await web.PostAsync("/api/queue", new JsonObject { ["text"] = "also read the notes" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var id = JsonNode.Parse(await queued.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        var dropped = JsonNode.Parse(await (await web.PostAsync("/api/queue", new JsonObject { ["text"] = "never mind" })).Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(web, HttpMethod.Delete, $"/api/queue/{dropped}", new JsonObject())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(web, HttpMethod.Patch, $"/api/queue/{id}", new JsonObject { ["text"] = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(web, HttpMethod.Patch, $"/api/queue/{id}", new JsonObject { ["text"] = "also read the README" })).StatusCode);
        var waiting = (await web.GetJsonAsync("/api/state"))["queued"]!.AsArray();
        Assert.Equal([(id, "also read the README")], waiting.Select(q => (q!["id"]!.GetValue<string>(), q["text"]!.GetValue<string>())));

        _go.Set();
        var events = await turn.RestAsync().WaitAsync(Deadline);
        var types = events.Select(e => e["type"]!.GetValue<string>()).ToList();
        // One turn: the step's result, the message taken in, the next step, done; nothing stopped, no second question.
        Assert.DoesNotContain("stopped", types);
        Assert.Single(types, t => t == "question");
        Assert.Single(types, t => t == "done");
        var tookIn = events.Single(e => e["type"]!.GetValue<string>() == "took_in");
        Assert.True(types.IndexOf("tool_result") < types.IndexOf("took_in") && types.IndexOf("took_in") < types.LastIndexOf("assistant"));
        Assert.Equal("also read the README", tookIn["content"]!.GetValue<string>());
        Assert.Equal(id, tookIn["queuedId"]!.GetValue<string>());

        // The model read it after the step's result, in the same turn.
        Assert.Equal(2, _gateway.Requests.Count);
        var read = FakeGateway.History(_gateway.Requests[1]);
        Assert.Equal(["user", "assistant", "tool", "user"], read.Select(Role));
        Assert.Equal("also read the README", read[^1]!["content"]!.GetValue<string>());

        // The session has it in its place, shown as sent while it worked; nothing waits, and it cannot be changed any more.
        var messages = (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "tool", "user", "assistant"], messages.Select(Role));
        Assert.Equal(tookIn["id"]!.GetValue<string>(), messages[3]!["id"]!.GetValue<string>());
        Assert.True(messages[3]!["details"]!["takenIn"]!.GetValue<bool>());
        Assert.Equal("Done: also read the README", messages[4]!["content"]!.GetValue<string>());
        Assert.Empty((await web.GetJsonAsync("/api/state"))["queued"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(web, HttpMethod.Patch, $"/api/queue/{id}", new JsonObject { ["text"] = "too late" })).StatusCode);

        // Arena's chat gets it in order, after the step's result, marked as sent meanwhile.
        await Within(() => _mcp.Chats.All.SingleOrDefault()?.Messages.Count == 5);
        var synced = _mcp.Chats.All.Single().Messages;
        Assert.Equal(["user", "assistant", "tool", "user", "assistant"], synced.Select(Role));
        Assert.Equal("also read the README", synced[3]["content"]!.GetValue<string>());
        Assert.Equal(Agent.MeanwhileKind, synced[3]["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task What_the_person_queues_in_the_web_chat_while_the_turn_works_is_read_at_its_next_step_and_synced_back_in_its_place()
    {
        using var h = new Harness(_gateway, _mcp);
        var web = _mcp.Chats.Add("Fix the cart", new JsonObject { ["role"] = "user", ["content"] = "Fix the cart" }, new JsonObject { ["role"] = "assistant", ["content"] = "Which part?" });
        _gateway.Answer = req =>
        {
            if (FakeGateway.HasToolResults(req))
            {
                return Reply.Say("Done: " + FakeGateway.History(req).Last()!["content"]);
            }
            // Written on the web while this step runs (the turn holds the chat there).
            web.Queued.Add("and keep the old total");
            return Reply.Call(("list_dir", """{"path":"."}"""));
        };
        Assert.Equal(0, await h.Run("", "-p", "the total", "--web", web.Id));

        Assert.Equal(2, _gateway.Requests.Count);
        var read = FakeGateway.History(_gateway.Requests[1]);
        Assert.Equal(["user", "assistant", "user", "assistant", "tool", "user"], read.Select(Role));
        Assert.Equal("and keep the old total", read[^1]!["content"]!.GetValue<string>());
        Assert.Contains("Done: and keep the old total", h.Out);
        Assert.Empty(web.Queued);
        // Back on the web in its place: after the step's result, before the answer, as sent meanwhile.
        var synced = web.Messages;
        Assert.Equal(["user", "assistant", "user", "assistant", "tool", "user", "assistant"], synced.Select(Role));
        Assert.Equal(Agent.MeanwhileKind, synced[5]["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_line_typed_at_the_terminal_while_a_step_runs_is_read_at_the_next_step_and_up_takes_one_back()
    {
        WorksOneStep();
        using var h = new Harness(_gateway, _mcp);
        using var terminal = new SharedTerminal();
        var output = new LockedWriter();
        var env = new CliEnv
        {
            In = terminal, Out = output, Err = output, Env = k => h.Env.GetValueOrDefault(k), Cwd = h.Work, Paths = h.Paths, InTerminal = true, Keys = terminal,
        };
        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        try
        {
            terminal.Type("look around\n");
            await _asked.Task.WaitAsync(Deadline);
            // Typed while it works: the first is kept for the next step; the second is taken back with ↑ before it goes.
            terminal.Type("use the tests folder\n");
            await Within(() => output.ToString().Contains("Read at the model's next step: use the tests folder"));
            terminal.Type("wrong one\n");
            await Within(() => output.ToString().Contains("Read at the model's next step: wrong one"));
            terminal.Up();
            await Within(() => output.ToString().Contains("Taken back: wrong one"));
            _go.Set();
            await Within(() => output.ToString().Contains("Done: use the tests folder"));
            // One turn of two steps: the line went into it after the step's result, as the person's.
            Assert.Equal(2, _gateway.Requests.Count);
            var read = FakeGateway.History(_gateway.Requests[1]);
            Assert.Equal(["user", "assistant", "tool", "user"], read.Select(Role));
            Assert.Equal("use the tests folder", read[^1]!["content"]!.GetValue<string>());
            Assert.Contains("› use the tests folder", output.ToString());
            // The line taken back waits on the prompt: Enter sends it as the next message.
            terminal.Type("\n");
            await Within(() => _gateway.Requests.Count == 3);
            Assert.Equal("wrong one", FakeGateway.History(_gateway.Requests[2]).Last()!["content"]!.GetValue<string>());
        }
        finally
        {
            _go.Set();
            terminal.Type("/exit\n");
            terminal.End();
            Assert.True(await Task.WhenAny(run, Task.Delay(Deadline)) == run, "code-arena chat did not end");
        }
    }
}
