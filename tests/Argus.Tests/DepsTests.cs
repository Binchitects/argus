using System.Text.Json;
using Argus.Deps;
using Argus.Packs;
using Argus.Packs.Sources;

namespace Argus.Tests;

/// <summary>deps_check's parts: versions ordered as each ecosystem orders them, the lock files and manifests read, and the matching.</summary>
[Collection("process-state")]
public class DepsTests
{
    [Theory]
    [InlineData("npm", "1.2.3", "1.10.0", -1)]
    [InlineData("npm", "1.0.0-alpha", "1.0.0", -1)]
    [InlineData("npm", "1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("npm", "1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("npm", "v2.0.0", "2.0.0+build.5", 0)]
    [InlineData("NuGet", "4.1.0.1", "4.1.0", 1)]
    [InlineData("Go", "v0.17.0", "v0.9.1", 1)]
    [InlineData("PyPI", "2.31.0", "2.31", 0)]
    [InlineData("PyPI", "1.0.dev1", "1.0a1", -1)]
    [InlineData("PyPI", "1.0rc1", "1.0", -1)]
    [InlineData("PyPI", "1.0.post1", "1.0", 1)]
    [InlineData("PyPI", "1!0.1", "2.0", 1)]
    [InlineData("Maven", "1.0-SNAPSHOT", "1.0", -1)]
    [InlineData("Maven", "1.0-beta-2", "1.0-rc1", -1)]
    [InlineData("Maven", "2.13.4.2", "2.13.4", 1)]
    [InlineData("Maven", "1.0.1", "1.0-sp", 1)]
    [InlineData("RubyGems", "1.0.a", "1.0", -1)]
    [InlineData("RubyGems", "1.10", "1.9.3", 1)]
    public void Versions_are_ordered_as_their_ecosystem_orders_them(string ecosystem, string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(Versions.Compare(ecosystem, a, b) ?? 99));

    [Fact]
    public void A_range_is_from_its_introduced_up_to_its_fix_or_its_last_affected()
    {
        Assert.True(Versions.InRange("npm", "4.17.20", "0", "4.17.21", null));
        Assert.False(Versions.InRange("npm", "4.17.21", "0", "4.17.21", null));
        Assert.False(Versions.InRange("PyPI", "2.2.0", "2.3.0", "2.31.0", null));
        Assert.True(Versions.InRange("npm", "4.5.0", "0", null, "4.5.0"));
        Assert.True(Versions.InRange("Go", "v1.2.3", "1.0.0", null, null));
        Assert.Null(Versions.InRange("npm", "^1.2.3", "0", "2.0.0", null));
        Assert.Equal("python-dateutil", Versions.PackageKey("PyPI", "Python_DateUtil"));
    }

    [Fact]
    public void Lock_files_and_manifests_say_which_versions_are_used_and_how_each_comes_in()
    {
        const string npm = """
            {"lockfileVersion": 3, "packages": {
              "": {"name": "app", "dependencies": {"lodash": "^4.17.0", "express": "^4.18.0"}},
              "node_modules/lodash": {"version": "4.17.20"},
              "node_modules/express": {"version": "4.18.2", "dependencies": {"qs": "6.11.0"}},
              "node_modules/qs": {"version": "6.11.0"},
              "node_modules/express/node_modules/lodash": {"version": "3.10.1"},
              "packages/local": {"link": true}
            }}
            """;
        var deps = Manifests.Read("web/package-lock.json", npm).ToList();
        Assert.Contains(new Dependency("npm", "lodash", "4.17.20", true, null, "web/package-lock.json", 3), deps);
        Assert.Contains(deps, d => d is { Name: "qs", Version: "6.11.0", Direct: false, Via: "express" });
        Assert.Contains(deps, d => d is { Name: "lodash", Version: "3.10.1", Direct: false });

        Assert.Equal([("@babel/core", "7.24.0"), ("left-pad", "1.3.0")],
            Manifests.Read("yarn.lock", "\"@babel/core@^7.0.0\", \"@babel/core@^7.24.0\":\n  version \"7.24.0\"\n  resolved \"x\"\n\nleft-pad@^1.0.0:\n  version \"1.3.0\"\n")
                .Select(d => (d.Name, d.Version!)));
        Assert.Equal([("react", "18.2.0"), ("@types/node", "20.1.0")],
            Manifests.Read("pnpm-lock.yaml", "lockfileVersion: '9.0'\n\npackages:\n\n  react@18.2.0:\n    resolution: {}\n\n  '@types/node@20.1.0':\n    resolution: {}\n\nsnapshots:\n\n  react@18.2.0: {}\n")
                .Select(d => (d.Name, d.Version!)));

        var nuget = Manifests.Read("packages.lock.json", """
            {"version": 1, "dependencies": {"net8.0": {
              "Serilog": {"type": "Direct", "requested": "[3.1.1, )", "resolved": "3.1.1"},
              "Newtonsoft.Json": {"type": "Transitive", "resolved": "12.0.3"},
              "Some.Lib": {"type": "Transitive", "resolved": "1.0.0", "dependencies": {"Newtonsoft.Json": "12.0.3"}}
            }}}
            """).ToList();
        Assert.Contains(nuget, d => d is { Name: "Serilog", Direct: true });
        Assert.Contains(nuget, d => d is { Name: "Newtonsoft.Json", Version: "12.0.3", Direct: false, Via: "Some.Lib" });
        Assert.Equal([("Serilog", "3.1.1"), ("Polly", null)],
            Manifests.Read("App.csproj", """<Project><ItemGroup><PackageReference Include="Serilog" Version="3.1.1" /><PackageReference Include="Polly" Version="8.*" /></ItemGroup></Project>""")
                .Select(d => (d.Name, d.Version)));

        Assert.Equal([("requests", "2.30.0"), ("flask", null)], Manifests.Read("requirements.txt", "# web\nrequests==2.30.0\nflask>=2.0  # any\n-r other.txt\n").Select(d => (d.Name, d.Version)));
        Assert.Equal([("serde", "1.0.190")], Manifests.Read("Cargo.lock", "version = 3\n\n[[package]]\nname = \"serde\"\nversion = \"1.0.190\"\n").Select(d => (d.Name, d.Version!)));
        var go = Manifests.Read("go.mod", "module x\n\nrequire (\n\tgolang.org/x/net v0.17.0\n\tgithub.com/pkg/errors v0.9.1 // indirect\n)\n").ToList();
        Assert.Equal([("golang.org/x/net", "v0.17.0", true), ("github.com/pkg/errors", "v0.9.1", false)], go.Select(d => (d.Name, d.Version!, d.Direct)));
        Assert.Equal([("com.fasterxml.jackson.core:jackson-databind", "2.13.4")],
            Manifests.Read("pom.xml", "<project><properties><jackson.version>2.13.4</jackson.version></properties><dependencies><dependency><groupId>com.fasterxml.jackson.core</groupId><artifactId>jackson-databind</artifactId><version>${jackson.version}</version></dependency></dependencies></project>")
                .Select(d => (d.Name, d.Version!)));
        Assert.Equal([("rack", "2.2.3", true), ("nio4r", "2.5.8", false)],
            Manifests.Read("Gemfile.lock", "GEM\n  remote: https://rubygems.org/\n  specs:\n    rack (2.2.3)\n    nio4r (2.5.8)\n\nDEPENDENCIES\n  rack\n").Select(d => (d.Name, d.Version!, d.Direct)));
        Assert.Equal([("symfony/http-kernel", "5.4.20")], Manifests.Read("composer.lock", """{"packages": [{"name": "symfony/http-kernel", "version": "v5.4.20"}]}""").Select(d => (d.Name, d.Version!)));
        Assert.Empty(Manifests.Read("package-lock.json", "not json"));
        Assert.Matches(Manifests.Vendored(), "web/node_modules/x/package-lock.json");
        Assert.False(Manifests.Reads("src/app.cs"));
    }

    [Fact]
    public void A_repositorys_packages_are_matched_against_the_advisories_known_exploited_first_with_the_smallest_upgrade()
    {
        using var dir = new TempDir();
        var work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(Path.Combine(work, "npm"));
        File.WriteAllText(Path.Combine(work, "npm", "A.md"), "# GHSA-a\n");
        File.WriteAllText(Path.Combine(work, "npm", "B.md"), "# GHSA-b\n");
        object Range(string package, string? fixedIn, string advisory, string severity, bool exploited) => new
        {
            ecosystem = "npm", package, introduced = "0", @fixed = fixedIn, last_affected = (string?)null, advisory, aliases = "CVE-1", severity, cwes = "CWE-79",
            exploited, summary = "s", doc_path = $"npm/{(advisory == "GHSA-a" ? "A" : "B")}.md",
        };
        File.WriteAllText(Path.Combine(work, "index.json"), JsonSerializer.Serialize(new
        {
            provenance = "github=abc,kev=2026.10.20",
            docs = new[] { new { path = "npm/A.md", title = "a", url = "u", symbols = Array.Empty<object>() }, new { path = "npm/B.md", title = "b", url = "u", symbols = Array.Empty<object>() } },
            affected = new[] { Range("left-pad", "1.3.0", "GHSA-a", "medium", false), Range("left-pad", "1.4.0", "GHSA-b", "low", true), Range("lodash", "4.17.21", "GHSA-a", "high", false) },
        }));
        var pack = Path.Combine(dir.Path, "advisories.arguspack");
        PackBuilder.BuildPack(new AdvisoriesTexts(), work, pack, "1", FakeEmbedder.Embed, log: TextWriter.Null);
        var packs = PackStore.OpenPacks([pack]);
        try
        {
            Assert.Equal("3", packs[0].Meta["affected_count"]);
            var deps = DepsCheck.Dependencies([("package-lock.json", """{"lockfileVersion": 3, "packages": {"": {"dependencies": {"left-pad": "1"}}, "node_modules/left-pad": {"version": "1.2.0"}, "node_modules/lodash": {"version": "4.17.21"}}}""")]);
            var result = DepsCheck.Check(deps, packs);
            var findings = result["findings"]!.AsArray();
            // The fixed lodash is not one; left-pad is twice, the exploited advisory first, both fixed by 1.4.0.
            Assert.Equal(["GHSA-b", "GHSA-a"], findings.Select(f => f!["advisory"]!.GetValue<string>()));
            Assert.All(findings, f => Assert.Equal("1.4.0", f!["upgrade_to"]!.GetValue<string>()));
            Assert.True(findings[0]!["exploited"]!.GetValue<bool>());
            Assert.True(findings[0]!["direct"]!.GetValue<bool>());
            Assert.Equal("2026-10-20", result["advisories"]![0]!["as_of"]!.GetValue<string>());
            var bom = DepsCheck.CycloneDx("g/app", "abc", deps, DateTimeOffset.UnixEpoch);
            Assert.Equal(["pkg:npm/left-pad@1.2.0", "pkg:npm/lodash@4.17.21"], bom["components"]!.AsArray().Select(c => c!["purl"]!.GetValue<string>()));
            Assert.Equal("pkg:npm/%40babel/core@7.24.0", DepsCheck.Purl(new Dependency("npm", "@babel/core", "7.24.0", true, null, "f", 1)));
        }
        finally { PackStore.ClosePacks(packs); }
    }
}
