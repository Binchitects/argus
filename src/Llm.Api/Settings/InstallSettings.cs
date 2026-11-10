using Branding;
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
        ["GITLAB_URL"] = "CodeIntel:GitlabUrl", ["GITLAB_CA_FILE"] = "Tls:CompanyCaFile", ["BACKUP_KEEP"] = "Storage:BackupsKept", ["DB_HOST"] = "Database:Host",
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
        new("CODE_INTEL_KEY", "Secrets the installer made", "secret", "-", $"{Brand.CodeIntel}'s admin and chat credential, the app's only", "CodeIntel:AdminToken"),
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
    /// The app's configuration keys that are the stack's own wiring, each with why it is no setting: where its services,
    /// volumes and files are inside its containers, the secrets between them, how a Helm release or several machines are
    /// laid out, and what only tests turn off. A change breaks the stack, so they are not on the Settings page; the
    /// installation's settings above move them where one is needed (the database, GitLab). What an admin may want to
    /// change is a setting instead (SettingsCatalog).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Wiring = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CodeIntel:Url"] = $"{Brand.CodeIntel}'s admin surface on the stack's network; {Brand.CodeIntel} runs beside the app",
        ["Audit:AnchorDir"] = "The audit volume as compose mounts it into the app",
        ["Auth:AdminUserName"] = "The first admin's name, made when nobody exists; a later change would leave that account unprotected",
        ["Auth:DirectoryFile"] = $"The file the app writes for {Brand.CodeIntel} on their shared volume",
        ["Blender:Dir"] = "The blender-jobs volume as the app mounts it",
        ["Browser:ProxyClients"] = "The browser's container, the only one its proxy serves",
        ["Browser:ProxyPort"] = "The proxy port the browser's container is started with",
        ["Browser:Url"] = "The browser's DevTools on the stack's network (BROWSER_URL in compose)",
        ["Certificates:CaFile"] = "The CA file a Helm release mounts from a Secret; compose reads the certificate served",
        ["Certificates:Probe"] = "Traefik on the stack's network, where the site's chain is read",
        ["Chat:CodeIntelChatToken"] = $"The secret {Brand.CodeIntel} and the app share for the chat, made by the installer",
        ["Chat:GatewayUrl"] = "LiteLLM on the stack's network",
        ["Compliance:Starter"] = "Read once, at a new installation's first start, before anything can be saved; Admin → Compliance applies the same defaults a step at a time",
        ["Dashboards:AlertmanagerUrl"] = "Alertmanager on the stack's network",
        ["Dashboards:LokiUrl"] = "Loki on the stack's network (the logging feature)",
        ["Dashboards:Path"] = "The dashboard files inside the image",
        ["Dashboards:SqlDatabase"] = "The gateway's database, which the installer makes with that name",
        ["Engine:ConfigDir"] = "The volume the app and the engines share for the engines' desired state",
        ["Engine:Enabled"] = "Off only in tests, where no engine runs",
        ["Engine:EngineLibraryDir"] = "The model library's path inside the engines' containers",
        ["Engine:HardwareFile"] = "The file the GPU probe writes on the engines' status volume",
        ["Engine:ImageModelDir"] = "The picture model's folder in the library, as the media servers lay it out",
        ["Engine:ImageTextEncoder"] = "The picture model's text encoder file, as the media servers lay it out",
        ["Engine:LibraryDir"] = "The model library's path inside the app's container (MODELS_DIR is the installation's)",
        ["Engine:LlamaCpp"] = "Whether the Helm release runs llama.cpp (llamacpp.enabled in its values)",
        ["Engine:MediaOwnGpus"] = "Set by the Helm chart, whose media servers have pods and GPUs of their own",
        ["Engine:MediaStatusDir"] = "The media servers' status volume as the app mounts it",
        ["Engine:Url"] = "llama.cpp's router on the stack's network",
        ["Engine:Vllm"] = "The engines of several machines or Helm instances: their hosts and shared folders, laid out with the machines (deployment.md)",
        ["Engine:VllmStatusDir"] = "The engines' status volume as the app mounts it",
        ["Gateway:Url"] = "LiteLLM on the stack's network",
        ["Knowledge:FolderRoot"] = "Where an admin mounts the folders a folder source reads, inside the app's container",
        ["Laya:Url"] = "Laya on the stack's network",
        ["Lean:Dir"] = "The lean-jobs volume as the app mounts it",
        ["Oidc:ApiSecret"] = "The engine API's client secret, made by the installer and shared with its client",
        ["Oidc:LangfuseSecret"] = "Langfuse's client secret, made by the installer and shared with Langfuse",
        ["Plugins:Directory"] = "The plugins that come with the app, inside its image",
        ["Sandbox:Dir"] = "The sandbox-jobs volume as the app mounts it",
        ["Stack:LiteLlmProbeUrl"] = "LiteLLM on the stack's network, for the health checks",
        ["Stack:PrometheusUrl"] = "Prometheus on the stack's network",
        ["Storage:BackupDir"] = "The backups volume as the app mounts it (BACKUP_DIR is the installation's)",
        ["Storage:DockerDir"] = "Where Docker's own disk is mounted into the app",
        ["Workspaces:ArenaUrl"] = "The app as the workspaces reach it on the stack's network",
        ["Workspaces:CodeIntelUrl"] = $"{Brand.CodeIntel}'s MCP endpoint as the workspaces reach it",
        ["Workspaces:EngineHost"] = "The workspaces' service name on the stack's network",
        ["Workspaces:Key"] = "The secret the app and the workspaces' supervisor share, made by the installer",
        ["Workspaces:SelfUrl"] = "This replica's own address for the others, found by itself; set only where the network hides it",
        ["Workspaces:Url"] = "The workspaces' supervisor on the stack's network",
        ["Keys:Watch"] = "Off only in tests that run its checks themselves: off, a disabled person's API keys would keep working",
        ["Downloads:Directory"] = "Code Arena's downloads inside the image",
        ["Downloads:SignatureDirectory"] = "Their signatures inside the image",
        ["Database:RootCertificate"] = "The external database's CA file, mounted with the database in a compose override (deployment.md)",
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
