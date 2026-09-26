using System.Globalization;
using System.Numerics;
using System.Text;
using Llm.Api.Endpoints;
using Microsoft.Extensions.Options;

namespace Llm.Api.Dashboards;

/// <summary>The viewer's filters: the range, containers (none is all), text, and the least level shown.</summary>
public sealed record LogFilter(DateTimeOffset? From, DateTimeOffset? To, string[]? Container, string? Search, string? Level, string? After, int? Limit, long? IntervalMs);

/// <summary>
/// The live log viewer: every service's logs from Loki, narrowed by container,
/// level and text. The page never sends LogQL: the query is built here from the
/// filters, so the page reads logs and nothing else.
/// </summary>
public static class LogEndpoints
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(30);

    /// <summary>
    /// Levels as Loki detects them (detected_level; the level label where a
    /// service has one, like llama.cpp's single letters or the app's
    /// "Information"), most severe first.
    /// </summary>
    private static readonly (string Name, string Pattern)[] Levels =
    [
        ("error", "e|err|error|fatal|crit|critical|panic|emerg|alert"),
        ("warn", "w|warn|warning"),
        ("info", "i|info|information|notice"),
        ("debug", "d|debug|trace"),
    ];

    public static void MapLogs(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/logs").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/", LinesAsync);
        g.MapGet("/volume", VolumeAsync);
        g.MapGet("/containers", ContainersAsync);
    }

    /// <summary>The LogQL for the filters: every value escaped, so text is only ever text.</summary>
    public static string Query(LogFilter f)
    {
        var containers = (f.Container ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var q = new StringBuilder(containers.Count == 0
            ? "{container=~\".+\"}"
            : $"{{container=~\"{Interpolation.Quote(string.Join('|', containers.Select(Interpolation.RegexEscape)))}\"}}");
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            q.Append(" |~ \"").Append(Interpolation.Quote("(?i)" + Interpolation.RegexEscape(f.Search.Trim()))).Append('"');
        }
        // "warn" shows warnings and errors: the level and every one above it. Anchored:
        // Loki matches a line's labels by search, so "w" alone would match "unknown".
        var at = Array.FindIndex(Levels, l => l.Name == f.Level);
        if (at >= 0)
        {
            var names = string.Join('|', Levels.Take(at + 1).Select(l => l.Pattern));
            q.Append(" | detected_level=~\"(?i)^(").Append(names).Append(")$\"");
        }
        return q.ToString();
    }

    /// <summary>A level as the viewer names it, from whatever Loki detected.</summary>
    public static string LevelName(string? detected)
    {
        var d = (detected ?? "").Trim();
        foreach (var (name, pattern) in Levels)
        {
            if (pattern.Split('|').Any(p => string.Equals(p, d, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }
        return "other";
    }

    private static IResult? Check(LogFilter f, out DateTimeOffset from, out DateTimeOffset to)
    {
        to = f.To ?? DateTimeOffset.UtcNow;
        from = f.From ?? to.AddHours(-1);
        if (from >= to || to - from > MaxRange)
        {
            return AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 30 days.");
        }
        if (f.Level is { Length: > 0 } level && level != "all" && !Levels.Any(l => l.Name == level))
        {
            return AuthEndpoints.Problem(400, "level", "The level is one of all, error, warn, info and debug.");
        }
        if (f.After is { Length: > 0 } after && !after.All(char.IsAsciiDigit))
        {
            return AuthEndpoints.Problem(400, "after", "After is a log line's time in nanoseconds.");
        }
        return null;
    }

    private static async Task<IResult> LinesAsync([AsParameters] LogFilter f, LokiDatasource loki, IOptions<DashboardOptions> options, CancellationToken ct)
    {
        if (Check(f, out var from, out var to) is { } bad)
        {
            return bad;
        }
        var limit = Math.Clamp(f.Limit ?? 500, 1, options.Value.MaxLogLines * 5);
        var query = Query(f);
        // A live tail asks only for what came after the newest line it has.
        var start = f.After is { Length: > 0 } after ? (BigInteger.Parse(after, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture) : LokiDatasource.Nanos(from);
        return await Guard(async () =>
        {
            var (lines, _) = await loki.RangeAsync(query, start, LokiDatasource.Nanos(to), TimeSpan.FromSeconds(1), limit, ct);
            var list = lines ?? [];
            return Results.Ok(new { query, lines = list, limit, more = list.Count >= limit });
        });
    }

    /// <summary>How many lines each level wrote per interval: the bars above the lines.</summary>
    private static async Task<IResult> VolumeAsync([AsParameters] LogFilter f, LokiDatasource loki, CancellationToken ct)
    {
        if (Check(f, out var from, out var to) is { } bad)
        {
            return bad;
        }
        var range = to - from;
        var step = TimeSpan.FromMilliseconds(Math.Max(f.IntervalMs ?? (long)(range.TotalMilliseconds / 100), 1000));
        if (range / 11000 > step)
        {
            step = range / 11000;
        }
        var ms = (long)step.TotalMilliseconds;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(from.ToUnixTimeMilliseconds() / ms * ms);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(to.ToUnixTimeMilliseconds() / ms * ms);
        var query = $"sum by (detected_level) (count_over_time({Query(f)} [{Interpolation.Duration(step)}]))";
        return await Guard(async () =>
        {
            var (_, series) = await loki.RangeAsync(query, start, end, step, 11000, ct);
            // Loki's levels ("E", "ERROR", "error") merged into the viewer's, most severe first.
            // Debug lines are rare: they count with the lines that have no level.
            var merged = (series ?? [])
                .GroupBy(s => LevelName(s.Labels.GetValueOrDefault("detected_level")) is var n && n == "debug" ? "other" : n)
                .OrderBy(g => g.Key == "other" ? Levels.Length : Array.FindIndex(Levels, l => l.Name == g.Key))
                .Select(g => new
                {
                    name = g.Key,
                    points = g.SelectMany(s => s.Points).GroupBy(p => p[0]).OrderBy(p => p.Key)
                        .Select(p => new double?[] { p.Key, p.Sum(x => x[1] ?? 0) }).ToList(),
                });
            return Results.Ok(new { intervalMs = ms, series = merged });
        });
    }

    private static async Task<IResult> ContainersAsync(DateTimeOffset? from, DateTimeOffset? to, LokiDatasource loki, CancellationToken ct)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-1);
        return await Guard(async () => Results.Ok(new { containers = (await loki.LabelValuesAsync("container", null, start, end, ct)).Order(StringComparer.Ordinal) }));
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> run)
    {
        try
        {
            return await run();
        }
        catch (DatasourceException ex)
        {
            return AuthEndpoints.Problem(502, "loki", ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AuthEndpoints.Problem(503, "loki", "Loki is not reachable: the logs need the logging profile.");
        }
    }
}
