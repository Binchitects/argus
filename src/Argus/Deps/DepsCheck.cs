using System.Globalization;
using System.Text.Json.Nodes;
using Argus.Packs;
using Argus.Util;

namespace Argus.Deps;

/// <summary>
/// A repository's packages matched against the advisories packs with no model: each pinned version in a range an
/// advisory affects is a finding, with how the package comes in, the version that fixes it, and the smallest upgrade
/// that fixes all of its advisories. Also the repository's packages as a CycloneDX bill of materials.
/// </summary>
public static class DepsCheck
{
    static readonly Dictionary<string, int> SeverityRank = new(StringComparer.Ordinal) { ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["low"] = 3 };

    /// <summary>Each file's dependencies (a lock file's over its manifest's, file by file), deduplicated by ecosystem, name and version.</summary>
    public static List<Dependency> Dependencies(IEnumerable<(string Path, string Text)> files)
    {
        var all = files.SelectMany(f => Manifests.Read(f.Path, f.Text)).ToList();
        // What a lock file pins, the manifest beside it says less precisely: the lock's version wins, and a direct flag carries over.
        var directNames = all.Where(d => d.Direct).Select(d => (d.Ecosystem, Versions.PackageKey(d.Ecosystem, d.Name))).ToHashSet();
        return [.. all.GroupBy(d => (d.Ecosystem, Key: Versions.PackageKey(d.Ecosystem, d.Name), d.Version))
            .Select(g => g.OrderByDescending(d => d.Direct).First() with { Direct = directNames.Contains((g.Key.Ecosystem, g.Key.Key)) })
            .Where(d => d.Version is not null || !all.Any(o => o.Version is not null && o.Ecosystem == d.Ecosystem && Versions.PackageKey(o.Ecosystem, o.Name) == Versions.PackageKey(d.Ecosystem, d.Name)))
            .OrderBy(d => d.Ecosystem, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal)];
    }

