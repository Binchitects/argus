using System.Text.RegularExpressions;
using Argus.Util;

namespace Argus.Packs.Sources;

/// <summary>A repository of sample code, one pack document per source file (code_samples.py).</summary>
public class CodeRepo(string name, string repoUrl, string branch, string subtree, string baseUrl, string license,
    string licenseUrl, string attribution, string[]? suffixes = null, int sampleDepth = 1) : ISource
{
    public string Name { get; } = name;
    public string RepoUrl { get; } = repoUrl;
    public string Branch { get; } = branch;
    public string Subtree { get; } = subtree;
    public string BaseUrl { get; } = baseUrl;
    public string License { get; } = license;
    public string LicenseUrl { get; } = licenseUrl;
    public string Attribution { get; } = attribution;
    public string[] Suffixes { get; } = suffixes ?? DefaultSuffixes;
    public int SampleDepth { get; } = sampleDepth;

    public static readonly string[] DefaultSuffixes = [".c", ".cpp", ".cxx", ".cc", ".h", ".hpp", ".hxx"];
    public static readonly HashSet<string> SkipDirs = new(StringComparer.Ordinal)
    {
        ".git", ".github", "build", "obj", "bin", "x64", "x86", "arm64", "debug", "release", "packages",
        "node_modules", "generated", "__pycache__", "third_party", "external", "vendor",
    };
    public const long MaxFileBytes = 2_000_000;
    protected static readonly Regex Word = new(@"\A[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant);

    public static CodeRepo WindowsDriverSamples() => new(
        "wdk-samples", "https://github.com/microsoft/Windows-driver-samples", "main", "",
        "https://github.com/microsoft/Windows-driver-samples/blob/main/", "MS-PL",
        "https://github.com/microsoft/Windows-driver-samples/blob/main/LICENSE",
        "Windows driver samples. Copyright (c) Microsoft Corporation. Used under the Microsoft Public License (MS-PL).",
        sampleDepth: 2);

    public static CodeRepo WindowsClassicSamples() => new(
        "win32-samples", "https://github.com/microsoft/Windows-classic-samples", "main", "Samples",
        "https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/", "MIT",
        "https://github.com/microsoft/Windows-classic-samples/blob/main/LICENSE",
        "Windows classic samples. Copyright (c) Microsoft Corporation. Used under the MIT License.",
        sampleDepth: 1);

    protected string Root(string root) => Subtree.Length > 0 ? Path.Combine(root, Subtree) : root;

    protected IEnumerable<string> Files(string root)
    {
        var b = Root(root);
        foreach (var path in Walk.Files(b, n => Suffixes.Contains(Indexing.Filters.Suffix(n).ToLowerInvariant())))
        {
            var parts = Walk.Relative(b, path).Split('/');
            if (parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p.ToLowerInvariant()))) continue;
            yield return path;
        }
    }

    protected static string? Readable(string path)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            text = Walk.ReadText(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { return null; }
        if (PyStr.Count(text, '�') > PyStr.Len(text) / 50.0) return null;
        return text;
    }

    public string SampleOf(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length <= 1) return Name;
        return string.Join("/", parts.Take(SampleDepth));
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        var b = Root(root);
        foreach (var path in Files(root))
        {
            if (Readable(path) is not { } text) continue;
            var relative = Walk.Relative(b, path);
            var sample = SampleOf(relative);
            yield return new Doc(relative, $"{sample} - {Path.GetFileName(path)}", $"{BaseUrl}{relative}", "md",
                $"# {sample}\n\n## {relative}\n\n{text}");
        }
    }

    public virtual IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var b = Root(root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Files(root))
        {
            if (Readable(path) is null) continue;
            var relative = Walk.Relative(b, path);
            var sample = SampleOf(relative);
            var n = PyStr.AfterLast(sample, '/');
            if (n.Length == 0 || seen.Contains(n) || !Word.IsMatch(n)) continue;
            seen.Add(n);
            yield return new ApiSymbol(n, "sample", Name, relative, "", $"sample: {sample}");
        }
    }
}

