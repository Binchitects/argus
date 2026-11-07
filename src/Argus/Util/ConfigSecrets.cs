using System.Text.RegularExpressions;

namespace Argus.Util;

/// <summary>
/// The values of a configuration file (.env, .properties, appsettings*.json) that look like secrets, taken out before the
/// file is stored: the index keeps the file's keys, hosts and names (what the runtime links are read from) and never
/// its passwords, tokens or keys. A value is a secret when its key names one (PASSWORD, SECRET, TOKEN, API_KEY,
/// PRIVATE_KEY, CREDENTIALS, a connection string's password), or when it has a token's shape anywhere
/// (<see cref="SecretMask"/>). A placeholder ($VAR, ${VAR}, &lt;...&gt;, ***, empty) is kept.
/// </summary>
public static partial class ConfigSecrets
{
    /// <summary>Whether a file is a configuration file whose values are masked before it is stored.</summary>
    public static bool Applies(string path)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal) || name.EndsWith(".env", StringComparison.Ordinal)
               || name.EndsWith(".properties", StringComparison.Ordinal)
               || name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The file's text with every secret value replaced by "[removed: secret]" (and token shapes by their kind).</summary>
    public static string Mask(string path, string text)
    {
        if (string.IsNullOrEmpty(text) || !Applies(path)) return text;
        var masked = KeyValue().Replace(text, Removed);
        masked = JsonValue().Replace(masked, Removed);
        return SecretMask.Mask(masked);
    }

    static string Removed(Match m)
    {
        var value = m.Groups["value"];
        // A key about a secret that is not one (TOKEN_URL, PASSWORD_MIN_LENGTH, SECRET_NAME): its value is kept.
        if (Keep(value.Value) || NotASecret().IsMatch(m.Groups["key"].Value)) return m.Value;
        var at = value.Index - m.Index;
        return m.Value[..at] + "[removed: secret]" + m.Value[(at + value.Length)..];
    }

    [GeneratedRegex(@"(?:url|uri|host|hostname|endpoint|path|file|dir|name|header|type|ttl|expiry|expires|lifetime|length|size|enabled|required|issuer|audience|algorithm|scheme|policy|provider)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NotASecret();

    static bool Keep(string value)
    {
        var v = value.Trim().Trim('"', '\'');
        return v.Length == 0 || v[0] is '$' or '<' or '{' or '*' or '%' || v.StartsWith("[removed", StringComparison.Ordinal)
               || v.All(c => c is '*' or 'x' or 'X' or '.' or '-' or '_' or '#') || v is "true" or "false" or "null" or "changeme";
    }

    const string SecretKey = @"[A-Za-z0-9_.\-]*(?:pass(?:word|wd|phrase)?|pwd|secret|token|api[_.\-]?key|private[_.\-]?key|access[_.\-]?key|credentials?|client[_.\-]?secret|signing[_.\-]?key|encryption[_.\-]?key)[A-Za-z0-9_.\-]*";

    /// <summary>KEY=value or key: value at the start of a line (.env, .properties, export KEY=...).</summary>
    [GeneratedRegex(@"^(?:\s*export\s+)?\s*(?<key>" + SecretKey + @")\s*[=:]\s*(?<value>[^\r\n#]*?)\s*(?:#.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, 1000)]
    private static partial Regex KeyValue();

    /// <summary>"Key": "value" in JSON.</summary>
    [GeneratedRegex(@"""(?<key>" + SecretKey + @")""\s*:\s*""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex JsonValue();
}
