using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Dashboards;

public sealed class DashboardOptions
{
    /// <summary>The dashboard files, in Grafana's JSON format, mounted read-only.</summary>
    public string Path { get; set; } = "/dashboards";

    /// <summary>The database behind the `litellm-db` datasource.</summary>
    public string SqlDatabase { get; set; } = "litellm";

    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxRows { get; set; } = 5000;

    /// <summary>Loki, behind the `loki` datasource (the logging profile).</summary>
    public string LokiUrl { get; set; } = "http://loki:3100";

    /// <summary>Alertmanager: the alerts that fire now.</summary>
    public string AlertmanagerUrl { get; set; } = "http://alertmanager:9093";

    /// <summary>Prometheus's scrape interval (Grafana's timeInterval): the finest step, and part of $__rate_interval.</summary>
    public TimeSpan ScrapeInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Log lines a logs panel shows at most (Grafana's maxLines).</summary>
    public int MaxLogLines { get; set; } = 1000;
}

/// <summary>A dashboard variable: a list from a datasource (query), a text box, or fixed options (custom).</summary>
public sealed record Variable(
    string Name, string? Label, string Type, string? Datasource, JsonNode? Query, bool Multi, bool IncludeAll, string? AllValue,
    string? Regex, int Sort, JsonNode? Current, IReadOnlyList<string> CustomOptions, int Hide);

/// <summary>One query of a panel. Never sent to the browser.</summary>
public sealed record PanelTarget(string RefId, string Datasource, string? RawSql, string Format, string? Expr = null, string? LegendFormat = null, bool Instant = false);

public sealed record Panel(int Key, string Type, string? Title, JsonObject Definition, IReadOnlyList<PanelTarget> Targets, TimeSpan? MinInterval = null);

public sealed record Dashboard(string Uid, string Title, JsonObject? Time, string? Refresh, IReadOnlyList<Panel> Panels, IReadOnlyList<Variable>? Variables = null);

/// <summary>
/// Reads the dashboard files on each request (they are small, and an edit then
/// shows without a restart). A panel is addressed by its position in the file:
/// provisioned panels carry no stable id of their own.
/// </summary>
public sealed class DashboardStore(IOptions<DashboardOptions> options)
{
    public IReadOnlyList<Dashboard> All()
    {
        var dir = options.Value.Path;
        if (!Directory.Exists(dir))
        {
            return [];
        }
        return [.. Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(Load).OfType<Dashboard>()];
    }

    public Dashboard? Find(string uid) => All().FirstOrDefault(d => d.Uid == uid);

    private static Dashboard? Load(string file)
    {
        if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject d || d["uid"]?.GetValue<string>() is not { } uid)
        {
            return null;
        }
        var panels = new List<Panel>();
        foreach (var p in Walk(d["panels"] as JsonArray))
        {
            var defaultDs = Uid(p["datasource"]);
            var targets = (p["targets"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(t => t["hide"]?.GetValue<bool>() != true)
                .Select(t => new PanelTarget(
                    t["refId"]?.GetValue<string>() ?? "A",
                    Uid(t["datasource"]) ?? defaultDs ?? "",
                    t["rawSql"]?.GetValue<string>(),
                    t["format"]?.GetValue<string>() ?? (t["rawSql"] is not null ? "table" : "time_series"),
                    t["expr"]?.GetValue<string>(),
                    t["legendFormat"]?.GetValue<string>(),
                    t["instant"]?.GetValue<bool>() == true || t["queryType"]?.GetValue<string>() == "instant"))
                .ToList();
            // The browser gets how to draw the panel, never what it queries.
            var definition = (JsonObject)p.DeepClone();
            definition.Remove("targets");
            definition.Remove("panels");
            definition["key"] = panels.Count;
            definition["datasources"] = new JsonArray([.. targets.Select(t => JsonValue.Create(t.Datasource)).Distinct()]);
            panels.Add(new Panel(panels.Count, p["type"]?.GetValue<string>() ?? "", p["title"]?.GetValue<string>(), definition, targets,
                Interpolation.ParseDuration(p["interval"]?.GetValue<string>())));
        }
        var variables = (d["templating"]?["list"] as JsonArray ?? []).OfType<JsonObject>().Select(v => new Variable(
            v["name"]?.GetValue<string>() ?? "",
            v["label"]?.GetValue<string>(),
            v["type"]?.GetValue<string>() ?? "query",
            Uid(v["datasource"]),
            v["query"]?.DeepClone(),
            v["multi"]?.GetValue<bool>() == true,
            v["includeAll"]?.GetValue<bool>() == true,
            v["allValue"]?.GetValue<string>(),
            v["regex"]?.GetValue<string>(),
            v["sort"] is JsonValue sv && sv.TryGetValue<int>(out var sort) ? sort : 0,
            v["current"]?.DeepClone(),
            [.. (v["options"] as JsonArray ?? []).OfType<JsonObject>().Select(o => o["value"]?.GetValue<string>() ?? "")],
            v["hide"] is JsonValue hv && hv.TryGetValue<int>(out var hide) ? hide : 0)).Where(v => v.Name.Length > 0).ToList();
        return new Dashboard(uid, d["title"]?.GetValue<string>() ?? uid, d["time"]?.DeepClone() as JsonObject, d["refresh"]?.GetValue<string>(), panels, variables);
    }

    private static IEnumerable<JsonObject> Walk(JsonArray? panels)
    {
        foreach (var p in (panels ?? []).OfType<JsonObject>())
        {
            yield return p;
            foreach (var child in Walk(p["panels"] as JsonArray))
            {
                yield return child;
            }
        }
    }

    private static string? Uid(JsonNode? ds) => ds switch
    {
        JsonObject o => o["uid"]?.GetValue<string>(),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => null,
    };
}
