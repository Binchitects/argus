using System.Net;
using System.Text;
using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Admin → Prompt log: everyone's questions and answers, the chat's and the API's, for prompts.review only; every search audited.</summary>
[Collection(nameof(AppCollection))]
public sealed class PromptLogTests(AppFixture app)
{
    private static async Task<HttpResponseMessage> AskAsync(WebApplicationFactory<Program> f, string key, string text)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "Qwen3.8-Flash-Next", messages = new object[] { new { role = "system", content = "Be brief." }, new { role = "user", content = text } } }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", key);
        var res = await client.SendAsync(req);
        await res.Content.ReadAsStringAsync();
        return res;
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task The_chats_and_the_API_requests_questions_and_answers_are_read_with_prompts_review_and_each_search_is_audited()
    {
        await using var f = app.Create(app.ConnectionStringFor("prompts_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pl" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);

        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        var sent = await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Which port does Postgres use?" });
        await sent.Content.ReadAsStringAsync();
        await StatusAssert.Is(HttpStatusCode.OK, await AskAsync(f, made.GetProperty("apiKey").GetString()!, "Name a prime number."));

        // The API request is kept after its answer went out.
        List<JsonElement> items = [];
        for (var waited = 0; waited < 10_000; waited += 100)
        {
            items = [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}"))).GetProperty("items").EnumerateArray()];
            if (items.Any(i => i.GetProperty("source").GetString() == "api"))
            {
                break;
            }
            await Task.Delay(100);
        }
        var api = items.Single(i => i.GetProperty("source").GetString() == "api");
        Assert.Equal("Name a prime number.", api.GetProperty("question").GetString());
        Assert.StartsWith("Answer to:", api.GetProperty("answer").GetString(), StringComparison.Ordinal);
        var asked = items.Single(i => i.GetProperty("source").GetString() == "chat");
        Assert.Equal("Which port does Postgres use?", asked.GetProperty("question").GetString());
        Assert.StartsWith("Answer to:", asked.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Equal(chat, asked.GetProperty("chat").GetGuid());
        Assert.Equal(name, asked.GetProperty("person").GetProperty("userName").GetString());

        // Searched by words, and each search audited with what was asked.
        var found = (await admin.JsonAsync(await admin.GetAsync("/api/admin/prompts?q=postgres&source=chat"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("Which port does Postgres use?", Assert.Single(found).GetProperty("question").GetString());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "prompts.viewed" && e.GetProperty("detail").GetString()!.Contains("text \"postgres\"", StringComparison.Ordinal));

        // Not theirs to read, nor an Auditor's, who holds every ".read".
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/admin/prompts"));
        var auditorName = "au" + Guid.NewGuid().ToString("N")[..8];
        var auditor = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = auditorName, email = $"{auditorName}@example.test" }));
        var roles = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray();
        var role = roles.Single(r => r.GetProperty("key").GetString() == "auditor").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = auditor.GetProperty("id").GetGuid() }));
        var them = await new TestBrowser(f).SignedInAsync(auditorName, auditor.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/prompts"));
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task An_answer_from_the_answer_cache_is_in_the_prompt_log_as_well()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptscache_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Gateway:AnswerCache"] = "all",
            ["Gateway:KeyCacheSeconds"] = "0",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var question = "Cached " + Guid.NewGuid().ToString("N")[..8];
        await StatusAssert.Is(HttpStatusCode.OK, await AskAsync(f, key, question));
        var hit = await AskAsync(f, key, question);
        Assert.Equal("hit", hit.Headers.GetValues(AnswerCache.Header).Single());

        List<JsonElement> items = [];
        for (var waited = 0; waited < 10_000 && items.Count < 2; waited += 100)
        {
            items = [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}&source=api"))).GetProperty("items").EnumerateArray()];
            await Task.Delay(100);
        }
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(question, i.GetProperty("question").GetString()));
        Assert.Equal(items[0].GetProperty("answer").GetString(), items[1].GetProperty("answer").GetString());
    }

    private static async Task<List<JsonElement>> LogAsync(TestBrowser admin, string query) =>
        [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?{query}"))).GetProperty("items").EnumerateArray()];

    /// <summary>The log's lines for a query once <paramref name="ready"/> holds of them (the writes run behind the requests).</summary>
    private static async Task<List<JsonElement>> LogWhenAsync(TestBrowser admin, string query, Func<List<JsonElement>, bool> ready)
    {
        for (var waited = 0; ; waited += 50)
        {
            var items = await LogAsync(admin, query);
            if (ready(items) || waited > 10_000)
            {
                return items;
            }
            await Task.Delay(50);
        }
    }

    private static JsonElement Asked(List<JsonElement> items, string question) => items.Single(i => i.GetProperty("question").GetString() == question);

    private static string StateOf(List<JsonElement> items, string question) =>
        items.FirstOrDefault(i => i.GetProperty("question").GetString() == question) is { ValueKind: JsonValueKind.Object } i ? i.GetProperty("state").GetString()! : "";

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task Every_API_request_is_in_the_log_from_its_arrival_while_it_runs_and_when_it_fails_is_refused_or_is_stopped()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptstates_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Chat:ApiRequestsPerKey"] = "1",
            ["Chat:ApiQueueTimeout"] = "00:00:00.300",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ps" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;

        // Failed: with the error the caller got from the gateway.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await AskAsync(f, key, "[budget] Spend more."));

        // Running while its answer is written, and the key's next request refused once it waited past its limit.
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var leave = new CancellationTokenSource();
        using var slow = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "Qwen3.8-Flash-Next", stream = true, messages = new object[] { new { role = "user", content = "[slow] A long story." } } }), Encoding.UTF8, "application/json"),
        };
        slow.Headers.Authorization = new("Bearer", key);
        using var streaming = await client.SendAsync(slow, HttpCompletionOption.ResponseHeadersRead, leave.Token);
        await using var body = await streaming.Content.ReadAsStreamAsync(leave.Token);
        Assert.True(await body.ReadAsync(new byte[64], leave.Token) > 0);
        var items = await LogWhenAsync(admin, $"person={name}", i => StateOf(i, "[slow] A long story.") == "running");
        Assert.Equal("running", StateOf(items, "[slow] A long story."));
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, await AskAsync(f, key, "Another one, please."));

        // Stopped: the caller went away mid-answer.
        await leave.CancelAsync();
        streaming.Dispose();
        items = await LogWhenAsync(admin, $"person={name}", i => StateOf(i, "[slow] A long story.") == "stopped" && StateOf(i, "Another one, please.") == "refused"
            && StateOf(i, "[budget] Spend more.") == "failed");
        Assert.Equal("stopped", StateOf(items, "[slow] A long story."));
        var refused = Asked(items, "Another one, please.");
        Assert.Equal("refused", refused.GetProperty("state").GetString());
        Assert.Contains("waited longer than", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
        var failed = Asked(items, "[budget] Spend more.");
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.StartsWith("Budget has been exceeded!", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.True(failed.GetProperty("durationMs").GetInt32() >= 0);

        // Searched by state.
        var onlyFailed = await LogAsync(admin, $"person={name}&state=failed");
        Assert.Equal("[budget] Spend more.", Assert.Single(onlyFailed).GetProperty("question").GetString());
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task A_chat_question_is_in_the_log_with_its_answers_state_and_why_it_failed()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptchat_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pq" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await (await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Which port does Postgres use?" })).Content.ReadAsStringAsync();
        await (await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "[budget] And MySQL?" })).Content.ReadAsStringAsync();

        var items = await LogWhenAsync(admin, $"person={name}&source=chat", i => StateOf(i, "[budget] And MySQL?") == "failed");
        Assert.Equal("done", StateOf(items, "Which port does Postgres use?"));
        var failed = Asked(items, "[budget] And MySQL?");
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Equal("You have used all your credit. Ask an admin to raise it.", failed.GetProperty("error").GetString());
        Assert.Equal("[budget] And MySQL?", Assert.Single(await LogAsync(admin, $"person={name}&source=chat&state=failed")).GetProperty("question").GetString());
        Assert.Empty(await LogAsync(admin, $"person={name}&source=chat&state=refused"));
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task With_the_prompt_log_off_API_requests_are_not_kept()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptsoff_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Gateway:LogPrompts"] = "false" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "po" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        await StatusAssert.Is(HttpStatusCode.OK, await AskAsync(f, made.GetProperty("apiKey").GetString()!, "Not to be kept."));
        await Task.Delay(500);
        Assert.Empty((await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}&source=api"))).GetProperty("items").EnumerateArray());
    }
}
