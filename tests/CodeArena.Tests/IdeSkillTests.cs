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
    }

    [Fact]
    public async Task Not_signed_in_to_Arena_a_skill_made_for_you_is_kept_on_this_computer()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Remove("url"));
        await using var web = await WebRun.StartAsync(h);
        Assert.False((await web.GetJsonAsync("/api/skills"))["mineInArena"]!.GetValue<bool>());

        // In the config folder, opened and saved as their own files are.
        var mine = JsonNode.Parse(await (await web.PostAsync("/api/skills", new JsonObject { ["name"] = "notes", ["scope"] = "person" })).Content.ReadAsStringAsync())!;
        Assert.Equal("user:skills/notes/SKILL.md", mine["path"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(h.Paths.ConfigDir, "skills", "notes", "SKILL.md")));
        var read = await web.GetJsonAsync("/api/user-files?name=" + Uri.EscapeDataString("skills/notes/SKILL.md"));
        var saved = await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "skills/notes/SKILL.md", ["text"] = "---\nname: notes\ndescription: My notes\n---\nKeep them short.", ["version"] = read["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("My notes", (await web.GetJsonAsync("/api/skills"))["skills"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "notes")!["description"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/user-files?name=" + Uri.EscapeDataString("skills/../config.json"))).StatusCode);
        Assert.Empty(_mcp.Chats.SkillsMade);
    }

    [Fact]
    public async Task Signed_in_a_skill_made_for_you_is_one_of_yours_in_Arena_and_saved_there()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.ArenaSkills = [];
        await using var web = await WebRun.StartAsync(h);
        Assert.True((await web.GetJsonAsync("/api/skills"))["mineInArena"]!.GetValue<bool>());

        // Made in Arena with the person's key, as the web chat, Code Studio and Code Arena elsewhere have it; not in the config folder.
        var made = await web.PostAsync("/api/skills", new JsonObject { ["name"] = "notes", ["scope"] = "person" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var mine = JsonNode.Parse(await made.Content.ReadAsStringAsync())!;
        Assert.Equal(("person", "user:skills/notes/SKILL.md"), (mine["source"]!.GetValue<string>(), mine["path"]!.GetValue<string>()));
        var sent = Assert.Single(_mcp.Chats.SkillsMade);
        Assert.Equal("notes", sent["name"]!.GetValue<string>());
        Assert.Contains("$ARGUMENTS", sent["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(h.Paths.ConfigDir, "skills", "notes")));
        Assert.Equal(("person", "user:skills/notes/SKILL.md"), Listed(await web.GetJsonAsync("/api/skills"))["notes"]);

        // Opened as one of their files, as Arena keeps it; saved back to Arena.
        var read = await web.GetJsonAsync("/api/user-files?name=" + Uri.EscapeDataString("skills/notes/SKILL.md"));
        Assert.StartsWith("---\nname: notes\n", read["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var saved = await web.PostAsync("/api/user-files", new JsonObject
        {
            ["name"] = "skills/notes/SKILL.md", ["text"] = "---\nname: notes\ndescription: My notes\n---\nKeep them short for $ARGUMENTS.", ["version"] = read["version"]!.GetValue<string>(),
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var changed = Assert.Single(_mcp.Chats.SkillsChanged);
        Assert.Equal(("My notes", "Keep them short for $ARGUMENTS."), (changed["description"]!.GetValue<string>(), changed["instructions"]!.GetValue<string>()));
        Assert.Equal("My notes", (await web.GetJsonAsync("/api/skills"))["skills"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "notes")!["description"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(h.Paths.ConfigDir, "skills", "notes")));

        // Changed on Arena's Skills page since it was opened: not overwritten. Nor named anew here, nor emptied.
        _mcp.Chats.ArenaSkills = [FakeChats.OwnSkill("notes", "Changed on the web", "Other steps.")];
        var stale = await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "skills/notes/SKILL.md", ["text"] = "---\nname: notes\n---\nMine.", ["version"] = read["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var now = (await web.GetJsonAsync("/api/user-files?name=" + Uri.EscapeDataString("skills/notes/SKILL.md")))["version"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "skills/notes/SKILL.md", ["text"] = "---\nname: other\n---\nMine.", ["version"] = now })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/user-files", new JsonObject { ["name"] = "skills/notes/SKILL.md", ["text"] = "---\nname: notes\n---\n", ["version"] = now })).StatusCode);
        Assert.Single(_mcp.Chats.SkillsChanged);

        // One of that name in Arena already: said, not made twice.
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/skills", new JsonObject { ["name"] = "notes", ["scope"] = "person" })).StatusCode);
    }

    [Fact]
    public async Task Saving_a_web_made_skill_leaves_what_was_not_touched_as_Arena_has_it()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.ArenaSkills = [FakeChats.OwnSkill("websk6", "Says \"hello\" in a language.", "Say hello.")];
        await using var web = await WebRun.StartAsync(h);

        // Only the instructions changed: the description (its quotes single in the SKILL.md) and the title are not sent.
        var read = await web.GetJsonAsync("/api/user-files?name=" + Uri.EscapeDataString("skills/websk6/SKILL.md"));
        var text = read["text"]!.GetValue<string>();
        var saved = await web.PostAsync("/api/user-files", new JsonObject
        {
            ["name"] = "skills/websk6/SKILL.md", ["text"] = text.Replace("Say hello.", "Say hello in French.", StringComparison.Ordinal), ["version"] = read["version"]!.GetValue<string>(),
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var changed = Assert.Single(_mcp.Chats.SkillsChanged);
        Assert.Equal("Say hello in French.", changed["instructions"]!.GetValue<string>());
        Assert.Null(changed["description"]);
        Assert.Null(changed["title"]);
        Assert.Null(changed["parameters"]);
        Assert.Equal("Says \"hello\" in a language.", _mcp.Chats.ArenaSkills!.OfType<JsonObject>().Single()["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_skill_made_or_turned_off_in_Arena_while_the_IDE_runs_is_there_after_Refresh()
    {
        // Asked again at most this often unasked: here, not within the test.
        using var h = new Harness(_gateway, _mcp, c => c["builtInSkills"] = true) { ArenaSkillsEvery = TimeSpan.FromHours(1) };
        _mcp.Chats.SkillsOff = [];
        _mcp.Chats.ArenaSkills = [new JsonObject { ["name"] = "triage", ["files"] = new JsonObject { ["SKILL.md"] = "---\nname: triage\ndescription: Sort the issues\n---\nSort them." } }];
        await using var web = await WebRun.StartAsync(h);
        var skills = Listed(await web.GetJsonAsync("/api/skills"));
        Assert.Contains("triage", skills.Keys);
        Assert.Contains("debug", skills.Keys);

        // Made on Arena's Skills page, and an admin turned one of Arena's own off: Refresh (fresh) asks Arena at once.
        _mcp.Chats.ArenaSkills = [new JsonObject { ["name"] = "websky", ["files"] = new JsonObject { ["SKILL.md"] = "---\nname: websky\ndescription: Made on the web\n---\nSay WEBSKY." } }];
        _mcp.Chats.SkillsOff = ["debug"];
        Assert.DoesNotContain("websky", Listed(await web.GetJsonAsync("/api/skills")).Keys);
        skills = Listed(await web.GetJsonAsync("/api/skills?fresh=1"));
        Assert.Equal(("arena", (string?)null), skills["websky"]);
        Assert.DoesNotContain("triage", skills.Keys);
        Assert.DoesNotContain("debug", skills.Keys);
        Assert.True(File.Exists(Path.Combine(h.Paths.ConfigDir, "plugin-skills", "websky", "SKILL.md")));
    }

    [Fact]
    public async Task A_skill_made_in_Arena_while_the_IDE_runs_is_used_at_the_next_turn()
    {
        using var h = new Harness(_gateway, _mcp) { ArenaSkillsEvery = TimeSpan.Zero };
        _mcp.Chats.ArenaSkills = [];
        await using var web = await WebRun.StartAsync(h);
        _gateway.Answer = _ => Reply.Say("ok");

        _mcp.Chats.ArenaSkills = [new JsonObject { ["name"] = "websky2", ["files"] = new JsonObject { ["SKILL.md"] = "---\nname: websky2\ndescription: Made on the web\n---\nSay WEBSKY2-USED for $ARGUMENTS." } }];
        await (await web.SendAsync("/websky2 the team")).RestAsync();
        var asked = _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString();
        Assert.StartsWith("/websky2 the team\n\n<attached>\n<skill name=\"websky2\">", asked, StringComparison.Ordinal);
        Assert.Contains("Say WEBSKY2-USED for the team.", asked, StringComparison.Ordinal);
        Assert.Contains("- websky2: Made on the web", _gateway.Requests[^1]["messages"]![0]!["content"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_skill_made_on_the_web_just_after_a_turn_is_used_at_once()
    {
        // Asked again at most this often unasked: here, not within the test.
        using var h = new Harness(_gateway, _mcp) { ArenaSkillsEvery = TimeSpan.FromHours(1) };
        _mcp.Chats.ArenaSkills = [];
        await using var web = await WebRun.StartAsync(h);
        _gateway.Answer = _ => Reply.Say("ok");
        await (await web.SendAsync("first")).RestAsync();

        _mcp.Chats.ArenaSkills = [new JsonObject { ["name"] = "websk3", ["files"] = new JsonObject { ["SKILL.md"] = "---\nname: websk3\ndescription: Made on the web\n---\nSay WEBSK3-USED." } }];
        await (await web.SendAsync("/websk3 now")).RestAsync();
        var asked = _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString();
        Assert.StartsWith("/websk3 now\n\n<attached>\n<skill name=\"websk3\">", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_subfolder_of_the_repository_the_projects_skills_open_and_a_new_one_opens_too()
    {
        using var h = new Harness(_gateway, _mcp);
        Directory.CreateDirectory(Path.Combine(h.Work, ".git"));
        h.Write(".arena/skills/ship/SKILL.md", "---\nname: ship\ndescription: How we ship\n---\nRun make ship.");
        h.Write(".arena/skills/ship/notes.md", "notes");
        h.Write("secret.txt", "not the IDE's");
        h.Write("packages/web/index.ts", "export {}\n");
        h.Cwd = Path.Combine(h.Work, "packages", "web");
        await using var web = await WebRun.StartAsync(h);

        // Outside the folder opened, by its whole path: read and saved as a file of the folder is.
        var whole = Path.Combine(h.Work, ".arena", "skills", "ship", "SKILL.md");
        Assert.Equal(("project", whole), Listed(await web.GetJsonAsync("/api/skills"))["ship"]);
        var read = await web.GetJsonAsync("/api/file?path=" + Uri.EscapeDataString(whole));
        Assert.Equal("---\nname: ship\ndescription: How we ship\n---\nRun make ship.", read["text"]!.GetValue<string>());
        var saved = await web.PostAsync("/api/file", new JsonObject { ["path"] = whole, ["text"] = "---\nname: ship\ndescription: How we ship now\n---\nRun make ship.", ["version"] = read["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("How we ship now", (await web.GetJsonAsync("/api/skills"))["skills"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "ship")!["description"]!.GetValue<string>());
        // What is typed and not saved yet is kept as a folder's file's is.
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/backups", new JsonObject { ["path"] = whole, ["text"] = "unsaved", ["version"] = "v" })).StatusCode);

        // A new one for the project: in .arena/skills at the repository's root, and given the path the editor opens.
        var made = JsonNode.Parse(await (await web.PostAsync("/api/skills", new JsonObject { ["name"] = "release", ["scope"] = "project" })).Content.ReadAsStringAsync())!;
        var release = Path.Combine(h.Work, ".arena", "skills", "release", "SKILL.md");
        Assert.Equal(release, made["path"]!.GetValue<string>());
        Assert.StartsWith("---\nname: release\n", (await web.GetJsonAsync("/api/file?path=" + Uri.EscapeDataString(release)))["text"]!.GetValue<string>(), StringComparison.Ordinal);

        // Only the skills' SKILL.md: the rest of the repository stays outside the folder.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=" + Uri.EscapeDataString(Path.Combine(h.Work, "secret.txt")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=" + Uri.EscapeDataString(Path.Combine(h.Work, ".arena", "skills", "ship", "notes.md")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=" + Uri.EscapeDataString(Path.Combine(h.Work, ".arena", "skills", "nope", "SKILL.md")))).StatusCode);
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
