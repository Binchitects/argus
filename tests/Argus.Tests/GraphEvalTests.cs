using Argus.Indexing;
using Argus.Store;
using Argus.Util;

namespace Argus.Tests;

/// <summary>
/// The graph measured against what owners know (argus graph-eval), calibrated priors read back by the next rebuild, and
/// what each repository is for, kept with the graph.
/// </summary>
public sealed class GraphEvalTests
{
    static void Add(TestIndex ix, long repo, string file, string content, string lang = "msbuild") =>
        Writes.ReplaceDecls(ix.Conn, repo, ix.File(repo, file, content, lang), Links.Extract(file, lang, content));

    static string Project(string id, params string[] uses) =>
        $"<Project><PropertyGroup><PackageId>Acme.{id}</PackageId><Description>The {id} library of Acme.</Description></PropertyGroup><ItemGroup>" +
        string.Concat(uses.Select(u => $"<PackageReference Include=\"Acme.{u}\" Version=\"1.0\" />")) + "</ItemGroup></Project>";

    /// <summary>core; billing and web use core by package; web uses billing by package; report uses nothing the graph can see.</summary>
    static TestIndex Estate()
    {
        var ix = new TestIndex();
        var core = ix.Repo(1, "g/core");
        var billing = ix.Repo(2, "g/billing");
        var web = ix.Repo(3, "g/web");
        var report = ix.Repo(4, "g/report");
        Add(ix, core, "src/Core.csproj", Project("Core"));
        Add(ix, billing, "src/Billing.csproj", Project("Billing", "Core"));
        Add(ix, web, "src/Web.csproj", Project("Web", "Core", "Billing"));
        Add(ix, report, "src/Report.csproj", "<Project><PropertyGroup><PackageId>Acme.Report</PackageId></PropertyGroup></Project>");
        ix.File(core, "README.md", "[![build](https://ci/badge.svg)](https://ci)\n# Core\n\nThe **money** and [time](docs/time.md) types every service uses. Start with `Money`.\n", "markdown");
        ix.File(report, "Program.cs", "class Program { static void Main() {} }\n", "csharp");
        Graph.RebuildLinks(ix.Conn);
        return ix;
    }

    [Fact]
    public void The_graph_is_measured_against_what_the_owners_confirm()
    {
        using var ix = Estate();
        var truth = Truth.Parse("""
            # billing's owners: it uses core.
            g/billing -> g/core build
            # web's owners: core and billing, and nothing else.
            g/web -> g/core
            g/web -> g/billing
            # report's owners: it calls billing at run time (no file says so).
            g/report -> g/billing runtime
            g/report !-> g/core
            g/ghost -> g/core
            this is not a link
            """);
        Assert.Single(truth.Problems);
        var report = GraphEval.Run(ix.Conn, truth);
        Assert.Equal(3, report["judged_repos"]!.GetValue<int>());
        Assert.Equal(["g/ghost"], report["unknown_repos"]!.AsArray().Select(u => u!.GetValue<string>()));
        var pairs = report["pairs"]!;
        Assert.Equal((3, 3, 0, 1), (pairs["walked"]!.GetValue<int>(), pairs["right"]!.GetValue<int>(), pairs["wrong"]!.GetValue<int>(), pairs["missed"]!.GetValue<int>()));
        Assert.Equal((1.0, 0.75), (pairs["precision"]!.GetValue<double>(), pairs["recall"]!.GetValue<double>()));
        var missed = Assert.Single(report["missed"]!.AsArray());
        Assert.Equal(("g/report", "g/billing"), (missed!["from"]!.GetValue<string>(), missed["to"]!.GetValue<string>()));
        var kind = report["by_kind"]!.AsArray().Single(k => k!["kind"]!.GetValue<string>() == "package:nuget");
        Assert.Equal((3, 3), (kind!["links"]!.GetValue<int>(), kind["right"]!.GetValue<int>()));
        // Too few links of the kind to calibrate its prior yet.
        Assert.Null(kind["suggested"]);
        Assert.Empty(report["priors"]!.AsObject());
    }

    [Fact]
    public void Calibrated_priors_are_suggested_from_enough_links_and_read_back_by_the_next_rebuild()
    {
        using var ix = Estate();
        // Owners who say every link is wrong: the package prior falls, drawn toward the old one by its weight.
        for (var i = 10; i < 16; i++)
        {
            var repo = ix.Repo(i, $"g/app{i}");
            Add(ix, repo, $"src/App{i}.csproj", Project($"App{i}", "Core"));
        }
        Graph.RebuildLinks(ix.Conn);
        var truth = Truth.Parse(string.Join("\n", Enumerable.Range(10, 6).Select(i => $"g/app{i} !-> g/core")));
        var report = GraphEval.Run(ix.Conn, truth);
        Assert.Equal(0.0, report["pairs"]!["precision"]!.GetValue<double>());
        var suggested = report["priors"]!["package:nuget|exact"]!.GetValue<double>();
        Assert.Equal(Math.Round(4 * 0.97 / 10, 2), suggested);
        Assert.Equal(6, report["wrong"]!.AsArray().Count(w => w!["owners_say_no"]!.GetValue<bool>()));

        // Written where the index reads them: the next rebuild uses them, and the links fall to weak.
        File.WriteAllText(LinkKinds.PriorsPath(ix.DbPath), report["priors"]!.ToJsonString());
        try
        {
            Graph.RebuildLinks(ix.Conn);
            Assert.Equal(suggested, LinkKinds.Prior("package:nuget", "exact", null, LinkKinds.ReadPriors(ix.DbPath)));
            var tier = Sql.One(ix.Conn, "SELECT e.tier FROM repo_edges e JOIN repos r ON r.id = e.from_repo_id WHERE r.path_with_namespace = 'g/app10'")!.Str("tier");
            Assert.Equal("weak", tier);
        }
        finally
        {
            File.Delete(LinkKinds.PriorsPath(ix.DbPath));
        }
        Assert.Empty(LinkKinds.ReadPriors(ix.DbPath));
    }

