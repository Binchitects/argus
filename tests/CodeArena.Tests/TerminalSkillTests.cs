using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// The skills in the terminal and in print mode (-p), as in the IDE: /skills lists them by whose they are and makes one,
/// /name uses one (or a command), one added or made in Arena while the session runs too.
/// </summary>
public sealed class TerminalSkillTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private string LastUser() => _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString();

    private static JsonObject ArenaSkill(string name, string description, string instructions) =>
        new() { ["name"] = name, ["files"] = new JsonObject { ["SKILL.md"] = $"---\nname: {name}\ndescription: {description}\n---\n{instructions}" } };

    [Fact]
    public async Task Print_mode_uses_a_skill_or_a_command_named_with_a_slash()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write(".arena/skills/proj-hello/SKILL.md", "---\nname: proj-hello\ndescription: Greet the project\n---\nSay PROJ-HELLO-USED to $ARGUMENTS.");
        h.Write(".arena/commands/greet.md", "Say hello to $ARGUMENTS in one line.");
        _gateway.Answer = _ => Reply.Say("ok");

        Assert.Equal(0, await h.Run("", "-p", "/proj-hello there"));
        Assert.StartsWith("/proj-hello there\n\n<attached>\n<skill name=\"proj-hello\">\nThe person used the skill /proj-hello (Greet the project).", LastUser(), StringComparison.Ordinal);
        Assert.Contains("Say PROJ-HELLO-USED to there.", LastUser(), StringComparison.Ordinal);

        Assert.Equal(0, await h.Run("", "-p", "/greet Pat"));
        Assert.Equal("Say hello to Pat in one line.", LastUser());
    }

    [Fact]
    public async Task Skills_lists_them_by_whose_and_one_added_while_the_session_runs_is_used()
    {
        using var h = new Harness(_gateway, _mcp, c => c["builtInSkills"] = true) { ArenaSkillsEvery = TimeSpan.Zero };
        _mcp.Chats.ArenaSkills = [ArenaSkill("triage", "Sort the issues", "Sort them.")];
        h.Write(".arena/skills/ship/SKILL.md", "---\nname: ship\ndescription: How we ship\n---\nRun make ship.");
        var own = Path.Combine(h.Paths.ConfigDir, "skills", "tidy", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "---\nname: tidy\ndescription: Tidy my notes\n---\nTidy them.");
        // While the first message is answered: one written in the project, one made on Arena's Skills page.
        _gateway.Answer = req =>
        {
            if (FakeGateway.Last(req) == "first")
            {
                h.Write(".arena/skills/late/SKILL.md", "---\nname: late\ndescription: Came late\n---\nSay LATE-USED for $ARGUMENTS.");
                _mcp.Chats.ArenaSkills = [ArenaSkill("triage", "Sort the issues", "Sort them."), ArenaSkill("websky", "Made on the web", "Say WEBSKY-USED.")];
            }
            return Reply.Say("ok");
        };
        Assert.Equal(0, await h.Run("/skills\n/help\nfirst\n/late the beta\n/websky now\n/exit\n", "chat"));
        var lateAsked = _gateway.Requests.Select(FakeGateway.Last).FirstOrDefault(m => m.StartsWith("/late", StringComparison.Ordinal));
        var listed = h.Out[h.Out.IndexOf("This project", StringComparison.Ordinal)..];
        string[] order = ["This project", "/ship", "How we ship", "Yours", "/tidy", "From Arena", "/triage", "Built in", "/code-review"];
        Assert.Equal(order, order.OrderBy(w => listed.IndexOf(w, StringComparison.Ordinal)));
        Assert.Contains("/NAME [words]", h.Out, StringComparison.Ordinal);
        Assert.Contains("/skills [new NAME", h.Out, StringComparison.Ordinal);
        Assert.NotNull(lateAsked);
        Assert.StartsWith("/late the beta\n\n<attached>\n<skill name=\"late\">", lateAsked, StringComparison.Ordinal);
        Assert.Contains("Say LATE-USED for the beta.", lateAsked, StringComparison.Ordinal);
        Assert.StartsWith("/websky now\n\n<attached>\n<skill name=\"websky\">", LastUser(), StringComparison.Ordinal);
        Assert.DoesNotContain("There is no command", h.Out + h.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skills_new_makes_one_for_the_project_or_one_of_yours_in_Arena()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.ArenaSkills = [];
        Assert.Equal(0, await h.Run("/skills new release\n/skills new release\n/skills new notes --mine\n/skills new Bad Name\n/skills\n/exit\n", "chat"));
        var file = Path.Combine(h.Work, ".arena", "skills", "release", "SKILL.md");
        Assert.StartsWith("---\nname: release\n", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(".arena", "skills", "release", "SKILL.md"), h.Out, StringComparison.Ordinal);
        Assert.Contains("There is a skill release there already", h.Out + h.Err, StringComparison.Ordinal);
        Assert.Equal("notes", Assert.Single(_mcp.Chats.SkillsMade)["name"]!.GetValue<string>());
        Assert.Contains("/notes is one of your skills in Arena", h.Out, StringComparison.Ordinal);
        Assert.Contains("A skill's name is lowercase letters", h.Out + h.Err, StringComparison.Ordinal);
        // Listed at once: the project's and the person's in Arena.
        var listed = h.Out[h.Out.LastIndexOf("This project", StringComparison.Ordinal)..];
        Assert.Contains("/release", listed, StringComparison.Ordinal);
        Assert.Contains("Yours", listed, StringComparison.Ordinal);
        Assert.Contains("/notes", listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_signed_in_skills_new_mine_keeps_it_on_this_computer()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Remove("url"));
        Assert.Equal(0, await h.Run("/skills new notes --mine\n/exit\n", "chat"));
        var file = Path.Combine(h.Paths.ConfigDir, "skills", "notes", "SKILL.md");
        Assert.True(File.Exists(file));
        Assert.Contains(file, h.Out, StringComparison.Ordinal);
        Assert.Empty(_mcp.Chats.SkillsMade);
    }
}
