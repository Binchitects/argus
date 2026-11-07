using System.Text.Json.Nodes;
using Argus.Indexing;
using System.Text.Json;
using Argus.Server;
using Argus.Store;
using Argus.Util;

namespace Argus.Tests;

/// <summary>The graph tools on the link model: what they walk by confidence, layer and scope, and the surest ways.</summary>
public sealed class GraphQueryTests
{
    /// <summary>One way one repository uses another, written as RebuildLinks would, with its pair's edge for its layer.</summary>
    static void Link(TestIndex ix, long from, long to, string kind, double confidence, string layer = LinkKinds.Build, string scope = "main", string name = "x",
        string tier = "", string? candidates = null)
    {
        tier = tier.Length > 0 ? tier : LinkKinds.Tier(confidence);
        var evidence = new JsonObject
        {
            ["why"] = "test",
            ["uses"] = new JsonArray(new JsonObject { ["path"] = "src/a.cs", ["line"] = 3 }),
            ["candidates"] = candidates is null ? null : new JsonArray([.. candidates.Split(',').Select(c => (JsonNode)c)]),
        };
        Sql.Exec(ix.Conn,
            "INSERT INTO repo_links (from_repo_id, to_repo_id, kind, name, layer, scope, how, providers, confidence, tier, files, evidence) VALUES (?, ?, ?, ?, ?, ?, 'exact', 1, ?, ?, 2, ?)",
            from, to, kind, name, layer, scope, confidence, tier, evidence.ToJsonString());
        if (tier == "candidate") return;
        Sql.Exec(ix.Conn,
            "INSERT INTO repo_edges (from_repo_id, to_repo_id, layer, scope, confidence, tier, files, kinds) VALUES (?, ?, ?, ?, ?, ?, 2, ?)" +
            " ON CONFLICT (from_repo_id, to_repo_id, layer) DO UPDATE SET confidence = 1 - (1 - confidence) * (1 - excluded.confidence)",
            from, to, layer, scope, confidence, tier, kind);
    }

    static List<long> All(TestIndex ix) => [.. Sql.Query(ix.Conn, "SELECT id FROM repos").Select(r => r.Long("id"))];

    [Fact]
    public void Layers_hubs_and_cycles_are_of_build_links_unless_layers_are_asked_for()
    {
        using var ix = new TestIndex();
        var template = ix.Repo(1, "devops/ci-templates");
        var lib = ix.Repo(2, "core/lib");
        var apps = Enumerable.Range(0, 4).Select(i => ix.Repo(10 + i, $"apps/app{i}")).ToList();
        foreach (var app in apps)
        {
            Link(ix, app, template, "ci:include", 0.97, LinkKinds.Ci);
            Link(ix, app, lib, "package:nuget", 0.97);
        }
        // The template's own pipeline builds the library: a CI cycle, not a build one.
        Link(ix, template, lib, "ci:include", 0.97, LinkKinds.Ci);
        Link(ix, lib, template, "ci:include", 0.97, LinkKinds.Ci);

        var map = GraphQueries.SystemMap(All(ix), ix.Conn);
        Assert.Equal("core/lib", map["hubs"]![0]!["repo"]!.ToString());
        Assert.Single(map["hubs"]!.AsArray());
        Assert.Empty(map["cycles"]!.AsArray());
        Assert.Equal(10, map["links"]!.GetValue<int>());

        var ci = GraphQueries.SystemMap(All(ix), ix.Conn, filter: GraphQueries.GraphFilter.Parse(null, "ci"));
        Assert.Equal("devops/ci-templates", ci["hubs"]![0]!["repo"]!.ToString());
        Assert.Single(ci["cycles"]!.AsArray());
        Assert.Equal(6, ci["links"]!.GetValue<int>());
        Assert.Equal(4, ci["hidden_links"]!.GetValue<int>());
    }

