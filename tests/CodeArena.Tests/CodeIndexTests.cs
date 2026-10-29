namespace CodeArena.Tests;

/// <summary>The code index kept: built, synced by what changed, written and read back, and rebuilt when it cannot be read.</summary>
public sealed class CodeIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("code-index-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("code-index-data-").FullName;

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

    private CodeIndex Built(out IndexSync sync)
    {
        var index = new CodeIndex(_root, CodeIndex.FileFor(_data, _root));
        sync = index.Sync(new Workspace(_root), [_root], CancellationToken.None, threads: 2);
        return index;
    }

    [Fact]
    public void A_sync_reads_only_what_changed_and_the_index_reads_back_as_it_was_written()
    {
        Write("src/Orders.cs", "namespace Shop;\n/// <summary>Takes payments.</summary>\npublic class Payments { public void TakePayment(decimal amount) { } }\n");
        Write("src/cart.ts", "export function addToCart(item: string) { return item }\n");
        Write("tools/report.py", "def make_report(rows):\n    return rows\n");
        Write("docs/guide.md", "# Guide\n\n## Paying\n\nHow payments are taken.\n");
        Write(".env", "SECRET=x\n");
        var index = Built(out var first);
        Assert.Equal((4, 1), (first.Read, first.Skipped));
        var orders = index.FileAt("src/Orders.cs")!;
        Assert.Contains(orders.Symbols, s => s.Symbol.Qualified == "Shop.Payments.TakePayment" && s.Terms.ContainsKey("payment") && s.Terms.ContainsKey("takepayment"));
        Assert.Contains("TakePayment", orders.Names);
        Assert.Contains(index.FileAt("docs/guide.md")!.Symbols, s => s.Symbol is { Kind: "heading", Name: "Paying" });
        // A secret's file is listed, with nothing of it read.
        var env = index.FileAt(".env")!;
        Assert.True(env.Tags.HasFlag(CodeFileTags.Sensitive));
        Assert.Empty(env.Terms);

        // Unchanged: nothing read again. One file changed: that one alone. One removed: gone.
        Assert.Equal(0, index.Sync(new Workspace(_root), [_root], CancellationToken.None).Read);
        Write("src/cart.ts", "export function addToCart(item: string) { return [item] }\nexport const removeFromCart = (id: string) => id\n");
        File.Delete(Path.Combine(_root, "tools", "report.py"));
        var again = index.Sync(new Workspace(_root), [_root], CancellationToken.None);
        Assert.Equal((1, 1), (again.Read, again.Removed));
        Assert.Contains(index.FileAt("src/cart.ts")!.Symbols, s => s.Symbol.Name == "removeFromCart");
        Assert.Null(index.FileAt("tools/report.py"));
        // Touched but the same bytes: not read again.
        File.SetLastWriteTimeUtc(Path.Combine(_root, "src", "Orders.cs"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(0, index.Sync(new Workspace(_root), [_root], CancellationToken.None).Read);

        index.Save("abc123");
        var loaded = CodeIndex.Load(_root, index.File)!;
        Assert.Equal(index.Files.Select(f => f.Path).Order(), loaded.Files.Select(f => f.Path).Order());
        var back = loaded.FileAt("src/Orders.cs")!;
        Assert.Equal(orders.Symbols.Select(s => s.Symbol), back.Symbols.Select(s => s.Symbol));
        Assert.Equal(orders.Terms.OrderBy(t => t.Key, StringComparer.Ordinal), back.Terms.OrderBy(t => t.Key, StringComparer.Ordinal));
        Assert.Equal(orders.Names.Order(), back.Names.Order());
        Assert.Equal(0, loaded.Sync(new Workspace(_root), [_root], CancellationToken.None).Read);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(index.File));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(index.File)!));
        }
    }

    [Fact]
    public void An_index_that_cannot_be_read_or_is_of_another_folder_is_built_again()
    {
        Write("a.cs", "class A { }\n");
        var index = Built(out _);
        index.Save();
        Assert.Null(CodeIndex.Load(_root + "-elsewhere", index.File));
        File.WriteAllBytes(index.File, [1, 2, 3]);
        Assert.Null(CodeIndex.Load(_root, index.File));
        Assert.Null(CodeIndex.Load(_root, Path.Combine(_data, "missing.bin")));
    }

    [Theory]
    [InlineData("RunToolsAsync", "run tool runtoolsasync")]
    [InlineData("KEEP_WHOLE", "keep whole keepwhole")]
    [InlineData("parse2Json", "parse json parse2json")]
    [InlineData("HTTPServer", "http server httpserver")]
    public void Names_split_into_their_words(string name, string words) => Assert.Equal(words, string.Join(' ', Terms.Split(name)));

    /// <summary>This repository whole: how long a first build takes, how big the index is, and that a second sync reads nothing.</summary>
    [Fact]
    public void The_repository_itself_is_indexed_in_seconds_into_a_small_file()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var index = new CodeIndex(repo, CodeIndex.FileFor(_data, repo));
        var sync = index.Sync(new Workspace(repo), [repo], CancellationToken.None);
        Assert.True(sync.Read > 1000, $"read {sync.Read}");
        Assert.True(sync.Took < TimeSpan.FromSeconds(60), $"took {sync.Took}");
        index.Save();
        var size = new FileInfo(index.File).Length;
        Assert.True(size < 50L * 1024 * 1024, $"{size:N0} bytes");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(CodeIndex.Load(repo, index.File));
        var load = clock.Elapsed;
        Assert.Equal(0, index.Sync(new Workspace(repo), [repo], CancellationToken.None).Read);
        Console.WriteLine($"code index of the repository: {sync.Read} files read in {sync.Took.TotalSeconds:0.0} s, {size / 1024.0 / 1024:0.0} MB, loaded in {load.TotalMilliseconds:0} ms, " +
            $"{index.Files.Sum(f => f.Symbols.Count):N0} symbols");
    }
}
