namespace Llm.Api.Operations;

/// <summary>Configuration section "Stack": what the deployment runs, for the Model, Settings and Monitoring pages.</summary>
public sealed class StackOptions
{
    public string? ComposeProfiles { get; set; }
    public string? ModelName { get; set; }
    public string? ModelFile { get; set; }
    public string? ModelContext { get; set; }
    public string? ModelMaxOutput { get; set; }
    public string? ModelReasoningEffort { get; set; }
    public string? ModelEnableThinking { get; set; }
    public string? ThinkingPresets { get; set; }
    public string? MtpDraftMax { get; set; }
    public string? GpuPowerLimitW { get; set; }
    public string? CpuPowerLimitW { get; set; }
    public string? PriceInputPerMtok { get; set; }
    public string? PriceCachedInputPerMtok { get; set; }
    public string? PriceOutputPerMtok { get; set; }
    public string? DefaultUserBudget { get; set; }

    /// <summary>The shipped env-samples, mounted read-only, that the Model page offers to switch to.</summary>
    public string EnvSamplesDir { get; set; } = "/env-samples";

    public string PrometheusUrl { get; set; } = "http://prometheus:9090";
    public string GrafanaProbeUrl { get; set; } = "http://grafana:3000";
    public string LiteLlmProbeUrl { get; set; } = "http://litellm:4000";
}

/// <summary>Configuration section "Argus": where its admin surface is and the operator token.</summary>
public sealed class ArgusOptions
{
    public string Url { get; set; } = "http://argus:7700";
    public string? AdminToken { get; set; }
    public string? GitlabUrl { get; set; }
    public string? GitlabAuth { get; set; }
    public string? GitlabUsername { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AdminToken);
}
