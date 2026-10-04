using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The model for sub-agents and small steps (Chat:SmallModel), and Auto, which it brings to the model menu.</summary>
[Collection(nameof(AppCollection))]
public sealed class SmallModelTests(AppFixture app)
{
    private const string Small = "Qwen3-4B";
    private const string Main = "Qwen3.8-Flash-Next";

    /// <summary>Its own app and database, with a small model at the gateway beside the main one.</summary>
    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? settings = null, bool smallSet = true)
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(Small, 32768, 4096, Vision: false, Tools: true, Thinking: true, 0.02m, 0.002m, 0.08m));
        var all = new Dictionary<string, string?>(settings ?? []) { ["Auth:DataKey"] = "a-data-key-for-small-model-tests" };
        if (smallSet)
        {
            all.TryAdd("Chat:SmallModel", Small);
        }
        return app.Create(app.ConnectionStringFor("small_" + Guid.NewGuid().ToString("N")[..8]), gateway, all);
    }

    private static Task<TestBrowser> AdminAsync(WebApplicationFactory<Program> f) => new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await AdminAsync(f);
        var name = "s" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/chat/conversations", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text, string path = "messages", object? body = null)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/{path}", body ?? new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    private static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    /// <summary>What the person's requests were, by the start of their system prompt.</summary>
    private List<JsonObject> RequestsOf(string email, string systemStart) =>
        [.. app.Model.Requests.Select(r => r.Body).Where(r => r["user"]!.GetValue<string>() == email
            && r["messages"]![0]!["content"]!.GetValue<string>().StartsWith(systemStart, StringComparison.Ordinal))];

    /// <summary>The answers' own requests (not a sub-agent's, a summary, a title or Auto's sorting).</summary>
    private List<JsonObject> AnswersOf(string email) => RequestsOf(email, "Today is").Where(r => !Says(r, "You are a sub-agent")).ToList();

    private static bool Says(JsonObject request, string text) => request["messages"]![0]!["content"]!.GetValue<string>().Contains(text, StringComparison.Ordinal);

    private static string ModelOf(JsonObject request) => request["model"]!.GetValue<string>();

    private static readonly string Delegate = "[call delegate " + JsonSerializer.Serialize(new
    {
        tasks = new[] { new { title = "Sum", instructions = """Work it out: [call calculate {"expression":"2+2"}]""" }, new { title = "Colour", instructions = "Name a colour." } },
    }) + "]";

    [Fact]
    public async Task With_a_small_model_sub_agents_titles_compaction_and_the_check_go_to_it_and_the_answer_to_the_chats_model()
    {
        await using var f = NewApp(new() { ["Safeguards:Moderation"] = "check" });
        var (b, _, email) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { tools = new[] { "agents", "calculator" } });

        var events = await SendAsync(b, id, $"Plan the garden party {Delegate}");

        // The answer itself: the chat's model, thinking as the chat says.
        Assert.All(AnswersOf(email), r => Assert.Equal(Main, ModelOf(r)));
        // The sub-agents: the small model, thinking off, each step's words capped.
        var agents = RequestsOf(email, "Today is").Where(r => Says(r, "You are a sub-agent")).ToList();
        Assert.True(agents.Count >= 3);
        Assert.All(agents, r =>
        {
            Assert.Equal(Small, ModelOf(r));
            Assert.False(r["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
            Assert.Equal(1200, r["max_tokens"]!.GetValue<int>());
        });
        // Each part says which model did it, as it ends and as it is kept.
        Assert.All(events.Where(e => Type(e) == "agent" && e.GetProperty("event").GetString() == "done"), e => Assert.Equal(Small, e.GetProperty("model").GetString()));
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        var call = chat.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("toolName").GetString() == "delegate");
        Assert.All(call.GetProperty("details").GetProperty("agents").EnumerateArray(), a => Assert.Equal(Small, a.GetProperty("model").GetString()));
        var answer = chat.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal(Main, answer.GetProperty("model").GetString());

        // The title: the first line at once, then the small model's, without thinking.
        var titles = events.Where(e => Type(e) == "title").Select(e => e.GetProperty("title").GetString()).ToList();
        Assert.Equal(2, titles.Count);
        Assert.StartsWith("Plan the garden party", titles[0], StringComparison.Ordinal);
        Assert.Equal("Named Plan the garden", titles[1]);
        Assert.Equal("Named Plan the garden", chat.GetProperty("title").GetString());
        var titled = Assert.Single(RequestsOf(email, "You name conversations"));
        Assert.Equal(Small, ModelOf(titled));
        Assert.False(titled["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        // The safeguards' check read the message on the small model.
        Assert.All(RequestsOf(email, "You check messages"), r => Assert.Equal(Small, ModelOf(r)));
        Assert.NotEmpty(RequestsOf(email, "You check messages"));

        // Later questions keep their title; a compaction is the small model's summary.
        await SendAsync(b, id, "And a second question");
        Assert.Single(RequestsOf(email, "You name conversations"));
        var compacted = (await SendAsync(b, id, "", path: "compact", body: new { })).Single(e => Type(e) == "compacted");
        Assert.Equal(Small, compacted.GetProperty("model").GetString());
        Assert.All(RequestsOf(email, "You compact"), r => Assert.Equal(Small, ModelOf(r)));

        // A chat compacted before an answer: the small model summarizes, the chat's model answers.
        var full = await NewChatAsync(b, new { tools = Array.Empty<string>() });
        await OldExchangesAsync(f, full, 30, 2_500);
        var before = RequestsOf(email, "You compact").Count;
        var auto = await SendAsync(b, full, "the newest question");
        Assert.Equal(Small, auto.Single(e => Type(e) == "compacted").GetProperty("model").GetString());
        Assert.Equal(before + 1, RequestsOf(email, "You compact").Count);
        Assert.All(RequestsOf(email, "You compact"), r => Assert.Equal(Small, ModelOf(r)));
        Assert.Equal(Main, ModelOf(AnswersOf(email).Last()));

        // Without it, every step is the answer's model again, and a title is the first line.
        var admin = await AdminAsync(f);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Chat:SmallModel", value = "" } } }));
        var plain = await NewChatAsync(b, new { tools = new[] { "agents", "calculator" } });
        var later = await SendAsync(b, plain, $"Plan the picnic {Delegate}");
        Assert.All(later.Where(e => Type(e) == "agent" && e.GetProperty("event").GetString() == "done"), e => Assert.Equal(Main, e.GetProperty("model").GetString()));
        Assert.Single(later, e => Type(e) == "title");
        Assert.Single(RequestsOf(email, "You name conversations"));
        Assert.Equal(Main, ModelOf(RequestsOf(email, "You check messages").Last()));
    }

    /// <summary>A branch of old exchanges on screen, each question <paramref name="chars"/> long.</summary>
    private static async Task OldExchangesAsync(WebApplicationFactory<Program> f, Guid id, int count, int chars)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid? parent = null;
        for (var i = 0; i < count; i++)
        {
            var q = new Llm.Core.Chat.ChatMessage { ConversationId = id, ParentId = parent, Role = "user", Sequence = 2 * i + 1, Content = $"old question {i} " + new string('x', chars) };
            var a = new Llm.Core.Chat.ChatMessage { ConversationId = id, ParentId = q.Id, Role = "assistant", Sequence = 2 * i + 2, Content = $"old answer {i}" };
            db.ChatMessages.AddRange(q, a);
            parent = a.Id;
        }
        var c = await db.Conversations.SingleAsync(x => x.Id == id);
        c.CurrentLeafId = parent;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Auto_has_the_small_model_answer_easy_questions_and_hands_the_rest_on_with_thinking_by_how_hard_they_are()
    {
        await using var f = NewApp();
        var (b, _, email) = await PersonAsync(f);
        var config = await b.JsonAsync(await b.GetAsync("/api/chat/config"));
        Assert.Equal(Small, config.GetProperty("auto").GetProperty("model").GetString());
        Assert.False(config.GetProperty("auto").GetProperty("byDefault").GetBoolean());
        var id = await NewChatAsync(b, new { model = "auto", tools = Array.Empty<string>() });
        Assert.Equal("auto", (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"))).GetProperty("model").GetString());

        // Small talk: one short call sorts it, and the small model answers, without thinking.
        var hello = await SendAsync(b, id, "hello there [kind:chat]");
        var sorted = Assert.Single(RequestsOf(email, "You sort the questions"));
        Assert.Equal(Small, ModelOf(sorted));
        Assert.Contains("hello there", sorted["messages"]![1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        var easy = AnswersOf(email).Last();
        Assert.Equal(Small, ModelOf(easy));
        Assert.False(easy["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        var route = hello.Single(e => Type(e) == "route").GetProperty("route");
        Assert.Equal("chat", route.GetProperty("kind").GetString());
        Assert.True(route.GetProperty("small").GetBoolean());
        Assert.Equal(Small, route.GetProperty("model").GetString());
        Assert.Equal(Main, route.GetProperty("main").GetString());
        Assert.Equal("looks like chat", route.GetProperty("reason").GetString());
        Assert.Equal(Small, hello.Single(e => Type(e) == "assistant").GetProperty("model").GetString());

        // Code: the main model, thinking a little (the nearest of the chat's levels to "medium": low); the sorting read the exchange before.
        var code = await SendAsync(b, id, "fix this loop [kind:code]");
        Assert.Contains("Person: hello there", RequestsOf(email, "You sort the questions").Last()["messages"]![1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(Main, ModelOf(AnswersOf(email).Last()));
        Assert.Equal("low", AnswersOf(email).Last()["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        var handed = code.Single(e => Type(e) == "route").GetProperty("route");
        Assert.False(handed.GetProperty("small").GetBoolean());
        Assert.Equal("low", handed.GetProperty("thinking").GetString());

        // Reasoning: the deepest level there is.
        var reasoning = await SendAsync(b, id, "prove there are infinitely many primes [kind:reasoning]");
        Assert.Equal("xhigh", AnswersOf(email).Last()["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("reasoning", reasoning.Single(e => Type(e) == "route").GetProperty("route").GetProperty("kind").GetString());

        // A word the small model cannot say: the main model answers, as the chat thinks.
        var unsure = await SendAsync(b, id, "hmm [kind:?]");
        var unknown = unsure.Single(e => Type(e) == "route").GetProperty("route");
        Assert.Equal("unknown", unknown.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("thinking").ValueKind);
        Assert.Equal(Main, ModelOf(AnswersOf(email).Last()));
        Assert.Null(AnswersOf(email).Last()["chat_template_kwargs"]);

        // Each answer keeps who answered and why.
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));
        var answers = chat.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "assistant").ToList();
        Assert.Equal(["chat", "code", "reasoning", "unknown"], answers.Select(a => a.GetProperty("details").GetProperty("route").GetProperty("kind").GetString()));
        Assert.Equal(Small, answers[0].GetProperty("model").GetString());

        // "Ask the big model": the small model's answer again, from the main model, beside it; not sorted again.
        var question = chat.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetGuid();
        var sorts = RequestsOf(email, "You sort the questions").Count;
        var big = await SendAsync(b, id, "", path: "regenerate", body: new { messageId = question, model = Main });
        Assert.Equal(sorts, RequestsOf(email, "You sort the questions").Count);
        Assert.DoesNotContain(big, e => Type(e) == "route");
        Assert.Equal(Main, ModelOf(AnswersOf(email).Last()));
        Assert.Equal(Main, big.Single(e => Type(e) == "assistant").GetProperty("model").GetString());

        // Deep research goes to the main model unasked.
        var deep = await SendAsync(b, id, "", body: new { content = "everything about tides [kind:chat]", research = true });
        Assert.Equal(sorts, RequestsOf(email, "You sort the questions").Count);
        Assert.Equal("deep", deep.Single(e => Type(e) == "route").GetProperty("route").GetProperty("kind").GetString());
        Assert.Equal(Main, ModelOf(AnswersOf(email).Last()));
    }

    [Fact]
    public async Task Auto_is_offered_only_with_a_small_model_the_person_may_use_and_can_be_the_default()
    {
        await using var f = NewApp(new() { ["Chat:DefaultModel"] = "auto" });
        var admin = await AdminAsync(f);
        var (b, _, email) = await PersonAsync(f);
        var auto = (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("auto");
        Assert.True(auto.GetProperty("byDefault").GetBoolean());
        // The real default stays the main model (what Auto hands on to).
        Assert.Equal(Main, (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("model").GetString());

        // A chat that chose no model is on Auto.
        var id = await NewChatAsync(b, new { tools = Array.Empty<string>() });
        var first = await SendAsync(b, id, "hi [kind:chat]");
        Assert.Equal(Small, first.Single(e => Type(e) == "route").GetProperty("route").GetProperty("model").GetString());
        // Choosing a model leaves Auto.
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { model = Main }));
        Assert.DoesNotContain(await SendAsync(b, id, "hi again [kind:chat]"), e => Type(e) == "route");
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { model = "auto" }));

        // Only admins may use the small model: Auto is not offered to others, nor may they choose it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/models/{Small}/access", UriKind.Relative), new { audience = "Admins" }));
        Assert.Equal(JsonValueKind.Null, (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("auto").ValueKind);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.PostAsync("/api/chat/conversations", new { model = "auto" }));
        Assert.NotEqual(JsonValueKind.Null, (await admin.JsonAsync(await admin.GetAsync("/api/chat/config"))).GetProperty("auto").ValueKind);

        // Their chat on Auto from before: the main model answers, it says why, and nothing reaches the small model.
        var sorts = RequestsOf(email, "You sort the questions").Count;
        var after = await SendAsync(b, id, "hello once more [kind:chat]");
        var route = after.Single(e => Type(e) == "route").GetProperty("route");
        Assert.Equal("unavailable", route.GetProperty("kind").GetString());
        Assert.Equal(Main, route.GetProperty("model").GetString());
        Assert.Equal(sorts, RequestsOf(email, "You sort the questions").Count);
        Assert.DoesNotContain(app.Model.Requests, r => r.Body["user"]!.GetValue<string>() == email && ModelOf(r.Body) == Small
            && r.Body["messages"]!.ToJsonString().Contains("hello once more", StringComparison.Ordinal));

        // Without a small model there is no Auto at all.
        await using var none = NewApp(smallSet: false);
        var (other, _, _) = await PersonAsync(none);
        Assert.Equal(JsonValueKind.Null, (await other.JsonAsync(await other.GetAsync("/api/chat/config"))).GetProperty("auto").ValueKind);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await other.PostAsync("/api/chat/conversations", new { model = "auto" }));
    }
}
