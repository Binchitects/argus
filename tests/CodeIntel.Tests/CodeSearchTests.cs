using System.Text.Json.Nodes;
using CodeIntel.Store;

namespace CodeIntel.Tests;

/// <summary>
/// The wiki's code search on a fixture estate: a name with its scope found first, any way it is written; the files that
/// hold the words ranked after it; every match reachable a page at a time; and the filters with their facets.
/// </summary>
public class CodeSearchTests : IDisposable
{
    readonly TestIndex _ix = new();
    readonly List<long> _all = [];
    readonly long _mylib, _other, _py;

    public CodeSearchTests()
    {
        _mylib = Repo(1, "team/mylib");
        _other = Repo(2, "team/other");
        _py = Repo(3, "data/pytools");

        var cpp = _ix.File(_mylib, "src/mylib.cpp", "#include \"mylib.h\"\nnamespace MyLib {\nint TestFunc(int a) {\n  return a + 1;\n}\nint TestFuncHelper() { return 0; }\n}\n", "cpp");
        Symbols(_mylib, cpp, ("TestFunc", "MyLib", 3), ("TestFuncHelper", "MyLib", 6));
        _ix.File(_mylib, "src/mylib.h", "namespace MyLib {\nint TestFunc(int a);\n}\n", "cpp");
        _ix.File(_mylib, "README.md", "# MyLib\n\nThe library.\n\nCall MyLib::TestFunc to add one.\n", "markdown");
        var other = _ix.File(_other, "src/other.cpp", "namespace Other {\nint TestFunc() { return 2; }\n}\n", "cpp");
        Symbols(_other, other, ("TestFunc", "Other", 2));
        var py = _ix.File(_py, "pytools/a/b.py", "class b:\n    def c(self):\n        return test_func()\n\ndef test_func():\n    return 1\n", "python");
        Symbols(_py, py, ("c", "a.b", 2), ("test_func", null, 5));
        // Forty-five files that only say "widget": more than two pages of them.
        for (int i = 0; i < 45; i++)
            _ix.File(_other, $"ui/widget{i:00}.c", $"/* widget {i} */\nint draw_{i}(void) {{ return {i}; }}\n", "c");
    }

    public void Dispose() => _ix.Dispose();

    long Repo(long gitlabId, string path)
    {
        var id = _ix.Repo(gitlabId, path);
        _all.Add(id);
        return id;
    }

    void Symbols(long repo, long file, params (string Name, string? Scope, long Line)[] symbols) =>
        Writes.ReplaceSymbols(_ix.Conn, repo, file,
            [.. symbols.Select(s => new Writes.SymbolRow(s.Name, "function", s.Line, s.Line, "(int a)", s.Scope, 1, null))], "2:x");

    JsonObject Search(string q, CodeSearch.Filter? filter = null, int page = 1, int perPage = 20, IReadOnlyList<long>? allowed = null) =>
        CodeSearch.Run(allowed ?? _all, _ix.Conn, q, filter ?? new CodeSearch.Filter(), page, perPage);

    static List<JsonObject> Rows(JsonObject found) => [.. found["rows"]!.AsArray().OfType<JsonObject>()];
    static string Where(JsonObject r) => $"{r["repo"]}/{r["path"]}:{r["line"]}";

    [Theory]
    [InlineData("MyLib::TestFunc", "MyLib", "TestFunc")]
    [InlineData("MyLib.TestFunc", "MyLib", "TestFunc")]
    [InlineData("a.b.c", "a.b", "c")]
    [InlineData("obj->field", "obj", "field")]
    [InlineData("TestFunc", null, "TestFunc")]
    public void A_typed_name_is_its_last_part_and_what_qualifies_it(string token, string? qualifier, string last)
    {
        var name = CodeSearch.ParseName(token)!;
        Assert.Equal(last, name.Last);
        Assert.Equal(qualifier, name.Qualifier);
    }

    [Theory]
    [InlineData("TestFunc", "test func")]
    [InlineData("test_func", "test func")]
    [InlineData("HTTPServer2", "http server 2")]
    [InlineData("parse-frame-header", "parse frame header")]
    public void A_name_has_its_camel_and_snake_words(string name, string words) => Assert.Equal(words.Split(' '), CodeSearch.Words(name));

