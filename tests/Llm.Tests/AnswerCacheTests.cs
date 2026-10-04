using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The answer cache for API keys: repeated identical calls answered from the database, at no cost, and saying so.</summary>
[Collection(nameof(AppCollection))]
public sealed class AnswerCacheTests(AppFixture app)
{
    private const string Model = "Qwen3.8-Flash-Next";

    private (WebApplicationFactory<Program> App, FakeGateway Gateway) NewApp(string mode)
    {
        var gateway = new FakeGateway();
        var f = app.Create(app.ConnectionStringFor("cache_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Gateway:AnswerCache"] = mode,
            // What the gateway says of a key is asked every time here (blocking a key shows at once).
            ["Gateway:AnswerCacheKeyCheck"] = "00:00:00",
        });
        return (f, gateway);
    }

    /// <summary>A person and their API key, as an admin makes them.</summary>
    private static async Task<(TestBrowser Browser, string Key)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var browser = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (browser, made.GetProperty("apiKey").GetString()!);
    }

    /// <summary>A call as a pipeline makes it at gateway.DOMAIN: the key, a JSON body with its length, no cookies.</summary>
    private static async Task<HttpResponseMessage> CallAsync(WebApplicationFactory<Program> f, string key, object body, string? cacheControl = null)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", key);
        if (cacheControl is not null)
        {
            req.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
        }
        return await client.SendAsync(req);
    }

    private static object Question(string text, string model = Model, bool stream = false) => stream
        ? new { model, messages = new[] { new { role = "user", content = text } }, stream = true, stream_options = new { include_usage = true } }
        : new { model, messages = new[] { new { role = "user", content = text } } };

    /// <summary>How many times the model was asked something with this marker in it.</summary>
    private int Asked(string marker) => app.Model.Requests.Count(r => r.Body.ToJsonString().Contains(marker, StringComparison.Ordinal));

    private static string? Cache(HttpResponseMessage res) => res.Headers.TryGetValues(AnswerCache.Header, out var v) ? v.Single() : null;

    private static async Task<JsonObject> BodyAsync(HttpResponseMessage res) => JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();

    private static string Words(JsonObject completion) => completion["choices"]![0]!["message"]!["content"]!.GetValue<string>();

    [Fact]
    public async Task A_repeated_identical_API_call_is_answered_from_the_cache_costs_nothing_and_says_so()
    {
        var (f, _) = NewApp("all");
        await using var _f = f;
        var (_, key) = await PersonAsync(f);
        var marker = "faq-" + Guid.NewGuid().ToString("N")[..8];

        var first = await CallAsync(f, key, Question($"Opening hours? {marker}"));
        await StatusAssert.Is(HttpStatusCode.OK, first);
        Assert.Equal("miss", Cache(first));
        var answer = Words(await BodyAsync(first));
        Assert.StartsWith("Answer to: Opening hours?", answer, StringComparison.Ordinal);
        Assert.Equal(1, Asked(marker));
        // The gateway got the caller's own key: the spend of a miss is theirs, as without the cache.
        Assert.Equal($"Bearer {key}", app.Model.Requests.Last(r => r.Body.ToJsonString().Contains(marker, StringComparison.Ordinal)).Headers["Authorization"]);

        var again = await CallAsync(f, key, Question($"Opening hours? {marker}"));
        await StatusAssert.Is(HttpStatusCode.OK, again);
        Assert.Equal("hit", Cache(again));
        var kept = await BodyAsync(again);
        Assert.Equal(answer, Words(kept));
        Assert.Equal(0, kept["usage"]!["prompt_tokens"]!.GetValue<int>() + kept["usage"]!["completion_tokens"]!.GetValue<int>());
        // The model was not asked: nothing was spent.
        Assert.Equal(1, Asked(marker));

        // Asked for as a stream, the same answer streams as the gateway would send it.
        var streamed = await CallAsync(f, key, Question($"Opening hours? {marker}", stream: true));
        Assert.Equal("hit", Cache(streamed));
        Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType!.MediaType);
        var text = await streamed.Content.ReadAsStringAsync();
        Assert.Equal(answer, Words(AnswerCache.Assemble(text, streamed: true)!));
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
        Assert.Equal(1, Asked(marker));
    }

    [Fact]
    public async Task A_streamed_answer_passes_through_as_it_comes_and_is_kept_whole()
    {
        var (f, _) = NewApp("all");
        await using var _f = f;
        var (_, key) = await PersonAsync(f);
        var marker = "stream-" + Guid.NewGuid().ToString("N")[..8];

        var first = await CallAsync(f, key, Question($"Explain it {marker}", stream: true));
        Assert.Equal("miss", Cache(first));
        Assert.Equal("text/event-stream", first.Content.Headers.ContentType!.MediaType);
        var streamed = await first.Content.ReadAsStringAsync();
        Assert.Contains("\"reasoning_content\":\"Thinking about it.\"", streamed, StringComparison.Ordinal);

        var whole = await CallAsync(f, key, Question($"Explain it {marker}"));
        Assert.Equal("hit", Cache(whole));
        var kept = await BodyAsync(whole);
        Assert.Equal(Words(AnswerCache.Assemble(streamed, streamed: true)!), Words(kept));
        Assert.Equal("Thinking about it.", kept["choices"]![0]!["message"]!["reasoning_content"]!.GetValue<string>());
        Assert.Equal(1, Asked(marker));
    }

    [Fact]
    public async Task Answers_are_kept_per_key_and_per_model()
    {
        var (f, _) = NewApp("all");
        await using var _f = f;
        var (_, mine) = await PersonAsync(f);
        var (_, theirs) = await PersonAsync(f);
        var marker = "per-key-" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal("miss", Cache(await CallAsync(f, mine, Question(marker))));
        Assert.Equal("miss", Cache(await CallAsync(f, theirs, Question(marker))));
        Assert.Equal("miss", Cache(await CallAsync(f, mine, Question(marker, model: "Other-Model"))));
        Assert.Equal("hit", Cache(await CallAsync(f, mine, Question(marker))));
        Assert.Equal("hit", Cache(await CallAsync(f, theirs, Question(marker))));
        Assert.Equal(3, Asked(marker));
    }

    [Fact]
    public async Task With_opt_in_only_the_keys_of_people_who_turned_it_on_are_cached_and_turning_it_off_forgets_their_answers()
    {
        var (f, _) = NewApp("opt-in");
        await using var _f = f;
        var (browser, key) = await PersonAsync(f);
        var marker = "opt-" + Guid.NewGuid().ToString("N")[..8];

        var mine = await browser.JsonAsync(await browser.GetAsync("/api/account/answer-cache"));
        Assert.Equal("opt-in", mine.GetProperty("mode").GetString());
        Assert.False(mine.GetProperty("on").GetBoolean());
        Assert.Null(Cache(await CallAsync(f, key, Question(marker))));
        Assert.Null(Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal(2, Asked(marker));

        var turnedOn = await browser.JsonAsync(await browser.Http.PutAsJsonAsync(new Uri("/api/account/answer-cache", UriKind.Relative), new { on = true }));
        Assert.True(turnedOn.GetProperty("on").GetBoolean());
        Assert.Equal(24, turnedOn.GetProperty("ttlHours").GetDouble());
        Assert.Equal("miss", Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal("hit", Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal(3, Asked(marker));

        await StatusAssert.Is(HttpStatusCode.OK, await browser.Http.PutAsJsonAsync(new Uri("/api/account/answer-cache", UriKind.Relative), new { on = false }));
        Assert.Null(Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal(4, Asked(marker));
        // On again: what was kept before is gone, the model is asked anew.
        await browser.Http.PutAsJsonAsync(new Uri("/api/account/answer-cache", UriKind.Relative), new { on = true });
        Assert.Equal("miss", Cache(await CallAsync(f, key, Question(marker))));
    }

    [Fact]
    public async Task A_key_the_gateway_refuses_gets_nothing_from_the_cache()
    {
        var (f, gateway) = NewApp("all");
        await using var _f = f;
        var (_, key) = await PersonAsync(f);
        var marker = "blocked-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal("miss", Cache(await CallAsync(f, key, Question(marker))));

        // Blocked by an admin: the request goes to the gateway, which refuses it.
        await gateway.SetBlockedAsync([AnswerCache.HashOf(key)], true);
        app.Model.RevokedKeys[key] = true;
        var refused = await CallAsync(f, key, Question(marker));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Null(Cache(refused));

        // A key the gateway does not know at all is not even looked up.
        var stranger = await CallAsync(f, "sk-not-a-key", Question(marker));
        Assert.Null(Cache(stranger));
    }

    [Fact]
    public async Task Failed_answers_are_not_kept_and_no_cache_asks_the_model_again()
    {
        var (f, _) = NewApp("all");
        await using var _f = f;
        var (_, key) = await PersonAsync(f);
        var marker = "nocache-" + Guid.NewGuid().ToString("N")[..8];
        var broke = "broke-" + Guid.NewGuid().ToString("N")[..8];

        // Out of credit: the gateway's refusal passes through, and the next call asks again.
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(f, key, Question($"[budget] {broke}"))).StatusCode);
        var refusedAgain = await CallAsync(f, key, Question($"[budget] {broke}"));
        Assert.Equal(HttpStatusCode.BadRequest, refusedAgain.StatusCode);
        Assert.Equal("miss", Cache(refusedAgain));
        Assert.Equal(2, Asked(broke));

        Assert.Equal("miss", Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal("miss", Cache(await CallAsync(f, key, Question(marker), cacheControl: "no-cache")));
        Assert.Null(Cache(await CallAsync(f, key, Question(marker), cacheControl: "no-store")));
        Assert.Equal("hit", Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal(3, Asked(marker));
    }

    [Fact]
    public async Task Off_by_default_every_call_goes_to_the_model_and_the_health_check_tells_Traefik_to_go_straight_to_the_gateway()
    {
        var (f, _) = NewApp("off");
        await using var _f = f;
        var (_, key) = await PersonAsync(f);
        var marker = "off-" + Guid.NewGuid().ToString("N")[..8];
        var http = f.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(new Uri("/v1/answer-cache", UriKind.Relative))).StatusCode);
        Assert.Null(Cache(await CallAsync(f, key, Question(marker))));
        Assert.Null(Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal(2, Asked(marker));

        // Turned on in Settings: the health check says so at once, and Traefik sends the calls here.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Gateway:AnswerCache", value = "all" } } }));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri("/v1/answer-cache", UriKind.Relative))).StatusCode);
        Assert.Equal("miss", Cache(await CallAsync(f, key, Question(marker))));
        Assert.Equal("hit", Cache(await CallAsync(f, key, Question(marker))));
    }

    [Fact]
    public void Traefik_sends_chat_completions_by_the_app_only_while_its_health_check_passes()
    {
        var routes = File.ReadAllText(Path.Combine(AppFixture.PluginsPath, "..", "deploy", "config", "traefik", "routes.yml"));
        Assert.Contains("Path(`/v1/chat/completions`) || Path(`/chat/completions`)", routes, StringComparison.Ordinal);
        Assert.Contains("failover: { service: answer-cache, fallback: litellm }", routes, StringComparison.Ordinal);
        Assert.Contains("healthCheck: { path: /v1/answer-cache", routes, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exact_match_ignores_how_the_answer_is_delivered_and_who_asks_but_nothing_else()
    {
        var a = JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"hi"}],"temperature":0,"stream":true,"user":"x@example.test"}""")!.AsObject();
        var b = JsonNode.Parse("""{"temperature":0,"messages":[{"content":"hi","role":"user"}],"model":"m"}""")!.AsObject();
        var c = JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"hi"}],"temperature":0.5}""")!.AsObject();
        Assert.Equal(AnswerCache.Canonical(a), AnswerCache.Canonical(b));
        Assert.NotEqual(AnswerCache.Canonical(a), AnswerCache.Canonical(c));
    }

    [Fact]
    public void A_streamed_answer_with_thinking_and_tool_calls_is_kept_whole_and_streams_back_the_same()
    {
        var sse = string.Concat(new[]
        {
            """{"id":"c1","created":7,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Let me "}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"reasoning_content":"see."}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"find","arguments":"{\"q\":"}}]}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"x\"}"}}]}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"id":"c1","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5}}""",
        }.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
        var whole = AnswerCache.Assemble(sse, streamed: true)!;
        var message = whole["choices"]![0]!["message"]!;
        Assert.Equal("Let me see.", message["reasoning_content"]!.GetValue<string>());
        Assert.Equal("find", message["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("{\"q\":\"x\"}", message["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("tool_calls", whole["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal(10, whole["usage"]!["prompt_tokens"]!.GetValue<int>());

        var again = AnswerCache.Assemble(AnswerCache.Render(whole, streamed: true, includeUsage: true), streamed: true)!;
        Assert.Equal(message.ToJsonString(), again["choices"]![0]!["message"]!.ToJsonString());
        Assert.Equal(0, again["usage"]!["prompt_tokens"]!.GetValue<int>());

        // Cut short (no finish, no [DONE]): not an answer to keep.
        Assert.Null(AnswerCache.Assemble(sse[..sse.IndexOf("\"finish_reason\"", StringComparison.Ordinal)], streamed: true));
    }
}
