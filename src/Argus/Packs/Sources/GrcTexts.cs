using System.Text.Json;

namespace Argus.Packs.Sources;

/// <summary>
/// The governance frameworks whose texts may be shared, each clause in its own words: NIST SP 800-53 Rev. 5, SP 800-171
/// Rev. 3, the Cybersecurity Framework 2.0 and the AI Risk Management Framework with its Playbook (works of the United
/// States government), and the EU AI Act (whose reuse the EU authorises). Arena's chat cites them beside the company's own
/// controls (docs/compliance).
///
/// The work directory is what <c>tools/grc-catalogs.py --texts</c> writes from the downloaded sources: a page per clause,
/// and index.json listing each with its id, kind, framework and link, and each source's checksum. Not a checkout: those
/// checksums are the provenance. A clause's id is its symbol ("AC-2", "03.01.01", "GV.OC-01", "GOVERN 1.1", "Art.5"),
/// and each framework is a facet a search can be kept to.
/// </summary>
public sealed class GrcTexts : ISource
{
    public string Name => "grc";
    public string RepoUrl => "https://github.com/usnistgov/oscal-content, https://airc.nist.gov/, https://eur-lex.europa.eu/eli/reg/2024/1689/oj";
    public string Branch => "";
    public string Subtree => "";
    public string License => "LicenseRef-US-Government-Public-Domain AND LicenseRef-EU-Decision-2011-833";
    public string LicenseUrl => "https://eur-lex.europa.eu/content/legal-notice/legal-notice.html";
    public string Attribution =>
        "NIST SP 800-53 Rev. 5, NIST SP 800-171 Rev. 3, the NIST Cybersecurity Framework 2.0 and the NIST AI Risk Management Framework 1.0 with its Playbook: " +
        "works of the United States government, not subject to copyright in the United States (17 U.S.C. 105), from NIST's OSCAL content and AI Resource Center. " +
        "Regulation (EU) 2024/1689 (the Artificial Intelligence Act): © European Union, https://eur-lex.europa.eu, reused under Commission Decision 2011/833/EU; " +
        "only the text published in the Official Journal of the European Union is authentic.";

    /// <summary>Each framework by its key, and how its symbols are named.</summary>
    static readonly (string Key, string Title)[] Frameworks =
    [
        ("nist80053", "NIST SP 800-53 Rev. 5"), ("nist800171", "NIST SP 800-171 Rev. 3"), ("nistcsf", "NIST CSF 2.0"),
        ("airmf", "NIST AI RMF 1.0"), ("euaiact", "EU AI Act"),
    ];

    /// <summary>lang="nist80053" (and the others) keeps a search to that framework.</summary>
    public IReadOnlyList<(string Name, string Prefix)> Facets => [.. Frameworks.Select(f => (f.Key, f.Key + "/"))];

    sealed record Entry(string Path, string Id, string Title, string Kind, string Framework, string Url);

    sealed record SourceFile(string File, string Url, string Sha256);

    sealed record Index(Dictionary<string, List<SourceFile>> Sources, List<Entry> Docs);

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    static Index Load(string root)
    {
        var path = Path.Combine(root, "index.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"{path}: write it with tools/grc-catalogs.py --texts {root}");
        return JsonSerializer.Deserialize<Index>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty");
    }

    /// <summary>Each source file's first 12 hex digits of SHA-256, by framework: "nist80053=…+…,euaiact=…".</summary>
    public string? Provenance(string root)
    {
        var sources = Load(root).Sources;
        return sources.Count == 0 ? null : string.Join(",", Frameworks.Where(f => sources.ContainsKey(f.Key))
            .Select(f => $"{f.Key}={string.Join("+", sources[f.Key].Select(s => s.Sha256[..12]))}"));
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        foreach (var e in Load(root).Docs)
        {
            yield return new Doc(e.Path, $"{e.Id} {e.Title}", e.Url, "md", Walk.ReadText(Path.Combine(root, e.Path)));
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var titles = Frameworks.ToDictionary(f => f.Key, f => f.Title);
        foreach (var e in Load(root).Docs)
        {
            var framework = titles.GetValueOrDefault(e.Framework, e.Framework);
            yield return new ApiSymbol(e.Id, e.Kind, framework, e.Path, "", $"{framework} {e.Id}: {e.Title}");
        }
    }
}