    /// <summary>The findings: a dependency's version in a range an advisory affects, the known-exploited and the most severe first.</summary>
    public static JsonObject Check(IReadOnlyList<Dependency> dependencies, IReadOnlyList<Pack> packs)
    {
        var findings = new List<JsonObject>();
        var unread = 0;
        foreach (var d in dependencies.Where(d => d.Version is not null))
        {
            var rows = PackStore.Affected(packs, d.Ecosystem, d.Name);
            var hits = new List<Row>();
            foreach (var r in rows)
            {
                var inRange = Versions.InRange(d.Ecosystem, d.Version!, r.StrOrNull("introduced"), r.StrOrNull("fixed"), r.StrOrNull("last_affected"));
                if (inRange is null) unread++;
                else if (inRange.Value) hits.Add(r);
            }
            // The smallest upgrade that fixes every advisory hit: the highest of their fixes (none when one has no fix).
            var fixes = hits.Select(h => h.StrOrNull("fixed")).ToList();
            string? upgrade = fixes.Count > 0 && fixes.All(f => f is not null)
                ? fixes.Aggregate((a, b) => Versions.Compare(d.Ecosystem, a!, b!) is > 0 ? a : b) : null;
            foreach (var h in hits.GroupBy(h => h.Str("advisory")).Select(g => g.First()))
            {
                findings.Add(new JsonObject
                {
                    ["ecosystem"] = d.Ecosystem, ["package"] = d.Name, ["version"] = d.Version, ["direct"] = d.Direct, ["via"] = d.Via,
                    ["file"] = d.File, ["line"] = d.Line, ["advisory"] = h.Str("advisory"),
                    ["aliases"] = new JsonArray([.. (h.StrOrNull("aliases") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => (JsonNode)a)]),
                    ["severity"] = h.StrOrNull("severity") ?? "unknown", ["cwes"] = new JsonArray([.. (h.StrOrNull("cwes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => (JsonNode)a)]),
                    ["exploited"] = h.Long("exploited") == 1, ["summary"] = h.StrOrNull("summary"), ["fixed_in"] = h.StrOrNull("fixed"),
                    ["upgrade_to"] = upgrade, ["url"] = $"https://github.com/advisories/{h.Str("advisory")}", ["doc_path"] = h.StrOrNull("doc_path"),
                });
            }
        }
        var ordered = findings.OrderByDescending(f => f["exploited"]!.GetValue<bool>())
            .ThenBy(f => SeverityRank.GetValueOrDefault(f["severity"]!.ToString(), 4)).ThenBy(f => f["package"]!.ToString(), StringComparer.Ordinal).ToList();
        var advisories = packs.Where(p => p.HasAffected).ToList();
        return new JsonObject
        {
            ["dependencies"] = dependencies.Count,
            ["pinned"] = dependencies.Count(d => d.Version is not null),
            ["by_ecosystem"] = new JsonObject(dependencies.GroupBy(d => d.Ecosystem).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["findings"] = new JsonArray([.. ordered]),
            ["versions_not_compared"] = unread,
            ["advisories"] = advisories.Count == 0 ? null : new JsonArray([.. advisories.Select(p => (JsonNode)new JsonObject
            {
                ["pack"] = p.Name, ["version"] = p.Meta.GetValueOrDefault("pack_version"), ["source"] = p.Meta.GetValueOrDefault("source_commit"),
                ["as_of"] = AsOf(p)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["days_old"] = AsOf(p) is { } d ? (int)(DateTime.UtcNow.Date - d.ToDateTime(TimeOnly.MinValue)).TotalDays : null,
            })]),
            ["note"] = advisories.Count == 0 ? "No advisories pack is installed: load one (Admin -> Packs) for deps_check to match against." : null,
        };
    }

    /// <summary>The day an advisories pack's data is from: its KEV catalog's version (kev=2026.10.27 in its provenance).</summary>
    public static DateOnly? AsOf(Pack pack)
    {
        var source = pack.Meta.GetValueOrDefault("source_commit") ?? "";
        var at = source.IndexOf("kev=", StringComparison.Ordinal);
        return at >= 0 && DateOnly.TryParseExact(source[(at + 4)..].Split(',')[0], "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    static readonly Dictionary<string, string> PurlType = new(StringComparer.Ordinal)
    {
        ["npm"] = "npm", ["NuGet"] = "nuget", ["PyPI"] = "pypi", ["Maven"] = "maven", ["Go"] = "golang", ["crates.io"] = "cargo", ["RubyGems"] = "gem", ["Packagist"] = "composer",
    };

    /// <summary>The package URL (purl) of a dependency: pkg:npm/%40scope/name@1.2.3, pkg:maven/group/artifact@1.0.</summary>
    public static string Purl(Dependency d)
    {
        var type = PurlType.GetValueOrDefault(d.Ecosystem, "generic");
        var name = d.Ecosystem == "Maven" ? d.Name.Replace(':', '/') : string.Join('/', d.Name.Split('/').Select(Uri.EscapeDataString));
        return $"pkg:{type}/{name}" + (d.Version is null ? "" : "@" + Uri.EscapeDataString(d.Version));
    }

    /// <summary>A CycloneDX 1.5 bill of materials of the repository's packages at the commit read.</summary>
    public static JsonObject CycloneDx(string project, string sha, IReadOnlyList<Dependency> dependencies, DateTimeOffset now) => new()
    {
        ["bomFormat"] = "CycloneDX",
        ["specVersion"] = "1.5",
        ["serialNumber"] = $"urn:uuid:{Guid.NewGuid()}",
        ["version"] = 1,
        ["metadata"] = new JsonObject
        {
            ["timestamp"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["tools"] = new JsonObject { ["components"] = new JsonArray(new JsonObject { ["type"] = "application", ["name"] = "argus deps_check" }) },
            ["component"] = new JsonObject { ["type"] = "application", ["name"] = project, ["version"] = sha },
        },
        ["components"] = new JsonArray([.. dependencies.Where(d => d.Version is not null).Select(d => (JsonNode)new JsonObject
        {
            ["type"] = "library", ["name"] = d.Name, ["version"] = d.Version, ["purl"] = Purl(d), ["bom-ref"] = Purl(d),
            ["scope"] = d.Direct ? "required" : "optional",
            ["properties"] = new JsonArray(new JsonObject { ["name"] = "argus:file", ["value"] = d.File }),
        })]),
    };
}
