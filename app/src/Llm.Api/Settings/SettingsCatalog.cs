namespace Llm.Api.Settings;

/// <summary>
/// Every setting an admin can change in the app, in the order the Settings page
/// shows them. Stack settings are `.env` names and must be passed to the app by
/// compose as <c>StackEnv__NAME</c> (a test holds the two together).
/// </summary>
public static class SettingsCatalog
{
    private const string Branding = "Branding";
    private const string SignIn = "Sign-in and sessions";
    private const string Directory = "Company directory (LDAP)";
    private const string Chat = "Chat";
    private const string Credit = "Credit and prices";
    private const string Model = "Model";
    private const string Engine = "Engine and hardware";
    private const string Argus = "Argus (GitLab)";
    private const string Image = "Image generation";
    private const string Tools = "Python and web";
    private const string Deployment = "Deployment";
    private const string Monitoring = "Monitoring";
    private const string Backup = "Backup";

    public static readonly IReadOnlyList<string> Profiles =
        ["gateway", "proxy", "auth", "llamacpp", "vllm", "multi-model", "argus", "embed", "image", "sandbox", "websearch", "logging", "tracing", "smi", "dcgm", "cadvisor"];

    private const string Engine1 = "The engine restarts and reloads the model: chat and the API pause for a few minutes.";
    private const string Image1 = "The image server restarts (under a minute); the chat model is not touched.";
    private const string Sandbox1 = "The sandbox restarts (seconds); a Python run in progress fails.";

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        // ---------------------------------------------------------------- app, live --
        new("Branding:ProductName", Branding, "Product name", "Shown in the sidebar, on the sign-in page and in the browser tab.", SettingType.Text, SettingScope.Live)
            { Default = "LLM Service", Optional = false, Max = 60 },
        new("Branding:SignInHeadline", Branding, "Sign-in headline", "The sentence beside the sign-in form.", SettingType.Text, SettingScope.Live)
            { Default = "Your organisation's model, code search and usage, in one place.", Max = 160 },
        new("Branding:SupportContact", Branding, "Where to get help", "An email address or a link, shown on the sign-in page and in the account menu. Empty hides it.", SettingType.Text, SettingScope.Live)
            { Max = 200 },

