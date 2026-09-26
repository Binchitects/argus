using Npgsql;

namespace Llm.Tests;

/// <summary>
/// The gateway's tables (the real schema, exported from a running LiteLLM) with a
/// handful of requests whose numbers are easy to check by hand:
///
///   alice, by API key   prompt 100 (40 cached), output 10, spend 0.00100   2026-09-05 10:00
///   alice, by API key   prompt  60 ( 0 cached), output  6, spend 0.00060   2026-09-06 11:00
///   bob, by chat        prompt  50 ( 0 cached), output  5, spend 0.00050   2026-09-05 12:00
///   nobody (master key) prompt  10 ( 0 cached), output  1, spend 0.00010   2026-09-07 09:00
///   alice, OUTSIDE      prompt 999 (99 cached), output 99, spend 0.99900   2026-08-01 00:00
/// </summary>
public static class LitellmSeed
{
    public static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset To = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    public static async Task CreateAsync(string adminConnectionString, string database)
    {
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"create database \"{database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Resources", "litellm-schema.sql"));
        await using (var ddl = new NpgsqlCommand(schema, conn))
        {
            await ddl.ExecuteNonQueryAsync();
        }
        await using var seed = new NpgsqlCommand("""
            insert into "LiteLLM_UserTable" (user_id, user_email, spend, max_budget) values
              ('alice@example.test', 'alice@example.test', 0.0016, 10),
              ('bob@example.test',   'bob@example.test',   0.0005, null);
            insert into "LiteLLM_VerificationToken" (token, key_alias, key_name, user_id, spend) values
              ('hash-alice', 'app-alice', 'sk-...alic', 'alice@example.test', 0.0016),
              ('hash-webui', 'open-webui', 'sk-...webu', null, 0.0005);
            insert into "LiteLLM_SpendLogs"
              (request_id, call_type, api_key, spend, total_tokens, prompt_tokens, completion_tokens,
               "startTime", "endTime", "completionStartTime", model, "user", metadata, end_user) values
              ('r1', 'acompletion', 'hash-alice', 0.001,  110, 100, 10, '2026-09-05 10:00:00', '2026-09-05 10:00:02', '2026-09-05 10:00:01', 'qwen', '',
               '{"user_api_key_user_id":"alice@example.test","user_api_key_alias":"app-alice","usage_object":{"prompt_tokens_details":{"cached_tokens":40}}}', ''),
              ('r2', 'acompletion', 'hash-alice', 0.0006,  66,  60,  6, '2026-09-06 11:00:00', '2026-09-06 11:00:03', '2026-09-06 11:00:01', 'qwen', '',
               '{"user_api_key_user_id":"alice@example.test","user_api_key_alias":"app-alice"}', ''),
              ('r3', 'acompletion', 'hash-webui', 0.0005,  55,  50,  5, '2026-09-05 12:00:00', '2026-09-05 12:00:01', '2026-09-05 12:00:00.5', 'qwen', '',
               '{"user_api_key_alias":"open-webui"}', 'bob@example.test'),
              ('r4', 'acompletion', 'master',     0.0001,  11,  10,  1, '2026-09-07 09:00:00', '2026-09-07 09:00:01', '2026-09-07 09:00:00.2', 'qwen', '',
               '{}', ''),
              ('r5', 'acompletion', 'hash-alice', 0.999,  1098, 999, 99, '2026-08-01 00:00:00', '2026-08-01 00:00:09', '2026-08-01 00:00:01', 'qwen', '',
               '{"user_api_key_user_id":"alice@example.test","usage_object":{"prompt_tokens_details":{"cached_tokens":99}}}', '');
            """, conn);
        await seed.ExecuteNonQueryAsync();
    }
}
