using System.Text.Json;
using System.Text.RegularExpressions;
using Llm.Api.Endpoints;
using Microsoft.Extensions.Options;

namespace Llm.Api.Dashboards;

/// <summary>An alert Alertmanager holds now: firing, or silenced or inhibited ("suppressed").</summary>
public sealed record ActiveAlert(string Name, string? Severity, string State, DateTimeOffset StartsAt, string? Summary, string? Description,
    IReadOnlyDictionary<string, string> Labels, IReadOnlyList<string> SilencedBy, IReadOnlyList<string> InhibitedBy);

/// <summary>A Prometheus alerting rule: what it checks, for how long, and how it stands now.</summary>
public sealed record AlertRule(string Group, string Name, string? Severity, string State, string Health, string? LastError, string Query, double For,
    string? Summary, string? Description, int Active, DateTimeOffset? ActiveAt);

/// <summary>One time an alert fired: from when to when (no end while it still fires).</summary>
public sealed record AlertEpisode(string Name, string? Severity, IReadOnlyDictionary<string, string> Labels, DateTimeOffset Start, DateTimeOffset? End, string? Summary);

/// <summary>Alertmanager's v2 API: the alerts it holds now.</summary>
public sealed class AlertmanagerClient(HttpClient http, IOptions<DashboardOptions> options)
{
    public async Task<IReadOnlyList<ActiveAlert>> AlertsAsync(CancellationToken ct)
    {
        using var res = await http.GetAsync(new Uri(options.Value.AlertmanagerUrl.TrimEnd('/') + "/api/v2/alerts"), ct);
        var doc = await Json.ReadAsync(res, "Alertmanager", ct);
        return [.. doc.EnumerateArray().Select(a =>
        {
            var labels = Json.Labels(a.GetProperty("labels"));
            var notes = a.TryGetProperty("annotations", out var n) ? Json.Labels(n) : [];
            var status = a.GetProperty("status");
            return new ActiveAlert(labels.GetValueOrDefault("alertname", "?"), labels.GetValueOrDefault("severity"), status.GetProperty("state").GetString() ?? "active",
                a.GetProperty("startsAt").GetDateTimeOffset(), notes.GetValueOrDefault("summary"), notes.GetValueOrDefault("description"), labels,
                Strings(status, "silencedBy"), Strings(status, "inhibitedBy"));
        }).OrderBy(a => a.State == "active" ? 0 : 1).ThenBy(a => SeverityRank(a.Severity)).ThenByDescending(a => a.StartsAt)];
    }

    private static List<string> Strings(JsonElement o, string name) =>
        o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Select(x => x.GetString() ?? "")] : [];

    public static int SeverityRank(string? s) => s switch { "critical" => 0, "warning" => 1, _ => 2 };
}

/// <summary>
/// The alerts page: what fires now (Alertmanager), every rule and its state
/// (Prometheus), and what fired before, from Prometheus's ALERTS series.
/// </summary>
public static partial class AlertEndpoints
{
    /// <summary>The rules' evaluation interval: the finest a history can be.</summary>
    private static readonly TimeSpan Evaluation = TimeSpan.FromSeconds(30);

