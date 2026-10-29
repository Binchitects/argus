using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Safeguards;

/// <summary>A secret found in a text: what kind (never the secret itself), and where.</summary>
public sealed record SecretHit(string Kind, int Index, int Length);

/// <summary>
/// Secrets on the way in: private keys (PEM blocks), cloud and service tokens (AWS, Google, Azure, Alibaba, DigitalOcean,
/// GitHub, GitLab, Docker Hub, npm, PyPI, Hugging Face, Slack, Discord, Telegram, Twilio, SendGrid, Mailgun, Stripe, Square,
/// Shopify, Databricks, Grafana, HashiCorp Vault, Linear, Postman, Atlassian, sk- API keys, JSON web tokens), and passwords
/// in obvious places (password=..., a connection string, a URL's user:password@). Each is found by its shape; a placeholder
/// ($VAR, &lt;password&gt;, ***) or a name in code (config.password) is not a secret.
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
        ("Google API key", GoogleKey, 0),
        ("Stripe key", Stripe, 0),
        ("Hugging Face token", HuggingFace, 0),
        ("npm token", Npm, 0),
        ("JSON web token", Jwt, 0),
        ("Azure storage key", AzureKey, 1),
        ("Alibaba Cloud key", Alibaba, 0),
        ("DigitalOcean token", DigitalOcean, 0),
        ("Docker Hub token", DockerHub, 0),
        ("PyPI token", PyPi, 0),
        ("Discord token", Discord, 0),
        ("Telegram bot token", Telegram, 0),
        ("Twilio key", Twilio, 0),
        ("SendGrid key", SendGrid, 0),
        ("Mailgun key", Mailgun, 0),
        ("Square token", Square, 0),
        ("Shopify token", Shopify, 0),
        ("Databricks token", Databricks, 0),
        ("Grafana token", Grafana, 0),
        ("Vault token", Vault, 0),
        ("Linear key", Linear, 0),
        ("Postman key", Postman, 0),
        ("Atlassian token", Atlassian, 0),
        ("password", UrlPassword, 1),
    ];

    /// <summary>Every kind it finds, for a rule that looks for some of them.</summary>
    public static IEnumerable<string> AllKinds => Shapes.Select(s => s.Kind).Distinct();

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
                // Quoted, code that builds a string around a value (PASSWORD='" + bar + "') is not a password either.
                if (quoted is not null && Concatenated().IsMatch(quoted.Value))
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

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GoogleKey();

    [GeneratedRegex(@"\b(?:sk|rk)_(?:live|test)_[0-9A-Za-z]{24,}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Stripe();

    [GeneratedRegex(@"\bhf_[A-Za-z0-9]{30,}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HuggingFace();

    [GeneratedRegex(@"\bnpm_[A-Za-z0-9]{36}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Npm();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"AccountKey=([A-Za-z0-9+/]{40,}={0,2})", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AzureKey();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://[^\s/:@""']+:([^\s/@""']{3,})@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"\bLTAI[A-Za-z0-9]{12,20}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Alibaba();

    [GeneratedRegex(@"\bdo[opr]_v1_[a-f0-9]{64}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DigitalOcean();

    [GeneratedRegex(@"\bdckr_pat_[A-Za-z0-9_\-]{27,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DockerHub();

    [GeneratedRegex(@"\bpypi-AgEIcHlwaS5vcmc[A-Za-z0-9_\-]{50,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PyPi();

    [GeneratedRegex(@"\b[MNO][A-Za-z0-9_\-]{23,25}\.[A-Za-z0-9_\-]{6}\.[A-Za-z0-9_\-]{27,38}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Discord();

    [GeneratedRegex(@"\b\d{8,10}:AA[A-Za-z0-9_\-]{33}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Telegram();

    [GeneratedRegex(@"\b(?:SK|AC)[0-9a-f]{32}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Twilio();

    [GeneratedRegex(@"\bSG\.[A-Za-z0-9_\-]{22}\.[A-Za-z0-9_\-]{43}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SendGrid();

    [GeneratedRegex(@"\bkey-[0-9a-f]{32}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Mailgun();

    [GeneratedRegex(@"\bsq0(?:atp|csp)-[0-9A-Za-z_\-]{22,43}\b|\bEAAA[A-Za-z0-9_\-]{60}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Square();

    [GeneratedRegex(@"\bshp(?:at|ca|pa|ss)_[a-fA-F0-9]{32}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Shopify();

    [GeneratedRegex(@"\bdapi[a-f0-9]{32}(?:-\d)?\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Databricks();

    [GeneratedRegex(@"\bglsa_[A-Za-z0-9]{32}_[a-fA-F0-9]{8}\b|\bglc_[A-Za-z0-9+/]{32,}={0,2}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Grafana();

    [GeneratedRegex(@"\bhv[sbr]\.[A-Za-z0-9_\-]{24,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Vault();

    [GeneratedRegex(@"\blin_api_[A-Za-z0-9]{40}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Linear();

    [GeneratedRegex(@"\bPMAK-[a-f0-9]{24}-[a-f0-9]{34}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Postman();

    [GeneratedRegex(@"\bATATT3[A-Za-z0-9_\-=]{150,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Atlassian();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:password|passwd|pwd|pass|secret|client_secret|secret_key|api_?key|access_?token|auth_?token)[""']?\s*(?:=|:|=>)\s*(?:""(?<q1>[^""\r\n]{4,})""|'(?<q2>[^'\r\n]{4,})'|(?<bare>[^\s""';,&<>(){}\[\]]{4,})(?=[\s;,&]|$))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, 1000)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeName();

    /// <summary>A quoted value that opens or closes on the other quote and a concatenation (+, ., ||, &amp;): code, not a literal.</summary>
    [GeneratedRegex(@"^\s*[""']\s*(?:\+|\.|\|\||&)|(?:\+|\.|\|\||&)\s*[""']\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Concatenated();
}
