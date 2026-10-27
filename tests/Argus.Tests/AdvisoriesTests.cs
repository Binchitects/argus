using System.Net;
using System.Text.Json.Nodes;
using Argus.Deps;
using Argus.Indexing;
using Argus.Packs;

namespace Argus.Tests;

/// <summary>
/// The advisories pack renewed: OSV's ranges read as intervals, each advisory's page and symbols (marked known-exploited
/// when CISA lists its CVE), and a renewal from a database and a catalog built over the pack before it, so only what
/// changed is embedded again.
/// </summary>
[Collection("process-state")]
public class AdvisoriesTests
{
    static JsonObject Advisory(string ghsa, string package, string fixedIn, string severity = "HIGH", string cve = "CVE-2021-1", string summary = "Prototype  pollution") => new()
    {
        ["id"] = ghsa, ["aliases"] = new JsonArray(cve), ["summary"] = summary, ["published"] = "2021-02-15T00:00:00Z",
        ["severity"] = new JsonArray(new JsonObject { ["type"] = "CVSS_V3", ["score"] = "CVSS:3.1/AV:N" }),
        ["database_specific"] = new JsonObject { ["severity"] = severity, ["cwe_ids"] = new JsonArray("CWE-1321") },
        ["affected"] = new JsonArray(new JsonObject
        {
            ["package"] = new JsonObject { ["ecosystem"] = "npm", ["name"] = package },
            ["ranges"] = new JsonArray(new JsonObject { ["type"] = "ECOSYSTEM", ["events"] = new JsonArray(new JsonObject { ["introduced"] = "0" }, new JsonObject { ["fixed"] = fixedIn }) }),
        }),
        ["details"] = "Details.", ["references"] = new JsonArray(new JsonObject { ["url"] = "https://example.test/a" }),
    };

    [Fact]
    public void OSV_events_are_intervals_and_a_page_says_each_packages_fix_and_whether_it_is_exploited()
    {
        var affected = JsonNode.Parse("""
            {"ranges": [{"type": "ECOSYSTEM", "events": [{"introduced": "0"}, {"fixed": "1.2.0"}, {"introduced": "2.0.0"}, {"last_affected": "2.1.0"}, {"introduced": "3.0.0"}]}]}
            """)!.AsObject();
        Assert.Equal([new("0", "1.2.0", null), new("2.0.0", null, "2.1.0"), new("3.0.0", null, null)], Advisories.Ranges(affected));
        Assert.Equal([new Advisories.Interval("1.0", null, "1.0")], Advisories.Ranges(JsonNode.Parse("""{"versions": ["1.0"]}""")!.AsObject()));
        Assert.Equal(">= 2.0.0, <= 2.1.0", Advisories.Describe(new("2.0.0", null, "2.1.0")));
        Assert.Equal("< 1.2.0", Advisories.Describe(new("0", "1.2.0", null)));

        var d = Advisory("GHSA-x", "lodash", "4.17.21");
        // Two packages, each with its own fixes: the signature names each package's, not a mix of them.
        ((JsonArray)d["affected"]!).Add(JsonNode.Parse("""
            {"package": {"ecosystem": "npm", "name": "lodash-es"}, "ranges": [{"type": "ECOSYSTEM", "events": [{"introduced": "0"}, {"fixed": "4.17.21"}, {"introduced": "5.0.0"}, {"fixed": "5.0.3"}]}]}
            """));
        var kev = new Dictionary<string, JsonObject> { ["CVE-2021-1"] = JsonNode.Parse("""{"dateAdded": "2022-01-10", "knownRansomwareCampaignUse": "Known", "requiredAction": "Patch."}""")!.AsObject() };
        var (text, title, symbols) = Advisories.Page(d, kev);
        Assert.Equal("GHSA-x: Prototype pollution", title);
        Assert.Contains("**Known exploited** (CISA KEV, added 2022-01-10; ransomware use: Known). Patch.", text, StringComparison.Ordinal);
        Assert.Contains("| npm | `lodash` | < 4.17.21 | 4.17.21 |", text, StringComparison.Ordinal);
        Assert.Contains("| npm | `lodash-es` | >= 5.0.0, < 5.0.3 | 5.0.3 |", text, StringComparison.Ordinal);
        Assert.Equal(["GHSA-x", "CVE-2021-1"], symbols.Select(s => s!["name"]!.GetValue<string>()));
        Assert.Equal("GHSA-x: Prototype pollution (npm lodash fixed in 4.17.21; npm lodash-es fixed in 4.17.21, 5.0.3; high; KNOWN EXPLOITED)",
            symbols[0]!["signature"]!.GetValue<string>());
    }

