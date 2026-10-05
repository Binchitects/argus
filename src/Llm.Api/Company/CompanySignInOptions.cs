namespace Llm.Api.Company;

/// <summary>
/// Configuration section "CompanySignIn": sign-in with the company's identity provider,
/// by OIDC (on when Issuer and ClientId are set) or SAML 2.0 (on when the provider's metadata, or its address, entity ID and certificate, are set).
/// </summary>
public sealed class CompanySignInOptions
{
    /// <summary>"oidc" or "saml": which one the sign-in page offers.</summary>
    public string Protocol { get; set; } = "oidc";

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

    /// <summary>SAML: where the identity provider publishes its metadata (entity ID, sign-in address, signing certificates).</summary>
    public string? SamlMetadataUrl { get; set; }

    /// <summary>SAML: the identity provider's metadata XML, pasted.</summary>
    public string? SamlMetadata { get; set; }

    /// <summary>SAML, by hand (wins over the metadata): the provider's HTTP-Redirect sign-in address.</summary>
    public string? SamlSsoUrl { get; set; }

    /// <summary>SAML, by hand (wins over the metadata): the provider's entity ID, the Issuer of its answers.</summary>
    public string? SamlIdpEntityId { get; set; }

    /// <summary>SAML, by hand (wins over the metadata): the provider's signing certificates, PEM or base64.</summary>
    public string? SamlCertificate { get; set; }

    /// <summary>SAML: this app's entity ID at the provider. Empty: https://DOMAIN.</summary>
    public string? SamlEntityId { get; set; }

    /// <summary>SAML: the attribute with the username. Empty: the NameID.</summary>
    public string? SamlUserNameAttribute { get; set; }

    public string SamlEmailAttribute { get; set; } = "email";

    public string SamlDisplayNameAttribute { get; set; } = "displayName";

    /// <summary>SAML: the attribute with the person's groups, one value each. Empty: no groups.</summary>
    public string SamlGroupsAttribute { get; set; } = "groups";

    /// <summary>SAML: answers nobody here asked for (sign-in started at the provider's portal) are taken. Off: refused.</summary>
    public bool SamlAllowIdpInitiated { get; set; }

    /// <summary>Members are admins here. Compared with the groups claim's (or attribute's) values exactly, ignoring case.</summary>
    public string? AdminGroup { get; set; }

    /// <summary>When set, only members may sign in; a person who left it is disabled at their next sign-in.</summary>
    public string? RequiredGroup { get; set; }

    /// <summary>The sign-in page says "Sign in with {ButtonLabel}".</summary>
    public string ButtonLabel { get; set; } = "your company account";

    public bool IsSaml => string.Equals(Protocol?.Trim(), "saml", StringComparison.OrdinalIgnoreCase);

    public bool OidcEnabled => !IsSaml && !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(ClientId);

    public bool SamlEnabled => IsSaml && (!string.IsNullOrWhiteSpace(SamlMetadataUrl) || !string.IsNullOrWhiteSpace(SamlMetadata) ||
        (!string.IsNullOrWhiteSpace(SamlSsoUrl) && !string.IsNullOrWhiteSpace(SamlIdpEntityId) && !string.IsNullOrWhiteSpace(SamlCertificate)));

    public bool Enabled => OidcEnabled || SamlEnabled;
}
