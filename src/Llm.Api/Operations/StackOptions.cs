namespace Llm.Api.Operations;

/// <summary>Configuration section "Stack": where the monitoring and the gateway answer, for the Monitoring pages.</summary>
public sealed class StackOptions
{
    public string PrometheusUrl { get; set; } = "http://prometheus:9090";
    public string LiteLlmProbeUrl { get; set; } = "http://litellm:4000";

    /// <summary>Set ("letsencrypt") when Traefik gets its certificates from Let's Encrypt (ACME_EMAIL): Overview says who issued the one served.</summary>
    public string? Acme { get; set; }

    /// <summary>HSTS_SECONDS: Strict-Transport-Security's max-age; null: a year with Let's Encrypt, none (0) with your own CA.</summary>
    public int? HstsSeconds { get; set; }

    /// <summary>The max-age the stack sends, as Traefik works it out (config/traefik/routes.yml).</summary>
    public int Hsts => HstsSeconds ?? (string.IsNullOrEmpty(Acme) ? 0 : 31_536_000);
}

/// <summary>Configuration section "Argus": where its admin surface is and the operator token.</summary>
public sealed class ArgusOptions
{
    public string Url { get; set; } = "http://argus:7700";
    public string? AdminToken { get; set; }
    public string? GitlabUrl { get; set; }
    public string? GitlabAuth { get; set; }
    public string? GitlabUsername { get; set; }

    /// <summary>Argus's pages and tools need its address and token (ARGUS_KEY).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AdminToken);
}
