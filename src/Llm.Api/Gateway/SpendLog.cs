using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Dashboards;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Gateway;

/// <summary>Requests in a time range whose cost is worked out again: how many change, and their cost before and after.</summary>
/// <param name="Unpriced">Requests in the range that cannot be priced again: speech and transcriptions, whose length the log does not keep.</param>
public sealed record SpendChange(int Rows, decimal Before, decimal After, int Unpriced);

/// <summary>
/// Writes to the gateway's request log (LiteLLM_SpendLogs), where every page reads spend from (usage,
/// credit, the dashboards, chargeback): a video clip, and a picture the media studio has the picture server
/// make itself, both made without the gateway, booked as the gateway books a picture; and past requests' cost worked out again at today's prices
/// (CostRecalculation). Only these rows: the gateway's own running totals are left as they are.
/// </summary>
public sealed partial class SpendLog(IOptions<DashboardOptions> options, IConfiguration config, ILogger<SpendLog> logger)
{
    /// <summary>The call type of a clip the app booked (LiteLLM's own name for video generation).</summary>
    public const string VideoCall = "avideo_generation";

    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(config))
    {
        Database = options.Value.SqlDatabase,
        ApplicationName = "llm-app spend",
    }.ConnectionString;

    /// <summary>The call type of a picture the app booked (LiteLLM's own name for image generation).</summary>
    public const string PictureCall = "aimage_generation";

    /// <summary>
    /// A clip made for a person (in a chat or the media studio): in the log as the chat's request (its key, its person),
    /// with its length kept so its cost can be worked out again. A log that cannot be written is logged and left: the clip
    /// is made, and its cost is still where it was made.
    /// </summary>
    public Task BookVideoAsync(string email, string chatKeyHash, string model, double seconds, decimal cost, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        BookAsync(VideoCall, Models.MediaModels.VideoUrl, email, chatKeyHash, model, cost, start, end, new JsonObject { ["video_seconds"] = seconds }, ct);

    /// <summary>Pictures made for a person by the picture server itself (the media studio: changed, enlarged), as the gateway books one.</summary>
    public Task BookPicturesAsync(string email, string chatKeyHash, string model, int count, decimal cost, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        BookAsync(PictureCall, Models.MediaModels.ImageUrl, email, chatKeyHash, model, cost, start, end, new JsonObject { ["pictures"] = count }, ct);

    private async Task BookAsync(string call, string apiBase, string email, string chatKeyHash, string model, decimal cost, DateTimeOffset start, DateTimeOffset end,
        JsonObject metadata, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var insert = new NpgsqlCommand("""
                insert into "LiteLLM_SpendLogs" (request_id, call_type, api_key, spend, total_tokens, prompt_tokens, completion_tokens,
                  "startTime", "endTime", model, model_group, custom_llm_provider, api_base, "user", metadata, end_user, status)
                values (@id, @call, @key, @spend, 0, 0, 0, @start, @end, @model, @model, 'arena', @base, '', @metadata::jsonb, @email, 'success')
                """, conn);
            insert.Parameters.AddWithValue("id", $"arena-{(call == VideoCall ? "video" : "picture")}-{Guid.NewGuid():N}");
            insert.Parameters.AddWithValue("call", call);
            insert.Parameters.AddWithValue("key", chatKeyHash);
            insert.Parameters.AddWithValue("spend", (double)cost);
            insert.Parameters.AddWithValue("start", DateTime.SpecifyKind(start.UtcDateTime, DateTimeKind.Unspecified));
            insert.Parameters.AddWithValue("end", DateTime.SpecifyKind(end.UtcDateTime, DateTimeKind.Unspecified));
            insert.Parameters.AddWithValue("model", model);
            insert.Parameters.AddWithValue("base", apiBase);
            metadata["user_api_key_alias"] = Chat.ChatKey.Alias;
            metadata["booked_by"] = "app";
            insert.Parameters.AddWithValue("metadata", metadata.ToJsonString());
            insert.Parameters.AddWithValue("email", email);
            await insert.ExecuteNonQueryAsync(ct);
        }
        catch (NpgsqlException ex)
        {
            LogNotBooked(logger, ex.Message);
        }
    }

    /// <summary>
    /// The requests of a time range at today's prices: chat completions by their tokens (cached ones at
    /// the cached price), clips by their length, and pictures booked as free at one picture each. The
    /// log keeps no count of pictures: a request with a cost may have asked for several (an API key's
    /// n), so its cost is kept even when every cost is worked out again. <paramref name="onlyFree"/>:
    /// only those booked at no cost. Applied, each changed row is written; otherwise only counted.
    /// </summary>
    public async Task<SpendChange> RepriceAsync(DateTimeOffset from, DateTimeOffset to, bool onlyFree, IReadOnlyDictionary<string, TokenPrice> models, PriceOptions defaults,
        bool apply, CancellationToken ct)
    {
        var standard = TokenPrice.Of(null, null, null, defaults);
        var cte = $"""
            with p as (
              select * from unnest(@names::text[], @input::numeric[], @cached::numeric[], @output::numeric[]) as t(name, i, c, o)
            ), r as (
              select s.request_id, s.spend::numeric as old,
                case
                  when s.call_type in ('aimage_generation', 'image_generation') then case when coalesce(s.spend, 0) = 0 then @image else s.spend::numeric end
                  when s.call_type = '{VideoCall}' and jsonb_typeof(s.metadata->'video_seconds') = 'number' then (s.metadata->>'video_seconds')::numeric * @video
                  when s.call_type in ('aspeech', 'speech', 'atranscription', 'transcription', '{VideoCall}') then null
                  else (greatest(coalesce(s.prompt_tokens, 0) - least({UsageEndpoints.Cached}, coalesce(s.prompt_tokens, 0)), 0) * coalesce(p.i, @di)
                        + least({UsageEndpoints.Cached}, coalesce(s.prompt_tokens, 0)) * coalesce(p.c, @dc)
                        + coalesce(s.completion_tokens, 0) * coalesce(p.o, @do)) / 1000000
                end as new
              from "LiteLLM_SpendLogs" s
              left join p on p.name = {UsageEndpoints.Model}
              where s."startTime" >= @from and s."startTime" < @to and s.call_type <> '' and coalesce(s.status, 'success') <> 'failure'
                and (not @onlyFree or s.spend = 0)
            )
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        NpgsqlCommand Command(string sql)
        {
            var cmd = new NpgsqlCommand(sql, conn, tx);
            var names = models.Keys.ToArray();
            cmd.Parameters.AddWithValue("names", names);
            cmd.Parameters.AddWithValue("input", names.Select(n => models[n].Input).ToArray());
            cmd.Parameters.AddWithValue("cached", names.Select(n => models[n].CachedInput).ToArray());
            cmd.Parameters.AddWithValue("output", names.Select(n => models[n].Output).ToArray());
            cmd.Parameters.AddWithValue("di", standard.Input);
            cmd.Parameters.AddWithValue("dc", standard.CachedInput);
            cmd.Parameters.AddWithValue("do", standard.Output);
            cmd.Parameters.AddWithValue("image", defaults.PerImage);
            cmd.Parameters.AddWithValue("video", defaults.PerVideoSecond);
            cmd.Parameters.AddWithValue("from", from.UtcDateTime);
            cmd.Parameters.AddWithValue("to", to.UtcDateTime);
            cmd.Parameters.AddWithValue("onlyFree", onlyFree);
            return cmd;
        }
        SpendChange change;
        await using (var count = Command(cte + """
            select count(*) filter (where new is not null and abs(new - old) > 1e-12),
                   coalesce(sum(old) filter (where new is not null and abs(new - old) > 1e-12), 0),
                   coalesce(sum(new) filter (where new is not null and abs(new - old) > 1e-12), 0),
                   count(*) filter (where new is null)
            from r
            """))
        await using (var reader = await count.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            change = new SpendChange((int)reader.GetInt64(0), reader.GetDecimal(1), reader.GetDecimal(2), (int)reader.GetInt64(3));
        }
        if (apply && change.Rows > 0)
        {
            await using var update = Command(cte + """
                update "LiteLLM_SpendLogs" s set spend = r.new
                from r where s.request_id = r.request_id and r.new is not null and abs(r.new - r.old) > 1e-12
                """);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return change;
    }

    public static string Money(decimal value) => value.ToString(value is > 0 and < 0.01m ? "$0.0000" : "$0.00", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A clip's or picture's cost could not be booked in the gateway's request log: {Reason}")]
    private static partial void LogNotBooked(ILogger logger, string reason);
}
