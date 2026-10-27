using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Packs;
using Argus.Packs.Sources;
using Argus.Util;

namespace Argus.Deps;

/// <summary>
/// The advisories pack, renewed: GitHub's reviewed advisories (a shallow, sparse clone of github/advisory-database,
/// fetched again each time, so only what changed comes down) and CISA's Known Exploited Vulnerabilities catalog, written
/// as the pack's pages and its table of what each advisory affects, then built over the pack installed, so only the
/// advisories that changed are embedded again. Each part is fetched aside and swapped in only when whole.
/// </summary>
public static class Advisories
{
    public const string Database = "https://github.com/github/advisory-database";
    public const string KevUrl = "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json";
    public const string PackName = "advisories";

    static readonly Dictionary<string, string> Facet = new(StringComparer.Ordinal)
    {
        ["npm"] = "npm", ["NuGet"] = "nuget", ["PyPI"] = "pypi", ["Maven"] = "maven", ["Go"] = "go", ["crates.io"] = "crates", ["RubyGems"] = "rubygems",
        ["Packagist"] = "packagist",
    };

    static readonly Dictionary<string, string> Severity = new(StringComparer.Ordinal)
    {
        ["LOW"] = "low", ["MODERATE"] = "medium", ["MEDIUM"] = "medium", ["HIGH"] = "high", ["CRITICAL"] = "critical",
    };

    /// <summary>An affected range: from introduced (null: from the first) up to fixed, or up to and with last affected.</summary>
    public sealed record Interval(string? Introduced, string? Fixed, string? LastAffected);

