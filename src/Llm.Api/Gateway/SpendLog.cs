using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Dashboards;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Llm.Api.Gateway;

/// <summary>Requests in a time range whose cost is worked out again: how many change, and their cost before and after.</summary>
/// <param name="Unpriced">Requests in the range that cannot be priced again: speech and transcriptions, whose length the log does not keep.</param>
public sealed record SpendChange(int Rows, decimal Before, decimal After, int Unpriced);

/// <summary>
/// Writes to the app's ledger of model requests (gateway.requests), where every page reads spend from (usage, credit, the
/// dashboards, chargeback): a video clip, and a picture or a sound the media studio has its servers make themselves, each
/// booked as the gateway books one; and past requests' cost worked out again at today's prices (CostRecalculation).
/// </summary>
public sealed partial class SpendLog(IConfiguration config, IServiceScopeFactory scopes, ILogger<SpendLog> logger)
{
    /// <summary>The call types of what the app booked itself (the names the ledger's import knows them by).</summary>
    public const string VideoCall = "avideo_generation";

    /// <summary>The call type of a picture the app booked (LiteLLM's own name for image generation).</summary>
    public const string PictureCall = "aimage_generation";

    /// <summary>Speech in a voice from a sample (LiteLLM's name for speech), and music (the app's own name), made by the studio server.</summary>
    public const string SpeechCall = "aspeech";
    public const string MusicCall = "amusic_generation";

    /// <summary>
    /// A clip made for a person (in a chat or the media studio): in the ledger as the chat's request (its person), with its
    /// length kept so its cost can be worked out again. A ledger that cannot be written is logged and left: the clip is
    /// made, and its cost is still where it was made.
    /// </summary>
    public Task BookVideoAsync(string email, string model, double seconds, decimal cost, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        BookAsync(VideoCall, Models.MediaModels.VideoUrl, email, model, cost, start, end, new JsonObject { ["video_seconds"] = seconds }, ct);

    /// <summary>Pictures made for a person by the picture server itself (the media studio: changed, enlarged), as the gateway books one.</summary>
    public Task BookPicturesAsync(string email, string model, int count, decimal cost, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        BookAsync(PictureCall, Models.MediaModels.ImageUrl, email, model, cost, start, end, new JsonObject { ["pictures"] = count }, ct);

    /// <summary>A sound the studio server made for a person: speech (its characters kept) or music (its seconds kept).</summary>
    public Task BookSoundAsync(string call, string email, string model, double amount, decimal cost, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        BookAsync(call, Models.MediaModels.StudioUrl, email, model, cost, start, end,
            new JsonObject { [call == SpeechCall ? "characters" : "music_seconds"] = amount }, ct);

    private async Task BookAsync(string call, string apiBase, string email, string model, decimal cost, DateTimeOffset start, DateTimeOffset end,
        JsonObject metadata, CancellationToken ct)
    {
        try
        {
            var id = $"arena-{call switch { VideoCall => "video", PictureCall => "picture", SpeechCall => "speech", _ => "music" }}-{Guid.NewGuid():N}";
            await LedgerAsync(id, call, apiBase, email, model, cost, start, end, metadata, ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or DbUpdateException)
        {
            LogNotBooked(logger, ex.Message);
        }
    }

    /// <summary>The booking in the ledger, at once (its credit counts it now).</summary>
    private async Task LedgerAsync(string id, string call, string apiBase, string email, string model, decimal cost, DateTimeOffset start, DateTimeOffset end,
        JsonObject metadata, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        var person = email.Trim().ToLowerInvariant();
        var normalized = person.ToUpperInvariant();
        var userId = await db.Users.Where(u => u.NormalizedEmail == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        db.GatewayRequests.Add(new Llm.Core.Gateway.GatewayRequest
        {
            StartedAt = start, EndedAt = end, DurationMs = (int)Math.Max(0, (end - start).TotalMilliseconds), Surface = "media",
            Call = call switch { VideoCall => "video", PictureCall => "image", SpeechCall => "speech", _ => "music" }, Protocol = "internal",
            CreditKind = call switch { VideoCall => "video", PictureCall => "pictures", _ => "speech" }, UserId = userId, Person = person, KeyAlias = Chat.ChatKey.Alias,
            Model = model, Deployment = Uri.TryCreate(apiBase, UriKind.Absolute, out var u) ? u.Authority : null, Cost = cost,
            Seconds = metadata["video_seconds"]?.GetValue<double>() ?? metadata["music_seconds"]?.GetValue<double>(),
            Pictures = metadata["pictures"]?.GetValue<int>(), Characters = metadata["characters"] is { } c ? (int)c.GetValue<double>() : null,
            Status = Llm.Core.Gateway.GatewayRequest.Done, HttpStatus = 200, SourceId = id,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The requests of a time range at today's prices, in the ledger: chat completions by their tokens (cached ones at the
    /// cached price), clips by their length, pictures booked as free at one picture each; a sound (speech, music, a
    /// transcription) and a clip of no known length keep what they cost. <paramref name="onlyFree"/>: only those booked at
    /// no cost. Applied, each changed row is written; otherwise only counted.
    /// </summary>
    public async Task<SpendChange> RepriceAsync(DateTimeOffset from, DateTimeOffset to, bool onlyFree, IReadOnlyDictionary<string, TokenPrice> models, PriceOptions defaults,
        bool apply, CancellationToken ct)
    {
        var standard = TokenPrice.Of(null, null, null, defaults);
        const string Cte = """
            with p as (
              select * from unnest(@names::text[], @input::numeric[], @cached::numeric[], @output::numeric[]) as t(name, i, c, o)
            ), r as (
              select g.id, g.cost as old,
                case
                  when g.call = 'image' then case when g.cost = 0 then @image else g.cost end
                  when g.call = 'video' and g.seconds is not null then g.seconds::numeric * @video
                  when g.call in ('speech', 'transcription', 'video', 'music') then null
                  else (greatest(g.prompt_tokens - least(g.cached_tokens, g.prompt_tokens), 0) * coalesce(p.i, @di)
                        + least(g.cached_tokens, g.prompt_tokens) * coalesce(p.c, @dc)
                        + g.completion_tokens * coalesce(p.o, @do)) / 1000000
                end as new
              from gateway.requests g left join p on p.name = g.model
              where g.started_at >= @from and g.started_at < @to and g.status in ('done', 'cached') and (not @onlyFree or g.cost = 0)
            )
            """;
        await using var conn = new NpgsqlConnection(DatabaseSettings.ConnectionString(config));
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
            cmd.Parameters.AddWithValue("from", from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", to.ToUniversalTime());
            cmd.Parameters.AddWithValue("onlyFree", onlyFree);
            return cmd;
        }
        SpendChange change;
        await using (var count = Command(Cte + """
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
            await using var update = Command(Cte + """
                update gateway.requests g set cost = r.new
                from r where g.id = r.id and r.new is not null and abs(r.new - r.old) > 1e-12
                """);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return change;
    }

    public static string Money(decimal value) => value.ToString(value is > 0 and < 0.01m ? "$0.0000" : "$0.00", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A clip's or picture's cost could not be booked in the ledger: {Reason}")]
    private static partial void LogNotBooked(ILogger logger, string reason);
}
