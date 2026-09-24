using System.Text.Json.Nodes;
using Llm.Api.Endpoints;

namespace Llm.Api.Dashboards;

public sealed record PanelQuery(DateTimeOffset From, DateTimeOffset To, long? IntervalMs = null, int? MaxDataPoints = null);

public static class DashboardEndpoints
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(400);

    public static void MapDashboards(this IEndpointRouteBuilder app)
    {
        // Admins only: these dashboards show everyone's usage. People see their own under /api/usage/me.
        var g = app.MapGroup("/api/dashboards").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/", (DashboardStore store) => Results.Ok(store.All().Select(d => new
        {
            d.Uid, d.Title, panels = d.Panels.Count(p => p.Type != "row"),
            supported = d.Panels.Count(p => Supported(p)),
        })));
        g.MapGet("/{uid}", (string uid, DashboardStore store) =>
            store.Find(uid) is { } d
                ? Results.Ok(new
                {
                    d.Uid, d.Title, d.Time, d.Refresh,
                    panels = d.Panels.Select(p => WithSupport(p)),
                })
                : Results.NotFound());
        g.MapPost("/{uid}/panels/{key:int}/query", QueryAsync);
    }

    /// <summary>Datasources the app runs itself so far (Prometheus and Loki arrive in phase 5).</summary>
    public static bool Supported(Panel p) =>
        p.Type is "text" or "row" || (p.Targets.Count > 0 && p.Targets.All(t => t.Datasource == SqlDatasource.Uid && t.RawSql is not null));

    private static JsonObject WithSupport(Panel p)
    {
        var def = (JsonObject)p.Definition.DeepClone();
        def["supported"] = Supported(p);
        return def;
    }

    private static async Task<IResult> QueryAsync(string uid, int key, PanelQuery q, DashboardStore store, SqlDatasource sql, CancellationToken ct)
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
            return AuthEndpoints.Problem(400, "unsupported", "This panel's datasource is not served by the app yet.");
        }
        // As Grafana: the bucket is (range / points) rounded to a nice size, unless the caller fixed it.
        var interval = q.IntervalMs is > 0
            ? TimeSpan.FromMilliseconds(q.IntervalMs.Value)
            : SqlMacros.RoundInterval((q.To - q.From) / Math.Clamp(q.MaxDataPoints ?? 400, 10, 5000));
        var results = new List<TargetResult>();
        foreach (var t in panel.Targets)
        {
            try
            {
                var table = await sql.QueryAsync(SqlMacros.Expand(t.RawSql!, q.From, q.To, interval), ct);
                results.Add(t.Format == "time_series"
                    ? new TargetResult(t.RefId, t.Format, null, SeriesShaping.ToSeries(table), null)
                    : new TargetResult(t.RefId, t.Format, table, null, null));
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or FormatException or InvalidOperationException)
            {
                results.Add(new TargetResult(t.RefId, t.Format, null, null, ex.Message));
            }
        }
        return Results.Ok(new { intervalMs = (long)interval.TotalMilliseconds, results });
    }
}