        new("Auth:SessionIdle", SignIn, "Sign out after idle", "A session with no activity for this long ends.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "01:00:00", Unit = "minutes", Min = 5, Max = 1440, Optional = false, Impact = "Applies to sessions started after the restart." },
        new("Auth:SessionMax", SignIn, "Longest session", "Sign in again after this long, however active.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "12:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Auth:RememberMe", SignIn, "\"Keep me signed in\" lasts", "How long a device stays signed in when that box is ticked.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "30.00:00:00", Unit = "days", Min = 1, Max = 365, Optional = false },
        new("Throttle:MaxFailuresPerAccount", SignIn, "Wrong passwords before a lock", "Failed sign-ins for one account from one address, within the window, before that pair is locked.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "5", Min = 3, Max = 100, Optional = false },
        new("Throttle:AccountBan", SignIn, "Lock for", "How long that account is locked for that address.", SettingType.Duration, SettingScope.Live)
            { Default = "12:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Throttle:MaxFailuresPerAddress", SignIn, "Failures from one address", "Failed sign-ins from one address, for any accounts, before the address is blocked.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "50", Min = 10, Max = 10000, Optional = false },
        new("Throttle:AddressBan", SignIn, "Block an address for", "How long a blocked address stays blocked.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Throttle:Window", SignIn, "Counting window", "Failures older than this are forgotten.", SettingType.Duration, SettingScope.Live)
            { Default = "00:10:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },

        new("Ldap:Url", Directory, "Directory server", "ldap://host:389 or ldaps://host:636. Empty turns directory sign-in off; local accounts keep working.", SettingType.Url, SettingScope.Live)
            { Pattern = @"ldaps?://[^\s/]+(:\d+)?/?", PatternHelp = "ldap://host:389 or ldaps://host:636" },
        new("Ldap:StartTls", Directory, "Use StartTLS", "Upgrade a plain ldap:// connection to TLS before binding.", SettingType.Boolean, SettingScope.Live) { Default = "false" },
        new("Ldap:BindDn", Directory, "Service account", "A read-only account that can search people and groups, as a DN. Never an admin.", SettingType.Text, SettingScope.Live),
        new("Ldap:BindPassword", Directory, "Service account password", "From your directory team.", SettingType.Secret, SettingScope.Live),
        new("Ldap:UserBaseDn", Directory, "Where people are", "People are searched below this DN. Active Directory: an OU, or the domain root.", SettingType.Text, SettingScope.Live),
        new("Ldap:GroupBaseDn", Directory, "Where groups are", "Only for directories without memberOf (OpenLDAP without the overlay). Leave empty on Active Directory.", SettingType.Text, SettingScope.Live),
        new("Ldap:AdminGroup", Directory, "Admin group", "Members are admins here. A group name or its full DN.", SettingType.Text, SettingScope.Live),
        new("Ldap:RequiredGroup", Directory, "Required group", "Only members may sign in; people who leave it are disabled at the next check. Empty: everyone in the directory.", SettingType.Text, SettingScope.Live),
        new("Ldap:SyncInterval", Directory, "Check the directory every", "People who left are disabled and people who came back enabled.", SettingType.Duration, SettingScope.Live)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Ldap:IgnoreCertificateErrors", Directory, "Accept any certificate", "Testing only: anyone on the network path could read the service account's password.", SettingType.Boolean, SettingScope.Live)
            { Default = "false", Dangerous = true },

        new("Chat:MaxToolRounds", Chat, "Tool calls per answer", "How many rounds of tool use (Argus searches) one answer may take before it must answer.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "8", Min = 1, Max = 32, Optional = false },
        new("Chat:MaxUploadBytes", Chat, "Largest attachment", "Per file.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20971520", Unit = "bytes", Min = 1048576, Max = 104857600, Optional = false },
        new("Chat:MaxAttachmentChars", Chat, "Text kept per attachment", "Longer files are cut to this many characters and marked \"cut to fit\". The model reads what does not fit in the question in parts.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "1000000", Min = 1000, Max = 5000000, Optional = false },
        new("Chat:InlineAttachmentChars", Chat, "Text of an attachment in the question", "What of each attachment goes into the question itself. The model reads the rest in parts (the Reading files tool), so a long file does not fill the context.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "30000", Min = 2000, Max = 1000000, Optional = false },
        new("Web:AllowedSites", Tools, "Sites the chat may open", "Host names, comma separated: docs.python.org, *.microsoft.com (a domain and its subdomains), or * for any public site. Empty: the Web tool stays off. Addresses inside your network are never opened.", SettingType.Text, SettingScope.Live)
            { Default = "" },
        new("Web:SearchUrl", Tools, "Search engine", "A SearXNG instance for the Web tool's search. Empty: the websearch profile's own when it is on; otherwise no search, only opening pages.", SettingType.Url, SettingScope.Live),
        new("Sandbox:TimeoutSeconds", Tools, "Longest Python run", "A run still going after this long is stopped, and the model told so.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "60", Min = 5, Max = 300, Unit = "seconds", Optional = false },
        new("Chat:RequestTimeout", Chat, "Longest single answer", "An answer still running after this long is stopped.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 240, Optional = false },

        // --------------------------------------------------------------- stack (.env) --
        new("PRICE_INPUT_PER_MTOK", Credit, "Input, cache miss", "Per million prompt tokens the engine processed, in your credit's currency.", SettingType.Number, SettingScope.Stack)
            { Min = 0, Max = 1000, Optional = false, Impact = "The gateway restarts (a few seconds). Past usage keeps its old price." },
        new("PRICE_CACHED_INPUT_PER_MTOK", Credit, "Input, cache hit", "Per million prompt tokens served from the prefix cache.", SettingType.Number, SettingScope.Stack)
            { Min = 0, Max = 1000, Optional = false, Impact = "The gateway restarts (a few seconds)." },
        new("PRICE_OUTPUT_PER_MTOK", Credit, "Output", "Per million generated tokens, reasoning included.", SettingType.Number, SettingScope.Stack)
            { Min = 0, Max = 1000, Optional = false, Impact = "The gateway restarts (a few seconds)." },
        new("LITELLM_DEFAULT_USER_BUDGET", Credit, "Default credit", "Credit for a new person, per period. Each person's own credit is set under People.", SettingType.Number, SettingScope.Stack)
            { Min = 0, Max = 1000000, Impact = "The gateway and the app restart." },
        new("LITELLM_BUDGET_DURATION", Credit, "Credit period", "When credit renews: 30d, 1mo, 7d…", SettingType.Text, SettingScope.Stack)
            { Pattern = @"\d+(s|m|h|d|mo)", PatternHelp = "a number and s, m, h, d or mo, e.g. 1mo", Impact = "The gateway restarts." },

        new("MODEL_NAME", Model, "Model name", "The one name every client sees (gateway, chat, Qwen Code). Clients configured with the old name stop working.", SettingType.Text, SettingScope.Stack)
            { Optional = false, Pattern = @"[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}", PatternHelp = "letters, digits and . _ : / -", Dangerous = true, Impact = Engine1 },
        new("MODEL_CONTEXT", Model, "Context window", "Tokens, shared by all parallel slots.", SettingType.WholeNumber, SettingScope.Stack)
            { Min = 1024, Max = 4194304, Optional = false, Impact = Engine1 },
        new("MODEL_MAX_OUTPUT", Model, "Longest reply", "The largest completion the gateway advertises, in tokens.", SettingType.WholeNumber, SettingScope.Stack)
            { Min = 256, Max = 1048576, Optional = false, Impact = "The gateway restarts." },
        new("MODEL_REASONING_EFFORT", Model, "Default thinking", "How hard the model thinks when a chat does not choose.", SettingType.Choice, SettingScope.Stack)
            { Options = ["xhigh", "high", "medium", "low"], Impact = Engine1 },
        new("MODEL_ENABLE_THINKING", Model, "Thinking at all", "Off makes every answer skip thinking.", SettingType.Choice, SettingScope.Stack)
            { Options = ["true", "false"], Impact = Engine1 },
        new("THINKING_PRESETS", Model, "Thinking levels offered", "level:Label pairs, comma-separated. The chat offers them per conversation.", SettingType.Text, SettingScope.Stack)
            { Pattern = @"[a-z]+:[^,:]+(,[a-z]+:[^,:]+)*", PatternHelp = "e.g. xhigh:Deep think,low:Quick,off:No thinking", Impact = "The app and Open WebUI restart." },
        new("LLAMACPP_MODEL_FILE", Model, "Model file", "The GGUF file in the model directory (for a split model, the first part).", SettingType.Text, SettingScope.Stack)
            { Optional = false, Pattern = @"[^/\s]+\.gguf", PatternHelp = "a file name ending in .gguf", Dangerous = true, Impact = Engine1 },
        new("LLAMACPP_HF_REPO", Model, "Download from", "Hugging Face repository to download the model from when it is not on disk. Empty: never download.", SettingType.Text, SettingScope.Stack)
            { Pattern = @"[\w.-]+/[\w.-]+", PatternHelp = "owner/repository", Impact = "A missing model downloads before the engine starts (see docker logs model-init)." },
        new("LLAMACPP_HF_FILES", Model, "Files to download", "Space-separated paths in that repository; SHA-256 checked.", SettingType.Text, SettingScope.Stack),

        new("LLAMACPP_MODEL_DIR", Model, "Model directory", "The host folder with the GGUF files. Put it on NVMe: the weights are read on demand.", SettingType.Text, SettingScope.Stack)
            { Optional = false, Dangerous = true, Impact = Engine1 },
        new("LLAMACPP_LIBRARY_DIR", Model, "Model library", "The host folder Admin → Models adds models from, searched three levels deep for GGUF files. Usually the folder that holds the model directory. Empty: the model directory alone.", SettingType.Text, SettingScope.Stack)
            { Dangerous = true, Impact = Engine1 },
        new("LLAMACPP_MTP_HEAD", Model, "Draft head file", "A separate multi-token-prediction head in the model directory. Empty: the model's own.", SettingType.Text, SettingScope.Stack)
            { Pattern = @"[^/\s]+\.gguf", PatternHelp = "a file name ending in .gguf", Impact = Engine1 },
        new("LLAMACPP_MTP_ARGS", Engine, "Draft flags", "Extra llama-server flags for the draft.", SettingType.Text, SettingScope.Stack) { Dangerous = true, Impact = Engine1 },
        new("LLAMACPP_ENGINE_URL", Engine, "Engine build", "A llama.cpp release tarball to run instead of the stock server. Empty: the stock image.", SettingType.Url, SettingScope.Stack)
            { Pattern = @"https://\S+", PatternHelp = "an https:// address", Dangerous = true, Impact = Engine1 },
        new("LLAMACPP_ENGINE_SHA256", Engine, "Engine build checksum", "SHA-256 of that tarball; the engine refuses a download that does not match.", SettingType.Text, SettingScope.Stack)
            { Pattern = "[0-9a-f]{64}", PatternHelp = "64 hexadecimal characters", Impact = Engine1 },
        new("ENGINE_API_BASE", Engine, "Gateway's backend", "Where the gateway sends requests: http://llamacpp:8080/v1, or http://vllm:8000/v1 for vLLM.", SettingType.Url, SettingScope.Stack)
            { Optional = false, Pattern = @"https?://\S+", PatternHelp = "http(s)://…", Dangerous = true, Impact = "The gateway restarts." },
        new("LLAMACPP_N_GPU_LAYERS", Engine, "Layers on the GPU", "99 puts every layer on the GPU.", SettingType.WholeNumber, SettingScope.Stack) { Min = 0, Max = 999, Impact = Engine1 },
        new("LLAMACPP_N_CPU_MOE", Engine, "MoE layers in system RAM", "Layers whose experts stay in RAM. Raise it if loading runs out of GPU memory; 0 for dense models.", SettingType.WholeNumber, SettingScope.Stack)
            { Min = 0, Max = 999, Impact = Engine1 },
        new("LLAMACPP_KV_TYPE", Engine, "KV cache precision", "q8_0 halves the cache against f16 for little quality cost.", SettingType.Choice, SettingScope.Stack)
            { Options = ["f16", "bf16", "q8_0", "q5_1", "q5_0", "q4_1", "q4_0"], Impact = Engine1 },
        new("LLAMACPP_PARALLEL", Engine, "People served at once", "Parallel slots. More slots share the context window.", SettingType.WholeNumber, SettingScope.Stack) { Min = 1, Max = 64, Impact = Engine1 },
        new("LLAMACPP_MODELS_MAX", Engine, "Models loaded at once", "Loading one more unloads the least recently used. One GPU usually holds one large model.", SettingType.WholeNumber, SettingScope.Stack)
            { Default = "1", Min = 1, Max = 8, Optional = false, Impact = Engine1 },
        new("LLAMACPP_MTP_DRAFT_MAX", Engine, "Multi-token prediction", "Tokens drafted per step; 0 is off.", SettingType.WholeNumber, SettingScope.Stack) { Min = 0, Max = 16, Impact = Engine1 },
        new("LLAMACPP_EXTRA_ARGS", Engine, "Extra engine flags", "Anything else for llama-server. A wrong flag stops the engine from starting.", SettingType.Text, SettingScope.Stack)
            { Dangerous = true, Impact = Engine1 },
        new("LLAMACPP_THREADS", Engine, "CPU threads", "Physical cores. Hyperthreads measured slower, not faster.", SettingType.WholeNumber, SettingScope.Stack) { Min = 1, Max = 512, Impact = Engine1 },
        new("LLAMACPP_CPUS", Engine, "Engine CPU limit", "Keep it equal to the thread count.", SettingType.WholeNumber, SettingScope.Stack) { Min = 1, Max = 512, Impact = Engine1 },
        new("LLAMACPP_MEM_LIMIT", Engine, "Engine RAM limit", "e.g. 56g; 0 for none.", SettingType.Text, SettingScope.Stack)
            { Pattern = @"0|\d+[kmgKMG]", PatternHelp = "0, or a number and k, m or g", Impact = Engine1 },
        new("LLAMACPP_MLOCK", Engine, "Pin the weights in RAM", "auto pins them when they fit beside the reserve.", SettingType.Choice, SettingScope.Stack) { Options = ["auto", "on", "off"], Impact = Engine1 },
        new("LLAMACPP_PRELOAD", Engine, "Read the model in at start", "auto reads a model that fits in RAM before serving, at full disk speed.", SettingType.Choice, SettingScope.Stack) { Options = ["auto", "on", "off"], Impact = Engine1 },
        new("LLAMACPP_RAM_RESERVE_GB", Engine, "RAM kept for everything else", "In GB, when deciding whether the weights fit.", SettingType.WholeNumber, SettingScope.Stack) { Min = 0, Max = 4096, Impact = Engine1 },
        new("GPU_POWER_LIMIT_W", Engine, "GPU power cap", "Watts (nvidia-smi -pl). Empty: the card's default.", SettingType.WholeNumber, SettingScope.Stack) { Min = 50, Max = 2000 },
        new("CPU_POWER_LIMIT_W", Engine, "CPU power cap", "Watts (Intel RAPL). Many boards ship with no limit.", SettingType.WholeNumber, SettingScope.Stack) { Min = 15, Max = 1000 },
        new("HOST_SWAPPINESS", Engine, "Host swappiness", "vm.swappiness. Empty leaves the host's.", SettingType.WholeNumber, SettingScope.Stack) { Min = 0, Max = 200 },
        new("OLLAMA_CPUS", Engine, "Embedder CPU limit", "For the embed and argus profiles.", SettingType.WholeNumber, SettingScope.Stack) { Min = 1, Max = 64 },
        new("POSTGRES_CPUS", Engine, "Database CPU limit", "", SettingType.WholeNumber, SettingScope.Stack) { Min = 1, Max = 64, Impact = "The database restarts: everything pauses for a few seconds." },

        new("ARGUS_GITLAB_URL", Argus, "GitLab address", "https://gitlab.example.com", SettingType.Url, SettingScope.Stack)
            { Pattern = @"https?://\S+", PatternHelp = "http(s)://…", Impact = "Argus restarts and reindexes." },
        new("Chat:GitlabLinkUrl", Argus, "GitLab address for links", "Where people's browsers open GitLab, for the files and lines in Argus's answers. Empty: the GitLab address above. Set it when Argus reaches GitLab by an internal name.", SettingType.Url, SettingScope.Live)
            { Pattern = @"https?://\S+", PatternHelp = "http(s)://…" },
        new("ARGUS_GITLAB_TOKEN", Argus, "Read-only token", "read_api and read_repository, for an account that is at least Reporter in every project to index. No admin needed.", SettingType.Secret, SettingScope.Stack)
            { Impact = "Argus restarts." },
        new("ARGUS_GITLAB_AUTH", Argus, "Sign in with", "Empty infers it: a username wins over a token.", SettingType.Choice, SettingScope.Stack) { Options = ["token", "password"] },
        new("ARGUS_GITLAB_USERNAME", Argus, "Username", "Only when no token can be issued for the account.", SettingType.Text, SettingScope.Stack),
        new("ARGUS_GITLAB_PASSWORD", Argus, "Password", "Only with a username. Prefer a token: a password works everywhere that account signs in.", SettingType.Secret, SettingScope.Stack),
        new("ARGUS_GITLAB_CA_CERT", Argus, "GitLab CA certificate", "For a private CA: the PEM's path inside the container, e.g. /etc/argus/tls/gitlab-ca.pem.", SettingType.Text, SettingScope.Stack),
        new("ARGUS_GITLAB_VERIFY", Argus, "Verify GitLab's certificate", "false only as a last resort for a self-signed GitLab with no CA file.", SettingType.Choice, SettingScope.Stack)
            { Options = ["true", "false"], Dangerous = true },
        new("HF_TOKEN", Model, "Hugging Face token", "Only for gated repositories.", SettingType.Secret, SettingScope.Stack),

        new("IMAGEGEN_MODEL_NAME", Image, "Model name", "What the gateway and the chat call the picture model. It runs when the image profile is on (Deployment).", SettingType.Text, SettingScope.Stack)
            { Default = "FLUX.2-klein-4B", Optional = false, Pattern = @"[A-Za-z0-9._:-]{1,100}", PatternHelp = "letters, digits and . _ : -", Impact = "The gateway restarts (a few seconds)." },
        new("IMAGEGEN_MODEL_DIR", Image, "Model directory", "The host folder with the diffusion model, its text encoder and VAE.", SettingType.Text, SettingScope.Stack)
            { Dangerous = true, Impact = Image1 },
        new("IMAGEGEN_DIFFUSION_MODEL", Image, "Diffusion model file", "In the model directory, e.g. flux-2-klein-4b-Q4_0.gguf.", SettingType.Text, SettingScope.Stack)
            { Default = "flux-2-klein-4b-Q4_0.gguf", Optional = false, Impact = Image1 },
        new("IMAGEGEN_TEXT_ENCODER", Image, "Text encoder file", "The language model that reads the prompt, e.g. Qwen3-4B-Q4_K_M.gguf for FLUX.2 klein.", SettingType.Text, SettingScope.Stack)
            { Default = "Qwen3-4B-Q4_K_M.gguf", Optional = false, Impact = Image1 },
        new("IMAGEGEN_VAE", Image, "VAE file", "e.g. flux2-vae.safetensors.", SettingType.Text, SettingScope.Stack)
            { Default = "flux2-vae.safetensors", Optional = false, Impact = Image1 },
        new("IMAGEGEN_STEPS", Image, "Steps", "More is slower and, for distilled models like klein, rarely better. 4 for FLUX.2 klein.", SettingType.WholeNumber, SettingScope.Stack)
            { Default = "4", Min = 1, Max = 60, Optional = false, Impact = Image1 },
        new("IMAGEGEN_CFG_SCALE", Image, "Guidance (CFG) scale", "1.0 for distilled models like FLUX.2 klein.", SettingType.Number, SettingScope.Stack)
            { Default = "1.0", Min = 0, Max = 30, Optional = false, Impact = Image1 },
        new("IMAGEGEN_MAX_VRAM", Image, "GPU memory budget (GiB)", "What the image server may use of the GPU. Negative: leave that much free for the chat model; 0: all that is free.", SettingType.Number, SettingScope.Stack)
            { Default = "-1", Min = -24, Max = 192, Optional = false, Unit = "GiB", Impact = Image1 },

        new("SANDBOX_SLOTS", Tools, "Python runs at once", "More runs wait for a free slot. Each slot is its own user in the sandbox.", SettingType.WholeNumber, SettingScope.Stack)
            { Default = "2", Min = 1, Max = 8, Optional = false, Impact = Sandbox1 },
        new("SANDBOX_JOB_MEMORY_MB", Tools, "Memory per Python run", "A run that needs more fails with MemoryError.", SettingType.WholeNumber, SettingScope.Stack)
            { Default = "1536", Min = 256, Max = 65536, Unit = "MB", Optional = false, Impact = Sandbox1 },
        new("SANDBOX_MEMORY", Tools, "Memory for the whole sandbox", "All runs together, e.g. 3g. Less than the runs at once times the memory per run is usually enough: most runs use little.", SettingType.Text, SettingScope.Stack)
            { Default = "3g", Pattern = @"\d+(m|g)", PatternHelp = "a number and m or g", Optional = false, Impact = Sandbox1 },
        new("SANDBOX_CPUS", Tools, "CPUs for the sandbox", "All runs together; a run uses one.", SettingType.Number, SettingScope.Stack)
            { Default = "2", Min = 0.5m, Max = 64, Optional = false, Impact = Sandbox1 },

        new("COMPOSE_PROFILES", Deployment, "Parts that run", "gateway, proxy and auth are the core; llamacpp or vllm is the engine; argus adds the code index; image adds picture generation; sandbox runs the chat's Python; websearch adds a search engine for the chat; logging, tracing, smi, dcgm and cadvisor are observability.", SettingType.Choices, SettingScope.Stack)
            { Options = Profiles, Optional = false, Dangerous = true, Impact = "Services start or stop to match." },

        new("PROMETHEUS_RETENTION_TIME", Monitoring, "Keep metrics for", "e.g. 30d.", SettingType.Text, SettingScope.Stack)
            { Pattern = @"\d+(h|d|w|y)", PatternHelp = "a number and h, d, w or y", Impact = "Prometheus restarts." },
        new("PROMETHEUS_RETENTION_SIZE", Monitoring, "Metrics disk limit", "e.g. 20GB.", SettingType.Text, SettingScope.Stack)
            { Pattern = @"\d+(MB|GB|TB)", PatternHelp = "a number and MB, GB or TB", Impact = "Prometheus restarts." },

        new("BACKUP_DIR", Backup, "Backups go to", "A host path. Put it on a different disk from the data it protects.", SettingType.Text, SettingScope.Stack) { Optional = false },
        new("BACKUP_COPY_DIR", Backup, "Second copy", "A verified copy of every good backup, on another physical disk. Empty: none.", SettingType.Text, SettingScope.Stack),
        new("BACKUP_KEEP", Backup, "Backups kept", "Older ones are removed after a good backup.", SettingType.WholeNumber, SettingScope.Stack) { Min = 1, Max = 3650 },
        new("BACKUP_INCLUDE_LOGS", Backup, "Include logs and metrics", "1 also backs up Loki logs and Prometheus metrics.", SettingType.Choice, SettingScope.Stack) { Options = ["0", "1"] },
        new("BACKUP_TIME", Backup, "Daily at", "HH:MM, or any systemd OnCalendar value. Takes effect when the timer is installed again (sudo ./scripts/backup.sh --install-timer).", SettingType.Text, SettingScope.Stack)
            { Pattern = @"[0-9A-Za-z:*/ ,.-]{1,64}" },
    ];

    public static readonly IReadOnlyDictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static IEnumerable<SettingDefinition> StackSettings => All.Where(d => d.Scope == SettingScope.Stack);
}