    [Fact]
    public void The_surest_way_beats_the_fewest_steps_and_a_weak_one_is_marked()
    {
        using var ix = new TestIndex();
        var a = ix.Repo(1, "g/a");
        var b = ix.Repo(2, "g/b");
        var c = ix.Repo(3, "g/c");
        var d = ix.Repo(4, "g/d");
        var e = ix.Repo(5, "g/e");
        Link(ix, a, d, "import:python", 0.6);
        Link(ix, a, b, "package:pypi", 0.97);
        Link(ix, b, c, "package:pypi", 0.97);
        Link(ix, c, d, "package:pypi", 0.97);
        Link(ix, d, e, "import:python", 0.3);

        var path = GraphQueries.DependencyPath(All(ix), ix.Conn, "g/a", "g/d");
        var paths = path["paths"]!.AsArray();
        Assert.Equal(2, paths.Count);
        Assert.Equal(3, paths[0]!["length"]!.GetValue<int>());
        Assert.Equal(Math.Round(0.97 * 0.97 * 0.97, 4), paths[0]!["confidence"]!.GetValue<double>());
        Assert.Equal(1, paths[1]!["length"]!.GetValue<int>());
        Assert.Null(path["weak"]);

        // Only a weak link reaches e: it is found, and marked weak.
        var weak = GraphQueries.DependencyPath(All(ix), ix.Conn, "g/d", "g/e");
        Assert.True(weak["weak"]!.GetValue<bool>());
        Assert.Equal("g/d -> g/e 0.30", weak["paths"]![0]!["weakest"]!.ToString());
        Assert.Empty(GraphQueries.RepoMap(All(ix), ix.Conn, d)["depends_on"]!.AsArray());
    }

    [Fact]
    public void A_dependent_is_reached_through_its_surest_parent_and_listed_surest_first()
    {
        using var ix = new TestIndex();
        var lib = ix.Repo(1, "core/lib");
        var strong = ix.Repo(2, "core/strong");
        var likely = ix.Repo(3, "core/likely");
        var app = ix.Repo(4, "apps/app");
        Link(ix, strong, lib, "package:nuget", 0.97);
        Link(ix, likely, lib, "import:csharp", 0.6);
        Link(ix, app, likely, "package:nuget", 0.97);
        Link(ix, app, strong, "import:csharp", 0.7);

        var impact = GraphQueries.ChangeImpact(All(ix), ix.Conn, "core/lib");
        var first = impact["by_depth"]![0]!["repos"]!.AsArray();
        Assert.Equal(["core/strong", "core/likely"], first.Select(r => r!["repo"]!.ToString()));
        var second = impact["by_depth"]![1]!["repos"]![0]!;
        Assert.Equal(("apps/app", "core/strong"), (second["repo"]!.ToString(), second["through"]!.ToString()));
        Assert.Equal(Math.Round(0.97 * 0.7, 4), second["confidence"]!.GetValue<double>());
    }

    [Fact]
    public void A_name_several_provide_is_a_possible_link_not_a_link_and_tests_only_links_are_asked_for()
    {
        using var ix = new TestIndex();
        var a = ix.Repo(1, "g/a");
        var b = ix.Repo(2, "g/b");
        var user = ix.Repo(3, "g/user");
        var hidden = ix.Repo(4, "secret/c");
        Link(ix, user, a, "import:csharp", 0.425, name: "Acme.Shared", tier: "candidate", candidates: "g/a,g/b,secret/c");
        Link(ix, user, b, "import:csharp", 0.425, name: "Acme.Shared", tier: "candidate", candidates: "g/a,g/b,secret/c");
        Link(ix, user, hidden, "import:csharp", 0.425, name: "Acme.Shared", tier: "candidate", candidates: "g/a,g/b,secret/c");
        Link(ix, user, b, "import:csharp", 0.85, scope: "test", name: "Acme.Testing");

        var readable = new List<long> { a, b, user };
        var map = GraphQueries.RepoMap(readable, ix.Conn, user);
        Assert.Empty(map["depends_on"]!.AsArray());
        var possible = map["possible"]!.AsArray();
        Assert.Equal(2, possible.Count);
        // Only what the caller may read is named among the candidates.
        Assert.Equal(["g/a", "g/b"], possible[0]!["candidates"]!.AsArray().Select(n => n!.ToString()));

        var withTests = GraphQueries.RepoMap(readable, ix.Conn, user, filter: GraphQueries.GraphFilter.Parse(null, null, includeTests: true));
        Assert.Equal("g/b", withTests["depends_on"]![0]!["path_with_namespace"]!.ToString());
        Assert.Equal("test", withTests["depends_on"]![0]!["by"]!["import:csharp"]!["scope"]!.ToString());
    }

    [Fact]
    public void A_branch_is_answered_by_its_default_branch_and_is_never_a_dependent()
    {
        using var ix = new TestIndex();
        var lib = ix.Repo(1, "core/lib");
        var app = ix.Repo(2, "apps/app");
        var feature = ix.Repo(2, "apps/app", branch: "feature/x");
        Link(ix, app, lib, "package:nuget", 0.97);
        // A branch's row is never a link's source, even if one is written.
        Link(ix, feature, lib, "package:nuget", 0.97);

        var impact = GraphQueries.ChangeImpact(All(ix), ix.Conn, "core/lib");
        Assert.Equal(1, impact["dependents"]!.GetValue<int>());
        var path = GraphQueries.DependencyPath(All(ix), ix.Conn, "apps/app@feature/x", "core/lib");
        Assert.Contains("default branches", path["note"]!.ToString());
        Assert.Equal("apps/app", path["from"]!.ToString());
    }