/// <summary>TheAlgorithms/C-Plus-Plus: one symbol per algorithm file.</summary>
public sealed class AlgorithmsCpp() : CodeRepo(
    "algorithms", "https://github.com/TheAlgorithms/C-Plus-Plus", "master", "",
    "https://github.com/TheAlgorithms/C-Plus-Plus/blob/master/", "MIT",
    "https://github.com/TheAlgorithms/C-Plus-Plus/blob/master/LICENSE",
    "TheAlgorithms/C-Plus-Plus. Used under the MIT License.", [".cpp", ".h", ".hpp"], 1)
{
    public override IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var b = Root(root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Files(root))
        {
            if (Readable(path) is null) continue;
            var relative = Walk.Relative(b, path);
            var name = Walk.Stem(path);
            var topic = relative.Contains('/') ? relative.Split('/')[0] : Name;
            var key = $"{topic}/{name}";
            if (seen.Contains(key) || !Word.IsMatch(name)) continue;
            seen.Add(key);
            yield return new ApiSymbol(name, "algorithm", topic, relative, "", $"{topic}/{Path.GetFileName(path)}");
        }
    }
}

/// <summary>Command reference in markdown: PowerShell, Windows commands, tldr (scripting.py).</summary>
public sealed class CommandDocs(string name, string repoUrl, string branch, string subtree, string baseUrl, string licenseUrl,
    string attribution, string kind, string[]? onlyDirs = null) : ISource
{
    public string Name { get; } = name;
    public string RepoUrl { get; } = repoUrl;
    public string Branch { get; } = branch;
    public string Subtree { get; } = subtree;
    public string License => "CC-BY-4.0";
    public string LicenseUrl { get; } = licenseUrl;
    public string Attribution { get; } = attribution;

    const RegexOptions O = RegexOptions.CultureInvariant;
    static readonly Regex FrontMatterRe = new(@"\A---\r?\n(.*?)\r?\n---\r?\n?", RegexOptions.Singleline | O);
    static readonly Regex ScalarRe = new(@"^([A-Za-z_][\w.\- ]*)\s*:\s*(.*)$", O);
    static readonly Regex AtxRe = new(@"^#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Multiline | O);
    static readonly Regex TldrSummaryRe = new(@"^>\s*(.+?)\s*$", RegexOptions.Multiline | O);
    static readonly Regex SynopsisRe = new(@"^##\s+SYNOPSIS\s*$\s*\n+(.+?)\s*$", RegexOptions.Multiline | O);
    static readonly Regex DescriptionSectionRe = new(@"^##\s+Description\s*$\s*\n+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | O);
    static readonly Regex AdmonitionRe = new(@"^\[!\w+\]", O);
    static readonly HashSet<string> SkipNames = new(StringComparer.Ordinal)
        { "README.md", "TOC.md", "CONTRIBUTING.md", "LICENSE.md", "index.md", "CODE_OF_CONDUCT.md", "SECURITY.md" };

    public static CommandDocs PowerShell() => new("powershell", "https://github.com/MicrosoftDocs/PowerShell-Docs", "main", "reference",
        "https://github.com/MicrosoftDocs/PowerShell-Docs/blob/main/reference/",
        "https://github.com/MicrosoftDocs/PowerShell-Docs/blob/main/LICENSE.md",
        "PowerShell documentation. Copyright (c) Microsoft Corporation. Used under CC BY 4.0.", "cmdlet", ["5.1", "7.5"]);

    public static CommandDocs WindowsCommands() => new("windows-commands", "https://github.com/MicrosoftDocs/windowsserverdocs", "main",
        "WindowsServerDocs/administration/windows-commands",
        "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/",
        "https://github.com/MicrosoftDocs/windowsserverdocs/blob/main/LICENSE",
        "Windows Commands reference. Copyright (c) Microsoft Corporation. Used under CC BY 4.0.", "command");

    public static CommandDocs TldrPages() => new("tldr", "https://github.com/tldr-pages/tldr", "main", "pages",
        "https://github.com/tldr-pages/tldr/blob/main/pages/", "https://github.com/tldr-pages/tldr/blob/main/LICENSE.md",
        "tldr-pages. Used under CC BY 4.0.", "example");

    static (Dictionary<string, string>, string) FrontMatter(string text)
    {
        var m = FrontMatterRe.Match(text);
        if (!m.Success) return (new(StringComparer.Ordinal), text);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in PyStr.SplitLines(m.Groups[1].Value))
        {
            var found = ScalarRe.Match(line);
            if (found.Success)
                meta[PyStr.Strip(found.Groups[1].Value)] = PyStr.Strip(PyStr.Strip(PyStr.Strip(found.Groups[2].Value), "\""), "'");
        }
        return (meta, text.Substring(m.Index + m.Length));
    }

    string Root(string root) => Subtree.Length > 0 ? Path.Combine(root, Subtree) : root;

    IEnumerable<string> Files(string root)
    {
        var b = Root(root);
        foreach (var path in Walk.Files(b, n => n.EndsWith(".md", StringComparison.Ordinal) && !SkipNames.Contains(n)))
        {
            var parts = Walk.Relative(b, path).Split('/');
            if (parts.Contains(".git")) continue;
            if (onlyDirs is { Length: > 0 } && !onlyDirs.Contains(parts[0])) continue;
            yield return path;
        }
    }

    (string Title, string Summary, string Body) TitleAndBody(string path)
    {
        var (meta, body) = FrontMatter(Walk.ReadText(path));
        var title = meta.GetValueOrDefault("title", "");
        var summary = meta.GetValueOrDefault("description", "");
        if (title.Length == 0)
        {
            var heading = AtxRe.Match(body);
            title = heading.Success ? PyStr.Strip(PyStr.Strip(heading.Groups[1].Value), "`") : Walk.Stem(path);
        }
        if (summary.Length == 0 && SynopsisRe.Match(body) is { Success: true } synopsis) summary = PyStr.Strip(synopsis.Groups[1].Value);
        if (summary.Length == 0 && DescriptionSectionRe.Match(body) is { Success: true } section) summary = PyStr.Strip(section.Groups[1].Value);
        if (summary.Length == 0)
            foreach (Match quoted in TldrSummaryRe.Matches(body))
            {
                var candidate = PyStr.Strip(quoted.Groups[1].Value);
                if (!AdmonitionRe.IsMatch(candidate)) { summary = candidate; break; }
            }
        return (title, summary, body);
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        var b = Root(root);
        foreach (var path in Files(root))
        {
            var (meta, _) = FrontMatter(Walk.ReadText(path));
            var (title, summary, body) = TitleAndBody(path);
            var relative = Walk.Relative(b, path);
            var command = Walk.Stem(path);
            var header = $"# {command}\n\n{title}";
            if (summary.Length > 0) header += $"\n\n{summary}";
            var online = meta.GetValueOrDefault("online version", "");
            yield return new Doc(relative, title.Length > 0 ? title : command, online.Length > 0 ? online : baseUrl + relative, "md", $"{header}\n\n{body}");
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var b = Root(root);
        foreach (var path in Files(root))
        {
            var relative = Walk.Relative(b, path);
            var command = Walk.Stem(path);
            if (command.Length == 0) continue;
            var parts = relative.Split('/');
            var ns = parts.Length > 1 ? parts[0] : Name;
            var (_, summary, _) = TitleAndBody(path);
            yield return new ApiSymbol(command, kind, ns, relative, "", PyStr.Prefix(summary, 200));
        }
    }
}

/// <summary>Several checkouts published as one pack, each under its own path prefix (composite.py).</summary>
public sealed class Composite(string name, (string Checkout, ISource Source, string Prefix)[] parts, string repoUrl, string branch, string licenseUrl) : ISource
{
    public string Name { get; } = name;
    public string RepoUrl { get; } = repoUrl;
    public string Branch { get; } = branch;
    public string Subtree => "";
    public string License { get; } = string.Join(" AND ", parts.Select(p => p.Source.License).Distinct());
    public string LicenseUrl { get; } = licenseUrl;
    public string Attribution { get; } = string.Join(" ", parts.Select(p => p.Source.Attribution));

    public static Composite Win32WithSamples() => new("win32",
        [("sdk-api", MicrosoftApiRef.Win32Api(), "api"), ("Windows-classic-samples", CodeRepo.WindowsClassicSamples(), "samples")],
        "https://github.com/MicrosoftDocs/sdk-api", "docs", "https://github.com/MicrosoftDocs/sdk-api/blob/docs/LICENSE");

    public static Composite WdkWithSamples() => new("wdk",
        [("windows-driver-docs-ddi", MicrosoftApiRef.WdkDdi(), "ddi"), ("Windows-driver-samples", CodeRepo.WindowsDriverSamples(), "samples")],
        "https://github.com/MicrosoftDocs/windows-driver-docs-ddi", "staging",
        "https://github.com/MicrosoftDocs/windows-driver-docs-ddi/blob/staging/LICENSE");

    public static Composite ScriptingDocs() => new("scripting",
        [("PowerShell-Docs", CommandDocs.PowerShell(), "powershell"), ("windowsserverdocs", CommandDocs.WindowsCommands(), "cmd"), ("tldr", CommandDocs.TldrPages(), "tldr")],
        "https://github.com/MicrosoftDocs/PowerShell-Docs", "main", "https://github.com/MicrosoftDocs/PowerShell-Docs/blob/main/LICENSE.md");

    string Resolve(string root, (string Checkout, ISource Source, string Prefix) part)
    {
        var path = Path.Combine(root, part.Checkout);
        if (!Directory.Exists(path))
            throw new BuildError(
                $"{Name}: no checkout for '{part.Source.Name}' at {path}. Composite sources need every part cloned beneath --work-dir; they cannot be fetched with --fetch.");
        return path;
    }

    public IReadOnlyList<(string Name, string Checkout)>? PartCheckouts(string root) => parts.Select(p => (p.Source.Name, Resolve(root, p))).ToList();

    public IEnumerable<Doc> IterDocs(string root)
    {
        foreach (var part in parts)
        {
            var b = Resolve(root, part);
            foreach (var doc in part.Source.IterDocs(b)) yield return doc with { Path = $"{part.Prefix}/{doc.Path}" };
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        foreach (var part in parts)
        {
            var b = Resolve(root, part);
            foreach (var s in part.Source.IterSymbols(b)) yield return s with { DocPath = $"{part.Prefix}/{s.DocPath}" };
        }
    }
}

/// <summary>The System Design Primer, minus its translations (system_design.py).</summary>
public sealed class SystemDesignPrimer : ISource
{
    public string Name => "system-design";
    public string RepoUrl => "https://github.com/donnemartin/system-design-primer";
    public string Branch => "master";
    public string Subtree => "";
    public string License => "CC-BY-4.0";
    public string LicenseUrl => "https://github.com/donnemartin/system-design-primer/blob/master/LICENSE.txt";
    public string Attribution => "The System Design Primer. Copyright (c) Donne Martin. Used under CC BY 4.0.";
    const string BaseUrl = "https://github.com/donnemartin/system-design-primer/blob/master/";
    static readonly Regex TranslatedRe = new(@"-(?:[a-z]{2})(?:-[A-Za-z]{2,4})?$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> Skip = new(StringComparer.Ordinal) { "CONTRIBUTING.md", "TRANSLATIONS.md", "PULL_REQUEST_TEMPLATE.md", "CODE_OF_CONDUCT.md" };
    static readonly Regex AtxRe = new(@"^#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static bool IsTranslation(string name)
    {
        var stem = name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
        return TranslatedRe.IsMatch(stem);
    }

    IEnumerable<string> Files(string root) =>
        Walk.Files(root, n => n.EndsWith(".md", StringComparison.Ordinal))
            .Where(p => !Walk.Relative(root, p).Split('/').Contains(".git") && !Skip.Contains(Path.GetFileName(p)) && !IsTranslation(Path.GetFileName(p)));

    static string Title(string relative, string body)
    {
        var parts = relative.Split('/');
        if (parts[^1].ToLowerInvariant() == "readme.md" && parts.Length > 1) return parts[^2].Replace('_', ' ');
        var heading = AtxRe.Match(body);
        return heading.Success ? PyStr.Strip(heading.Groups[1].Value) : relative;
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        foreach (var path in Files(root))
        {
            var body = Walk.ReadText(path);
            var relative = Walk.Relative(root, path);
            yield return new Doc(relative, Title(relative, body), BaseUrl + relative, "md", body);
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        foreach (var path in Files(root))
        {
            var relative = Walk.Relative(root, path);
            var parts = relative.Split('/');
            if (parts[^1].ToLowerInvariant() != "readme.md" || parts.Length < 2) continue;
            var subject = parts[^2];
            yield return new ApiSymbol(subject, "case-study", Name, relative, "", $"system design case study: {subject.Replace('_', ' ')}");
        }
    }
}
