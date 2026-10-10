using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>Where Code Arena keeps its settings (the config folder) and its sessions (the data folder).</summary>
internal sealed class AppPaths(string configDir, string dataDir)
{
    public string ConfigDir { get; } = configDir;
    public string DataDir { get; } = dataDir;
    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string SessionsDir => Path.Combine(DataDir, "sessions");
    /// <summary>What was sent in each folder, a file per folder (the prompt's ↑).</summary>
    public string HistoryDir => Path.Combine(DataDir, "history");
    /// <summary>The person's own instructions for every project.</summary>
    public string UserInstructions => Path.Combine(ConfigDir, "ARENA.md");

    /// <summary>
    /// CODE_ARENA_HOME holds both when set. Otherwise: %APPDATA% and %LOCALAPPDATA%
    /// on Windows; $XDG_CONFIG_HOME and $XDG_DATA_HOME (~/.config, ~/.local/share) elsewhere.
    /// </summary>
    public static AppPaths From(Func<string, string?> env)
    {
        if (env("CODE_ARENA_HOME") is { Length: > 0 } home)
        {
            return new(Path.Combine(home, "config"), Path.Combine(home, "data"));
        }
        var user = env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var roaming = env("APPDATA") is { Length: > 0 } a ? a : Path.Combine(user, "AppData", "Roaming");
            var local = env("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(user, "AppData", "Local");
            return new(Path.Combine(roaming, "code-arena"), Path.Combine(local, "code-arena"));
        }
        var config = env("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(user, ".config");
        var data = env("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(user, ".local", "share");
        return new(Path.Combine(config, "code-arena"), Path.Combine(data, "code-arena"));
    }
}

/// <summary>An MCP server of the person's own: a command (stdio) or an address (streamable HTTP).</summary>
internal sealed class McpServerConfig
{
    public required string Name { get; init; }
    public string? Command { get; init; }
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
    public string? Cwd { get; init; }
    public string? Url { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    /// <summary>Its tools run without asking (in every mode but plan).</summary>
    public bool Trust { get; init; }
    public bool Disabled { get; init; }

    public static McpServerConfig? From(string name, JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }
        static Dictionary<string, string> Map(JsonNode? n) =>
            n is JsonObject m ? m.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString()) : [];
        return new McpServerConfig
        {
            Name = name,
            Command = o.Str("command"),
            Args = o["args"] is JsonArray a ? [.. a.Select(x => x?.ToString() ?? "")] : [],
            Env = Map(o["env"]),
            Cwd = o.Str("cwd"),
            Url = o.Str("url") ?? o.Str("httpUrl"),
            Headers = Map(o["headers"]),
            Trust = o.Bool("trust") ?? false,
            Disabled = o.Bool("disabled") ?? false,
        };
    }
}

/// <summary>
/// config.json: the Arena's address, the person's API key and their choices.
/// Unknown keys are kept as they are when it is saved.
/// </summary>
internal sealed class Config
{
    private JsonObject _raw = [];

