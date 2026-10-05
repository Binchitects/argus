using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Knowledge;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Company knowledge: sources (GitLab wikis and issues, a folder, a website) synced into passages with their readers,
/// and search_knowledge returning only what the asker may read, with its link. And retrieval: an assistant's files and
/// long attachments go to the model as the passages that match the question, not their first part.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class KnowledgeTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-knowledge-tests", ["GitLab:Url"] = "https://gitlab.test", ["GitLab:BotToken"] = "bot-token", ["Modules:embed"] = "true",
        };
        foreach (var (k, v) in more ?? [])
        {
            settings[k] = v;
        }
        return app.Create(app.ConnectionStringFor("knowledge_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings);
    }

    internal static async Task<TestBrowser> AdminAsync(WebApplicationFactory<Program> f) => await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    internal static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin, string name)
    {
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    internal static async Task<JsonElement> SourceAsync(TestBrowser admin, Guid id) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/knowledge"))).GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);

    /// <summary>Waits for a sync that ends after <paramref name="after"/> (null: any).</summary>
    internal static async Task<JsonElement> SyncedAsync(TestBrowser admin, Guid id, DateTimeOffset? after = null)
    {
        for (var i = 0; i < 300; i++)
        {
            var s = await SourceAsync(admin, id);
            if (s.GetProperty("state").GetString() is "synced" or "failed" && s.GetProperty("syncedAt").ValueKind == JsonValueKind.String
                && (after is null || s.GetProperty("syncedAt").GetDateTimeOffset() > after))
            {
                return s;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("the sync never ended");
    }

    internal static async Task<(Guid Id, JsonElement Source)> AddAsync(TestBrowser admin, object source)
    {
        var res = await admin.PostAsync("/api/admin/knowledge", source);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var id = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        return (id, await SyncedAsync(admin, id));
    }

    internal static async Task<JsonElement> SyncAgainAsync(TestBrowser admin, Guid id)
    {
        var before = (await SourceAsync(admin, id)).GetProperty("syncedAt").GetDateTimeOffset();
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync($"/api/admin/knowledge/{id}/sync"));
        return await SyncedAsync(admin, id, before);
    }

    private Task<JsonElement> SearchAsync(TestBrowser b, string query) => SearchAsync(app, b, query);

    /// <summary>The person asks; the model calls search_knowledge; what it got back.</summary>
    internal static async Task<JsonElement> SearchAsync(AppFixture app, TestBrowser b, string query)
    {
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools = new[] { "knowledge" } }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = $"Look it up [call search_knowledge {{\"query\":\"{query}\"}}]" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        var tool = app.Model.Requests.Last().Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
        Assert.StartsWith("[Content from the company's documents", tool, StringComparison.Ordinal);
        return JsonDocument.Parse(tool[(tool.IndexOf('\n', StringComparison.Ordinal) + 1)..]).RootElement;
    }

    internal static List<string> Links(JsonElement found) => [.. found.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("link").GetString() ?? r.GetProperty("title").GetString()!)];

    [Fact]
    public async Task A_GitLab_wiki_answers_only_its_project_members_with_the_passage_and_its_link()
    {
        app.Mcp.ResetKnowledge();
        await using var f = NewApp();
        var admin = await AdminAsync(f);
        var (alice, _) = await PersonAsync(f, admin, "alice");
        var (dave, _) = await PersonAsync(f, admin, "dave");
        var (carol, _) = await PersonAsync(f, admin, "carol");
        var (erin, _) = await PersonAsync(f, admin, "erin");
        var (bob, _) = await PersonAsync(f, admin, "bob");

        // No source yet: the tool is there for nobody, and says why to admins.
        var tools = (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == "knowledge");
        Assert.Contains("Admin → Knowledge", tools.GetProperty("unavailable").GetString(), StringComparison.Ordinal);

        // The whole group: both projects, each with its own members.
        var (id, source) = await AddAsync(admin, new { name = "Engineering", kind = "gitlab", location = "group" });
        Assert.Equal("synced", source.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("error").ValueKind);
        // Two wiki pages and an issue of group/app (not the confidential one), and group/secret's page.
        Assert.Equal(4, source.GetProperty("documents").GetInt32());
        Assert.True(source.GetProperty("passages").GetInt32() >= 4);
        var projects = source.GetProperty("projects").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("people").GetInt32());
        // Active members, Guest and up: alice and Dave; not erin (blocked) or frank (minimal access).
        Assert.Equal(new Dictionary<string, int> { ["group/app"] = 2, ["group/secret"] = 1 }, projects);
        var documents = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{id}/documents"))).EnumerateArray().Select(d => d.GetProperty("title").GetString()).ToList();
        Assert.Contains("#12 Decoder drops frames (group/app)", documents);
        Assert.DoesNotContain(documents, d => d!.Contains("Salary", StringComparison.Ordinal));

        // A member gets the passage with its link; the issue with its comments.
        var found = await SearchAsync(alice, "how do I roll back a deploy");
        var first = found.GetProperty("results")[0];
        Assert.Equal("https://gitlab.test/group/app/-/wikis/deploy", first.GetProperty("link").GetString());
        Assert.Equal("Deploy (group/app wiki)", first.GetProperty("title").GetString());
        Assert.Contains("run make rollback", first.GetProperty("passage").GetString(), StringComparison.Ordinal);
        Assert.Equal("Deploy › Rollback", first.GetProperty("section").GetString());
        Assert.Equal("Engineering", first.GetProperty("source").GetString());
        var issue = await SearchAsync(alice, "decoder drops frames ring buffer");
        Assert.Equal("https://gitlab.test/group/app/-/issues/12", Links(issue)[0]);
        Assert.Contains("widening the ring buffer", issue.GetProperty("results")[0].GetProperty("passage").GetString(), StringComparison.Ordinal);
        // A Guest too (GitLab's username Dave is the person dave).
        Assert.Contains("https://gitlab.test/group/app/-/wikis/deploy", Links(await SearchAsync(dave, "roll back a deploy")));
        Assert.DoesNotContain(Links(await SearchAsync(alice, "acquisition codename")), l => l.Contains("group/secret", StringComparison.Ordinal));

        // Not a member, a blocked member, an admin who is no member: nothing from it.
        foreach (var outsider in new[] { carol, erin, admin })
        {
            var none = await SearchAsync(outsider, "how do I roll back a deploy");
            Assert.Empty(none.GetProperty("results").EnumerateArray());
            Assert.Contains("Nothing the person may read matches", none.GetProperty("note").GetString(), StringComparison.Ordinal);
        }
        // bob reads group/secret's and only that.
        var secret = await SearchAsync(bob, "acquisition codename");
        Assert.Equal(["https://gitlab.test/group/secret/-/wikis/plan"], Links(secret));

        // The admin page; every change audited.
        var page = await admin.JsonAsync(await admin.GetAsync("/api/admin/knowledge"));
        Assert.True(page.GetProperty("embedder").GetBoolean());
        Assert.True(page.GetProperty("gitlabBot").GetBoolean());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "knowledge.add" && e.GetProperty("target").GetString() == "Engineering");
    }

    [Fact]
    public async Task A_GitLab_source_reads_only_what_changed_and_drops_what_is_gone_and_who_left()
    {
        app.Mcp.ResetKnowledge();
        await using var f = NewApp();
        var admin = await AdminAsync(f);
        var (alice, _) = await PersonAsync(f, admin, "alice");
        var (id, first) = await AddAsync(admin, new { name = "App", kind = "gitlab", location = "group/app", wiki = true, issues = true });
        Assert.Equal(3, first.GetProperty("documents").GetInt32());

        // Nothing moved in GitLab: nothing is read again or embedded, and nothing goes.
        var embedded = app.Embedder.Texts;
        var reads = app.Mcp.GitLabCalls.Count(c => c.PathAndQuery.Contains("/wikis", StringComparison.Ordinal));
        var again = await SyncAgainAsync(admin, id);
        Assert.Equal(3, again.GetProperty("documents").GetInt32());
        Assert.Equal(embedded, app.Embedder.Texts);
        Assert.Equal(reads, app.Mcp.GitLabCalls.Count(c => c.PathAndQuery.Contains("/wikis", StringComparison.Ordinal)));

        // A page edited, a page deleted, the project active: only the edited page is embedded again, the deleted one goes.
        var project = app.Mcp.Projects[7];
        project.Wiki.RemoveAll(w => w.Slug == "onboarding");
        project.Wiki[0] = ("deploy", "Deploy", "## Rollback\n\nTo roll back a deploy, run make undo-release with the release tag.");
        project.Activity = "2026-10-02T10:00:00Z";
        var passagesBefore = again.GetProperty("passages").GetInt32();
        var changed = await SyncAgainAsync(admin, id);
        Assert.Equal(2, changed.GetProperty("documents").GetInt32());
        Assert.True(app.Embedder.Texts - embedded is > 0 and <= 2, $"embedded {app.Embedder.Texts - embedded} passages again");
        Assert.True(changed.GetProperty("passages").GetInt32() < passagesBefore);
        var found = await SearchAsync(alice, "roll back a deploy");
        Assert.Contains("undo-release", found.GetProperty("results")[0].GetProperty("passage").GetString(), StringComparison.Ordinal);

        // alice leaves the project: at the next read of its members, she reads nothing of it.
        project.Members.RemoveAll(m => m.Username == "alice");
        var left = await SyncAgainAsync(admin, id);
        Assert.Empty((await SearchAsync(alice, "roll back a deploy")).GetProperty("results").EnumerateArray());

        // Set to a project the bot cannot read (moved, or the bot taken out of it): the sync fails, says why, and keeps what it had.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/knowledge/{id}", UriKind.Relative), new { location = "group/nothing" }));
        var failed = await SyncedAsync(admin, id, left.GetProperty("syncedAt").GetDateTimeOffset());
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Contains("no project or group group/nothing", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, failed.GetProperty("documents").GetInt32());

        // Removed: its documents and passages go with it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/knowledge/{id}", UriKind.Relative)));
        Assert.Empty((await admin.JsonAsync(await admin.GetAsync("/api/admin/knowledge"))).GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task A_folder_and_a_website_are_read_by_the_groups_an_admin_chose()
    {
        var root = Directory.CreateTempSubdirectory("knowledge-root-").FullName;
        try
        {
            var handbook = Directory.CreateDirectory(Path.Combine(root, "handbook")).FullName;
            Directory.CreateDirectory(Path.Combine(handbook, "policies"));
            await File.WriteAllTextAsync(Path.Combine(handbook, "policies", "leave.md"), "# Leave\n\nEveryone gets 30 days of paid leave a year, booked in the HR portal.\n");
            await File.WriteAllTextAsync(Path.Combine(handbook, "travel.html"), "<html><head><title>Travel</title></head><body><main><h1>Travel</h1><p>Book trains for trips under four hours.</p></main></body></html>");
            await File.WriteAllTextAsync(Path.Combine(handbook, ".secret.md"), "The hidden budget is 9 million.");
            Directory.CreateDirectory(Path.Combine(handbook, ".git"));
            await File.WriteAllTextAsync(Path.Combine(handbook, ".git", "config"), "the repository's own settings");
            await File.WriteAllBytesAsync(Path.Combine(handbook, "logo.bin"), [0, 1, 2, 0, 3]);
            app.Web.Html("https://docs.test/", "<html><head><title>Docs</title></head><body><a href=\"/guide\">Guide</a> <a href=\"https://other.test/x\">elsewhere</a> <a href=\"/private/keys\">keys</a> <a href=\"/logo.png\">logo</a></body></html>");
            app.Web.Html("https://docs.test/guide", "<html><head><title>Guide</title></head><body><main><h1>Guide</h1><p>The staging cluster is reset every Sunday night.</p><a href=\"/#top\">home</a></main></body></html>");
            app.Web.Html("https://docs.test/private/keys", "<html><body>Private keys live here.</body></html>");
            app.Web.Pages["https://docs.test/robots.txt"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: *\nDisallow: /private\n") };

            // Without pgvector too: the same answers, in plain SQL.
            await using var f = NewApp(new() { ["Knowledge:FolderRoot"] = root, ["Knowledge:PgVector"] = "false" });
            var admin = await AdminAsync(f);
            var (hanna, hannaId) = await PersonAsync(f, admin, "hanna");
            var (ivan, _) = await PersonAsync(f, admin, "ivan");
            var hr = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "HR" }))).GetProperty("id").GetGuid();
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{hr}/members", new { userIds = new[] { hannaId } }));

            // A folder outside the root, and chosen groups with none chosen, are refused.
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/knowledge", new { name = "Etc", kind = "folder", location = "/etc" }));
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/knowledge", new { name = "Handbook", kind = "folder", location = handbook, audience = "Groups" }));

            var (folder, synced) = await AddAsync(admin, new { name = "Handbook", kind = "folder", location = handbook, audience = "Groups", groups = new[] { hr } });
            Assert.Equal("synced", synced.GetProperty("state").GetString());
            var titles = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{folder}/documents"))).EnumerateArray()
                .Where(d => d.GetProperty("passages").GetInt32() > 0).Select(d => d.GetProperty("title").GetString()).ToList();
            Assert.Equal(["policies/leave.md", "travel.html"], titles);
            var leave = await SearchAsync(hanna, "how many days of paid leave");
            Assert.Equal("policies/leave.md", leave.GetProperty("results")[0].GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, leave.GetProperty("results")[0].GetProperty("link").ValueKind);
            Assert.Empty((await SearchAsync(ivan, "how many days of paid leave")).GetProperty("results").EnumerateArray());

            var (site, crawled) = await AddAsync(admin, new { name = "Docs", kind = "website", location = "https://docs.test/", maxPages = 20 });
            Assert.Equal("synced", crawled.GetProperty("state").GetString());
            var pages = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/knowledge/{site}/documents"))).EnumerateArray().Select(d => d.GetProperty("url").GetString()).Order().ToList();
            // Its host only, not what robots.txt keeps out, not pictures.
            Assert.Equal(["https://docs.test/", "https://docs.test/guide"], pages);
            Assert.DoesNotContain(app.Web.Requests, r => r.Host == "other.test" || r.AbsolutePath.StartsWith("/private", StringComparison.Ordinal) || r.AbsolutePath.EndsWith(".png", StringComparison.Ordinal));
            var guide = await SearchAsync(ivan, "when is the staging cluster reset");
            Assert.Equal("https://docs.test/guide", Links(guide)[0]);

            // Readers changed: at once, for every page.
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/knowledge/{site}", UriKind.Relative), new { audience = "Admins" }));
            Assert.Empty((await SearchAsync(ivan, "when is the staging cluster reset")).GetProperty("results").EnumerateArray());
            Assert.Equal("https://docs.test/guide", Links(await SearchAsync(admin, "when is the staging cluster reset"))[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Without_the_embedder_there_is_no_knowledge_and_files_go_in_as_before()
    {
        await using var f = NewApp(new() { ["Modules:embed"] = "false" });
        var admin = await AdminAsync(f);
        var page = await admin.JsonAsync(await admin.GetAsync("/api/admin/knowledge"));
        Assert.False(page.GetProperty("embedder").GetBoolean());
        Assert.Contains("embedder", page.GetProperty("problem").GetString(), StringComparison.Ordinal);
        var res = await admin.PostAsync("/api/admin/knowledge", new { name = "App", kind = "gitlab", location = "group/app" });
        var id = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        var source = await SyncedAsync(admin, id);
        Assert.Equal("failed", source.GetProperty("state").GetString());
        Assert.Contains("embedder", source.GetProperty("error").GetString(), StringComparison.Ordinal);
        var tool = (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == "knowledge");
        Assert.Contains("embedder", tool.GetProperty("unavailable").GetString(), StringComparison.Ordinal);
    }

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
    }

    private async Task<(string System, string Question)> AskAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        var messages = app.Model.Requests.Last().Body["messages"]!.AsArray();
        return (messages[0]!["content"]!.GetValue<string>(), messages.Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>());
    }

    /// <summary>A file of a codec's notes: a line on its frame header, and filler of its own words.</summary>
    private static string Notes(int n) =>
        $"# Codec {n} notes\n\nThe frame header of codec{n} is {n + 10} bytes and starts with the marker M{n}.\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} of codec{n}: benchmark run{n}x{i} measured throughput{n} on machine{n}{i} with buffers{n}{i}."));

    [Fact]
    public async Task An_assistant_with_many_files_answers_about_any_of_them_without_the_files_inlined()
    {
        await using var f = NewApp(new() { ["Chat:InlineAttachmentChars"] = "2000" });
        var admin = await AdminAsync(f);
        var (b, _) = await PersonAsync(f, admin, "pat");
        var assistant = (await b.JsonAsync(await b.PostAsync("/api/assistants", new { name = "Codecs" }))).GetProperty("id").GetGuid();
        for (var n = 1; n <= 60; n++)
        {
            await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/assistants/{assistant}/files", new { attachmentId = await UploadAsync(b, $"notes-{n:00}.txt", Notes(n)) }));
        }
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { assistantId = assistant, tools = new[] { "files" } }))).GetProperty("id").GetGuid();

        foreach (var n in new[] { 7, 53 })
        {
            var (system, question) = await AskAsync(b, chat, $"How big is the frame header of codec{n}?");
            // The files are named, not inlined.
            Assert.Contains($"[assistant file: notes-{n:00}.txt (", system, StringComparison.Ordinal);
            Assert.DoesNotContain("<assistant_file", system, StringComparison.Ordinal);
            Assert.DoesNotContain("frame header of codec", system, StringComparison.Ordinal);
            // The question carries the passage that answers it, with the file's name and lines.
            Assert.Contains($"<passage file=\"notes-{n:00}.txt\" lines=\"1-", question, StringComparison.Ordinal);
            Assert.Contains($"The frame header of codec{n} is {n + 10} bytes", question, StringComparison.Ordinal);
            Assert.True(question.Length < 14_000, $"the question is {question.Length} characters");
        }

        // A long attachment: its start where it was attached, and the passage about the question.
        var other = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools = new[] { "files" } }))).GetProperty("id").GetGuid();
        var log = string.Join("\n", Enumerable.Range(1, 600).Select(i => i == 480 ? "line 480: the nightly backup failed because the disk quota was exceeded" : $"line {i}: job{i} finished in {i % 7} seconds"));
        var file = await UploadAsync(b, "jobs.log", log);
        var res = await b.PostAsync($"/api/chat/conversations/{other}/messages", new { content = "Why did the nightly backup fail?", attachments = new[] { file } });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        var asked = app.Model.Requests.Last().Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("line 1: job1", asked, StringComparison.Ordinal);
        Assert.Contains("the passages that match each question come with the question", asked, StringComparison.Ordinal);
        Assert.Contains("<passage file=\"jobs.log\"", asked, StringComparison.Ordinal);
        Assert.Contains("disk quota was exceeded", asked, StringComparison.Ordinal);
    }
}

