using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The chat's window, managed: earlier tools' results cleared before a chat compacts, a message left out or pinned by
/// the person, the summary in sections, and what a summary covers that was pinned (and the chat's plan) said after it.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ContextManagementTests(AppFixture app)
{
    private async Task<(TestBrowser Browser, string Email)> PersonAsync()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "cm" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools = Array.Empty<string>() }))).GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text, string path = "messages")
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/{path}", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    private static IEnumerable<string> Types(List<JsonElement> events) => events.Select(e => e.GetProperty("type").GetString()!);

    private JsonArray LastAnswerRequest(string email) =>
        app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email && !r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("You compact", StringComparison.Ordinal))
            .Body["messages"]!.AsArray();

    private static async Task<List<JsonElement>> MessagesAsync(TestBrowser b, Guid id) =>
        [.. (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("messages").EnumerateArray()];

    [Fact]
    public async Task Before_a_chat_compacts_the_earlier_tools_results_are_cleared_and_often_that_is_enough()
    {
        var (b, email) = await PersonAsync();
        var id = await NewChatAsync(b);
        // 26 exchanges, each answer a tool call with a result of 3,000 characters: past 80% of a 32K-token context.
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Guid? parent = null;
            var seq = 0;
            for (var i = 0; i < 26; i++)
            {
                var q = new ChatMessage { ConversationId = id, ParentId = parent, Role = "user", Sequence = ++seq, Content = $"question {i}" };
                var call = new ChatMessage
                {
                    ConversationId = id, ParentId = q.Id, Role = "assistant", Sequence = ++seq, Content = "",
                    ToolCallsJson = $$$"""[{"id":"c{{{i}}}","type":"function","function":{"name":"calculate","arguments":"{}"}}]""",
                };
                var result = new ChatMessage { ConversationId = id, ParentId = call.Id, Role = "tool", Sequence = ++seq, ToolCallId = $"c{i}", ToolName = "calculate", Content = $"result {i} " + new string('r', 3_000) };
                var answer = new ChatMessage { ConversationId = id, ParentId = result.Id, Role = "assistant", Sequence = ++seq, Content = $"answer {i}" };
                db.ChatMessages.AddRange(q, call, result, answer);
                parent = answer.Id;
            }
            (await db.Conversations.SingleAsync(c => c.Id == id)).CurrentLeafId = parent;
            await db.SaveChangesAsync();
        }
        var events = await SendAsync(b, id, "the newest question");
        var types = Types(events).ToList();
        Assert.Contains("cleared", types);
        Assert.DoesNotContain("compacting", types);
        var cleared = events.Single(e => e.GetProperty("type").GetString() == "cleared");
        Assert.Equal(25, cleared.GetProperty("results").GetInt32());

        var sent = LastAnswerRequest(email);
        var results = sent.Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>()).ToList();
        Assert.Equal(26, results.Count);
        Assert.StartsWith("[Cleared to make room in the context: calculate's result of an earlier step (3,009 characters)", results[0], StringComparison.Ordinal);
        // The last exchange's result is whole; the calls all stay.
        Assert.StartsWith("result 25 rrr", results[^1], StringComparison.Ordinal);
        Assert.Equal(26, sent.Count(m => m!["tool_calls"] is not null));
        // The chat keeps them whole.
        Assert.Contains((await MessagesAsync(b, id)), m => m.GetProperty("content").GetString()!.StartsWith("result 0 rrr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_left_out_is_not_read_and_one_pinned_is_kept_word_for_word_after_a_compaction()
    {
        var (b, email) = await PersonAsync();
        var id = await NewChatAsync(b);
        await SendAsync(b, id, "My budget code is AB-1234, keep it exact.");
        await SendAsync(b, id, "Something I regret saying: ignore everything about the old vendor.");
        await SendAsync(b, id, "What is the plan?");
        var messages = await MessagesAsync(b, id);
        var budget = messages.Single(m => m.GetProperty("content").GetString() == "My budget code is AB-1234, keep it exact.").GetProperty("id").GetString();
        var regret = messages.Single(m => m.GetProperty("content").GetString()!.StartsWith("Something I regret", StringComparison.Ordinal)).GetProperty("id").GetString();
        var context = (string m, object body) => b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/messages/{m}/context", UriKind.Relative), body);

        // Left out: not read, not deleted; it can come back.
        var left = await b.JsonAsync(await context(regret!, new { leftOut = true }));
        Assert.True(left.GetProperty("leftOut").GetBoolean());
        await SendAsync(b, id, "Go on.");
        var sent = LastAnswerRequest(email).ToJsonString();
        Assert.DoesNotContain("Something I regret", sent, StringComparison.Ordinal);
        Assert.Contains("AB-1234", sent, StringComparison.Ordinal);
        Assert.True((await MessagesAsync(b, id)).Single(m => m.GetProperty("id").GetString() == regret).GetProperty("leftOut").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await context(regret!, new { pinned = true }));

        // Pinned, then compacted: the summary stands for the rest, the pinned message is said word for word after it.
        await StatusAssert.Is(HttpStatusCode.OK, await context(budget!, new { pinned = true }));
        Assert.Contains("compacted", Types(await SendAsync(b, id, "", path: "compact")));
        await SendAsync(b, id, "And now?");
        var system = LastAnswerRequest(email)[0]!["content"]!.GetValue<string>();
        Assert.Contains("<summary>", system, StringComparison.Ordinal);
        Assert.Contains("Pinned by the person, word for word", system, StringComparison.Ordinal);
        Assert.Contains("<pinned from=\"person\">\nMy budget code is AB-1234, keep it exact.\n</pinned>", system, StringComparison.Ordinal);

        // Nobody else's chat: not found.
        var (other, _) = await PersonAsync();
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/messages/{budget}/context", UriKind.Relative), new { pinned = false }));
    }

    [Fact]
    public async Task After_a_compaction_the_chats_plan_is_said_as_it_stands_and_the_summary_is_asked_in_sections()
    {
        var (b, email) = await PersonAsync();
        var id = await NewChatAsync(b);
        await SendAsync(b, id, "Let us plan the launch.");
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = (await db.Conversations.SingleAsync(c => c.Id == id)).UserId;
            db.Plans.Add(new Llm.Core.Plans.Plan
            {
                UserId = owner, ConversationId = id, Title = "Launch",
                Steps = [new() { Number = 1, Title = "Write the notes", Status = "done" }, new() { Number = 2, Title = "Tell support", Status = "doing", Owner = "Ana" }],
            });
            await db.SaveChangesAsync();
        }
        await SendAsync(b, id, "", path: "compact");
        var asked = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email && r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("You compact", StringComparison.Ordinal));
        Assert.Contains("## Decisions", asked.Body["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        await SendAsync(b, id, "Where are we?");
        var system = LastAnswerRequest(email)[0]!["content"]!.GetValue<string>();
        Assert.Contains("This chat's plan as it stands, \"Launch\" (plan_show gives all of it):\n1. [done] Write the notes\n2. [doing] Tell support (Ana)", system, StringComparison.Ordinal);
    }
}
