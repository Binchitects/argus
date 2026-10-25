using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Arena's own skills in Code Arena: built in, read after everyone else's, left out when Arena's admins turned one off, their parameters named for the model.</summary>
public sealed class BuiltInSkillTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string System(JsonObject request) => request["messages"]![0]!["content"]!.ToString();

    [Fact]
    public void A_skills_parameters_are_named_for_the_model_to_take_from_the_request()
    {
        var skill = BuiltInSkills.All["code-review"];
        var body = skill[(skill.IndexOf("\n---", 3, StringComparison.Ordinal) + 4)..].Trim();
        var filled = BuiltInSkills.Filled(skill, body);
        Assert.StartsWith("Review ‹what›, with ‹focus› as the focus.", filled, StringComparison.Ordinal);
        Assert.Contains("- ‹what›: The code to review: a file, a diff, a branch, or the change just made (default: the code or change in this conversation)", filled, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", filled, StringComparison.Ordinal);
        Assert.True(BuiltInSkills.All.Count >= 20);
    }

    [Fact]
    public async Task Arenas_own_skills_are_offered_after_the_persons_and_one_turned_off_in_Arena_is_not()
    {
        using var h = new Harness(_gateway, _mcp, c => c["builtInSkills"] = true);
        _mcp.Chats.SkillsOff = ["debug"];
        // The person's own of the same name wins.
        h.Write(".arena/skills/summarize/SKILL.md", "---\nname: summarize\ndescription: Our own way to summarize\n---\nSummarize in one line.");
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("ok") : Reply.Call(("skill", """{"name":"code-review"}"""));
        Assert.Equal(0, await h.Run("", "-p", "review my change"));
        var system = System(_gateway.Requests[0]);
        Assert.Contains("- code-review: Review code or a change for bugs", system, StringComparison.Ordinal);
        Assert.Contains("- summarize: Our own way to summarize", system, StringComparison.Ordinal);
        Assert.DoesNotContain("- debug:", system, StringComparison.Ordinal);
        var loaded = _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.ToString();
        Assert.Contains("Review ‹what›", loaded, StringComparison.Ordinal);
        Assert.Contains("Parameters: take each from the person's request", loaded, StringComparison.Ordinal);
    }
}
