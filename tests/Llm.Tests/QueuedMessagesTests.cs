using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Messages sent while a chat answers: kept on the server, shown on reload, answered in turn, cancelled or sent now.</summary>
[Collection(nameof(AppCollection))]
public sealed class QueuedMessagesTests(AppFixture app)
{
    private Task<(TestBrowser Browser, string Email)> PersonAsync() => PersonAsync(app.Factory);

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "q" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    /// <summary>An app of its own whose calculator asks first: an answer waits in its tool call until the person allows it.</summary>
    private async Task<WebApplicationFactory<Program>> AskingAppAsync()
    {
        var f = app.Create(app.ConnectionStringFor("steer_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-queue-tests" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/tools/calculator", UriKind.Relative),
            new { enabled = true, audience = "Everyone", onByDefault = true, askFirst = true }));
        return f;
    }

    private static Task<HttpResponseMessage> AllowAsync(TestBrowser b, Guid id) => b.PostAsync($"/api/chat/conversations/{id}/tool-calls/call_1", new { allow = true });

    private static Task<HttpResponseMessage> EditAsync(TestBrowser b, Guid id, Guid queuedId, string content) =>
        b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}/queue/{queuedId}", UriKind.Relative), new { content });

    private static async Task<Guid> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return (await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_message_sent_while_a_tool_runs_is_taken_in_at_the_next_step_and_the_answer_goes_on()
    {
        await using var f = await AskingAppAsync();
        var (b, email) = await PersonAsync(f);
        var id = await NewChatAsync(b);
        var (res, _) = await StartAsync(b, id, """Sum: [call calculate {"expression":"6*7"}]""", "\"approval\"");
        JsonElement line;
        string sse;
        using (res)
        {
            // The answer waits in its tool call: a message sent now waits in line, and is not a new question yet.
            line = await b.JsonAsync(await QueueAsync(b, id, new { content = "Also say it in words" }));
            Assert.Equal(["Also say it in words"], Queued(line));
            Assert.True(line.GetProperty("answering").GetBoolean());
            await StatusAssert.Is(HttpStatusCode.NoContent, await AllowAsync(b, id));
            sse = await res.Content.ReadAsStringAsync();
        }
        var events = Events(sse);
        // One answer, not stopped: the tool's result, the message taken in, the next step, done.
        var types = events.Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.DoesNotContain("stopped", types);
        Assert.Equal("done", types[^1]);
        var tookIn = events.Single(e => e.GetProperty("type").GetString() == "took_in");
        Assert.True(types.IndexOf("tool_result") < types.IndexOf("took_in"));
        Assert.True(types.IndexOf("took_in") < types.LastIndexOf("assistant"));
        Assert.Equal("Also say it in words", tookIn.GetProperty("content").GetString());
        Assert.Equal(line.GetProperty("queued")[0].GetProperty("id").GetGuid(), tookIn.GetProperty("queuedId").GetGuid());

        // The model read it after the tool's result, in the same answer.
        var asked = app.Model.Requests.Where(r => r.Body["user"]?.GetValue<string>() == email).ToList();
        Assert.Equal(2, asked.Count);
        var read = asked[1].Body["messages"]!.AsArray();
        Assert.Equal("tool", read[^2]!["role"]!.GetValue<string>());
        Assert.Equal("user", read[^1]!["role"]!.GetValue<string>());
        Assert.Equal("Also say it in words", read[^1]!["content"]!.GetValue<string>());

        // Saved in its place: after the tool's result, and the answer's next step after it; nothing waits, nothing else is asked.
        var chat = await SettledAsync(b, id, 2);
        Assert.Empty(Queued(chat));
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "tool", "user", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(messages[2].GetProperty("id").GetGuid(), messages[3].GetProperty("parentId").GetGuid());
        Assert.Equal(tookIn.GetProperty("id").GetGuid(), messages[3].GetProperty("id").GetGuid());
        Assert.True(messages[3].GetProperty("details").GetProperty("takenIn").GetBoolean());
        Assert.Equal(messages[3].GetProperty("id").GetGuid(), messages[4].GetProperty("parentId").GetGuid());
        Assert.Equal("Answer to: Also say it in words", messages[4].GetProperty("content").GetString());
        Assert.Equal(messages[4].GetProperty("id").GetGuid(), chat.GetProperty("currentLeafId").GetGuid());
    }

    [Fact]
    public async Task A_queued_message_is_edited_until_it_is_taken_in_and_one_with_files_waits_for_the_answer_to_end()
    {
        await using var f = await AskingAppAsync();
        var (b, _) = await PersonAsync(f);
        var (other, _) = await PersonAsync(f);
        var id = await NewChatAsync(b);
        var (res, _) = await StartAsync(b, id, """Sum: [call calculate {"expression":"6*7"}]""", "\"approval\"");
        using (res)
        {
            var line = await b.JsonAsync(await QueueAsync(b, id, new { content = "Say it in French" }));
            var queuedId = line.GetProperty("queued")[0].GetProperty("id").GetGuid();
            await StatusAssert.Is(HttpStatusCode.BadRequest, await EditAsync(b, id, queuedId, " "));
            await StatusAssert.Is(HttpStatusCode.NotFound, await EditAsync(other, id, queuedId, "Mine"));
            // Checked as a sent one is: the safeguards' longest message.
            var tooLong = await EditAsync(b, id, queuedId, new string('a', 100_001));
            await StatusAssert.Is(HttpStatusCode.BadRequest, tooLong);
            Assert.Equal("safeguard_length", (await b.JsonAsync(tooLong)).GetProperty("status").GetString());
            var edited = await EditAsync(b, id, queuedId, "Say it in Spanish");
            await StatusAssert.Is(HttpStatusCode.OK, edited);
            Assert.Equal(["Say it in Spanish"], Queued(await b.JsonAsync(edited)));
            await StatusAssert.Is(HttpStatusCode.NoContent, await AllowAsync(b, id));
            await res.Content.ReadAsStringAsync();
            // Taken in: there is nothing to edit any more.
            var late = await EditAsync(b, id, queuedId, "Too late");
            await StatusAssert.Is(HttpStatusCode.NotFound, late);
            Assert.Equal("not_queued", (await b.JsonAsync(late)).GetProperty("status").GetString());
        }
        var chat = await SettledAsync(b, id, 2);
        Assert.Equal(["Sum: [call calculate {\"expression\":\"6*7\"}]", "Say it in Spanish"],
            chat.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user").Select(m => m.GetProperty("content").GetString()));

        // With a file it waits for the answer to end, and goes as the next question (the model reads files with the history).
        var fileId = await UploadAsync(b, "notes.txt", "Some notes");
        (res, _) = await StartAsync(b, id, """Again: [call calculate {"expression":"1+1"}]""", "\"approval\"");
        using (res)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await QueueAsync(b, id, new { content = "Read this", attachments = new[] { fileId } }));
            await StatusAssert.Is(HttpStatusCode.NoContent, await AllowAsync(b, id));
            Assert.DoesNotContain(Events(await res.Content.ReadAsStringAsync()), e => e.GetProperty("type").GetString() == "took_in");
        }
        chat = await SettledAsync(b, id, 4);
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        var withFile = messages.Single(m => m.GetProperty("content").GetString() == "Read this");
        Assert.Equal("assistant", messages.Single(m => m.GetProperty("id").GetGuid() == withFile.GetProperty("parentId").GetGuid()).GetProperty("role").GetString());
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
    public async Task A_queued_skill_waits_filled_in_and_goes_as_the_skill_it_was()
    {
        var (b, email) = await PersonAsync();
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/skills", new { name = "recap", title = "Recap", instructions = "Recap {{topic}} in three bullets." }));
        var id = await NewChatAsync(b);
        var (res, _) = await StartAsync(b, id, "Take your time [steady]", "s3 ");
        await StatusAssert.Is(HttpStatusCode.BadRequest, await QueueAsync(b, id, new { content = "", skill = new { name = "recap" } }));
        await StatusAssert.Is(HttpStatusCode.OK, await QueueAsync(b, id, new { content = "", skill = new { name = "recap", args = new { topic = "the outage" } } }));
        var waiting = (await ConversationAsync(b, id)).GetProperty("queued").EnumerateArray().Single();
        Assert.Equal("the outage", waiting.GetProperty("skill").GetProperty("args").GetProperty("topic").GetString());
        await res.Content.ReadAsStringAsync();
        res.Dispose();

        var chat = await SettledAsync(b, id, 2);
        var question = chat.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal("recap", question.GetProperty("skill").GetProperty("name").GetString());
        Assert.Contains(app.Model.Requests, r => r.Body["user"]?.GetValue<string>() == email
            && r.Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToJsonString().Contains("Recap the outage in three bullets.", StringComparison.Ordinal));
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
    public async Task Put_first_moves_a_queued_message_ahead_without_stopping_the_answer()
    {
        var (b, _) = await PersonAsync();
        var id = await NewChatAsync(b);
        var (res, _) = await StartAsync(b, id, "Take your time [steady]", "s3 ");
        using (res)
        {
            await QueueAsync(b, id, new { content = "First in line" });
            var line = await b.JsonAsync(await QueueAsync(b, id, new { content = "Urgent" }));
            var urgent = line.GetProperty("queued")[1].GetProperty("id").GetGuid();
            var now = await b.PostAsync($"/api/chat/conversations/{id}/queue/{urgent}/now");
            await StatusAssert.Is(HttpStatusCode.OK, now);
            var after = await b.JsonAsync(now);
            Assert.Equal(["Urgent", "First in line"], Queued(after));
            Assert.True(after.GetProperty("answering").GetBoolean());
            // The answer runs to its end: only Stop stops it.
            Assert.Equal("done", Events(await res.Content.ReadAsStringAsync()).Last().GetProperty("type").GetString());
        }
        var chat = await SettledAsync(b, id, 3);
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["Take your time [steady]", "Urgent", "First in line"],
            messages.Where(m => m.GetProperty("role").GetString() == "user").Select(m => m.GetProperty("content").GetString()));
        Assert.Equal("complete", messages.First(m => m.GetProperty("role").GetString() == "assistant").GetProperty("status").GetString());
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
            var owner = db.Conversations.Where(c => c.Id == id).Select(c => c.UserId).Single();
            db.QueuedMessages.AddRange(
                new QueuedMessage { ConversationId = id, Content = "Left waiting", Position = 1, UserId = owner },
                new QueuedMessage { ConversationId = id, Content = "And after it", Position = 2, UserId = owner });
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
