namespace Llm.Api.Settings;

/// <summary>One setting of the installation's .env (deploy/settings): what it is, and how it is changed.</summary>
/// <param name="ConfigKey">The app's configuration key compose gives it as (Auth:Domain for DOMAIN), when the app sees it; then its value is shown.</param>
public sealed record InstallSetting(string Env, string Group, string Kind, string? Default, string What, string? ConfigKey)
{
    public bool IsSecret => Kind == "secret";

    /// <summary>The one command that changes it: the stack starts again with it, and waits until each service is healthy.</summary>
    public string Command => Env == InstallSettings.Features ? "arenactl repair --with FEATURE (or --without FEATURE)"
        : IsSecret ? $"arenactl config --set {Env}=@FILE" : $"arenactl config --set {Env}=VALUE";
}

/// <summary>
/// What the installation sets rather than the app: the address, the models' folder and engines, GitLab, backups, power
/// and heat, the sandboxes' size, scale, an external database (deploy/settings, built into the app), the features,
/// and the secrets the installer made. Settings shows them, with their value where the app sees one, and the command
/// that changes each; nothing here is saved by the app. And the stack's own wiring, which is no setting at all.
/// </summary>
public static class InstallSettings
{
    public const string Features = "COMPOSE_PROFILES";
    public const string GroupTitle = "Set by the installation";

    /// <summary>The .env keys compose gives the app, by the app's own key for them.</summary>
    private static readonly Dictionary<string, string> Seen = new(StringComparer.Ordinal)
    {
        ["DOMAIN"] = "Auth:Domain", ["ADMIN_EMAIL"] = "Auth:AdminEmail", ["HTTPS_PORT"] = "Auth:HttpsPort", ["ACME_EMAIL"] = "Stack:Acme",
        ["HSTS_SECONDS"] = "Stack:HstsSeconds", ["MODEL"] = "Engine:FirstModel", ["DEFAULT_MODELS"] = "Engine:DefaultModels", ["HF_TOKEN"] = "HuggingFace:Token",
        ["LLAMACPP_BACKEND"] = "Engine:Backend", ["MEDIA_GPU"] = "Engine:MediaGpu", ["EMBED_GPU"] = "Engine:EmbedGpu", ["LMCACHE_REMOTE"] = "Engine:LmcacheRemote",
        ["GITLAB_URL"] = "Argus:GitlabUrl", ["GITLAB_CA_FILE"] = "Tls:CompanyCaFile", ["BACKUP_KEEP"] = "Storage:BackupsKept", ["DB_HOST"] = "Database:Host",
        ["DB_PORT"] = "Database:Port", ["DB_USER"] = "Database:Username", ["DB_SSL_MODE"] = "Database:SslMode", [Features] = "Features:On",
    };

    /// <summary>The secrets the installer makes, which deploy/settings leaves out: shown as set or not, and why they stay.</summary>
    private static readonly InstallSetting[] Made =
    [
        new(Features, "Features", "text", "-", "The features that are on (installer.sh features): each optional service runs only when its feature is named", "Features:On"),
        new("APP_KEY", "Secrets the installer made", "secret", "-", "Encrypts the app's key ring and saved secrets: never changed, or sessions, two-factor and saved secrets are lost", "Auth:DataKey"),
        new("DB_PASSWORD", "Secrets the installer made", "secret", "-", "Postgres, for the app and the gateway", "Database:Password"),
        new("GATEWAY_KEY", "Secrets the installer made", "secret", "-", "The gateway's master key; the app and the gateway must agree", "Gateway:MasterKey"),
        new("ENGINE_KEY", "Secrets the installer made", "secret", "-", "The engine's key (the gateway and Prometheus use it)", "Engine:ApiKey"),
        new("ARGUS_KEY", "Secrets the installer made", "secret", "-", "Argus's admin and chat credential, the app's only", "Argus:AdminToken"),
        new("ADMIN_PASSWORD", "Secrets the installer made", "secret", "-", "The first admin's password, only used while nobody exists", "Auth:AdminPassword"),
    ];

    private static readonly Lazy<IReadOnlyList<InstallSetting>> _all = new(Load);

    /// <summary>Every installation setting: deploy/settings' in its order, then the features and the installer's secrets.</summary>
    public static IReadOnlyList<InstallSetting> All => _all.Value;

