using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Shared chats: a read-only link for the company or chosen groups, forked into one's own chats, revoked in one click.</summary>
[Collection(nameof(AppCollection))]
public sealed class ShareTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private async Task<(Guid Id, TestBrowser Browser)> PersonAsync(TestBrowser admin)
    {
        var name = "sh" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = $"Sharer {name}" }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!));
    }

    private static async Task<Guid> GroupAsync(TestBrowser admin, params Guid[] members)
    {
        var res = await admin.PostAsync("/api/admin/groups", new { name = "Team " + Guid.NewGuid().ToString("N")[..8] });
        var id = (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = members }));
        return id;
    }

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
    }

    private static async Task SendAsync(TestBrowser b, Guid chat, object message)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", message);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
    }

    private static async Task<Guid> ChatAsync(TestBrowser b) => (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();

    private static async Task<Guid> ShareAsync(TestBrowser b, Guid chat, object body)
    {
        var res = await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/share", UriKind.Relative), body);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_shared_chat_opens_for_a_colleague_and_not_for_someone_outside_the_group_and_revoking_stops_it()
    {
        var admin = await AdminAsync();
        var (ownerId, owner) = await PersonAsync(admin);
        var (colleagueId, colleague) = await PersonAsync(admin);
        var (_, outsider) = await PersonAsync(admin);
        var team = await GroupAsync(admin, ownerId, colleagueId);
        var chat = await ChatAsync(owner);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await owner.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/share", UriKind.Relative), new { reach = "Company" }));
        var notes = await UploadAsync(owner, "notes.txt", "The launch is on Friday.\n");
        await SendAsync(owner, chat, new { content = "When is the launch?", attachments = new[] { notes } });
        Assert.Equal(JsonValueKind.Null, (await owner.JsonAsync(await owner.GetAsync($"/api/chat/conversations/{chat}/share"))).GetProperty("share").ValueKind);

        // Only the owner shares it, and with groups they are in.
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/share", UriKind.Relative), new { reach = "Company" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await owner.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/share", UriKind.Relative), new { reach = "Groups" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await owner.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/share", UriKind.Relative),
            new { reach = "Groups", groups = new[] { await GroupAsync(admin, colleagueId) } }));
        var link = await ShareAsync(owner, chat, new { reach = "Groups", groups = new[] { team } });

        // A colleague in the group: the messages and the file, read-only.
        var seen = await colleague.JsonAsync(await colleague.GetAsync($"/api/shared/{link}"));
        Assert.Equal("When is the launch?", seen.GetProperty("title").GetString());
        Assert.StartsWith("Sharer sh", seen.GetProperty("owner").GetString(), StringComparison.Ordinal);
        Assert.False(seen.GetProperty("mine").GetBoolean());
        var messages = seen.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("notes.txt", messages[0].GetProperty("attachments")[0].GetProperty("fileName").GetString());
        var file = await colleague.GetAsync($"/api/chat/attachments/{notes}/content");
        await StatusAssert.Is(HttpStatusCode.OK, file);
        Assert.Contains("Friday", await file.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // The chat itself stays the owner's: no writing in it, no answering its tools.
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.GetAsync($"/api/chat/conversations/{chat}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Hi" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.PostAsync($"/api/chat/conversations/{chat}/tool-calls/x", new { allow = true }));

        // Someone outside the group, or signed out: nothing.
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.GetAsync($"/api/shared/{link}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.GetAsync($"/api/chat/attachments/{notes}/content"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.PostAsync($"/api/shared/{link}/fork"));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await new TestBrowser(app.Factory).GetAsync($"/api/shared/{link}"));

        // The owner sees how many opened it (their own visits do not count).
        await colleague.GetAsync($"/api/shared/{link}");
        Assert.True((await owner.JsonAsync(await owner.GetAsync($"/api/shared/{link}"))).GetProperty("mine").GetBoolean());
        var mine = (await owner.JsonAsync(await owner.GetAsync($"/api/chat/conversations/{chat}/share"))).GetProperty("share");
        Assert.Equal(2, mine.GetProperty("opens").GetInt32());
        Assert.Equal(1, mine.GetProperty("people").GetInt32());
        Assert.Equal([team], mine.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()));

        // Changing it keeps its address; revoking stops it at once.
        Assert.Equal(link, await ShareAsync(owner, chat, new { reach = "Company" }));
        await StatusAssert.Is(HttpStatusCode.OK, await outsider.GetAsync($"/api/shared/{link}"));
        await StatusAssert.Is(HttpStatusCode.NoContent, await owner.Http.DeleteAsync(new Uri($"/api/chat/conversations/{chat}/share", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.GetAsync($"/api/shared/{link}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.GetAsync($"/api/chat/attachments/{notes}/content"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await owner.Http.DeleteAsync(new Uri($"/api/chat/conversations/{chat}/share", UriKind.Relative)));

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()
            .Where(e => e.GetProperty("target").GetString() == $"chat {chat}").Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("chat.share", audit);
        Assert.Contains("chat.unshare", audit);
    }

    [Fact]
    public async Task A_shared_chat_forks_into_the_colleagues_own_chats_with_copies_of_its_files()
    {
        var admin = await AdminAsync();
        var (_, owner) = await PersonAsync(admin);
        var (_, colleague) = await PersonAsync(admin);
        var chat = await ChatAsync(owner);
        await owner.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{chat}", UriKind.Relative), new { systemPrompt = "My private instructions." });
        var plan = await UploadAsync(owner, "plan.txt", "Step one: measure.\n");
        await SendAsync(owner, chat, new { content = "Read the plan", attachments = new[] { plan } });
        await SendAsync(owner, chat, new { content = "And then?" });
        var link = await ShareAsync(owner, chat, new { reach = "Company" });

        var res = await colleague.PostAsync($"/api/shared/{link}/fork");
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var forkId = (await colleague.JsonAsync(res)).GetProperty("id").GetGuid();
        var fork = await colleague.JsonAsync(await colleague.GetAsync($"/api/chat/conversations/{forkId}"));
        Assert.Equal("Read the plan (fork)", fork.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, fork.GetProperty("systemPrompt").ValueKind);
        Assert.Equal(4, fork.GetProperty("messages").GetArrayLength());
        var copy = fork.GetProperty("messages")[0].GetProperty("attachments")[0].GetProperty("id").GetGuid();
        Assert.NotEqual(plan, copy);

        // The copy is the colleague's own: it stays after the link is revoked and the chat deleted.
        await owner.Http.DeleteAsync(new Uri($"/api/chat/conversations/{chat}", UriKind.Relative));
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.GetAsync($"/api/shared/{link}"));
        Assert.Contains("measure", await (await colleague.GetAsync($"/api/chat/attachments/{copy}/content")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await SendAsync(colleague, forkId, new { content = "What was step one?" });
        var asked = app.Model.Requests.Last().Body["messages"]!.ToJsonString();
        Assert.Contains("Step one: measure.", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("My private instructions.", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shared_branch_shows_that_branch_only()
    {
        var admin = await AdminAsync();
        var (_, owner) = await PersonAsync(admin);
        var (_, colleague) = await PersonAsync(admin);
        var chat = await ChatAsync(owner);
        var first = await UploadAsync(owner, "first.txt", "First draft.\n");
        await SendAsync(owner, chat, new { content = "Draft one", attachments = new[] { first } });
        // An edit of the first question: a second branch, now on screen.
        await SendAsync(owner, chat, new { content = "Draft two", root = true });
        var conversation = await owner.JsonAsync(await owner.GetAsync($"/api/chat/conversations/{chat}"));
        var leaf = conversation.GetProperty("currentLeafId").GetGuid();

        var link = await ShareAsync(owner, chat, new { reach = "Company", branch = true });
        var seen = await colleague.JsonAsync(await colleague.GetAsync($"/api/shared/{link}"));
        Assert.True(seen.GetProperty("branch").GetBoolean());
        Assert.Equal(leaf, seen.GetProperty("currentLeafId").GetGuid());
        Assert.Equal(["Draft two", "Answer to: Draft two"], seen.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await colleague.GetAsync($"/api/chat/attachments/{first}/content"));
        var other = conversation.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("content").GetString() == "Draft one").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await colleague.PostAsync($"/api/shared/{link}/fork", new { messageId = other }));

        // The whole chat: both branches, and the first one's file.
        await ShareAsync(owner, chat, new { reach = "Company" });
        var whole = await colleague.JsonAsync(await colleague.GetAsync($"/api/shared/{link}"));
        Assert.False(whole.GetProperty("branch").GetBoolean());
        Assert.Equal(4, whole.GetProperty("messages").GetArrayLength());
        await StatusAssert.Is(HttpStatusCode.OK, await colleague.GetAsync($"/api/chat/attachments/{first}/content"));
    }
}
