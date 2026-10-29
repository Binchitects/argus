namespace CodeArena.Tests;

/// <summary>A session's index kept in step with the folder: what the agent writes or runs is in the next answer, read alone.</summary>
public sealed class CodeIndexFreshTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("code-fresh-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("code-fresh-data-").FullName;

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

    [Fact]
    public async Task What_the_agent_writes_is_in_the_next_answer_and_only_that_file_is_read_again()
    {
        Write("src/Payments.cs", "namespace Shop;\n\npublic sealed class Payments\n{\n    public void Charge() { }\n}\n");
        Write("src/Cart.cs", "namespace Shop;\n\npublic sealed class Cart { }\n");
        using var host = new CodeIndexHost(_data, new Workspace(_root));
        var index = (await host.GetAsync(CodeIndexHost.FirstWait, CancellationToken.None))!;

        // An edit and a new file, within the second: the write marks the index, so the next question syncs.
        Write("src/Cart.cs", "namespace Shop;\n\npublic sealed class Cart\n{\n    public void CheckOut(Payments p) => p.Charge();\n}\n");
        Write("src/Refunds.cs", "namespace Shop;\n\n/// <summary>Gives the money back.</summary>\npublic sealed class Refunds { }\n");
        host.Touch();
        Assert.Same(index, await host.GetAsync(CodeIndexHost.FirstWait, CancellationToken.None));
        Assert.Equal(2, index.LastScanned);
        var answer = index.Search().Answer(new CodeQuery("Charge"));
        Assert.Contains("- src/Cart.cs (1): Cart.CheckOut :5", answer.Text, StringComparison.Ordinal);
        Assert.Contains("[1] src/Refunds.cs:4-4 · class Refunds", index.Search().Answer(new CodeQuery("gives the money back")).Text, StringComparison.Ordinal);

        // Nothing changed: nothing read.
        host.Touch();
        await host.GetAsync(CodeIndexHost.FirstWait, CancellationToken.None);
        Assert.Equal(0, index.LastScanned);
    }

    [Fact]
    public async Task A_change_to_hundreds_of_files_is_synced_by_the_next_question_and_a_name_answer_stays_complete()
    {
        for (var i = 0; i < 500; i++)
        {
            Write($"src/Part{i}.cs", $"namespace Shop;\n\npublic sealed class Part{i}\n{{\n    public void Go() => Log.Write({i});\n}}\n");
        }
        Write("src/Log.cs", "namespace Shop;\n\npublic static class Log\n{\n    public static void Write(int n) { }\n}\n");
        using var host = new CodeIndexHost(_data, new Workspace(_root)) { SyncEvery = TimeSpan.Zero };
        var index = (await host.GetAsync(CodeIndexHost.FirstWait, CancellationToken.None))!;

        // As a checkout would: every part changed, by another program (no write of the agent's to mark it).
        for (var i = 0; i < 500; i++)
        {
            Write($"src/Part{i}.cs", $"namespace Shop;\n\npublic sealed class Part{i}\n{{\n    public void Go() => Log.Write({i});\n\n    public void Again() => Log.Write(-{i});\n}}\n");
        }
        await host.GetAsync(CodeIndexHost.FirstWait, CancellationToken.None);
        Assert.Equal(500, index.LastScanned);
        // Every use counted, the budget showing what fits and naming the rest.
        var tied = index.Search().Answer(new CodeQuery("Log.Write") { Budget = 4000 });
        Assert.Equal(1 + 1000, tied.Total);
        var named = index.Search().Answer(new CodeQuery("Write") { Budget = 300 });
        Assert.Equal(1 + 1000, named.Total);
        Assert.Contains("uses in 500 files · complete for code", named.Text, StringComparison.Ordinal);
        Assert.Contains("more files: Part", named.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kept_index_is_used_at_the_next_start_and_what_changed_meanwhile_is_read_again()
    {
        Write("src/Payments.cs", "namespace Shop;\n\npublic sealed class Payments { }\n");
        Write("src/Cart.cs", "namespace Shop;\n\npublic sealed class Cart { }\n");
        using (var first = new CodeIndexHost(_data, new Workspace(_root)))
        {
            await first.GetAsync(CodeIndexHost.FirstWait, CancellationToken.None);
            Assert.True(first.Built);
        }
        Write("src/Cart.cs", "namespace Shop;\n\npublic sealed class Cart\n{\n    public void Empty() { }\n}\n");
        using var next = new CodeIndexHost(_data, new Workspace(_root));
        var index = (await next.GetAsync(CodeIndexHost.FirstWait, CancellationToken.None))!;
        Assert.False(next.Built);
        Assert.Equal(1, next.LastSync!.Read);
        Assert.Contains(index.FileAt("src/Cart.cs")!.Symbols, s => s.Symbol.Name == "Empty");
    }
}
