using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Large files go to the model as their outline, and it reads the part it needs; grep keeps to .gitignore outside git too.</summary>
public sealed class LargeFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("arena-large-").FullName;
    private readonly StringWriter _out = new();

    private ToolContext Context() => new()
    {
        Workspace = new Workspace(_root),
        Ui = new Ui(new StringReader(""), _out, _out, color: false, canAsk: false),
    };

    private Task<ToolResult> Run(string tool, object args) =>
        LocalTools.All().Single(t => t.Name == tool).Run(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject(), Context(), CancellationToken.None);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A Go file of <paramref name="funcs"/> functions of 20 lines each (Go has no scanner of its own).</summary>
    private static string Go(int funcs)
    {
        var sb = new StringBuilder("package shop\n\nimport \"fmt\"\n\n");
        for (var f = 0; f < funcs; f++)
        {
            sb.Append($"func Handle{f}(w Writer, r *Request) error {{\n");
            for (var i = 0; i < 18; i++)
            {
                sb.Append($"\tfmt.Println(\"handler {f} step {i}\")\n");
            }
            sb.Append("}\n\n");
        }
        return sb.ToString();
    }

    [Fact]
    public async Task A_large_file_of_a_language_without_a_scanner_is_outlined_by_its_declarations_and_read_by_one()
    {
        Write("shop.go", Go(40));
        var outline = await Run("read_file", new { path = "shop.go" });
        Assert.Contains("func Handle7(w Writer, r *Request) error {  [", outline.Text);
        Assert.DoesNotContain("fmt.Println(\"handler 7", outline.Text);
        Assert.StartsWith("outline", outline.Display);

        var one = await Run("read_file", new { path = "shop.go", symbol = "Handle7" });
        Assert.Contains("handler 7 step 17", one.Text);
        Assert.DoesNotContain("handler 8 step", one.Text);
        Assert.DoesNotContain("handler 6 step", one.Text);

        var none = await Assert.ThrowsAsync<ToolError>(() => Run("read_file", new { path = "shop.go", symbol = "Nothing" }));
        Assert.Contains("has no symbol Nothing", none.Message);
        // A small file is read whole, as before.
        Write("small.go", Go(2));
        Assert.Contains("handler 1 step 17", (await Run("read_file", new { path = "small.go" })).Text);
    }

    [Fact]
    public async Task A_name_of_several_symbols_lists_them_to_choose_from()
    {
        var sb = new StringBuilder("namespace App;\n\n");
        foreach (var type in new[] { "Orders", "Invoices" })
        {
            sb.Append($"public sealed class {type}\n{{\n    public void Save()\n    {{\n        var x = 1;\n    }}\n}}\n\n");
        }
        Write("Both.cs", sb.ToString());
        var both = await Assert.ThrowsAsync<ToolError>(() => Run("read_file", new { path = "Both.cs", symbol = "Save" }));
        Assert.Contains("Save is 2 symbols", both.Message);
        Assert.Contains("App.Orders.Save", both.Message);
        var one = await Run("read_file", new { path = "Both.cs", symbol = "Invoices.Save" });
        Assert.StartsWith("App.Invoices.Save (method), lines 13-16:\n", one.Text);
    }

    [Fact]
    public void A_large_file_named_with_at_goes_as_its_outline_and_a_range_of_it_as_its_lines()
    {
        Write("shop.go", Go(40));
        var ws = new Workspace(_root);
        var (text, files) = Mentions.Expand("Why does @shop.go fail?", ws);
        Assert.Equal(["shop.go"], files);
        Assert.Contains("<file path=\"shop.go\" outline=\"true\">", text);
        Assert.Contains("func Handle39(w Writer", text);
        Assert.True(text.Length < 8_000, text.Length.ToString());
        Assert.Equal(["shop.go"], Mentions.Attached(text));

        var (range, _) = Mentions.Expand("Why @shop.go:30-31?", ws);
        Assert.Contains("handler 1 step 6", range);
    }

    [Fact]
    public async Task Grep_outside_git_keeps_to_each_folders_gitignore_with_its_negations()
    {
        Write(".gitignore", "*.log\n/build\ncache/\n!keep.log\n");
        Write("a.txt", "needle\n");
        Write("x.log", "needle\n");
        Write("keep.log", "needle\n");
        Write("build/out.txt", "needle\n");
        Write("src/build/kept.txt", "needle\n");
        Write("src/cache/no.txt", "needle\n");
        Write("src/lib/.gitignore", "gen.txt\n");
        Write("src/lib/gen.txt", "needle\n");
        Write("src/lib/own.txt", "needle\n");
        var found = await Run("grep", new { pattern = "needle", output = "files" });
        Assert.Equal(["a.txt", "keep.log", "src/build/kept.txt", "src/lib/own.txt"], found.Text.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Grep_with_a_lookaround_still_works_and_context_lines_join_up()
    {
        Write("a.cs", "one\nfoo bar\ntwo\nthree\nfoo baz\nfour\n\nfive\nsix\nfoo end\n");
        var look = await Run("grep", new { pattern = "foo (?!bar)" });
        Assert.Equal("a.cs:5: foo baz\na.cs:10: foo end", look.Text);
        var context = await Run("grep", new { pattern = "foo", context = 1 });
        Assert.Equal("a.cs:1- one\na.cs:2: foo bar\na.cs:3- two\na.cs:4- three\na.cs:5: foo baz\na.cs:6- four\n--\na.cs:9- six\na.cs:10: foo end", context.Text);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
