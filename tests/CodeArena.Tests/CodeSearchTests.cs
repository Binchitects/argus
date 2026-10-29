using System.Text.RegularExpressions;

namespace CodeArena.Tests;

/// <summary>The code index's answers: by name complete for code, by words ranked; cited lines as on disk, within the budget.</summary>
public sealed partial class CodeSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("code-search-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("code-search-data-").FullName;

    public CodeSearchTests()
    {
        Write("src/Shop/Payments.cs", """
            namespace Shop;

            /// <summary>Takes payments from cards.</summary>
            public sealed class Payments
            {
                // Charge is called by the cart; "Charge" in a string is not a use.
                public void Charge(decimal amount)
                {
                    if (amount <= 0)
                    {
                        throw new ArgumentException("A Charge needs an amount above zero.");
                    }
                    Record(amount);
                }

                /// <summary>Writes the payment to the ledger.</summary>
                private void Record(decimal amount) { }

                /// <summary>Gives the money back to the card.</summary>
                public void Refund(decimal amount)
                {
                    Record(-amount);
                }
            }
            """);
        Write("src/Shop/Cart.cs", "namespace Shop;\n\npublic sealed class Cart\n{\n    private readonly Payments payments = new();\n\n    public void CheckOut()\n    {\n"
            + string.Concat(Enumerable.Range(0, 30).Select(i => $"        payments.Charge({i});\n")) + "        // Charge again later.\n    }\n}\n");
        Write("tools/bill.py", "def bill(p):\n    # Charge it\n    p.Charge(1)\n");
        Write("src/web/cart.ts", "// Charge happens on the server.\nexport function total(items: number[]) {\n  return items.reduce((a, b) => a + b, 0)\n}\n");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_data, recursive: true);
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private CodeIndex Index()
    {
        var index = new CodeIndex(_root, CodeIndex.FileFor(_data, _root));
        index.Sync(new Workspace(_root), [_root], CancellationToken.None, threads: 2);
        return index;
    }

    [GeneratedRegex(@"^\s*(\d+)\t(.*)$")]
    private static partial Regex NumberedLine();

    /// <summary>Every numbered line of an answer, with the path of the place it is under.</summary>
    private static List<(string Path, int Line, string Text)> Cited(string answer)
    {
        var cited = new List<(string, int, string)>();
        string? path = null;
        foreach (var line in answer.Split('\n'))
        {
            if (Regex.Match(line, @"^\[\d+\] ([^:]+):\d+-\d+") is { Success: true } head)
            {
                path = head.Groups[1].Value;
            }
            else if (path is not null && NumberedLine().Match(line) is { Success: true } m)
            {
                cited.Add((path, int.Parse(m.Groups[1].Value), m.Groups[2].Value));
            }
        }
        return cited;
    }

    [Fact]
    public void A_question_goes_by_name_only_when_it_names_code_and_asks_nothing_else()
    {
        var search = Index().Search();
        Assert.Equal(["Charge"], search.Names("where is Charge defined"));
        Assert.Equal(["Payments.Charge"], search.Names("callers of Payments.Charge()"));
        Assert.Equal(["check_out", "CheckOut"], search.Names("`check_out` CheckOut"));
        Assert.Null(search.Names("where are payments refunded"));
        // A known name with other words: by words.
        Assert.Null(search.Names("Payments ledger"));
    }

    [Fact]
    public void By_name_every_use_in_code_is_given_as_grep_would_find_it_outside_comments_and_strings()
    {
        var answer = Index().Search().Answer(new CodeQuery("Charge") { Budget = 4000 });
        Assert.Equal("name", answer.Route);
        Assert.True(answer.Complete);
        Assert.Contains("complete for code (comments and strings not counted)", answer.Text, StringComparison.Ordinal);

        // grep -w Charge over each file's code: its comments and strings blanked.
        var expected = new HashSet<(string, int)>();
        foreach (var path in new[] { "src/Shop/Payments.cs", "src/Shop/Cart.cs", "tools/bill.py", "src/web/cart.ts" })
        {
            var code = Argus.Util.CodeText.Blank(CodeFiles.LanguageOf(path), File.ReadAllText(Path.Combine(_root, path))).Split('\n');
            for (var i = 0; i < code.Length; i++)
            {
                if (Regex.IsMatch(code[i], @"\bCharge\b"))
                {
                    expected.Add((path, i + 1));
                }
            }
        }
        var given = answer.Items.Select(h => (h.Path, h.Kind == "use" ? h.Start : NameLine(h.Path, h.Start, "Charge"))).ToHashSet();
        Assert.Equal(expected.Order(), given.Order());
        Assert.Contains("- src/Shop/Cart.cs (30): Cart.CheckOut :9 (×30)", answer.Text, StringComparison.Ordinal);
    }

    private int NameLine(string path, int from, string name) =>
        File.ReadAllLines(Path.Combine(_root, path)).Select((l, i) => (l, i)).First(x => x.i + 1 >= from && x.l.Contains(name, StringComparison.Ordinal)).i + 1;

    [Fact]
    public void A_name_answer_is_smaller_than_grep_n_w_for_a_name_used_often()
    {
        var answer = Index().Search().Answer(new CodeQuery("Charge"));
        var grep = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i)))
            .Where(x => Regex.IsMatch(x.l, @"\bCharge\b")).Sum(x => $"{Path.GetRelativePath(_root, x.f)}:{x.i + 1}:{x.l}\n".Length);
        Assert.True(answer.Text.Length < grep, $"{answer.Text.Length} >= {grep}");
    }

    [Fact]
    public void By_words_the_answer_is_the_same_each_time_cites_lines_as_on_disk_and_holds_its_budget()
    {
        var index = Index();
        var first = index.Search().Answer(new CodeQuery("payment refund to the card"));
        Assert.Equal("words", first.Route);
        Assert.Equal("src/Shop/Payments.cs", first.Items[0].Path);
        Assert.Equal("Payments.Refund", first.Items[0].Name);
        Assert.Equal(first.Text, Index().Search().Answer(new CodeQuery("payment refund to the card")).Text);

        foreach (var (path, line, text) in Cited(first.Text))
        {
            var disk = File.ReadAllLines(Path.Combine(_root, path))[line - 1];
            Assert.Equal(disk.Length > 300 ? disk[..300] + "…" : disk, text);
        }
        foreach (var budget in new[] { 300, 600, 1200 })
        {
            var q = new CodeQuery("payment charge refund cart record ledger") { Budget = budget };
            Assert.True(index.Search().Answer(q).Text.Length <= budget * q.CharsPerToken);
        }
    }

    [Fact]
    public void A_file_edited_after_the_build_is_read_again_before_it_is_cited()
    {
        var index = Index();
        var path = Path.Combine(_root, "src/Shop/Payments.cs");
        File.WriteAllText(path, "// Moved down.\n\n\n\n\n" + File.ReadAllText(path));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var answer = index.Search().Answer(new CodeQuery("refund to the card"));
        var refund = answer.Items.First(h => h.Name == "Payments.Refund");
        Assert.Equal(25, refund.Start);
        Assert.Contains(Cited(answer.Text), c => c.Line == 25 && c.Text.Contains("public void Refund", StringComparison.Ordinal));
        // The index holds the file as it is now.
        Assert.Contains(index.FileAt("src/Shop/Payments.cs")!.Symbols, s => s.Symbol is { Name: "Refund", Start: 25 });
    }

    [Fact]
    public void Paths_the_rules_deny_and_secrets_never_appear_and_the_answer_says_what_it_hid()
    {
        Write("src/Private/Vault.cs", "namespace Shop;\n\npublic sealed class Vault\n{\n    public void Refund(decimal amount) { }\n}\n");
        Write(".env", "REFUND_TOKEN=refund-card-payment\n");
        var token = "gh" + "p_" + new string('a', 36);
        Write("src/Shop/Keys.cs", $"namespace Shop;\n\n/// <summary>The refund key for the card.</summary>\npublic static class Keys\n{{\n    public const string Refund = \"{token}\";\n}}\n");
        var index = Index();
        bool May(string p) => !p.StartsWith("src/Private/", StringComparison.Ordinal);

        var words = index.Search().Answer(new CodeQuery("refund payment card") { MayRead = May, Budget = 4000 });
        var name = index.Search().Answer(new CodeQuery("Refund") { MayRead = May, Budget = 4000 });
        foreach (var answer in new[] { words, name })
        {
            Assert.DoesNotContain("src/Private", answer.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(".env", answer.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(token, answer.Text, StringComparison.Ordinal);
            Assert.Contains("hidden by your rules", answer.Text, StringComparison.Ordinal);
        }
        Assert.Contains("«secret-like value hidden»", words.Text, StringComparison.Ordinal);
        Assert.Contains("1 secret-like value hidden", words.Text.Split('\n')[^1], StringComparison.Ordinal);
    }
}
