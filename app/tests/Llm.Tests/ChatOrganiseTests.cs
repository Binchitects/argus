using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Archiving, forking, and a deleted chat's files.</summary>
[Collection(nameof(AppCollection))]
public sealed class ChatOrganiseTests(AppFixture app)
{
    private async Task<(TestBrowser Browser, string Email)> PersonAsync()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "o" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/chat/conversations", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task SendAsync(TestBrowser b, Guid id, object body)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", body);
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        await res.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> ChatAsync(TestBrowser b, Guid id) => await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));

    private static List<JsonElement> Messages(JsonElement chat) => [.. chat.GetProperty("messages").EnumerateArray()];

    private static async Task<List<string>> TitlesAsync(TestBrowser b, bool archived = false) =>
        [.. (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations?archived={archived}"))).EnumerateArray().Select(c => c.GetProperty("title").GetString()!)];

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        var res = await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> ForkAsync(TestBrowser b, Guid id, Guid? messageId = null) =>
        await b.PostAsync($"/api/chat/conversations/{id}/fork", new { messageId });

    [Fact]
    public async Task An_archived_chat_leaves_the_list_and_comes_back_when_written_in()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        await SendAsync(b, id, new { content = "keep this for later" });
        Assert.Contains("keep this for later", await TitlesAsync(b));

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { archived = true }));
        Assert.DoesNotContain("keep this for later", await TitlesAsync(b));
        Assert.Equal(["keep this for later"], await TitlesAsync(b, archived: true));
        Assert.NotEqual(JsonValueKind.Null, (await ChatAsync(b, id)).GetProperty("archivedAt").ValueKind);

        await SendAsync(b, id, new { content = "one more thing" });
        Assert.Contains("keep this for later", await TitlesAsync(b));
        Assert.Empty(await TitlesAsync(b, archived: true));
        Assert.Equal(JsonValueKind.Null, (await ChatAsync(b, id)).GetProperty("archivedAt").ValueKind);
    }

    [Fact]
    public async Task A_fork_holds_the_branch_up_to_the_chosen_answer_with_the_chats_settings_and_files()
    {
        var (b, email) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false, thinking = "low", systemPrompt = "Answer in French." });
        var file = await UploadAsync(b, "plan.txt", "The launch is on Tuesday.");
        await SendAsync(b, id, new { content = "first", attachments = new[] { file } });
        await SendAsync(b, id, new { content = "second" });
        var original = Messages(await ChatAsync(b, id));
        Assert.Equal(4, original.Count);
        var firstAnswer = original[1].GetProperty("id").GetGuid();

        var res = await ForkAsync(b, id, firstAnswer);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var forkId = (await b.JsonAsync(res)).GetProperty("id").GetGuid();
        var fork = await ChatAsync(b, forkId);
        Assert.Equal("first (fork)", fork.GetProperty("title").GetString());
        Assert.Equal("low", fork.GetProperty("thinking").GetString());
        Assert.Equal("Answer in French.", fork.GetProperty("systemPrompt").GetString());
        Assert.Equal(id, fork.GetProperty("forkedFrom").GetProperty("id").GetGuid());
        var copied = Messages(fork);
        Assert.Equal(["user", "assistant"], copied.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(original[1].GetProperty("content").GetString(), copied[1].GetProperty("content").GetString());
        Assert.Equal(file, copied[0].GetProperty("attachments")[0].GetProperty("id").GetGuid());
        Assert.Equal(copied[1].GetProperty("id").GetGuid(), fork.GetProperty("currentLeafId").GetGuid());
        Assert.Equal(copied[0].GetProperty("id").GetGuid(), copied[1].GetProperty("parentId").GetGuid());

        // The fork goes its own way; the original is untouched.
        await SendAsync(b, forkId, new { content = "third" });
        var sent = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "user"], sent.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Contains("The launch is on Tuesday.", sent[1]!["content"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(4, Messages(await ChatAsync(b, id)).Count);

        // Without a message, the fork takes the whole branch on screen.
        var whole = (await b.JsonAsync(await ForkAsync(b, id))).GetProperty("id").GetGuid();
        Assert.Equal(4, Messages(await ChatAsync(b, whole)).Count);
    }

    [Fact]
    public async Task A_fork_never_ends_inside_a_tool_round_and_only_the_owner_can_fork()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { });
        await SendAsync(b, id, new { content = "Where is ParseHeader? [tool]" });
        var m = Messages(await ChatAsync(b, id));
        Assert.Equal(["user", "assistant", "tool", "assistant"], m.Select(x => x.GetProperty("role").GetString()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ForkAsync(b, id, m[1].GetProperty("id").GetGuid()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ForkAsync(b, id, m[2].GetProperty("id").GetGuid()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ForkAsync(b, id, Guid.NewGuid()));
        var fork = (await b.JsonAsync(await ForkAsync(b, id, m[3].GetProperty("id").GetGuid()))).GetProperty("id").GetGuid();
        Assert.Equal(4, Messages(await ChatAsync(b, fork)).Count);

        var (other, _) = await PersonAsync();
        await StatusAssert.Is(HttpStatusCode.NotFound, await ForkAsync(other, id));
    }

    [Fact]
    public async Task Deleting_a_chat_deletes_its_files_unless_a_fork_still_uses_them()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b, new { useArgus = false });
        var shared = await UploadAsync(b, "shared.txt", "shared");
        await SendAsync(b, id, new { content = "look", attachments = new[] { shared } });
        var fork = (await b.JsonAsync(await ForkAsync(b, id))).GetProperty("id").GetGuid();
        var own = await UploadAsync(b, "own.txt", "only in the original");
        await SendAsync(b, id, new { content = "and this", attachments = new[] { own } });

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/attachments/{own}/content"));
        await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync($"/api/chat/attachments/{shared}/content"));
        Assert.Equal("shared.txt", Messages(await ChatAsync(b, fork))[0].GetProperty("attachments")[0].GetProperty("fileName").GetString());

        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{fork}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/attachments/{shared}/content"));
    }
}
