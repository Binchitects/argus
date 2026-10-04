using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>
/// Assistants (projects, grown up): instructions and files every answer of their chats reads,
/// the settings a new chat starts with, and sharing with groups or the company.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class AssistantTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private async Task<(Guid Id, TestBrowser Browser)> PersonAsync(TestBrowser admin, bool isAdmin = false, string? userName = null)
    {
        var name = userName ?? "as" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = $"Person {name}", admin = isAdmin }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!));
    }

    private static async Task<Guid> GroupAsync(TestBrowser admin, params Guid[] members)
    {
        var res = await admin.PostAsync("/api/admin/groups", new { name = "Team " + Guid.NewGuid().ToString("N")[..8] });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var id = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        if (members.Length > 0)
        {
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = members }));
        }
        return id;
    }

    private static async Task<Guid> CreateAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/assistants", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> SendAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        await res.Content.LoadIntoBufferAsync();
        return res;
    }

    private static async Task SentAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await SendAsync(b, chat, text);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> ChatAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/chat/conversations", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PatchAsync(TestBrowser b, string path, object body) => b.Http.PatchAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private static Task<HttpResponseMessage> PutAsync(TestBrowser b, string path, object body) => b.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private string SystemPrompt() => app.Model.Requests.Last().Body["messages"]![0]!["content"]!.GetValue<string>();

    private static async Task<List<Guid>> GalleryAsync(TestBrowser b, string? q = null) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/assistants" + (q is null ? "" : "?q=" + Uri.EscapeDataString(q))))).EnumerateArray().Select(x => x.GetProperty("id").GetGuid())];

    [Fact]
    public async Task A_private_assistant_gives_its_chats_its_instructions_and_files_and_nobody_else_sees_it()
    {
        var admin = await AdminAsync();
        var (_, b) = await PersonAsync(admin);
        var (_, other) = await PersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/assistants", new { name = " " }));
        var id = await CreateAsync(b, new
        {
            name = "Codec rewrite", description = "The new decoder", instructions = "Answer as a codec engineer.",
            starters = new[] { "Review my decoder", "Explain the frame header" }, icon = "code", color = "green",
        });
        var facts = await UploadAsync(b, "facts.txt", "The frame header is 12 bytes.\n");
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/assistants/{id}/files", new { attachmentId = facts }));

        // A chat with it: the answer reads its instructions and its file; the chat shows its starters.
        var chat = await ChatAsync(b, new { assistantId = id, tools = new[] { "files" } });
        await SentAsync(b, chat, "How big is a frame header?");
        Assert.Contains("assistant \"Codec rewrite\". Its instructions:\nAnswer as a codec engineer.", SystemPrompt(), StringComparison.Ordinal);
        Assert.Contains("<assistant_file name=\"facts.txt\">\nThe frame header is 12 bytes.", SystemPrompt(), StringComparison.Ordinal);
        var shown = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("assistant");
        Assert.Equal("Codec rewrite", shown.GetProperty("name").GetString());
        Assert.Equal("Review my decoder", shown.GetProperty("starters")[0].GetString());
        Assert.False(shown.GetProperty("noAccess").GetBoolean());

        // Its tools have the file too.
        await SentAsync(b, chat, "Read it [call read_file {\"file\":\"facts.txt\"}]");
        var tool = app.Model.Requests.Last().Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
        Assert.Contains("12 bytes", tool, StringComparison.Ordinal);

        var listed = (await b.JsonAsync(await b.GetAsync("/api/assistants"))).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal(1, listed.GetProperty("myChats").GetInt32());
        Assert.Equal(1, listed.GetProperty("chats").GetInt32());
        Assert.Equal(1, listed.GetProperty("people").GetInt32());
        Assert.Equal(1, listed.GetProperty("files").GetInt32());
        Assert.Equal("Private", listed.GetProperty("reach").GetString());
        Assert.True(listed.GetProperty("mine").GetBoolean());
        var full = await b.JsonAsync(await b.GetAsync($"/api/assistants/{id}"));
        Assert.Equal("facts.txt", full.GetProperty("files")[0].GetProperty("fileName").GetString());
        Assert.Equal(chat, full.GetProperty("chats")[0].GetProperty("id").GetGuid());
        Assert.Equal("code", full.GetProperty("icon").GetString());
        Assert.Equal([chat], (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations?assistant={id}"))).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));

        // Someone else: nothing to see, change or use, its file included.
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/assistants/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await PatchAsync(other, $"/api/assistants/{id}", new { name = "mine" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await other.PostAsync("/api/chat/conversations", new { assistantId = id }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/attachments/{facts}/content"));
        Assert.DoesNotContain(id, await GalleryAsync(other));
        var theirs = await UploadAsync(other, "x.txt", "x");
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync($"/api/assistants/{id}/files", new { attachmentId = theirs }));

        // Away from the assistant: the next answer reads neither.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(b, $"/api/chat/conversations/{chat}", new { assistantId = Guid.Empty }));
        await SentAsync(b, chat, "And now?");
        Assert.DoesNotContain("Codec rewrite", SystemPrompt(), StringComparison.Ordinal);

        // Removed: its chats stay, without it, unless they go too.
        await PatchAsync(b, $"/api/chat/conversations/{chat}", new { assistantId = id });
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/assistants/{id}", UriKind.Relative)));
        Assert.Equal(JsonValueKind.Null, (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("assistant").ValueKind);
        var second = await CreateAsync(b, new { name = "Short lived" });
        var doomed = await ChatAsync(b, new { assistantId = second });
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/assistants/{second}?chats=delete", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/conversations/{doomed}"));
    }

    [Fact]
    public async Task An_assistant_shared_with_a_group_is_usable_by_its_members_and_invisible_to_others()
    {
        var admin = await AdminAsync();
        var (ownerId, owner) = await PersonAsync(admin);
        var (memberId, member) = await PersonAsync(admin);
        var (_, outsider) = await PersonAsync(admin);
        var team = await GroupAsync(admin, ownerId, memberId);
        var elsewhere = await GroupAsync(admin);
        var id = await CreateAsync(owner, new { name = "Release helper", instructions = "Write release notes in our style.", starters = new[] { "Notes for this week" } });
        var style = await UploadAsync(owner, "style.md", "Short sentences. No emojis.\n");
        await StatusAssert.Is(HttpStatusCode.NoContent, await owner.PostAsync($"/api/assistants/{id}/files", new { attachmentId = style }));

        // Private until shared; only admins share with the whole company; a person shares with their own groups.
        await StatusAssert.Is(HttpStatusCode.NotFound, await member.GetAsync($"/api/assistants/{id}"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(owner, $"/api/assistants/{id}/sharing", new { reach = "Company" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(owner, $"/api/assistants/{id}/sharing", new { reach = "Groups", groups = new[] { elsewhere } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PutAsync(owner, $"/api/assistants/{id}/sharing", new { reach = "Groups", groups = Array.Empty<Guid>() }));
        var mine = await owner.JsonAsync(await owner.GetAsync("/api/sharing/groups"));
        Assert.Equal([team], mine.EnumerateArray().Select(g => g.GetProperty("id").GetGuid()));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(owner, $"/api/assistants/{id}/sharing", new { reach = "Groups", groups = new[] { team } }));

        // A member: it is in the gallery, its page opens, a chat with it reads it, its file opens.
        var listed = (await member.JsonAsync(await member.GetAsync("/api/assistants?q=release"))).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.False(listed.GetProperty("canEdit").GetBoolean());
        Assert.False(listed.GetProperty("mine").GetBoolean());
        Assert.StartsWith("Person as", listed.GetProperty("owner").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(id, await GalleryAsync(member, "no such words"));
        var page = await member.JsonAsync(await member.GetAsync($"/api/assistants/{id}"));
        Assert.Equal("Write release notes in our style.", page.GetProperty("instructions").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("sharing").ValueKind);
        var chat = await ChatAsync(member, new { assistantId = id });
        await SentAsync(member, chat, "Notes for this week");
        Assert.Contains("Write release notes in our style.", SystemPrompt(), StringComparison.Ordinal);
        Assert.Contains("<assistant_file name=\"style.md\">\nShort sentences.", SystemPrompt(), StringComparison.Ordinal);
        var file = await member.GetAsync($"/api/chat/attachments/{style}/content");
        await StatusAssert.Is(HttpStatusCode.OK, file);
        Assert.Contains("Short sentences", await file.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(member, $"/api/assistants/{id}", new { name = "Mine now" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(member, $"/api/assistants/{id}/sharing", new { reach = "Private" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.Http.DeleteAsync(new Uri($"/api/assistants/{id}", UriKind.Relative)));

        // Its owner sees it used: one chat started, one person in the last 30 days (their own chats only).
        var usage = await owner.JsonAsync(await owner.GetAsync($"/api/assistants/{id}"));
        Assert.Equal(1, usage.GetProperty("usage").GetProperty("chats").GetInt32());
        Assert.Equal(1, usage.GetProperty("usage").GetProperty("people").GetInt32());
        Assert.Empty(usage.GetProperty("chats").EnumerateArray());
        Assert.Equal([team], usage.GetProperty("sharing").GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()));

        // Someone outside the group: it does not exist for them.
        Assert.DoesNotContain(id, await GalleryAsync(outsider));
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.GetAsync($"/api/assistants/{id}"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await outsider.PostAsync("/api/chat/conversations", new { assistantId = id }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.GetAsync($"/api/chat/attachments/{style}/content"));
        var loose = await ChatAsync(outsider, new { });
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PatchAsync(outsider, $"/api/chat/conversations/{loose}", new { assistantId = id }));

        // Out of the group: the member's chat says so and answers no more, until it goes on without the assistant.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{team}/members/{memberId}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await member.GetAsync($"/api/assistants/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await member.GetAsync($"/api/chat/attachments/{style}/content"));
        Assert.True((await member.JsonAsync(await member.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("assistant").GetProperty("noAccess").GetBoolean());
        var refused = await SendAsync(member, chat, "More notes");
        await StatusAssert.Is(HttpStatusCode.Forbidden, refused);
        Assert.Contains("no longer have access to the assistant Release helper", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(member, $"/api/chat/conversations/{chat}", new { assistantId = Guid.Empty }));
        await SentAsync(member, chat, "More notes");
        Assert.DoesNotContain("release notes in our style", SystemPrompt(), StringComparison.Ordinal);

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() == "assistant.share").Select(e => e.GetProperty("target").GetString());
        Assert.Contains("Release helper", audit);
    }

    [Fact]
    public async Task Editors_change_an_assistant_and_only_admins_make_one_company_wide()
    {
        var admin = await AdminAsync();
        var (ownerId, owner) = await PersonAsync(admin);
        var editorName = "ed" + Guid.NewGuid().ToString("N")[..8];
        var (editorId, editor) = await PersonAsync(admin, userName: editorName);
        var (writerId, writer) = await PersonAsync(admin);
        var (_, anyone) = await PersonAsync(admin);
        var (_, otherAdmin) = await PersonAsync(admin, isAdmin: true);
        var writers = await GroupAsync(admin, ownerId, writerId);
        var id = await CreateAsync(owner, new { name = "Style guide" });
        var people = await owner.JsonAsync(await owner.GetAsync($"/api/sharing/people?q={editorName}"));
        Assert.Contains(editorId, people.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(owner, $"/api/assistants/{id}/sharing", new { reach = "Private", editorPeople = new[] { editorId }, editorGroups = new[] { writers } }));

        // Editors change it and add files of their own; they neither share it nor remove it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(editor, $"/api/assistants/{id}", new { instructions = "Use British spelling.", starters = new[] { "Check this paragraph" } }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(writer, $"/api/assistants/{id}", new { description = "House style" }));
        var rules = await UploadAsync(editor, "rules.txt", "Colour, not color.\n");
        await StatusAssert.Is(HttpStatusCode.NoContent, await editor.PostAsync($"/api/assistants/{id}/files", new { attachmentId = rules }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(editor, $"/api/assistants/{id}/sharing", new { reach = "Private" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await editor.Http.DeleteAsync(new Uri($"/api/assistants/{id}", UriKind.Relative)));
        var page = await editor.JsonAsync(await editor.GetAsync($"/api/assistants/{id}"));
        Assert.True(page.GetProperty("canEdit").GetBoolean());
        Assert.False(page.GetProperty("canShare").GetBoolean());
        Assert.Equal("House style", page.GetProperty("description").GetString());
        Assert.Equal([editorId], page.GetProperty("sharing").GetProperty("editorPeople").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        var chat = await ChatAsync(owner, new { assistantId = id });
        await SentAsync(owner, chat, "Check this paragraph");
        Assert.Contains("Use British spelling.", SystemPrompt(), StringComparison.Ordinal);
        Assert.Contains("Colour, not color.", SystemPrompt(), StringComparison.Ordinal);

        // Private: nobody else sees it, admins included.
        await StatusAssert.Is(HttpStatusCode.NotFound, await anyone.GetAsync($"/api/assistants/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/assistants/{id}"));

        // An admin's assistant for the company: everyone uses it, and another admin looks after it.
        var handbook = await CreateAsync(admin, new { name = "Company handbook", instructions = "Answer from the handbook." });
        await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(admin, $"/api/assistants/{handbook}/sharing", new { reach = "Company" }));
        Assert.Contains(handbook, await GalleryAsync(anyone));
        await ChatAsync(anyone, new { assistantId = handbook });
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PatchAsync(anyone, $"/api/assistants/{handbook}", new { name = "Mine" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PatchAsync(otherAdmin, $"/api/assistants/{handbook}", new { description = "Policies and benefits" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await otherAdmin.Http.DeleteAsync(new Uri($"/api/assistants/{handbook}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await anyone.GetAsync($"/api/assistants/{handbook}"));
    }

    [Fact]
    public async Task A_chat_with_an_assistant_starts_with_its_model_thinking_and_tools()
    {
        var admin = await AdminAsync();
        var (_, b) = await PersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/assistants", new { name = "x", thinking = "nope" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/assistants", new { name = "x", tools = new[] { "nope" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/assistants", new { name = "x", model = "nope" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/assistants", new { name = "x", starters = new[] { "a", "b", "c", "d", "e" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/assistants", new { name = "x", icon = "unicorn" }));
        var id = await CreateAsync(b, new { name = "Numbers", model = "Qwen3.8-Flash-Next", thinking = "low", tools = new[] { "calculator" } });

        var made = await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { assistantId = id }));
        Assert.Equal("Qwen3.8-Flash-Next", made.GetProperty("model").GetString());
        Assert.Equal("low", made.GetProperty("thinking").GetString());
        Assert.Equal(["calculator"], made.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("Numbers", made.GetProperty("assistant").GetProperty("name").GetString());

        // What the person chose for this chat wins.
        var own = await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { assistantId = id, thinking = "xhigh", tools = new[] { "files" } }));
        Assert.Equal("xhigh", own.GetProperty("thinking").GetString());
        Assert.Equal(["files"], own.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        var listed = (await b.JsonAsync(await b.GetAsync("/api/assistants"))).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal(2, listed.GetProperty("chats").GetInt32());
    }
}
