namespace CodeArena.Tests;

/// <summary>Code Arena improves itself: what a session worked out kept as a skill (the project's, or the person's in Arena), and a nudge after a long turn.</summary>
public sealed class SelfImproveTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private const string Steps = "1. Bump VERSION. 2. Write the changelog's section. 3. Tag vX.Y.Z and push the tag.";

    [Fact]
    public async Task A_procedure_worked_out_is_kept_as_the_projects_skill_and_used_from_the_next_session()
    {
        using var h = new Harness(_gateway, _mcp);
        var step = 0;
        _gateway.Answer = _ => ++step switch
        {
            1 => Reply.Call(("skill_save", $$"""{"name":"release-a-version","description":"When the person asks to release a version","instructions":"{{Steps}}"}""")),
            _ => Reply.Say("kept"),
        };
        Assert.Equal(0, await h.Run("", "-p", "we released 6.9 together; keep how", "--mode", "auto-edit"));
        var file = Path.Combine(h.Work, ".arena", "skills", "release-a-version", "SKILL.md");
        var text = File.ReadAllText(file);
        Assert.StartsWith("---\nname: release-a-version\ndescription: When the person asks to release a version\nlearned: ", text, StringComparison.Ordinal);
        Assert.EndsWith($"---\n\n{Steps}\n", text, StringComparison.Ordinal);

        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Contains("release-a-version", FakeGateway.SystemOnly(_gateway.Requests[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_for_the_person_goes_to_their_skills_in_Arena()
    {
        using var h = new Harness(_gateway, _mcp);
        var step = 0;
        _gateway.Answer = _ => ++step switch
        {
            1 => Reply.Call(("skill_save", $$"""{"name":"debug-flaky-test","description":"When a test fails now and then","instructions":"1. Run it ten times. 2. Find the race between the test and the app. 3. Fix the cause, never with a sleep.","scope":"person","why":"Fixed a flake together"}""")),
            _ => Reply.Say("kept"),
        };
        Assert.Equal(0, await h.Run("", "-p", "keep how we fixed it", "--mode", "auto-edit"));
        var learned = Assert.Single(_mcp.Chats.SkillsLearned);
        Assert.Equal(("debug-flaky-test", "Fixed a flake together"), (learned["name"]!.GetValue<string>(), learned["why"]!.GetValue<string>()));
        Assert.Contains("It waits for the person on the Skills page.", FakeGateway.HasToolResults(_gateway.Requests[^1]) ? _gateway.Requests[^1].ToJsonString() : "", StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(h.Work, ".arena")));
    }

    [Fact]
    public async Task After_a_long_turn_the_next_one_is_nudged_to_keep_what_it_worked_out()
    {
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "a.txt"), "a\n");
        var calls = 0;
        _gateway.Answer = req => FakeGateway.Last(req) == "long work" || (FakeGateway.HasToolResults(req) && calls < Agent.NudgeSteps)
            ? Reply.Call(("read_file", $$"""{"path":"a.txt","offset":{{++calls}}}"""))
            : Reply.Say("done");
        Assert.Equal(0, await h.Run("long work\nand then?\n/exit\n", "chat", "--mode", "auto-edit"));
        var then = _gateway.Requests.Last(r => r.ToJsonString().Contains("and then?", StringComparison.Ordinal)).ToJsonString();
        Assert.Contains("steps. If it worked out a way of doing something worth doing again, keep it as a skill with skill_save", then, StringComparison.Ordinal);
        var first = _gateway.Requests.First(r => r.ToJsonString().Contains("long work", StringComparison.Ordinal)).ToJsonString();
        Assert.DoesNotContain("skill_save.", first, StringComparison.Ordinal);
    }
}
