using System.Text.RegularExpressions;

namespace Llm.Api.Safeguards;

/// <summary>
/// Commands that do damage, in what the assistant sends to a tool (a terminal, Code Arena's shell, the sandbox) or writes
/// in an answer: wiping files or disks, a fork bomb, a script piped from the internet into a shell, a reverse shell,
/// dropping a database, turning security off, reading keys and passwords off the machine, making everything writable,
/// force-pushing over a main branch, and crypto miners. Each kind can be ticked on its own.
/// </summary>
public static partial class Commands
{
    private static readonly (string Kind, Func<Regex> Pattern)[] Shapes =
    [
        ("deleting everything", WipeFiles),
        ("wiping a disk", WipeDisk),
        ("a fork bomb", ForkBomb),
        ("running a script from the internet", PipeToShell),
        ("a reverse shell", ReverseShell),
        ("dropping a database", DropDatabase),
        ("turning security off", SecurityOff),
        ("reading keys and passwords", ReadSecrets),
        ("making everything writable", WorldWritable),
        ("force-pushing over a main branch", ForcePush),
        ("a crypto miner", Miner),
    ];

    public static IEnumerable<string> AllKinds => Shapes.Select(s => s.Kind);

    public static List<Finding> Find(string text, IReadOnlyCollection<string>? kinds = null)
    {
        var found = new List<Finding>();
        foreach (var (kind, pattern) in Shapes)
        {
            if (kinds is { Count: > 0 } && !kinds.Contains(kind))
            {
                continue;
            }
            try
            {
                found.AddRange(pattern().Matches(text).Select(m => new Finding(kind, m.Index, m.Length)));
            }
            catch (RegexMatchTimeoutException)
            {
                // As far as it was read.
            }
        }
        return Findings.Ordered(found);
    }

    [GeneratedRegex(@"\brm\s+(?:-[a-zA-Z]*[rR][a-zA-Z]*\s+(?:-[a-zA-Z-]+\s+)*|--recursive\s+(?:-[a-zA-Z-]+\s+)*)(?:--no-preserve-root\s+)?(?:/\*?|~/?\*?|\$HOME/?\*?|/(?:etc|usr|var|home|boot|root|bin|lib|opt)/?\*?|\*)(?=\s|$|;|&|\|)" +
        @"|\bdel\s+(?:/[sfqSFQ]\s+){2,}[a-zA-Z]:\\(?:\*)?|\bRemove-Item\b[^\n;|]{0,40}-Recurse\b[^\n;|]{0,40}(?:[A-Za-z]:\\\*?|~|\$HOME)(?=\s|$|;)" +
        @"|\bfind\s+/\s+[^\n;|]{0,40}-delete\b",
        RegexOptions.CultureInvariant, 500)]
    private static partial Regex WipeFiles();

    [GeneratedRegex(@"\b(?:mkfs(?:\.\w+)?|wipefs\s+-a|shred\s+[^\n;|]{0,20}/dev/)\s*[^\n;|]{0,30}/dev/\w+|\bdd\s+[^\n;|]{0,60}\bof=/dev/(?:sd|nvme|hd|vd|xvd|mmcblk|disk)\w*|\bformat\s+[a-zA-Z]:\s*(?:/[a-zA-Z]+\s*)*(?=$|\s|;)|>\s*/dev/(?:sd|nvme)\w*",
        RegexOptions.CultureInvariant, 500)]
    private static partial Regex WipeDisk();

    [GeneratedRegex(@":\s*\(\s*\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:|\bwhile\s*\(?\s*true\s*\)?\s*;?\s*do\s+\S+\s*&\s*done|%0\|%0", RegexOptions.CultureInvariant, 500)]
    private static partial Regex ForkBomb();

