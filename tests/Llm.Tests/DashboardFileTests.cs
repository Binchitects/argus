using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Llm.Api.Dashboards;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>The stack's own Prometheus image, idle, for its promtool.</summary>
public sealed partial class Promtool : IAsyncLifetime
{
    private static readonly string Deploy = Path.GetFullPath(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "deploy"));

    /// <summary>The Prometheus the stack runs (deploy/docker-compose.yml), so its PromQL is what is checked.</summary>
    public static string Image { get; } = PrometheusImage().Match(File.ReadAllText(Path.Combine(Deploy, "docker-compose.yml"))).Groups[1].Value;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithEntrypoint("/bin/sh", "-c", "sleep 3600")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Runs promtool on a file written into the container: its exit code and everything it printed.</summary>
    public async Task<(long ExitCode, string Output)> RunAsync(string file, string text, params string[] args)
    {
        var path = $"/tmp/{Guid.NewGuid():N}-{file}";
        await _container.CopyAsync(Encoding.UTF8.GetBytes(text), path);
        var r = await _container.ExecAsync(["promtool", .. args, path]);
        return (r.ExitCode ?? -1, r.Stdout + r.Stderr);
    }

    [GeneratedRegex(@"image:\s*(prom/prometheus:\S+)")]
    private static partial Regex PrometheusImage();
}

/// <summary>
/// The dashboard files: they show any machine as it reports itself (no drive,
/// CPU, GPU or core count of the machine they were written on), a list that grows
/// with the core count is never a stat that stretches its row, their panels fit
/// the grid, and every Prometheus query parses in the stack's own Prometheus.
/// </summary>
public sealed partial class DashboardFileTests(Promtool promtool) : IClassFixture<Promtool>
{
    private static readonly DashboardStore Store = new(Options.Create(new DashboardOptions { Path = AppFixture.DashboardsPath }));

    private static IEnumerable<(Dashboard Dashboard, Panel Panel, PanelTarget Target)> PromTargets() =>
        from d in Store.All()
        from p in d.Panels
        from t in p.Targets
        where t.Datasource == PromDatasource.Uid && t.Expr is not null
        select (d, p, t);

    [Fact]
    public void No_dashboard_names_the_hardware_of_one_machine()
    {
        var files = Directory.GetFiles(AppFixture.DashboardsPath, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var found = MachineSpecific().Matches(File.ReadAllText(file)).Select(m => m.Value).Distinct().ToList();
            Assert.True(found.Count == 0, $"{Path.GetFileName(file)} names one machine's hardware: {string.Join(", ", found)}");
        }
        // A drive or a sensor is named from what the machine reports: label_replace takes a
        // part of a label ($1), never writes a fixed name in.
        foreach (var (d, p, t) in PromTargets())
        {
            foreach (var args in Calls(t.Expr!, "label_replace"))
            {
                Assert.True(args.Count == 5, $"{d.Uid} / {p.Title}: label_replace takes 5 arguments: {t.Expr}");
                var replacement = JsonSerializer.Deserialize<string>(args[2])!;
                Assert.True(replacement.Length == 0 || replacement.Contains('$', StringComparison.Ordinal),
                    $"{d.Uid} / {p.Title}: label_replace writes the fixed name \"{replacement}\"");
            }
        }
    }

    [Fact]
    public void A_list_that_grows_with_the_core_count_is_never_a_stat_and_has_a_line_of_the_grid_to_itself()
    {
        var perSensor = 0;
        foreach (var (d, p, t) in PromTargets())
        {
            if (p.Type is "stat" or "gauge")
            {
                Assert.False(PerCore().IsMatch(t.Expr! + " " + t.LegendFormat), $"{d.Uid} / {p.Title}: a value per core or sensor makes the row as tall as the core count");
            }
            // One line per sensor is one per core. Beside other panels the hottest and the average
            // say it in two; a chart of every sensor has the dashboard's whole width.
            if (RawCpuTemperature().IsMatch(t.Expr!))
            {
                Assert.True(p.Type == "timeseries" && p.Definition["gridPos"]!["w"]!.GetValue<int>() == 24,
                    $"{d.Uid} / {p.Title}: cpu_temperature_celsius per sensor beside other panels: {t.Expr}");
                perSensor++;
            }
        }
        // Which core runs hot is still on a dashboard.
        Assert.True(perSensor > 0, "no dashboard draws each CPU sensor");
    }

