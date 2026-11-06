using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Local skills and skills as files in Code Arena: those of .code-arena/skills and Claude Code's .claude/skills in the
/// project, ~/.code-arena/skills and ~/.claude/skills in the person's home, listed with where each was read; exported as a
/// .zip of their folders and imported from one, a SKILL.md or a folder, in the IDE and the terminal, with a preview, a
/// name taken replaced, renamed or skipped, and a secret never brought in.
/// </summary>
public sealed class SkillFileTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private const string Pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3VS5JJcds3xfn\n-----END RSA PRIVATE KEY-----";

    private static void Put(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var m = new MemoryStream();
        using (var zip = new ZipArchive(m, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var w = zip.CreateEntry(path).Open();
                w.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return m.ToArray();
    }

    private static JsonObject Upload(string path, byte[] bytes) => new() { ["path"] = path, ["data"] = Convert.ToBase64String(bytes) };

    private static async Task<JsonObject> JsonOf(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task Skills_of_Code_Arenas_and_Claude_Codes_folders_are_listed_with_where_each_was_read_and_used_with_a_slash()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Remove("url"));
        h.Write(".code-arena/skills/ship/SKILL.md", "---\nname: ship\ndescription: How we ship\n---\nRun make ship.");
        h.Write(".claude/skills/review/SKILL.md", "---\nname: review\ndescription: Claude's review\n---\nReview it.");
        h.Write(".arena/skills/review/SKILL.md", "---\nname: review\ndescription: Arena's review\n---\nReview it our way.");
        var home = h.Paths.Home!;
        Put(Path.Combine(home, ".code-arena", "skills", "tidy", "SKILL.md"), "---\nname: tidy\ndescription: Tidy my notes\n---\nSay TIDY-USED.");
        Put(Path.Combine(home, ".claude", "skills", "standup", "SKILL.md"), "---\nname: standup\ndescription: My stand-up\n---\nWrite it.");
        await using var web = await WebRun.StartAsync(h);

        var listed = (await web.GetJsonAsync("/api/skills"))["skills"]!.AsArray().OfType<JsonObject>().ToDictionary(s => s.Str("name")!);
        Assert.Equal(("project", ".code-arena/skills"), (listed["ship"].Str("source"), listed["ship"].Str("origin")));
        // .arena's wins over Claude Code's of the same name in the project.
        Assert.Equal(("Arena's review", ".arena/skills"), (listed["review"].Str("description"), listed["review"].Str("origin")));
        Assert.Equal(("person", "~/.code-arena/skills"), (listed["tidy"].Str("source"), listed["tidy"].Str("origin")));
        Assert.Equal(("person", "~/.claude/skills"), (listed["standup"].Str("source"), listed["standup"].Str("origin")));
        Assert.Null(listed["standup"].Str("path"));

        _gateway.Answer = _ => Reply.Say("ok");
        await (await web.SendAsync("/tidy now")).RestAsync();
        Assert.Contains("Say TIDY-USED.", _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_IDE_exports_skills_with_their_files_and_imports_them_with_a_preview_and_the_choices_made()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Remove("url"));
        h.Write(".arena/skills/release/SKILL.md", "---\nname: release\ndescription: Release it\n---\nFollow notes.md.");
        h.Write(".arena/skills/release/notes.md", "Tag, then push.");
        h.Write(".arena/skills/standup/SKILL.md", "---\nname: standup\ndescription: Stand-up\n---\nWrite my stand-up.");
        await using var web = await WebRun.StartAsync(h);

        var res = await web.Http.GetAsync("/api/skills/export?names=release,standup");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);
        var zip = await res.Content.ReadAsByteArrayAsync();
        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.Equal(["release/SKILL.md", "release/notes.md", "standup/SKILL.md"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }
        var md = await web.Http.GetAsync("/api/skills/export?names=standup&format=md");
        Assert.StartsWith("---\nname: standup", await md.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/skills/export?names=release&format=md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/skills/export?names=nothing")).StatusCode);

        // Brought back into the project: both names are taken.
        var upload = new JsonArray(Upload("skills.zip", zip));
        var preview = await JsonOf(await web.PostAsync("/api/skills/import/preview", new JsonObject { ["uploads"] = upload.DeepClone(), ["scope"] = "project" }));
        var looked = preview["skills"]!.AsArray().OfType<JsonObject>().ToDictionary(s => s.Str("name")!);
        Assert.Equal(".arena/skills", looked["release"]["clash"].Str("origin"));
        Assert.Equal("release-2", looked["release"].Str("suggested"));
        Assert.Equal("notes.md", looked["release"]["files"]![0].Str("path"));
        Assert.Equal("Not signed in to Arena: only looked for secrets here, not through Arena's safeguards.", preview.Str("note"));

        // Not chosen for: left as it was. Renamed and replaced as chosen.
        var none = await JsonOf(await web.PostAsync("/api/skills/import", new JsonObject { ["uploads"] = upload.DeepClone(), ["scope"] = "project" }));
        Assert.Empty(none["imported"]!.AsArray());
        h.Write(".arena/skills/standup/SKILL.md", "---\nname: standup\ndescription: Stand-up\n---\nChanged here.");
        var done = await JsonOf(await web.PostAsync("/api/skills/import", new JsonObject
        {
            ["uploads"] = upload.DeepClone(), ["scope"] = "project",
            ["choices"] = new JsonArray(new JsonObject { ["folder"] = "release", ["action"] = "rename", ["name"] = "release-copy" }, new JsonObject { ["folder"] = "standup", ["action"] = "replace" }),
        }));
        Assert.Equal(["release-copy", "standup"], done["imported"]!.AsArray().Select(i => i.Str("name")));
        Assert.StartsWith("---\nname: release-copy\n", File.ReadAllText(Path.Combine(h.Work, ".arena", "skills", "release-copy", "SKILL.md")), StringComparison.Ordinal);
        Assert.Equal("Tag, then push.", File.ReadAllText(Path.Combine(h.Work, ".arena", "skills", "release-copy", "notes.md")));
        Assert.Contains("Write my stand-up.", File.ReadAllText(Path.Combine(h.Work, ".arena", "skills", "standup", "SKILL.md")), StringComparison.Ordinal);
        Assert.Contains("release-copy", (await web.GetJsonAsync("/api/skills"))["skills"]!.AsArray().Select(s => s.Str("name")));
    }

    [Fact]
    public async Task A_secret_never_comes_in_and_nothing_leads_out_of_its_folder()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Remove("url"));
        await using var web = await WebRun.StartAsync(h);
        var zip = Zip(("deploy/SKILL.md", "---\nname: deploy\n---\nUse key.pem."), ("deploy/key.pem", Pem), ("ok/SKILL.md", "---\nname: ok\n---\nFine."),
            ("ok/../../escape.md", "out"), ("tokens/SKILL.md", "---\nname: tokens\n---\nUse ghp_1234567890abcdefghijklmnopqrstuvwxyzAB."));
        var body = new JsonObject { ["uploads"] = new JsonArray(Upload("s.zip", zip)), ["scope"] = "project" };

        var preview = await JsonOf(await web.PostAsync("/api/skills/import/preview", body.DeepClone()));
        var looked = preview["skills"]!.AsArray().OfType<JsonObject>().ToDictionary(s => s.Str("name")!);
        Assert.StartsWith("key.pem holds a secret", looked["deploy"].Str("problem"), StringComparison.Ordinal);
        Assert.StartsWith("SKILL.md holds a secret", looked["tokens"].Str("problem"), StringComparison.Ordinal);
        Assert.Contains(preview["leftOut"]!.AsArray(), l => l!.GetValue<string>().StartsWith("ok/../../escape.md", StringComparison.Ordinal));

        var done = await JsonOf(await web.PostAsync("/api/skills/import", body.DeepClone()));
        Assert.Equal(["ok"], done["imported"]!.AsArray().Select(i => i.Str("name")));
        Assert.False(Directory.Exists(Path.Combine(h.Work, ".arena", "skills", "deploy")));
        Assert.False(File.Exists(Path.Combine(h.Work, "escape.md")));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/skills/import/preview", new JsonObject { ["uploads"] = new JsonArray(Upload("a.zip", [1, 2, 3])) })).StatusCode);
    }

    [Fact]
    public async Task Signed_in_your_imported_skills_are_yours_in_Arena_and_Arenas_safeguards_look_at_the_projects_too()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.ArenaSkills = [FakeChats.OwnSkill("notes", "Mine", "My notes.")];
        _mcp.Chats.SkillsRefused["leaky"] = "its SKILL.md: it holds a password.";
        await using var web = await WebRun.StartAsync(h);
        var zip = Zip(("notes/SKILL.md", "---\nname: notes\ndescription: Theirs\n---\nTheir notes."), ("leaky/SKILL.md", "---\nname: leaky\n---\nThe password is hunter2."));
        var upload = new JsonArray(Upload("s.zip", zip));

        var preview = await JsonOf(await web.PostAsync("/api/skills/import/preview", new JsonObject { ["uploads"] = upload.DeepClone(), ["scope"] = "person" }));
        Assert.True(preview["inArena"]!.GetValue<bool>());
        Assert.NotNull(preview["skills"]!.AsArray().Single(s => s.Str("name") == "notes")!["clash"]);
        var done = await JsonOf(await web.PostAsync("/api/skills/import", new JsonObject
        {
            ["uploads"] = upload.DeepClone(), ["scope"] = "person", ["choices"] = new JsonArray(new JsonObject { ["folder"] = "notes", ["action"] = "rename", ["name"] = "their-notes" }),
        }));
        Assert.Equal(["their-notes"], done["imported"]!.AsArray().Select(i => i.Str("name")));
        Assert.Equal("person", (await web.GetJsonAsync("/api/skills"))["skills"]!.AsArray().Single(s => s.Str("name") == "their-notes")!.Str("source"));
        Assert.False(Directory.Exists(Path.Combine(h.Paths.ConfigDir, "skills", "their-notes")));

        // For the project: Arena's safeguards are asked first, and what they refuse stays out.
        var project = await JsonOf(await web.PostAsync("/api/skills/import", new JsonObject { ["uploads"] = upload.DeepClone(), ["scope"] = "project" }));
        Assert.Equal(["notes"], project["imported"]!.AsArray().Select(i => i.Str("name")));
        Assert.Equal("The safeguards refused its SKILL.md: it holds a password.", project["skipped"]!.AsArray().Single()!.Str("reason"));
        Assert.Contains(_mcp.Chats.SkillsImported, s => s["preview"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task The_terminal_exports_and_imports_skills_and_asks_about_a_name_taken()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Remove("url"));
        h.Write(".arena/skills/ship/SKILL.md", "---\nname: ship\ndescription: How we ship\n---\nRun make ship.");
        h.Write(".arena/skills/ship/steps.md", "1. make\n2. ship");
        _gateway.Answer = _ => Reply.Say("ok");

        Assert.Equal(0, await h.Run("/skills export ship --to out.zip\n/exit\n", "chat"));
        Assert.Contains("Exported /ship to out.zip", (h.Out + h.Err), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(h.Work, "out.zip")));

        // Back in, for the person: nothing in the way. Then into the project, where /ship is taken: renamed as answered.
        Assert.Equal(0, await h.Run("/skills import out.zip --mine\n/skills import out.zip\nn\n/exit\n", "chat"));
        Assert.Contains("Imported /ship as yours on this computer.", (h.Out + h.Err), StringComparison.Ordinal);
        Assert.Equal("1. make\n2. ship", File.ReadAllText(Path.Combine(h.Paths.ConfigDir, "skills", "ship", "steps.md")));
        Assert.Contains("There is a /ship already.", (h.Out + h.Err), StringComparison.Ordinal);
        Assert.Contains("Imported /ship-2 for this project", (h.Out + h.Err), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(h.Work, ".arena", "skills", "ship-2", "steps.md")));

        // A folder, as Claude Code's skills folder is: each skill in it.
        Put(Path.Combine(h.Root, "claude-skills", "a", "SKILL.md"), "---\nname: alpha\n---\nDo A.");
        Put(Path.Combine(h.Root, "claude-skills", "b", "SKILL.md"), "---\nname: beta\n---\nDo B.");
        Assert.Equal(0, await h.Run($"/skills import {Path.Combine(h.Root, "claude-skills")} --skip\n/exit\n", "chat"));
        Assert.Contains("Imported /alpha, /beta for this project", (h.Out + h.Err), StringComparison.Ordinal);
    }
}