/// <summary>Cutting documents into passages, and what a crawl reads, without an app.</summary>
public sealed class ChunkerTests
{
    [Fact]
    public void A_document_is_cut_at_headings_then_paragraphs_with_its_lines_and_headings()
    {
        var text = "# Deploy\n\nIntro line.\n\n## Rollback\n\n" + string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Step {i}: " + new string('x', 150))) + "\n\n## Releases\n\nOn Tuesdays.";
        var chunks = Chunker.Split(text);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= Chunker.Target + 200, $"{c.Text.Length} characters"));
        // The short intro stays with the section after it; the passage is under the heading most of it is under.
        Assert.StartsWith("# Deploy", chunks[0].Text, StringComparison.Ordinal);
        Assert.Equal("Deploy › Rollback", chunks[0].Heading);
        Assert.Contains(chunks, c => c.Heading == "Deploy › Rollback" && c.Text.Contains("Step 1:", StringComparison.Ordinal));
        var last = chunks[^1];
        Assert.Equal("Deploy › Releases", last.Heading);
        Assert.StartsWith("## Releases", last.Text, StringComparison.Ordinal);
        Assert.Equal(text.Split('\n').Length, last.EndLine);
        // Cut for size, a passage starts with the paragraph before (so nothing on the cut is lost).
        var rollback = chunks.Where(c => c.Heading == "Deploy › Rollback").ToList();
        Assert.True(rollback.Count >= 2);
        var lastStepOfFirst = rollback[0].Text.Split("\n\n")[^1];
        Assert.StartsWith(lastStepOfFirst, rollback[1].Text, StringComparison.Ordinal);
        // Its lines: the line a passage starts on holds its first words.
        var lines = text.Split('\n');
        Assert.All(chunks, c => Assert.StartsWith(lines[c.Line - 1], c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_paragraph_longer_than_a_passage_is_cut_at_line_ends_and_a_code_block_is_not_split_at_blank_lines()
    {
        var log = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}: something happened here"));
        var chunks = Chunker.Split(log);
        Assert.True(chunks.Count > 2);
        Assert.Equal(1, chunks[0].Line);
        Assert.Equal(100, chunks[^1].EndLine);
        Assert.All(chunks.Zip(chunks.Skip(1)), p => Assert.True(p.Second.Line >= p.First.EndLine, "in order"));
        var code = Chunker.Split("```\na = 1\n\nb = 2\n```");
        Assert.Single(code);
    }

    [Fact]
    public void Robots_txt_and_links_decide_what_a_crawl_reads()
    {
        Assert.Equal(["/private", "/tmp/"], WebsiteConnector.Disallowed("User-agent: Googlebot\nDisallow: /\n\nUser-agent: *\nDisallow: /private\nDisallow: /tmp/*\nAllow: /\n"));
        var links = WebsiteConnector.Links("<a href=\"/a#x\">a</a><a class='b' href='b.html'>b</a><a href=\"mailto:x@y\">m</a><a href=\"/p.png\">p</a>", new Uri("https://docs.test/dir/page"))
            .Select(u => u.AbsoluteUri).ToList();
        Assert.Equal(["https://docs.test/a", "https://docs.test/dir/b.html"], links);
    }
}