    [Fact]
    public void Every_panel_fits_the_grid_without_overlapping_another()
    {
        foreach (var d in Store.All())
        {
            var boxes = d.Panels.Where(p => p.Type != "row").Select(p => (p.Title, Grid: p.Definition["gridPos"]!)).Select(p => (p.Title,
                X: p.Grid["x"]!.GetValue<int>(), Y: p.Grid["y"]!.GetValue<int>(), W: p.Grid["w"]!.GetValue<int>(), H: p.Grid["h"]!.GetValue<int>())).ToList();
            foreach (var (a, i) in boxes.Select((b, i) => (b, i)))
            {
                Assert.True(a.X >= 0 && a.X + a.W <= 24, $"{d.Uid} / {a.Title} runs past the 24 columns");
                foreach (var b in boxes.Skip(i + 1))
                {
                    var overlap = a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
                    Assert.False(overlap, $"{d.Uid}: {a.Title} and {b.Title} overlap");
                }
            }
        }
    }

    [Fact]
    public async Task Every_prometheus_query_parses()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var rules = new StringBuilder("groups:\n  - name: dashboards\n    rules:\n");
        var count = 0;
        foreach (var (d, _, t) in PromTargets())
        {
            // Each variable at All, as a dashboard opens.
            var vars = (d.Variables ?? []).ToDictionary(v => v.Name, v => new VariableValue(v, ["$__all"], ["app", "web"]));
            var expr = Interpolation.Expand(t.Expr!, from, from.AddHours(6), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), vars, loki: false);
            rules.Append(CultureInfo.InvariantCulture, $"      - record: dashboard_query_{count++}\n        expr: {JsonSerializer.Serialize(expr)}\n");
        }
        Assert.True(count > 100, $"only {count} Prometheus queries");
        var (exit, output) = await promtool.RunAsync("dashboards.yml", rules.ToString(), "check", "rules");
        Assert.True(exit == 0, output);
        Assert.Contains($"SUCCESS: {count} rules found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drives_are_named_by_their_device_then_their_model_on_a_machine_with_one_three_or_no_nvme_drives()
    {
        var queries = PromTargets().Where(x => x.Target.Expr!.Contains("node_nvme_info", StringComparison.Ordinal)).Select(x => x.Target.Expr!).Distinct().ToList();
        Assert.True(queries.Count == 1, "every NVMe panel names its drives with the same query");
        Assert.Equal(3, PromTargets().Count(x => x.Target.Expr == queries[0] && x.Target.LegendFormat == "{{drive}}"));
        // "one": a drive with its model. "three": two of one model (told apart by the device,
        // which comes first so a cell that cuts the name keeps it), one reporting no model, and a
        // second sensor (temp2) that is not the composite. "old": a node-exporter without the nvme
        // collector. "sata": no NVMe drive at all.
        var test = $$"""
            evaluation_interval: 1m
            tests:
              - interval: 1m
                input_series:
                  - series: 'node_hwmon_temp_celsius{instance="one",chip="nvme_nvme0",sensor="temp1"}'
                    values: '41x3'
                  - series: 'node_nvme_info{instance="one",device="nvme0",model="Drive A",serial="s1",state="live"}'
                    values: '1x3'
                  - series: 'node_hwmon_temp_celsius{instance="three",chip="nvme_nvme0",sensor="temp1"}'
                    values: '50x3'
                  - series: 'node_hwmon_temp_celsius{instance="three",chip="nvme_nvme0",sensor="temp2"}'
                    values: '60x3'
                  - series: 'node_hwmon_temp_celsius{instance="three",chip="nvme_nvme1",sensor="temp1"}'
                    values: '51x3'
                  - series: 'node_hwmon_temp_celsius{instance="three",chip="nvme_nvme2",sensor="temp1"}'
                    values: '52x3'
                  - series: 'node_nvme_info{instance="three",device="nvme0",model="Drive B",serial="s2",state="live"}'
                    values: '1x3'
                  - series: 'node_nvme_info{instance="three",device="nvme1",model="Drive B",serial="s3",state="live"}'
                    values: '1x3'
                  - series: 'node_nvme_info{instance="three",device="nvme2",model="",serial="s4",state="live"}'
                    values: '1x3'
                  - series: 'node_hwmon_temp_celsius{instance="old",chip="nvme_nvme0",sensor="temp1"}'
                    values: '45x3'
                  - series: 'node_hwmon_temp_celsius{instance="sata",chip="platform_coretemp_0",sensor="temp1"}'
                    values: '70x3'
                  - series: 'node_disk_info{instance="sata",device="sda",rotational="1"}'
                    values: '1x3'
                promql_expr_test:
                  - expr: {{JsonSerializer.Serialize(queries[0])}}
                    eval_time: 2m
                    exp_samples:
                      - labels: '{instance="one",chip="nvme_nvme0",sensor="temp1",device="nvme0",model="Drive A",drive="nvme0 · Drive A"}'
                        value: 41
                      - labels: '{instance="three",chip="nvme_nvme0",sensor="temp1",device="nvme0",model="Drive B",drive="nvme0 · Drive B"}'
                        value: 50
                      - labels: '{instance="three",chip="nvme_nvme1",sensor="temp1",device="nvme1",model="Drive B",drive="nvme1 · Drive B"}'
                        value: 51
                      - labels: 'node_hwmon_temp_celsius{instance="three",chip="nvme_nvme2",sensor="temp1",drive="nvme2"}'
                        value: 52
                      - labels: 'node_hwmon_temp_celsius{instance="old",chip="nvme_nvme0",sensor="temp1",drive="nvme0"}'
                        value: 45
            """;
        var (exit, output) = await promtool.RunAsync("drives.yml", test, "test", "rules");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public async Task Gpus_are_told_apart_by_their_index_and_model_only_on_a_machine_with_more_than_one()
    {
        var targets = PromTargets().Where(x => x.Target.Expr!.Contains("nvidia_smi_", StringComparison.Ordinal)).ToList();
        Assert.True(targets.Count > 20, $"only {targets.Count} nvidia-smi queries");
        // Every nvidia-smi query ends in the same naming of its GPU, and its legend shows it.
        var naming = targets[0].Target.Expr![targets[0].Target.Expr!.IndexOf(" * on (instance, uuid) group_left (gpu) (", StringComparison.Ordinal)..];
        foreach (var (d, p, t) in targets)
        {
            Assert.True(t.Expr!.EndsWith(naming, StringComparison.Ordinal), $"{d.Uid} / {p.Title}: the GPU is not named: {t.Expr}");
            Assert.True(t.LegendFormat?.Contains("{{gpu}}", StringComparison.Ordinal) == true, $"{d.Uid} / {p.Title}: the legend \"{t.LegendFormat}\" leaves out the GPU");
        }
        // "one": a single GPU keeps its legends short ("temp", not "temp GPU 0 · ..."). "two": two
        // of one model, told apart by the index nvidia-smi gives them. "mixed": the vendor's
        // "NVIDIA " is left out where the name has it. "none": a machine with no GPU.
        var test = $$"""
            evaluation_interval: 1m
            tests:
              - interval: 1m
                input_series:
                  - series: 'nvidia_smi_temperature_gpu{instance="one",uuid="u1"}'
                    values: '50x3'
                  - series: 'nvidia_smi_index{instance="one",uuid="u1"}'
                    values: '0x3'
                  - series: 'nvidia_smi_gpu_info{instance="one",uuid="u1",name="NVIDIA GeForce RTX 3090",driver_version="595.91.07"}'
                    values: '1x3'
                  - series: 'nvidia_smi_temperature_gpu{instance="two",uuid="u2"}'
                    values: '60x3'
                  - series: 'nvidia_smi_temperature_gpu{instance="two",uuid="u3"}'
                    values: '61x3'
                  - series: 'nvidia_smi_index{instance="two",uuid="u2"}'
                    values: '0x3'
                  - series: 'nvidia_smi_index{instance="two",uuid="u3"}'
                    values: '1x3'
                  - series: 'nvidia_smi_gpu_info{instance="two",uuid="u2",name="NVIDIA GeForce RTX 3090"}'
                    values: '1x3'
                  - series: 'nvidia_smi_gpu_info{instance="two",uuid="u3",name="NVIDIA GeForce RTX 3090"}'
                    values: '1x3'
                  - series: 'nvidia_smi_temperature_gpu{instance="mixed",uuid="u4"}'
                    values: '70x3'
                  - series: 'nvidia_smi_temperature_gpu{instance="mixed",uuid="u5"}'
                    values: '71x3'
                  - series: 'nvidia_smi_index{instance="mixed",uuid="u4"}'
                    values: '0x3'
                  - series: 'nvidia_smi_index{instance="mixed",uuid="u5"}'
                    values: '1x3'
                  - series: 'nvidia_smi_gpu_info{instance="mixed",uuid="u4",name="Tesla T4"}'
                    values: '1x3'
                  - series: 'nvidia_smi_gpu_info{instance="mixed",uuid="u5",name="NVIDIA A100-SXM4-80GB"}'
                    values: '1x3'
                  - series: 'node_hwmon_temp_celsius{instance="none",chip="platform_coretemp_0",sensor="temp1"}'
                    values: '45x3'
                promql_expr_test:
                  - expr: {{JsonSerializer.Serialize("nvidia_smi_temperature_gpu" + naming)}}
                    eval_time: 2m
                    exp_samples:
                      - labels: '{instance="one",uuid="u1"}'
                        value: 50
                      - labels: '{instance="two",uuid="u2",gpu="GPU 0 · GeForce RTX 3090"}'
                        value: 60
                      - labels: '{instance="two",uuid="u3",gpu="GPU 1 · GeForce RTX 3090"}'
                        value: 61
                      - labels: '{instance="mixed",uuid="u4",gpu="GPU 0 · Tesla T4"}'
                        value: 70
                      - labels: '{instance="mixed",uuid="u5",gpu="GPU 1 · A100-SXM4-80GB"}'
                        value: 71
            """;
        var (exit, output) = await promtool.RunAsync("gpus.yml", test, "test", "rules");
        Assert.True(exit == 0, output);
        // With one GPU the label is not there, and its legend loses the space it would have taken.
        Assert.Equal("temp", Interpolation.Legend("temp {{gpu}}", new Dictionary<string, string> { ["uuid"] = "u1" }, "x"));
        Assert.Equal("temp GPU 1 · A100-SXM4-80GB", Interpolation.Legend("temp {{gpu}}", new Dictionary<string, string> { ["gpu"] = "GPU 1 · A100-SXM4-80GB" }, "x"));
    }

    /// <summary>Each call of a PromQL function in an expression: its top-level arguments, as written.</summary>
    private static IEnumerable<List<string>> Calls(string expr, string function)
    {
        foreach (Match m in Regex.Matches(expr, $@"\b{function}\s*\("))
        {
            var args = new List<string>();
            var depth = 0;
            var quoted = false;
            var start = m.Index + m.Length;
            for (var i = start; i < expr.Length; i++)
            {
                var c = expr[i];
                if (quoted)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c is '(' or '{' or '[')
                {
                    depth++;
                }
                else if (c is ')' or '}' or ']' && depth > 0)
                {
                    depth--;
                }
                else if (depth == 0 && c is ',' or ')')
                {
                    args.Add(expr[start..i].Trim());
                    start = i + 1;
                    if (c == ')')
                    {
                        break;
                    }
                }
            }
            yield return args;
        }
    }

    // Drive, CPU and GPU models, block devices and core counts: one machine's, not every machine's.
    [GeneratedRegex(@"(?i)\b(?:WD_BLACK|SN\d{3}X?|Samsung|990 PRO|Crucial|Kingston|Seagate|Toshiba|SK ?hynix|Intel Core|i[3579]-\d{4,5}\w*|\d{4,5}K|Ryzen|Threadripper|EPYC|Xeon|GeForce|RTX ?\d{4}|nvme\d+n\d+|sd[a-z]|\d+ (?:logical CPUs|cores|P-cores|E-cores|threads))\b")]
    private static partial Regex MachineSpecific();

    [GeneratedRegex(@"\bby\s*\((?:cpu|core|sensor)\)|\{\{\s*(?:cpu|core|sensor)\s*\}\}")]
    private static partial Regex PerCore();

    [GeneratedRegex(@"(?<!\b(?:max|min|avg|count)\s*\(\s*)\bcpu_temperature_celsius\b")]
    private static partial Regex RawCpuTemperature();
}
