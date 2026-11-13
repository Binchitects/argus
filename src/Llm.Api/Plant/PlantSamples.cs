using System.Globalization;
using System.Security.Claims;
using Llm.Api.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Plant;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Llm.Api.Plant;

/// <summary>One value sent: its point by id, or by its source and address there; when (now if not said); a number or a text.</summary>
public sealed record SampleIn(Guid? Point = null, string? Source = null, string? Address = null, DateTimeOffset? At = null, double? Value = null, string? Text = null, bool? On = null);

public sealed record SamplesIn(List<SampleIn>? Samples = null);

/// <summary>
/// The plant's values (v9): sent by connectors and gateways with an API key that holds plant.write, a batch at a time,
/// to the points of the sites that key's person sees; read back as a series over a time range, in buckets, and as each
/// point's latest value.
/// </summary>
public static class PlantSamples
{
    public const int MostABatch = 10_000, MostBuckets = 2_000, MostText = 2_000;
    /// <summary>How far ahead of the server's clock a value may be dated (a gateway's clock a little fast).</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/plant/samples", WriteAsync).RequireFeature("plant")
            .RequireAuthorization(ArenaMcp.KeyAuthentication.Policy).RequirePermission(Core.Access.Permissions.PlantWrite);
        app.MapGet("/api/plant/points/{id:guid}/series", SeriesAsync).RequireFeature("plant").RequirePermission(Core.Access.Permissions.Plant);
    }

    private static async Task<IResult> WriteAsync(SamplesIn body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var samples = body.Samples ?? [];
        if (samples.Count == 0)
        {
            return PlantEndpoints.Bad("samples", "Send at least one value.");
        }
        if (samples.Count > MostABatch)
        {
            return PlantEndpoints.Bad("samples", $"At most {MostABatch:N0} values a batch.");
        }
        var m = await access.MembershipAsync(await PlantEndpoints.Me(p, users), ct);
        var seen = (await db.PlantSites.AsNoTracking().ToListAsync(ct)).Where(s => PlantEndpoints.Sees(s, m)).Select(s => s.Id).ToList();

        // The points named, by id or by source and address, among the sites the sender sees.
        var ids = samples.Where(s => s.Point is not null).Select(s => s.Point!.Value).Distinct().ToList();
        var addresses = samples.Where(s => s.Point is null && s.Address is not null).Select(s => s.Address!).Distinct().ToList();
        var points = await (from pt in db.PlantPoints.AsNoTracking()
                            join a in db.PlantAssets.AsNoTracking() on pt.AssetId equals a.Id
                            where seen.Contains(a.SiteId) && (ids.Contains(pt.Id) || (pt.Address != null && addresses.Contains(pt.Address)))
                            select pt).ToListAsync(ct);
        var byId = points.ToDictionary(pt => pt.Id);
        var byAddress = points.Where(pt => pt.Address is not null).ToLookup(pt => pt.Address!);

        var now = DateTimeOffset.UtcNow;
        var kept = new Dictionary<(Guid, DateTimeOffset), PlantSample>();
        var unknown = new List<int>();
        var wrong = new List<string>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            IEnumerable<PlantPoint> targets = s.Point is { } id
                ? byId.TryGetValue(id, out var one) ? [one] : []
                : s.Address is not null ? byAddress[s.Address].Where(pt => s.Source is null || string.Equals(pt.Source, s.Source.Trim(), StringComparison.OrdinalIgnoreCase)) : [];
            var list = targets.ToList();
            if (list.Count == 0)
            {
                unknown.Add(i);
                continue;
            }
            var at = (s.At ?? now).ToUniversalTime();
            if (at > now + Ahead)
            {
                wrong.Add($"value {i + 1}: dated {at:u}, ahead of now");
                continue;
            }
            // Postgres keeps microseconds: a moment is compared as it is stored.
            at = new DateTimeOffset(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);
            foreach (var point in list)
            {
                if (Sample(point, s, out var why) is { } sample)
                {
                    sample.At = at;
                    kept[(point.Id, at)] = sample;
                }
                else
                {
                    wrong.Add($"value {i + 1} ({point.Name}): {why}");
                }
            }
        }
        if (kept.Count > 0)
        {
            await SaveAsync(db, [.. kept.Values], ct);
        }
        return Results.Ok(new { kept = kept.Count, unknown = unknown.Take(50), unknownCount = unknown.Count, wrong = wrong.Take(50), wrongCount = wrong.Count });
    }

    /// <summary>The value as the point's type keeps it, or why not.</summary>
    public static PlantSample? Sample(PlantPoint point, SampleIn s, out string? why)
    {
        why = null;
        double? value = s.Value;
        string? text = s.Text;
        if (value is { } v && (double.IsNaN(v) || double.IsInfinity(v)))
        {
            why = "not a number";
            return null;
        }
        switch (point.DataType)
        {
            case PlantPoint.Boolean:
                value = s.On is { } on ? (on ? 1 : 0)
                    : value is { } b ? (b != 0 ? 1 : 0)
                    : text?.Trim().ToLowerInvariant() switch { "true" or "on" or "1" or "yes" => 1, "false" or "off" or "0" or "no" => 0, _ => null };
                text = null;
                if (value is null)
                {
                    why = "a boolean needs on, a value or true/false";
                    return null;
                }
                break;
            case PlantPoint.Text:
                text ??= value?.ToString(CultureInfo.InvariantCulture);
                value = null;
                if (text is null)
                {
                    why = "a text point needs a text";
                    return null;
                }
                break;
            default:
                if (value is null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                }
                text = null;
                if (value is null)
                {
                    why = "needs a value";
                    return null;
                }
                break;
        }
        if (text is { Length: > MostText })
        {
            text = text[..MostText];
        }
        return new PlantSample { PointId = point.Id, Value = value, Text = text };
    }

    /// <summary>All of them in one statement; a point's moment sent again replaces the value kept for it.</summary>
    private static async Task SaveAsync(AppDbContext db, List<PlantSample> samples, CancellationToken ct)
    {
        var sql = """
            INSERT INTO plant_samples ("PointId", "At", "Value", "Text")
            SELECT * FROM unnest(@points, @ats, @values, @texts)
            ON CONFLICT ("PointId", "At") DO UPDATE SET "Value" = excluded."Value", "Text" = excluded."Text"
            """;
        await db.Database.ExecuteSqlRawAsync(sql, [
            new NpgsqlParameter("points", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = samples.Select(s => s.PointId).ToArray() },
            new NpgsqlParameter("ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = samples.Select(s => s.At).ToArray() },
            new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = samples.Select(s => s.Value).ToArray() },
            new NpgsqlParameter("texts", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = samples.Select(s => s.Text).ToArray() },
        ], ct);
    }

    /// <summary>
    /// A point's values from <paramref name="from"/> to <paramref name="to"/> (the last day if not said), in at most
    /// <paramref name="buckets"/> buckets of equal time: each its average, lowest, highest, last and how many. A text
    /// point's are its last texts, as they came.
    /// </summary>
    private static async Task<IResult> SeriesAsync(Guid id, DateTimeOffset? from, DateTimeOffset? to, int? buckets, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        AccessService access, CancellationToken ct)
    {
        var m = await access.MembershipAsync(await PlantEndpoints.Me(p, users), ct);
        var found = await (from pt in db.PlantPoints.AsNoTracking()
                           join a in db.PlantAssets.AsNoTracking() on pt.AssetId equals a.Id
                           join s in db.PlantSites.AsNoTracking() on a.SiteId equals s.Id
                           where pt.Id == id
                           select new { pt, s }).SingleOrDefaultAsync(ct);
        if (found is null || !PlantEndpoints.Sees(found.s, m))
        {
            return Results.NotFound();
        }
        var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = (from ?? end.AddDays(-1)).ToUniversalTime();
        if (start >= end)
        {
            return PlantEndpoints.Bad("from", "From is before to.");
        }
        var n = Math.Clamp(buckets ?? 300, 1, MostBuckets);
        var width = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, (end - start).Ticks / n));
        if (found.pt.DataType == PlantPoint.Text)
        {
            var texts = await db.PlantSamples.AsNoTracking().Where(s => s.PointId == id && s.At >= start && s.At < end).OrderByDescending(s => s.At).Take(MostBuckets)
                .Select(s => new { at = s.At, text = s.Text }).ToListAsync(ct);
            texts.Reverse();
            return Results.Ok(new { point = id, from = start, to = end, bucketSeconds = width.TotalSeconds, texts });
        }
        var rows = await db.Database.SqlQueryRaw<Bucket>("""
            SELECT date_bin(@width, "At", @start) AS "At", avg("Value") AS "Avg", min("Value") AS "Min", max("Value") AS "Max",
                   (array_agg("Value" ORDER BY "At" DESC))[1] AS "Last", count(*)::int AS "N"
            FROM plant_samples WHERE "PointId" = @id AND "At" >= @start AND "At" < @end
            GROUP BY 1 ORDER BY 1
            """,
            new NpgsqlParameter("width", width), new NpgsqlParameter("start", start), new NpgsqlParameter("end", end), new NpgsqlParameter("id", id)).ToListAsync(ct);
        return Results.Ok(new { point = id, from = start, to = end, bucketSeconds = width.TotalSeconds, buckets = rows });
    }

    public sealed record Bucket(DateTimeOffset At, double? Avg, double? Min, double? Max, double? Last, int N);

    /// <summary>Each point's latest value, of those given.</summary>
    internal static async Task<Dictionary<Guid, PlantSample>> LatestAsync(AppDbContext db, IReadOnlyCollection<Guid> points, CancellationToken ct)
    {
        if (points.Count == 0)
        {
            return [];
        }
        var rows = await db.PlantSamples.FromSqlRaw("""
            SELECT DISTINCT ON ("PointId") "PointId", "At", "Value", "Text" FROM plant_samples
            WHERE "PointId" = ANY(@ids) ORDER BY "PointId", "At" DESC
            """, new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = points.ToArray() }).AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(r => r.PointId);
    }
}
