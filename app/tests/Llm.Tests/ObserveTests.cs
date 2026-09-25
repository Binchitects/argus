using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Llm.Api.Dashboards;

namespace Llm.Tests;

/// <summary>The metric and log panels, the log viewer and the alerts, against a fake Prometheus, Loki and Alertmanager.</summary>
[Collection(nameof(AppCollection))]
public sealed partial class ObserveTests(AppFixture app) : IAsyncLifetime
{
    private static readonly DateTimeOffset End = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        app.Observe.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        app.Observe.Reset();
        return Task.CompletedTask;
    }

    private async Task<TestBrowser> Admin() => await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("O", CultureInfo.InvariantCulture));

    private static long Unix(DateTimeOffset t) => t.ToUnixTimeSeconds();

    [GeneratedRegex(@"\$(__|\{|[A-Za-z_])|\[\[")]
    private static partial Regex Unexpanded();

    [Fact]
    public async Task Every_metric_and_log_panel_of_the_real_dashboards_asks_a_fully_expanded_query()
    {
        var b = await Admin();
        var list = await b.JsonAsync(await b.GetAsync("/api/dashboards/"));
        var asked = 0;
        foreach (var uid in list.EnumerateArray().Select(d => d.GetProperty("uid").GetString()!))
        {
            var d = await b.JsonAsync(await b.GetAsync($"/api/dashboards/{uid}"));
            foreach (var p in d.GetProperty("panels").EnumerateArray())
            {
                // The page is not sent the queries: a panel's datasources say which ones ask Prometheus or Loki.
                var metric = p.TryGetProperty("datasources", out var sources) && sources.EnumerateArray()
                    .Any(s => s.GetString() is PromDatasource.Uid or LokiDatasource.Uid);
                if (!metric || !p.GetProperty("supported").GetBoolean())
                {
                    continue;
                }
                app.Observe.Requests.Clear();
                var res = await b.Http.PostAsJsonAsync(new Uri($"/api/dashboards/{uid}/panels/{p.GetProperty("key").GetInt32()}/query", UriKind.Relative),
                    new { from = End.AddHours(-6), to = End });
                await StatusAssert.Is(HttpStatusCode.OK, res);
                var r = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
                foreach (var t in r.GetProperty("results").EnumerateArray())
                {
                    Assert.True(t.GetProperty("error").ValueKind == JsonValueKind.Null, $"{uid} / {p.GetProperty("title")}: {t.GetProperty("error")}");
                }
                foreach (var q in app.Observe.Requests.Where(q => q.Path.EndsWith("/query", StringComparison.Ordinal) || q.Path.EndsWith("/query_range", StringComparison.Ordinal)))
                {
                    Assert.False(Unexpanded().IsMatch(q.Args["query"]!), $"{uid} / {p.GetProperty("title")}: {q.Args["query"]}");
                    asked++;
                }
            }
        }
        Assert.True(asked > 100, $"only {asked} metric queries were asked");
    }

    [Fact]
    public async Task A_prometheus_panel_asks_on_the_step_grid_and_names_series_by_the_legend()
    {
        var b = await Admin();
        app.Observe.Answers["/api/v1/query_range"] = _ => """
            {"status":"success","data":{"resultType":"matrix","result":[
              {"metric":{"name":"llamacpp"},"values":[[1790000000,"0.5"],[1790000015,"NaN"]]}]}}
            """;
        var d = await b.JsonAsync(await b.GetAsync("/api/dashboards/host-containers"));
        // Its query is sum by (name) (...), legend "{{name}}".
        var panel = d.GetProperty("panels").EnumerateArray().First(p => p.TryGetProperty("title", out var t) && t.GetString() == "Container CPU (cores)");
        var from = new DateTimeOffset(2026, 9, 20, 11, 0, 7, TimeSpan.Zero);
        var res = await b.Http.PostAsJsonAsync(new Uri($"/api/dashboards/host-containers/panels/{panel.GetProperty("key").GetInt32()}/query", UriKind.Relative),
            new { from, to = End.AddSeconds(7), intervalMs = 30_000 });
        var r = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        var asked = app.Observe.To("/api/v1/query_range").First();
        // As Grafana's backend: the range floored to the step.
        Assert.Equal(Unix(End.AddHours(-1)).ToString(CultureInfo.InvariantCulture), asked.Args["start"]);
        Assert.Equal(Unix(End).ToString(CultureInfo.InvariantCulture), asked.Args["end"]);
        Assert.Equal("30", asked.Args["step"]);
        var series = r.GetProperty("results")[0].GetProperty("series")[0];
        Assert.Equal("llamacpp", series.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, series.GetProperty("points")[1][1].ValueKind);
    }

    [Fact]
    public async Task A_dashboards_list_variable_offers_loki_labels_and_takes_only_those()
    {
        var b = await Admin();
        var vars = await b.JsonAsync(await b.GetAsync($"/api/dashboards/stack-logs/variables?from={Iso(End.AddHours(-1))}&to={Iso(End)}"));
        var container = vars.EnumerateArray().First(v => v.GetProperty("name").GetString() == "container");
        Assert.Equal(["app", "web"], container.GetProperty("options").EnumerateArray().Select(o => o.GetString()));

        var d = await b.JsonAsync(await b.GetAsync("/api/dashboards/stack-logs"));
        var logs = d.GetProperty("panels").EnumerateArray().First(p => p.GetProperty("type").GetString() == "logs");
        app.Observe.Requests.Clear();
        await b.Http.PostAsJsonAsync(new Uri($"/api/dashboards/stack-logs/panels/{logs.GetProperty("key").GetInt32()}/query", UriKind.Relative),
            new { from = End.AddHours(-1), to = End, vars = new Dictionary<string, string[]> { ["container"] = ["web", "not-offered\"}"] } });
        var query = app.Observe.To("/loki/api/v1/query_range").Single().Args["query"]!;
        Assert.Contains("\"web\"", query, StringComparison.Ordinal);
        Assert.DoesNotContain("not-offered", query, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_query_is_built_from_the_filters_with_every_value_escaped()
    {
        Assert.Equal("{container=~\".+\"}", LogEndpoints.Query(new LogFilter(null, null, null, null, null, null, null, null)));
        Assert.Equal(
            """{container=~"app|a\\.b"} |~ "(?i)say \"hi\" \\(now\\)" | detected_level=~"(?i)^(e|err|error|fatal|crit|critical|panic|emerg|alert|w|warn|warning)$" """.TrimEnd(),
            LogEndpoints.Query(new LogFilter(null, null, ["app", "a.b", " "], "say \"hi\" (now)", "warn", null, null, null)));
        Assert.Equal("error", LogEndpoints.LevelName("E"));
        Assert.Equal("error", LogEndpoints.LevelName("ERROR"));
        Assert.Equal("warn", LogEndpoints.LevelName("warning"));
        Assert.Equal("info", LogEndpoints.LevelName("I"));
        Assert.Equal("info", LogEndpoints.LevelName("Information"));
        Assert.Equal("other", LogEndpoints.LevelName("unknown"));
        Assert.Equal("other", LogEndpoints.LevelName(null));
    }

    [Fact]
    public async Task The_log_viewer_reads_lines_newest_first_and_a_live_tail_asks_after_the_newest()
    {
        var b = await Admin();
        app.Observe.Answers["/loki/api/v1/query_range"] = _ => """
            {"status":"success","data":{"resultType":"streams","result":[
              {"stream":{"container":"app","detected_level":"error"},"values":[["1790000060000000000","boom two"],["1790000000000000000","boom one"]]},
              {"stream":{"container":"web"},"values":[["1790000030000000000","boom web"]]}]}}
            """;
        var body = await b.JsonAsync(await b.GetAsync($"/api/admin/logs/?container=app&container=web&search=boom&level=error&from={Iso(End.AddHours(-1))}&to={Iso(End)}&limit=3"));
        Assert.Equal(["boom two", "boom web", "boom one"], body.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("line").GetString()));
        Assert.Equal("app", body.GetProperty("lines")[0].GetProperty("labels").GetProperty("container").GetString());
        Assert.True(body.GetProperty("more").GetBoolean());
        var asked = app.Observe.To("/loki/api/v1/query_range").Single();
        Assert.Equal(body.GetProperty("query").GetString(), asked.Args["query"]);
        Assert.StartsWith("{container=~\"app|web\"} |~ \"(?i)boom\" | detected_level=~", asked.Args["query"], StringComparison.Ordinal);
        Assert.Equal(End.AddHours(-1).ToUnixTimeMilliseconds() + "000000", asked.Args["start"]);
        Assert.Equal("backward", asked.Args["direction"]);

        app.Observe.Requests.Clear();
        await b.GetAsync($"/api/admin/logs/?from={Iso(End.AddHours(-1))}&to={Iso(End)}&after=1790000060000000000");
        Assert.Equal("1790000060000000001", app.Observe.To("/loki/api/v1/query_range").Single().Args["start"]);
    }

    [Fact]
    public async Task The_log_volume_merges_lokis_levels_into_the_viewers()
    {
        var b = await Admin();
        app.Observe.Answers["/loki/api/v1/query_range"] = _ => """
            {"status":"success","data":{"resultType":"matrix","result":[
              {"metric":{"detected_level":"unknown"},"values":[[1790000000,"5"]]},
              {"metric":{"detected_level":"debug"},"values":[[1790000000,"2"],[1790000060,"1"]]},
              {"metric":{"detected_level":"E"},"values":[[1790000000,"1"]]},
              {"metric":{"detected_level":"error"},"values":[[1790000000,"3"]]},
              {"metric":{"detected_level":"INFO"},"values":[[1790000060,"4"]]}]}}
            """;
        var body = await b.JsonAsync(await b.GetAsync($"/api/admin/logs/volume?from={Iso(End.AddHours(-1))}&to={Iso(End)}&intervalMs=60000"));
        var series = body.GetProperty("series").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("points"));
        Assert.Equal(["error", "info", "other"], series.Keys);
        Assert.Equal(4, series["error"][0][1].GetDouble());
        Assert.Equal(7, series["other"][0][1].GetDouble());
        Assert.Equal(1, series["other"][1][1].GetDouble());
        var q = app.Observe.To("/loki/api/v1/query_range").Single();
        Assert.Equal("sum by (detected_level) (count_over_time({container=~\".+\"} [1m]))", q.Args["query"]);
        Assert.Equal("60", q.Args["step"]);
    }

    [Fact]
    public async Task The_log_viewer_refuses_bad_filters_and_says_when_loki_is_down()
    {
        var b = await Admin();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.GetAsync($"/api/admin/logs/?from={Iso(End.AddDays(-31))}&to={Iso(End)}"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.GetAsync("/api/admin/logs/?level=loud"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.GetAsync("/api/admin/logs/?after=1e9"));
        Assert.Empty(app.Observe.Requests);

        app.Observe.Down["/loki/api/v1/query_range"] = true;
        var res = await b.GetAsync("/api/admin/logs/");
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, res);
        Assert.Contains("Loki is not reachable", (await b.JsonAsync(res)).GetProperty("error").GetString(), StringComparison.Ordinal);

        var containers = await b.JsonAsync(await b.GetAsync("/api/admin/logs/containers"));
        Assert.Equal(["app", "web"], containers.GetProperty("containers").EnumerateArray().Select(c => c.GetString()));
    }

    private const string Rules = """
        {"status":"success","data":{"groups":[
          {"name":"stack","file":"stack.yml","rules":[
            {"type":"alerting","name":"TargetDown","query":"up == 0","duration":300,"labels":{"severity":"critical"},
             "annotations":{"summary":"Target {{ $labels.job }} ({{$labels.instance}}) is down","description":"Scrapes fail."},
             "alerts":[{"labels":{"job":"loki"},"state":"firing","activeAt":"2026-09-20T11:00:00Z","value":"0"}],
             "health":"ok","state":"firing"},
            {"type":"recording","name":"job:up:sum","query":"sum(up)","health":"ok"},
            {"type":"alerting","name":"GpuHot","query":"gpu_temp > 85","duration":0,"labels":{"severity":"warning"},
             "annotations":{"summary":"GPU hot ({{ $value | printf \"%.0f\" }} degC)"},"alerts":[],"health":"err","lastError":"bad metric","state":"inactive"}]}]}}
        """;

    [Fact]
    public async Task Alerts_show_what_fires_and_every_rule_and_survive_either_source_down()
    {
        var b = await Admin();
        app.Observe.Answers["/api/v1/rules"] = _ => Rules;
        app.Observe.Answers["/api/v2/alerts"] = _ => """
            [{"labels":{"alertname":"GpuHot","severity":"warning"},"annotations":{"summary":"GPU hot"},"startsAt":"2026-09-20T10:00:00Z",
              "status":{"state":"suppressed","silencedBy":["s1"],"inhibitedBy":[]}},
             {"labels":{"alertname":"TargetDown","severity":"critical","job":"loki"},"annotations":{"summary":"Target loki is down"},"startsAt":"2026-09-20T11:05:00Z",
              "status":{"state":"active","silencedBy":[],"inhibitedBy":[]}}]
            """;
        var now = await b.JsonAsync(await b.GetAsync("/api/admin/alerts/"));
        Assert.Equal(["TargetDown", "GpuHot"], now.GetProperty("firing").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["s1"], now.GetProperty("firing")[1].GetProperty("silencedBy").EnumerateArray().Select(s => s.GetString()));
        var rules = now.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(["TargetDown", "GpuHot"], rules.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("firing", rules[0].GetProperty("state").GetString());
        Assert.Equal(300, rules[0].GetProperty("for").GetDouble());
        Assert.Equal(1, rules[0].GetProperty("active").GetInt32());
        Assert.Equal("bad metric", rules[1].GetProperty("lastError").GetString());

        app.Observe.Down["/api/v2/alerts"] = true;
        now = await b.JsonAsync(await b.GetAsync("/api/admin/alerts/"));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("firing").ValueKind);
        Assert.Equal("Alertmanager is not reachable.", now.GetProperty("errors").GetProperty("alertmanager").GetString());
        Assert.Equal(2, now.GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task Alert_history_turns_the_alerts_series_into_firings_with_their_summaries()
    {
        var b = await Admin();
        var from = End.AddDays(-1);
        var t0 = Unix(from.AddHours(3));
        var end = Unix(End);
        app.Observe.Answers["/api/v1/rules"] = _ => Rules;
        app.Observe.Answers["/api/v1/query_range"] = _ => $$$"""
            {"status":"success","data":{"resultType":"matrix","result":[
              {"metric":{"__name__":"ALERTS","alertname":"TargetDown","alertstate":"firing","job":"loki","instance":"loki:3100","severity":"critical"},
               "values":[[{{{t0}}},"1"],[{{{t0 + 30}}},"1"],[{{{t0 + 60}}},"1"],[{{{end - 60}}},"1"],[{{{end - 30}}},"1"],[{{{end}}},"1"]]}]}}
            """;
        var body = await b.JsonAsync(await b.GetAsync($"/api/admin/alerts/history?from={Iso(from)}&to={Iso(End)}"));
        var asked = app.Observe.To("/api/v1/query_range").Single();
        Assert.Equal("ALERTS{alertstate=\"firing\"}", asked.Args["query"]);
        Assert.Equal("30", asked.Args["step"]);
        var episodes = body.GetProperty("episodes").EnumerateArray().ToList();
        Assert.Equal(2, episodes.Count);
        // Still firing comes first, with no end.
        Assert.Equal(JsonValueKind.Null, episodes[0].GetProperty("end").ValueKind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(end - 60), episodes[0].GetProperty("start").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(t0), episodes[1].GetProperty("start").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(t0 + 90), episodes[1].GetProperty("end").GetDateTimeOffset());
        Assert.Equal("Target loki (loki:3100) is down", episodes[1].GetProperty("summary").GetString());
        Assert.False(episodes[1].GetProperty("labels").TryGetProperty("alertstate", out _));
    }

    [Fact]
    public void Alert_history_is_as_fine_as_the_rules_allow_and_summaries_leave_out_the_value()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), AlertEndpoints.Step(TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromSeconds(236), AlertEndpoints.Step(TimeSpan.FromDays(30)));
        Assert.Equal("GPU hot (… degC)", AlertEndpoints.Expand("GPU hot ({{ $value | printf \"%.0f\" }} degC)", new Dictionary<string, string>()));
        Assert.Equal("Disk sda full", AlertEndpoints.Expand("Disk {{ $labels.device }} full", new Dictionary<string, string> { ["device"] = "sda" }));
    }

    [Fact]
    public async Task Members_cannot_read_logs_or_alerts()
    {
        var admin = await Admin();
        var name = "ob" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var member = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        foreach (var path in new[] { "/api/admin/logs/", "/api/admin/logs/volume", "/api/admin/logs/containers", "/api/admin/alerts/", "/api/admin/alerts/history" })
        {
            await StatusAssert.Is(HttpStatusCode.Forbidden, await member.GetAsync(path));
        }
        Assert.Empty(app.Observe.Requests);
    }
}