    [Fact]
    public void A_qualified_name_finds_its_definition_first_then_the_same_name_elsewhere_then_its_words()
    {
        // Before: the full-text index read "::" as its syntax and the whole search failed.
        var rows = Rows(Search("MyLib::TestFunc"));
        Assert.Equal(("symbol", "name and scope", "team/mylib/src/mylib.cpp:3"), (rows[0]["kind"]!.ToString(), rows[0]["how"]!.ToString(), Where(rows[0])));
        var other = rows.FindIndex(r => r["repo"]!.ToString() == "team/other" && r["kind"]!.ToString() == "symbol");
        var helper = rows.FindIndex(r => r["name"]?.ToString() == "TestFuncHelper");
        var readme = rows.FindIndex(r => r["path"]!.ToString() == "README.md");
        Assert.True(0 < other && other < helper && helper < readme, string.Join(", ", rows.Select(Where)));
        Assert.Equal("doc", rows[readme]["kind"]!.ToString());
        Assert.Equal(5, rows[readme]["line"]!.GetValue<long>());
        Assert.Contains("Call MyLib::TestFunc to add one.", rows[readme]["snippet"]!.ToString());
        // Never a file that does not hold it: no widget among them.
        Assert.DoesNotContain(rows, r => r["path"]!.ToString().StartsWith("ui/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dotted_name_any_case_and_its_words_all_find_the_symbol()
    {
        Assert.Equal("data/pytools/pytools/a/b.py:2", Where(Rows(Search("a.b.c"))[0]));
        Assert.Equal(("name, another case", "TestFunc"), (Rows(Search("testfunc"))[0]["how"]!.ToString(), Rows(Search("testfunc"))[0]["name"]!.ToString()));
        var words = Rows(Search("test func")).Where(r => r["kind"]!.ToString() == "symbol").Select(r => r["name"]!.ToString()).ToList();
        Assert.Contains("TestFunc", words);
        Assert.Contains("test_func", words);
        Assert.Contains("TestFuncHelper", words);
        Assert.Equal("same words", Rows(Search("TestFunc")).First(r => r["name"]?.ToString() == "test_func")["how"]!.ToString());
        Assert.Equal("start of a name", Rows(Search("TestF")).First(r => r["name"]?.ToString() == "TestFuncHelper")["how"]!.ToString());
    }

    [Fact]
    public void A_file_name_finds_the_file()
    {
        var first = Rows(Search("mylib.h"))[0];
        Assert.Equal(("file name", "team/mylib/src/mylib.h:1"), (first["how"]!.ToString(), Where(first)));
    }

    [Fact]
    public void Every_match_is_reached_a_page_at_a_time_each_once()
    {
        var first = Search("widget", perPage: 20);
        Assert.Equal(45, first["total"]!.GetValue<long>());
        var seen = new List<string>();
        for (int page = 1; page <= 3; page++) seen.AddRange(Rows(Search("widget", page: page, perPage: 20)).Select(Where));
        Assert.Equal(45, seen.Count);
        Assert.Equal(45, seen.Distinct().Count());
        Assert.Empty(Rows(Search("widget", page: 4, perPage: 20)));
        Assert.Equal(2, Rows(Search("widget", page: 2, perPage: 43)).Count);
    }

    [Fact]
    public void Filters_narrow_by_repository_language_path_and_kind_and_the_facets_say_where_the_matches_are()
    {
        var all = Search("TestFunc");
        var facets = all["facets"]!;
        Assert.Contains(facets["repos"]!.AsArray(), f => f!["repo"]!.ToString() == "team/other");
        Assert.Contains(facets["langs"]!.AsArray(), f => f!["lang"]!.ToString() == "python");
        Assert.True(facets["kinds"]!["symbol"]!.GetValue<long>() >= 4);
        Assert.Equal(1, facets["kinds"]!["doc"]!.GetValue<long>());

        Assert.All(Rows(Search("TestFunc", new(Repo: "team/other"))), r => Assert.Equal("team/other", r["repo"]!.ToString()));
        Assert.All(Rows(Search("TestFunc", new(Lang: "python"))), r => Assert.Equal("python", r["lang"]!.ToString()));
        Assert.All(Rows(Search("TestFunc", new(Path: "src/"))), r => Assert.StartsWith("src/", r["path"]!.ToString()));
        Assert.All(Rows(Search("widget", new(Path: "ui/widget1*"))), r => Assert.StartsWith("ui/widget1", r["path"]!.ToString()));
        var docs = Search("TestFunc", new(Kind: "doc"));
        Assert.Equal(["README.md"], Rows(docs).Select(r => r["path"]!.ToString()));
        Assert.Equal(1, docs["total"]!.GetValue<long>());
        // The kind picked leaves the other kinds' counts in the facets, so another can be picked.
        Assert.True(docs["facets"]!["kinds"]!["symbol"]!.GetValue<long>() >= 4);
        Assert.All(Rows(Search("TestFunc", new(Kind: "symbol"))), r => Assert.Equal("symbol", r["kind"]!.ToString()));
        Assert.All(Rows(Search("TestFunc", new(Kind: "code"))), r => Assert.Equal("code", r["kind"]!.ToString()));
    }

    [Fact]
    public void Only_the_repositories_allowed_are_searched_and_none_finds_nothing()
    {
        Assert.All(Rows(Search("TestFunc", allowed: [_other])), r => Assert.Equal("team/other", r["repo"]!.ToString()));
        Assert.Equal(0, Search("TestFunc", allowed: [])["total"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("\"unclosed")]
    [InlineData("a AND OR NOT")]
    [InlineData("NEAR(x y)")]
    [InlineData("::")]
    [InlineData("col:value *")]
    public void Punctuation_and_operators_are_words_never_syntax(string q) => Assert.NotNull(Search(q)["total"]);
}
