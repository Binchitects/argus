using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Rewind: a chat goes back to one of its questions (just before it, or after its answer). What came after leaves the branch the
/// model reads, a summary that covered it too; it stays as another branch, so Undo puts it back. Its owner rewinds (a room's,
/// for everyone), never while an answer is written; memories written from what goes may go too, and its digest is written anew.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ChatRewindTests(AppFixture app)
{
    private static async Task<(TestBrowser Browser, string Email, Guid Id)> PersonAsync(WebApplicationFactory<Program> f, string? name = null)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var user = "rw" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = name ?? user }));
        return (await new TestBrowser(f).SignedInAsync(user, made.GetProperty("password").GetString()!), $"{user}@example.test", made.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b)
    {
        var res = await b.PostAsync("/api/chat/conversations", new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<string> SendAsync(TestBrowser b, Guid id, string text, string path = "messages")
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/{path}", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> ChatAsync(TestBrowser b, Guid id) => await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));

    private static Guid IdOf(JsonElement chat, string content) =>
        chat.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("content").GetString() == content).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> RewindAsync(TestBrowser b, Guid id, object body) => b.PostAsync($"/api/chat/conversations/{id}/rewind", body);

    /// <summary>What the model read for the person's last answer, after the system prompt.</summary>
    private List<string> Read(string email) =>
        [.. app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email && r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("Today is", StringComparison.Ordinal))
            .Body["messages"]!.AsArray().Skip(1).Select(m => m!["content"]?.ToString() ?? "")];

    [Fact]
    public async Task Rewinding_to_before_a_question_takes_it_and_what_followed_out_of_what_the_model_reads_and_undo_puts_them_back()
    {
        var (b, email, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await SendAsync(b, id, "first");
        await SendAsync(b, id, "second");
        await SendAsync(b, id, "third");
        var chat = await ChatAsync(b, id);
        var (second, end) = (IdOf(chat, "second"), chat.GetProperty("currentLeafId").GetGuid());
        var firstAnswer = chat.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("parentId").ValueKind != JsonValueKind.Null
            && m.GetProperty("parentId").GetGuid() == IdOf(chat, "first")).GetProperty("id").GetGuid();

        // What the confirmation says: four messages go, the branch ends at the first answer.
        var preview = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/rewind?messageId={second}"));
        Assert.Equal((4, firstAnswer), (preview.GetProperty("gone").GetInt32(), preview.GetProperty("currentLeafId").GetGuid()));
        Assert.Empty(preview.GetProperty("memories").EnumerateArray());

        var rewound = await b.JsonAsync(await RewindAsync(b, id, new { messageId = second }));
        Assert.Equal((firstAnswer, end, 4), (rewound.GetProperty("currentLeafId").GetGuid(), rewound.GetProperty("previousLeafId").GetGuid(), rewound.GetProperty("gone").GetInt32()));
        chat = await ChatAsync(b, id);
        Assert.Equal(firstAnswer, chat.GetProperty("currentLeafId").GetGuid());
        // Kept as another branch, as an edited question's turns are.
        Assert.Equal(6, chat.GetProperty("messages").GetArrayLength());

        // The next answer reads the chat from there.
        await SendAsync(b, id, "instead");
        Assert.Equal(["first", "Answer to: first", "instead"], Read(email));
        Assert.Equal(second, IdOf(await ChatAsync(b, id), "second"));

        // Undo (the toast's): the branch ends where it did, and the model reads all of it again.
        await StatusAssert.Is(HttpStatusCode.OK, await RewindAsync(b, id, new { messageId = IdOf(await ChatAsync(b, id), "instead") }));
        var undone = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/leaf", UriKind.Relative), new { messageId = end, exact = true }));
        Assert.Equal(end, undone.GetProperty("currentLeafId").GetGuid());
        await SendAsync(b, id, "fourth");
        Assert.Equal(["first", "Answer to: first", "second", "Answer to: second", "third", "Answer to: third", "fourth"], Read(email));
    }

    [Fact]
    public async Task Kept_the_question_and_its_answer_stay_and_only_what_came_after_goes()
    {
        var (b, email, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await SendAsync(b, id, "first");
        await SendAsync(b, id, "second");
        var chat = await ChatAsync(b, id);
        var first = IdOf(chat, "first");
        var preview = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/rewind?messageId={first}&keep=true"));
        Assert.Equal(2, preview.GetProperty("gone").GetInt32());

        var kept = await b.JsonAsync(await RewindAsync(b, id, new { messageId = first, keep = true }));
        Assert.Equal(2, kept.GetProperty("gone").GetInt32());
        await SendAsync(b, id, "next");
        Assert.Equal(["first", "Answer to: first", "next"], Read(email));

        // The last question kept with its answer: nothing comes after it.
        var refused = await RewindAsync(b, id, new { messageId = IdOf(await ChatAsync(b, id), "next"), keep = true });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        // Only a question of the branch on screen: not an answer, not one rewound from, not another chat's.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await RewindAsync(b, id, new { messageId = kept.GetProperty("currentLeafId").GetGuid() }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await RewindAsync(b, id, new { messageId = IdOf(chat, "second") }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await RewindAsync(b, id, new { messageId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Rewound_to_before_a_compaction_mark_the_model_reads_the_whole_messages_again_not_the_summary()
    {
        var (b, email, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await SendAsync(b, id, "first");
        await SendAsync(b, id, "second");
        await SendAsync(b, id, "", path: "compact");
        await SendAsync(b, id, "third");
        Assert.Equal(["third"], Read(email));

        // The summary is on the second answer, which goes: what is left is read word for word.
        await StatusAssert.Is(HttpStatusCode.OK, await RewindAsync(b, id, new { messageId = IdOf(await ChatAsync(b, id), "second") }));
        await SendAsync(b, id, "instead");
        Assert.Equal(["first", "Answer to: first", "instead"], Read(email));
        var system = app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email).Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.DoesNotContain("Summary of ", system, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Only_the_owner_rewinds_a_room_for_everyone_and_nobody_while_an_answer_is_written()
    {
        var (owner, _, _) = await PersonAsync(app.Factory, "Olivia");
        var (member, _, memberId) = await PersonAsync(app.Factory, "Ben");
        var (outsider, _, _) = await PersonAsync(app.Factory, "Otto");
        var made = await owner.PostAsync("/api/rooms", new { title = "Launch", userIds = new[] { memberId } });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var room = (await owner.JsonAsync(made)).GetProperty("id").GetGuid();
        await SendAsync(member, room, "@Arena first");
        await SendAsync(member, room, "@Arena second");
        var second = IdOf(await ChatAsync(owner, room), "@Arena second");

        await StatusAssert.Is(HttpStatusCode.Forbidden, await RewindAsync(member, room, new { messageId = second }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await RewindAsync(outsider, room, new { messageId = second }));
        await StatusAssert.Is(HttpStatusCode.OK, await RewindAsync(owner, room, new { messageId = second }));
        // Gone back for everyone in it.
        var seen = await ChatAsync(member, room);
        Assert.Equal(IdOf(seen, "@Arena first"), Guid.Parse(seen.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("id").GetGuid() == seen.GetProperty("currentLeafId").GetGuid()).GetProperty("parentId").GetString()!));

        // Not while an answer is written, nor while Code Arena answers in it.
        var mine = await NewChatAsync(owner);
        await SendAsync(owner, mine, "first");
        var first = IdOf(await ChatAsync(owner, mine), "first");
        using (var slow = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/chat/conversations/{mine}/messages", UriKind.Relative)) { Content = JsonContent.Create(new { content = "long [slow]" }) })
        {
            using var res = await owner.Http.SendAsync(slow, HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await res.Content.ReadAsStreamAsync();
            await stream.ReadExactlyAsync(new byte[64]);
            await StatusAssert.Is(HttpStatusCode.Conflict, await RewindAsync(owner, mine, new { messageId = first }));
            await StatusAssert.Is(HttpStatusCode.Conflict, await owner.GetAsync($"/api/chat/conversations/{mine}/rewind?messageId={first}"));
            await StatusAssert.Is(HttpStatusCode.Accepted, await owner.PostAsync($"/api/chat/conversations/{mine}/stop", new { }));
        }
        using (var scope = app.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations.Where(c => c.Id == mine)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CodeArenaTurnUntil, DateTimeOffset.UtcNow.AddMinutes(1)));
        }
        for (var i = 0; i < 100 && (await ChatAsync(owner, mine)).GetProperty("answering").GetBoolean(); i++)
        {
            await Task.Delay(50);
        }
        await StatusAssert.Is(HttpStatusCode.Conflict, await RewindAsync(owner, mine, new { messageId = first }));
    }

    [Fact]
    public async Task A_shared_branch_stays_as_shared_and_the_whole_chat_s_link_shows_it_rewound()
    {
        var (b, _, _) = await PersonAsync(app.Factory);
        var (reader, _, _) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await SendAsync(b, id, "first");
        await SendAsync(b, id, "second");
        var chat = await ChatAsync(b, id);
        var end = chat.GetProperty("currentLeafId").GetGuid();
        var link = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/share", UriKind.Relative), new { reach = "Company", branch = true }));

        await StatusAssert.Is(HttpStatusCode.OK, await RewindAsync(b, id, new { messageId = IdOf(chat, "second") }));
        var shared = await reader.JsonAsync(await reader.GetAsync($"/api/shared/{link.GetProperty("id").GetGuid()}"));
        Assert.Equal(end, shared.GetProperty("currentLeafId").GetGuid());
        Assert.Equal(4, shared.GetProperty("messages").GetArrayLength());

        var whole = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/share", UriKind.Relative), new { reach = "Company", branch = false }));
        shared = await reader.JsonAsync(await reader.GetAsync($"/api/shared/{whole.GetProperty("id").GetGuid()}"));
        Assert.NotEqual(end, shared.GetProperty("currentLeafId").GetGuid());
        Assert.Equal(IdOf(chat, "first"), Guid.Parse(shared.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("id").GetGuid() == shared.GetProperty("currentLeafId").GetGuid()).GetProperty("parentId").GetString()!));
    }

    [Fact]
    public async Task Memories_written_from_what_goes_are_listed_and_forgotten_when_asked_and_the_digest_is_written_anew()
    {
        var (b, _, me) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b);
        await SendAsync(b, id, "first");
        await SendAsync(b, id, "second");
        var chat = await ChatAsync(b, id);
        var second = IdOf(chat, "second");
        var at = chat.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == second).GetProperty("createdAt").GetDateTimeOffset();
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Memories.AddRange(
                new Memory { UserId = me, Text = "Before it.", Source = Memory.FromChat, SourceId = id, CreatedAt = at.AddSeconds(-1) },
                new Memory { UserId = me, Text = "Said after it.", Source = Memory.FromChat, SourceId = id, CreatedAt = at.AddSeconds(1) },
                new Memory { UserId = me, Text = "Proposed from it.", Source = Memory.Reflected, SourceId = id, Proposed = true, CreatedAt = at.AddSeconds(2) },
                new Memory { UserId = me, Text = "Typed.", CreatedAt = at.AddSeconds(3) });
            await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Digest, "About two questions.").SetProperty(c => c.DigestedSequence, 4));
            await db.SaveChangesAsync();
        }

        var preview = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}/rewind?messageId={second}"));
        Assert.Equal(["Said after it.", "Proposed from it."], preview.GetProperty("memories").EnumerateArray().Select(m => m.GetProperty("text").GetString()));
        var rewound = await b.JsonAsync(await RewindAsync(b, id, new { messageId = second, forget = true }));
        Assert.Equal(2, rewound.GetProperty("forgotten").GetInt32());
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(["Before it.", "Typed."], await db.Memories.Where(m => m.UserId == me).OrderBy(m => m.CreatedAt).Select(m => m.Text).ToListAsync());
            var c = await db.Conversations.SingleAsync(x => x.Id == id);
            Assert.Equal(((string?)null, 0), (c.Digest, c.DigestedSequence));
        }
    }

    [Fact]
    public async Task What_a_rewind_went_back_from_is_not_reflected_on_later()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = app.Create(app.ConnectionStringFor("rewind_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), null, s => s.AddSingleton<TimeProvider>(clock));
        var (b, _, me) = await PersonAsync(f);
        var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await SendAsync(b, id, "A question about the weather today, nothing to keep.");
        await SendAsync(b, id, """I always deploy with Podman, never Docker. [propose {"text":"Deploys with Podman, never Docker.","kind":"howto","scope":"person"}]""");
        var second = IdOf(await ChatAsync(b, id), """I always deploy with Podman, never Docker. [propose {"text":"Deploys with Podman, never Docker.","kind":"howto","scope":"person"}]""");
        await StatusAssert.Is(HttpStatusCode.OK, await RewindAsync(b, id, new { messageId = second }));

        clock.Now += MemoryReflection.Quiet + TimeSpan.FromMinutes(1);
        await f.Services.GetRequiredService<MemoryReflection>().RunAsync(CancellationToken.None);
        using var scope = f.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Memories.Where(m => m.UserId == me).ToListAsync());
    }

    [Fact]
    public async Task A_Code_Arena_session_rewound_there_shows_rewound_here_and_one_rewound_here_is_read_there_from_where_it_ends()
    {
        await using var f = app.Create(app.ConnectionStringFor("rewsync_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-sync-tests" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var user = "rs" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test" }));
        var web = await new TestBrowser(f).SignedInAsync(user, made.GetProperty("password").GetString()!);
        var arena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        arena.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", made.GetProperty("apiKey").GetString()!);
        arena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        static async Task<JsonElement> Json(HttpResponseMessage res) => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
        var id = (await Json(await arena.PostAsJsonAsync("/api/code-arena/chats", new { @ref = "rw-1", place = "/w" }))).GetProperty("id").GetGuid();
        object Said(string role, string text, int n) => new { role, content = text, @ref = $"s:{n}" };
        var sent = await arena.PostAsJsonAsync($"/api/code-arena/chats/{id}/messages", new { parent = (Guid?)null, messages = new[] { Said("user", "q1", 0), Said("assistant", "a1", 1), Said("user", "q2", 2), Said("assistant", "a2", 3) } });
        var ids = (await Json(sent)).GetProperty("ids").EnumerateArray().Select(i => i.GetGuid()).ToList();

        // Rewound in Code Arena (/rewind, the IDE's Rewind to here): it says where its branch ends now, with nothing to add.
        await StatusAssert.Is(HttpStatusCode.OK, await arena.PostAsJsonAsync($"/api/code-arena/chats/{id}/messages", new { parent = ids[1], branch = true, messages = Array.Empty<object>() }));
        var chat = await ChatAsync(web, id);
        Assert.Equal(ids[1], chat.GetProperty("currentLeafId").GetGuid());
        Assert.Equal(4, chat.GetProperty("messages").GetArrayLength());

        // Shown again, then rewound on the web: Code Arena reads it as a fork, from where the web's branch ends now.
        await StatusAssert.Is(HttpStatusCode.OK, await web.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/leaf", UriKind.Relative), new { messageId = ids[3], exact = true }));
        await StatusAssert.Is(HttpStatusCode.OK, await RewindAsync(web, id, new { messageId = ids[2] }));
        var read = await Json(await arena.GetAsync($"/api/code-arena/chats/{id}?since={ids[3]}"));
        Assert.True(read.GetProperty("fork").GetBoolean());
        Assert.Equal(ids[1], read.GetProperty("base").GetGuid());
        Assert.Empty(read.GetProperty("messages").EnumerateArray());
    }
}
