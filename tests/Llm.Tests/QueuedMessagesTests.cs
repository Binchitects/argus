using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Messages sent while a chat answers: kept on the server, shown on reload, answered in turn, cancelled or sent now.</summary>
[Collection(nameof(AppCollection))]
public sealed class QueuedMessagesTests(AppFixture app)
{
    private async Task<(TestBrowser Browser, string Email)> PersonAsync()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "q" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b)
    {
        var res = await b.PostAsync("/api/chat/conversations", new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ConversationAsync(TestBrowser b, Guid id) => await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));

    private static List<JsonElement> Events(string sse) =>
        [.. sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    /// <summary>Starts an answer and reads its stream until <paramref name="until"/> has arrived; the stream stays open.</summary>
    private static async Task<(HttpResponseMessage Response, string Seen)> StartAsync(TestBrowser b, Guid id, string text, string until)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/chat/conversations/{id}/messages", UriKind.Relative))
        {
            Content = JsonContent.Create(new { content = text }),
        };
        var res = await b.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var stream = await res.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        var seen = new StringBuilder();
        while (!seen.ToString().Contains(until, StringComparison.Ordinal))
        {
            seen.Append(Encoding.UTF8.GetString(buffer, 0, await stream.ReadAsync(buffer)));
        }
        return (res, seen.ToString());
    }

    private static async Task<HttpResponseMessage> QueueAsync(TestBrowser b, Guid id, object body) => await b.PostAsync($"/api/chat/conversations/{id}/queue", body);

    private static IEnumerable<string> Queued(JsonElement e) => e.GetProperty("queued").EnumerateArray().Select(q => q.GetProperty("content").GetString()!);

    /// <summary>The chat once it has <paramref name="questions"/> questions and is not answering (the queue runs on its own).</summary>
    private static async Task<JsonElement> SettledAsync(TestBrowser b, Guid id, int questions)
    {
        JsonElement c = default;
        for (var i = 0; i < 150; i++)
        {
            c = await ConversationAsync(b, id);
            if (!c.GetProperty("answering").GetBoolean() && c.GetProperty("messages").EnumerateArray().Count(m => m.GetProperty("role").GetString() == "user") >= questions)
            {
                return c;
            }
            await Task.Delay(100);
        }
        return c;
    }

    [Fact]
    public async Task A_message_sent_while_the_chat_answers_waits_on_the_server_and_is_answered_after_it_on_its_branch()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b);
        var (res, _) = await StartAsync(b, id, "Take your time [steady]", "s3 ");
        using (res)
        {
            var queued = await QueueAsync(b, id, new { content = "Then this one [steady]" });
            await StatusAssert.Is(HttpStatusCode.OK, queued);
            var after = await b.JsonAsync(queued);
            Assert.Equal(["Then this one [steady]"], Queued(after));
            Assert.True(after.GetProperty("answering").GetBoolean());
            var second = await b.JsonAsync(await QueueAsync(b, id, new { content = "And this one" }));
            Assert.Equal(["Then this one [steady]", "And this one"], Queued(second));

            // A reload (or another tab) sees them, in line.
            var shown = await ConversationAsync(b, id);
            Assert.Equal(["Then this one [steady]", "And this one"], Queued(shown));
            Assert.Single(shown.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "user");

            // Cancel takes one out of line; a second cancel finds it gone.
            var cancel = second.GetProperty("queued")[1].GetProperty("id").GetGuid();
            await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}/queue/{cancel}", UriKind.Relative)));
            await StatusAssert.Is(HttpStatusCode.NotFound, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}/queue/{cancel}", UriKind.Relative)));

            // The answer ends: the queued message is the chat's question already, so the page that watched finds it answering.
            Assert.Equal("done", Events(await res.Content.ReadAsStringAsync()).Last().GetProperty("type").GetString());
        }
        var next = await ConversationAsync(b, id);
        Assert.True(next.GetProperty("answering").GetBoolean());
        Assert.Empty(Queued(next));
        var watched = Events(await (await b.GetAsync($"/api/chat/conversations/{id}/stream")).Content.ReadAsStringAsync());
        Assert.Equal("question", watched[0].GetProperty("type").GetString());
        Assert.Equal("done", watched.Last().GetProperty("type").GetString());

        // It followed the answer before it, as a question sent then would have.
        var chat = await SettledAsync(b, id, 2);
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        var questions = messages.Where(m => m.GetProperty("role").GetString() == "user").ToList();
        Assert.Equal(["Take your time [steady]", "Then this one [steady]"], questions.Select(q => q.GetProperty("content").GetString()));
        var firstAnswer = messages.Last(m => m.GetProperty("role").GetString() == "assistant" && m.GetProperty("parentId").GetGuid() == questions[0].GetProperty("id").GetGuid());
        Assert.Equal(firstAnswer.GetProperty("id").GetGuid(), questions[1].GetProperty("parentId").GetGuid());
        Assert.Equal(watched[0].GetProperty("id").GetGuid(), questions[1].GetProperty("id").GetGuid());
        Assert.Equal(messages[^1].GetProperty("id").GetGuid(), chat.GetProperty("currentLeafId").GetGuid());
        Assert.DoesNotContain(messages, m => m.GetProperty("content").GetString() == "And this one");
    }

    [Fact]
    public async Task Send_now_puts_a_queued_message_first_and_stops_the_answer_so_it_goes_at_once_and_the_rest_follow()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b);
        var (res, _) = await StartAsync(b, id, "Write a lot [slow]", "w3 ");
        using (res)
        {
            await QueueAsync(b, id, new { content = "First in line" });
            var line = await b.JsonAsync(await QueueAsync(b, id, new { content = "Urgent" }));
            var urgent = line.GetProperty("queued")[1].GetProperty("id").GetGuid();
            var now = await b.PostAsync($"/api/chat/conversations/{id}/queue/{urgent}/now");
            await StatusAssert.Is(HttpStatusCode.OK, now);
            Assert.Equal(["Urgent", "First in line"], Queued(await b.JsonAsync(now)));
            Assert.Equal("stopped", Events(await res.Content.ReadAsStringAsync()).Last().GetProperty("type").GetString());
        }
        var chat = await SettledAsync(b, id, 3);
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["Write a lot [slow]", "Urgent", "First in line"],
            messages.Where(m => m.GetProperty("role").GetString() == "user").Select(m => m.GetProperty("content").GetString()));
        Assert.Equal("stopped", messages.First(m => m.GetProperty("role").GetString() == "assistant").GetProperty("status").GetString());
        Assert.Equal("Answer to: First in line", messages[^1].GetProperty("content").GetString());
        Assert.Empty(Queued(chat));
    }

    [Fact]
    public async Task A_queued_message_is_checked_as_a_sent_one_and_only_its_owner_sees_or_changes_it()
    {
        var (b, _) = await PersonAsync();
        var (other, _) = await PersonAsync();
        var id = await NewChatAsync(b);

        // A chat not answering takes it at once.
        var idle = await b.JsonAsync(await QueueAsync(b, id, new { content = "Hello while idle" }));
        Assert.Empty(Queued(idle));
        var chat = await SettledAsync(b, id, 1);
        Assert.Equal("Answer to: Hello while idle", chat.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());

        var (res, _) = await StartAsync(b, id, "Write a lot [slow]", "w3 ");
        using (res)
        {
            await StatusAssert.Is(HttpStatusCode.BadRequest, await QueueAsync(b, id, new { content = "  " }));
            await StatusAssert.Is(HttpStatusCode.BadRequest, await QueueAsync(b, id, new { content = "x", attachments = new[] { Guid.NewGuid() } }));
            // The safeguards (here the longest message) apply as to a message sent.
            var tooLong = await QueueAsync(b, id, new { content = new string('a', 100_001) });
            await StatusAssert.Is(HttpStatusCode.BadRequest, tooLong);
            Assert.Equal("safeguard_length", (await b.JsonAsync(tooLong)).GetProperty("status").GetString());

            for (var i = 0; i < QueuedMessages.Max; i++)
            {
                await StatusAssert.Is(HttpStatusCode.OK, await QueueAsync(b, id, new { content = $"Waiting {i}" }));
            }
            await StatusAssert.Is(HttpStatusCode.Conflict, await QueueAsync(b, id, new { content = "One too many" }));
            var queuedId = (await ConversationAsync(b, id)).GetProperty("queued")[0].GetProperty("id").GetGuid();

            // Someone else: not their chat.
            await StatusAssert.Is(HttpStatusCode.NotFound, await QueueAsync(other, id, new { content = "Mine now" }));
            await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}/queue/{queuedId}", UriKind.Relative)));
            await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/chat/conversations/{id}/queue/{queuedId}/now"));
            Assert.Equal(QueuedMessages.Max, Queued(await ConversationAsync(b, id)).Count());
        }

        // Deleting the chat takes its line with it.
        await b.PostAsync($"/api/chat/conversations/{id}/stop");
        await SettledAsync(b, id, 2 + QueuedMessages.Max);
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative)));
        await using var scope = app.Factory.Services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().QueuedMessages.Any(q => q.ConversationId == id));
    }

    [Fact]
    public async Task Messages_queued_before_a_restart_go_once_the_app_is_back()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b);
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            // As a restart leaves them: waiting, with no answer running.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.QueuedMessages.AddRange(
                new QueuedMessage { ConversationId = id, Content = "Left waiting", Position = 1 },
                new QueuedMessage { ConversationId = id, Content = "And after it", Position = 2 });
            await db.SaveChangesAsync();
        }
        Assert.Equal(["Left waiting", "And after it"], Queued(await ConversationAsync(b, id)));

        await app.Factory.Services.GetRequiredService<QueuedMessages>().StartWaitingAsync(CancellationToken.None);
        var chat = await SettledAsync(b, id, 2);
        var questions = chat.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user").ToList();
        Assert.Equal(["Left waiting", "And after it"], questions.Select(q => q.GetProperty("content").GetString()));
        // The first question of the chat names it.
        Assert.Equal("Left waiting", chat.GetProperty("title").GetString());
        Assert.Empty(Queued(chat));
    }
}
