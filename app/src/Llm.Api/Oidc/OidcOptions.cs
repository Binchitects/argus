namespace Llm.Api.Oidc;

/// <summary>
/// Configuration section "Oidc": the secrets of the apps that sign people in
/// through the app. Same values as the *_OIDC_CLIENT_SECRET settings in .env.
/// A client whose secret is empty is not registered.
/// </summary>
public sealed class OidcOptions
{
    public string? OpenWebUiSecret { get; set; }
    public string? LangfuseSecret { get; set; }

    /// <summary>Machine clients: client_credentials for the engine API (scope "api").</summary>
    public string? ApiSecret { get; set; }

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromHours(12);
}

public static class OidcScopes
{
    public const string Groups = "groups";
    public const string Api = "api";
    public const string ApiResource = "llm-api";
}
