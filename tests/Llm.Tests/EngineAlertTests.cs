namespace Llm.Tests;

/// <summary>
/// The engines' alert rules (config/prometheus/rules/engines.yml) in the stack's own Prometheus (promtool test rules): a
/// vLLM cache full, requests waiting inside vLLM, preemption, a restart loop, a GPU's Xid and ECC errors; and nothing on a
/// llama.cpp-only stack.
/// </summary>
public sealed class EngineAlertTests(Promtool promtool) : IClassFixture<Promtool>
{
    private static readonly string Rules = File.ReadAllText(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "deploy", "config", "prometheus", "rules", "engines.yml"));

    [Fact]
    public async Task Each_engine_alert_fires_on_its_signal_and_a_llama_cpp_stack_fires_none()
    {
        var path = $"/tmp/{Guid.NewGuid():N}-engines.yml";
        // What fires, and with which labels, is tested; the annotations' wording is not (promtool compares them whole).
        var lines = new List<string>();
        var skipping = false;
        foreach (var line in Rules.Split('\n'))
        {
            if (line.StartsWith("        annotations:", StringComparison.Ordinal))
            {
                skipping = true;
                continue;
            }
            if (skipping && line.StartsWith("      - ", StringComparison.Ordinal))
            {
                skipping = false;
            }
            if (!skipping)
            {
                lines.Add(line);
            }
        }
        await promtool.CopyAsync(path, string.Join('\n', lines));
        var test = $$"""
            rule_files: ["{{path}}"]
            evaluation_interval: 30s
            tests:
              - interval: 30s
                input_series:
                  - series: 'vllm:kv_cache_usage_perc{model_name="flash"}'
                    values: '0.97x30'
                  - series: 'vllm:kv_cache_usage_perc{model_name="oss"}'
                    values: '0.5x30'
                  - series: 'vllm:num_requests_waiting{model_name="flash"}'
                    values: '3x30'
                  - series: 'vllm:num_preemptions_total{model_name="flash"}'
                    values: '0+10x30'
                  - series: 'process_start_time_seconds{component="inference",model="flash"}'
                    values: '1 2 3 4 5 5 5 5 5 5 5 5'
                  - series: 'DCGM_FI_DEV_XID_ERRORS{gpu="2"}'
                    values: '0 0 0 79 79 79 79 79'
                  - series: 'DCGM_FI_DEV_ECC_DBE_VOL_TOTAL{gpu="3"}'
                    values: '0 0 1 1 1 1'
                alert_rule_test:
                  - eval_time: 6m
                    alertname: VllmKvCacheFull
                    exp_alerts:
                      - exp_labels: { severity: warning, component: inference, model_name: flash }
                  - eval_time: 5m
                    alertname: VllmRequestsWaiting
                    exp_alerts:
                      - exp_labels: { severity: warning, component: inference, model_name: flash }
                  - eval_time: 12m
                    alertname: VllmPreempting
                    exp_alerts:
                      - exp_labels: { severity: warning, component: inference, model_name: flash }
                  - eval_time: 4m
                    alertname: EngineRestartLoop
                    exp_alerts:
                      - exp_labels: { severity: critical, component: inference, model: flash }
                  - eval_time: 3m
                    alertname: GpuXidErrors
                    exp_alerts:
                      - exp_labels: { severity: critical, component: gpu, gpu: "2" }
                  - eval_time: 2m
                    alertname: GpuEccErrors
                    exp_alerts:
                      - exp_labels: { severity: critical, component: gpu, gpu: "3" }
              - interval: 30s
                input_series:
                  - series: 'up{component="inference",job="llamacpp"}'
                    values: '1x30'
                alert_rule_test:
                  - { eval_time: 10m, alertname: VllmKvCacheFull, exp_alerts: [] }
                  - { eval_time: 10m, alertname: VllmRequestsWaiting, exp_alerts: [] }
                  - { eval_time: 10m, alertname: GpuXidErrors, exp_alerts: [] }
            """;
        var (exit, output) = await promtool.RunAsync("engines.test.yml", test, "test", "rules");
        Assert.True(exit == 0, output);
        var (checkExit, checkOutput) = await promtool.RunAsync("engines-check.yml", Rules, "check", "rules");
        Assert.True(checkExit == 0, checkOutput);
    }
}
