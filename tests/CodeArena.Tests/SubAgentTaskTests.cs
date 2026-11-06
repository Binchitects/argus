using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Sub-agents on the session's board: each in a context of its own, those that only read side by side, those that change
/// files one at a time and never asking; stopped from the Tasks panel; the person's own in the background; as an assistant.
/// </summary>
public sealed class SubAgentTaskTests : IDisposable
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

    /// <summary>The task a sub-agent's request is for: its first message after the system prompt.</summary>
    private static string TaskOf(JsonObject req) => req["messages"]![1]!["content"]!.GetValue<string>();

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
    public async Task Sub_agents_asked_at_once_run_side_by_side_each_in_a_context_of_its_own()
    {
        using var h = new Harness(_gateway, _mcp);
        // Each sub-agent's first request waits for the other's: they only both get there if they run at once.
        using var both = new CountdownEvent(2);
        var met = 0;
        _gateway.Answer = req =>
        {
            if (IsSub(req))
            {
                if (!FakeGateway.HasToolResults(req) && TaskOf(req).StartsWith("Find", StringComparison.Ordinal))
                {
                    both.Signal();
                    if (both.Wait(TimeSpan.FromSeconds(10)))
                    {
                        Interlocked.Increment(ref met);
                    }
                }
                return Reply.Say($"Report on: {TaskOf(req)}");
            }
            return FakeGateway.HasToolResults(req)
                ? Reply.Say(string.Join(" | ", req["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>())))
                : Reply.Call(("task", """{"description":"the parser","prompt":"Find the parser."}"""), ("task", """{"description":"the lexer","prompt":"Find the lexer."}"""));
        };

        Assert.Equal(0, await h.Run("", "-p", "where are the parser and the lexer?"));

        Assert.Equal(2, met);
        Assert.Equal("Report on: Find the parser. | Report on: Find the lexer.", h.Out.Trim());
        var subs = _gateway.Requests.Where(IsSub).ToList();
        Assert.Equal(2, subs.Count);
        // Each saw its own task and nothing of the other's, nor of the conversation.
        Assert.All(subs, r => Assert.Equal(2, r["messages"]!.AsArray().Count));
        Assert.Equal(["Find the lexer.", "Find the parser."], subs.Select(TaskOf).Order());
        // The terminal showed one line for each, not their steps.
        Assert.Equal(2, h.Err.Split('\n').Count(l => l.Contains("● task", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_sub_agent_given_tools_that_change_files_uses_them_never_asks_and_such_sub_agents_run_one_at_a_time()
    {
        using var h = new Harness(_gateway, _mcp);
        var order = new List<string>();
        _gateway.Answer = req =>
        {
            if (IsSub(req))
            {
                var task = TaskOf(req);
                lock (order)
                {
                    order.Add(task);
                }
                if (FakeGateway.HasToolResults(req))
                {
                    return Reply.Say($"{task}: {FakeGateway.Last(req)}");
                }
                // A moment in between, so a second one running at once would come in between.
                Thread.Sleep(150);
                return Reply.Call(("write_file", task == "Write a." ? """{"path":"a.txt","content":"A\n"}""" : """{"path":"b.txt","content":"B\n"}"""));
            }
            return FakeGateway.HasToolResults(req)
                ? Reply.Say("done")
                : Reply.Call(("task", """{"description":"write a","prompt":"Write a.","tools":["read_file","write_file"]}"""),
                    ("task", """{"description":"write b","prompt":"Write b.","tools":["read_file","write_file"]}"""));
        };

        Assert.Equal(0, await h.Run("", "-p", "write both", "--mode", "auto-edit"));

        Assert.Equal("A\n", File.ReadAllText(Path.Combine(h.Work, "a.txt")));
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(h.Work, "b.txt")));
        // One after the other: each one's two requests together.
        Assert.Equal(4, order.Count);
        Assert.Equal(order[0], order[1]);
        Assert.Equal(order[2], order[3]);
        Assert.NotEqual(order[0], order[2]);
        var sub = _gateway.Requests.First(IsSub);
        Assert.Equal(["read_file", "write_file"], FakeGateway.ToolNames(sub).Order());
        Assert.Contains("which may edit files and run commands", FakeGateway.System(sub));

        // In ask mode the same sub-agent may not write: it would have to ask, and nobody watches it.
        File.Delete(Path.Combine(h.Work, "a.txt"));
        File.Delete(Path.Combine(h.Work, "b.txt"));
        Assert.Equal(0, await h.Run("", "-p", "write both", "--mode", "ask"));
        Assert.False(File.Exists(Path.Combine(h.Work, "a.txt")));
        var refused = _gateway.Requests.Where(IsSub).Last(FakeGateway.HasToolResults);
        Assert.Contains("A sub-agent does not ask the person: in this mode write_file needs their approval", FakeGateway.Last(refused));
    }

    [Fact]
    public async Task A_sub_agent_named_tools_never_gets_those_of_the_main_conversation_only()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => IsSub(req) ? Reply.Say("ok")
            : FakeGateway.HasToolResults(req) ? Reply.Say("done")
            : Reply.Call(("task", """{"description":"all of it","prompt":"Do it.","tools":["*"]}"""));
        Assert.Equal(0, await h.Run("", "-p", "go", "--mode", "yolo"));
        var tools = FakeGateway.ToolNames(_gateway.Requests.First(IsSub));
        Assert.Contains("edit_file", tools);
        Assert.Contains("run_shell", tools);
        Assert.DoesNotContain("task", tools);
        Assert.DoesNotContain("plan", tools);
        Assert.DoesNotContain("run_steps", tools);
    }

    [Fact]
    public async Task The_chat_hears_only_a_sub_agents_start_and_end_while_the_Tasks_panel_has_its_steps_and_stops_it()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("src/a.cs", "class Parser { }\n");
        var stopped = false;
        _gateway.Answer = req =>
        {
            if (IsSub(req))
            {
                // It reads, then goes on and on until it is stopped.
                return FakeGateway.HasToolResults(req) ? Reply.Say("still looking ") with { Endless = true } : Reply.Call(("read_file", """{"path":"src/a.cs"}"""));
            }
            if (FakeGateway.HasToolResults(req))
            {
                stopped = FakeGateway.Last(req).Contains("The person stopped this sub-agent", StringComparison.Ordinal);
                return Reply.Say("Carried on without it.");
            }
            return Reply.Call(("task", """{"description":"find the parser","prompt":"Find the parser."}"""));
        };
        await using var web = await WebRun.StartAsync(h);
        using var stream = await web.SendAsync("where is it?");
        string? id = null;
        await Until(async () =>
        {
            var tasks = (await web.GetJsonAsync("/api/agent-tasks"))["tasks"]!.AsArray();
            id = tasks.FirstOrDefault(t => t!["status"]!.ToString() == "running" && t["calls"]!.GetValue<int>() >= 1)?["id"]?.ToString();
            return id is not null;
        }, "the sub-agent is at work on the board");
        var state = await web.GetJsonAsync("/api/state");
        Assert.Equal(1, state["tasksRunning"]!.GetValue<int>());

        // Its own transcript: the read and what it found.
        var task = await web.GetJsonAsync($"/api/agent-tasks/{id}");
        Assert.Equal(("find the parser", "Find the parser.", "subagent"), (task["title"]!.ToString(), task["prompt"]!.ToString(), task["kind"]!.ToString()));
        var step = task["work"]!["steps"]!.AsArray().Single()!;
        Assert.Equal("read_file", step["name"]!.ToString());
        Assert.Contains("class Parser", step["result"]!.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync($"/api/agent-tasks/{id}/stop", new JsonObject())).StatusCode);
        var events = await stream.RestAsync();
        Assert.True(stopped);
        Assert.Equal("done", events[^1].Str("type"));
        // The chat heard that it started and how it ended: never its steps.
        var agents = events.Where(e => e.Str("type") == "agent").ToList();
        Assert.Equal(["start", "done"], agents.Select(e => e.Str("event")));
        Assert.Equal(id, agents[0].Str("task"));
        var ended = await web.GetJsonAsync($"/api/agent-tasks/{id}");
        Assert.Equal("stopped", ended["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync($"/api/agent-tasks/{id}/stop", new JsonObject())).StatusCode);

        // Kept with the session: opened again, the board has it, found by its call's id as the chat's line asks for it.
        var session = (await web.GetJsonAsync("/api/state"))["session"]!.ToString();
        await web.PostAsync("/api/sessions/new", new JsonObject());
        await web.PostAsync("/api/sessions/resume", new JsonObject { ["id"] = session });
        var kept = await web.GetJsonAsync($"/api/agent-tasks/{agents[0].Str("id")}");
        Assert.Equal(("find the parser", "stopped", true), (kept["title"]!.ToString(), kept["status"]!.ToString(), kept["kept"]!.GetValue<bool>()));
        Assert.Equal("read_file", kept["work"]!["steps"]![0]!["name"]!.ToString());
    }

    [Fact]
    public async Task A_background_task_of_the_persons_runs_while_the_chat_is_idle_and_its_report_reaches_the_next_turn()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => IsSub(req) ? Reply.Say("The build uses make.") : Reply.Say("Thanks.");
        await using var web = await WebRun.StartAsync(h);

        var started = await web.PostAsync("/api/agent-tasks", new JsonObject { ["prompt"] = "How is it built?", ["title"] = "the build" });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var id = JsonNode.Parse(await started.Content.ReadAsStringAsync())!["id"]!.ToString();
        await Until(async () => (await web.GetJsonAsync($"/api/agent-tasks/{id}"))["status"]!.ToString() == "done", "the task is done");
        var task = await web.GetJsonAsync($"/api/agent-tasks/{id}");
        Assert.Equal(("background", "The build uses make."), (task["kind"]!.ToString(), task["report"]!.ToString()));
        Assert.Null(task["call"]);

        await (await web.SendAsync("go on")).RestAsync();
        var main = _gateway.Requests.Last(r => !IsSub(r));
        var note = main["messages"]!.AsArray().Select(m => m!["content"]?.ToString() ?? "").Single(c => c.Contains("background task", StringComparison.Ordinal));
        Assert.Contains($"The person's background task {id} (the build) is done. Its report:\nThe build uses make.", note);
        // Heard once only.
        await (await web.SendAsync("again")).RestAsync();
        Assert.Single(_gateway.Requests.Last(r => !IsSub(r))["messages"]!.AsArray(), m => (m!["content"]?.ToString() ?? "").Contains("background task", StringComparison.Ordinal));

        // A task kept with the session is on the board again when the session is opened again.
        var session = (await web.GetJsonAsync("/api/state"))["session"]!.ToString();
        await web.PostAsync("/api/sessions/new", new JsonObject());
        Assert.Empty((await web.GetJsonAsync("/api/agent-tasks"))["tasks"]!.AsArray());
        await web.PostAsync("/api/sessions/resume", new JsonObject { ["id"] = session });
        var kept = (await web.GetJsonAsync("/api/agent-tasks"))["tasks"]!.AsArray().Single()!;
        Assert.Equal(("the build", "done", true, "background"), (kept["title"]!.ToString(), kept["status"]!.ToString(), kept["kept"]!.GetValue<bool>(), kept["kind"]!.ToString()));
    }

    [Fact]
    public async Task A_task_the_person_starts_is_refused_without_words_or_with_edits_in_plan_mode()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h, "--mode", "plan");
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/agent-tasks", new JsonObject { ["prompt"] = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/agent-tasks", new JsonObject { ["prompt"] = "fix it", ["edits"] = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/agent-tasks/t9")).StatusCode);
    }

    private static JsonArray Assistants() => JsonNode.Parse("""
        [{"id":"a1","name":"Reviewer","description":"Reviews changes","instructions":"Point out risks first.","model":"other-model","thinking":"high","tools":["web"],"skills":["review"],"icon":"code","color":"green"}]
        """)!.AsArray();

    [Fact]
    public async Task A_session_works_as_an_Arena_assistant_and_a_sub_agent_can_too()
    {
        _mcp.Chats.Assistants = Assistants();
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => IsSub(req) ? Reply.Say("No risk found.")
            : FakeGateway.HasToolResults(req) ? Reply.Say("ok")
            : FakeGateway.Last(req) == "review it" ? Reply.Call(("task", """{"description":"a review","prompt":"Review a.cs.","assistant":"Reviewer"}"""))
            : Reply.Say("ok");

        Assert.Equal(0, await h.Run("/assistant\n/assistant reviewer\nhello\nreview it\n/exit\n", "chat"));

        Assert.Contains("1. Reviewer — Reviews changes", h.Out);
        Assert.Contains("Working as the assistant Reviewer", h.Out);
        var main = _gateway.Requests.First(r => !IsSub(r) && FakeGateway.Last(r) == "hello");
        var system = FakeGateway.SystemOnly(main);
        Assert.Contains("You work as the assistant \"Reviewer\" from Arena (Reviews changes). Its instructions:\nPoint out risks first.", system);
        // A sub-agent can be one of them too.
        Assert.Contains("# The person's assistants in Arena", system);
        var sub = _gateway.Requests.First(IsSub);
        Assert.StartsWith("# Assistant\n\nYou work as the assistant \"Reviewer\"", FakeGateway.SystemOnly(sub));

        // Kept with the session: a resumed one works as it did, Arena not asked again.
        _mcp.Chats.Assistants = null;
        var id = Path.GetFileNameWithoutExtension(Directory.GetFiles(h.Paths.SessionsDir).Single());
        Assert.Equal(0, await h.Run("", "-p", "and now?", "--resume", id));
        Assert.Contains("You work as the assistant \"Reviewer\"", FakeGateway.SystemOnly(_gateway.Requests.Last()));
    }

    [Fact]
    public async Task The_IDE_lists_the_assistants_and_puts_one_on_and_off()
    {
        _mcp.Chats.Assistants = Assistants();
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        var listed = await web.GetJsonAsync("/api/assistants");
        Assert.Equal("Reviewer", listed["assistants"]![0]!["name"]!.ToString());
        Assert.Null(listed["current"]);

        var on = JsonNode.Parse(await (await web.PostAsync("/api/assistant", new JsonObject { ["id"] = "a1" })).Content.ReadAsStringAsync())!;
        Assert.StartsWith("Working as the assistant Reviewer", on["said"]!.ToString());
        Assert.Equal("Reviewer", on["state"]!["assistant"]!["name"]!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/assistant", new JsonObject { ["id"] = "nope" })).StatusCode);

        var off = JsonNode.Parse(await (await web.PostAsync("/api/assistant", new JsonObject())).Content.ReadAsStringAsync())!;
        Assert.Null(off["state"]!["assistant"]);
    }
}
