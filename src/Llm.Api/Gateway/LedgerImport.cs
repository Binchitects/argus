using System.Globalization;
using Llm.Api.Dashboards;
using Llm.Api.Operations;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Llm.Api.Gateway;

/// <summary>
/// LiteLLM's request log brought into the app's own ledger (gateway.requests) while LiteLLM still answers: on the leader,
/// every few seconds, the rows written since the last look (each once, by LiteLLM's request id), and at first all it keeps
/// (Retention:GatewayLogDays). Each row is attributed as the usage pages attribute it today (the person, the key, the kind
/// of credit), so the pages can read the ledger and show the same numbers; refusals and failures are told apart.
/// </summary>
public sealed partial class LedgerImport(IServiceScopeFactory scopes, IOptions<DashboardOptions> dashboards, IConfiguration config, Replicas replicas,
    TimeProvider clock, ILogger<LedgerImport> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    /// <summary>
    /// LiteLLM writes its log in batches, each replica its own: a row may land a little after a later one, so each look reads
    /// this far back again (a row already brought over is left as it is).
    /// </summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(1);

    private const int Batch = 5000;

    private readonly string _litellm = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(config))
    {
        Database = dashboards.Value.SqlDatabase, ApplicationName = "llm-app ledger import",
    }.ConnectionString;

    /// <summary>Where the next look starts: a row's time of writing, and its id (a batch written at once shares one time).</summary>
    private DateTime? _since;
    private string _after = "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            if (!replicas.IsLeader)
            {
                continue;
            }
            try
            {
                while (await ImportAsync(stoppingToken) == Batch)
                {
                    // A full batch: there is more (the first look brings the whole log over).
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private sealed record Row(string RequestId, string CallType, string ApiKey, double Spend, int Prompt, int Completion, DateTime Start, DateTime End, DateTime? FirstToken,
        string Model, string ApiBase, string? User, string? MetaUser, string? KeyUser, string? EndUser, string? Alias, string? CacheHit, string? Tag, string? Status,
        string? ErrorClass, string? ErrorMessage, string? ErrorCode, long Cached, string? VideoSeconds, string? MusicSeconds, string? Pictures, string? Characters,
        string? BookedBy, int? DurationMs, DateTime CreatedAt);

    /// <summary>One look: the rows LiteLLM wrote since the last, into the ledger; how many it read.</summary>
    public async Task<int> ImportAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (_since is null)
        {
            // Where the ledger's copy ends, a little before (rows written late), else the start of what LiteLLM keeps.
            var last = await db.GatewayRequests.Where(r => r.SourceId != null).MaxAsync(r => (DateTimeOffset?)r.EndedAt, ct);
            _since = last is { } l ? l.UtcDateTime - TimeSpan.FromMinutes(10) : DateTime.UnixEpoch;
            _after = "";
        }
        var rows = new List<Row>();
        await using (var conn = new NpgsqlConnection(_litellm))
        {
            try
            {
                await conn.OpenAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
            {
                // No LiteLLM database (a stack that never had one): nothing to bring over.
                return 0;
            }
            await using var cmd = new NpgsqlCommand("""
                select s.request_id, s.call_type, s.api_key, s.spend, s.prompt_tokens, s.completion_tokens, s."startTime", s."endTime", s."completionStartTime",
                  coalesce(nullif(s.model_group, ''), s.model), coalesce(s.api_base, ''), s."user", s.metadata->>'user_api_key_user_id', v.user_id, s.end_user,
                  coalesce(s.metadata->>'user_api_key_alias', v.key_alias), s.cache_hit, s.request_tags->>0, s.status,
                  s.metadata->'error_information'->>'error_class', s.metadata->'error_information'->>'error_message', s.metadata->'error_information'->>'error_code',
                  coalesce((s.metadata->'usage_object'->'prompt_tokens_details'->>'cached_tokens')::bigint, 0),
                  s.metadata->>'video_seconds', s.metadata->>'music_seconds', s.metadata->>'pictures', s.metadata->>'characters', s.metadata->>'booked_by',
                  s.request_duration_ms, s.created_at
                from "LiteLLM_SpendLogs" s left join "LiteLLM_VerificationToken" v on v.token = s.api_key
                where (s.created_at, s.request_id) > (@since, @after)
                order by s.created_at, s.request_id
                limit @batch
                """, conn);
            // LiteLLM's times have no zone (they are UTC): the cursor is given as such.
            cmd.Parameters.AddWithValue("since", NpgsqlDbType.Timestamp, DateTime.SpecifyKind(_since.Value, DateTimeKind.Unspecified));
            cmd.Parameters.AddWithValue("after", _after);
            cmd.Parameters.AddWithValue("batch", Batch);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
            while (await r.ReadAsync(ct))
            {
                rows.Add(new Row(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetInt32(4), r.GetInt32(5), r.GetDateTime(6), r.GetDateTime(7),
                    r.IsDBNull(8) ? null : r.GetDateTime(8), r.GetString(9), r.GetString(10), S(11), S(12), S(13), S(14), S(15), S(16), S(17), S(18), S(19), S(20), S(21),
                    r.GetInt64(22), S(23), S(24), S(25), S(26), S(27), r.IsDBNull(28) ? null : r.GetInt32(28), r.GetDateTime(29)));
            }
        }
        if (rows.Count == 0)
        {
            return 0;
        }
        var emails = rows.Select(Person).OfType<string>().Distinct().ToList();
        var ids = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail != null && emails.Select(e => e.ToUpperInvariant()).Contains(u.NormalizedEmail))
            .Select(u => new { u.NormalizedEmail, u.Id }).ToListAsync(ct);
        var idOf = ids.ToDictionary(u => u.NormalizedEmail!, u => u.Id, StringComparer.Ordinal);
        var ledger = rows.Select(row => ToRequest(row, Person(row) is { } p && idOf.TryGetValue(p.ToUpperInvariant(), out var id) ? id : null)).ToList();
        await WriteAsync(db, ledger, ct);
        // A full batch: the next reads on from its last row. Else the next look reads the last minute again (rows written late).
        if (rows.Count == Batch)
        {
            (_since, _after) = (rows[^1].CreatedAt, rows[^1].RequestId);
        }
        else if (rows[^1].CreatedAt - Overlap > _since)
        {
            (_since, _after) = (rows[^1].CreatedAt - Overlap, "");
        }
        return rows.Count;
    }

    /// <summary>Who a request was for, as the usage pages say it: the user LiteLLM was told, else the key's, else the end user; null when no one.</summary>
    private static string? Person(Row row) =>
        new[] { row.User, row.MetaUser, row.KeyUser, row.EndUser }.FirstOrDefault(p => p is { Length: > 0 } && p != "default_user_id")?.Trim().ToLowerInvariant();

    /// <summary>A row of LiteLLM's log as the ledger keeps it.</summary>
    private static GatewayRequest ToRequest(Row row, Guid? userId)
    {
        var (call, protocol) = row.CallType switch
        {
            "acompletion" or "completion" => ("chat", "openai"),
            "atext_completion" or "text_completion" => ("completion", "completions"),
            "anthropic_messages" or "aanthropic_messages" => ("chat", "anthropic"),
            "aresponses" or "responses" => ("chat", "responses"),
            "aembedding" or "embedding" => ("embedding", "openai"),
            "image_generation" or "aimage_generation" => ("image", "openai"),
            "image_edit" or "aimage_edit" => ("image_edit", "openai"),
            "transcription" or "atranscription" => ("transcription", "openai"),
            "speech" or "aspeech" => ("speech", "openai"),
            "video_generation" or "avideo_generation" => ("video", "openai"),
            "amusic_generation" => ("music", "openai"),
            "rerank" or "arerank" => ("rerank", "openai"),
            var other => (other.Length > 24 ? other[..24] : other, "openai"),
        };
        var booked = row.BookedBy == "app";
        var credit = call switch
        {
            "image" or "image_edit" => "pictures",
            "video" => "video",
            "speech" or "transcription" => "speech",
            _ when row.Alias == Chat.ChatKey.Alias => "chat",
            _ => "api",
        };
        var surface = booked ? "media"
            : row.Alias == Chat.ChatKey.Alias ? (row.Tag is { Length: > 0 } ? "app" : "chat")
            : row.Alias?.StartsWith("studio:", StringComparison.Ordinal) == true ? "studio"
            : "api";
        var (status, refusal) = Outcome(row);
        var start = DateTime.SpecifyKind(row.Start, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(row.End, DateTimeKind.Utc);
        static double? Number(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return new GatewayRequest
        {
            Id = Guid.CreateVersion7(new DateTimeOffset(start)), StartedAt = start, EndedAt = end,
            FirstTokenAt = row.FirstToken is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
            DurationMs = row.DurationMs ?? (int)Math.Max(0, (end - start).TotalMilliseconds),
            Surface = surface, Call = call, Protocol = booked ? "internal" : protocol, CreditKind = credit, UserId = userId, Person = Person(row),
            KeyHash = surface is "api" or "studio" && row.ApiKey.Length == 64 ? row.ApiKey : null, KeyAlias = Cut(row.Alias, 120), Tag = Cut(row.Tag, 64),
            Model = Cut(row.Model, 200) ?? "", Deployment = Uri.TryCreate(row.ApiBase, UriKind.Absolute, out var u) ? Cut(u.Authority, 200) : null,
            PromptTokens = row.Prompt, CachedTokens = (int)Math.Min(row.Cached, row.Prompt), CompletionTokens = row.Completion, Cost = (decimal)row.Spend,
            Seconds = Number(row.VideoSeconds) ?? Number(row.MusicSeconds), Pictures = (int?)Number(row.Pictures), Characters = (int?)Number(row.Characters),
            Status = status, Refusal = refusal,
            HttpStatus = (short)(int.TryParse(row.ErrorCode, CultureInfo.InvariantCulture, out var code) ? code : status switch { GatewayRequest.Failed => 500, GatewayRequest.Refused => 429, _ => 200 }),
            Error = Cut(row.ErrorMessage, 500), SourceId = Cut(row.RequestId, 100),
        };
    }

    /// <summary>
    /// Done, answered from the cache, refused (a limit, a budget, the guardrail: it never reached a model), or failed; and why
    /// a refusal was made.
    /// </summary>
    private static (string Status, string? Refusal) Outcome(Row row)
    {
        if (row.CacheHit is "True" or "true")
        {
            return (GatewayRequest.Cached, null);
        }
        if (row.Status != "failure")
        {
            return (GatewayRequest.Done, null);
        }
        var message = row.ErrorMessage ?? "";
        if (row.ErrorClass == "ProxyRateLimitError")
        {
            var kind = LimitType().Match(message) is { Success: true } m ? m.Groups[1].Value : "";
            return (GatewayRequest.Refused, kind switch { "tokens" => "tpm", "max_parallel_requests" => "parallel", _ => "rpm" });
        }
        if (row.ErrorClass == "BudgetExceededError" || (message.Contains("budget", StringComparison.OrdinalIgnoreCase) && message.Contains("exceed", StringComparison.OrdinalIgnoreCase)))
        {
            return (GatewayRequest.Refused, "budget");
        }
        return (GatewayRequest.Failed, null);
    }

    [System.Text.RegularExpressions.GeneratedRegex("Limit type: ([a-z_]+)")]
    private static partial System.Text.RegularExpressions.Regex LimitType();

    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    /// <summary>The rows into the ledger at once, each once: one already there (by LiteLLM's id) is left as it is.</summary>
    private static async Task WriteAsync(AppDbContext db, List<GatewayRequest> rows, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await conn.OpenAsync(ct);
        }
        try
        {
            await using var cmd = new NpgsqlCommand("""
                insert into gateway.requests (id, started_at, first_token_at, ended_at, duration_ms, surface, call, protocol, credit_kind, user_id, person, key_hash,
                  key_alias, tag, model, deployment, prompt_tokens, cached_tokens, completion_tokens, cost, seconds, pictures, characters, status, http_status, refusal,
                  error, source_id, usage_estimated)
                select *, false from unnest(@id, @started, @first, @ended, @duration, @surface, @call, @protocol, @credit, @user, @person, @key, @alias, @tag, @model,
                  @deployment, @prompt, @cached, @completion, @cost, @seconds, @pictures, @characters, @status, @http, @refusal, @error, @source)
                on conflict (source_id) where source_id is not null do nothing
                """, conn);
            void Add<T>(string name, NpgsqlDbType type, Func<GatewayRequest, T> of) =>
                cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | type) { Value = rows.Select(of).ToArray() });
            Add("id", NpgsqlDbType.Uuid, r => r.Id);
            Add("started", NpgsqlDbType.TimestampTz, r => r.StartedAt);
            Add("first", NpgsqlDbType.TimestampTz, r => r.FirstTokenAt);
            Add("ended", NpgsqlDbType.TimestampTz, r => r.EndedAt);
            Add("duration", NpgsqlDbType.Integer, r => r.DurationMs);
            Add("surface", NpgsqlDbType.Varchar, r => r.Surface);
            Add("call", NpgsqlDbType.Varchar, r => r.Call);
            Add("protocol", NpgsqlDbType.Varchar, r => r.Protocol);
            Add("credit", NpgsqlDbType.Varchar, r => r.CreditKind);
            Add("user", NpgsqlDbType.Uuid, r => r.UserId);
            Add("person", NpgsqlDbType.Varchar, r => r.Person);
            Add("key", NpgsqlDbType.Char, r => r.KeyHash);
            Add("alias", NpgsqlDbType.Varchar, r => r.KeyAlias);
            Add("tag", NpgsqlDbType.Varchar, r => r.Tag);
            Add("model", NpgsqlDbType.Varchar, r => r.Model);
            Add("deployment", NpgsqlDbType.Varchar, r => r.Deployment);
            Add("prompt", NpgsqlDbType.Integer, r => r.PromptTokens);
            Add("cached", NpgsqlDbType.Integer, r => r.CachedTokens);
            Add("completion", NpgsqlDbType.Integer, r => r.CompletionTokens);
            Add("cost", NpgsqlDbType.Numeric, r => r.Cost);
            Add("seconds", NpgsqlDbType.Double, r => r.Seconds);
            Add("pictures", NpgsqlDbType.Integer, r => r.Pictures);
            Add("characters", NpgsqlDbType.Integer, r => r.Characters);
            Add("status", NpgsqlDbType.Varchar, r => r.Status);
            Add("http", NpgsqlDbType.Smallint, r => r.HttpStatus);
            Add("refusal", NpgsqlDbType.Varchar, r => r.Refusal);
            Add("error", NpgsqlDbType.Varchar, r => r.Error);
            Add("source", NpgsqlDbType.Varchar, r => r.SourceId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (opened)
            {
                await conn.CloseAsync();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "LiteLLM's request log could not be brought into the ledger: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
