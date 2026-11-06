using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// The tools on a tree the size of a real project: each task's time, result size and whether it went right, printed for
/// docs/measurements/code-arena-tools.md. What must hold is asserted; the times only loosely, the machine is shared.
/// </summary>
public sealed class ToolMeasureTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("arena-measure-").FullName;
    private readonly StringWriter _out = new();

    private ToolContext Context() => new()
    {
        Workspace = new Workspace(_root),
        Ui = new Ui(new StringReader(""), _out, _out, color: false, canAsk: false),
    };

    private static Task<ToolResult> Run(string tool, object args, ToolContext context) =>
        LocalTools.All().Single(t => t.Name == tool).Run(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject(), context, CancellationToken.None);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// 3,000 source files of 200 lines, a 4 MB minified bundle, 200 binary files and 2,000 files in a folder .gitignore
    /// leaves out: a git repository (nothing committed: git lists the files all the same).
    /// </summary>
    private void Tree(bool git)
    {
        var line = new StringBuilder();
        for (var f = 0; f < 3_000; f++)
        {
            line.Clear();
            line.Append("namespace App.Part").Append(f % 40).Append(";\n\npublic sealed class Thing").Append(f).Append("\n{\n");
            for (var i = 0; i < 196; i++)
            {
                line.Append("    public int Value").Append(i).Append(" => ").Append(i * f % 97).Append("; // a line of code\n");
            }
            if (f % 500 == 7)
            {
                line.Append("    // NeedleToken here\n");
            }
            line.Append("}\n");
            Write($"src/Part{f % 40}/Thing{f}.cs", line.ToString());
        }
        Write("web/dist-bundle.min.js", string.Concat(Enumerable.Repeat("var a=1;function b(){return a+1}", 130_000)) + "NeedleToken");
        var blob = new byte[64 * 1024];
        new Random(1).NextBytes(blob);
        blob[10] = 0;
        for (var i = 0; i < 200; i++)
        {
            Directory.CreateDirectory(Path.Combine(_root, "assets"));
            File.WriteAllBytes(Path.Combine(_root, "assets", $"img{i}.png"), blob);
        }
        for (var i = 0; i < 2_000; i++)
        {
            Write($"generated/out{i}.cs", "// NeedleToken generated\n");
        }
        Write(".gitignore", "generated/\n");
        if (git)
        {
            using var init = Process.Start(new ProcessStartInfo("git", ["init", "-q"]) { WorkingDirectory = _root, UseShellExecute = false })!;
            Assert.True(init.WaitForExit(30_000));
            Assert.Equal(0, init.ExitCode);
        }
    }

    private async Task<(ToolResult? Result, string? Error, TimeSpan Took)> Timed(string tool, object args)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await Run(tool, args, Context());
            return (result, null, clock.Elapsed);
        }
        catch (ToolError e)
        {
            return (null, e.Message, clock.Elapsed);
        }
    }

    private void Report(string task, (ToolResult? Result, string? Error, TimeSpan Took) run, string note = "") =>
        output.WriteLine($"| {task} | {run.Took.TotalMilliseconds:0} ms | {(run.Error is { } e ? "error: " + Fmt.OneLine(e, 60) : $"{run.Result!.Text.Length:N0} chars")} | {note} |");

    [Fact]
    public async Task Grep_on_a_big_tree_is_quick_skips_ignored_and_binary_files_and_says_what_it_cut()
    {
        Tree(git: true);
        var literal = await Timed("grep", new { pattern = "NeedleToken" });
        Report("grep literal, git tree", literal, literal.Result?.Display ?? "");
        var files = await Timed("grep", new { pattern = @"Value1\d\d =>", output = "files" });
        Report("grep regex, files", files, files.Result?.Display ?? "");
        var glob = await Timed("grep", new { pattern = "class Thing2999\\b", glob = "*.cs" });
        Report("grep with glob", glob, glob.Result?.Display ?? "");
        var count = await Timed("grep", new { pattern = "Value5 =>", output = "count" });
        Report("grep count", count, count.Result?.Display ?? "");

        Assert.Null(literal.Error);
        Assert.DoesNotContain("generated/", literal.Result!.Text);
        Assert.DoesNotContain("assets/", files.Result!.Text);
        Assert.Contains("src/Part7/Thing7.cs:", literal.Result.Text);
        // The bundle's one line matches: it is shown cut, never whole.
        Assert.Contains("web/dist-bundle.min.js:1:", literal.Result.Text);
        Assert.True(literal.Result.Text.Length < 4_000, literal.Result.Text.Length.ToString());
        Assert.Equal("1 match in 1 file", glob.Result!.Display);
        Assert.Contains("3,000 files", count.Result!.Display!.Replace("3000", "3,000"));
        Assert.True(literal.Took < TimeSpan.FromSeconds(20), literal.Took.ToString());
    }

    [Fact]
    public async Task Grep_reads_a_big_log_it_is_given_and_a_slow_pattern_does_not_stall_it()
    {
        var log = new StringBuilder();
        for (var i = 0; i < 400_000; i++)
        {
            log.Append("2026-11-06 12:00:00 INFO request ").Append(i).Append(" served in 3 ms\n");
            if (i == 345_678)
            {
                log.Append("2026-11-06 12:00:01 ERROR the disk is full\n");
            }
        }
        Write("logs/app.log", log.ToString());
        var big = await Timed("grep", new { pattern = "ERROR", path = "logs/app.log" });
        Report($"grep a {log.Length / 1_000_000} MB log", big, big.Result?.Display ?? "");

        for (var f = 0; f < 300; f++)
        {
            Write($"src/f{f}.txt", string.Concat(Enumerable.Repeat(new string('a', 30) + "!\n", 20)));
        }
        var slow = await Timed("grep", new { pattern = "(a+)+$", path = "src" });
        Report("grep, catastrophic pattern, 300 files", slow, slow.Result?.Display ?? slow.Error ?? "");
        Assert.Contains("the disk is full", big.Result!.Text);
        Assert.True(slow.Took < TimeSpan.FromSeconds(15), slow.Took.ToString());
    }

    [Fact]
    public async Task Grep_outside_git_keeps_to_the_gitignore()
    {
        Tree(git: false);
        var literal = await Timed("grep", new { pattern = "NeedleToken", output = "files" });
        Report("grep literal, no git", literal, literal.Result?.Display ?? "");
        Assert.DoesNotContain("generated/", literal.Result!.Text);
        Assert.Contains("src/Part7/Thing7.cs", literal.Result.Text);
    }

    [Fact]
    public async Task Read_file_of_a_large_file_gives_its_outline_and_reads_a_part_by_line_or_symbol()
    {
        var sb = new StringBuilder("using System;\n\nnamespace App;\n\n");
        for (var c = 0; c < 60; c++)
        {
            sb.Append($"/// <summary>Part {c}.</summary>\npublic sealed class Part{c}\n{{\n");
            for (var m = 0; m < 5; m++)
            {
                sb.Append($"    public int Method{m}(int x)\n    {{\n");
                for (var i = 0; i < 16; i++)
                {
                    sb.Append($"        x += {i}; // step {i} of method {m} in part {c}\n");
                }
                sb.Append("        return x;\n    }\n\n");
            }
            sb.Append("}\n\n");
        }
        Write("Big.cs", sb.ToString());
        var lines = sb.ToString().Count(ch => ch == '\n');
        var whole = await Timed("read_file", new { path = "Big.cs" });
        Report($"read_file, {lines:N0}-line file", whole);
        var part = await Timed("read_file", new { path = "Big.cs", symbol = "Part42.Method3" });
        Report("read_file, one symbol", part);

        Assert.Contains("Part42", whole.Result!.Text);
        Assert.Contains("outline", whole.Result.Text);
        Assert.True(whole.Result.Text.Length < 12_000, whole.Result.Text.Length.ToString());
        Assert.Contains("public int Method3(int x)", part.Result!.Text);
        Assert.Contains("step 15 of method 3 in part 42", part.Result.Text);
        Assert.DoesNotContain("method 2 in part 42", part.Result.Text);
    }

    [Fact]
    public async Task Run_shell_keeps_a_long_output_whole_ends_with_a_child_left_behind_and_says_how_it_ended()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var dir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(dir);
        var c = new ToolContext
        {
            Workspace = new Workspace(_root),
            Ui = new Ui(new StringReader(""), _out, _out, color: false, canAsk: false),
            SessionsDir = dir,
            SessionId = () => "s1",
        };
        var clock = Stopwatch.StartNew();
        var noisy = await Run("run_shell", new { command = "seq 1 200000; echo FAILED at the middle >&2; seq 1 200000; exit 1" }, c);
        Report("run_shell, 2.6 MB of output", (noisy, null, clock.Elapsed));
        clock.Restart();
        var child = await Run("run_shell", new { command = "sleep 30 & echo started" }, c);
        Report("run_shell, child left running", (child, null, clock.Elapsed));
        clock.Restart();
        var killed = await Run("run_shell", new { command = "kill -9 $$" }, c);
        Report("run_shell, killed by a signal", (killed, null, clock.Elapsed), Fmt.OneLine(killed.Text, 60));

        // What the middle said is not lost: it is kept, to read with read_result.
        Assert.Contains("[exit code 1]", noisy.Text);
        Assert.Matches(@"went to r\d+", noisy.Text);
        var kept = KeptResults.Read(c, System.Text.RegularExpressions.Regex.Match(noisy.Text, @"went to (r\d+)").Groups[1].Value)!;
        Assert.Contains("FAILED at the middle", kept);
        Assert.Contains("started", child.Text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("signal 9", killed.Text);
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
