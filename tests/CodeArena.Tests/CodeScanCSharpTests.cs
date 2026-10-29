namespace CodeArena.Tests;

/// <summary>A test that needs universal-ctags on PATH (the scanners are measured against it): skipped where there is none.</summary>
public sealed class CtagsFactAttribute : FactAttribute
{
    public CtagsFactAttribute()
    {
        if (GdbFactAttribute.Run("ctags", "--version") is not { } v || !v.Contains("Universal Ctags", StringComparison.Ordinal))
        {
            Skip = "No universal-ctags on PATH.";
        }
    }
}

/// <summary>The C# scanner: what a file declares, where each starts and ends, its line, its doc and what holds it.</summary>
public sealed class CodeScanCSharpTests
{
    private const string Fixture = """"
        using System.Text;
        using static System.Math;
        using Json = System.Text.Json;

        namespace Shop.Orders;

        /// <summary>An order and its lines.</summary>
        [Serializable]
        public sealed partial class Order(Guid id) : Entity, IComparable<Order>
        {
            private readonly List<Line> _lines = new() { new Line("a", 1) };
            public const string Kind = "order";
            public Guid Id { get; } = id;
            public decimal Total => _lines.Sum(l => l.Price);
            public event EventHandler? Changed;

            /// <summary>Adds a line.</summary>
            [Obsolete("use Add(Line)")]
            public void Add(string sku, int count = 1)
            {
                var s = $"{sku} {{ not a block }}";
                var raw = """
                    { "json": [ "}" ] }
                    """;
                int Twice(int x) => x * 2;
                void Log(string m) { Console.WriteLine(m); }
                if (count > 0) { _lines.Add(new Line(sku, Twice(count))); }
                Changed?.Invoke(this, EventArgs.Empty);
            }

            public (int Lines, decimal Sum) Totals() => (_lines.Count, Total);

            public T Pick<T>(Func<Line, T> f) where T : struct
            {
                return f(_lines[0]);
            }

            public static Order operator +(Order a, Line b) { a.Add(b.Sku); return a; }

            public int this[int i] => i;

            public int CompareTo(Order? other) => 0;

        #if DEBUG
            public void Debug() { }
        #else
            public void Debug() { }
        #endif

            public interface IRule
            {
                bool Holds(Order o);
            }

            private enum State { New, Paid }
        }

        public record Line(string Sku, decimal Price);

        public record struct Money(decimal Amount)
        {
            public static Money Zero => new(0);
        }
        """";

    private static List<(string Kind, string Qualified, int Start, int End)> Shapes(ScannedFile f) => [.. f.Symbols.Select(s => (s.Kind, s.Qualified, s.Start, s.End))];

    [Fact]
    public void A_file_declares_its_namespace_types_and_members_each_with_its_range()
    {
        var scanned = new CSharpScanner().Scan(Fixture);
        var shapes = Shapes(scanned);
        Assert.Contains(("namespace", "Shop.Orders", 5, Fixture.Split('\n').Length), shapes);
        // A type and a member start at their first attribute (their doc is above it), and end at their brace or semicolon.
        Assert.Contains(("class", "Shop.Orders.Order", 8, 56), shapes);
        Assert.Contains(("field", "Shop.Orders.Order._lines", 11, 11), shapes);
        Assert.Contains(("const", "Shop.Orders.Order.Kind", 12, 12), shapes);
        Assert.Contains(("property", "Shop.Orders.Order.Id", 13, 13), shapes);
        Assert.Contains(("property", "Shop.Orders.Order.Total", 14, 14), shapes);
        Assert.Contains(("event", "Shop.Orders.Order.Changed", 15, 15), shapes);
        // The raw string's braces, the interpolation's and the comment's count for nothing.
        Assert.Contains(("method", "Shop.Orders.Order.Add", 18, 29), shapes);
        Assert.Contains(("function", "Shop.Orders.Order.Twice", 25, 25), shapes);
        Assert.Contains(("function", "Shop.Orders.Order.Log", 26, 26), shapes);
        Assert.Contains(("method", "Shop.Orders.Order.Totals", 31, 31), shapes);
        Assert.Contains(("method", "Shop.Orders.Order.Pick", 33, 36), shapes);
        Assert.Contains(("operator", "Shop.Orders.Order.operator +", 38, 38), shapes);
        Assert.Contains(("indexer", "Shop.Orders.Order.this[]", 40, 40), shapes);
        Assert.Contains(("method", "Shop.Orders.Order.CompareTo", 42, 42), shapes);
        // Both branches of the #if declare it: once.
        Assert.Single(shapes, s => s.Qualified == "Shop.Orders.Order.Debug");
        Assert.Contains(("interface", "Shop.Orders.Order.IRule", 50, 53), shapes);
        Assert.Contains(("method", "Shop.Orders.Order.IRule.Holds", 52, 52), shapes);
        Assert.Contains(("enum", "Shop.Orders.Order.State", 55, 55), shapes);
        Assert.Contains(("record", "Shop.Orders.Line", 58, 58), shapes);
        Assert.Contains(("record", "Shop.Orders.Money", 60, 63), shapes);
        Assert.Contains(("property", "Shop.Orders.Money.Zero", 62, 62), shapes);
        Assert.DoesNotContain(shapes, s => s.Qualified.Contains("not", StringComparison.Ordinal) || s.Qualified.EndsWith(".json", StringComparison.Ordinal));

        var add = scanned.Symbols.Single(s => s.Qualified == "Shop.Orders.Order.Add");
        Assert.Equal(("Adds a line.", "Shop.Orders.Order"), (add.Doc, add.Parent));
        Assert.StartsWith("[Obsolete(\"use Add(Line)\")] public void Add(string sku, int count = 1)", add.Signature, StringComparison.Ordinal);
        Assert.Equal("An order and its lines.", scanned.Symbols.Single(s => s.Name == "Order" && s.Kind == "class").Doc);
        Assert.Equal(["System.Text", "System.Math", "System.Text.Json"], scanned.Imports);
        // The names it uses, calls marked, with what is before their dot.
        Assert.Contains(scanned.Uses, u => u.Name == "Twice" && u.Call && u.Line == 27);
        Assert.Contains(scanned.Uses, u => u.Name == "Invoke" && u.Call && u.Receiver == "Changed");
        Assert.Contains(scanned.Uses, u => u.Name == "Line" && u.Call);
        Assert.DoesNotContain(scanned.Uses, u => u.Name == "block" || u.Name == "json");
    }