    /// <summary>OSV's events read in order as intervals; a version listed alone is its own.</summary>
    public static List<Interval> Ranges(JsonObject affected)
    {
        var found = new List<Interval>();
        foreach (var r in (affected["ranges"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string? introduced = null;
            foreach (var e in (r["events"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (e["introduced"]?.ToString() is { } i)
                {
                    if (introduced is not null) found.Add(new Interval(introduced, null, null));
                    introduced = i;
                }
                else if (e["fixed"]?.ToString() is { } f)
                {
                    found.Add(new Interval(introduced ?? "0", f, null));
                    introduced = null;
                }
                else if (e["last_affected"]?.ToString() is { } l)
                {
                    found.Add(new Interval(introduced ?? "0", null, l));
                    introduced = null;
                }
            }
            if (introduced is not null) found.Add(new Interval(introduced, null, null));
        }
        if (found.Count == 0)
            found.AddRange((affected["versions"] as JsonArray ?? []).Select(v => v?.ToString()).OfType<string>().Select(v => new Interval(v, null, v)));
        return found;
    }

    /// <summary>An interval in words: "&gt;= 2.0.0, &lt;= 2.1.0", "&lt; 1.2.0", or "every version".</summary>
    public static string Describe(Interval i)
    {
        var low = i.Introduced is null or "0" ? "" : $">= {i.Introduced}";
        var high = i.Fixed is not null ? $"< {i.Fixed}" : i.LastAffected is not null ? $"<= {i.LastAffected}" : "";
        var both = string.Join(", ", new[] { low, high }.Where(x => x.Length > 0));
        return both.Length > 0 ? both : "every version";
    }

    static string Squash(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string SeverityOf(JsonObject d) =>
        Severity.GetValueOrDefault((d["database_specific"]?["severity"]?.ToString() ?? "").ToUpperInvariant(), "unknown");

    static List<string> Strings(JsonNode? n) => [.. (n as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>()];

    /// <summary>An advisory's page, its title and its symbols (its GHSA id and each CVE), told whether CISA lists it as exploited.</summary>
    public static (string Text, string Title, JsonArray Symbols) Page(JsonObject d, IReadOnlyDictionary<string, JsonObject> kev)
    {
        var ghsa = d["id"]!.ToString();
        var aliases = Strings(d["aliases"]);
        var cves = aliases.Where(a => a.StartsWith("CVE-", StringComparison.Ordinal)).ToList();
        var severity = SeverityOf(d);
        var vectors = (d["severity"] as JsonArray ?? []).OfType<JsonObject>().Where(s => (s["type"]?.ToString() ?? "").StartsWith("CVSS", StringComparison.Ordinal))
            .Select(s => s["score"]?.ToString()).OfType<string>().ToList();
        var cwes = Strings(d["database_specific"]?["cwe_ids"]);
        var exploited = cves.Where(kev.ContainsKey).Select(c => kev[c]).ToList();
        var summary = Squash(d["summary"]?.ToString() is { Length: > 0 } s ? s : ghsa);
        var facts = new List<string> { cves.Count > 0 ? string.Join(", ", cves) : "no CVE", $"severity {severity}" + (vectors.Count > 0 ? $" ({vectors[0]})" : "") };
        if (cwes.Count > 0) facts.Add(string.Join(", ", cwes));
        facts.Add($"published {PyStr.Prefix(d["published"]?.ToString() ?? "", 10)}");
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"# {ghsa}: {summary}\n\n{string.Join(" · ", facts)}.\n");
        if (exploited.Count > 0)
        {
            var k = exploited[0];
            body.Append(CultureInfo.InvariantCulture,
                $"\n{$"**Known exploited** (CISA KEV, added {k["dateAdded"]}; ransomware use: {k["knownRansomwareCampaignUse"]?.ToString() ?? "Unknown"}). {k["requiredAction"]}".Trim()}\n");
        }
        var affected = (d["affected"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var rows = new List<string>();
        // Each package with the versions that fix it, in the advisory's own order: what the signature says.
        var packages = new List<(string Name, List<string> Fixes)>();
        foreach (var a in affected)
        {
            var ecosystem = a["package"]?["ecosystem"]?.ToString() ?? "";
            var name = a["package"]?["name"]?.ToString() ?? "";
            var intervals = Ranges(a);
            foreach (var i in intervals) rows.Add($"| {ecosystem} | `{name}` | {Describe(i)} | {i.Fixed ?? "none yet"} |");
            var label = $"{ecosystem} {name}";
            var fixes = intervals.Select(i => i.Fixed).OfType<string>().Distinct().ToList();
            if (packages.FirstOrDefault(p => p.Name == label) is { Name: not null } known) known.Fixes.AddRange(fixes.Except(known.Fixes));
            else packages.Add((label, fixes));
        }
        if (rows.Count > 0) body.Append("\n## Affected\n\n| Ecosystem | Package | Versions | Fixed in |\n| --- | --- | --- | --- |\n").Append(string.Join('\n', rows)).Append('\n');
        if (d["details"]?.ToString() is { Length: > 0 } details) body.Append("\n## Details\n\n").Append(details.Trim()).Append('\n');
        var refs = (d["references"] as JsonArray ?? []).OfType<JsonObject>().Select(r => r["url"]?.ToString()).OfType<string>().Take(12).ToList();
        if (refs.Count > 0) body.Append("\n## References\n\n").Append(string.Join('\n', refs.Select(u => $"- {u}"))).Append('\n');
        var named = string.Join("; ", packages.Take(3).Select(p => p.Fixes.Count > 0 ? $"{p.Name} fixed in {string.Join(", ", p.Fixes.Take(3))}" : $"{p.Name}, no fix yet"));
        var signature = $"{ghsa}: {summary} ({named}{(packages.Count > 3 ? "; …" : "")}; {severity}{(exploited.Count > 0 ? "; KNOWN EXPLOITED" : "")})";
        var symbols = new JsonArray(new JsonObject { ["name"] = ghsa, ["kind"] = "advisory", ["namespace"] = "GitHub Advisory Database", ["signature"] = PyStr.Prefix(signature, 300) });
        foreach (var c in cves)
            symbols.Add(new JsonObject { ["name"] = c, ["kind"] = "cve", ["namespace"] = "GitHub Advisory Database", ["signature"] = PyStr.Prefix($"{c} = {signature}", 300) });
        return (body.ToString(), $"{ghsa}: {summary}", symbols);
    }

    /// <summary>What a write made: the pages, the affected ranges, and the withdrawn advisories left out.</summary>
    public sealed record Written(int Docs, int Affected, int Withdrawn, string Provenance);

    /// <summary>
    /// The pack's work directory from the fetched sources: a page per advisory not withdrawn (under its first package's
    /// ecosystem), and index.json with the pages and a row per package and range for deps_check. Written aside and
    /// swapped in whole.
    /// </summary>
    public static Written Write(string sourceDir, string outDir)
    {
        var reviewed = Path.Combine(sourceDir, "github", "advisories", "github-reviewed");
        var kevPath = Path.Combine(sourceDir, "kev.json");
        if (!Directory.Exists(reviewed) || !File.Exists(kevPath))
            throw new IOException($"{sourceDir} has no github/advisories/github-reviewed or kev.json: fetch them first");
        var catalog = JsonNode.Parse(File.ReadAllText(kevPath))!.AsObject();
        var kev = (catalog["vulnerabilities"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(v => v["cveID"] is not null).GroupBy(v => v["cveID"]!.ToString()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var commit = PyStr.Strip(Mirror.RunGit(Path.Combine(sourceDir, "github"), ["rev-parse", "HEAD"]).Stdout);
        var aside = outDir.TrimEnd('/') + ".new";
        if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
        Directory.CreateDirectory(aside);
        var docs = new JsonArray();
        var affectedRows = new JsonArray();
        var withdrawn = 0;
        foreach (var file in Directory.EnumerateFiles(reviewed, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            JsonObject d;
            try
            {
                d = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            }
            catch (JsonException)
            {
                continue;
            }
            if (d["withdrawn"] is not null)
            {
                withdrawn++;
                continue;
            }
            var ghsa = d["id"]!.ToString();
            var affected = (d["affected"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var first = affected.Select(a => a["package"]?["ecosystem"]?.ToString()).FirstOrDefault() ?? "";
            var path = $"{Facet.GetValueOrDefault(first, "other")}/{ghsa}.md";
            var (text, title, symbols) = Page(d, kev);
            var target = Path.Combine(aside, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var url = $"https://github.com/advisories/{ghsa}";
            File.WriteAllText(target, text.TrimEnd() + $"\n\n---\nSource: the GitHub Advisory Database, CC BY 4.0. {url}\n");
            docs.Add(new JsonObject { ["path"] = path, ["title"] = title, ["url"] = url, ["symbols"] = symbols });
            var aliases = Strings(d["aliases"]);
            var exploited = aliases.Any(kev.ContainsKey);
            var severity = SeverityOf(d);
            var cwes = string.Join(',', Strings(d["database_specific"]?["cwe_ids"]));
            var summary = PyStr.Prefix(Squash(d["summary"]?.ToString()), 300);
            foreach (var a in affected)
            {
                foreach (var i in Ranges(a))
                {
                    affectedRows.Add(new JsonObject
                    {
                        ["ecosystem"] = a["package"]?["ecosystem"]?.ToString(), ["package"] = a["package"]?["name"]?.ToString(), ["introduced"] = i.Introduced,
                        ["fixed"] = i.Fixed, ["last_affected"] = i.LastAffected, ["advisory"] = ghsa, ["aliases"] = string.Join(',', aliases),
                        ["severity"] = severity, ["cwes"] = cwes, ["exploited"] = exploited, ["summary"] = summary, ["doc_path"] = path,
                    });
                }
            }
        }
        var provenance = $"github={PyStr.Prefix(commit, 12)},kev={catalog["catalogVersion"]}";
        File.WriteAllText(Path.Combine(aside, "index.json"),
            new JsonObject { ["provenance"] = provenance, ["docs"] = docs, ["affected"] = affectedRows }.ToJsonString() + "\n");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.Move(aside, outDir);
        return new Written(docs.Count, affectedRows.Count, withdrawn, provenance);
    }

    /// <summary>
    /// GitHub's reviewed advisories and the KEV catalog, into sourceDir: the clone made once (shallow, sparse) and fetched
    /// again after; the catalog downloaded aside and kept only when it reads. Returns the database's commit and the
    /// catalog's version.
    /// </summary>
    public static (string Commit, string Catalog) Fetch(string sourceDir, Action<string> say, HttpClient? http = null, string database = Database)
    {
        Directory.CreateDirectory(sourceDir);
        var clone = Path.Combine(sourceDir, "github");
        if (!Directory.Exists(Path.Combine(clone, ".git")))
        {
            var aside = Path.Combine(sourceDir, ".github.new");
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            say("cloning GitHub's advisory database (its reviewed advisories only)");
            Mirror.Git(sourceDir, ["clone", "--quiet", "--depth", "1", "--filter=blob:none", "--no-checkout", database, aside]);
            Mirror.Git(aside, ["sparse-checkout", "set", "--no-cone", "/advisories/github-reviewed/", "/LICENSE.md"]);
            Mirror.Git(aside, ["checkout", "--quiet"]);
            Directory.Move(aside, clone);
        }
        else
        {
            say("fetching what changed in GitHub's advisory database");
            Mirror.Git(clone, ["remote", "set-url", "origin", database]);
            Mirror.Git(clone, ["fetch", "--quiet", "--depth", "1", "origin", "main"]);
            Mirror.Git(clone, ["reset", "--quiet", "--hard", "FETCH_HEAD"]);
        }
        var commit = PyStr.Strip(Mirror.Git(clone, ["rev-parse", "HEAD"]));
        var part = Path.Combine(sourceDir, "kev.json.part");
        var client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            say("downloading CISA's known exploited vulnerabilities");
            File.WriteAllBytes(part, client.GetByteArrayAsync(new Uri(KevUrl)).GetAwaiter().GetResult());
        }
        finally
        {
            if (http is null) client.Dispose();
        }
        var catalog = JsonNode.Parse(File.ReadAllText(part)) as JsonObject;
        if (catalog?["vulnerabilities"] is not JsonArray { Count: > 0 } || catalog["catalogVersion"]?.ToString() is not { Length: > 0 } version)
        {
            File.Delete(part);
            throw new IOException("CISA's catalog came back empty or unreadable: the one before is kept");
        }
        File.Move(part, Path.Combine(sourceDir, "kev.json"), overwrite: true);
        return (commit, version);
    }

    /// <summary>
    /// The advisories pack renewed in place: fetched, written, and built over the pack installed (so only the advisories
    /// that changed are embedded again), as advisories.arguspack in packsDir. Its version is the day's.
    /// </summary>
    public static string Renew(string dataDir, string packsDir, Action<string> say, Func<IReadOnlyList<string>, List<double[]>>? embed = null,
        HttpClient? http = null, DateOnly? today = null, string database = Database)
    {
        var sourceDir = Path.Combine(dataDir, "advisories", "sources");
        var workDir = Path.Combine(dataDir, "advisories", "texts");
        var (commit, catalog) = Fetch(sourceDir, say, http, database);
        say($"github {PyStr.Prefix(commit, 12)}, KEV {catalog}");
        var written = Write(sourceDir, workDir);
        say($"{written.Docs} advisories ({written.Withdrawn} withdrawn left out), {written.Affected} affected ranges");
        var outPath = Path.Combine(packsDir, PackName + Registry.PackSuffix);
        var version = (today ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        var log = new StringWriter();
        PackBuilder.BuildPack(new AdvisoriesTexts(), workDir, outPath, version, embed, log: log);
        foreach (var line in log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) say(line.Trim());
        say($"advisories {version} built");
        return outPath;
    }
}
