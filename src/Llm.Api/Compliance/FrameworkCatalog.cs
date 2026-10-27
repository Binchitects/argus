using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Llm.Api.Compliance;

/// <summary>One clause of a framework: its id, a short title, and the company's document that governs it.</summary>
public sealed record CatalogClause(string Id, string Title, string Document);

/// <summary>
/// A framework's clauses (docs/compliance/frameworks.yaml, or the catalog file it names, which tools/grc-catalogs.py
/// writes from the framework's published source), as tools/compliance-docs.py reads them too.
/// </summary>
public sealed class FrameworkCatalog(string key, string title, IReadOnlyList<CatalogClause> clauses)
{
    private readonly HashSet<string> _ids = [.. clauses.Select(c => c.Id)];

    public string Key { get; } = key;
    public string Title { get; } = title;
    public IReadOnlyList<CatalogClause> Clauses { get; } = clauses;

    /// <summary>The clause a cited one counts for: itself, or the nearest it is part of (164.308(a)(5)(ii)(C), AC-2(1)).</summary>
    public string? Entry(string clause)
    {
        while (!_ids.Contains(clause))
        {
            var cut = clause.EndsWith(')') ? clause.LastIndexOf('(') : -1;
            if (cut <= 0)
            {
                return null;
            }
            clause = clause[..cut];
        }
        return clause;
    }

    /// <summary>frameworks.yaml and the catalogs it names, read through <paramref name="read"/> (a path relative to docs/compliance).</summary>
    public static IReadOnlyDictionary<string, FrameworkCatalog> Read(Func<string, string> read)
    {
        var yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var file = yaml.Deserialize<FrameworksFile>(read("compliance-frameworks.yaml"));
        var all = new Dictionary<string, FrameworkCatalog>(StringComparer.Ordinal);
        foreach (var (key, fw) in file.Frameworks)
        {
            var entry = fw.Catalog is { } path ? yaml.Deserialize<FrameworkEntry>(read("compliance-" + path)) : fw;
            all[key] = new FrameworkCatalog(key, entry.Title ?? key, [.. entry.Clauses.Select(c => new CatalogClause(c[0], c[1], c[2]))]);
        }
        return all;
    }

    private sealed class FrameworksFile
    {
        public Dictionary<string, FrameworkEntry> Frameworks { get; set; } = [];
    }

    private sealed class FrameworkEntry
    {
        public string? Title { get; set; }
        public string? Catalog { get; set; }
        public List<List<string>> Clauses { get; set; } = [];
    }
}
