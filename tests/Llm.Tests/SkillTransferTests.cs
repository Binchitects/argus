using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Api.Chat;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Skills as files, as Claude Code keeps them (a folder with a SKILL.md and the files beside it): exported one or many as a
/// .zip, imported from a .zip, a SKILL.md or a folder with a preview first, a name the person has already replaced,
/// renamed or skipped as they choose, every file through the safeguards, within limits, and only by whom may make skills.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class SkillTransferTests(AppFixture app)
{
    private const string Pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF8PbnGy0AHB7MhgHcTz6sE2I2yPB\naFDrBz9vFqU4yCsDGV0AsbJ2uKgr2ZJ3QaLTBmHeXGfrBDqbEJ0VXqRvbJF4Jd9X\n-----END RSA PRIVATE KEY-----";

    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("skillfiles_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-skill-file-tests" });

    private static async Task<(TestBrowser Browser, Guid Id, HttpClient CodeArena)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "sf" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var arena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        arena.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.GetProperty("apiKey").GetString());
        arena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), arena);
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

    private static async Task<HttpResponseMessage> UploadAsync(TestBrowser b, string path, (string Name, byte[] Bytes)[] files, object? choices = null)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            form.Add(new ByteArrayContent(bytes), "files", name);
        }
        if (choices is not null)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(choices, JsonSerializerOptions.Web)), "choices");
        }
        return await b.Http.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    private static async Task<List<JsonElement>> SkillsAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/skills"))).GetProperty("skills").EnumerateArray()];

    private const string Review = """
        ---
        name: review-mr
        description: Review a merge request: what changed, what could break.
        allowed-tools: Read, Grep
        ---
        Review the merge request. Read checklist.md first.
        """;

    [Fact]
    public void A_folder_is_found_by_its_SKILL_md_and_nothing_leads_out_of_it()
    {
        var (folders, leftOut) = SkillTransfer.Read([new("skills.zip", Zip(
            ("skills/review-mr/SKILL.md", Review),
            ("skills/review-mr/checklist.md", "- tests\n- docs"),
            ("skills/review-mr/scripts/run.sh", "echo ok"),
            ("skills/review-mr/logo.png", "\u0089PNG\0\0"),
            ("skills/../../etc/passwd", "root"),
            ("skills/notes.txt", "outside every skill"),
            ("__MACOSX/skills/._SKILL.md", "junk")))]);

        var folder = Assert.Single(folders);
        Assert.Equal("review-mr", folder.Folder);
        Assert.Equal(["SKILL.md", "checklist.md", "scripts/run.sh"], folder.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("skills/review-mr/logo.png: not a text file", leftOut);
        Assert.Contains("skills/notes.txt: in no skill's folder", leftOut);
        Assert.Contains(leftOut, l => l.StartsWith("skills/../../etc/passwd", StringComparison.Ordinal));
        Assert.DoesNotContain(leftOut, l => l.Contains("MACOSX", StringComparison.Ordinal));

        var skill = SkillTransfer.Parse(folder);
        Assert.Null(skill.Problem);
        Assert.Equal(("review-mr", "Review mr", "Review a merge request: what changed, what could break."), (skill.Name, skill.Title, skill.Description));
        Assert.Equal("Review the merge request. Read checklist.md first.", skill.Instructions);
        Assert.Equal(["checklist.md", "scripts/run.sh"], skill.Files.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_lone_SKILL_md_is_one_skill_and_what_will_not_do_is_said()
    {
        var (folders, _) = SkillTransfer.Read([new("SKILL.md", Encoding.UTF8.GetBytes("---\nname: Triage It\n---\n"))]);
        var skill = SkillTransfer.Parse(Assert.Single(folders));
        Assert.Equal("triage it", skill.Name);
        Assert.Equal("Its SKILL.md has no instructions under its front matter.", skill.Problem);
        Assert.NotNull(SkillLibrary.NameProblem(skill.Name));

        Assert.Equal("No skill is in it: each skill is a folder with a SKILL.md.",
            Assert.Throws<SkillTransfer.TransferException>(() => SkillTransfer.Read([new("a.zip", Zip(("readme.txt", "hi")))])).Message);
        Assert.StartsWith("The archive does not read as a .zip",
            Assert.Throws<SkillTransfer.TransferException>(() => SkillTransfer.Read([new("a.zip", [1, 2, 3])])).Message, StringComparison.Ordinal);
        Assert.Equal("An import is at most 5 MB.",
            Assert.Throws<SkillTransfer.TransferException>(() => SkillTransfer.Read([new("big/SKILL.md", new byte[SkillTransfer.MaxUpload + 1])])).Message);
        var many = Enumerable.Range(0, SkillTransfer.MaxSkills + 1).Select(i => ($"s{i}/SKILL.md", $"---\nname: s{i}\n---\nDo {i}.")).ToArray();
        Assert.Equal($"An import brings at most {SkillTransfer.MaxSkills} skills; this has {SkillTransfer.MaxSkills + 1}.",
            Assert.Throws<SkillTransfer.TransferException>(() => SkillTransfer.Read([new("many.zip", Zip(many))])).Message);
        Assert.Null(SkillTransfer.Clean("/etc/passwd"));
        Assert.Null(SkillTransfer.Clean("C:/Windows/x"));
        Assert.Null(SkillTransfer.Clean("a/../../b"));
        Assert.Equal("a/b.md", SkillTransfer.Clean("a\\b.md"));
    }

    [Fact]
    public async Task Skills_go_out_as_a_zip_of_their_folders_and_come_back_as_they_were()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (a, _, _) = await PersonAsync(f, admin);
        var (b, _, bArena) = await PersonAsync(f, admin);
        var made = await a.JsonAsync(await a.PostAsync("/api/skills", new
        {
            name = "release_check", title = "Check a \"release\"", description = "Before a release ships.", instructions = "Check {{version}}: see notes.md.",
            parameters = new object[] { new { name = "version", description = "x.y.z", required = true } },
            files = new Dictionary<string, string> { ["notes.md"] = "Tags, notes, the changelog." },
        }));
        var other = await a.JsonAsync(await a.PostAsync("/api/skills", new { name = "standup", title = "Stand-up", instructions = "Write my stand-up." }));

        var one = await a.GetAsync($"/api/skills/export?ids={other.GetProperty("id")}&format=md");
        await StatusAssert.Is(HttpStatusCode.OK, one);
        Assert.Equal("SKILL.md", one.Content.Headers.ContentDisposition!.FileNameStar ?? one.Content.Headers.ContentDisposition.FileName);
        Assert.StartsWith("---\nname: standup\n", await one.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await a.GetAsync($"/api/skills/export?ids={made.GetProperty("id")}&format=md"));
        // Nobody else's skill goes out to them.
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/skills/export?ids={made.GetProperty("id")}"));

        var res = await a.GetAsync($"/api/skills/export?ids={made.GetProperty("id")},{other.GetProperty("id")}");
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);
        var zip = await res.Content.ReadAsByteArrayAsync();
        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.Equal(["release_check/SKILL.md", "release_check/notes.md", "standup/SKILL.md"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        // Another person imports the archive: their own now, parameters and files with them.
        var preview = await b.JsonAsync(await UploadAsync(b, "/api/skills/import/preview", [("skills.zip", zip)]));
        Assert.Equal(["release_check", "standup"], preview.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.All(preview.GetProperty("skills").EnumerateArray(), s => Assert.Equal(JsonValueKind.Null, s.GetProperty("clash").ValueKind));
        var done = await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("skills.zip", zip)]));
        Assert.Equal(2, done.GetProperty("imported").GetArrayLength());
        var theirs = (await SkillsAsync(b)).Single(s => s.GetProperty("name").GetString() == "release_check");
        Assert.Equal(("Check a \"release\"", "mine", "Check {{version}}: see notes.md."),
            (theirs.GetProperty("title").GetString(), theirs.GetProperty("source").GetString(), theirs.GetProperty("instructions").GetString()));
        Assert.Equal("x.y.z", theirs.GetProperty("parameters")[0].GetProperty("description").GetString());
        Assert.Equal(["notes.md"], theirs.GetProperty("files").EnumerateArray().Select(x => x.GetString()));

        // Code Arena keeps its files beside its SKILL.md.
        var brought = JsonDocument.Parse(await bArena.GetStringAsync(new Uri("/api/code-arena/skills", UriKind.Relative))).RootElement.GetProperty("skills").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "release_check");
        Assert.Equal("Tags, notes, the changelog.", brought.GetProperty("files").GetProperty("notes.md").GetString());
    }

    [Fact]
    public async Task A_name_the_person_has_is_replaced_renamed_or_skipped_as_they_chose()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, _) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/skills", new { name = "review-mr", title = "Mine", instructions = "My own way." }));
        var zip = Zip(("review-mr/SKILL.md", Review), ("review-mr/checklist.md", "- tests"));

        var preview = await b.JsonAsync(await UploadAsync(b, "/api/skills/import/preview", [("s.zip", zip)]));
        var looked = Assert.Single(preview.GetProperty("skills").EnumerateArray());
        Assert.Equal("Mine", looked.GetProperty("clash").GetProperty("title").GetString());
        Assert.Equal("review-mr-2", looked.GetProperty("suggested").GetString());

        // Not chosen for: left as it was.
        var left = await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("s.zip", zip)]));
        Assert.Equal(0, left.GetProperty("imported").GetArrayLength());
        Assert.Equal("My own way.", (await SkillsAsync(b)).Single(s => s.GetProperty("name").GetString() == "review-mr").GetProperty("instructions").GetString());

        var renamed = await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("s.zip", zip)], new[] { new { folder = "review-mr", action = "rename", name = "review-theirs" } }));
        Assert.Equal("review-theirs", renamed.GetProperty("imported")[0].GetProperty("name").GetString());
        var replaced = await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("s.zip", zip)], new[] { new { folder = "review-mr", action = "replace" } }));
        Assert.True(replaced.GetProperty("imported")[0].GetProperty("replaced").GetBoolean());
        var skills = await SkillsAsync(b);
        Assert.Equal("Review the merge request. Read checklist.md first.", skills.Single(s => s.GetProperty("name").GetString() == "review-mr").GetProperty("instructions").GetString());
        Assert.Equal(["review-mr", "review-theirs"], skills.Where(s => s.GetProperty("source").GetString() == "mine").Select(s => s.GetProperty("name").GetString()).Order(StringComparer.Ordinal));

        // A name that is no slash name comes in only renamed.
        var odd = Zip(("x/SKILL.md", "---\nname: Way Too Odd!\n---\nDo it."));
        var oddPreview = await b.JsonAsync(await UploadAsync(b, "/api/skills/import/preview", [("odd.zip", odd)]));
        Assert.Equal("way-too-odd", oddPreview.GetProperty("skills")[0].GetProperty("suggested").GetString());
        Assert.Equal(0, (await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("odd.zip", odd)]))).GetProperty("imported").GetArrayLength());
        var oddDone = await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("odd.zip", odd)], new[] { new { folder = "x", action = "rename", name = "way-too-odd" } }));
        Assert.Equal("way-too-odd", oddDone.GetProperty("imported")[0].GetProperty("name").GetString());
    }

    [Fact]
    [Trait("Control", "DAT-04")]
    public async Task An_imported_skill_goes_through_the_safeguards_and_one_holding_a_private_key_is_refused()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, arena) = await PersonAsync(f, admin);
        var zip = Zip(("deploy/SKILL.md", "---\nname: deploy\ndescription: Deploy it.\n---\nDeploy with the key in key.pem."), ("deploy/key.pem", Pem), ("fine/SKILL.md", "---\nname: fine\n---\nAll good."));

        var preview = await b.JsonAsync(await UploadAsync(b, "/api/skills/import/preview", [("s.zip", zip)]));
        var deploy = preview.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "deploy");
        Assert.StartsWith("The safeguards refused key.pem: ", deploy.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Contains("private key", deploy.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEow", preview.GetRawText(), StringComparison.Ordinal);

        var done = await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("s.zip", zip)], new[] { new { folder = "deploy", action = "import" } }));
        Assert.Equal(["fine"], done.GetProperty("imported").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        Assert.Equal("deploy", Assert.Single(done.GetProperty("skipped").EnumerateArray()).GetProperty("name").GetString());
        Assert.DoesNotContain(await SkillsAsync(b), s => s.GetProperty("name").GetString() == "deploy");

        // From Code Arena alike.
        var fromIde = await arena.PostAsJsonAsync(new Uri("/api/code-arena/skills/import", UriKind.Relative), new
        {
            skills = new[] { new { folder = "deploy", files = new Dictionary<string, string> { ["SKILL.md"] = "---\nname: deploy\n---\nUse:\n" + Pem } } },
        });
        await StatusAssert.Is(HttpStatusCode.OK, fromIde);
        var said = JsonDocument.Parse(await fromIde.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, said.GetProperty("imported").GetArrayLength());
        Assert.StartsWith("The safeguards refused its SKILL.md: ", said.GetProperty("skipped")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_web_chats_model_reads_the_files_an_imported_skill_brought()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, _) = await PersonAsync(f, admin);
        var zip = Zip(("review-mr/SKILL.md", Review), ("review-mr/checklist.md", "- tests pass\n- docs updated"));
        Assert.Equal(1, (await b.JsonAsync(await UploadAsync(b, "/api/skills/import", [("s.zip", zip)]))).GetProperty("imported").GetArrayLength());

        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = """Review it [call use_skill {"name":"review-mr","file":"checklist.md"}]""" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        var tool = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("<skill-file skill=\"review-mr\" path=\"checklist.md\">\n- tests pass\n- docs updated\n</skill-file>", tool.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Only_who_may_make_skills_imports_them_from_the_page_or_Code_Arena()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, id, arena) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/rules",
            new { effect = "Deny", subjectKind = "Person", subjectId = id, resourceKind = "feature", resource = "feature.skills" }));
        var zip = Zip(("fine/SKILL.md", "---\nname: fine\n---\nAll good."));

        await StatusAssert.Is(HttpStatusCode.Forbidden, await UploadAsync(b, "/api/skills/import/preview", [("s.zip", zip)]));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await UploadAsync(b, "/api/skills/import", [("s.zip", zip)]));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await arena.PostAsJsonAsync(new Uri("/api/code-arena/skills/import", UriKind.Relative), new
        {
            skills = new[] { new { folder = "fine", files = new Dictionary<string, string> { ["SKILL.md"] = "---\nname: fine\n---\nAll good." } } },
        }));
        Assert.Empty(await SkillsAsync(b));
        // A path out of its folder is refused before anything is read.
        var (c, _, cArena) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await cArena.PostAsJsonAsync(new Uri("/api/code-arena/skills/import", UriKind.Relative), new
        {
            skills = new[] { new { folder = "fine", files = new Dictionary<string, string> { ["SKILL.md"] = "---\nname: fine\n---\nOk.", ["../escape.md"] = "x" } } },
        }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await c.PostAsync("/api/skills", new { name = "bad", title = "Bad", instructions = "x", files = new Dictionary<string, string> { ["/etc/x"] = "y" } }));
    }
}