    [Fact]
    public void The_filter_takes_layers_and_tests_and_rejects_anything_else()
    {
        var f = GraphQueries.GraphFilter.Parse(0.85, "build, ci test");
        Assert.Equal((0.85, true, true), (f.MinConfidence, f.IncludeTests, f.LayersGiven));
        Assert.Equal(["build", "ci"], f.Layers.Order());
        Assert.Equal(6, GraphQueries.GraphFilter.Parse(null, "all").Layers.Count);
        Assert.DoesNotContain("history", GraphQueries.GraphFilter.Parse(null, null).Layers);
        var error = Assert.Throws<QueryError>(() => GraphQueries.GraphFilter.Parse(null, "build,runtim"));
        Assert.Contains("'runtim'", error.Message);
        Assert.Throws<QueryError>(() => GraphQueries.GraphFilter.Parse(1.5, null));
    }

    [Fact]
    public void The_graph_tools_take_a_filter_and_coerce_it_as_pydantic_does()
    {
        foreach (var name in new[] { "repo_map", "system_map", "dependency_path", "change_impact" })
        {
            var properties = ToolCatalog.Specs.Single(s => s.Name == name).InputSchema["properties"]!.AsObject();
            Assert.Contains("min_confidence", properties.Select(p => p.Key));
            Assert.Contains("layers", properties.Select(p => p.Key));
            Assert.Equal("boolean", properties["include_tests"]!["type"]!.ToString());
        }
        var spec = ToolCatalog.Specs.Single(s => s.Name == "repo_map");
        Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        var a = ToolRuntime.Validate(spec, Args("{\"repo_id\": 1, \"min_confidence\": \"0.85\", \"include_tests\": \"yes\"}"));
        Assert.Equal((0.85, true), (a.OptDouble("min_confidence"), a.Bool("include_tests")));
        var defaults = ToolRuntime.Validate(spec, Args("{\"repo_id\": 1}"));
        Assert.Equal((null, false), (defaults.OptDouble("min_confidence"), defaults.Bool("include_tests")));
        Assert.Equal(1.0, ToolRuntime.Validate(spec, Args("{\"repo_id\": 1, \"min_confidence\": 1}")).OptDouble("min_confidence"));

        var number = Assert.Throws<ToolError>(() => ToolRuntime.Validate(spec, Args("{\"repo_id\": 1, \"min_confidence\": \"high\"}")));
        Assert.Contains("Input should be a valid number, unable to parse string as a number [type=float_parsing, input_value='high', input_type=str]", number.Message);
        var boolean = Assert.Throws<ToolError>(() => ToolRuntime.Validate(spec, Args("{\"repo_id\": 1, \"include_tests\": \"maybe\"}")));
        Assert.Contains("Input should be a valid boolean, unable to interpret input [type=bool_parsing, input_value='maybe', input_type=str]", boolean.Message);
    }

    // ---- access in SQL: what a person who may read some repositories gets is what an index of only those gives ----

    static readonly (long From, long To, string Kind, double Confidence, string Layer, string Tier, string? Candidates)[] Estate5 =
    [
        (1, 2, "package:nuget", 0.97, LinkKinds.Build, "", null),
        (2, 3, "package:nuget", 0.9, LinkKinds.Build, "", null),
        (1, 3, "ci:include", 0.95, LinkKinds.Ci, "", null),
        (3, 4, "package:npm", 0.6, LinkKinds.Build, "", null),
        (4, 5, "runtime:host", 0.88, LinkKinds.Runtime, "", null),
        (5, 1, "package:nuget", 0.7, LinkKinds.Build, "", null),
        (2, 4, "package:nuget", 0.5, LinkKinds.Build, "candidate", "g/r4,g/r5"),
    ];

    /// <summary>An index of only these of the five repositories, each under its own row id, with the links among them.</summary>
    static TestIndex Only(IReadOnlyCollection<long> repos)
    {
        var ix = new TestIndex();
        foreach (var id in repos.Order())
            Sql.Exec(ix.Conn, "INSERT INTO repos (id, gitlab_id, path_with_namespace, default_branch, branch, http_url) VALUES (?, ?, ?, 'main', 'main', 'u')",
                id, 100 + id, $"g/r{id}");
        foreach (var l in Estate5.Where(l => repos.Contains(l.From) && repos.Contains(l.To)))
            Link(ix, l.From, l.To, l.Kind, l.Confidence, l.Layer, tier: l.Tier, candidates: l.Candidates);
        return ix;
    }

