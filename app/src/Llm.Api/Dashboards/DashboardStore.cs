using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Dashboards;

public sealed class DashboardOptions
{
    /// <summary>The Grafana dashboard JSON files, mounted read-only (the same files Grafana provisions).</summary>
    public string Path { get; set; } = "/dashboards";

    /// <summary>The database behind the `litellm-db` datasource.</summary>
    public string SqlDatabase { get; set; } = "litellm";

    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxRows { get; set; } = 5000;
}

/// <summary>One query of a panel. Never sent to the browser.</summary>
public sealed record PanelTarget(string RefId, string Datasource, string? RawSql, string Format);

public sealed record Panel(int Key, string Type, string? Title, JsonObject Definition, IReadOnlyList<PanelTarget> Targets);

public sealed record Dashboard(string Uid, string Title, JsonObject? Time, string? Refresh, IReadOnlyList<Panel> Panels);

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
                    t["format"]?.GetValue<string>() ?? "table"))
                .ToList();
            // The browser gets how to draw the panel, never what it queries.
            var definition = (JsonObject)p.DeepClone();
            definition.Remove("targets");
            definition.Remove("panels");
            definition["key"] = panels.Count;
            definition["datasources"] = new JsonArray([.. targets.Select(t => JsonValue.Create(t.Datasource)).Distinct()]);
            panels.Add(new Panel(panels.Count, p["type"]?.GetValue<string>() ?? "", p["title"]?.GetValue<string>(), definition, targets));
        }
        return new Dashboard(uid, d["title"]?.GetValue<string>() ?? uid, d["time"]?.DeepClone() as JsonObject, d["refresh"]?.GetValue<string>(), panels);
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
