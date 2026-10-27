using Argus.Packs;
using Argus.Packs.Sources;

namespace Argus.Tests;

/// <summary>
/// The grc pack, from three clauses as tools/grc-catalogs.py --texts writes them from NIST's OSCAL and the AI Act
/// (tests/fixtures/argus/packs/grc).
/// </summary>
[Collection("process-state")]
public class GrcPackTests
{
    static string Root => Path.Combine(AppContext.BaseDirectory, "packfixtures", "grc");

    [Fact]
    public void Each_clause_is_a_page_in_its_own_words_linked_to_its_publication()
    {
        var docs = new GrcTexts().IterDocs(Root).ToDictionary(d => d.Path);
        var ac = docs["nist80053/ac/ac-2.1.md"];
        Assert.Equal("AC-2(1) Account Management: Automated System Account Management", ac.Title);
        Assert.Equal("https://doi.org/10.6028/NIST.SP.800-53r5", ac.Url);
        Assert.Contains("Support the management of system accounts using [Assignment: organization-defined automated mechanisms].", ac.Body);
        Assert.Contains("Baselines: moderate, high.", ac.Body);
        Assert.EndsWith("#art_50", docs["euaiact/art_50.md"].Url);
        Assert.Contains("informed that they are interacting with an AI system", docs["euaiact/art_50.md"].Body);

        var symbols = new GrcTexts().IterSymbols(Root).ToList();
        Assert.Equal(["AC-2(1)", "GV.OC-01", "Art.50"], symbols.Select(s => s.Name));
        var art = symbols.Single(s => s.Name == "Art.50");
        Assert.Equal(("article", "EU AI Act", "euaiact/art_50.md"), (art.Kind, art.Namespace, art.DocPath));
        Assert.StartsWith("EU AI Act Art.50: Transparency obligations", art.Signature);
    }

    [Fact]
    public void A_grc_pack_builds_with_its_sources_checksums_as_provenance_and_a_search_can_keep_to_one_framework()
    {
        using var dir = new TempDir();
        var outPath = Path.Combine(dir.Path, "grc.arguspack");
        PackBuilder.BuildPack(new GrcTexts(), Root, outPath, "1", FakeEmbedder.Embed, log: TextWriter.Null);
        var packs = PackStore.OpenPacks([outPath]);
        try
        {
            var meta = packs[0].Meta;
            Assert.Equal("nist80053=64ca6b4161de+cfd4f5d9a192+e3a0f11237cf+d2ef340986b9+c0e3d54d9f47,nistcsf=0ac12904a4bc,euaiact=8f0b656302f9", meta["source_commit"]);
            Assert.Equal("0", meta["unresolved_symbol_count"]);
            Assert.Equal("nist80053=nist80053/;nist800171=nist800171/;nistcsf=nistcsf/;airmf=airmf/;euaiact=euaiact/", meta["facets"]);
            Assert.Contains("17 U.S.C. 105", meta["attribution"]);

            Assert.Equal("https://doi.org/10.6028/NIST.CSWP.29", PackStore.LookupSymbol(packs, "GV.OC-01").Single()["url"]!.GetValue<string>());
            Assert.Empty(PackStore.LookupSymbol(packs, "GV.OC-01", "euaiact"));
            var text = PackStore.SearchText(packs, "account", "nist80053");
            Assert.NotEmpty(text);
            Assert.All(text, r => Assert.StartsWith("nist80053/", r["doc_path"]!.GetValue<string>()));
            var semantic = PackStore.SearchDocs(packs, FakeEmbedder.Vector("telling people they talk to an AI"), "euaiact", limit: 5);
            Assert.All(semantic, r => Assert.StartsWith("euaiact/", r["doc_path"]!.GetValue<string>()));
            Assert.Single(PackStore.SelectPacks(packs, "grc"));
        }
        finally { PackStore.ClosePacks(packs); }
    }
}
