using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// The skills in the IDE: every one Code Arena reads (the project's, the person's, Arena's and its own), read again as they
/// change; one made from the IDE; and /name, which uses one as the web chat's slash does.
/// </summary>
public sealed class IdeSkillTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static Dictionary<string, (string Source, string? Path)> Listed(JsonNode answer) =>
        answer["skills"]!.AsArray().ToDictionary(s => s!["name"]!.GetValue<string>(), s => (s!["source"]!.GetValue<string>(), s["path"]?.GetValue<string>()));

    [Fact]
    public async Task The_IDE_lists_every_skill_and_one_added_while_it_runs()
    {
        using var h = new Harness(_gateway, _mcp, c => c["builtInSkills"] = true);
        _mcp.Chats.SkillsOff = ["debug"];
        _mcp.Chats.ArenaSkills = [new JsonObject { ["name"] = "triage", ["files"] = new JsonObject { ["SKILL.md"] = "---\nname: triage\ndescription: Sort the issues\n---\nSort them." } }];
        h.Write(".arena/skills/ship/SKILL.md", "---\nname: ship\ndescription: How we ship\n---\nRun make ship.");
        h.Write(".arena/commands/hello.md", "---\ndescription: Say hello\nargument-hint: NAME\n---\nSay hello to $ARGUMENTS.");
        var own = Path.Combine(h.Paths.ConfigDir, "skills", "tidy", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "---\nname: tidy\ndescription: Tidy my notes\n---\nTidy them.");
        await using var web = await WebRun.StartAsync(h);

        var answer = await web.GetJsonAsync("/api/skills");
        var skills = Listed(answer);
        Assert.Equal(("project", ".arena/skills/ship/SKILL.md"), skills["ship"]);
        Assert.Equal(("person", "user:skills/tidy/SKILL.md"), skills["tidy"]);
        Assert.Equal(("arena", (string?)null), skills["triage"]);
        Assert.Equal(("builtin", (string?)null), skills["code-review"]);
        Assert.DoesNotContain("debug", skills.Keys);
        Assert.Equal("How we ship", answer["skills"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "ship")!["description"]!.GetValue<string>());
        // The slash menu has the commands too.
        var hello = answer["commands"]!.AsArray().Single()!;
        Assert.Equal(("hello", "Say hello", "NAME"), (hello["name"]!.GetValue<string>(), hello["description"]!.GetValue<string>(), hello["argumentHint"]!.GetValue<string>()));

        // One written while it runs is listed at once, and the model is offered it at the next turn.
        h.Write(".arena/skills/late/SKILL.md", "---\nname: late\ndescription: Came late\n---\nSay LATE-SKILL-USED.");
        Assert.Contains("late", Listed(await web.GetJsonAsync("/api/skills")).Keys);
        _gateway.Answer = _ => Reply.Say("ok");
        await (await web.SendAsync("anything")).RestAsync();
        var request = _gateway.Requests[^1];
        Assert.Contains("- late: Came late", request["messages"]![0]!["content"]!.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"late\"", request["tools"]!.AsArray().Single(t => t!["function"]!["name"]!.GetValue<string>() == "skill")!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_skill_is_made_from_the_IDE_for_the_project_or_the_person()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        var made = await web.PostAsync("/api/skills", new JsonObject { ["name"] = "release", ["scope"] = "project" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var skill = JsonNode.Parse(await made.Content.ReadAsStringAsync())!;
        Assert.Equal((".arena/skills/release/SKILL.md", "project"), (skill["path"]!.GetValue<string>(), skill["source"]!.GetValue<string>()));
        var text = File.ReadAllText(Path.Combine(h.Work, ".arena", "skills", "release", "SKILL.md"));
        Assert.StartsWith("---\nname: release\ndescription: ", text, StringComparison.Ordinal);
        Assert.Contains("$ARGUMENTS", text, StringComparison.Ordinal);
        Assert.Contains("release", Listed(await web.GetJsonAsync("/api/skills")).Keys);

        // Not twice, and only a name a skill can have.
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/skills", new JsonObject { ["name"] = "release", ["scope"] = "project" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/skills", new JsonObject { ["name"] = "Bad Name", ["scope"] = "project" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/skills", new JsonObject { ["name"] = "x", ["scope"] = "company" })).StatusCode);

        // The person's: in the config folder, opened and saved as their own files are.
        var mine = JsonNode.Parse(await (await web.PostAsync("/api/skills", new JsonObject { ["name"] = "notes", ["scope"] = "person" })).Content.ReadAsStringAsync())!;
        Assert.Equal("user:skills/notes/SKILL.md", mine["path"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(h.Paths.ConfigDir, "skills", "notes", "SKILL.md")));
        var read = await web.GetJsonAsync("/api/user-files?name=" + Uri.EscapeDataString("skills/notes/SKILL.md"));
        var saved = await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "skills/notes/SKILL.md", ["text"] = "---\nname: notes\ndescription: My notes\n---\nKeep them short.", ["version"] = read["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("My notes", (await web.GetJsonAsync("/api/skills"))["skills"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "notes")!["description"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/user-files?name=" + Uri.EscapeDataString("skills/../config.json"))).StatusCode);
    }

    [Fact]
    public async Task Slash_and_a_skills_name_uses_it_with_the_words_after_it()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write(".arena/skills/ship/SKILL.md", "---\nname: ship\ndescription: How we ship\n---\nRun make ship for $ARGUMENTS.");
        h.Write(".arena/skills/plain/SKILL.md", "---\nname: plain\ndescription: No words taken\n---\nDo it plainly.");
        h.Write("notes.txt", "the notes\n");
        await using var web = await WebRun.StartAsync(h);
        _gateway.Answer = _ => Reply.Say("ok");
        string Asked() => _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString();

        // The model reads the skill's instructions with the words in them; the page shows what was typed.
        await (await web.SendAsync("/ship the beta")).RestAsync();
        Assert.StartsWith("/ship the beta\n\n<attached>\n<skill name=\"ship\">\nThe person used the skill /ship (How we ship).", Asked(), StringComparison.Ordinal);
        Assert.EndsWith("\n\nRun make ship for the beta.\n</skill>\n</attached>", Asked(), StringComparison.Ordinal);
        Assert.Equal("/ship the beta", (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray().First(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>());

        // Instructions that do not take the words: the words are the message above them; a file named goes too, listed.
        await (await web.SendAsync("/plain carefully, with @notes.txt")).RestAsync();
        Assert.StartsWith("/plain carefully, with @notes.txt\n\n<attached>\n<file path=\"notes.txt\">", Asked(), StringComparison.Ordinal);
        Assert.EndsWith("<skill name=\"plain\">\nThe person used the skill /plain (No words taken). Follow its instructions:\n\nDo it plainly.\n</skill>\n</attached>", Asked(), StringComparison.Ordinal);
        var shown = (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!;
        Assert.Equal(("/plain carefully, with @notes.txt", "notes.txt"), (shown["content"]!.GetValue<string>(), string.Join(", ", shown["files"]!.AsArray().Select(a => a!.GetValue<string>()))));
    }
}