    public static void MapAlerts(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/alerts").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/", NowAsync);
        g.MapGet("/history", HistoryAsync);
    }

    /// <summary>Each source answers on its own: Alertmanager down still shows the rules, and the other way round.</summary>
    private static async Task<IResult> NowAsync(AlertmanagerClient alertmanager, PromDatasource prom, CancellationToken ct)
    {
        var firing = Try(() => alertmanager.AlertsAsync(ct), "Alertmanager");
        var rules = Try(() => prom.RulesAsync(ct), "Prometheus");
        var (active, alertmanagerError) = await firing;
        var (list, prometheusError) = await rules;
        return Results.Ok(new { firing = active, rules = list, errors = new { alertmanager = alertmanagerError, prometheus = prometheusError } });
    }

    private static async Task<(T? Value, string? Error)> Try<T>(Func<Task<T>> run, string what) where T : class
    {
        try
        {
            return (await run(), null);
        }
        catch (DatasourceException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, $"{what} is not reachable.");
        }
    }

    private static async Task<IResult> HistoryAsync(DateTimeOffset? from, DateTimeOffset? to, PromDatasource prom, CancellationToken ct)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-7);
        if (start >= end || end - start > DashboardEndpoints.MaxRange)
        {
            return AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.");
        }
        var (episodes, error) = await Try(async () =>
        {
            var step = Step(end - start);
            var ms = (long)step.TotalMilliseconds;
            var alignedStart = DateTimeOffset.FromUnixTimeMilliseconds(start.ToUnixTimeMilliseconds() / ms * ms);
            var alignedEnd = DateTimeOffset.FromUnixTimeMilliseconds(end.ToUnixTimeMilliseconds() / ms * ms);
            var series = await prom.RangeAsync("ALERTS{alertstate=\"firing\"}", alignedStart, alignedEnd, step, ct);
            var rules = (await prom.RulesAsync(ct)).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().Summary);
            return Episodes(series, step, alignedEnd, rules);
        }, "Prometheus");
        return error is null
            ? Results.Ok(new { episodes, from = start, to = end })
            : AuthEndpoints.Problem(503, "prometheus", error);
    }

    /// <summary>The step: the rules' interval, or coarser so a series stays within Prometheus's 11,000 points.</summary>
    public static TimeSpan Step(TimeSpan range)
    {
        var step = TimeSpan.FromSeconds(Math.Ceiling((range / 11000).TotalSeconds));
        return step < Evaluation ? Evaluation : step;
    }

    /// <summary>
    /// Runs of points one step apart are one firing; a gap starts the next.
    /// A run that reaches the end of the range is still firing.
    /// </summary>
    public static List<AlertEpisode> Episodes(IReadOnlyList<RawSeries> series, TimeSpan step, DateTimeOffset end, IReadOnlyDictionary<string, string?> summaries)
    {
        var stepMs = step.TotalMilliseconds;
        var episodes = new List<AlertEpisode>();
        foreach (var s in series)
        {
            var labels = s.Labels.Where(l => l.Key is not ("alertstate" or "__name__")).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
            var name = labels.GetValueOrDefault("alertname", "?");
            var summary = summaries.TryGetValue(name, out var template) ? Expand(template, labels) : null;
            var times = s.Points.Where(p => p[1] is not null).Select(p => p[0]!.Value).Order().ToList();
            for (var i = 0; i < times.Count;)
            {
                var j = i;
                while (j + 1 < times.Count && times[j + 1] - times[j] <= stepMs * 1.5)
                {
                    j++;
                }
                var first = DateTimeOffset.FromUnixTimeMilliseconds((long)times[i]);
                var last = DateTimeOffset.FromUnixTimeMilliseconds((long)times[j]);
                var ongoing = end - last < step * 1.5;
                episodes.Add(new AlertEpisode(name, labels.GetValueOrDefault("severity"), labels, first, ongoing ? null : last + step, summary));
                i = j + 1;
            }
        }
        return [.. episodes.OrderByDescending(e => e.End is null).ThenByDescending(e => e.Start)];
    }

    /// <summary>A rule's summary with its labels filled in; what needs the value at the time ({{ $value }}) becomes "…".</summary>
    public static string? Expand(string? template, IReadOnlyDictionary<string, string> labels)
    {
        if (template is null)
        {
            return null;
        }
        var named = LabelRef().Replace(template, m => labels.GetValueOrDefault(m.Groups[1].Value, ""));
        return OtherRef().Replace(named, "…");
    }

    [GeneratedRegex(@"\{\{-?\s*\$labels\.(\w+)\s*-?\}\}")]
    private static partial Regex LabelRef();

    [GeneratedRegex(@"\{\{.*?\}\}")]
    private static partial Regex OtherRef();
}

public static class PromRules
{
    /// <summary>Prometheus's alerting rules (recording rules are not alerts), in their files' order.</summary>
    public static async Task<IReadOnlyList<AlertRule>> RulesAsync(this PromDatasource prom, CancellationToken ct)
    {
        var doc = await prom.GetAsync("/api/v1/rules?type=alert", ct);
        var rules = new List<AlertRule>();
        foreach (var g in doc.GetProperty("data").GetProperty("groups").EnumerateArray())
        {
            var group = g.GetProperty("name").GetString() ?? "";
            foreach (var r in g.GetProperty("rules").EnumerateArray())
            {
                if (r.GetProperty("type").GetString() != "alerting")
                {
                    continue;
                }
                var labels = r.TryGetProperty("labels", out var l) ? Json.Labels(l) : [];
                var notes = r.TryGetProperty("annotations", out var n) ? Json.Labels(n) : [];
                var alerts = r.TryGetProperty("alerts", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
                var since = alerts.Select(x => x.TryGetProperty("activeAt", out var t) && t.TryGetDateTimeOffset(out var at) ? at : (DateTimeOffset?)null)
                    .Where(t => t is not null).Min();
                rules.Add(new AlertRule(group, r.GetProperty("name").GetString() ?? "", labels.GetValueOrDefault("severity"),
                    r.TryGetProperty("state", out var st) ? st.GetString() ?? "inactive" : "inactive",
                    r.TryGetProperty("health", out var h) ? h.GetString() ?? "unknown" : "unknown",
                    r.TryGetProperty("lastError", out var e) && e.GetString() is { Length: > 0 } err ? err : null,
                    r.GetProperty("query").GetString() ?? "",
                    r.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
                    notes.GetValueOrDefault("summary"), notes.GetValueOrDefault("description"), alerts.Count, since));
            }
        }
        return rules;
    }
}