    [Fact]
    public void A_symbols_hash_changes_with_its_code_not_its_comments_or_spacing()
    {
        static string HashOf(string text) => new CSharpScanner().Scan(text).Symbols.Single(s => s.Kind == "method").Hash;
        var plain = HashOf("class A { int M() { return 1; } }");
        Assert.Equal(plain, HashOf("class A {\n  // why\n  int M()   {\n    return 1; // one\n  }\n}"));
        Assert.NotEqual(plain, HashOf("class A { int M() { return 2; } }"));
    }

    /// <summary>What ctags finds declared in Code Arena's own source, the scanner finds too: at least 98% of it.</summary>
    [CtagsFact]
    public void The_scanner_finds_what_ctags_finds_declared()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CodeArena"));
        var tags = GdbFactAttribute.Run("ctags", "-R", "--languages=C#", "--kinds-C#=cmpfeEigs", "--fields=+n", "-f", "-", root) ?? "";
        var expected = new HashSet<(string File, string Name, int Line)>();
        foreach (var line in tags.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            var at = parts.FirstOrDefault(p => p.StartsWith("line:", StringComparison.Ordinal));
            if (parts.Length > 2 && at is not null && !parts[1].Contains("/obj/", StringComparison.Ordinal))
            {
                expected.Add((Path.GetFileName(parts[1]), parts[0], int.Parse(at[5..], System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        var scanner = new CSharpScanner();
        var found = new HashSet<(string File, string Name)>();
        var lines = new Dictionary<(string, string), List<(int, int)>>();
        foreach (var file in Directory.GetFiles(root, "*.cs").Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (var sym in scanner.Scan(File.ReadAllText(file)).Symbols)
            {
                var key = (Path.GetFileName(file), sym.Name);
                (lines.TryGetValue(key, out var l) ? l : lines[key] = []).Add((sym.Start, sym.End));
            }
        }
        // Found: a symbol of that name in that file whose range holds ctags's line.
        var hit = expected.Count(e => lines.TryGetValue((e.File, e.Name), out var ranges) && ranges.Any(r => r.Item1 <= e.Line && e.Line <= r.Item2));
        Assert.True(expected.Count > 1000, $"ctags found {expected.Count}");
        Assert.True(hit >= expected.Count * 0.98, $"the scanner found {hit} of the {expected.Count} declarations ctags found");
    }

    [Fact]
    public void Code_Arenas_own_source_reads_with_every_symbol_where_its_name_is()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CodeArena");
        var files = Directory.GetFiles(root, "*.cs").Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();
        Assert.True(files.Count > 50);
        var scanner = new CSharpScanner();
        var count = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            foreach (var s in scanner.Scan(text).Symbols.Where(s => s.Kind != "namespace"))
            {
                count++;
                Assert.True(s.Start <= s.End && s.End <= lines.Length, $"{Path.GetFileName(file)}: {s.Qualified} {s.Start}-{s.End}");
                var name = s.Kind is "indexer" ? "this" : s.Kind is "operator" ? "operator" : s.Name;
                Assert.True(string.Join('\n', lines[(s.Start - 1)..Math.Min(lines.Length, s.Start + 5)]).Contains(name, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}:{s.Start}: {s.Kind} {s.Qualified} is not named near its start");
            }
        }
        Assert.True(count > 3000, $"only {count} symbols in Code Arena's own source");
    }
}