    static string Answers(IReadOnlyList<long> allowed, TestIndex ix)
    {
        var parts = new List<string> { GraphQueries.SystemMap(allowed, ix.Conn).ToJsonString() };
        foreach (var id in allowed)
        {
            parts.Add(GraphQueries.RepoMap(allowed, ix.Conn, id, depth: 3).ToJsonString());
            parts.Add(GraphQueries.ChangeImpact(allowed, ix.Conn, $"g/r{id}").ToJsonString());
            foreach (var to in allowed.Where(t => t != id))
                parts.Add(GraphQueries.DependencyPath(allowed, ix.Conn, $"g/r{id}", $"g/r{to}").ToJsonString());
        }
        return string.Join("\n", parts);
    }

    [Fact]
    public void Every_graph_answer_for_some_repositories_is_the_answer_of_an_index_without_the_others()
    {
        long[] five = [1, 2, 3, 4, 5];
        using var full = Only(five);
        // Every allowlist of the five (31 of them): nothing of a repository left out changes a byte of any answer.
        for (var mask = 1; mask < 32; mask++)
        {
            var allowed = five.Where((_, i) => (mask & (1 << i)) != 0).ToList();
            using var only = Only(allowed);
            Assert.Equal(Answers(allowed, only), Answers(allowed, full));
        }
    }

    [Fact]
    public void A_path_through_a_repository_the_person_cannot_read_does_not_exist_for_them()
    {
        using var ix = Only([1, 2, 3]);
        // 1 -> 2 -> 3 is the only way from 1 to 3 by build links; without 2 there is none.
        var path = GraphQueries.DependencyPath([1, 3], ix.Conn, "g/r1", "g/r3", GraphQueries.GraphFilter.Parse(null, "build"));
        Assert.DoesNotContain("g/r2", path.ToJsonString());
    }

    [Fact]
    public void Semantic_search_answers_a_person_who_reads_little_as_fully_as_one_who_reads_everything()
    {
        using var ix = new TestIndex();
        var mine = ix.Repo(1, "g/mine");
        var others = ix.Repo(2, "g/others");
        var mineFile = ix.File(mine, "m.c", "");
        var othersFile = ix.File(others, "o.c", "");
        Writes.ReplaceSymbols(ix.Conn, mine, mineFile,
            [.. Enumerable.Range(0, 5).Select(i => new Writes.SymbolRow($"Mine{i}", "function", i + 1, i + 1, "()", null, 1, null))], "2:x");
        // More of others' code than the coarse pass has places, all nearer the question than anything of hers.
        Writes.ReplaceSymbols(ix.Conn, others, othersFile,
            [.. Enumerable.Range(0, Queries.SemanticCoarse + 100).Select(i => new Writes.SymbolRow($"Other{i}", "function", i + 1, i + 1, "()", null, 1, null))], "2:x");
        var dim = Embed.Dim;
        double[] Vector(bool near, int seed)
        {
            var v = new double[dim];
            for (var d = 0; d < dim; d++) v[d] = near ? 1 + seed % 7 * 0.001 : (d % 2 == 0 ? 1 : -1) * (1 + seed * 0.01);
            return v;
        }
        Semantic.BuildSymbolEmbeddings(ix.Conn, texts => [.. texts.Select((t, i) => Vector(t.Contains("Other"), i))]);
        var question = Enumerable.Repeat(1.0, dim).ToList();

        var hers = Queries.SemanticSearch([mine], ix.Conn, question, limit: 5);
        Assert.Equal(5, hers.Count);
        Assert.All(hers, h => Assert.StartsWith("Mine", h["name"]!.ToString()));
        Assert.All(Queries.SemanticSearch([mine, others], ix.Conn, question, limit: 5), h => Assert.StartsWith("Other", h["name"]!.ToString()));
    }

    [Fact]
    public void Every_read_of_the_repository_graph_puts_the_allowlist_in_its_sql()
    {
        // A read of the graph's tables without the allowlist in the same statement would load what the person may not
        // read and leave it to C# to drop: each statement naming one must also carry the predicate, on both ends.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var source = File.ReadAllText(Path.Combine(root, "src/Argus/Store/GraphQueries.cs"));
        foreach (var table in new[] { "repo_edges", "repo_links" })
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, $@"FROM {table}[^\n]*(\n[^\n]*){{0,1}}"))
                Assert.True(System.Text.RegularExpressions.Regex.Matches(m.Value, "InAllowed").Count >= 2, $"{table} read without the allowlist on both ends: {m.Value}");
        Assert.Contains("FROM repos WHERE id {Queries.InAllowed}", source);
    }
}
