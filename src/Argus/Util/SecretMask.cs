using System.Text;
using System.Text.RegularExpressions;

namespace Argus.Util;

/// <summary>
/// Secrets taken out of a text before it leaves Argus (a CI job's log): private keys, cloud and service
/// tokens (AWS, GitHub, GitLab, Slack, sk- API keys), and passwords in obvious places (password=..., a
/// URL's user:password@). The same shapes as the app's safeguards (src/Llm.Api/Safeguards/Secrets.cs);
/// a placeholder ($VAR, &lt;password&gt;, ***) is not a secret.
/// </summary>
public static partial class SecretMask
{
    static readonly (string Kind, Func<Regex> Pattern, int Group)[] Shapes =
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
        ("password", Assignment, 1),
    ];

    /// <summary>The text with each secret replaced by "[removed: kind]".</summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var hits = new List<(int Index, int Length, string Kind)>();
        foreach (var (kind, pattern, group) in Shapes)
        {
            try
            {
                foreach (Match m in pattern().Matches(text))
                {
                    var g = m.Groups[group];
                    if (g.Success && !Placeholder(g.Value)) hits.Add((g.Index, g.Length, kind));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A text that takes too long for one shape is not held up by it.
            }
        }
        if (hits.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var (index, length, kind) in hits.OrderBy(h => h.Index).ThenByDescending(h => h.Length))
        {
            if (index < at) continue;
            sb.Append(text, at, index - at).Append("[removed: ").Append(kind).Append(']');
            at = index + length;
        }
        return sb.Append(text, at, text.Length - at).ToString();
    }

    static bool Placeholder(string value)
    {
        var v = value.Trim();
        return v.Length == 0 || v[0] is '$' or '%' or '<' or '{' or '*' || v.StartsWith("[removed", StringComparison.Ordinal) ||
               v.All(c => c is '*' or 'x' or 'X' or '.' or '-' or '_' or '#');
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

    /// <summary>password=..., access_token: "..." and the like: in a log, any such value is taken out.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:password|passwd|pwd|secret|client_secret|secret_key|api_?key|access_?token|auth_?token|private_?token)[""']?\s*(?:=|:|=>)\s*[""']?([^\s""';,&<>(){}\[\]]{4,})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, 1000)]
    private static partial Regex Assignment();
}
