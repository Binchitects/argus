using YamlDotNet.RepresentationModel;

namespace Argus.Configuration;

/// <summary>Raised when configuration is missing or malformed.</summary>
public sealed class ConfigError(string message) : Exception(message);

/// <summary>
/// How Argus authenticates to GitLab: an access token (<c>PRIVATE-TOKEN</c>) or a
/// username and password exchanged for a token through GitLab's sign-in form.
/// The password is read from the environment only, never from the config file,
/// and reaches exactly one request; clones always use the minted token.
/// </summary>
public sealed record GitLabConfig
{
    public string Url { get; init; } = "";
    public string Token { get; init; } = "";
    public string Auth { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string CaCert { get; init; } = "";
    public bool Verify { get; init; } = true;

    /// <summary>Validate and normalise, exactly as <c>GitLabConfig.__post_init__</c>.</summary>
    public static GitLabConfig Create(string url, string token = "", string auth = "", string username = "",
        string password = "", string caCert = "", bool verify = true)
    {
        var mode = (string.IsNullOrEmpty(auth) ? (string.IsNullOrEmpty(username) ? "token" : "password") : auth).ToLowerInvariant();
        if (mode is not ("token" or "password"))
            throw new ConfigError($"gitlab.auth must be 'token' or 'password', not {Util.PyStr.Repr(auth)}");
        if (mode == "token" && string.IsNullOrEmpty(token))
            throw new ConfigError("gitlab.token is required when gitlab.auth is 'token'");
        if (mode == "password" && (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)))
            throw new ConfigError(
                "gitlab.auth is 'password', so gitlab.username and a password " +
                "are both required (set the password in ARGUS_GITLAB_PASSWORD)");
        return new GitLabConfig
        {
            Url = url, Token = token, Auth = mode, Username = username, Password = password,
            CaCert = caCert, Verify = verify,
        };
    }

    /// <summary>A description safe to log: neither the token nor the password appears.</summary>
    public string Redacted()
    {
        var described = Auth == "password" ? $"{Url} as {Username} (password)" : $"{Url} (access token)";
        if (!Verify) described += ", certificate verification DISABLED";
        return described;
    }
}

public sealed record IndexConfig
{
    public static readonly string[] DefaultExcludeDirs =
        ["third_party", "vendor", "node_modules", "build", "out", "x64", "Debug", "Release"];

    public required string DataDir { get; init; }
    public required string DbPath { get; init; }
    public long MaxFileBytes { get; init; } = 1048576;
    public IReadOnlyList<string> ExcludeDirs { get; init; } = DefaultExcludeDirs;
    public long RepoTimeBudgetSeconds { get; init; } = 600;
    /// <summary>Glob patterns naming extra branches to index; the default branch is always indexed.</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    public string MirrorsDir => Path.Combine(DataDir, "mirrors");
    public string TreesDir => Path.Combine(DataDir, "trees");
}

public sealed record ArgusConfig
{
    public required GitLabConfig GitLab { get; init; }
    public required IndexConfig Index { get; init; }
    public string? PacksDirSetting { get; init; }
    /// <summary>The file this was loaded from, so a spawned indexer can be handed the same one.</summary>
    public string? SourcePath { get; init; }

    public string PacksDir => PacksDirSetting ?? Path.Combine(Index.DataDir, "packs");

    static readonly string[] TrueWords = ["1", "true", "yes", "on"];
    static readonly string[] FalseWords = ["0", "false", "no", "off"];

    /// <summary>Parse a YAML or environment boolean, refusing anything ambiguous.</summary>
    public static bool AsBool(string name, object? value)
    {
        if (value is bool b) return b;
        var text = (value?.ToString() ?? "").Trim().ToLowerInvariant();
        if (TrueWords.Contains(text)) return true;
        if (FalseWords.Contains(text)) return false;
        throw new ConfigError($"{name} must be a boolean (true or false), not {Describe(value)}");
    }

    static string Describe(object? value) => value switch
    {
        null => "None",
        string s => Util.PyStr.Repr(s),
        _ => value.ToString() ?? "",
    };

