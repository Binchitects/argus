using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Rate limits for API keys: requests and tokens a minute, the company's, a group's and a
/// person's own, put on each key at the gateway (LiteLLM counts and answers 429 itself); what
/// people see of them, and what admins see of the refusals.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class RateLimitTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeGateway gateway) =>
        app.Create(app.ConnectionStringFor("rates_" + Guid.NewGuid().ToString("N")[..8]), gateway);

    private static async Task<TestBrowser> AdminAsync(WebApplicationFactory<Program> f) => await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<(Guid Id, string Name, string Email, string Password, string Key)> PersonAsync(TestBrowser admin)
    {
        var name = "rl" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (made.GetProperty("id").GetGuid(), name, $"{name}@example.test", made.GetProperty("password").GetString()!, made.GetProperty("apiKey").GetString()!);
    }

    private static async Task<Guid> GroupAsync(TestBrowser admin, object policies, params Guid[] members)
    {
        var id = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "rl" + Guid.NewGuid().ToString("N")[..8] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{id}/policies", UriKind.Relative), policies));
        if (members.Length > 0)
        {
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = members }));
        }
        return id;
    }

    private static Task<HttpResponseMessage> LimitsAsync(TestBrowser admin, Guid person, int? requests, int? tokens) =>
        admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{person}/limits", UriKind.Relative), new { requestsPerMinute = requests, tokensPerMinute = tokens });

    private static Task<HttpResponseMessage> CompanyAsync(TestBrowser admin, int requests, int tokens) =>
        admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new
        {
            changes = new[]
            {
                new { key = "Gateway:RequestsPerMinute", value = requests.ToString(CultureInfo.InvariantCulture) },
                new { key = "Gateway:TokensPerMinute", value = tokens.ToString(CultureInfo.InvariantCulture) },
            },
        });

    private static KeyRate RateOf(FakeGateway gateway, string email) => gateway.KeysOf(email).Single().Rate;

    /// <summary>Waits (up to 15 seconds) for the key sync to give the person's key this rate.</summary>
    private static async Task KeyHasAsync(FakeGateway gateway, string email, KeyRate want)
    {
        for (var i = 0; i < 150 && RateOf(gateway, email) != want; i++)
        {
            await Task.Delay(100);
        }
        Assert.Equal(want, RateOf(gateway, email));
    }

    [Fact]
    public void One_limit_is_the_persons_own_else_the_most_generous_group_else_the_companys()
    {
        Assert.Equal(new RateLimit(null, "none"), RateLimits.Resolve(null, [], 0));
        Assert.Equal(new RateLimit(10, "company"), RateLimits.Resolve(null, [("A", null)], 10));
        Assert.Equal(new RateLimit(40, "group", "B"), RateLimits.Resolve(null, [("A", 20), ("B", 40), ("C", null)], 10));
        // A group's 0 is no limit: the most generous of all.
        Assert.Equal(new RateLimit(null, "group", "C"), RateLimits.Resolve(null, [("A", 20), ("C", 0)], 10));
        // A group's own replaces the company's, lower too.
        Assert.Equal(new RateLimit(5, "group", "A"), RateLimits.Resolve(null, [("A", 5)], 10));
        Assert.Equal(new RateLimit(3, "person"), RateLimits.Resolve(3, [("A", 20)], 10));
        Assert.Equal(new RateLimit(null, "person"), RateLimits.Resolve(0, [("A", 20)], 10));
    }

    [Fact]
    public async Task Nothing_is_limited_until_an_admin_sets_a_limit()
    {
        var admin = await AdminAsync(app.Factory);
        var p = await PersonAsync(admin);
        Assert.Equal(KeyRate.None, RateOf(app.Gateway, p.Email));
        var me = await new TestBrowser(app.Factory).SignedInAsync(p.Name, p.Password);
        var limits = await me.JsonAsync(await me.GetAsync("/api/account/keys/limits"));
        foreach (var name in new[] { "requestsPerMinute", "tokensPerMinute" })
        {
            Assert.Equal(JsonValueKind.Null, limits.GetProperty(name).GetProperty("value").ValueKind);
            Assert.Equal("none", limits.GetProperty(name).GetProperty("from").GetString());
        }
        // Requests at once, per key, as before.
        Assert.Equal(2, limits.GetProperty("atOnce").GetInt32());
        // The keys' list (the home page's credit card reads it too) does not read the gateway's request log.
        Assert.False((await me.JsonAsync(await me.GetAsync("/api/account/keys"))).TryGetProperty("limits", out _));
    }

    [Fact]
    public async Task The_companys_limits_reach_every_key_at_once_and_new_keys_are_made_with_them()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await AdminAsync(f);
        var p = await PersonAsync(admin);
        Assert.Equal(KeyRate.None, RateOf(gateway, p.Email));

        await StatusAssert.Is(HttpStatusCode.OK, await CompanyAsync(admin, 30, 50_000));
        await KeyHasAsync(gateway, p.Email, new KeyRate(30, 50_000));
        var view = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{p.Id}"))).GetProperty("limits");
        Assert.Equal((30, "company"), (view.GetProperty("requestsPerMinute").GetProperty("value").GetInt32(), view.GetProperty("requestsPerMinute").GetProperty("from").GetString()));

        // Someone added now has their key made with them: no moment without.
        var q = await PersonAsync(admin);
        Assert.Equal(new KeyRate(30, 50_000), RateOf(gateway, q.Email));
        Assert.DoesNotContain(gateway.RateChanges, c => c.Token == gateway.KeysOf(q.Email).Single().Token);

        await StatusAssert.Is(HttpStatusCode.OK, await CompanyAsync(admin, 0, 0));
        await KeyHasAsync(gateway, p.Email, KeyRate.None);
        await KeyHasAsync(gateway, q.Email, KeyRate.None);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await CompanyAsync(admin, -1, 0));
    }

    [Fact]
    public async Task A_group_sets_its_members_limits_the_most_generous_group_wins_and_a_persons_own_wins_over_all()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await AdminAsync(f);
        await StatusAssert.Is(HttpStatusCode.OK, await CompanyAsync(admin, 10, 0));
        var p = await PersonAsync(admin);
        Assert.Equal(new KeyRate(10, null), RateOf(gateway, p.Email));

        await GroupAsync(admin, new { requestsPerMinute = 20 }, p.Id);
        await KeyHasAsync(gateway, p.Email, new KeyRate(20, null));
        var b = await GroupAsync(admin, new { requestsPerMinute = 40, tokensPerMinute = 1000 }, p.Id);
        await KeyHasAsync(gateway, p.Email, new KeyRate(40, 1000));
        var unlimited = await GroupAsync(admin, new { requestsPerMinute = 0 }, p.Id);
        await KeyHasAsync(gateway, p.Email, new KeyRate(null, 1000));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{unlimited}/members/{p.Id}", UriKind.Relative)));
        await KeyHasAsync(gateway, p.Email, new KeyRate(40, 1000));
        var group = await admin.JsonAsync(await admin.GetAsync($"/api/admin/groups/{b}"));
        Assert.Equal((40, 1000), (group.GetProperty("policies").GetProperty("requestsPerMinute").GetInt32(), group.GetProperty("policies").GetProperty("tokensPerMinute").GetInt32()));
        var view = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{p.Id}"))).GetProperty("limits").GetProperty("requestsPerMinute");
        Assert.Equal(("group", group.GetProperty("name").GetString()), (view.GetProperty("from").GetString(), view.GetProperty("group").GetString()));

        // Their own: on their key at once, no sync needed.
        Assert.Equal(JsonValueKind.Null, (await admin.JsonAsync(await LimitsAsync(admin, p.Id, 5, null))).GetProperty("warning").ValueKind);
        Assert.Equal(new KeyRate(5, 1000), RateOf(gateway, p.Email));
        await LimitsAsync(admin, p.Id, 0, 0);
        Assert.Equal(KeyRate.None, RateOf(gateway, p.Email));
        var own = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{p.Id}"))).GetProperty("limits");
        Assert.Equal((0, "person"), (own.GetProperty("own").GetProperty("requestsPerMinute").GetInt32(), own.GetProperty("requestsPerMinute").GetProperty("from").GetString()));
        await LimitsAsync(admin, p.Id, null, null);
        Assert.Equal(new KeyRate(40, 1000), RateOf(gateway, p.Email));

        await StatusAssert.Is(HttpStatusCode.BadRequest, await LimitsAsync(admin, p.Id, -1, null));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await LimitsAsync(admin, p.Id, null, RateLimits.MaxTokens + 1));
        await StatusAssert.Is(HttpStatusCode.BadRequest,
            await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{b}/policies", UriKind.Relative), new { requestsPerMinute = -5 }));

        // Every change is audited, with the limits in words.
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "person.set_limits" && e.GetProperty("target").GetString() == p.Name
            && e.GetProperty("detail").GetString() == "requests a minute 5; tokens a minute from their groups or the company");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "group.policies" && e.GetProperty("detail").GetString()!.Contains("requests a minute 40; tokens a minute 1,000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_replicas_give_the_keys_one_set_of_limits_whichever_one_an_admin_uses()
    {
        var db = app.ConnectionStringFor("ratescale_" + Guid.NewGuid().ToString("N")[..8]);
        var gateway = new FakeGateway();
        var settings = new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-rate-tests", ["Replicas:Renew"] = "00:00:01" };
        await using var a = app.Create(db, gateway, settings);
        await using var b = app.Create(db, gateway, settings);
        await Task.WhenAll(Task.Run(() => a.Server), Task.Run(() => b.Server));
        static Llm.Api.Operations.Replicas Of(WebApplicationFactory<Program> f) => f.Services.GetRequiredService<Llm.Api.Operations.Replicas>();
        for (var i = 0; i < 200 && !(Of(a).Count == 2 && Of(b).Count == 2 && Of(a).IsLeader != Of(b).IsLeader); i++)
        {
            await Task.Delay(100);
        }
        Assert.True(Of(a).IsLeader != Of(b).IsLeader, "one replica leads");
        // The admin works on the replica that does not lead: the one that leads puts the limits on the keys.
        var (follower, other) = Of(a).IsLeader ? (b, a) : (a, b);
        var admin = await AdminAsync(follower);
        var p = await PersonAsync(admin);
        await StatusAssert.Is(HttpStatusCode.OK, await CompanyAsync(admin, 25, 0));
        await KeyHasAsync(gateway, p.Email, new KeyRate(25, null));
        await GroupAsync(admin, new { requestsPerMinute = 50 }, p.Id);
        await KeyHasAsync(gateway, p.Email, new KeyRate(50, null));
        foreach (var f in new[] { follower, other })
        {
            var me = await new TestBrowser(f).SignedInAsync(p.Name, p.Password);
            var limit = (await me.JsonAsync(await me.GetAsync("/api/account/keys/limits"))).GetProperty("requestsPerMinute");
            Assert.Equal((50, "group"), (limit.GetProperty("value").GetInt32(), limit.GetProperty("from").GetString()));
        }
    }

    [Fact]
    public async Task A_new_key_keeps_its_persons_limits_and_a_gateway_that_is_down_gets_them_later()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await AdminAsync(f);
        var p = await PersonAsync(admin);
        await LimitsAsync(admin, p.Id, 7, 7000);
        var old = gateway.KeysOf(p.Email).Single().Token;
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/people/{p.Id}/key"));
        var made = gateway.KeysOf(p.Email).Single();
        Assert.NotEqual(old, made.Token);
        Assert.Equal(new KeyRate(7, 7000), made.Rate);

        // Saved while the gateway is down: said so, and the key gets it at the next check.
        gateway.Down = true;
        var res = await LimitsAsync(admin, p.Id, 9, null);
        gateway.Down = false;
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.Contains("next check", (await admin.JsonAsync(res)).GetProperty("warning").GetString(), StringComparison.Ordinal);
        Assert.Equal(new KeyRate(7, 7000), made.Rate);
        await CompanyAsync(admin, 0, 1); // any change of the company's wakes the check
        await KeyHasAsync(gateway, p.Email, new KeyRate(9, 1));
    }

    /// <summary>Holds the first call a fake gateway's hook sees for one person or key until released, and says when it holds it.</summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _once;

        public Task Held => _held.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public Func<string, Task> For(string target) => async seen =>
        {
            if (seen == target && Interlocked.Exchange(ref _once, 1) == 0)
            {
                _held.SetResult();
                await _go.Task;
            }
        };

        public void Release() => _go.TrySetResult();
    }

    [Fact]
    public async Task A_persons_own_limit_set_while_a_key_sync_runs_wins_over_what_the_sync_read()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await AdminAsync(f);
        var p = await PersonAsync(admin);
        var hold = new Hold();
        gateway.BeforeKeys = hold.For(p.Email);
        await using var scope = f.Services.CreateAsyncScope();
        // A sync reads everyone (no limit of their own yet) and is held at the person's keys...
        var sync = scope.ServiceProvider.GetRequiredService<RateLimits>().SyncAsync();
        try
        {
            await hold.Held;
            // ...while an admin sets their own, which is on their key at once.
            await StatusAssert.Is(HttpStatusCode.OK, await LimitsAsync(admin, p.Id, 5, 500));
            Assert.Equal(new KeyRate(5, 500), RateOf(gateway, p.Email));
        }
        finally
        {
            hold.Release();
        }
        await sync;
        // The sync read their own again before it changed their key: what the admin set stays.
        Assert.Equal(new KeyRate(5, 500), RateOf(gateway, p.Email));
        Assert.DoesNotContain(gateway.RateChanges, c => c.Rate == KeyRate.None);
    }

    [Fact]
    public async Task A_sync_that_undoes_a_persons_own_limit_is_followed_by_one_that_puts_it_back()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await AdminAsync(f);
        var p = await PersonAsync(admin);
        var token = gateway.KeysOf(p.Email).Single().Token;
        var group = await GroupAsync(admin, new { requestsPerMinute = 20 }, p.Id);
        await KeyHasAsync(gateway, p.Email, new KeyRate(20, null));
        // The syncs those changes woke are done.
        await Task.Delay(500);
        var hold = new Hold();
        gateway.BeforeRate = hold.For(token);
        try
        {
            // A new limit for the group starts one sync, held just as it gives the key the group's 30...
            await StatusAssert.Is(HttpStatusCode.NoContent,
                await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{group}/policies", UriKind.Relative), new { requestsPerMinute = 30 }));
            await hold.Held;
            // ...while an admin sets the person's own, on their key at once.
            await StatusAssert.Is(HttpStatusCode.OK, await LimitsAsync(admin, p.Id, 5, null));
            Assert.Equal(new KeyRate(5, null), RateOf(gateway, p.Email));
        }
        finally
        {
            hold.Release();
        }
        // The held sync gives the key the 30 it decided before; the sync the change woke puts their own back.
        for (var i = 0; i < 150 && !gateway.RateChanges.Contains((token, new KeyRate(30, null))); i++)
        {
            await Task.Delay(100);
        }
        Assert.Contains((token, new KeyRate(30, null)), gateway.RateChanges);
        await KeyHasAsync(gateway, p.Email, new KeyRate(5, null));
    }

    [Fact]
    public async Task A_person_makes_a_few_new_keys_an_hour_so_a_new_key_is_no_way_round_their_limits()
    {
        var admin = await AdminAsync(app.Factory);
        var p = await PersonAsync(admin);
        await LimitsAsync(admin, p.Id, 10, null);
        var me = await new TestBrowser(app.Factory).SignedInAsync(p.Name, p.Password);
        for (var i = 0; i < Llm.Api.Identity.PeopleService.OwnKeysPerHour; i++)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await me.PostAsync("/api/account/keys/rotate"));
        }
        var key = app.Gateway.KeysOf(p.Email).Single();
        Assert.Equal(new KeyRate(10, null), key.Rate);

        // One more within the hour: refused, said why and how long to wait, audited; the key stays.
        var res = await me.PostAsync("/api/account/keys/rotate");
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, res);
        var wait = int.Parse(res.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture);
        Assert.InRange(wait, 3000, 3600);
        Assert.StartsWith($"You made {Llm.Api.Identity.PeopleService.OwnKeysPerHour} new keys in the last hour", (await me.JsonAsync(res)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(key.Token, app.Gateway.KeysOf(p.Email).Single().Token);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "person.rotate_key" && e.GetProperty("target").GetString() == p.Name
            && !e.GetProperty("success").GetBoolean());

        // An admin still makes one for them, and it does not count against theirs.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.PostAsync($"/api/admin/people/{p.Id}/key"));
        Assert.NotEqual(key.Token, app.Gateway.KeysOf(p.Email).Single().Token);
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, await me.PostAsync("/api/account/keys/rotate"));
    }

    /// <summary>One Arena MCP tool call with the person's key, as an outside agent makes it: the result's text and whether it is an error.</summary>
    private static async Task<(string Text, bool IsError)> McpCallAsync(WebApplicationFactory<Program> f, string key, string function, object arguments)
    {
        using var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        var res = await http.PostAsync(new Uri($"https://{AppFixture.Domain}/mcp"),
            JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = function, arguments } }));
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        var answer = res.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split("\n\n").Where(e => e.StartsWith("data: ", StringComparison.Ordinal)).Select(e => JsonDocument.Parse(e[6..]).RootElement).Single(m => m.TryGetProperty("id", out _))
            : JsonDocument.Parse(body).RootElement;
        var result = answer.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Pictures_made_through_Arena_MCP_count_against_the_persons_requests_a_minute()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var f = app.Create(app.ConnectionStringFor("ratesmcp_" + Guid.NewGuid().ToString("N")[..8]), gateway,
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-rate-tests" });
        var admin = await AdminAsync(f);
        var p = await PersonAsync(admin);
        await LimitsAsync(admin, p.Id, 2, null);
        var picture = new { prompt = "A red fox in the snow" };

        // Two a minute: the third is refused, says which limit, and is audited; a tool that reaches no model still runs.
        Assert.False((await McpCallAsync(f, p.Key, "generate_image", picture)).IsError);
        Assert.False((await McpCallAsync(f, p.Key, "generate_image", picture)).IsError);
        var (text, isError) = await McpCallAsync(f, p.Key, "generate_image", picture);
        Assert.True(isError);
        Assert.StartsWith("Your API key reached its limit of 2 requests a minute; pictures, speech and video made through Arena MCP count too.", text, StringComparison.Ordinal);
        Assert.False((await McpCallAsync(f, p.Key, "calculate", new { expression = "1 + 1" })).IsError);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        var refusal = Assert.Single(audit, e => e.GetProperty("action").GetString() == RateLimits.McpRefused);
        Assert.Equal(("image", "generate_image", p.Name, false),
            (refusal.GetProperty("target").GetString(), refusal.GetProperty("detail").GetString(), refusal.GetProperty("actor").GetString(), refusal.GetProperty("success").GetBoolean()));

        // Their key's card counts them as requests of the last minute, and the refusal.
        var me = await new TestBrowser(f).SignedInAsync(p.Name, p.Password);
        var limits = await me.JsonAsync(await me.GetAsync("/api/account/keys/limits"));
        Assert.Equal(2, limits.GetProperty("used").GetProperty("requests").GetInt64());
        Assert.Equal(1, limits.GetProperty("refused").EnumerateArray().Single(r => r.GetProperty("limit").GetString() == "requests").GetProperty("count").GetInt64());

        // What their key sent through the gateway this minute counts too.
        var q = await PersonAsync(admin);
        await LimitsAsync(admin, q.Id, 1, null);
        var run = "rl-" + Guid.NewGuid().ToString("N")[..8] + "-";
        try
        {
            await LogAsync(run + "1", gateway.KeysOf(q.Email).Single().Token, q.Email, DateTimeOffset.UtcNow.AddSeconds(-5), total: 10);
            var (one, refused) = await McpCallAsync(f, q.Key, "generate_image", picture);
            Assert.True(refused);
            // One request, not "1 requests".
            Assert.StartsWith("Your API key reached its limit of 1 request a minute;", one, StringComparison.Ordinal);
        }
        finally
        {
            await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
            await conn.OpenAsync();
            await using var delete = new NpgsqlCommand("""delete from "LiteLLM_SpendLogs" where request_id like @run""", conn);
            delete.Parameters.AddWithValue("run", run + "%");
            await delete.ExecuteNonQueryAsync();
        }

        // Someone with no limit is never counted.
        var r = await PersonAsync(admin);
        for (var i = 0; i < 3; i++)
        {
            Assert.False((await McpCallAsync(f, r.Key, "generate_image", picture)).IsError);
        }
    }

    [Fact]
    public void Arena_MCP_calls_started_at_once_each_count_until_they_end()
    {
        var calls = new ModelCalls();
        var p = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = calls.TryStart(p, now, n => n < 2);
        var second = calls.TryStart(p, now, n => n < 2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(calls.TryStart(p, now, n => n < 2));
        // Someone else's are their own.
        Assert.NotNull(calls.TryStart(Guid.NewGuid(), now, n => n < 1));
        second!.Dispose();
        second.Dispose();
        Assert.Equal(1, calls.Running(p, now));
        Assert.NotNull(calls.TryStart(p, now, n => n < 2));
        first!.Dispose();
    }

    [Fact]
    public void A_running_call_counts_in_the_minute_it_started_not_for_as_long_as_it_runs()
    {
        var calls = new ModelCalls();
        var p = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        // One a minute: a video starts...
        using var video = calls.TryStart(p, start, n => n < 1);
        Assert.NotNull(video);
        Assert.Null(calls.TryStart(p, start.AddSeconds(59), n => n < 1));
        // ...and still runs a minute later, but its minute is over: a picture may start.
        Assert.Equal(0, calls.Running(p, start.AddSeconds(61)));
        using var picture = calls.TryStart(p, start.AddSeconds(61), n => n < 1);
        Assert.NotNull(picture);
        Assert.Equal(1, calls.Running(p, start.AddSeconds(62)));
    }

    [Fact]
    public async Task A_slow_Arena_MCP_call_counts_in_the_minute_it_started_while_it_runs_and_once_it_is_audited()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        // Whole seconds: the audit log keeps microseconds.
        var clock = new MovableClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var f = app.Create(app.ConnectionStringFor("ratesslow_" + Guid.NewGuid().ToString("N")[..8]), gateway,
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-rate-tests" }, s => s.AddSingleton<TimeProvider>(clock));
        var admin = await AdminAsync(f);
        var p = await PersonAsync(admin);
        await LimitsAsync(admin, p.Id, 1, null);
        var picture = new { prompt = "A red fox in the snow" };
        var hold = new Hold();
        app.Model.BeforeImage = hold.For("A slow fox");
        try
        {
            // One a minute: a slow picture starts, and while its minute lasts nothing else may.
            var start = clock.Now;
            var slow = McpCallAsync(f, p.Key, "generate_image", new { prompt = "A slow fox" });
            await hold.Held;
            Assert.True((await McpCallAsync(f, p.Key, "generate_image", picture)).IsError);

            // A minute after it started it still runs, but no longer counts: the gateway counts a request once, when it starts.
            clock.Now = start.AddSeconds(61);
            Assert.False((await McpCallAsync(f, p.Key, "generate_image", picture)).IsError);

            // It ends when that minute is over too, and is audited at its start: it does not count again.
            clock.Now = start.AddSeconds(122);
            hold.Release();
            Assert.False((await slow).IsError);
            Assert.False((await McpCallAsync(f, p.Key, "generate_image", picture)).IsError);
            var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray()
                .Where(e => e.GetProperty("action").GetString() == "mcp.call" && e.GetProperty("actor").GetString() == p.Name).ToList();
            Assert.Equal([start, start.AddSeconds(61), start.AddSeconds(122)], audit.Select(e => e.GetProperty("at").GetDateTimeOffset()).Order());
        }
        finally
        {
            app.Model.BeforeImage = null;
            hold.Release();
        }
    }

    [Fact]
    public async Task The_chat_is_never_held_to_a_persons_rate_limits()
    {
        var admin = await AdminAsync(app.Factory);
        var p = await PersonAsync(admin);
        await LimitsAsync(admin, p.Id, 1, 1);
        Assert.Equal(new KeyRate(1, 1), RateOf(app.Gateway, p.Email));
        var me = await new TestBrowser(app.Factory).SignedInAsync(p.Name, p.Password);
        var before = app.Model.Requests.Count;
        for (var i = 0; i < 2; i++)
        {
            var id = (await me.JsonAsync(await me.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
            var stream = await (await me.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "hello" })).Content.ReadAsStringAsync();
            Assert.Contains("\"done\"", stream, StringComparison.Ordinal);
        }
        // The chat's requests go with the chat's own key, which carries no limit; the person's key is not used.
        var keys = app.Model.Requests.Skip(before).Select(r => r.Headers.GetValueOrDefault("Authorization", "")).Distinct().ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.Contains(k.Replace("Bearer ", "", StringComparison.Ordinal), app.Gateway.ServiceKeys));
        Assert.All(keys, k => Assert.DoesNotContain(app.Gateway.Keys.Values, g => "Bearer " + g.Secret == k));
    }

    /// <summary>A request in the gateway's log for this key, as LiteLLM writes it: answered, or refused with this error.</summary>
    private async Task LogAsync(string id, string token, string email, DateTimeOffset at, int total = 0, int cached = 0, (string Class, string Message)? error = null)
    {
        await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
        await conn.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, total_tokens, prompt_tokens, completion_tokens,
              "startTime", "endTime", model, "user", metadata, end_user, status)
            values (@id, 'acompletion', @key, 0, @total, @total, 0, @at, @at, 'qwen', @email, @metadata::jsonb, '', @status)
            """, conn);
        var metadata = new Dictionary<string, object> { ["user_api_key_user_id"] = email, ["user_api_key_alias"] = "app-" + email.Split('@')[0] };
        if (cached > 0)
        {
            metadata["usage_object"] = new { prompt_tokens_details = new { cached_tokens = cached } };
        }
        if (error is { } e)
        {
            metadata["status"] = "failure";
            metadata["error_information"] = new { error_code = "429", error_class = e.Class, error_message = e.Message };
        }
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("key", token);
        insert.Parameters.AddWithValue("total", total);
        insert.Parameters.AddWithValue("at", DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified));
        insert.Parameters.AddWithValue("email", email);
        insert.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(metadata));
        insert.Parameters.AddWithValue("status", error is null ? "success" : "failure");
        await insert.ExecuteNonQueryAsync();
    }

    /// <summary>A dashboard panel's table (Usage by person's unless said), by its title, for a time range.</summary>
    private static async Task<JsonElement> PanelAsync(TestBrowser admin, string title, DateTimeOffset from, DateTimeOffset to, string dashboard = "usage-by-user")
    {
        var d = await admin.JsonAsync(await admin.GetAsync($"/api/dashboards/{dashboard}"));
        var key = d.GetProperty("panels").EnumerateArray().Single(x => x.GetProperty("title").GetString() == title).GetProperty("key").GetInt32();
        var res = await admin.Http.PostAsJsonAsync(new Uri($"/api/dashboards/{dashboard}/panels/{key}/query", UriKind.Relative), new { from, to });
        return (await admin.JsonAsync(res)).GetProperty("results")[0].GetProperty("table");
    }

    [Fact]
    public async Task The_dashboards_count_refused_requests_as_refused_not_as_requests_or_failures()
    {
        var admin = await AdminAsync(app.Factory);
        var p = await PersonAsync(admin);
        var token = app.Gateway.KeysOf(p.Email).Single().Token;
        // A second of its own, long ago: no other test's rows are in it.
        var at = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(Random.Shared.Next(86_000));
        var run = "rl-" + Guid.NewGuid().ToString("N")[..8] + "-";
        try
        {
            await LogAsync(run + "1", token, p.Email, at, total: 100);
            await LogAsync(run + "2", token, p.Email, at, total: 50);
            await LogAsync(run + "3", token, p.Email, at, error: ("BudgetExceededError", "Budget has been exceeded!"));
            await LogAsync(run + "4", token, p.Email, at, error: Over(token, "requests"));
            await LogAsync(run + "5", token, p.Email, at, error: Over(token, "tokens"));
            var (from, to) = (at.AddSeconds(-1), at.AddSeconds(1));
            double Stat(JsonElement table) => table.GetProperty("rows")[0][0].GetDouble();
            Assert.Equal(3, Stat(await PanelAsync(admin, "Requests", from, to)));
            Assert.Equal(3, Stat(await PanelAsync(admin, "Requests", from, to, "llm-overview")));
            // Refused for credit is a failure; refused for a key's rate limit is the client's limit.
            Assert.Equal(1, Stat(await PanelAsync(admin, "Failures", from, to, "llm-overview")));
            Assert.Equal(3, (await PanelAsync(admin, "By model", from, to, "llm-overview")).GetProperty("rows")[0][1].GetDouble());
            Assert.Equal(2, (await PanelAsync(admin, "Refused by rate limits", from, to)).GetProperty("rows").EnumerateArray().Sum(r => r[3].GetDouble()));
        }
        finally
        {
            await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
            await conn.OpenAsync();
            await using var delete = new NpgsqlCommand("""delete from "LiteLLM_SpendLogs" where request_id like @run""", conn);
            delete.Parameters.AddWithValue("run", run + "%");
            await delete.ExecuteNonQueryAsync();
        }
    }

    private static (string, string) Over(string token, string limit) =>
        ("ProxyRateLimitError", $"litellm.RateLimitError: Rate limit exceeded for api_key: {token}. Limit type: {limit}. Current limit: 60, Remaining: 0. Limit resets at: 2026-10-09 10:01:00 UTC");

    [Fact]
    public async Task People_see_their_limits_what_they_used_this_minute_and_what_was_refused_and_admins_see_who_was_refused()
    {
        var admin = await AdminAsync(app.Factory);
        var p = await PersonAsync(admin);
        var other = await PersonAsync(admin);
        await LimitsAsync(admin, p.Id, 60, null);
        var token = app.Gateway.KeysOf(p.Email).Single().Token;
        var now = DateTimeOffset.UtcNow;
        var run = "rl-" + Guid.NewGuid().ToString("N")[..8] + "-";
        try
        {
            await LogAsync(run + "1", token, p.Email, now.AddSeconds(-15), total: 500, cached: 100);
            await LogAsync(run + "2", token, p.Email, now.AddSeconds(-30), total: 100);
            await LogAsync(run + "3", token, p.Email, now.AddMinutes(-5), total: 999);
            await LogAsync(run + "4", token, p.Email, now.AddSeconds(-10), error: Over(token, "requests"));
            await LogAsync(run + "5", token, p.Email, now.AddHours(-2), error: Over(token, "tokens"));
            await LogAsync(run + "6", token, p.Email, now.AddHours(-3), error: Over(token, "max_parallel_requests"));
            // A key of theirs that is gone (they made a new one): its refusals are still theirs.
            await LogAsync(run + "10", "hash-of-their-old-key", p.Email, now.AddHours(-1), error: Over("hash-of-their-old-key", "requests"));
            // Not a rate limit: refused for credit. Nor this one: two days ago. Nor another person's.
            await LogAsync(run + "7", token, p.Email, now.AddSeconds(-20), error: ("BudgetExceededError", "Budget has been exceeded!"));
            await LogAsync(run + "8", token, p.Email, now.AddDays(-2), error: Over(token, "requests"));
            await LogAsync(run + "9", app.Gateway.KeysOf(other.Email).Single().Token, other.Email, now.AddSeconds(-10), error: Over("x", "requests"));

            var me = await new TestBrowser(app.Factory).SignedInAsync(p.Name, p.Password);
            var limits = await me.JsonAsync(await me.GetAsync("/api/account/keys/limits"));
            Assert.Equal((60, "person"), (limits.GetProperty("requestsPerMinute").GetProperty("value").GetInt32(), limits.GetProperty("requestsPerMinute").GetProperty("from").GetString()));
            // Two requests in the last minute; their tokens less the prompt read from the cache.
            Assert.Equal((2, 500), (limits.GetProperty("used").GetProperty("requests").GetInt64(), limits.GetProperty("used").GetProperty("tokens").GetInt64()));
            var refused = limits.GetProperty("refused").EnumerateArray().ToDictionary(r => r.GetProperty("limit").GetString()!, r => r.GetProperty("count").GetInt64());
            Assert.Equal(new Dictionary<string, long> { ["requests"] = 2, ["tokens"] = 1, ["at once"] = 1 }, refused);
            // The admin's page of them says the same.
            var seen = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{p.Id}"))).GetProperty("limits");
            Assert.Equal(2, seen.GetProperty("used").GetProperty("requests").GetInt64());
            Assert.Equal(3, seen.GetProperty("refused").GetArrayLength());

            // The Usage by person dashboard: who was refused, with which key, for which limit.
            var table = await PanelAsync(admin, "Refused by rate limits", now.AddDays(-1), now.AddMinutes(1));
            var cols = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
            Assert.Equal(["Person", "Key", "Limit", "Refused", "Last"], cols);
            var rows = table.GetProperty("rows").EnumerateArray().Where(r => r[0].GetString() == p.Email)
                .ToDictionary(r => r[2].GetString()!, r => (Key: r[1].GetString(), Count: r[3].GetDouble()));
            Assert.Equal(new Dictionary<string, (string?, double)>
            {
                ["requests a minute"] = ("app-" + p.Name, 2), ["tokens a minute"] = ("app-" + p.Name, 1), ["requests at once"] = ("app-" + p.Name, 1),
            }, rows);
            Assert.Contains(table.GetProperty("rows").EnumerateArray(), r => r[0].GetString() == other.Email);

            // A refusal never reached a model: their usage and the dashboard's requests count the four that did (one refused for credit), not the four refused.
            var q = $"from={Uri.EscapeDataString(now.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(now.AddMinutes(1).ToString("o"))}";
            Assert.Equal(4, (await me.JsonAsync(await me.GetAsync("/api/usage/me?" + q))).GetProperty("totals").GetProperty("requests").GetDouble());
            foreach (var panel in new[] { "Per person — every surface combined", "Tokens and cost per person" })
            {
                var people = (await PanelAsync(admin, panel, now.AddDays(-1), now.AddMinutes(1))).GetProperty("rows").EnumerateArray();
                Assert.Equal(4, people.Single(r => r[0].GetString() == p.Email)[1].GetDouble());
            }
            var recent = (await PanelAsync(admin, "Recent requests, resolved", now.AddDays(-1), now.AddMinutes(1))).GetProperty("rows").EnumerateArray();
            Assert.Equal(4, recent.Count(r => r[1].GetString() == p.Email));
        }
        finally
        {
            await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
            await conn.OpenAsync();
            await using var delete = new NpgsqlCommand("""delete from "LiteLLM_SpendLogs" where request_id like @run""", conn);
            delete.Parameters.AddWithValue("run", run + "%");
            await delete.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task A_v5_2_0_database_keeps_its_people_and_groups_with_no_limits()
    {
        var cs = app.ConnectionStringFor("ratesup_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_RateLimits", StringComparison.Ordinal)) - 1]);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DisplayName","Source","IsDisabled","CreatedAt","CacheApiAnswers","MemoryOff")
                  VALUES ('00000000-0000-0000-0000-0000000000b1','old','OLD','old@example.test','OLD@EXAMPLE.TEST',true,false,false,true,0,'Old',0,false,now(),false,false);
                INSERT INTO groups ("Id","Name","Scim","Priority","CreatedAt","CreditPerMember","Credit")
                  VALUES ('00000000-0000-0000-0000-0000000000b2','Old group',false,0,now(),false,5);
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await migrator.MigrateAsync();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.UserName == "old");
        var group = await db.Groups.AsNoTracking().SingleAsync(g => g.Name == "Old group");
        Assert.Equal(("old@example.test", (int?)null, (int?)null), (user.Email, user.RequestsPerMinute, user.TokensPerMinute));
        Assert.Equal((5m, (int?)null, (int?)null), (group.Credit, group.RequestsPerMinute, group.TokensPerMinute));
        // Nothing set anywhere, and the company's default is none: no limit.
        Assert.Equal(new RateLimit(null, "none"), RateLimits.Resolve(user.RequestsPerMinute, [(group.Name, group.RequestsPerMinute)], new RateLimitOptions().RequestsPerMinute));
        Assert.Equal(new RateLimit(null, "none"), RateLimits.Resolve(user.TokensPerMinute, [(group.Name, group.TokensPerMinute)], new RateLimitOptions().TokensPerMinute));
    }
}
