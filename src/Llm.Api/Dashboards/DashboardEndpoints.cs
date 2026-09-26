using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Endpoints;
using Microsoft.Extensions.Options;

namespace Llm.Api.Dashboards;

/// <summary>A panel's time range and resolution, and the dashboard variables chosen on the page.</summary>
public sealed record PanelQuery(DateTimeOffset From, DateTimeOffset To, long? IntervalMs = null, int? MaxDataPoints = null, Dictionary<string, string[]>? Vars = null);

public static partial class DashboardEndpoints
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(400);

    /// <summary>The datasources the app runs: LiteLLM's database (SQL), Prometheus and Loki.</summary>
    private static readonly HashSet<string> Served = [SqlDatasource.Uid, PromDatasource.Uid, LokiDatasource.Uid];

    public static void MapDashboards(this IEndpointRouteBuilder app)
    {
        // Admins only: these dashboards show everyone's usage. People see their own under /api/usage/me.
        var g = app.MapGroup("/api/dashboards").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/", (DashboardStore store) => Results.Ok(store.All().Select(d => new
        {
            d.Uid, d.Title, panels = d.Panels.Count(p => p.Type != "row"),
            supported = d.Panels.Count(p => p.Type != "row" && Supported(p)),
        })));
        g.MapGet("/{uid}", (string uid, DashboardStore store) =>
            store.Find(uid) is { } d
                ? Results.Ok(new
                {
                    d.Uid, d.Title, d.Time, d.Refresh,
                    panels = d.Panels.Select(p => WithSupport(p)),
                    variables = (d.Variables ?? []).Where(v => v.Hide < 2).Select(v => new
                    {
                        v.Name, label = v.Label ?? v.Name, v.Type, v.Multi, v.IncludeAll, current = Defaults(v),
                    }),
                })
                : Results.NotFound());
        g.MapGet("/{uid}/variables", VariablesAsync);
        g.MapPost("/{uid}/panels/{key:int}/query", QueryAsync);
    }

    public static bool Supported(Panel p) =>
        p.Type is "text" or "row" || (p.Targets.Count > 0 && p.Targets.All(t => Served.Contains(t.Datasource) && (t.RawSql is not null || t.Expr is not null)));

    private static JsonObject WithSupport(Panel p)
    {
        var def = (JsonObject)p.Definition.DeepClone();
        def["supported"] = Supported(p);
        return def;
    }

    /// <summary>A variable's default values: as the file left them, or All, or none.</summary>
    private static List<string> Defaults(Variable v)
    {
        var current = v.Current?["value"];
        var values = current switch
        {
            JsonArray a => [.. a.Select(x => x?.GetValue<string>() ?? "")],
            JsonValue x when x.TryGetValue<string>(out var s) => [s],
            _ => new List<string>(),
        };
        if (values.Count == 0 && v.Type == "textbox" && v.Query is JsonValue q && q.TryGetValue<string>(out var text))
        {
            values.Add(text);
        }
        if (values.Count == 0 && v.IncludeAll)
        {
            values.Add("$__all");
        }
        return values;
    }

    private static async Task<IResult> VariablesAsync(string uid, DateTimeOffset? from, DateTimeOffset? to, DashboardStore store, PromDatasource prom, LokiDatasource loki, CancellationToken ct)
    {
        if (store.Find(uid) is not { } d)
        {
            return Results.NotFound();
        }
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-6);
        var list = new List<object>();
        foreach (var v in (d.Variables ?? []).Where(v => v.Hide < 2))
        {
            try
            {
                list.Add(new { v.Name, options = await OptionsAsync(v, start, end, prom, loki, ct), error = (string?)null });
            }
            catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException)
            {
                list.Add(new { v.Name, options = (IReadOnlyList<string>)[], error = (string?)ex.Message });
            }
        }
        return Results.Ok(list);
    }

    /// <summary>What a variable offers: a label's values from Prometheus or Loki, or its fixed options.</summary>
    private static async Task<IReadOnlyList<string>> OptionsAsync(Variable v, DateTimeOffset start, DateTimeOffset end, PromDatasource prom, LokiDatasource loki, CancellationToken ct)
    {
        IReadOnlyList<string> values;
        if (v.Type == "custom")
        {
            values = v.CustomOptions.Count > 0 ? v.CustomOptions
                : [.. (v.Query?.GetValue<string>() ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        }
        else if (v.Type != "query")
        {
            return [];
        }
        else if (v.Datasource == LokiDatasource.Uid || v.Query is JsonObject)
        {
            var label = v.Query?["label"]?.GetValue<string>() ?? "";
            values = label.Length == 0 ? [] : await loki.LabelValuesAsync(label, v.Query?["stream"]?.GetValue<string>(), start, end, ct);
        }
        else
        {
            var m = LabelValues().Match(v.Query?.GetValue<string>() ?? "");
            if (!m.Success)
            {
                throw new DatasourceException("Only label_values(...) variables are supported.");
            }
            values = await prom.LabelValuesAsync(m.Groups["label"].Value, m.Groups["match"].Success ? m.Groups["match"].Value : null, start, end, ct);
        }
        if (v.Regex is { Length: > 2 } re && re.StartsWith('/'))
        {
            var pattern = new Regex(re.Trim('/'), RegexOptions.None, TimeSpan.FromSeconds(1));
            values = [.. values.Where(x => pattern.IsMatch(x))];
        }
        return v.Sort is 1 or 3 or 5 ? [.. values.Order(StringComparer.OrdinalIgnoreCase)] : v.Sort is 2 or 4 or 6 ? [.. values.OrderDescending(StringComparer.OrdinalIgnoreCase)] : values;
    }

    [GeneratedRegex(@"^\s*label_values\(\s*(?:(?<match>.+?)\s*,\s*)?(?<label>[a-zA-Z_]\w*)\s*\)\s*$")]
    private static partial Regex LabelValues();

    private static async Task<IResult> QueryAsync(string uid, int key, PanelQuery q, DashboardStore store, SqlDatasource sql, PromDatasource prom, LokiDatasource loki,
        IOptions<DashboardOptions> options, CancellationToken ct)
    {
        if (store.Find(uid) is not { } d || key < 0 || key >= d.Panels.Count)
        {
            return Results.NotFound();
        }
        if (q.To <= q.From || q.To - q.From > MaxRange)
        {
            return AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.");
        }
        var panel = d.Panels[key];
        if (!Supported(panel))
        {
            return AuthEndpoints.Problem(400, "unsupported", "This panel's datasource is not served by the app.");
        }
        // As Grafana: the bucket is (range / points) rounded to a nice size, unless the caller fixed it.
        var interval = q.IntervalMs is > 0
            ? TimeSpan.FromMilliseconds(q.IntervalMs.Value)
            : SqlMacros.RoundInterval((q.To - q.From) / Math.Clamp(q.MaxDataPoints ?? 400, 10, 5000));

        // The dashboard's variables: as chosen on the page (a query variable only among its options), else their defaults.
        var vars = new Dictionary<string, VariableValue>(StringComparer.Ordinal);
        foreach (var v in d.Variables ?? [])
        {
            IReadOnlyList<string> chosen = q.Vars?.GetValueOrDefault(v.Name) is { Length: > 0 } picked ? picked : Defaults(v);
            IReadOnlyList<string> offered = [];
            if (v.Type is "query" or "custom")
            {
                try
                {
                    offered = await OptionsAsync(v, q.From, q.To, prom, loki, ct);
                }
                catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException)
                {
                    offered = [];
                }
                chosen = [.. chosen.Where(c => c == "$__all" ? v.IncludeAll : offered.Contains(c))];
                if (chosen.Count == 0)
                {
                    chosen = v.IncludeAll ? ["$__all"] : offered.Take(1).ToList();
                }
            }
            vars[v.Name] = new VariableValue(v, chosen, offered);
        }

        var o = options.Value;
        var results = new List<TargetResult>();
        foreach (var t in panel.Targets)
        {
            try
            {
                results.Add(t.Datasource switch
                {
                    PromDatasource.Uid => await PromTargetAsync(t, panel, q, interval, vars, prom, o, ct),
                    LokiDatasource.Uid => await LokiTargetAsync(t, panel, q, interval, vars, loki, o, ct),
                    _ => await SqlTargetAsync(t, q, interval, sql, ct),
                });
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or FormatException or InvalidOperationException or DatasourceException or HttpRequestException)
            {
                results.Add(new TargetResult(t.RefId, t.Format, null, null, t.Datasource == LokiDatasource.Uid && ex is HttpRequestException
                    ? "Loki is not reachable: the logs need the logging profile." : ex.Message));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                results.Add(new TargetResult(t.RefId, t.Format, null, null, "The query took too long."));
            }
        }
        return Results.Ok(new { intervalMs = (long)interval.TotalMilliseconds, results });
    }

    private static async Task<TargetResult> SqlTargetAsync(PanelTarget t, PanelQuery q, TimeSpan interval, SqlDatasource sql, CancellationToken ct)
    {
        var table = await sql.QueryAsync(SqlMacros.Expand(t.RawSql!, q.From, q.To, interval), ct);
        return t.Format == "time_series"
            ? new TargetResult(t.RefId, t.Format, null, SeriesShaping.ToSeries(table), null)
            : new TargetResult(t.RefId, t.Format, table, null, null);
    }

    private static async Task<TargetResult> PromTargetAsync(PanelTarget t, Panel panel, PanelQuery q, TimeSpan interval, Dictionary<string, VariableValue> vars,
        PromDatasource prom, DashboardOptions o, CancellationToken ct)
    {
        var step = Interpolation.Step(interval, panel.MinInterval ?? o.ScrapeInterval);
        var expr = Interpolation.Expand(t.Expr!, q.From, q.To, step, o.ScrapeInterval, vars, loki: false);
        if (t.Instant)
        {
            var at = await prom.InstantAsync(expr, q.To, ct);
            return t.Format == "table"
                ? new TargetResult(t.RefId, t.Format, InstantTable(at, t.RefId, panel.Targets.Count > 1), null, null)
                : new TargetResult(t.RefId, t.Format, null, [.. at.Select(s => new Series(Interpolation.Legend(t.LegendFormat, s.Labels, expr), s.Points))], null);
        }
        // As Grafana's backend: the range aligned to the step, so buckets fall on the same instants.
        var ms = (long)step.TotalMilliseconds;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(q.From.ToUnixTimeMilliseconds() / ms * ms);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(q.To.ToUnixTimeMilliseconds() / ms * ms);
        var series = await prom.RangeAsync(expr, start, end, step, ct);
        return new TargetResult(t.RefId, t.Format, null, [.. series.Select(s => new Series(Interpolation.Legend(t.LegendFormat, s.Labels, expr), s.Points))], null);
    }

    /// <summary>Grafana's table format for an instant query: Time, each label, Value (Value #A with several queries).</summary>
    private static TableResult InstantTable(IReadOnlyList<RawSeries> rows, string refId, bool several)
    {
        var labels = rows.SelectMany(r => r.Labels.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        var value = several ? $"Value #{refId}" : "Value";
        List<Column> columns = [new("Time", "time"), .. labels.Select(l => new Column(l, "string")), new(value, "number")];
        var data = rows.Select(r => (object?[])[
            DateTimeOffset.FromUnixTimeMilliseconds((long)(r.Points[0][0] ?? 0)).ToString("O", CultureInfo.InvariantCulture),
            .. labels.Select(l => (object?)r.Labels.GetValueOrDefault(l)),
            r.Points[0][1],
        ]).ToList();
        return new TableResult(columns, data, false);
    }

    private static async Task<TargetResult> LokiTargetAsync(PanelTarget t, Panel panel, PanelQuery q, TimeSpan interval, Dictionary<string, VariableValue> vars,
        LokiDatasource loki, DashboardOptions o, CancellationToken ct)
    {
        var range = q.To - q.From;
        // Grafana's Loki step: the interval, but never more than 11,000 points.
        var step = Interpolation.Step(interval, panel.MinInterval ?? TimeSpan.FromMilliseconds(1));
        if (range / 11000 > step)
        {
            step = range / 11000;
        }
        // An instant query's $__auto is the whole range, as Grafana has it.
        var expr = Interpolation.Expand(t.Expr!, q.From, q.To, step, o.ScrapeInterval, vars, loki: true, instant: t.Instant);
        var (lines, series) = t.Instant
            ? await loki.InstantAsync(expr, q.To, o.MaxLogLines, ct)
            : await loki.RangeAsync(expr, q.From, q.To, step, o.MaxLogLines, ct);
        if (lines is not null)
        {
            return new TargetResult(t.RefId, "logs", null, null, null, lines);
        }
        // Grafana names a Loki series without labels "{}" (a Prometheus one, by its query).
        var named = (series ?? []).Select(s => new Series(Interpolation.Legend(t.LegendFormat, s.Labels, "{}"), s.Points)).ToList();
        return t.Instant && t.Format == "table"
            ? new TargetResult(t.RefId, t.Format, InstantTable(series ?? [], t.RefId, panel.Targets.Count > 1), null, null)
            : new TargetResult(t.RefId, t.Format, null, named, null);
    }
}
