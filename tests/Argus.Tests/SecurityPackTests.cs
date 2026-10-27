using Argus.Packs;
using Argus.Packs.Sources;

namespace Argus.Tests;

/// <summary>
/// The security pack, from six pages as tools/security-texts.py writes them from MITRE's CWE and CAPEC, OWASP's ASVS
/// and cheat sheets, CodeQL's query help and Bandit (tests/fixtures/argus/packs/security).
/// </summary>
[Collection("process-state")]
public class SecurityPackTests
{
    static string Root => Path.Combine(AppContext.BaseDirectory, "packfixtures", "security");

    [Fact]
    public void Each_weakness_rule_and_requirement_is_a_symbol_on_its_page()
    {
        var docs = new SecurityTexts().IterDocs(Root).ToDictionary(d => d.Path);
        var cwe = docs["cwe/top25/CWE-89.md"];
        Assert.Equal("CWE-89: Improper Neutralization of Special Elements used in an SQL Command ('SQL Injection')", cwe.Title);
        Assert.Equal("https://cwe.mitre.org/data/definitions/89.html", cwe.Url);
        Assert.Contains("In the 2025 CWE Top 25", cwe.Body);
        Assert.Contains("Bad code (C#):", cwe.Body);
        Assert.Contains("## Potential mitigations", cwe.Body);
        Assert.EndsWith("https://cwe.mitre.org/data/definitions/89.html\n", cwe.Body);

        var symbols = new SecurityTexts().IterSymbols(Root).ToList();
        var requirement = symbols.Single(s => s.Name == "V6.2.1");
        Assert.Equal(("requirement", "OWASP ASVS 5.0", "asvs/V6/V6.2.md"), (requirement.Kind, requirement.Namespace, requirement.DocPath));
        Assert.StartsWith("ASVS 5.0 V6.2.1 (level 1): Verify that user set passwords are at least 8 characters", requirement.Signature);
        var query = symbols.Single(s => s.Name == "cs/sql-injection");
        Assert.Equal(("codeql query", "CodeQL C#"), (query.Kind, query.Namespace));
        Assert.EndsWith("(CWE-89)", query.Signature);
        Assert.Contains(symbols, s => s.Name == "B608" && s.Signature.Contains("CWE-89", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Name == "CAPEC-66" && s.Kind == "attack pattern");
    }

    [Fact]
    public void A_security_pack_builds_with_its_sources_versions_as_provenance_and_a_search_can_keep_to_one_source()
    {
        using var dir = new TempDir();
        var outPath = Path.Combine(dir.Path, "security.arguspack");
        PackBuilder.BuildPack(new SecurityTexts(), Root, outPath, "1", FakeEmbedder.Embed, log: TextWriter.Null);
        var packs = PackStore.OpenPacks([outPath]);
        try
        {
            var meta = packs[0].Meta;
            Assert.Equal("cwe=4.20,capec=3.9,cheatsheets=9d41c054686e,asvs=9ccce0e3cf47,codeql=fd2bfc8a3da9,bandit=48ef27bd1bbc", meta["source_commit"]);
            Assert.Equal("0", meta["unresolved_symbol_count"]);
            Assert.Contains("cwe-top25=cwe/top25/", meta["facets"]);
            Assert.Contains("codeql-csharp=codeql/csharp/", meta["facets"]);
            Assert.Contains("CC BY-SA 4.0", meta["attribution"]);

            Assert.Equal("https://cwe.mitre.org/data/definitions/89.html", PackStore.LookupSymbol(packs, "CWE-89").Single()["url"]!.GetValue<string>());
            Assert.Equal("asvs/V6/V6.2.md", PackStore.LookupSymbol(packs, "V6.2.1", "asvs").Single()["doc_path"]!.GetValue<string>());
            Assert.Empty(PackStore.LookupSymbol(packs, "CWE-89", "capec"));
            var text = PackStore.SearchText(packs, "prepared statement", "codeql-csharp");
            Assert.NotEmpty(text);
            Assert.All(text, r => Assert.StartsWith("codeql/csharp/", r["doc_path"]!.GetValue<string>()));
            var top25 = PackStore.SearchDocs(packs, FakeEmbedder.Vector("SQL injection"), "cwe-top25", limit: 5);
            Assert.All(top25, r => Assert.StartsWith("cwe/top25/", r["doc_path"]!.GetValue<string>()));
            Assert.Single(PackStore.SelectPacks(packs, "security"));
        }
        finally { PackStore.ClosePacks(packs); }
    }
}
