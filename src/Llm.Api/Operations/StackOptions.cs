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

/// <summary>Configuration section "CodeIntel": the code-intelligence service, where its admin surface is and the operator token.</summary>
public sealed class CodeIntelOptions
{
    public string Url { get; set; } = "http://code-intel:7700";
    /// <summary>Its address under the domain people's tools reach it at: HOST.DOMAIN (CODE_INTEL_HOST; one from before 8.0 keeps argus).</summary>
    public string Host { get; set; } = "code-intel";

    /// <summary>https://HOST.DOMAIN[:port]/PATH, as people's browsers, tools and GitLab reach it.</summary>
    public string PublicUrl(Identity.AuthOptions auth, string path) =>
        auth.HttpsPort == 443 ? $"https://{Host}.{auth.Domain}{path}" : $"https://{Host}.{auth.Domain}:{auth.HttpsPort}{path}";
    public string? AdminToken { get; set; }
    public string? GitlabUrl { get; set; }

    /// <summary>code-intel's pages and tools need its address and token (CODE_INTEL_KEY).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AdminToken);
}
