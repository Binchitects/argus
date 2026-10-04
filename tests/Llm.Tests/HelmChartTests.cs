namespace Llm.Tests;

/// <summary>The Helm chart carries copies of the stack's configuration: they must not drift from deploy/ (deploy/helm/check-chart.py checks the rest).</summary>
public sealed class HelmChartTests
{
    private static readonly string Deploy = Path.GetFullPath(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "deploy"));

    [Fact]
    public void The_charts_configuration_files_are_the_stacks_own()
    {
        var chart = Path.Combine(Deploy, "helm", "argus-arena", "files");
        var copies = new Dictionary<string, string>
        {
            ["litellm.yaml"] = "config/litellm.yaml", ["argus.yaml"] = "config/argus.yaml", ["searxng.yml"] = "config/searxng.yml",
            ["alertmanager.yml"] = "config/alertmanager.yml", ["loki.yml"] = "config/loki.yml", ["prometheus/prometheus.yml"] = "config/prometheus/prometheus.yml",
            ["router.sh"] = "services/llamacpp/router.sh", ["sd-serve.sh"] = "services/sd-serve.sh",
        };
        foreach (var rule in Directory.GetFiles(Path.Combine(Deploy, "config", "prometheus", "rules"), "*.yml"))
        {
            copies[$"prometheus/rules/{Path.GetFileName(rule)}"] = $"config/prometheus/rules/{Path.GetFileName(rule)}";
        }
        foreach (var (copy, source) in copies)
        {
            Assert.True(File.Exists(Path.Combine(chart, copy)), $"deploy/helm/argus-arena/files/{copy} is missing: copy deploy/{source} there.");
            Assert.True(File.ReadAllBytes(Path.Combine(chart, copy)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Deploy, source))),
                $"deploy/helm/argus-arena/files/{copy} differs from deploy/{source}: copy it again.");
        }
    }
}
