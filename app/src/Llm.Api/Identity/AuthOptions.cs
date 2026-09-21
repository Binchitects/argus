namespace Llm.Api.Identity;

/// <summary>Configuration section "Auth" (compose passes Auth__Domain and friends).</summary>
public sealed class AuthOptions
{
    /// <summary>LLM_DOMAIN. The app is served at its root; the session cookie covers every subdomain.</summary>
    public string Domain { get; set; } = "llm.localhost";

    /// <summary>Only used for building absolute URLs (login redirects, the OIDC issuer).</summary>
    public int HttpsPort { get; set; } = 443;

    public string AdminUserName { get; set; } = "admin";
    public string AdminEmail { get; set; } = "admin@example.com";

    /// <summary>Sets the first admin's password when nobody exists yet; never changes it afterwards.</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Group name other services see for admins (Grafana and Open WebUI map it to their admin role).</summary>
    public string AdminGroup { get; set; } = "admins";

    /// <summary>Authelia's users.yml, imported once so existing people keep their passwords.</summary>
    public string? ImportUsersFile { get; set; }

    /// <summary>Hash-free list of people for Argus (email -> username), rewritten on every change.</summary>
    public string? DirectoryFile { get; set; }

    public TimeSpan SessionIdle { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan SessionMax { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan RememberMe { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often a session is re-checked against the account (disabled, password reset, role change).</summary>
    public TimeSpan SessionRecheck { get; set; } = TimeSpan.FromMinutes(1);

    public string Origin => HttpsPort == 443 ? $"https://{Domain}" : $"https://{Domain}:{HttpsPort}";
}
