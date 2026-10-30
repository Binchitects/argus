using Llm.Api.Gateway;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// LiteLLM's request log mirrored into the app's own ledger: each row once, attributed as the usage pages attribute it, a
/// refusal told from a failure, and the same totals per person as LiteLLM's log gives. People's keys come over too.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class LedgerImportTests(AppFixture app)
{
    private static readonly string[] Seeded = ["r1", "r2", "r3", "r4", "r5"];

    private async Task<List<GatewayRequest>> LedgerAsync(params string[] ids)
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GatewayRequests.AsNoTracking().Where(r => ids.Contains(r.SourceId!)).OrderBy(r => r.SourceId).ToListAsync();
    }

    private async Task ImportAllAsync()
    {
        var import = app.Factory.Services.GetRequiredService<LedgerImport>();
        while (await import.ImportAsync(CancellationToken.None) > 0 && (await LedgerAsync(Seeded)).Count < Seeded.Length)
        {
            // Batches until the seed is in (the log may hold other tests' rows too).
        }
        // Once more: what is read again (the last minute) is not brought over twice.
        await import.ImportAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Each_row_comes_over_once_attributed_as_the_usage_pages_attribute_it()
    {
        await ImportAllAsync();
        await ImportAllAsync();
        var rows = await LedgerAsync(Seeded);
        Assert.Equal(Seeded, rows.Select(r => r.SourceId));
        var r1 = rows[0];
        Assert.Equal(("alice@example.test", "api", "chat", "openai", "api", 100, 40, 10, 0.001m, GatewayRequest.Done, "app-alice"),
            (r1.Person, r1.Surface, r1.Call, r1.Protocol, r1.CreditKind, r1.PromptTokens, r1.CachedTokens, r1.CompletionTokens, r1.Cost, r1.Status, r1.KeyAlias));
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero), r1.StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 10, 0, 1, TimeSpan.Zero), r1.FirstTokenAt);
        Assert.Equal(2000, r1.DurationMs);
        // A key with no person: the end user it was for. The master key's: no one.
        Assert.Equal("bob@example.test", rows[2].Person);
        Assert.Null(rows[3].Person);
        Assert.Equal(99, rows[4].CachedTokens);
    }

    [Fact]
    public async Task A_refusal_is_told_from_a_failure_and_from_an_answer_out_of_the_cache()
    {
        var (tpm, budget, failed, cached) = ("rt-" + Guid.NewGuid().ToString("N")[..8], "rb-" + Guid.NewGuid().ToString("N")[..8], "rf-" + Guid.NewGuid().ToString("N")[..8], "rc-" + Guid.NewGuid().ToString("N")[..8]);
        await using (var conn = new NpgsqlConnection(app.LitellmConnectionString))
        {
            await conn.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, prompt_tokens, completion_tokens, "startTime", "endTime", model, metadata, status, cache_hit) values
                  (@tpm, 'acompletion', 'hash-alice', 0, 0, 0, '2025-02-01', '2025-02-01', 'qwen',
                   '{"user_api_key_user_id":"alice@example.test","error_information":{"error_class":"ProxyRateLimitError","error_code":"429","error_message":"Rate limit exceeded. Limit type: tokens. Current limit: 60"}}', 'failure', ''),
                  (@budget, 'acompletion', 'hash-alice', 0, 0, 0, '2025-02-01', '2025-02-01', 'qwen',
                   '{"user_api_key_user_id":"alice@example.test","error_information":{"error_class":"BudgetExceededError","error_code":"400","error_message":"Budget has been exceeded"}}', 'failure', ''),
                  (@failed, 'acompletion', 'hash-alice', 0, 0, 0, '2025-02-01', '2025-02-01', 'qwen',
                   '{"user_api_key_user_id":"alice@example.test","error_information":{"error_class":"APIConnectionError","error_code":"500","error_message":"Connection refused"}}', 'failure', ''),
                  (@cached, 'acompletion', 'hash-alice', 0, 5, 1, '2025-02-01', '2025-02-01', 'qwen', '{"user_api_key_user_id":"alice@example.test"}', 'success', 'True')
                """, conn);
            foreach (var (name, value) in new[] { ("tpm", tpm), ("budget", budget), ("failed", failed), ("cached", cached) })
            {
                insert.Parameters.AddWithValue(name, value);
            }
            await insert.ExecuteNonQueryAsync();
        }
        var import = app.Factory.Services.GetRequiredService<LedgerImport>();
        for (var i = 0; i < 20 && (await LedgerAsync(tpm, budget, failed, cached)).Count < 4; i++)
        {
            await import.ImportAsync(CancellationToken.None);
        }
        var rows = (await LedgerAsync(tpm, budget, failed, cached)).ToDictionary(r => r.SourceId!);
        Assert.Equal((GatewayRequest.Refused, "tpm", (short)429), (rows[tpm].Status, rows[tpm].Refusal, rows[tpm].HttpStatus));
        Assert.Equal((GatewayRequest.Refused, "budget"), (rows[budget].Status, rows[budget].Refusal));
        Assert.Equal((GatewayRequest.Failed, (string?)null, (short)500, "Connection refused"), (rows[failed].Status, rows[failed].Refusal, rows[failed].HttpStatus, rows[failed].Error));
        Assert.Equal(GatewayRequest.Cached, rows[cached].Status);
    }

    [Theory]
    [InlineData("GuardrailRaisedException", "You have used all your API credit for this month ($0.00). Ask an admin to raise it.", "credit")]
    [InlineData("GuardrailRaisedException", "Your API access is off. Ask an admin to turn it on (Admin → People).", "api_off")]
    [InlineData("GuardrailRaisedException", "This account is disabled.", "disabled")]
    [InlineData("GuardrailRaisedException", "The request was refused: it holds a credit card number. Remove it and send it again.", "safeguards")]
    [InlineData("GuardrailRaisedException", "Qwen3-4B is starting.", "room")]
    [InlineData("Exception", "Key is blocked. Update via `/key/unblock` if you're an admin.", "blocked")]
    [InlineData("KeyNotFoundError", "Authentication Error, Invalid proxy server token passed.", "key")]
    [InlineData("Exception", "No api key passed in.", "key")]
    [InlineData("ProxyException", "key not allowed to access model. This key can only access models=['a']", "model")]
    [InlineData("BadRequestError", "litellm.BadRequestError: OpenAIException - model is not loaded.", null)]
    public async Task Each_refusal_before_a_model_says_why_and_an_engines_error_is_a_failure(string errorClass, string message, string? refusal)
    {
        var id = "rr-" + Guid.NewGuid().ToString("N")[..8];
        await using (var conn = new NpgsqlConnection(app.LitellmConnectionString))
        {
            await conn.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, prompt_tokens, completion_tokens, "startTime", "endTime", model, metadata, status) values
                  (@id, 'acompletion', 'hash-alice', 0, 0, 0, '2025-02-02', '2025-02-02', 'qwen',
                   jsonb_build_object('user_api_key_user_id', 'alice@example.test', 'error_information', jsonb_build_object('error_class', @class, 'error_code', '400', 'error_message', @message)), 'failure')
                """, conn);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("class", errorClass);
            insert.Parameters.AddWithValue("message", message);
            await insert.ExecuteNonQueryAsync();
        }
        var import = app.Factory.Services.GetRequiredService<LedgerImport>();
        for (var i = 0; i < 20 && (await LedgerAsync(id)).Count < 1; i++)
        {
            await import.ImportAsync(CancellationToken.None);
        }
        var row = Assert.Single(await LedgerAsync(id));
        Assert.Equal((refusal is null ? GatewayRequest.Failed : GatewayRequest.Refused, refusal), (row.Status, row.Refusal));
    }

    [Fact]
    public async Task Peoples_keys_come_over_with_the_names_and_limits_given_here_and_go_when_LiteLLM_drops_them()
    {
        // An app of its own: its people are this test's alone (LiteLLM's database is every app's).
        await using var f = app.Create(app.ConnectionStringFor("keys_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ki" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var id = made.GetProperty("id").GetGuid();
        static string Hash(string s) => AnswerCache.HashOf(s);
        var (main, own, studio, chat, nobody) = (Hash(name + "main"), Hash(name + "own"), Hash(name + "studio"), Hash(name + "chat"), Hash(name + "nobody"));
        var workspace = Guid.NewGuid();
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OwnKeys.Add(new Llm.Core.Identity.OwnKey { Token = own, UserId = id, Name = "CI", RequestsPerMinute = 5, MonthlyBudget = 2m, CreatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }
        await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
        await conn.OpenAsync();
        try
        {
            await using (var insert = new NpgsqlCommand("""
                insert into "LiteLLM_VerificationToken" (token, key_alias, key_name, user_id, spend, expires, created_at) values
                  (@main, 'app-' || @name, 'sk-...main', @email, 0.25, '2027-01-01 00:00', '2026-01-01 00:00'),
                  (@own, 'app-' || @name || '-own-abcd1234', 'sk-...ownk', upper(@email), 0, null, '2026-01-02 00:00'),
                  (@studio, @workspace, 'sk-...stud', @email, 0.5, null, '2026-01-03 00:00'),
                  (@chat, 'chat', 'sk-...chat', @email, 9, null, '2026-01-04 00:00'),
                  (@nobody, 'app-nobody', 'sk-...nobo', 'nobody-' || @email, 1, null, '2026-01-05 00:00')
                """, conn))
            {
                foreach (var (k, v) in new[] { ("main", main), ("own", own), ("studio", studio), ("chat", chat), ("nobody", nobody), ("name", name), ("email", $"{name}@example.test"), ("workspace", $"studio:{workspace:N}") })
                {
                    insert.Parameters.AddWithValue(k, v);
                }
                await insert.ExecuteNonQueryAsync();
            }
            var import = f.Services.GetRequiredService<LedgerImport>();
            async Task<Dictionary<string, ApiKey>> KeysAsync()
            {
                await import.ImportKeysAsync(CancellationToken.None);
                await using var scope = f.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await db.ApiKeys.AsNoTracking().Where(k => k.UserId == id).ToDictionaryAsync(k => k.Hash.Trim());
            }
            var keys = await KeysAsync();
            // The chat's own key is the app's, not the person's; a key of no one with an account here stays out.
            Assert.Equal(new[] { main, own, studio }.Order(), keys.Keys.Order());
            Assert.Equal((ApiKey.Main, "app-" + name, (string?)null, "sk-...main", 0.25m, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                (keys[main].Kind, keys[main].Alias, keys[main].Name, keys[main].Preview, keys[main].Spend, keys[main].ExpiresAt));
            Assert.Equal((ApiKey.Own, "CI", (int?)5, (int?)null, (decimal?)2m, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
                (keys[own].Kind, keys[own].Name, keys[own].RequestsPerMinute, keys[own].TokensPerMinute, keys[own].MonthlyBudget, keys[own].CreatedAt));
            // A workspace the app no longer has: the key is still its kind, tied to none.
            Assert.Equal((ApiKey.Studio, (Guid?)null), (keys[studio].Kind, keys[studio].WorkspaceId));

            // What it spent since, and a key revoked at LiteLLM.
            await using (var change = new NpgsqlCommand("""
                update "LiteLLM_VerificationToken" set spend = 0.75 where token = @main;
                delete from "LiteLLM_VerificationToken" where token = @own;
                """, conn))
            {
                change.Parameters.AddWithValue("main", main);
                change.Parameters.AddWithValue("own", own);
                await change.ExecuteNonQueryAsync();
            }
            keys = await KeysAsync();
            Assert.Equal(new[] { main, studio }.Order(), keys.Keys.Order());
            Assert.Equal(0.75m, keys[main].Spend);
        }
        finally
        {
            await using var delete = new NpgsqlCommand("""delete from "LiteLLM_VerificationToken" where token = any(@tokens)""", conn);
            delete.Parameters.AddWithValue("tokens", new[] { main, own, studio, chat, nobody });
            await delete.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Each_persons_totals_are_LiteLLMs_to_the_billionth()
    {
        await ImportAllAsync();
        var old = new Dictionary<string, double>();
        await using (var conn = new NpgsqlConnection(app.LitellmConnectionString))
        {
            await conn.OpenAsync();
            // As the usage pages read LiteLLM's log: the user, else the key's, else the end user.
            await using var cmd = new NpgsqlCommand("""
                select lower(coalesce(nullif(s."user", ''), nullif(s.metadata->>'user_api_key_user_id', ''), nullif(v.user_id, ''), nullif(s.end_user, ''))) p, sum(s.spend)
                from "LiteLLM_SpendLogs" s left join "LiteLLM_VerificationToken" v on v.token = s.api_key
                where s.request_id = any(@ids) group by 1 having lower(coalesce(nullif(s."user", ''), nullif(s.metadata->>'user_api_key_user_id', ''), nullif(v.user_id, ''), nullif(s.end_user, ''))) is not null
                """, conn);
            cmd.Parameters.AddWithValue("ids", Seeded);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                old[r.GetString(0)] = r.GetDouble(1);
            }
        }
        var ledger = (await LedgerAsync(Seeded)).Where(r => r.Person is not null).GroupBy(r => r.Person!).ToDictionary(g => g.Key, g => (double)g.Sum(r => r.Cost));
        Assert.Equal(old.Keys.Order(), ledger.Keys.Order());
        foreach (var (person, spend) in old)
        {
            Assert.True(Math.Abs(spend - ledger[person]) < 1e-9, $"{person}: {spend} against {ledger[person]}");
        }
    }
}
