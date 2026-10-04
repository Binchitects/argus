namespace Llm.Api.Company;

/// <summary>Configuration section "CompanySignIn": sign-in with the company's OIDC identity provider. On when Issuer and ClientId are set.</summary>
public sealed class CompanySignInOptions
{
    /// <summary>The identity provider's issuer; its discovery document is at Issuer/.well-known/openid-configuration.</summary>
    public string? Issuer { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Empty for a public client (PKCE alone).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Space-separated; openid is always asked for.</summary>
    public string Scopes { get; set; } = "openid profile email";

    /// <summary>The claim with the username; an email-like value (alice@example.com) gives its part before the @.</summary>
    public string UserNameClaim { get; set; } = "preferred_username";

    /// <summary>The claim with the person's groups; a dotted path (realm_access.roles) reaches into an object. Empty: no groups.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>Members are admins here. Compared with the groups claim's values exactly, ignoring case.</summary>
    public string? AdminGroup { get; set; }

    /// <summary>When set, only members may sign in; a person who left it is disabled at their next sign-in.</summary>
    public string? RequiredGroup { get; set; }

    /// <summary>The sign-in page says "Sign in with {ButtonLabel}".</summary>
    public string ButtonLabel { get; set; } = "your company account";

    public bool Enabled => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(ClientId);
}
