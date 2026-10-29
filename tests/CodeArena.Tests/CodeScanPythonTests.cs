namespace CodeArena.Tests;

/// <summary>The Python scanner: what a module declares, from each first decorator to its last line, and what it imports.</summary>
public sealed class CodeScanPythonTests
{
    private const string Fixture = """"
        import os, sys
        from . import tools
        from .store import Store as S

        LIMIT = 10
        BASE_URL: str = (
            "https://x"
        )


        class Runner(Base, metaclass=Meta):
            """Runs one task at a time."""

            def __init__(self, store):
                self.store = store

            @property
            @cached
            def size(self) -> int:
                return len(self.store)

            async def run(self, task,
                          retries=3):
                def again():
                    return self.run(task)
                text = """
            def not_a_function():
                pass
            """
                return await again()


        def helper(x):
            '''Doubles it.'''
            return x * 2


        if __name__ == "__main__":
            helper(os.getpid())
        """";

    [Fact]
    public void A_module_declares_its_classes_functions_and_constants_each_with_its_range()
    {
        var f = new PythonScanner().Scan(Fixture);
        var shapes = f.Symbols.Select(s => (s.Kind, s.Qualified, s.Start, s.End)).ToList();
        Assert.Contains(("const", "LIMIT", 5, 5), shapes);
        Assert.Contains(("const", "BASE_URL", 6, 8), shapes);
        Assert.Contains(("class", "Runner", 11, 30), shapes);
        Assert.Contains(("method", "Runner.__init__", 14, 15), shapes);
        Assert.Contains(("method", "Runner.size", 17, 20), shapes);
        Assert.Contains(("method", "Runner.run", 22, 30), shapes);
        Assert.Contains(("function", "Runner.run.again", 24, 25), shapes);
        Assert.Contains(("function", "helper", 33, 35), shapes);
        Assert.Contains(("main", "__main__", 38, 39), shapes);
        Assert.DoesNotContain(shapes, s => s.Qualified.Contains("not_a_function", StringComparison.Ordinal));
        Assert.Equal(("Runs one task at a time.", "Doubles it."), (f.Symbols.Single(s => s.Name == "Runner").Doc, f.Symbols.Single(s => s.Name == "helper").Doc));
        Assert.StartsWith("async def run(self, task, retries=3):", f.Symbols.Single(s => s.Name == "run").Signature, StringComparison.Ordinal);
        Assert.Equal(["os", "sys", ".", ".store"], f.Imports);
        Assert.Contains(f.Uses, u => u.Name == "again" && u.Call && u.Line == 30);
        Assert.Contains(f.Uses, u => u.Name == "getpid" && u.Call && u.Receiver == "os");
    }

    [Fact]
    public void The_repositorys_own_python_reads_with_every_symbol_where_its_name_is()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        var files = new[] { "evals", "deploy/scripts", "tools" }.SelectMany(d => Directory.Exists(Path.Combine(root, d)) ? Directory.GetFiles(Path.Combine(root, d), "*.py") : []).ToList();
        Assert.True(files.Count > 10);
        var count = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            foreach (var s in new PythonScanner().Scan(text).Symbols)
            {
                count++;
                Assert.True(s.Start <= s.End && s.End <= lines.Length, $"{Path.GetFileName(file)}: {s.Qualified} {s.Start}-{s.End}");
                Assert.True(string.Join('\n', lines[(s.Start - 1)..Math.Min(lines.Length, s.Start + 8)]).Contains(s.Name.Trim('_') is { Length: > 0 } n ? n : s.Name, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}:{s.Start}: {s.Kind} {s.Qualified} is not named near its start");
            }
        }
        Assert.True(count > 300, $"only {count} symbols in the repository's Python");
    }
}
