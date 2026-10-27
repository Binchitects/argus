using System.Text.Json;

namespace Argus.Packs.Sources;

/// <summary>
/// A pack whose work directory a tool of this repository wrote from sources it fetched: a Markdown page per entry, and
/// index.json listing each page (its path, title and link) with the symbols it defines (name, kind, namespace,
/// signature), and the sources' versions and commits as the provenance. The parsing of each source's own format (XML,
/// OSCAL, qhelp) stays in that tool; the pack only reads what it wrote.
/// </summary>
public abstract class IndexedTexts : ISource
{
    public abstract string Name { get; }
    public abstract string RepoUrl { get; }
    public string Branch => "";
    public string Subtree => "";
    public abstract string License { get; }
    public abstract string LicenseUrl { get; }
    public abstract string Attribution { get; }
    public virtual IReadOnlyList<(string Name, string Prefix)> Facets => [];

    /// <summary>The tool that writes the work directory, for the message when it has not.</summary>
    protected abstract string Writer { get; }

    sealed record SymbolEntry(string Name, string Kind, string Namespace, string Signature);

    sealed record Entry(string Path, string Title, string Url, List<SymbolEntry> Symbols);

    sealed record Index(string Provenance, List<Entry> Docs, List<AffectedRange>? Affected);

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    Index Load(string root)
    {
        var path = Path.Combine(root, "index.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"{path}: write it with {Writer} {root}");
        return JsonSerializer.Deserialize<Index>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty");
    }

    /// <summary>Not a checkout: the sources' versions and commits, as the tool recorded them.</summary>
    public string? Provenance(string root) => Load(root).Provenance is { Length: > 0 } p ? p : null;

    public IEnumerable<Doc> IterDocs(string root)
    {
        foreach (var e in Load(root).Docs)
        {
            yield return new Doc(e.Path, e.Title, e.Url, "md", Walk.ReadText(Path.Combine(root, e.Path)));
        }
    }

    /// <summary>An advisories pack's rows of what each advisory affects (index.json's "affected"); none for the others.</summary>
    public IEnumerable<AffectedRange> IterAffected(string root) => Load(root).Affected ?? [];

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        foreach (var e in Load(root).Docs)
        {
            foreach (var s in e.Symbols)
            {
                yield return new ApiSymbol(s.Name, s.Kind, s.Namespace, e.Path, "", s.Signature);
            }
        }
    }
}

/// <summary>
/// The governance frameworks whose texts may be shared, each clause in its own words: NIST SP 800-53 Rev. 5, SP 800-171
/// Rev. 3, the Cybersecurity Framework 2.0 and the AI Risk Management Framework with its Playbook (works of the United
/// States government), and the EU AI Act (whose reuse the EU authorises). Arena's chat cites them beside the company's own
/// controls (docs/compliance). <c>tools/grc-catalogs.py --texts</c> writes the work directory from the downloaded sources,
/// with the sources' checksums as the provenance. A clause's id is its symbol ("AC-2", "03.01.01", "GV.OC-01",
/// "GOVERN 1.1", "Art.5"), and each framework is a facet a search can be kept to.
/// </summary>
public sealed class GrcTexts : IndexedTexts
{
    public override string Name => "grc";
    public override string RepoUrl => "https://github.com/usnistgov/oscal-content, https://airc.nist.gov/, https://eur-lex.europa.eu/eli/reg/2024/1689/oj";
    public override string License => "LicenseRef-US-Government-Public-Domain AND LicenseRef-EU-Decision-2011-833";
    public override string LicenseUrl => "https://eur-lex.europa.eu/content/legal-notice/legal-notice.html";
    public override string Attribution =>
        "NIST SP 800-53 Rev. 5, NIST SP 800-171 Rev. 3, the NIST Cybersecurity Framework 2.0 and the NIST AI Risk Management Framework 1.0 with its Playbook: " +
        "works of the United States government, not subject to copyright in the United States (17 U.S.C. 105), from NIST's OSCAL content and AI Resource Center. " +
        "Regulation (EU) 2024/1689 (the Artificial Intelligence Act): © European Union, https://eur-lex.europa.eu, reused under Commission Decision 2011/833/EU; " +
        "only the text published in the Official Journal of the European Union is authentic.";
    protected override string Writer => "tools/grc-catalogs.py --from <the downloaded sources> --texts";

    /// <summary>lang="nist80053" (and the others) keeps a search to that framework.</summary>
    public override IReadOnlyList<(string Name, string Prefix)> Facets =>
        [.. new[] { "nist80053", "nist800171", "nistcsf", "airmf", "euaiact" }.Select(f => (f, f + "/"))];
}

