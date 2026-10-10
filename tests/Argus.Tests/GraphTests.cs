using System.Diagnostics;
using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Store;

namespace Argus.Tests;

/// <summary>The cross-repository graph: what files declare, how uses resolve, and the estate's questions, on 200 repositories.</summary>
public sealed class GraphTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static List<(string Role, string Kind, string Name)> Decls(string path, string content) =>
        [.. Links.Extract(path, Filters.DetectLang(path), content).Select(d => (d.Role, d.Kind, d.Name))];

    [Fact]
    public void Each_ecosystem_says_what_a_repository_provides_and_uses()
    {
        Assert.Equal([("uses", "nuget", "acme.core"), ("provides", "nuget", "acme.billing")],
            Decls("src/Billing/Billing.csproj", "<Project><PropertyGroup><PackageId>Acme.Billing</PackageId></PropertyGroup><ItemGroup><PackageReference Include=\"Acme.Core\" Version=\"1.0\" /></ItemGroup></Project>"));
        Assert.Equal([("provides", "cs", "Acme.Billing.Invoices"), ("uses", "cs", "Acme.Core.Money"), ("uses", "cs", "System.Linq")],
            Decls("Invoices.cs", "using Acme.Core.Money;\nglobal using System.Linq;\nnamespace Acme.Billing.Invoices;\n"));
        Assert.Equal([("provides", "npm", "@acme/ui"), ("uses", "npm", "@acme/core"), ("uses", "npm", "react")],
            Decls("package.json", "{\"name\": \"@acme/ui\", \"dependencies\": {\"@acme/core\": \"^1\", \"react\": \"18\"}}"));
        Assert.Equal([("uses", "npm", "@acme/core"), ("uses", "npm", "lodash")],
            Decls("src/a.ts", "import { x } from '@acme/core/money';\nconst _ = require(\"lodash/fp\");\nimport y from './local';\n"));
        Assert.Equal([("provides", "pypi", "acme-data-tools"), ("uses", "pypi", "acme-core"), ("uses", "pypi", "requests")],
            Decls("pyproject.toml", "[project]\nname = \"acme_data.tools\"\ndependencies = [\"acme-core>=1.0\", \"requests\"]\n"));
        Assert.Equal([("uses", "pypi", "acme-core"), ("uses", "repo", "data/loader")],
            Decls("requirements.txt", "acme_core==1.2  # ours\n-r base.txt\ngit+https://gitlab.acme.test/data/loader.git@v1#egg=loader\n"));
        Assert.Equal([("provides", "py-file", "src/acme_data/__init__.py"), ("uses", "py", "acme_core.money"), ("uses", "py", "os")],
            Decls("src/acme_data/__init__.py", "from acme_core.money import Money\nimport os\nfrom . import local\n"));
        Assert.Equal([("provides", "go", "gitlab.acme.test/platform/auth"), ("uses", "go", "gitlab.acme.test/core/log")],
            Decls("go.mod", "module gitlab.acme.test/platform/auth\n\nrequire (\n\tgitlab.acme.test/core/log v1.2.0\n)\n"));
        Assert.Equal([("uses", "go", "gitlab.acme.test/core/log/level"), ("uses", "go", "github.com/pkg/errors")],
            Decls("main.go", "package main\n\nimport (\n\t\"fmt\"\n\tlv \"gitlab.acme.test/core/log/level\"\n\t\"github.com/pkg/errors\"\n)\n"));
        Assert.Equal([("provides", "maven", "com.acme:payments"), ("uses", "maven", "com.acme:parent"), ("uses", "maven", "com.acme:core")],
            Decls("pom.xml", "<project><parent><groupId>com.acme</groupId><artifactId>parent</artifactId></parent><artifactId>payments</artifactId><dependencies><dependency><groupId>com.acme</groupId><artifactId>core</artifactId></dependency></dependencies></project>"));
        Assert.Equal([("provides", "java", "com.acme.payments"), ("uses", "java", "com.acme.core.Money")],
            Decls("src/main/java/Pay.java", "package com.acme.payments;\n\nimport com.acme.core.Money;\n"));
        Assert.Equal([("uses", "repo", "core/protos")], Decls(".gitmodules", "[submodule \"protos\"]\n\tpath = protos\n\turl = git@gitlab.acme.test:core/protos.git\n"));
        Assert.Equal([("uses", "repo", "devops/templates"), ("uses", "image", "devops/build-image")],
            Decls(".gitlab-ci.yml", "include:\n  - project: 'devops/templates'\n    file: '/ci.yml'\nimage: registry.acme.test/devops/build-image:3\n"));
        Assert.Equal([("uses", "image", "platform/base")], Decls("Dockerfile", "FROM registry.acme.test:5000/platform/base:1.4 AS build\nFROM nginx:1.25\n"));
        Assert.Equal([("provides", "proto", "api/v1/pay.proto"), ("uses", "proto", "core/money.proto")],
            Decls("api/v1/pay.proto", "syntax = \"proto3\";\nimport \"core/money.proto\";\n"));
        Assert.Equal([("provides", "cargo", "acme-codec"), ("uses", "cargo", "serde")],
            Decls("Cargo.toml", "[package]\nname = \"acme_codec\"\n\n[dependencies]\nserde = \"1\"\n"));
    }

    /// <summary>A generated estate: 200 repositories in seven groups and six languages, each using some of those before it.</summary>
    sealed class Estate
    {
        public readonly List<(string Path, string Lang)> Repos = [];
        public readonly HashSet<(string From, string To)> Expected = [];
        /// <summary>The expected links made by a CI include alone: not what a repository is built from.</summary>
        public readonly HashSet<(string From, string To)> Ci = [];
        public readonly Dictionary<string, long> Ids = new(StringComparer.Ordinal);
    }

    static Estate Build(TestIndex ix, int seed = 7)
    {
        var random = new Random(seed);
        var estate = new Estate();
        var groups = new[] { ("core", "csharp", 20), ("platform", "go", 30), ("payments", "java", 30), ("web", "typescript", 40), ("data", "python", 40), ("native", "cpp", 20), ("devops", "ops", 20) };
        foreach (var (group, lang, count) in groups)
            for (var i = 0; i < count; i++)
                estate.Repos.Add(($"{group}/{group}-{i:00}", lang));
        var files = new Dictionary<string, List<(string Path, string Content)>>();
        foreach (var (path, _) in estate.Repos) files[path] = [];
        string Name(string path) => path.Split('/')[1].Replace("-", "");

        for (var r = 0; r < estate.Repos.Count; r++)
        {
            var (path, lang) = estate.Repos[r];
            var name = Name(path);
            var group = path.Split('/')[0];
            // What it provides, by its language.
            switch (lang)
            {
                case "csharp":
                    files[path].Add(($"src/{name}.csproj", $"<Project><PropertyGroup><PackageId>Acme.{name}</PackageId></PropertyGroup></Project>"));
                    files[path].Add(($"src/Api.cs", $"namespace Acme.{name}.Api;\npublic class Thing {{ }}\n"));
                    break;
                case "go":
                    files[path].Add(("go.mod", $"module gitlab.acme.test/{path}\n"));
                    break;
                case "java":
                    files[path].Add(("pom.xml", $"<project><groupId>com.acme</groupId><artifactId>{name}</artifactId></project>"));
                    files[path].Add(($"src/main/java/com/acme/{name}/Api.java", $"package com.acme.{name};\npublic class Api {{}}\n"));
                    break;
                case "typescript":
                    files[path].Add(("package.json", $"{{\"name\": \"@acme/{name}\", \"dependencies\": {{\"react\": \"18\"}}}}"));
                    break;
                case "python":
                    files[path].Add(("pyproject.toml", $"[project]\nname = \"acme-{name}\"\ndependencies = [\"requests\"]\n"));
                    files[path].Add(($"src/acme_{name}/__init__.py", "import os\n"));
                    // Every Python repository has a local utils package: its own, never another's.
                    files[path].Add(("utils/__init__.py", "import utils.local\n"));
                    break;
                case "cpp":
                    files[path].Add(($"include/{name}/api.h", "#pragma once\n"));
                    break;
            }
            // What it uses: some earlier repositories of the same language (devops: any, by CI, image and submodule).
            var earlier = Enumerable.Range(0, r).Where(j => lang == "ops" ? estate.Repos[j].Lang != "ops" : estate.Repos[j].Lang == lang).ToList();
            var picks = earlier.OrderBy(_ => random.Next()).Take(Math.Min(earlier.Count, random.Next(1, 5))).ToList();
            var manifest = new List<string>();
            var code = new List<string>();
            foreach (var j in picks)
            {
                var (dep, _) = estate.Repos[j];
                var depName = Name(dep);
                estate.Expected.Add((path, dep));
                switch (lang)
                {
                    case "csharp":
                        manifest.Add($"<PackageReference Include=\"Acme.{depName}\" Version=\"1.0\" />");
                        code.Add($"using Acme.{depName}.Api;");
                        break;
                    case "go":
                        manifest.Add($"\tgitlab.acme.test/{dep} v1.0.0");
                        code.Add($"\t\"gitlab.acme.test/{dep}/client\"");
                        break;
                    case "java":
                        manifest.Add($"<dependency><groupId>com.acme</groupId><artifactId>{depName}</artifactId></dependency>");
                        code.Add($"import com.acme.{depName}.Api;");
                        break;
                    case "typescript":
                        manifest.Add($"\"@acme/{depName}\": \"^1\"");
                        code.Add($"import {{ x }} from '@acme/{depName}/lib';");
                        break;
                    case "python":
                        manifest.Add($"acme-{depName}>=1.0");
                        code.Add($"from acme_{depName}.core import thing");
                        break;
                    case "cpp":
                        code.Add($"#include \"{depName}/api.h\"");
                        break;
                    case "ops":
                        switch (random.Next(3))
                        {
                            case 0: manifest.Add($"ci:{dep}"); estate.Ci.Add((path, dep)); break;
                            case 1: manifest.Add($"image:{dep}"); break;
                            default: manifest.Add($"sub:{dep}"); break;
                        }
                        break;
                }
            }
            switch (lang)
            {
                case "csharp":
                    files[path][0] = ($"src/{name}.csproj", $"<Project><PropertyGroup><PackageId>Acme.{name}</PackageId></PropertyGroup><ItemGroup>{string.Join("", manifest)}</ItemGroup></Project>");
                    files[path].Add(($"tests/{name}.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" /><PackageReference Include=\"xunit\" /></ItemGroup></Project>"));
                    files[path].Add(("src/Use.cs", string.Join("\n", code.Append("using System.Linq;")) + "\n"));
                    break;
                case "go":
                    files[path][0] = ("go.mod", $"module gitlab.acme.test/{path}\n\nrequire (\n{string.Join("\n", manifest)}\n\tgithub.com/pkg/errors v0.9.1\n)\n");
                    files[path].Add(("main.go", $"package main\n\nimport (\n\t\"fmt\"\n{string.Join("\n", code)}\n)\n"));
                    break;
                case "java":
                    files[path][0] = ("pom.xml", $"<project><groupId>com.acme</groupId><artifactId>{name}</artifactId><dependencies>{string.Join("", manifest)}<dependency><groupId>org.slf4j</groupId><artifactId>slf4j-api</artifactId></dependency></dependencies></project>");
                    files[path].Add(($"src/main/java/com/acme/{name}/Use.java", $"package com.acme.{name};\n{string.Join("\n", code)}\nimport java.util.List;\n"));
                    break;
                case "typescript":
                    files[path][0] = ("package.json", $"{{\"name\": \"@acme/{name}\", \"dependencies\": {{{string.Join(", ", manifest.Append("\"react\": \"18\""))}}}}}");
                    files[path].Add(("src/index.ts", string.Join("\n", code.Append("import React from 'react';")) + "\n"));
                    break;
                case "python":
                    files[path].Add(("requirements.txt", string.Join("\n", manifest.Append("requests==2.0")) + "\n"));
                    files[path].Add(($"src/acme_{name}/main.py", string.Join("\n", code.Append("import utils")) + "\n"));
                    break;
                case "cpp":
                    files[path].Add(("src/main.cpp", string.Join("\n", code.Append("#include <vector>")) + "\n"));
                    break;
                case "ops":
                    var ci = manifest.Where(m => m.StartsWith("ci:", StringComparison.Ordinal)).Select(m => $"  - project: '{m[3..]}'\n    file: '/ci.yml'").ToList();
                    if (ci.Count > 0) files[path].Add((".gitlab-ci.yml", "include:\n" + string.Join("\n", ci) + "\n"));
                    var images = manifest.Where(m => m.StartsWith("image:", StringComparison.Ordinal)).Select(m => $"FROM registry.acme.test/{m[6..]}:1.0").ToList();
                    if (images.Count > 0) files[path].Add(("Dockerfile", string.Join("\n", images.Append("FROM alpine:3")) + "\n"));
                    var subs = manifest.Where(m => m.StartsWith("sub:", StringComparison.Ordinal)).Select(m => $"[submodule \"x\"]\n\turl = https://gitlab.acme.test/{m[4..]}.git").ToList();
                    if (subs.Count > 0) files[path].Add((".gitmodules", string.Join("\n", subs) + "\n"));
                    break;
            }
            files[path].Add(("README.md", $"# {path}\n\nThe {name} service of the {group} group, which does one thing well.\n"));
        }

        var gitlab = 1;
        foreach (var (path, _) in estate.Repos)
        {
            var id = ix.Repo(gitlab++, path);
            estate.Ids[path] = id;
            foreach (var (file, content) in files[path])
            {
                var lang = Filters.DetectLang(file);
                var fileId = ix.File(id, file, content, lang);
                Writes.ReplaceIncludes(ix.Conn, id, fileId, Includes.Extract(content));
                Writes.ReplaceDecls(ix.Conn, id, fileId, Links.Extract(file, lang, content));
            }
        }
        return estate;
    }

    /// <summary>The pairs walked by default: the product's own links, likely or strong, of any layer but history.</summary>
    static HashSet<(string, string)> Pairs(TestIndex ix) =>
        [.. Argus.Util.Sql.Query(ix.Conn,
                "SELECT DISTINCT a.path_with_namespace AS f, b.path_with_namespace AS t FROM repo_edges e JOIN repos a ON a.id = e.from_repo_id JOIN repos b ON b.id = e.to_repo_id" +
                " WHERE e.scope = 'main' AND e.confidence >= 0.5 AND e.layer <> 'history'")
            .Select(r => (r.Str("f"), r.Str("t")))];

    [Fact]
    public void Two_hundred_repositories_are_linked_exactly_as_they_use_each_other_in_six_languages()
    {
        using var ix = new TestIndex();
        var estate = Build(ix);
        var clock = Stopwatch.StartNew();
        Resolve.ResolveIncludes(ix.Conn);
        Graph.RebuildRepoDeps(ix.Conn);
        var stats = Graph.RebuildLinks(ix.Conn);
        clock.Stop();

        var found = Pairs(ix);
        var missing = estate.Expected.Except(found).ToList();
        var wrong = found.Except(estate.Expected).ToList();
        Assert.True(missing.Count == 0, $"missing {missing.Count}: {string.Join(", ", missing.Take(5))}");
        Assert.True(wrong.Count == 0, $"wrong {wrong.Count}: {string.Join(", ", wrong.Take(5))}");
        Assert.True(estate.Expected.Count > 400, $"the estate has {estate.Expected.Count} links");
        // Public packages and the standard libraries stay outside; every repository's own utils stays inside it.
        Assert.True(stats["external"] > 0);
        Assert.True(stats["internal"] >= 40);
        Assert.Equal(0, stats.GetValueOrDefault("ambiguous"));
        Assert.Equal(0, stats.GetValueOrDefault("candidate"));
        // Each is made by a package, a submodule, a CI include, an image or many files' imports: strong.
        var weaker = Argus.Util.Sql.Query(ix.Conn,
            "SELECT a.path_with_namespace AS f, b.path_with_namespace AS t, MAX(e.confidence) AS c FROM repo_edges e JOIN repos a ON a.id = e.from_repo_id" +
            " JOIN repos b ON b.id = e.to_repo_id WHERE e.scope = 'main' GROUP BY e.from_repo_id, e.to_repo_id HAVING MAX(e.confidence) < 0.85").Select(r => $"{r.Str("f")} -> {r.Str("t")} {r.Double("c")}").ToList();
        Assert.True(weaker.Count == 0, $"not strong {weaker.Count}: {string.Join(", ", weaker.Take(5))}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        output.WriteLine($"200 repositories, {estate.Expected.Count} links expected and found; resolved in {clock.ElapsedMilliseconds} ms; " +
                         string.Join(", ", stats.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
    }

    [Fact]
    public void A_name_two_repositories_provide_links_to_neither_and_is_counted()
    {
        using var ix = new TestIndex();
        var a = ix.Repo(1, "g/a");
        var b = ix.Repo(2, "g/b");
        var c = ix.Repo(3, "g/c");
        foreach (var (repo, file, content) in new[] { (a, "A.cs", "namespace Acme.Shared;"), (b, "B.cs", "namespace Acme.Shared;"), (c, "C.cs", "using Acme.Shared;") })
            Writes.ReplaceDecls(ix.Conn, repo, ix.File(repo, file, content, "csharp"), Links.Extract(file, "csharp", content));
        var stats = Graph.RebuildLinks(ix.Conn);
        Assert.Equal(1, stats["ambiguous"]);
        Assert.Empty(Pairs(ix));
    }

    [Fact]
    public void A_link_has_a_confidence_a_tier_a_scope_and_its_evidence_and_a_name_several_provide_is_settled_or_a_candidate()
    {
        using var ix = new TestIndex();
        var a = ix.Repo(1, "g/a");
        var b = ix.Repo(2, "g/b");
        var byPackage = ix.Repo(3, "g/by-package");
        var byFile = ix.Repo(4, "g/by-file");
        var unsure = ix.Repo(5, "g/unsure");
        var testsOnly = ix.Repo(6, "g/tests-only");
        var once = ix.Repo(7, "g/once");
        Index(ix,
            (a, "src/A.csproj", "<Project><PropertyGroup><PackageId>Acme.A</PackageId></PropertyGroup></Project>"),
            (a, "src/Shared.cs", "namespace Acme.Shared;"),
            (a, "src/Only.cs", "namespace Acme.Only;"),
            (b, "src/B.csproj", "<Project><PropertyGroup><PackageId>Acme.B</PackageId></PropertyGroup></Project>"),
            (b, "src/Shared.cs", "namespace Acme.Shared;"),
            // Its package reference says which Acme.Shared it means.
            (byPackage, "App.csproj", "<Project><ItemGroup><PackageReference Include=\"Acme.B\" /></ItemGroup></Project>"),
            (byPackage, "One.cs", "using Acme.Shared;"),
            (byPackage, "Two.cs", "using Acme.Shared;"),
            // The same file's other use says it.
            (byFile, "One.cs", "using Acme.Only;\nusing Acme.Shared;"),
            // Nothing says it.
            (unsure, "One.cs", "using Acme.Shared;"),
            // Only its tests use a.
            (testsOnly, "tests/OnlyTests.cs", "using Acme.Only;"),
            // One file uses a: a code link on one file is less sure.
            (once, "One.cs", "using Acme.Only;"));
        var stats = Graph.RebuildLinks(ix.Conn);

        Link? Of(long from, long to, string kind, string name = "Acme.Shared") => Argus.Util.Sql.Query(ix.Conn,
                "SELECT how, scope, confidence, tier, files, providers, evidence FROM repo_links WHERE from_repo_id = ? AND to_repo_id = ? AND kind = ? AND name = ?",
                from, to, kind, kind == "package:nuget" ? "acme.b" : name)
            .Select(r => new Link(r.Str("how"), r.Str("scope"), r.Double("confidence"), r.Str("tier"), r.Long("files"), r.Long("providers"), JsonNode.Parse(r.Str("evidence"))!))
            .FirstOrDefault();

        // By a package reference: the package (0.97) and the import it settles (0.85 × 0.95), as one pair.
        var package = Of(byPackage, b, "package:nuget")!;
        Assert.Equal(("strong", 0.97), (package.Tier, package.Confidence));
        var settled = Of(byPackage, b, "import:csharp")!;
        Assert.Equal(("settled:manifest", 2L, 2L), (settled.How, settled.Providers, settled.Files));
        Assert.Equal(0.8075, settled.Confidence, 4);
        Assert.Null(Of(byPackage, a, "import:csharp"));
        var edge = Argus.Util.Sql.One(ix.Conn, "SELECT confidence, tier, kinds FROM repo_edges WHERE from_repo_id = ? AND to_repo_id = ? AND layer = 'build'", byPackage, b)!;
        Assert.Equal(1 - (1 - 0.97) * (1 - 0.8075), edge.Double("confidence"), 4);
        Assert.Equal(("strong", "package:nuget,import:csharp"), (edge.Str("tier"), edge.Str("kinds")));

        // By the same file: Acme.Only is a's alone, so its Acme.Shared is a's too (0.85 × 0.8, less on one file).
        var byItsFile = Of(byFile, a, "import:csharp")!;
        Assert.Equal(("settled:file", "likely"), (byItsFile.How, byItsFile.Tier));
        Assert.Equal(0.578, byItsFile.Confidence, 4);
        Assert.Contains("same file", byItsFile.Evidence["why"]!.ToString());
        Assert.Null(Of(byFile, b, "import:csharp"));

        // Unsettled: a candidate of each, never walked, and says which.
        var candidate = Of(unsure, a, "import:csharp")!;
        Assert.Equal(("candidate", 0.425), (candidate.Tier, candidate.Confidence));
        Assert.Equal(["g/a", "g/b"], candidate.Evidence["candidates"]!.AsArray().Select(n => n!.ToString()));
        Assert.Equal("candidate", Of(unsure, b, "import:csharp")!.Tier);
        Assert.Equal(1, stats["candidate"]);

        // Tests only: kept as test scope, never walked by default.
        Assert.Equal("test", Of(testsOnly, a, "import:csharp", "Acme.Only")!.Scope);

        // One file: 0.85 × 0.85, likely; its evidence names the use and where a provides it.
        var single = Of(once, a, "import:csharp", "Acme.Only")!;
        Assert.Equal(("likely", 0.7225), (single.Tier, single.Confidence));
        Assert.Equal("One.cs", single.Evidence["uses"]![0]!["path"]!.ToString());
        Assert.Equal("src/Only.cs", single.Evidence["provider"]!["path"]!.ToString());
        Assert.Equal(1L, Argus.Util.Sql.Scalar(ix.Conn, "SELECT COUNT(*) FROM file_links WHERE to_repo_id = ? AND name = 'Acme.Only' AND file_id IN (SELECT id FROM files WHERE repo_id = ?)", a, once));

        Assert.Equal(new HashSet<(string, string)> { ("g/by-package", "g/b"), ("g/by-file", "g/a"), ("g/once", "g/a") }, Pairs(ix));
    }

    sealed record Link(string How, string Scope, double Confidence, string Tier, long Files, long Providers, JsonNode Evidence);

    /// <summary>Writes files into repositories as an index run would, with what they declare.</summary>
    static void Index(TestIndex ix, params (long Repo, string Path, string Content)[] files)
    {
        foreach (var (repo, path, content) in files)
        {
            var lang = Filters.DetectLang(path);
            Writes.ReplaceDecls(ix.Conn, repo, ix.File(repo, path, content, lang), Links.Extract(path, lang, content));
        }
    }

    [Fact]
    public void Names_that_only_look_alike_link_nothing_a_platform_root_a_package_root_the_standard_library_fixtures_and_strings()
    {
        using var ix = new TestIndex();
        var trap = ix.Repo(1, "trap/polyfills");
        var core = ix.Repo(2, "payments/app");
        var stdlibish = ix.Repo(3, "tools/logging");
        var lib = ix.Repo(4, "lib/money");
        var users = Enumerable.Range(0, 20).Select(i => ix.Repo(100 + i, $"apps/app{i}")).ToList();
        Index(ix,
            // A polyfill declares .NET's own namespaces; a raw string holds code that is not this file's.
            (trap, "Ext.cs", "namespace System;\npublic static class StringExt {}"),
            (trap, "IsExternalInit.cs", "namespace System.Runtime.CompilerServices { internal static class IsExternalInit {} }"),
            (trap, "Di.cs", "namespace Microsoft.Extensions.DependencyInjection;"),
            (trap, "Gen.cs", "var src = \"\"\"\nnamespace Acme.Money;\nusing Acme.Secret;\n\"\"\";"),
            // A root package, and a repository package named like the standard library.
            (core, "src/main/java/com/acme/Application.java", "package com.acme;"),
            (stdlibish, "logging/__init__.py", ""),
            // The money library, and a test fixture elsewhere that claims its name.
            (lib, "src/Money.cs", "namespace Acme.Money { namespace Rounding { class R {} } }"),
            (lib, "src/main/java/com/acme/money/Cents.java", "package com.acme.money;"),
            (core, "tests/fixtures/Fake.cs", "namespace Acme.Money.Rounding;"));
        foreach (var u in users)
        {
            Index(ix,
                (u, "Program.cs", "using System.Linq;\nusing System.Runtime.CompilerServices;\nusing Microsoft.Extensions.DependencyInjection;\nglobal using global::Acme.Money.Rounding;\nusing static Acme.Money.Rounding.R;"),
                (u, "Pay.java", "import com.acme.generated.pay.v1.PayRequest;\nimport com.acme.money.Cents;\nimport com.acme.money.*;"),
                (u, "app.py", "import logging\nfrom logging import getLogger"));
        }
        Graph.RebuildLinks(ix.Conn);
        var pairs = Pairs(ix);
        // Each app links to the money library only: by C# (the nested namespace whole, and a using static) and by Java.
        Assert.Equal(users.Count, pairs.Count);
        Assert.All(pairs, p => Assert.Equal("lib/money", p.Item2));
        var names = Argus.Util.Sql.Query(ix.Conn, "SELECT DISTINCT kind, name FROM repo_links WHERE tier <> 'candidate'").Select(r => (r.Str("kind"), r.Str("name"))).ToHashSet();
        Assert.Equal(new HashSet<(string, string)> { ("import:csharp", "Acme.Money.Rounding"), ("import:java", "com.acme.money") }, names);
    }

    [Fact]
    public void A_python_module_is_named_from_its_import_root_and_a_bare_name_needs_a_declared_dependency()
    {
        Assert.Equal(["acme.money", "acme.money.round", "acme.money.tax", "cli", "tool"],
            Graph.PythonModules(["python/acme/__init__.py", "python/acme/money/__init__.py", "python/acme/money/round.py", "packages/x/acme/money/tax.py",
                "python/acme/money/tax.py", "cli.py", "src/tool.py", "tests/test_round.py", "setup.py"]).Where(m => m != "acme").Distinct().Order());
        using var ix = new TestIndex();
        var lib = ix.Repo(1, "lib/money");
        var app = ix.Repo(2, "apps/shop");
        var other = ix.Repo(3, "apps/stats");
        Index(ix,
            (lib, "python/acme/__init__.py", ""), (lib, "python/acme/money/__init__.py", ""), (lib, "config.py", ""),
            (lib, "pyproject.toml", "[project]\nname = \"acme-money\"\n"),
            (app, "shop.py", "from acme.money import round\nimport config\n"), (app, "requirements.txt", "acme-money\n"),
            (other, "stats.py", "import config\n"));
        var stats = Graph.RebuildLinks(ix.Conn);
        // Nested under python/, the package is acme.money; a bare config links only where acme-money is declared.
        Assert.Equal(new HashSet<(string, string)> { ("apps/shop", "lib/money") }, Pairs(ix));
        Assert.Equal(1, stats["unconfirmed"]);
    }

    [Fact]
    public void Images_link_to_the_project_they_are_built_from_sub_images_and_private_registries_too_mirrors_not()
    {
        using var ix = new TestIndex();
        var api = ix.Repo(1, "shop/payments");
        var mirror = ix.Repo(2, "mirrors/library/nginx");
        var flat = ix.Repo(3, "team/billing-api");
        var user = ix.Repo(4, "deploy/prod");
        Index(ix, (user, "docker-compose.yml",
            "services:\n  a:\n    image: registry.gitlab.acme.io/shop/payments/api:1.2\n  b:\n    image: library/nginx:1\n  c:\n    image: 123456789.dkr.ecr.eu-west-1.amazonaws.com/billing-api:3\n"));
        Graph.RebuildLinks(ix.Conn);
        Assert.Equal(new HashSet<(string, string)> { ("deploy/prod", "shop/payments"), ("deploy/prod", "team/billing-api") }, Pairs(ix));
        _ = (api, mirror, flat);
    }

    [Fact]
    public void Each_ecosystems_own_conventions_are_read()
    {
        // .NET: ProjectReference, <Using>, a PackageId of $(MSBuildProjectName).
        Assert.Equal([("uses", "nuget", "core"), ("uses", "cs", "Acme.Core"), ("provides", "nuget", "acme.api")],
            Decls("src/Acme.Api.csproj", "<Project><PropertyGroup><PackageId>$(MSBuildProjectName)</PackageId></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\..\\core\\src\\Core.csproj\" /><Using Include=\"Acme.Core\" /></ItemGroup></Project>"));
        // Maven: the parent, ${project.groupId}, artifactId before groupId.
        Assert.Equal([("provides", "maven", "com.acme:api"), ("uses", "maven", "com.acme:corp-parent"), ("uses", "maven", "com.acme:core"), ("uses", "maven", "com.acme:money")],
            Decls("pom.xml", "<project><parent><groupId>com.acme</groupId><artifactId>corp-parent</artifactId></parent><groupId>com.acme</groupId><artifactId>api</artifactId><dependencies><dependency><groupId>${project.groupId}</groupId><artifactId>core</artifactId></dependency><dependency><artifactId>money</artifactId><groupId>com.acme</groupId></dependency></dependencies></project>"));
        // Gradle: platform(), the map notation, a version catalog.
        Assert.Equal([("uses", "maven", "com.acme:bom"), ("uses", "maven", "com.acme:core")],
            Decls("build.gradle", "dependencies {\n  implementation(platform(\"com.acme:bom:1\"))\n  ksp group: 'com.acme', name: 'core'\n}\n"));
        Assert.Equal([("uses", "maven", "com.acme:money")], Decls("gradle/libs.versions.toml", "[versions]\nx = \"1\"\n[libraries]\nacme-money = { module = \"com.acme:money\", version.ref = \"x\" }\n"));
        // Python: -e and PEP 508 URLs, git+ssh; an import inside a try block.
        Assert.Equal([("uses", "repo", "core/lib"), ("uses", "pypi", "acme-core"), ("uses", "repo", "core/acme-core"), ("uses", "repo", "core/x")],
            Decls("requirements.txt", "-e git+https://gitlab.acme.test/core/lib.git#egg=lib\nacme-core @ git+https://gitlab.acme.test/core/acme-core.git@v1\ngit+ssh://git@gitlab.acme.test/core/x.git\n"));
        Assert.Contains(("uses", "py", "acme_core"), Decls("app.py", "try:\n    import acme_core\nexcept ImportError:\n    pass\n"));
        // Rust: workspace dependencies and a renamed package.
        Assert.Equal([("uses", "cargo", "acme-codec"), ("uses", "cargo", "acme-time")],
            Decls("Cargo.toml", "[workspace.dependencies]\ncodec = { package = \"acme-codec\", version = \"1\" }\nacme_time = \"2\"\n"));
        // GitLab CI: files under .gitlab/, and components.
        Assert.Equal([("uses", "repo", "devops/templates"), ("uses", "repo", "devops/components")],
            Decls(".gitlab/ci/build.yml", "include:\n  - project: devops/templates\n  - component: gitlab.acme.test/devops/components/dotnet@1.0\n"));
    }

    [Fact]
    public void A_relative_submodule_is_the_estates_own_and_a_path_alias_is_no_package()
    {
        using var ix = new TestIndex();
        var auth = ix.Repo(1, "platform/auth");
        var protos = ix.Repo(2, "platform/core/protos");
        var shop = ix.Repo(3, "web/shop");
        var ui = ix.Repo(4, "design/ui");
        Index(ix,
            (auth, ".gitmodules", "[submodule \"protos\"]\n  path = protos\n  url = ../core/protos.git\n"),
            (shop, "tsconfig.base.json", "{ \"compilerOptions\": { \"paths\": { \"@acme/ui\": [\"libs/ui/src/index.ts\"] } } }"),
            (shop, "apps/web/main.ts", "import { Button } from '@acme/ui';\n"),
            (ui, "package.json", "{\"name\": \"@acme/ui\"}"));
        Graph.RebuildLinks(ix.Conn);
        Assert.Equal(new HashSet<(string, string)> { ("platform/auth", "platform/core/protos") }, Pairs(ix));
        _ = (protos, ui);
    }

    [Fact]
    public void Only_real_uses_count_imports_in_code_not_strings_direct_requirements_not_pinned_or_indirect_ones()
    {
        // Go: a quoted module path in code is not an import; an aliased or dot import is.
        Assert.Equal([("uses", "go", "gitlab.acme.io/lib/money"), ("uses", "go", "gitlab.acme.io/lib/tax")],
            Decls("main.go", "package main\nimport (\n  m \"gitlab.acme.io/lib/money\"\n  . \"gitlab.acme.io/lib/tax\"\n)\nvar s = []string{\n  \"gitlab.acme.io/lib/other\",\n}\n"));
        // go.mod: an indirect requirement is not the module's own; a replace to a fork is.
        Assert.Equal([("provides", "go", "gitlab.acme.io/app"), ("uses", "go", "gitlab.acme.io/lib/money"), ("uses", "go", "gitlab.acme.io/forks/tax")],
            Decls("go.mod", "module gitlab.acme.io/app\nrequire (\n  gitlab.acme.io/lib/money v1.2.0\n  gitlab.acme.io/lib/util v0.1.0 // indirect\n)\nreplace gitlab.acme.io/lib/tax => gitlab.acme.io/forks/tax v1.0.0\n"));
        // Maven: dependencyManagement only pins versions.
        Assert.Equal([("provides", "maven", "com.acme:app"), ("uses", "maven", "com.acme:money")],
            Decls("pom.xml", "<project><groupId>com.acme</groupId><artifactId>app</artifactId><dependencyManagement><dependencies><dependency><groupId>com.acme</groupId><artifactId>bom-only</artifactId></dependency></dependencies></dependencyManagement><dependencies><dependency><groupId>com.acme</groupId><artifactId>money</artifactId></dependency></dependencies></project>"));
        // pyproject: an extra's bracket does not end the list.
        Assert.Equal([("provides", "pypi", "app"), ("uses", "pypi", "acme-money"), ("uses", "pypi", "acme-tax")],
            Decls("pyproject.toml", "[project]\nname = \"app\"\ndependencies = [\"acme-money[fast]>=1\", \"acme-tax\"]\n"));
        // requirements/ files, and PackageReference with its version first; an Update item is not a use.
        Assert.Equal([("uses", "pypi", "acme-money")], Decls("requirements/base.txt", "acme-money==1.0\n"));
        Assert.Equal([("uses", "nuget", "acme.core")],
            Decls("Directory.Packages.props", "<Project><ItemGroup><PackageReference Version=\"1\" Include=\"Acme.Core\" /><PackageReference Update=\"Acme.Old\" Version=\"2\" /></ItemGroup></Project>"));
        // GitLab CI's image: name: form, and a chart's repository: is no image.
        Assert.Equal([("uses", "image", "acme/build")], Decls(".gitlab-ci.yml", "image:\n  name: registry.acme.io/acme/build:1\n"));
        Assert.Empty(Decls("Chart.yaml", "dependencies:\n  - name: redis\n    repository: https://charts.example.com/stable\n"));
    }

    static (Estate, TestIndex) Linked()
    {
        var ix = new TestIndex();
        var estate = Build(ix);
        Resolve.ResolveIncludes(ix.Conn);
        Graph.RebuildRepoDeps(ix.Conn);
        Graph.RebuildLinks(ix.Conn);
        return (estate, ix);
    }

    static HashSet<string> Reach(Estate estate, string start, bool uses)
    {
        var seen = new HashSet<string> { start };
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var (f, t) in estate.Expected)
            {
                var next = uses ? (f == at ? t : null) : (t == at ? f : null);
                if (next is not null && seen.Add(next)) queue.Enqueue(next);
            }
        }
        seen.Remove(start);
        return seen;
    }

    [Fact]
    public void What_a_change_reaches_and_how_one_repository_reaches_another_follow_the_graph()
    {
        var (estate, ix) = Linked();
        using var _ = ix;
        var all = estate.Ids.Values.ToList();
        // The repository most depended on (directly or not) among the first C# ones.
        var core = estate.Repos.Where(r => r.Lang == "csharp").Select(r => r.Path).OrderByDescending(p => Reach(estate, p, uses: false).Count).First();
        var impact = GraphQueries.ChangeImpact(all, ix.Conn, core, depth: 8);
        var reached = impact["by_depth"]!.AsArray().SelectMany(l => l!["repos"]!.AsArray().Select(r => r!["repo"]!.GetValue<string>())).ToHashSet();
        Assert.Equal(Reach(estate, core, uses: false), reached);

        // A path from a repository far up to the core one, each step with its evidence.
        var far = reached.First(r => impact["by_depth"]!.AsArray().Last()!["repos"]!.AsArray().Any(x => x!["repo"]!.GetValue<string>() == r));
        var path = GraphQueries.DependencyPath(all, ix.Conn, far, core);
        var steps = path["paths"]![0]!["steps"]!.AsArray();
        Assert.Equal(far, steps[0]!["from"]!.GetValue<string>());
        Assert.Equal(core, steps[^1]!["to"]!.GetValue<string>());
        Assert.NotEmpty(steps[0]!["by"]!.AsObject());
        // Asked the other way round, it says which way the dependency runs.
        Assert.Contains("not the other way", GraphQueries.DependencyPath(all, ix.Conn, core, far)["direction"]!.GetValue<string>());
    }

    [Fact]
    public void The_system_map_has_hubs_layers_groups_and_a_line_per_link()
    {
        var (estate, ix) = Linked();
        using var _ = ix;
        var map = GraphQueries.SystemMap(estate.Ids.Values.ToList(), ix.Conn);
        Assert.Equal(200, map["repos"]!.GetValue<int>());
        Assert.Equal(estate.Expected.Count, map["links"]!.GetValue<int>());
        var top = map["hubs"]![0]!;
        // Hubs are of what each is built from: a CI template every pipeline includes is not one.
        var mostUsed = estate.Expected.Except(estate.Ci).GroupBy(e => e.To).Max(g => g.Count());
        Assert.Equal(mostUsed, top["used_by"]!.GetValue<int>());
        Assert.StartsWith("The ", top["what"]!.GetValue<string>());
        Assert.Equal(0, map["layers"]![0]!["layer"]!.GetValue<int>());
        Assert.Empty(map["cycles"]!.AsArray());
        Assert.Contains("devops -> ", string.Join("\n", map["between_groups"]!.AsObject().Select(k => k.Key)));
        Assert.Contains(" -> ", map["edges"]![0]!.GetValue<string>());

        // Focused: one repository's neighbourhood only.
        var focus = estate.Repos[0].Path;
        var near = GraphQueries.SystemMap(estate.Ids.Values.ToList(), ix.Conn, focus, depth: 1);
        Assert.Equal(1 + estate.Expected.Count(e => e.To == focus || e.From == focus), near["repos"]!.GetValue<int>());
    }

    [Fact]
    public void A_symbols_uses_are_found_only_in_the_repositories_that_depend_on_its_own()
    {
        using var ix = new TestIndex();
        var lib = ix.Repo(1, "core/money");
        var app = ix.Repo(2, "web/shop");
        var stranger = ix.Repo(3, "games/arcade");
        var defined = ix.File(lib, "package.json", "{\"name\": \"@acme/money\"}", "npm");
        Writes.ReplaceDecls(ix.Conn, lib, defined, Links.Extract("package.json", "npm", "{\"name\": \"@acme/money\"}"));
        var api = ix.File(lib, "src/round.ts", "export function roundCents(x) {}\n", "typescript");
        ix.Symbol(lib, api, "roundCents");
        var use = "import { roundCents } from '@acme/money';\nroundCents(1);\n";
        Writes.ReplaceDecls(ix.Conn, app, ix.File(app, "src/cart.ts", use, "typescript"), Links.Extract("src/cart.ts", "typescript", use));
        ix.File(stranger, "src/score.ts", "function roundCents() {} // their own\n", "typescript");
        Graph.RebuildLinks(ix.Conn);

        var impact = GraphQueries.ChangeImpact([lib, app, stranger], ix.Conn, "core/money", "roundCents");
        Assert.Equal(1, impact["dependents"]!.GetValue<int>());
        var repos = impact["references"]!.AsArray().Select(r => r!["repo"]!.GetValue<string>()).Distinct().Order().ToList();
        Assert.Equal(["core/money", "web/shop"], repos);
        Assert.Equal("src/round.ts", impact["defined"]![0]!["path"]!.GetValue<string>());
        // A repository the caller may not read is neither shown nor walked through.
        Assert.Equal(0, GraphQueries.ChangeImpact([lib, stranger], ix.Conn, "core/money")["dependents"]!.GetValue<int>());
    }
}
