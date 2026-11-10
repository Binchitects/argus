using Llm.Api.Gateway;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The app's own gateway for monitoring: its alert rules (config/prometheus/rules/gateway.yml) in the stack's Prometheus,
/// its metrics on the app's /metrics, and its health on Admin → Overview with Mode own.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class GatewayAlertTests(AppFixture app, Promtool promtool) : IClassFixture<Promtool>
{
    private static readonly string Rules = File.ReadAllText(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "deploy", "config", "prometheus", "rules", "gateway.yml"));

    [Fact]
    public async Task Errors_at_a_server_a_ledger_behind_and_a_slow_first_token_fire_and_a_quiet_gateway_fires_none()
    {
        var path = $"/tmp/{Guid.NewGuid():N}-gateway.yml";
        await promtool.CopyAsync(path, string.Join('\n', Rules.Split('\n').Where(l => !l.TrimStart().StartsWith("summary:", StringComparison.Ordinal) && !l.TrimStart().StartsWith("description:", StringComparison.Ordinal)
            && !l.TrimStart().StartsWith("annotations:", StringComparison.Ordinal))));
        var test = $$"""
            rule_files: ["{{path}}"]
            evaluation_interval: 30s
            tests:
              - interval: 30s
                input_series:
                  - series: 'arena_gateway_requests_total{model="m",deployment="down:8000",surface="api",status="failed"}'
                    values: '0+10x60'
                  - series: 'arena_gateway_upstream_errors_total{deployment="down:8000",reason="unreachable"}'
                    values: '0+10x60'
                  - series: 'arena_gateway_requests_total{model="m",deployment="fine:8000",surface="api",status="done"}'
                    values: '0+10x60'
                  - series: 'arena_gateway_log_pending'
                    values: '5000x60'
                  - series: 'arena_gateway_ttft_seconds_bucket{model="m",le="30"}'
                    values: '0x60'
                  - series: 'arena_gateway_ttft_seconds_bucket{model="m",le="60"}'
                    values: '0+5x60'
                  - series: 'arena_gateway_ttft_seconds_bucket{model="m",le="+Inf"}'
                    values: '0+5x60'
                alert_rule_test:
                  - eval_time: 25m
                    alertname: GatewayUpstreamErrors
                    exp_alerts:
                      - exp_labels: { severity: warning, component: gateway, deployment: "down:8000" }
                  - eval_time: 10m
                    alertname: GatewayLedgerBehind
                    exp_alerts:
                      - exp_labels: { severity: warning, component: gateway }
                  - eval_time: 29m
                    alertname: GatewaySlowFirstToken
                    exp_alerts:
                      - exp_labels: { severity: warning, component: gateway, model: m }
            """;
        var (exit, output) = await promtool.RunAsync("gateway.test.yml", test, "test", "rules");
        Assert.True(exit == 0, output);
        var (checkExit, checkOutput) = await promtool.RunAsync("gateway-check.yml", Rules, "check", "rules");
        Assert.True(checkExit == 0, checkOutput);
    }

    [Fact]
    public async Task The_gateways_metrics_are_on_the_apps_metrics_and_its_health_on_the_overview()
    {
        await using var f = app.Create(app.ConnectionStringFor("gwmon_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Gateway:Mode"] = "own" });
        var gateway = f.Services.GetRequiredService<ModelGateway>();
        using (var res = await gateway.ChatAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = "Qwen3.8-Flash-Next", ["stream"] = true, ["messages"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "hi" }),
        }, new GatewayCaller("nobody@example.test"), [], default))
        {
            await res.Content.ReadAsStringAsync();
        }
        var metrics = await f.CreateClient().GetStringAsync(new Uri("/metrics", UriKind.Relative));
        Assert.Contains("arena_gateway_requests_total{model=\"Qwen3.8-Flash-Next\",deployment=\"engine.test\",surface=\"chat\",status=\"done\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("arena_gateway_tokens_total{model=\"Qwen3.8-Flash-Next\",type=\"cached\"} 40", metrics, StringComparison.Ordinal);
        Assert.Contains("arena_gateway_ttft_seconds_count{model=\"Qwen3.8-Flash-Next\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("arena_gateway_log_pending ", metrics, StringComparison.Ordinal);

        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var services = (await admin.JsonAsync(await admin.GetAsync("/api/admin/services"))).EnumerateArray().ToList();
        var probe = services.Single(s => s.GetProperty("name").GetString() == "Model gateway");
        Assert.StartsWith("in the app:", probe.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