/// <summary>
/// What is known of software weaknesses and how to find and fix them, for the security review and Fix it: MITRE's CWE and
/// CAPEC; OWASP's Cheat Sheet Series, ASVS 5.0, Web Security Testing Guide and Top 10s (web 2025 and 2021, API 2023, LLM
/// applications 2025); CodeQL's security queries' help (never its engine); and the analyzers' rules (.NET's security
/// rules, Bandit, gosec, eslint-plugin-security). <c>tools/fetch-security-sources.sh</c> fetches them and
/// <c>tools/security-texts.py</c> writes the work directory. Each entry's id is a symbol (CWE-89, CAPEC-66, V6.2.1,
/// WSTG-INJT-05, A05:2025, cs/sql-injection, CA2100, B608, G201, security/detect-object-injection), each source a facet,
/// and CodeQL's queries a facet per language.
/// </summary>
public sealed class SecurityTexts : IndexedTexts
{
    public override string Name => "security";
    public override string RepoUrl => "https://cwe.mitre.org/, https://capec.mitre.org/, https://github.com/OWASP, https://github.com/github/codeql";
    public override string License => "LicenseRef-MITRE-CWE-CAPEC-Terms AND CC-BY-SA-4.0 AND MIT AND CC-BY-4.0 AND Apache-2.0";
    public override string LicenseUrl => "https://cwe.mitre.org/about/termsofuse.html";
    public override string Attribution =>
        "CWE and CAPEC: Copyright © The MITRE Corporation, used under MITRE's terms of use (cwe.mitre.org/about/termsofuse.html, capec.mitre.org/about/termsofuse.html). " +
        "The OWASP Cheat Sheet Series, Application Security Verification Standard 5.0, Web Security Testing Guide, Top 10 (2025, 2021), API Security Top 10 2023 " +
        "and Top 10 for LLM Applications 2025: © the OWASP Foundation and its contributors, under CC BY-SA 4.0; this pack's pages of them are shared alike. " +
        "CodeQL's query help: © GitHub, Inc., MIT. .NET's code analysis documentation: © Microsoft, CC BY 4.0. Bandit: © the PyCQA, Apache-2.0. " +
        "gosec: © the securego authors, Apache-2.0. eslint-plugin-security: © the eslint-community authors, Apache-2.0. Each page names its source.";
    protected override string Writer => "tools/security-texts.py --from .packwork/docsrc/security --out";

    static readonly string[] CodeQlLanguages = ["actions", "cpp", "csharp", "go", "java", "javascript", "python", "ruby", "rust", "swift"];

    /// <summary>lang="cwe", "cwe-top25", "asvs", "codeql-csharp" (and the others) keeps a search to that source.</summary>
    public override IReadOnlyList<(string Name, string Prefix)> Facets =>
    [
        ("cwe", "cwe/"), ("cwe-top25", "cwe/top25/"), ("capec", "capec/"), ("cheatsheets", "cheatsheets/"), ("asvs", "asvs/"), ("wstg", "wstg/"),
        ("top10", "top10/"), ("api-top10", "api-top10/"), ("llm-top10", "llm-top10/"), ("codeql", "codeql/"),
        .. CodeQlLanguages.Select(l => ($"codeql-{l}", $"codeql/{l}/")),
        ("dotnet-rules", "dotnet-rules/"), ("bandit", "bandit/"), ("gosec", "gosec/"), ("eslint-security", "eslint-security/"),
    ];
}

/// <summary>
/// Known vulnerabilities in packages, renewed each day: GitHub's reviewed security advisories (CC BY 4.0) for npm, NuGet,
/// PyPI, Maven, Go, crates.io, RubyGems, Packagist and the rest, each marked when CISA's Known Exploited Vulnerabilities
/// catalog lists its CVE. <c>argus pack renew</c> (Argus.Deps.Advisories) fetches them and writes the work directory: a
/// page per advisory (its GHSA id and CVEs as symbols, its ecosystem a facet) and the table of the
/// versions each affects and the versions that fix them, which deps_check matches a repository's locked versions against.
/// </summary>
public sealed class AdvisoriesTexts : IndexedTexts
{
    public override string Name => "advisories";
    public override string RepoUrl => "https://github.com/github/advisory-database, https://www.cisa.gov/known-exploited-vulnerabilities-catalog";
    public override string License => "CC-BY-4.0 AND LicenseRef-US-Government-Public-Domain";
    public override string LicenseUrl => "https://github.com/github/advisory-database/blob/main/LICENSE.md";
    public override string Attribution =>
        "The GitHub Advisory Database (github/advisory-database), © GitHub, Inc., under CC BY 4.0: GitHub-reviewed advisories in the OSV format. " +
        "CISA's Known Exploited Vulnerabilities catalog, a work of the United States government.";
    protected override string Writer => "argus pack renew (which fetches and writes it) instead of building";

    /// <summary>lang="npm", "nuget", "pypi", "maven", "go", "crates", "rubygems", "packagist" or "other" keeps a search to that ecosystem.</summary>
    public override IReadOnlyList<(string Name, string Prefix)> Facets =>
        [.. new[] { "npm", "nuget", "pypi", "maven", "go", "crates", "rubygems", "packagist", "other" }.Select(f => (f, f + "/"))];
}