    /// <summary>A database as GitHub keeps it (advisories/github-reviewed/YEAR/MONTH/GHSA/GHSA.json), committed.</summary>
    static void Commit(string repo, Dictionary<string, JsonObject?> advisories, string message)
    {
        foreach (var (ghsa, d) in advisories)
        {
            var path = Path.Combine(repo, "advisories", "github-reviewed", "2021", "02", ghsa, $"{ghsa}.json");
            if (d is null)
            {
                File.Delete(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, d.ToJsonString());
        }
        File.WriteAllText(Path.Combine(repo, "LICENSE.md"), "CC BY 4.0\n");
        Mirror.RunGit(repo, ["add", "-A"]);
        var r = Mirror.RunGit(repo, ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message]);
        Assert.Equal(0, r.Code);
    }

    sealed class Catalog(string version) : HttpMessageHandler
    {
        public string Version { get; set; } = version;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"catalogVersion": "{{Version}}", "vulnerabilities": [{"cveID": "CVE-2021-2", "dateAdded": "2022-01-10"}]}"""),
            });
    }

    [Fact]
    public void A_renewal_fetches_what_changed_and_embeds_only_the_advisories_that_did()
    {
        using var dir = new TempDir();
        var database = Path.Combine(dir.Path, "advisory-database");
        Directory.CreateDirectory(database);
        Mirror.RunGit(database, ["init", "-q", "-b", "main"]);
        Commit(database, new()
        {
            ["GHSA-aaaa"] = Advisory("GHSA-aaaa", "lodash", "4.17.21"),
            ["GHSA-bbbb"] = Advisory("GHSA-bbbb", "minimist", "1.2.6", "CRITICAL", "CVE-2021-2", "Prototype pollution in minimist"),
            ["GHSA-gone"] = new JsonObject { ["id"] = "GHSA-gone", ["withdrawn"] = "2022-01-01T00:00:00Z", ["summary"] = "x" },
        }, "one");
        var packs = Path.Combine(dir.Path, "packs");
        var data = Path.Combine(dir.Path, "data");
        var embedded = new List<string>();
        List<double[]> Embed(IReadOnlyList<string> texts)
        {
            embedded.AddRange(texts);
            return FakeEmbedder.Embed(texts);
        }
        var catalog = new Catalog("2026.10.27");
        var said = new List<string>();
        var built = Advisories.Renew(data, packs, said.Add, Embed, new HttpClient(catalog), new DateOnly(2026, 10, 27), "file://" + database);
        Assert.Equal(Path.Combine(packs, "advisories.arguspack"), built);
        Assert.Contains("2 advisories (1 withdrawn left out), 2 affected ranges", said);
        Assert.Contains(embedded, t => t.Contains("minimist", StringComparison.Ordinal));

        var opened = PackStore.OpenPacks([built]);
        try
        {
            var pack = opened[0];
            Assert.Equal(("2026.10.27", "2"), (pack.Meta["pack_version"], pack.Meta["affected_count"]));
            Assert.StartsWith("github=", pack.Meta["source_commit"], StringComparison.Ordinal);
            Assert.Equal(new DateOnly(2026, 10, 27), DepsCheck.AsOf(pack));
            var minimist = Assert.Single(PackStore.Affected(opened, "npm", "minimist"));
            Assert.Equal(1L, minimist.Long("exploited"));
        }
        finally { PackStore.ClosePacks(opened); }

        // A day on: one advisory changed, one new; only those two are embedded again.
        var changed = Advisory("GHSA-aaaa", "lodash", "4.17.22", summary: "Prototype pollution, fixed again");
        Commit(database, new() { ["GHSA-aaaa"] = changed, ["GHSA-cccc"] = Advisory("GHSA-cccc", "qs", "6.10.3", "MODERATE", "CVE-2022-3", "qs pollution") }, "two");
        catalog.Version = "2026.10.28";
        embedded.Clear();
        Advisories.Renew(data, packs, said.Add, Embed, new HttpClient(catalog), new DateOnly(2026, 10, 28), "file://" + database);
        Assert.DoesNotContain(embedded, t => t.Contains("minimist", StringComparison.Ordinal));
        Assert.Contains(embedded, t => t.Contains("fixed again", StringComparison.Ordinal));
        Assert.Contains(embedded, t => t.Contains("qs pollution", StringComparison.Ordinal));
        Assert.Contains(said, l => l.Contains("incremental: 1 unchanged, 2 rebuilt", StringComparison.Ordinal));
        opened = PackStore.OpenPacks([built]);
        try
        {
            Assert.Equal(("2026.10.28", "3"), (opened[0].Meta["pack_version"], opened[0].Meta["affected_count"]));
            Assert.Equal("4.17.22", Assert.Single(PackStore.Affected(opened, "npm", "lodash")).StrOrNull("fixed"));
        }
        finally { PackStore.ClosePacks(opened); }

        // An empty catalog is not taken: the renewal fails, saying so.
        File.Delete(Path.Combine(data, "advisories", "sources", "kev.json"));
        var empty = new HttpClient(new EmptyCatalog());
        Assert.Throws<IOException>(() => Advisories.Renew(data, packs, said.Add, Embed, empty, null, "file://" + database));
    }

    sealed class EmptyCatalog : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"catalogVersion": "x", "vulnerabilities": []}""") });
    }
}