    [GeneratedRegex(@"\b(?:curl|wget|fetch)\b[^\n;]{0,200}\|\s*(?:sudo\s+)?(?:ba|z|da|k)?sh\b|\b(?:curl|wget)\b[^\n;]{0,200}\|\s*(?:sudo\s+)?python3?\b|\b(?:iex|Invoke-Expression)\s*\(?\s*(?:\(?\s*(?:iwr|irm|Invoke-WebRequest|Invoke-RestMethod|New-Object\s+Net\.WebClient))|\bbash\s+<\(\s*(?:curl|wget)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex PipeToShell();

    [GeneratedRegex(@"\b(?:ba)?sh\s+-i\s+>&\s*/dev/tcp/|/dev/tcp/[\w.-]+/\d+|\bnc(?:at)?\s+(?:-\w+\s+)*-e\s+/bin/(?:ba)?sh\b|\bsocat\b[^\n;]{0,60}\bexec:|\bmkfifo\b[^\n;]{0,40}\bnc\b|\bpython3?\s+-c\s+[""'][^""'\n]{0,200}socket[^""'\n]{0,200}(?:subprocess|pty\.spawn|os\.dup2)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex ReverseShell();

    [GeneratedRegex(@"\bDROP\s+(?:DATABASE|SCHEMA)\b|\bDROP\s+TABLE\b(?!\s+IF\s+EXISTS\s+(?:tmp|temp)_)|\bTRUNCATE\s+(?:TABLE\s+)?\w+|\bDELETE\s+FROM\s+\w+\s*(?:;|$)|\bdropdb\b|\bFLUSHALL\b|\bdb\.dropDatabase\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, 500)]
    private static partial Regex DropDatabase();

    [GeneratedRegex(@"\bsetenforce\s+0\b|\bufw\s+disable\b|\bsystemctl\s+(?:stop|disable|mask)\s+(?:firewalld|apparmor|auditd|ufw|selinux)\b|\biptables\s+-F\b|\bSet-MpPreference\b[^\n;]{0,40}-Disable\w+\s+\$?true|\bnetsh\s+advfirewall\s+set\s+\w+\s+state\s+off\b|\bchattr\s+-i\s+/etc/",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex SecurityOff();

    [GeneratedRegex(@"\b(?:cat|less|more|head|tail|cp|scp|base64|xxd|type)\s+[^\n;|]{0,40}(?:~|\$HOME|/home/\w+|/root)/\.(?:ssh/id_\w+|aws/credentials|docker/config\.json|kube/config|netrc|git-credentials|npmrc|pypirc)\b|\b(?:cat|less|head|tail|cp)\s+[^\n;|]{0,20}/etc/(?:shadow|gshadow|sudoers)\b|\bmimikatz\b|\bsekurlsa::",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex ReadSecrets();

    [GeneratedRegex(@"\bchmod\s+(?:-R\s+|--recursive\s+)(?:0?777|a\+rwx|ugo\+rwx)\s+(?:/|~|\$HOME|/(?:etc|usr|var|home))(?=\s|$|;)|\bchmod\s+(?:0?[4267]\d{3}|u\+s)\s+/bin/(?:ba)?sh\b",
        RegexOptions.CultureInvariant, 500)]
    private static partial Regex WorldWritable();

    [GeneratedRegex(@"\bgit\s+push\s+(?:[^\n;|]*\s)?(?:--force(?!-with-lease)|-f)\b[^\n;|]*\b(?:main|master|trunk|release)\b|\bgit\s+push\s+[^\n;|]*\+(?:main|master|trunk)\b",
        RegexOptions.CultureInvariant, 500)]
    private static partial Regex ForcePush();

    [GeneratedRegex(@"\b(?:xmrig|minerd|cpuminer|ethminer|nbminer|t-rex|lolminer)\b|stratum\+tcp://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex Miner();
}

/// <summary>
/// Data sent out through a link, the way an injected instruction makes an assistant leak what it read: a markdown or HTML
/// image (fetched as soon as it shows) to another site, or a link whose address carries data (a long encoded value in its
/// query or path). The rule's domains are the company's own: links to them are left alone.
/// </summary>
public static partial class Exfiltration
{
    public static List<Finding> Find(string text, IReadOnlyCollection<string> allowed)
    {
        var found = new List<Finding>();
        bool Ours(string host) => allowed.Any(d => Links.Covers(d, host));
        try
        {
            foreach (Match m in Image().Matches(text))
            {
                var host = m.Groups["host"].Value.TrimEnd('.').ToLowerInvariant();
                if (!Ours(host))
                {
                    found.Add(new Finding("an image from another site", m.Index, m.Length));
                }
            }
            foreach (Match m in Url().Matches(text))
            {
                var host = m.Groups["host"].Value.TrimEnd('.').ToLowerInvariant();
                if (!Ours(host) && Carries(m.Groups["rest"].Value))
                {
                    found.Add(new Finding("a link carrying data", m.Index, m.Length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // As far as it was read.
        }
        return Findings.Ordered(found);
    }

    /// <summary>A path or query with a long run that looks encoded (base64, hex, percent-encoded): more than an address needs.</summary>
    private static bool Carries(string rest) => Encoded().IsMatch(rest);

    [GeneratedRegex(@"!\[[^\]\n]{0,200}\]\(\s*(?:https?:)?//(?<host>[A-Za-z0-9.-]+)[^)\s]*\)|<img\b[^>]*\bsrc\s*=\s*[""']?(?:https?:)?//(?<host>[A-Za-z0-9.-]+)[^""'\s>]*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex Image();

    [GeneratedRegex(@"\bhttps?://(?<host>[A-Za-z0-9.-]+)(?::\d+)?(?<rest>[/?][^\s<>""')\]]*)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex Url();

    [GeneratedRegex(@"[A-Za-z0-9+/_=-]{64,}|(?:%[0-9A-Fa-f]{2}[^%&\s]{0,3}){16,}|[0-9a-fA-F]{48,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex Encoded();
}