    /// <summary>The Arena's address, https://DOMAIN.</summary>
    public string? Url { get; set; }
    /// <summary>The gateway, when it is not at https://gateway.DOMAIN.</summary>
    public string? Gateway { get; set; }
    /// <summary>Arena's MCP endpoint, when it is not at https://DOMAIN/mcp.</summary>
    public string? McpUrl { get; set; }
    /// <summary>Whether to use Arena's tools (its MCP endpoint) at all.</summary>
    public bool ArenaTools { get; set; } = true;
    /// <summary>Argus's MCP endpoint, when it is not at https://argus.DOMAIN/mcp.</summary>
    public string? ArgusUrl { get; set; }
    /// <summary>Whether to connect to Argus's own MCP endpoint (with the same key) besides Arena's.</summary>
    public bool ArgusTools { get; set; } = true;
    /// <summary>The share of the model's window (in percent) at which the session compacts itself; null: <see cref="Compaction.DefaultAt"/>.</summary>
    public int? CompactAt { get; set; }
    /// <summary>The share of the window (in percent) the recent part may keep whole when it compacts; null: <see cref="Compaction.DefaultTarget"/>.</summary>
    public int? CompactTarget { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>A CA certificate file (PEM) to trust besides the system's.</summary>
    public string? Ca { get; set; }
    public string? Model { get; set; }
    public string? Thinking { get; set; }
    public string? Mode { get; set; }
    /// <summary>The model's window in tokens, when the gateway does not say.</summary>
    public int? Context { get; set; }
    /// <summary>The shell run_shell uses (default: bash or sh; cmd.exe on Windows).</summary>
    public string? Shell { get; set; }
    /// <summary>The shell the IDE's terminals start (default: $SHELL, else bash or sh; PowerShell on Windows).</summary>
    public string? TerminalShell { get; set; }
    /// <summary>Directories the tools may use besides the working directory.</summary>
    public List<string> AllowedPaths { get; set; } = [];
    public List<McpServerConfig> McpServers { get; set; } = [];
    /// <summary>The sandbox for the agent's commands: "auto" (null: one when the system has it), "on" (refuse to start without), "off".</summary>
    public string? Sandbox { get; set; }
    /// <summary>Whether commands in the sandbox may use the network (on unless false).</summary>
    public bool SandboxNetwork { get; set; } = true;
    /// <summary>Folders commands in the sandbox may write besides the working directory and the build caches.</summary>
    public List<string> SandboxWritable { get; set; } = [];
    /// <summary>Kept rules for tool calls: run without asking, or never (Rules.cs).</summary>
    public List<string> Allow { get; set; } = [];
    public List<string> Deny { get; set; } = [];
    /// <summary>Sessions are kept in step with chats in Arena (both ways): on unless false.</summary>
    public bool SyncChats { get; set; } = true;
    /// <summary>Each turn's changes are committed under Code Arena's name (in a git repository): on unless false.</summary>
    public bool AutoCommit { get; set; } = true;
    /// <summary>The author name of Code Arena's commits (default: Code Arena).</summary>
    public string? CommitName { get; set; }
    /// <summary>The author email of Code Arena's commits (default: code-arena@ the Arena's host).</summary>
    public string? CommitEmail { get; set; }

    /// <summary>https://gateway.DOMAIN, or the gateway given; without a trailing /v1.</summary>
    public string? GatewayUrl => Gateway is { Length: > 0 } g ? TrimV1(g) : DeriveGateway(Url);

    /// <summary>https://DOMAIN/mcp, or the address given.</summary>
    public string? ArenaMcpUrl => McpUrl is { Length: > 0 } m ? m : Url is { Length: > 0 } u ? u.TrimEnd('/') + "/mcp" : null;

    /// <summary>https://argus.DOMAIN/mcp, or the address given; null for an Arena reached by an IP address or as localhost, which has no such name.</summary>
    public string? ArgusMcpUrl => ArgusUrl is { Length: > 0 } a ? a : DeriveArgus(Url);

    public static string? DeriveArgus(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.HostNameType != UriHostNameType.Dns
            || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new UriBuilder(u) { Host = "argus." + u.Host, Path = "/mcp" }.Uri.ToString();
    }

    /// <summary>Code Arena's page of the Arena's manual, https://DOMAIN/help/code-arena: the IDE's help links to it.</summary>
    public string? ManualUrl => Url is { Length: > 0 } u ? u.TrimEnd('/') + "/help/code-arena" : null;

    public static string? DeriveGateway(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return null;
        }
        var b = new UriBuilder(u) { Host = "gateway." + u.Host, Path = "" };
        return b.Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>"llm.example.com" → "https://llm.example.com"; no trailing slash.</summary>
    public static string NormalizeUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        return u.Contains("://", StringComparison.Ordinal) ? u : "https://" + u;
    }

    private static string TrimV1(string url)
    {
        var u = url.TrimEnd('/');
        return u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? u[..^3] : u;
    }

