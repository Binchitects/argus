using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Plan mode, finished: the agent proposes its plan, the person approves it, changes a step, asks for another way or rejects it.</summary>
public sealed class PlanReviewTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    private const string Proposal = """
        {"title":"Fix the parser","goal":"empty frames parse","steps":[{"title":"Read parse.c"},{"title":"Fix the empty case","check":"make test","files":["src/parse.c"],"depends_on":[1]}]}
        """;

    public PlanReviewTests() => _gateway.Answer = req =>
    {
        // The person asks (or asks for a step changed): the model proposes its plan; anything else it answers in words.
        var asked = req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" ? FakeGateway.Last(req) : "";
        return asked == "plan the fix" || asked.StartsWith("Change step", StringComparison.Ordinal) ? Reply.Call(("plan", Proposal)) : Reply.Say("ok");
    };

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static List<string> Asked(FakeGateway g) => [.. g.Requests.Select(FakeGateway.Last)];

    [Fact]
    public async Task A_plan_made_in_plan_mode_is_proposed_and_one_made_otherwise_is_not()
    {
        var workspace = new Workspace(Path.GetTempPath(), []);
        var ui = new Ui(new StringReader(""), TextWriter.Null, TextWriter.Null, color: false, canAsk: false);
        var planning = true;
        var context = new ToolContext { Workspace = workspace, Ui = ui, Planning = () => planning };
        var made = await LocalTools.All().Single(t => t.Name == "plan").Run(JsonNode.Parse(Proposal)!.AsObject(), context, CancellationToken.None);
        Assert.Equal(WorkPlan.Proposed, context.Plan.Review);
        Assert.StartsWith("Plan: Fix the parser (proposed: the person reviews it before it is carried out)", made.Text, StringComparison.Ordinal);
        planning = false;
        context.Plan = context.Plan with { Review = WorkPlan.Approved };
        await LocalTools.All().Single(t => t.Name == "plan").Run(JsonNode.Parse("""{"steps":[{"number":1,"status":"doing"}]}""")!.AsObject(), context, CancellationToken.None);
        Assert.Equal(WorkPlan.Approved, context.Plan.Review);
    }

    [Fact]
    public async Task Approved_in_the_terminal_the_mode_lets_edits_run_and_the_agent_is_told_to_carry_it_out_step_by_step()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("plan the fix\n/plan approve\n/mode\n/exit\n", "chat", "--mode", "plan"));
        Assert.Contains("Review the plan: /plan approve", h.Out, StringComparison.Ordinal);
        Assert.Contains("Mode: auto-edit", h.Out, StringComparison.Ordinal);
        var approved = Asked(_gateway).Single(a => a.StartsWith("I approve the plan.", StringComparison.Ordinal));
        Assert.Contains("run its check when it is done, mark it done only once the check passed", approved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_changed_is_proposed_again_and_a_rejected_plan_is_dropped_and_nothing_waits_for_review()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("plan the fix\n/plan change 2 use a guard clause\n/plan reject too risky\n/plan reject\n/plan\n/exit\n", "chat", "--mode", "plan"));
        Assert.Contains("Change step 2 of the plan (Fix the empty case): use a guard clause\n\nThen propose the plan again.", Asked(_gateway));
        // The changed plan was proposed again (the review offered twice), then rejected: the agent is told, and the plan goes.
        Assert.Equal(2, h.Out.Split("Review the plan:").Length - 1);
        Assert.Contains("I reject the plan: too risky. Do not carry it out; I will say what to do next.", Asked(_gateway));
        Assert.Contains("There is no plan waiting for its review", h.Err, StringComparison.Ordinal);
        Assert.Contains("(no plan)", h.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposed_plan_is_read_again_with_fresh_eyes_once_and_the_agent_folds_in_what_it_found()
    {
        var readings = 0;
        _gateway.Answer = req =>
        {
            var last = FakeGateway.Last(req);
            if (last.StartsWith("Read this plan with fresh eyes", StringComparison.Ordinal))
            {
                // The second reading: a sub-agent, in a context of its own (no tools of the main agent's history).
                readings++;
                Assert.Contains("What the person asked:\nplan the fix", last, StringComparison.Ordinal);
                Assert.DoesNotContain(req["messages"]!.AsArray(), m => m!["role"]!.GetValue<string>() == "tool");
                return Reply.Say("- Step 2's check does not cover an empty frame: add a test for it.");
            }
            if (last.StartsWith("(A second reading of your plan", StringComparison.Ordinal))
            {
                return Reply.Call(("plan", """{"steps":[{"number":2,"check":"make test TEST=empty_frame"}]}"""));
            }
            return req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" && last == "plan the fix" ? Reply.Call(("plan", Proposal)) : Reply.Say("Folded in the empty-frame test.");
        };
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("plan the fix\n/exit\n", "chat", "--mode", "plan"));
        Assert.Equal(1, readings);
        Assert.Contains("The second reading:\n- Step 2's check does not cover an empty frame", h.Out, StringComparison.Ordinal);
        var note = Asked(_gateway).Single(a => a.StartsWith("(A second reading of your plan", StringComparison.Ordinal));
        Assert.Contains("Fold in what holds: change the plan with the plan tool.", note, StringComparison.Ordinal);
        // The revised plan is the one reviewed, with what the reading said kept on it; it is not read a second time.
        Assert.Contains("make test TEST=empty_frame", h.Out, StringComparison.Ordinal);
        Assert.Contains("Review the plan:", h.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_the_second_reading_finds_whole_goes_to_review_as_it_is_and_the_reading_can_be_turned_off()
    {
        _gateway.Answer = req => FakeGateway.Last(req).StartsWith("Read this plan with fresh eyes", StringComparison.Ordinal) ? Reply.Say("The plan holds.")
            : req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" && FakeGateway.Last(req) == "plan the fix" ? Reply.Call(("plan", Proposal)) : Reply.Say("ok");
        using (var h = new Harness(_gateway, _mcp))
        {
            Assert.Equal(0, await h.Run("plan the fix\n/exit\n", "chat", "--mode", "plan"));
            Assert.Contains("The second reading found nothing to add.", h.Out, StringComparison.Ordinal);
            Assert.DoesNotContain(Asked(_gateway), a => a.StartsWith("(A second reading", StringComparison.Ordinal));
        }
        var before = _gateway.Requests.Count;
        using (var h = new Harness(_gateway, _mcp, c => c["planCritique"] = false))
        {
            Assert.Equal(0, await h.Run("plan the fix\n/exit\n", "chat", "--mode", "plan"));
            Assert.DoesNotContain(Asked(_gateway).Skip(before), a => a.StartsWith("Read this plan with fresh eyes", StringComparison.Ordinal));
            Assert.Contains("Review the plan:", h.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_proposed_plan_is_grounded_in_the_code_its_symbols_callers_in_every_repository_and_the_tests_that_name_them()
    {
        using var argus = new FakeMcp
        {
            ToolList = [new JsonObject { ["name"] = "change_impact", ["description"] = "Argus's change_impact.", ["inputSchema"] = new JsonObject { ["type"] = "object" }, ["annotations"] = new JsonObject { ["readOnlyHint"] = true } }],
            CallContent = [new JsonObject
            {
                ["type"] = "text",
                ["text"] = """
                    {"repo":"shop/codec","dependents":1,"references":[
                      {"repo":"shop/codec","path":"src/parse.c","line":10,"is_definition":true},
                      {"repo":"shop/codec","path":"tests/parse_test.c","line":4},
                      {"repo":"shop/player","path":"src/play.c","line":88}]}
                    """,
            }],
        };
        _gateway.Answer = req => req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user" && FakeGateway.Last(req) == "plan the fix"
            ? Reply.Call(("plan", """{"title":"Fix parse","steps":[{"title":"Change parse","symbols":["parse"],"files":["src/parse.c"],"check":"make test"}]}"""))
            : Reply.Say("ok");
        using var h = new Harness(_gateway, _mcp, c =>
        {
            c["argusUrl"] = argus.Url;
            c["planCritique"] = false;
        });
        Git(h.Work, "init", "-q");
        Git(h.Work, "remote", "add", "origin", "https://gitlab.example/shop/codec.git");
        Assert.Equal(0, await h.Run("plan the fix\n/plan\n/exit\n", "chat", "--mode", "plan"));

        var call = Assert.Single(argus.ToolCalls, c => c["name"]!.GetValue<string>() == "change_impact");
        Assert.Equal(("shop/codec", "parse"), (call["arguments"]!["repo"]!.GetValue<string>(), call["arguments"]!["symbol"]!.GetValue<string>()));
        Assert.Contains("impact: parse: 2 lines use it, in shop/codec (1), shop/player (1); 1 repository depends on this one; tests that name it: tests/parse_test.c.", h.Out, StringComparison.Ordinal);
    }

    private static void Git(string dir, params string[] args)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
    }

    [Fact]
    public async Task The_IDE_reviews_the_proposed_plan_and_its_answer_runs_as_a_turn()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h, "--mode", "plan");
        await (await web.SendAsync("plan the fix")).RestAsync();
        var plan = await web.GetJsonAsync("/api/plan");
        Assert.Equal(("proposed", "plan"), (plan["review"]!.GetValue<string>(), plan["mode"]!.GetValue<string>()));

        var refused = await web.PostAsync("/api/plan/review", new JsonObject { ["action"] = "change", ["step"] = 9, ["text"] = "x" });
        Assert.Equal(400, (int)refused.StatusCode);
        await (await web.StreamAsync(HttpMethod.Post, "/api/plan/review", new JsonObject { ["action"] = "approve-ask" })).RestAsync();
        plan = await web.GetJsonAsync("/api/plan");
        Assert.Equal(("approved", "ask"), (plan["review"]!.GetValue<string>(), plan["mode"]!.GetValue<string>()));
        Assert.StartsWith("I approve the plan.", Asked(_gateway)[^1], StringComparison.Ordinal);
    }
}
