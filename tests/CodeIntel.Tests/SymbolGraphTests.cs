using System.Text.Json.Nodes;
using CodeIntel.Store;
using CodeIntel.Symbols;
using CodeIntel.Util;

namespace CodeIntel.Tests;

/// <summary>The symbol graph: uses tied to their definitions and types to their bases, built from a checkout, read only within the allowlist.</summary>
public sealed class SymbolGraphTests
{
    static readonly (string Path, string Text)[] Geo =
    [
        ("src/IShape.cs", "namespace Geo;\n\npublic interface IShape\n{\n    double Area();\n}\n"),
        ("src/Circle.cs", "namespace Geo;\n\npublic class Circle : IShape\n{\n    public double Area() => 3.14;\n}\n"),
        ("src/Square.cs", "namespace Geo;\n\npublic class Square : IShape\n{\n    public double Area() => 1.0;\n}\n"),
        ("src/Report.cs", "namespace Geo;\n\npublic static class Report\n{\n    public static double Total(IShape s) => s.Area();\n\n    // Circle.Area is not called here: a comment.\n    public static double Twice(Circle c) => c.Area() * 2;\n\n    public static double Both(Circle c) => Twice(c) + Total(c);\n}\n"),
        ("web/sum.ts", "export function add(a: number, b: number) { return a + b }\nexport function total(xs: number[]) { return xs.reduce((s, x) => add(s, x), 0) }\n"),
    ];

    static (TestIndex Ix, long Repo, long Other) Indexed()
    {
        var ix = new TestIndex();
        var repo = ix.Repo(11, "grp/geo");
        var other = ix.Repo(12, "grp/other");
        var tree = Path.Combine(ix.Root, "tree");
        foreach (var (path, text) in Geo)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tree, path))!);
            File.WriteAllText(Path.Combine(tree, path), text);
            ix.File(repo, path, text, path.EndsWith(".ts") ? "typescript" : "csharp");
        }
        Assert.StartsWith("symbol graph: ", SymbolGraph.Refresh(ix.Conn, repo, tree, "abc"));
        return (ix, repo, other);
    }

    static List<(string Symbol, string Evidence)> Results(JsonObject answer, int at = 0) =>
        [.. answer["answers"]![at]!["results"]!.AsArray().Select(r => (r!["symbol"]!.GetValue<string>(), r["evidence"]!.GetValue<string>()))];

    [Fact]
    public void Callers_are_the_uses_tied_to_the_definition_not_the_words()
    {
        var (ix, repo, _) = Indexed();
        using var _ix = ix;
        var circle = SymbolQueries.Walk([repo], ix.Conn, "Circle.Area", "callers");
        // Twice calls c.Area() on a Circle: by its declared type. The comment naming Circle.Area is not a use.
        Assert.Equal([("Geo.Report.Twice", "T")], Results(circle));
        Assert.Equal("src/Circle.cs", circle["answers"]![0]!["path"]!.GetValue<string>());
        // Two steps: who calls Twice.
        var further = SymbolQueries.Walk([repo], ix.Conn, "Circle.Area", "callers", depth: 2);
        Assert.Contains(("Geo.Report.Both", "S"), Results(further));
        Assert.Equal("Geo.Report.Twice", further["answers"]![0]!["results"]!.AsArray().Single(r => r!["symbol"]!.GetValue<string>() == "Geo.Report.Both")!["via"]!.GetValue<string>());
    }

    [Fact]
    public void Callees_implementations_and_bases()
    {
        var (ix, repo, _) = Indexed();
        using var _ix = ix;
        Assert.Contains("Geo.IShape.Area", Results(SymbolQueries.Walk([repo], ix.Conn, "Report.Total", "callees")).Select(r => r.Symbol));
        Assert.Equal(["Geo.Circle", "Geo.Square"], Results(SymbolQueries.Walk([repo], ix.Conn, "IShape", "implementations")).Select(r => r.Symbol).Order());
        Assert.Equal(["Geo.IShape"], Results(SymbolQueries.Walk([repo], ix.Conn, "Geo.Square", "bases")).Select(r => r.Symbol));
        // TypeScript too: total calls add.
        Assert.Equal(["total"], Results(SymbolQueries.Walk([repo], ix.Conn, "add", "callers")).Select(r => r.Symbol));
        // Area names three definitions; those in the graph (used, or using something) are answered each on its own:
        // Square.Area is neither, so nothing calls it.
        Assert.Equal(2, SymbolQueries.Walk([repo], ix.Conn, "Area", "callers")["definitions"]!.GetValue<int>());
        Assert.Contains("nothing uses", SymbolQueries.Walk([repo], ix.Conn, "Square.Area", "callers")["note"]!.GetValue<string>());
        Assert.Throws<QueryError>(() => SymbolQueries.Walk([repo], ix.Conn, "Area", "friends"));
    }

    [Fact]
    public void Someone_who_may_not_read_the_repository_gets_nothing_of_it()
    {
        var (ix, repo, other) = Indexed();
        using var _ix = ix;
        var answer = SymbolQueries.Walk([other], ix.Conn, "Circle.Area", "callers");
        Assert.Empty(answer["answers"]!.AsArray());
        Assert.Equal(0, answer["definitions"]!.GetValue<int>());
        Assert.Empty(SymbolQueries.Walk([], ix.Conn, "IShape", "implementations")["answers"]!.AsArray());
    }

    [Fact]
    public void It_is_built_once_per_commit_and_again_for_a_new_one_or_when_turned_off_not_at_all()
    {
        var (ix, repo, _) = Indexed();
        using var _ix = ix;
        var tree = Path.Combine(ix.Root, "tree");
        Assert.True(SymbolGraph.IsCurrent(ix.Conn, repo, "abc"));
        Assert.Null(SymbolGraph.Refresh(ix.Conn, repo, tree, "abc"));
        Assert.NotNull(SymbolGraph.Refresh(ix.Conn, repo, tree, "def"));
        using (new EnvScope(("CODE_INTEL_SYMBOL_GRAPH", "0")))
        {
            Assert.Null(SymbolGraph.Refresh(ix.Conn, repo, tree, "ghi"));
        }
        // Taken out with its repository.
        Writes.DeleteRepos(ix.Conn, [repo]);
        Assert.Equal(0L, Convert.ToInt64(Sql.Scalar(ix.Conn, "SELECT COUNT(*) FROM sym_edges")));
    }
}
