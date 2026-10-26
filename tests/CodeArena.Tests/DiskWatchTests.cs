namespace CodeArena.Tests;

/// <summary>What changes on disk by anyone reaches the IDE's page: each change once, by path, and nothing nobody reads.</summary>
public sealed class DiskWatchTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-watch-" + Guid.NewGuid().ToString("N")[..8]);

    public DiskWatchTests() => Directory.CreateDirectory(Path.Combine(_root, "src"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static List<(string Path, string Kind)> Changes(System.Text.Json.Nodes.JsonObject answer) =>
        [.. answer["changes"]!.AsArray().Select(c => (c!["path"]!.GetValue<string>(), c["kind"]!.GetValue<string>())).OrderBy(c => c.Item1, StringComparer.Ordinal)];

    /// <summary>As the page asks: from its version until a change it waits for comes (the system may tell a write in parts).</summary>
    private static async Task<(long Version, List<(string Path, string Kind)> Seen)> UntilAsync(DiskWatch watch, long version, (string, string) wanted)
    {
        var seen = new List<(string Path, string Kind)>();
        var until = DateTime.UtcNow + Wait;
        while (!seen.Contains(wanted))
        {
            Assert.True(DateTime.UtcNow < until, $"Not told within {Wait.TotalSeconds:0} s: {wanted}; told {string.Join(", ", seen)}.");
            var answer = await watch.SinceAsync(version, Wait, CancellationToken.None);
            Assert.Null(answer["all"]);
            version = answer["version"]!.GetValue<long>();
            seen.AddRange(Changes(answer));
        }
        return (version, seen);
    }

    [Fact]
    public async Task A_file_made_changed_and_deleted_by_hand_is_told_to_the_page_waiting_for_it()
    {
        using var watch = new DiskWatch(_root);
        Assert.True(watch.Watching);
        var start = await watch.SinceAsync(0, Wait, CancellationToken.None);
        var version = start["version"]!.GetValue<long>();
        Assert.Empty(Changes(start));

        // The page waits; a file made by another editor answers it.
        var waiting = watch.SinceAsync(version, Wait, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "calc.py"), "def add(a, b):\n    return a + b\n");
        Assert.NotEmpty(Changes(await waiting));
        var (made, _) = await UntilAsync(watch, version, ("src/calc.py", "created"));

        // Changed, then deleted: each told as what the file is then.
        await File.AppendAllTextAsync(Path.Combine(_root, "src", "calc.py"), "\n# more\n");
        var (changed, _) = await UntilAsync(watch, made, ("src/calc.py", "changed"));
        File.Delete(Path.Combine(_root, "src", "calc.py"));
        await UntilAsync(watch, changed, ("src/calc.py", "deleted"));

        // A page that was away gets what happened since its version at once, a path once, as it is now.
        var behind = await watch.SinceAsync(version, Wait, CancellationToken.None);
        Assert.Equal([("src/calc.py", "deleted")], Changes(behind));
    }

    [Fact]
    public async Task Nothing_changes_and_the_page_hears_so_when_its_wait_is_over()
    {
        using var watch = new DiskWatch(_root);
        var version = (await watch.SinceAsync(0, Wait, CancellationToken.None))["version"]!.GetValue<long>();
        var quiet = await watch.SinceAsync(version, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Equal(version, quiet["version"]!.GetValue<long>());
        Assert.Empty(Changes(quiet));
    }

    [Theory]
    [InlineData("src/app.ts", "src/app.ts")]
    [InlineData(".git/HEAD", "")]
    [InlineData(".git/index", "")]
    [InlineData(".git/objects/ab/cdef", null)]
    [InlineData("node_modules/react/index.js", null)]
    [InlineData("web/node_modules/x.js", null)]
    [InlineData(".code-arena/r/1/main.py", null)]
    public void Only_what_the_page_reads_is_told_and_git_s_own_as_Source_Control(string path, string? told)
    {
        Assert.Equal(told, DiskWatch.PathFor(_root, Path.Combine(_root, path)));
    }
}