    private static List<InstallSetting> Load()
    {
        using var stream = typeof(InstallSettings).Assembly.GetManifestResourceStream("install-settings")
            ?? throw new InvalidOperationException("deploy/settings is not built into the app.");
        using var reader = new StreamReader(stream);
        var all = new List<InstallSetting>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var f = line.Split('|', 5, StringSplitOptions.TrimEntries);
            if (f.Length == 5)
            {
                all.Add(new(f[0], f[1], f[2], f[3] == "-" ? null : f[3], f[4], Seen.GetValueOrDefault(f[0])));
            }
        }
        all.AddRange(Made);
        return all;
    }

    /// <summary>
    /// The app's configuration keys that are the stack's own wiring: where its services, volumes and files are inside
    /// its containers, and what only tests turn off. No setting: a change breaks the stack, so they are not on the
    /// Settings page; the installation's settings above move them where one is needed (the database, GitLab). Also the
    /// external database's CA file (given in an override with the database: deployment.md), and the channels email-in
    /// shares with the chat platforms but has none of.
    /// </summary>
    public static readonly IReadOnlySet<string> Wiring = new HashSet<string>(StringComparer.Ordinal)
    {
        "Argus:Url", "Argus:GitlabAuth", "Argus:GitlabUsername", "Audit:AnchorDir", "Auth:AdminGroup", "Auth:AdminUserName", "Auth:DirectoryFile",
        "Blender:Dir", "Browser:ProxyClients", "Browser:ProxyPort", "Browser:Url", "Certificates:CaFile", "Certificates:Probe", "Chat:ArgusChatToken", "Chat:GatewayUrl",
        "Compliance:Starter", "Dashboards:AlertmanagerUrl", "Dashboards:LokiUrl", "Dashboards:Path", "Dashboards:ScrapeInterval", "Dashboards:SqlDatabase",
        "Engine:ConfigDir", "Engine:Enabled", "Engine:EngineLibraryDir", "Engine:HardwareFile", "Engine:ImageModelDir", "Engine:ImageTextEncoder", "Engine:LibraryDir",
        "Engine:LlamaCpp", "Engine:MediaOwnGpus", "Engine:MediaStatusDir", "Engine:Url", "Engine:Vllm", "Engine:VllmStatusDir", "Gateway:Url", "HuggingFace:BaseUrl",
        "Knowledge:DocumentPrefix", "Knowledge:EmbedKey", "Knowledge:EmbedModel", "Knowledge:EmbedUrl", "Knowledge:FolderRoot", "Knowledge:PgVector", "Knowledge:QueryPrefix",
        "Laya:Url", "Lean:Dir", "Oidc:ApiSecret", "Oidc:LangfuseSecret", "Plugins:Directory", "Replicas:AskTimeout", "Replicas:Enabled", "Replicas:Renew",
        "Sandbox:Dir", "Security:Watch", "Stack:LiteLlmProbeUrl", "Stack:PrometheusUrl", "Storage:BackupDir", "Storage:DockerDir", "Workspaces:ArenaUrl", "Workspaces:ArgusUrl",
        "Workspaces:EngineHost", "Workspaces:Key", "Workspaces:SelfUrl", "Workspaces:Url", "Keys:Watch", "Downloads:Directory", "Downloads:SignatureDirectory",
        "Database:RootCertificate", "Bots:Email:Channels",
    };

    /// <summary>The app's configuration keys an installation setting gives it: shown there, not on the app's own settings.</summary>
    public static IEnumerable<string> ConfigKeys => All.Select(s => s.ConfigKey).OfType<string>();

    public sealed record View(string Env, string Group, string Kind, string? Default, string What, string Command, string? Value, bool IsSet, bool Secret);

    /// <summary>Each with its value where the app sees one; a secret only as set or not.</summary>
    public static IEnumerable<View> ViewOf(IConfiguration config) => All.Select(s =>
    {
        var value = s.ConfigKey is { } key ? config[key] : null;
        return new View(s.Env, s.Group, s.Kind, s.Default, s.What, s.Command, s.IsSecret ? null : value, !string.IsNullOrEmpty(value), s.IsSecret);
    });
}
