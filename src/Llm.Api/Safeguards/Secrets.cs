using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Safeguards;

/// <summary>A secret found in a text: what kind (never the secret itself), and where.</summary>
public sealed record SecretHit(string Kind, int Index, int Length);

/// <summary>
/// Secrets on the way in: private keys (PEM blocks), cloud and service tokens (AWS, GitHub,
/// GitLab, Slack, sk- API keys), and passwords in obvious places (password=..., a connection
/// string, a URL's user:password@). Each is found by its shape; a placeholder ($VAR, &lt;password&gt;,
/// ***) or a name in code (config.password) is not a secret.
/// </summary>
public static partial class Secrets
{
    private static readonly (string Kind, Func<Regex> Pattern, int Group)[] Shapes =
    [
        ("private key", PrivateKey, 0),
        ("AWS access key", AwsKeyId, 0),
        ("AWS secret key", AwsSecret, 1),
        ("GitHub token", GitHub, 0),
        ("GitLab token", GitLab, 0),
        ("Slack token", Slack, 0),
        ("Slack webhook", SlackHook, 0),
        ("API key", SkKey, 0),
        ("password", UrlPassword, 1),
    ];

    private static readonly HashSet<string> NotSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "none", "nil", "true", "false", "undefined", "required", "optional", "string", "password", "secret", "your_password",
        "yourpassword", "changeit", "example", "redacted", "hidden",
    };

    /// <summary>The secrets in a text, in order, none overlapping.</summary>
    public static IReadOnlyList<SecretHit> Find(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var hits = new List<SecretHit>();
        foreach (var (kind, pattern, group) in Shapes)
        {
            try
            {
                foreach (Match m in pattern().Matches(text))
                {
                    var g = m.Groups[group];
                    if (g.Success && !Placeholder(g.Value))
                    {
                        hits.Add(new SecretHit(kind, g.Index, g.Length));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A text that takes too long for one shape is not held up by it.
            }
        }
        try
        {
            foreach (Match m in Assignment().Matches(text))
            {
                var quoted = m.Groups["q1"].Success ? m.Groups["q1"] : m.Groups["q2"].Success ? m.Groups["q2"] : null;
                var value = quoted ?? m.Groups["bare"];
                if (!value.Success || Placeholder(value.Value))
                {
                    continue;
                }
                // Unquoted, a name in code (config.password, newPassword) is not a password: a literal has
                // something besides letters, or ends a connection string's part (Password=...;).
                if (quoted is null && (CodeName().IsMatch(value.Value) ||
                    (value.Value.All(char.IsLetter) && !(value.Index + value.Length < text.Length && text[value.Index + value.Length] == ';'))))
                {
                    continue;
                }
                hits.Add(new SecretHit("password", value.Index, value.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // As above.
        }
        var ordered = new List<SecretHit>();
        foreach (var hit in hits.OrderBy(h => h.Index).ThenByDescending(h => h.Length))
        {
            if (ordered.Count == 0 || hit.Index >= ordered[^1].Index + ordered[^1].Length)
            {
                ordered.Add(hit);
            }
        }
        return ordered;
    }

    /// <summary>The kinds found, each once, for a sentence or the audit log: "a private key and a GitHub token".</summary>
    public static string Kinds(IEnumerable<SecretHit> hits)
    {
        var kinds = hits.Select(h => h.Kind).Distinct().Select(k => (k[0] is 'A' or 'a' ? "an " : "a ") + k).ToList();
        return kinds.Count <= 1 ? kinds.FirstOrDefault() ?? "" : string.Join(", ", kinds[..^1]) + " and " + kinds[^1];
    }

    /// <summary>The text with each secret replaced by a marker that names its kind.</summary>
    public static string Mask(string text, IReadOnlyList<SecretHit> hits)
    {
        if (hits.Count == 0)
        {
            return text;
        }
        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var h in hits)
        {
            sb.Append(text, at, h.Index - at).Append("[removed: ").Append(h.Kind).Append(']');
            at = h.Index + h.Length;
        }
        return sb.Append(text, at, text.Length - at).ToString();
    }

    private static bool Placeholder(string value)
    {
        var v = value.Trim();
        return v.Length == 0 || v[0] is '$' or '%' or '<' or '{' or '*' || v.StartsWith("[removed", StringComparison.Ordinal) ||
            v.All(c => c is '*' or 'x' or 'X' or '.' or '-' or '_' or '#') || NotSecrets.Contains(v);
    }

    [GeneratedRegex(@"-----BEGIN (?<k>(?:RSA |DSA |EC |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY(?: BLOCK)?)-----[\s\S]*?(?:-----END \k<k>-----|\z)", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AwsKeyId();

    [GeneratedRegex(@"aws_?secret_?access_?key[""']?\s*[:=]\s*[""']?([A-Za-z0-9/+=]{40})(?![A-Za-z0-9/+=])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AwsSecret();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GitHub();

    [GeneratedRegex(@"\bgl(?:pat|dt|rt|ptt|ft|cbt|soat)-[A-Za-z0-9_\-]{20,}(?:\.[A-Za-z0-9_\-]+)*", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GitLab();

    [GeneratedRegex(@"\bxox[abposre]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Slack();

    [GeneratedRegex(@"https://hooks\.slack\.com/services/[A-Za-z0-9/_\-]{20,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SlackHook();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])sk-(?:proj-|svcacct-|admin-|ant-)?[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SkKey();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://[^\s/:@""']+:([^\s/@""']{3,})@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:password|passwd|pwd|pass|secret|client_secret|secret_key|api_?key|access_?token|auth_?token)[""']?\s*(?:=|:|=>)\s*(?:""(?<q1>[^""\r\n]{4,})""|'(?<q2>[^'\r\n]{4,})'|(?<bare>[^\s""';,&<>(){}\[\]]{4,})(?=[\s;,&]|$))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, 1000)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeName();
}
