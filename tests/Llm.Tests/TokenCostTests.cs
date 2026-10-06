using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// What tokens cost: every model has a price (its own, else Settings → Prices), the gateway is given
/// all of them, each answer keeps its cost as it runs (cached input at its own price, tool calls and
/// sub-agents with it), everyone sees their prompts and admins everyone's, and past costs can be
/// worked out again on request.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class TokenCostTests(AppFixture app)
{
    /// <summary>One round of the fake model at the default prices: 100 in, 40 of them cached, 12 out.</summary>
    private const decimal Round = (60 * 0.20m + 40 * 0.02m + 12 * 0.80m) / 1_000_000m;

    private static Task<TestBrowser> AdminAsync(WebApplicationFactory<Program> f) => new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await AdminAsync(f);
        var name = "tc" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid chat, string text) =>
        [.. (await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text })).Content.ReadAsStringAsync())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];

    private static string Range(DateTimeOffset from, DateTimeOffset to) =>
        $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static string Now => Range(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public void A_price_falls_back_to_the_default_on_its_own_and_cached_input_is_priced_apart()
    {
        var defaults = new PriceOptions();
        // Nothing of its own: the defaults.
        Assert.Equal(new TokenPrice(0.20m, 0.02m, 0.80m), TokenPrice.Of(null, null, null, defaults));
        // Each price it sets is its own; the rest are the defaults.
        Assert.Equal(new TokenPrice(1.50m, 0.02m, 0.80m), TokenPrice.Of(1.50m, null, null, defaults));
        Assert.Equal(new TokenPrice(0.20m, 0.10m, 2m), TokenPrice.Of(null, 0.10m, 2m, defaults));
        // A free model's cached input is free too: the default is never above the model's input.
        Assert.Equal(new TokenPrice(0m, 0m, 0.80m), TokenPrice.Of(0m, null, null, defaults));

        // 1,000 prompt tokens, 400 from the cache, 200 written: the cached ones at the cached price.
        Assert.Equal((600 * 0.20m + 400 * 0.02m + 200 * 0.80m) / 1_000_000m, TokenPrice.Of(null, null, null, defaults).Cost(1000, 400, 200));
        Assert.Equal(Round, TokenPrice.Of(null, null, null, defaults).Cost(100, 40, 12));
    }

    [Fact]
    public async Task Every_model_reaches_the_gateway_with_all_its_prices_and_new_defaults_follow_at_once()
    {
        app.Remote.Down = false;
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("prices_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Modules:imagegen"] = "true", ["Modules:audio"] = "true",
        });
        var admin = await AdminAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/servers", new
        {
            name = "GPU box", baseUrl = FakeRemote.Base + "/", apiKey = FakeRemote.Key,
            models = new object[]
            {
                new { remote = "big-remote", name = "big", context = 65536, inputPerMtok = 1.5m, outputPerMtok = 6m },
                new { remote = "small-remote", name = "small", context = 32768, cachedInputPerMtok = 0.01m },
            },
        }));
        decimal Param(string model, string key) => gateway.Managed.Values.Single(m => m.Name == model).Params[key]!.GetValue<decimal>();

        // Its own prices, per token; the cached input it does not set is the default (0.02), never above its input.
        Assert.Equal(1.5e-6m, Param("big", "input_cost_per_token"));
        Assert.Equal(0.02e-6m, Param("big", "cache_read_input_token_cost"));
        Assert.Equal(6e-6m, Param("big", "output_cost_per_token"));
        // No prices of its own but cached input: the defaults for the rest.
        Assert.Equal(0.2e-6m, Param("small", "input_cost_per_token"));
        Assert.Equal(0.01e-6m, Param("small", "cache_read_input_token_cost"));
        Assert.Equal(0.8e-6m, Param("small", "output_cost_per_token"));
        // The page shows each model's price, and which are its own.
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "small");
        Assert.Equal(0.8m, row.GetProperty("price").GetProperty("output").GetDecimal());
        Assert.True(row.GetProperty("price").GetProperty("own").GetProperty("cachedInput").GetBoolean());
        Assert.False(row.GetProperty("price").GetProperty("own").GetProperty("output").GetBoolean());

        // Pictures and speech by what they are (and token prices of 0, which make LiteLLM use the deployment's own prices).
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync();
        }
        Assert.Equal(0.01m, Param(MediaModels.ImageModel, "input_cost_per_image"));
        Assert.Equal(0m, Param(MediaModels.ImageModel, "input_cost_per_token"));
        Assert.Equal(0.006m / 60, Param(MediaModels.SpeechToText, "input_cost_per_second"));
        Assert.Equal(0.015m / 1000, Param(MediaModels.TextToSpeech, "input_cost_per_character"));
        Assert.Equal(0m, Param(MediaModels.TextToSpeech, "output_cost_per_token"));

        // A new default reaches the gateway when it is saved: only the prices that follow it change.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new
        {
            changes = new object[] { new { key = "Prices:OutputPerMtok", value = "1.20" }, new { key = "Prices:PerImage", value = "0.04" } },
        }));
        Assert.Equal(1.2e-6m, Param("small", "output_cost_per_token"));
        Assert.Equal(6e-6m, Param("big", "output_cost_per_token"));
        Assert.Equal(0.04m, Param(MediaModels.ImageModel, "input_cost_per_image"));
        // A price below zero is refused.
        var bad = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new object[] { new { key = "Prices:InputPerMtok", value = "-1" } } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, bad);
    }

    [Fact]
    public async Task An_answer_keeps_its_cost_with_its_tool_calls_and_sub_agents_and_everyone_sees_their_own_prompts()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(MediaModels.ImageModel, null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var f = app.Create(app.ConnectionStringFor("cost_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var (b, personId, email) = await PersonAsync(f);
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { tools = new[] { "agents", "image" } }))).GetProperty("id").GetGuid();
        var script = JsonSerializer.Serialize(new object[][]
        {
            [
                new { name = "generate_image", arguments = new { prompt = "A fox" } },
                new { name = "delegate", arguments = new { tasks = new[] { new { title = "Colour", instructions = "Name a colour." }, new { title = "Fruit", instructions = "Name a fruit." } } } },
            ],
        });
        var events = await SendAsync(b, chat, $"Draw and split [script {script}]");

        // As it streams: each round's cost, and each tool call's.
        Assert.All(events.Where(e => e.GetProperty("type").GetString() == "usage"), e => Assert.Equal(Round, e.GetProperty("cost").GetDecimal()));
        var results = events.Where(e => e.GetProperty("type").GetString() == "tool_result").ToList();
        Assert.Equal(0.01m, results.Single(e => e.GetProperty("name").GetString() == "generate_image").GetProperty("cost").GetDecimal());
        Assert.Equal(2 * Round, results.Single(e => e.GetProperty("name").GetString() == "delegate").GetProperty("cost").GetDecimal());

        // Kept: each round at the prices when it ran, the picture at its price, the sub-agents' tokens and cost on their call.
        var messages = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray().ToList();
        Assert.All(messages.Where(m => m.GetProperty("role").GetString() == "assistant"), m => Assert.Equal(Round, m.GetProperty("cost").GetDecimal()));
        var delegated = messages.Single(m => m.GetProperty("toolName").GetString() == "delegate");
        Assert.Equal(200, delegated.GetProperty("promptTokens").GetInt32());
        Assert.Equal(80, delegated.GetProperty("cachedTokens").GetInt32());
        Assert.Equal(24, delegated.GetProperty("completionTokens").GetInt32());
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var parts = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == chat && m.Role != "user").OrderBy(m => m.Sequence).ToListAsync();
            // One answer: every round and tool call names its first round.
            Assert.All(parts, m => Assert.Equal(parts[0].Id, m.AnswerId));
        }

        // Some requests of the person's own API key, and the chat's own small steps (not prompts of theirs: in the totals only).
        await app.SpendAsync(email, 0.5m, DateTimeOffset.UtcNow, apiKey: true);
        await app.SpendAsync(email, 0.25m, DateTimeOffset.UtcNow);

        // Their prompts: the answer (rounds, picture and sub-agents together) and the API request, newest first.
        var mine = await b.JsonAsync(await b.GetAsync($"/api/usage/prompts?{Now}"));
        var rows = mine.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var answer = rows.Single(r => r.GetProperty("source").GetString() == "chat");
        Assert.Equal(400, answer.GetProperty("prompt").GetInt64());
        Assert.Equal(160, answer.GetProperty("cached").GetInt64());
        Assert.Equal(48, answer.GetProperty("completion").GetInt64());
        Assert.Equal(0.01m + 4 * Round, answer.GetProperty("cost").GetDecimal());
        Assert.Equal(chat, answer.GetProperty("chatId").GetGuid());
        Assert.False(answer.GetProperty("unpriced").GetBoolean());
        var api = rows.Single(r => r.GetProperty("source").GetString() == "api");
        Assert.Equal("app-key", api.GetProperty("key").GetString());
        Assert.Equal(0.5m, api.GetProperty("cost").GetDecimal());
        Assert.Equal(0.01m + 4 * Round + 0.5m, mine.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(2, mine.GetProperty("totals").GetProperty("prompts").GetInt64());
        // Filtered: the chat's only, or one model's.
        Assert.Single((await b.JsonAsync(await b.GetAsync($"/api/usage/prompts?{Now}&source=chat"))).GetProperty("rows").EnumerateArray());
        Assert.Contains("Qwen3.8-Flash-Next", mine.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        Assert.Empty((await b.JsonAsync(await b.GetAsync($"/api/usage/prompts?{Now}&model=no-such"))).GetProperty("rows").EnumerateArray());

        // Someone else sees none of it, and not everyone's.
        var (other, _, _) = await PersonAsync(f);
        Assert.Empty((await other.JsonAsync(await other.GetAsync($"/api/usage/prompts?{Now}"))).GetProperty("rows").EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await other.GetAsync($"/api/admin/usage/prompts?{Now}"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await other.PostAsync("/api/admin/usage/recalculate", new { from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow }));

        // Admins see everyone's, by person or group: who, when, the model, tokens and cost, never a chat's title.
        var admin = await AdminAsync(f);
        var theirs = await admin.JsonAsync(await admin.GetAsync($"/api/admin/usage/prompts?{Now}&person={personId}"));
        var seen = theirs.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, seen.Count);
        Assert.All(seen, r => Assert.Equal(email, r.GetProperty("person").GetProperty("email").GetString()));
        Assert.All(seen, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("title").ValueKind));
        Assert.All(seen, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("chatId").ValueKind));
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "g" + Guid.NewGuid().ToString("N")[..8] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { personId } }));
        var byGroup = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/usage/prompts?{Now}&group={group}"))).GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, byGroup.Count);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.GetAsync($"/api/admin/usage/prompts?{Range(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1))}"));
    }

    [Fact]
    public async Task Past_costs_are_worked_out_again_at_todays_prices_only_when_asked_and_audited()
    {
        // A window no other test uses: the gateway's database is shared.
        var from = new DateTimeOffset(2023, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);
        var model = "recalc-" + Guid.NewGuid().ToString("N")[..8];
        var person = $"{model}@example.test";
        await using (var conn = new NpgsqlConnection(app.LitellmConnectionString))
        {
            await conn.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, total_tokens, prompt_tokens, completion_tokens, "startTime", "endTime", model, model_group, metadata, end_user, status) values
                  (@m || '-1', 'acompletion', 'k', 0, 1100, 1000, 100, '2023-05-01 10:00', '2023-05-01 10:00', 'openai/' || @m, @m,
                   ('{"user_api_key_user_id":"' || @p || '","user_api_key_alias":"tool","usage_object":{"prompt_tokens_details":{"cached_tokens":400}}}')::jsonb, '', 'success'),
                  (@m || '-2', 'aimage_generation', 'k', 0, 0, 0, 0, '2023-05-01 11:00', '2023-05-01 11:00', 'openai/sd-cpp-local', 'FLUX.2-klein-4B',
                   ('{"user_api_key_user_id":"' || @p || '","user_api_key_alias":"tool"}')::jsonb, '', 'success'),
                  (@m || '-3', 'aspeech', 'k', 0, 0, 0, 0, '2023-05-01 12:00', '2023-05-01 12:00', 'openai/kokoro', 'kokoro', '{}', '', 'success'),
                  (@m || '-4', 'acompletion', 'k', 0.5, 110, 100, 10, '2023-05-01 13:00', '2023-05-01 13:00', 'openai/' || @m, @m, '{}', '', 'success'),
                  (@m || '-5', '', 'k', 0, 0, 0, 0, '2023-05-01 14:00', '2023-05-01 14:00', @m, '', '{}', '', 'failure');
                """, conn);
            seed.Parameters.AddWithValue("m", model);
            seed.Parameters.AddWithValue("p", person);
            await seed.ExecuteNonQueryAsync();
        }
        await using var f = app.Create(app.ConnectionStringFor("recalc_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var (_, personId, _) = await PersonAsync(f);
        // An answer from before costs were kept, in the same window.
        Guid round;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var c = new Conversation { UserId = personId, Title = "Old chat" };
            var q = new ChatMessage { ConversationId = c.Id, Role = "user", Content = "q", Sequence = 1, CreatedAt = from.AddHours(9) };
            var a = new ChatMessage
            {
                ConversationId = c.Id, ParentId = q.Id, Role = "assistant", Content = "a", Sequence = 2, Model = model, PromptTokens = 100, CachedTokens = 40, CompletionTokens = 12,
                CreatedAt = from.AddHours(9),
            };
            a.AnswerId = a.Id;
            round = a.Id;
            // Its sub-agents' call: two rounds of theirs, on the model its details name, and a picture one of them drew.
            var picture = new ChatAttachment { UserId = personId, FileName = "fox.png", ContentType = "image/png", Kind = "image", Text = "", Data = [1] };
            var agents = new ChatMessage
            {
                ConversationId = c.Id, ParentId = a.Id, Role = "tool", ToolName = "delegate", Content = "[]", Sequence = 3, PromptTokens = 200, CachedTokens = 80, CompletionTokens = 24,
                DetailsJson = $$"""{"agents":[{"title":"x","model":"{{model}}-small","steps":[{"name":"generate_image","files":[{"id":"{{picture.Id}}"}]}]}]}""",
                CreatedAt = from.AddHours(9), AnswerId = a.Id,
            };
            db.Conversations.Add(c);
            db.ChatAttachments.Add(picture);
            db.ChatMessages.AddRange(q, a, agents);
            await db.SaveChangesAsync();
        }
        var admin = await AdminAsync(f);
        async Task<JsonElement> RunAsync(bool apply, bool onlyFree = true) =>
            await admin.JsonAsync(await admin.PostAsync("/api/admin/usage/recalculate", new { from, to, onlyFree, apply }));

        // Counted first: the free completion by its tokens (cached ones at the cached price), the picture at one picture.
        var counted = await RunAsync(apply: false);
        var completion = (600 * 0.20m + 400 * 0.02m + 100 * 0.80m) / 1_000_000m;
        Assert.Equal(2, counted.GetProperty("requests").GetProperty("rows").GetInt32());
        Assert.Equal(0m, counted.GetProperty("requests").GetProperty("before").GetDecimal());
        Assert.Equal(completion + 0.01m, counted.GetProperty("requests").GetProperty("after").GetDecimal());
        // Speech is not: the log keeps no length.
        Assert.Equal(1, counted.GetProperty("requests").GetProperty("unpriced").GetInt32());
        Assert.Equal(2, counted.GetProperty("answers").GetProperty("rows").GetInt32());
        Assert.Equal(3 * Round + 0.01m, counted.GetProperty("answers").GetProperty("after").GetDecimal());
        Assert.False(counted.GetProperty("applied").GetBoolean());
        // Nothing changed by counting, and nothing was audited.
        Assert.Equal(0m, await SpendAsync(model + "-1"));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null((await db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == round)).Cost);
            Assert.False(await db.AuditEvents.AnyAsync(e => e.Action == "usage.recalculate"));
        }

        var applied = await RunAsync(apply: true);
        Assert.True(applied.GetProperty("applied").GetBoolean());
        Assert.Equal(completion, await SpendAsync(model + "-1"));
        Assert.Equal(0.01m, await SpendAsync(model + "-2"));
        Assert.Equal(0m, await SpendAsync(model + "-3"));
        // Already priced, and so left alone.
        Assert.Equal(0.5m, await SpendAsync(model + "-4"));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(Round, (await db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == round)).Cost);
            var audited = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "usage.recalculate");
            Assert.Contains("2 requests", audited.Detail, StringComparison.Ordinal);
        }
        // Done once: nothing is left to change. Every cost, not only the free ones, prices the one booked at 0.5 again.
        Assert.Equal(0, (await RunAsync(apply: false)).GetProperty("requests").GetProperty("rows").GetInt32());
        var every = await RunAsync(apply: false, onlyFree: false);
        Assert.Equal(1, every.GetProperty("requests").GetProperty("rows").GetInt32());
        Assert.Equal((100 * 0.20m + 10 * 0.80m) / 1_000_000m, every.GetProperty("requests").GetProperty("after").GetDecimal());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/usage/recalculate", new { from = to, to = from }));
    }

    private async Task<decimal> SpendAsync(string request)
    {
        await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""select spend::numeric from "LiteLLM_SpendLogs" where request_id = @id""", conn);
        cmd.Parameters.AddWithValue("id", request);
        return Math.Round((decimal)(await cmd.ExecuteScalarAsync())!, 12);
    }

    [Fact]
    public async Task A_v5_2_0_database_gets_each_answer_named_and_its_sub_agents_tokens_on_their_call()
    {
        var cs = app.ConnectionStringFor("costup_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_TokenCost", StringComparison.Ordinal)) - 1]);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            // A question; an answer that delegated to two sub-agents, then wrote; the same question answered again.
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DisplayName","Source","IsDisabled","CreatedAt","AnswerLength","CacheApiAnswers","MemoryOff")
                  VALUES ('00000000-0000-0000-0000-0000000000a1','old','OLD','old@example.test','OLD@EXAMPLE.TEST',true,false,false,true,0,'Old',0,false,now(),'normal',false,false);
                INSERT INTO conversations ("Id","UserId","Title","CreatedAt","UpdatedAt")
                  VALUES ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000a1','Old chat',now(),now());
                INSERT INTO chat_messages ("Id","ConversationId","ParentId","Sequence","Role","Content","Status","CreatedAt","Model","PromptTokens","CachedTokens","CompletionTokens","ToolName","DetailsJson") VALUES
                  ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-0000000000c1',null,1,'user','q',0,now(),null,null,null,null,null,null),
                  ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000001',2,'assistant','',0,now(),'big',100,40,12,null,null),
                  ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000002',3,'tool','[]',0,now(),null,null,null,null,'delegate',
                   '{"agents":[{"title":"a","model":"small","usage":{"prompt":100,"cached":40,"completion":12}},{"title":"b","model":"small","usage":{"prompt":50,"cached":0,"completion":5}}]}'),
                  ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000003',4,'assistant','done',0,now(),'big',200,150,30,null,null),
                  ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000001',5,'assistant','again',0,now(),'big',100,0,9,null,null),
                  ('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000004',6,'user','next',0,now(),null,null,null,null,null,null),
                  ('00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-000000000006',7,'tool','x',0,now(),null,null,null,null,'delegate','not json');
                -- A fork: its copies keep the first chat's times; its own answer comes after it was made.
                INSERT INTO conversations ("Id","UserId","Title","ForkedFromId","CreatedAt","UpdatedAt")
                  VALUES ('00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-0000000000a1','Old chat (fork)','00000000-0000-0000-0000-0000000000c1',now(),now());
                INSERT INTO chat_messages ("Id","ConversationId","ParentId","Sequence","Role","Content","Status","CreatedAt","Model","PromptTokens","CachedTokens","CompletionTokens") VALUES
                  ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-0000000000c2',null,1,'user','q',0,now() - interval '1 day',null,null,null,null),
                  ('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-000000000011',2,'assistant','',0,now() - interval '1 day','big',100,40,12),
                  ('00000000-0000-0000-0000-000000000013','00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-000000000012',3,'user','more',0,now(),null,null,null,null),
                  ('00000000-0000-0000-0000-000000000014','00000000-0000-0000-0000-0000000000c2','00000000-0000-0000-0000-000000000013',4,'assistant','yes',0,now(),'big',300,200,20);
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await migrator.MigrateAsync();

        var m = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == Guid.Parse("00000000-0000-0000-0000-0000000000c1")).OrderBy(x => x.Sequence).ToListAsync();
        // Each answer is named by its first round, down to the next question; a question is in none.
        Assert.Null(m[0].AnswerId);
        Assert.Equal([m[1].Id, m[1].Id, m[1].Id], new[] { m[1].AnswerId, m[2].AnswerId, m[3].AnswerId });
        Assert.Equal(m[4].Id, m[4].AnswerId);
        Assert.Null(m[5].AnswerId);
        // The delegate call carries its sub-agents' tokens; costs from before stay unknown (Recalculate fills them).
        Assert.Equal((150, 40, 17), (m[2].PromptTokens, m[2].CachedTokens, m[2].CompletionTokens));
        Assert.Null(m[2].Model);
        Assert.All(m, x => Assert.Null(x.Cost));
        // Details that are not JSON are left as they were.
        Assert.Null(m[6].PromptTokens);
        // A fork's copy is not an answer again (it ran once, in the first chat); what the fork asked since is.
        var fork = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == Guid.Parse("00000000-0000-0000-0000-0000000000c2")).OrderBy(x => x.Sequence).ToListAsync();
        Assert.Null(fork[1].AnswerId);
        Assert.Equal(fork[3].Id, fork[3].AnswerId);
    }
}