    [Fact]
    public void What_each_repository_is_for_comes_from_its_readme_manifest_or_code_unless_an_admin_said()
    {
        using var ix = Estate();
        var all = Purposes.All(ix.Conn);
        Assert.Equal(("The money and time types every service uses.", Purposes.Readme), all[1]);
        Assert.Equal(("The Billing library of Acme.", Purposes.Manifest), all[2]);
        Assert.StartsWith("A C# ", all[4].Text, StringComparison.Ordinal);

        // An admin's line stays over the passes, until it is taken back.
        Purposes.Set(ix.Conn, 2, "Invoices and payments for every product.", 100);
        Graph.RebuildLinks(ix.Conn);
        Assert.Equal(("Invoices and payments for every product.", Purposes.Owner), Purposes.All(ix.Conn)[2]);
        Purposes.Set(ix.Conn, 2, "", 101);
        Assert.Equal(Purposes.Manifest, Purposes.All(ix.Conn)[2].Source);

        // In the overview, and in the system map's hubs.
        var ids = Sql.Query(ix.Conn, "SELECT id FROM repos").Select(r => r.Long("id")).ToList();
        var overview = Queries.RepoOverview(ids, ix.Conn, "g/core");
        Assert.Equal("The money and time types every service uses.", overview["repos"]![0]!["purpose"]!.GetValue<string>());
        var hubs = GraphQueries.SystemMap(ids, ix.Conn)["hubs"]!.AsArray();
        Assert.Equal("The money and time types every service uses.", hubs.Single(h => h!["repo"]!.GetValue<string>() == "g/core")!["what"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("---\ntitle: x\n---\n# T\n\nA tool that ships builds. More here.", "A tool that ships builds.")]
    [InlineData("Title\n=====\n\n<!-- hidden\nlong comment -->\n```\ncode block here\n```\n| a | b |\nServes the catalog over gRPC to the shop.", "Serves the catalog over gRPC to the shop.")]
    [InlineData("[Docs](x) | [API](y)\n\n> Reads `events` from Kafka and writes them to S3!", "Reads events from Kafka and writes them to S3!")]
    [InlineData("# Only a heading\n", null)]
    public void A_readme_says_what_it_is_in_its_first_sentence_that_says_something(string readme, string? expected) =>
        Assert.Equal(expected, Purposes.FromReadmeText(readme));
}

/// <summary>argus graph-eval on the command line: the report, and priors written where the index reads them.</summary>
[Collection("process-state")]
public sealed class GraphEvalCliTests
{
    static (int Rc, string Out, string Err) Capture(Func<int> run)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        using var o = new StringWriter();
        using var e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        try { return (run(), o.ToString(), e.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    [Fact]
    public void Graph_eval_reports_and_writes_priors_that_the_links_are_rebuilt_with()
    {
        using var ix = new TestIndex();
        var core = ix.Repo(1, "g/core");
        void Add(long repo, string file, string content) => Writes.ReplaceDecls(ix.Conn, repo, ix.File(repo, file, content, "msbuild"), Links.Extract(file, "msbuild", content));
        Add(core, "Core.csproj", "<Project><PropertyGroup><PackageId>Acme.Core</PackageId></PropertyGroup></Project>");
        for (var i = 10; i < 16; i++)
            Add(ix.Repo(i, $"g/app{i}"), $"App{i}.csproj", "<Project><ItemGroup><PackageReference Include=\"Acme.Core\" /></ItemGroup></Project>");
        Graph.RebuildLinks(ix.Conn);
        var cfg = Path.Combine(ix.Root, "config.yaml");
        File.WriteAllText(cfg, $"gitlab:\n  url: https://gl.test\n  token: t\nindex:\n  data_dir: {ix.Root}\n  db_path: {ix.DbPath}\n");
        var truth = Path.Combine(ix.Root, "truth.txt");
        File.WriteAllText(truth, string.Join("\n", Enumerable.Range(10, 6).Select(i => i < 12 ? $"g/app{i} -> g/core" : $"g/app{i} !-> g/core")));

        var (rc, stdout, _) = Capture(() => Program.Run(["graph-eval", "--config", cfg, "--truth", truth]));
        Assert.Equal(0, rc);
        Assert.Contains("links walked 6: 2 right, 4 wrong, 0 missed", stdout, StringComparison.Ordinal);
        Assert.Contains("g/app12 -> g/core", stdout, StringComparison.Ordinal);
        Assert.Contains("(owners say no)", stdout, StringComparison.Ordinal);

        (rc, _, var stderr) = Capture(() => Program.Run(["graph-eval", "--config", cfg, "--truth", truth, "--write-priors", "--apply"]));
        Assert.Equal(0, rc);
        Assert.Contains("1 calibrated priors written", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(ix.Root, LinkKinds.PriorsFile)));
        // (2 + 4 × 0.97) / (6 + 4) = 0.59: likely, no longer strong.
        Assert.All(Sql.Query(ix.Conn, "SELECT tier FROM repo_edges"), e => Assert.Equal("likely", e.Str("tier")));
    }
}