    public static Config Load(string file)
    {
        var config = new Config();
        if (!File.Exists(file))
        {
            return config;
        }
        var raw = Json.ParseObject(File.ReadAllText(file)) ?? throw new InvalidOperationException($"{file} is not valid JSON: fix or delete it.");
        config._raw = raw;
        config.Url = raw.Str("url");
        config.Gateway = raw.Str("gateway");
        config.McpUrl = raw.Str("mcpUrl");
        config.ArenaTools = raw.Bool("arenaTools") ?? true;
        config.ArgusUrl = raw.Str("argusUrl");
        config.ArgusTools = raw.Bool("argusTools") ?? true;
        config.CompactAt = Share(raw, "compactAt", file);
        config.CompactTarget = Share(raw, "compactTarget", file);
        config.ApiKey = raw.Str("apiKey");
        config.Ca = raw.Str("ca");
        config.Model = raw.Str("model");
        config.Thinking = raw.Str("thinking");
        config.Mode = raw.Str("mode");
        config.Context = raw.Int("context");
        config.Shell = raw.Str("shell");
        config.TerminalShell = raw.Str("terminalShell");
        config.AutoCommit = raw.Bool("autoCommit") ?? true;
        config.SyncChats = raw.Bool("syncChats") ?? true;
        config.Sandbox = raw.Str("sandbox");
        if (config.Sandbox is { } sandbox && sandbox.Trim().ToLowerInvariant() is not ("auto" or "on" or "off"))
        {
            throw new InvalidOperationException($"\"sandbox\" in {file} is \"auto\", \"on\" or \"off\".");
        }
        config.SandboxNetwork = raw.Bool("sandboxNetwork") ?? true;
        config.SandboxWritable = raw["sandboxWritable"] is JsonArray writable ? [.. writable.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0)] : [];
        var permissions = PermissionRules.From(raw["permissions"], null);
        config.Allow = permissions.Allow;
        config.Deny = permissions.Deny;
        config.CommitName = raw.Str("commitName");
        config.CommitEmail = raw.Str("commitEmail");
        config.AllowedPaths = raw["allowedPaths"] is JsonArray paths ? [.. paths.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0)] : [];
        config.McpServers = raw["mcpServers"] is JsonObject servers
            ? [.. servers.Select(s => McpServerConfig.From(s.Key, s.Value)).OfType<McpServerConfig>()]
            : [];
        return config;
    }

    /// <summary>
    /// A share of the model's window as the file gives it, written as the flags and /compact-at take
    /// it (70, "70%", 0.7); null when it is not there. Anything else is said, not passed over: it
    /// would leave the default in place, or be dropped from the file at the next save.
    /// </summary>
    private static int? Share(JsonObject raw, string key, string file)
    {
        if (raw[key] is not { } value)
        {
            return null;
        }
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        return Compaction.Percent(text) ?? throw new InvalidOperationException($"\"{key}\" in {file} is a share of the model's window: 70, \"70%\" or 0.7.");
    }

    /// <summary>An address Code Arena can reach: absolute, http or https (a port out of range, a space or another scheme is not).</summary>
    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && u.Host.Length > 0;

    /// <summary>
    /// The first of the addresses given (gateway, mcpUrl, argusUrl, from the file or the environment) that is not
    /// an http or https one, said with where to fix it; null when they all are.
    /// </summary>
    public string? WrongAddress(string file)
    {
        foreach (var (value, key, variable) in new[] { (Gateway, "gateway", "ARENA_GATEWAY_URL"), (McpUrl, "mcpUrl", "ARENA_MCP_URL"), (ArgusUrl, "argusUrl", "ARENA_ARGUS_URL") })
        {
            if (value is { Length: > 0 } && !IsWebAddress(value))
            {
                return $"{value} is not an http or https address: fix \"{key}\" in {file} (or {variable}).";
            }
        }
        return null;
    }

    /// <summary>Overrides from the environment: ARENA_URL, ARENA_API_KEY, ARENA_GATEWAY_URL, ARENA_MCP_URL, ARENA_ARGUS_URL, ARENA_MODEL.</summary>
    public void ApplyEnvironment(Func<string, string?> env)
    {
        if (env("ARENA_ARGUS_URL") is { Length: > 0 } argus)
        {
            ArgusUrl = argus;
        }
        if (env("ARENA_URL") is { Length: > 0 } url)
        {
            Url = NormalizeUrl(url);
        }
        if (env("ARENA_API_KEY") is { Length: > 0 } key)
        {
            ApiKey = key;
        }
        if (env("ARENA_GATEWAY_URL") is { Length: > 0 } gateway)
        {
            Gateway = gateway;
        }
        if (env("ARENA_MCP_URL") is { Length: > 0 } mcp)
        {
            McpUrl = mcp;
        }
        if (env("ARENA_MODEL") is { Length: > 0 } model)
        {
            Model = model;
        }
    }

    /// <summary>Writes the file readable by its owner only (0600 in a 0700 folder), replacing it whole.</summary>
    public void Save(string file)
    {
        var raw = _raw.Clone();
        void Set(string key, JsonNode? value)
        {
            if (value is null)
            {
                raw.Remove(key);
            }
            else
            {
                raw[key] = value;
            }
        }
        Set("url", Url);
        Set("gateway", Gateway);
        Set("mcpUrl", McpUrl);
        Set("arenaTools", ArenaTools ? null : false);
        Set("argusUrl", ArgusUrl);
        Set("argusTools", ArgusTools ? null : false);
        Set("compactAt", CompactAt);
        Set("compactTarget", CompactTarget);
        Set("apiKey", ApiKey);
        Set("ca", Ca);
        Set("model", Model);
        Set("thinking", Thinking);
        Set("mode", Mode);
        Set("context", Context);
        Set("shell", Shell);
        Set("terminalShell", TerminalShell);
        Set("autoCommit", AutoCommit ? null : false);
        Set("syncChats", SyncChats ? null : false);
        Set("sandbox", Sandbox);
        Set("sandboxNetwork", SandboxNetwork ? null : false);
        Set("permissions", Allow.Count + Deny.Count == 0 ? null : new JsonObject
        {
            ["allow"] = new JsonArray([.. Allow.Select(r => (JsonNode)r)]),
            ["deny"] = new JsonArray([.. Deny.Select(r => (JsonNode)r)]),
        });
        Set("commitName", CommitName);
        Set("commitEmail", CommitEmail);
        PrivateFiles.WriteAllText(file, raw.ToJsonString(Json.Indented) + "\n");
        _raw = raw;
    }
}

/// <summary>Files only their owner may read: the config (it holds the key) and the sessions (they hold code).</summary>
internal static class PrivateFiles
{
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, OwnerDir);
        }
    }

    /// <summary>Written to a temporary file first, created 0600, then moved over the old one.</summary>
    public static void WriteAllText(string file, string text)
    {
        EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var tmp = file + ".tmp";
        File.Delete(tmp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFile;
        }
        using (var stream = new FileStream(tmp, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
        }
        File.Move(tmp, file, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, OwnerFile);
        }
    }

    /// <summary>Appends a line, creating the file 0600 when it is new.</summary>
    public static void AppendLine(string file, string line)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows() && !File.Exists(file))
        {
            options.UnixCreateMode = OwnerFile;
        }
        using var stream = new FileStream(file, options);
        using var writer = new StreamWriter(stream);
        writer.Write(line);
        writer.Write('\n');
    }
}