    static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static ArgusConfig Load(string path)
    {
        var text = File.ReadAllText(path);
        var root = ParseYaml(text);
        var gl = Section(root, "gitlab");
        var ix = Section(root, "index");
        var pk = Section(root, "packs");

        var url = Env("ARGUS_GITLAB_URL") ?? Scalar(gl, "url");
        if (string.IsNullOrEmpty(url))
            throw new ConfigError(
                "gitlab.url is required: set ARGUS_GITLAB_URL in the " +
                "environment or gitlab.url in the config file");

        var token = Env("ARGUS_GITLAB_TOKEN") ?? Scalar(gl, "token") ?? "";
        var username = Env("ARGUS_GITLAB_USERNAME") ?? Scalar(gl, "username") ?? "";
        var auth = (Env("ARGUS_GITLAB_AUTH") ?? Scalar(gl, "auth") ?? "").ToLowerInvariant();

        if (gl.ContainsKey("password"))
            throw new ConfigError(
                "gitlab.password must not appear in the config file. Set " +
                "ARGUS_GITLAB_PASSWORD in the environment instead.");
        var password = Env("ARGUS_GITLAB_PASSWORD") ?? "";

        var caCert = (Env("ARGUS_GITLAB_CA_CERT") ?? Scalar(gl, "ca_cert") ?? "").Trim();
        var verifyEnv = (Environment.GetEnvironmentVariable("ARGUS_GITLAB_VERIFY") ?? "").Trim();
        object verifyRaw = verifyEnv.Length > 0 ? verifyEnv : (gl.TryGetValue("verify", out var vv) ? YamlValue(vv) ?? true : true);
        var verify = AsBool("gitlab.verify", verifyRaw);
        if (caCert.Length > 0 && !verify)
            throw new ConfigError(
                "gitlab.ca_cert and gitlab.verify=false are both set, and they " +
                "contradict each other: a CA bundle is exactly what makes " +
                "verification possible. Keep gitlab.ca_cert, or drop it and " +
                "leave gitlab.verify=false.");
        if (caCert.Length > 0 && !File.Exists(caCert))
            throw new ConfigError(
                $"gitlab.ca_cert is {Util.PyStr.Repr(caCert)}, which is not a file. Point it " +
                "at a PEM bundle containing the CA that signed GitLab's " +
                "certificate, or set gitlab.verify=false if no such file " +
                "exists.");

        if (token.Length == 0 && username.Length == 0 && password.Length == 0)
            throw new ConfigError(
                "no GitLab credential: set gitlab.token (or ARGUS_GITLAB_TOKEN), " +
                "or gitlab.username with ARGUS_GITLAB_PASSWORD");

        foreach (var key in new[] { "data_dir", "db_path" })
            if (string.IsNullOrEmpty(Scalar(ix, key)))
                throw new ConfigError($"index.{key} is required");

        long maxFileBytes, budget;
        try
        {
            maxFileBytes = IntValue(ix, "max_file_bytes", 1048576);
            budget = IntValue(ix, "repo_time_budget_seconds", 600);
        }
        catch (FormatException exc)
        {
            throw new ConfigError($"index config value is not an integer: {exc.Message}");
        }

        var dataDir = Scalar(ix, "data_dir")!;
        var packsDir = Scalar(pk, "dir");
        return new ArgusConfig
        {
            GitLab = GitLabConfig.Create(url.TrimEnd('/'), token, auth, username, password, caCert, verify),
            Index = new IndexConfig
            {
                DataDir = dataDir,
                DbPath = Scalar(ix, "db_path")!,
                MaxFileBytes = maxFileBytes,
                ExcludeDirs = ix.TryGetValue("exclude_dirs", out var ex) ? StringList(ex) : IndexConfig.DefaultExcludeDirs,
                RepoTimeBudgetSeconds = budget,
                Branches = ix.TryGetValue("branches", out var br) ? StringList(br) : [],
            },
            PacksDirSetting = string.IsNullOrEmpty(packsDir) ? Path.Combine(dataDir, "packs") : packsDir,
            SourcePath = Path.GetFullPath(path),
        };
    }

    static long IntValue(Dictionary<string, YamlNode> section, string key, long fallback)
    {
        if (!section.TryGetValue(key, out var node)) return fallback;
        var v = YamlValue(node);
        if (v is null) throw new FormatException($"int() argument must be a string or a number, not 'NoneType'");
        if (v is long l) return l;
        if (v is string s && long.TryParse(s.Trim(), out var parsed)) return parsed;
        throw new FormatException($"invalid literal for int() with base 10: {Describe(v)}");
    }

    static IReadOnlyList<string> StringList(YamlNode node)
    {
        if (node is YamlSequenceNode seq)
            return seq.Children.Select(c => YamlValue(c)?.ToString() ?? "").ToList();
        var v = YamlValue(node);
        if (v is null) return [];
        return [v.ToString() ?? ""];
    }

    static YamlMappingNode? ParseYaml(string text)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(text);
        try { stream.Load(reader); }
        catch (YamlDotNet.Core.YamlException exc) { throw new ConfigError($"config file is not valid YAML: {exc.Message}"); }
        if (stream.Documents.Count == 0) return null;
        return stream.Documents[0].RootNode as YamlMappingNode;
    }

    static Dictionary<string, YamlNode> Section(YamlMappingNode? root, string name)
    {
        var result = new Dictionary<string, YamlNode>();
        if (root is null) return result;
        foreach (var (k, v) in root.Children)
        {
            if (k is YamlScalarNode ks && ks.Value == name && v is YamlMappingNode map)
            {
                foreach (var (ck, cv) in map.Children)
                    if (ck is YamlScalarNode cks && cks.Value is not null) result[cks.Value] = cv;
            }
        }
        return result;
    }

    static string? Scalar(Dictionary<string, YamlNode> section, string key)
    {
        if (!section.TryGetValue(key, out var node)) return null;
        var v = YamlValue(node);
        return v switch { null => null, bool b => b ? "True" : "False", _ => v.ToString() };
    }

    /// <summary>A YAML 1.1 scalar resolved the way PyYAML's safe_load resolves it.</summary>
    public static object? YamlValue(YamlNode node)
    {
        if (node is not YamlScalarNode scalar) return node.ToString();
        var raw = scalar.Value;
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted
            or YamlDotNet.Core.ScalarStyle.Literal or YamlDotNet.Core.ScalarStyle.Folded)
            return raw ?? "";
        if (raw is null || raw == "~" || raw == "null" || raw == "Null" || raw == "NULL" || raw == "") return null;
        switch (raw)
        {
            case "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON": return true;
            case "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF": return false;
        }
        if (long.TryParse(raw.Replace("_", ""), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
        return raw;
    }
}
