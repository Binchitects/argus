using System.Text.Json.Nodes;

namespace CodeArena.Tests;

public sealed class LocalToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("arena-tools-").FullName;
    private readonly StringWriter _out = new();

    private ToolContext Context(params string[] allowed) => new()
    {
        Workspace = new Workspace(_root, allowed),
        Ui = new Ui(new StringReader(""), _out, _out, color: false, canAsk: false),
    };

    private static Task<ToolResult> Run(string tool, string args, ToolContext context) =>
        LocalTools.All().Single(t => t.Name == tool).Run(JsonNode.Parse(args)!.AsObject(), context, CancellationToken.None);

    private static string Args(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Edit_file_replaces_a_unique_match_and_shows_the_change_as_a_diff()
    {
        var file = Write("src/app.py", "def greet():\n    return 'hi'\n\nprint(greet())\n");
        var result = await Run("edit_file", Args(new { path = "src/app.py", old_string = "return 'hi'", new_string = "return 'hello'" }), Context());
        Assert.False(result.Error);
        Assert.Equal("def greet():\n    return 'hello'\n\nprint(greet())\n", File.ReadAllText(file));
        Assert.Contains("2 -     return 'hi'", result.Display);
        Assert.Contains("2 +     return 'hello'", result.Display);
        Assert.Contains("1   def greet():", result.Display);
    }

    [Fact]
    public async Task Edit_file_refuses_a_missing_or_ambiguous_match_and_replace_all_changes_every_one()
    {
        var file = Write("a.txt", "x = 1\ny = 1\nx = 1\n");
        var missing = await Assert.ThrowsAsync<ToolError>(() => Run("edit_file", Args(new { path = "a.txt", old_string = "z = 1", new_string = "z = 2" }), Context()));
        Assert.Contains("not found", missing.Message);
        var twice = await Assert.ThrowsAsync<ToolError>(() => Run("edit_file", Args(new { path = "a.txt", old_string = "x = 1", new_string = "x = 2" }), Context()));
        Assert.Contains("appears 2 times", twice.Message);
        Assert.Equal("x = 1\ny = 1\nx = 1\n", File.ReadAllText(file));
        var same = await Assert.ThrowsAsync<ToolError>(() => Run("edit_file", Args(new { path = "a.txt", old_string = "y = 1", new_string = "y = 1" }), Context()));
        Assert.Contains("the same", same.Message);

        var all = await Run("edit_file", Args(new { path = "a.txt", old_string = "x = 1", new_string = "x = 2", replace_all = true }), Context());
        Assert.Equal("Edited a.txt: 2 replacements.", all.Text);
        Assert.Equal("x = 2\ny = 1\nx = 2\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Edit_file_creates_a_file_from_an_empty_old_string_and_keeps_windows_line_endings()
    {
        var created = await Run("edit_file", Args(new { path = "new/notes.md", old_string = "", new_string = "# Notes\n" }), Context());
        Assert.Equal("Created new/notes.md.", created.Text);
        Assert.Equal("# Notes\n", File.ReadAllText(Path.Combine(_root, "new", "notes.md")));
        await Assert.ThrowsAsync<ToolError>(() => Run("edit_file", Args(new { path = "new/notes.md", old_string = "", new_string = "again" }), Context()));

        // The model writes \n; the file has \r\n: it still matches, and the file keeps its endings.
        var file = Write("win.cs", "class A\r\n{\r\n    int x;\r\n}\r\n");
        await Run("edit_file", Args(new { path = "win.cs", old_string = "{\n    int x;", new_string = "{\n    int x;\n    int y;" }), Context());
        Assert.Equal("class A\r\n{\r\n    int x;\r\n    int y;\r\n}\r\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Paths_outside_the_working_directory_are_refused_unless_allowed()
    {
        var outside = Directory.CreateTempSubdirectory("arena-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "no");
            var refused = await Assert.ThrowsAsync<ToolError>(() => Run("read_file", Args(new { path = Path.Combine(outside, "secret.txt") }), Context()));
            Assert.Contains("outside the working directory", refused.Message);
            await Assert.ThrowsAsync<ToolError>(() => Run("write_file", Args(new { path = "../escape.txt", content = "x" }), Context()));
            Assert.False(File.Exists(Path.Combine(_root, "..", "escape.txt")));

            var allowed = await Run("read_file", Args(new { path = Path.Combine(outside, "secret.txt") }), Context(outside));
            Assert.Contains("no", allowed.Text);

            if (!OperatingSystem.IsWindows())
            {
                // A link inside that leads outside is outside.
                File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), Path.Combine(outside, "secret.txt"));
                await Assert.ThrowsAsync<ToolError>(() => Run("read_file", Args(new { path = "link.txt" }), Context()));
            }
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task A_link_leads_where_the_system_takes_it_not_where_its_name_points()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // links need a right of their own there
        }
        var outside = Directory.CreateTempSubdirectory("arena-outside-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "dir"));
            var secret = Path.Combine(outside, "secret.txt");
            File.WriteAllText(secret, "keep out\n");
            Write("inside.txt", "in\n");
            // out leads outside (refused on its own). Through it, .. is outside's own folder, not this one: x.txt is
            // inside by name only, as the system reads it. Nor is z.txt, which points at nothing yet.
            Directory.CreateSymbolicLink(Path.Combine(_root, "out"), Path.Combine(outside, "dir"));
            File.CreateSymbolicLink(Path.Combine(_root, "x.txt"), "out/../secret.txt");
            File.CreateSymbolicLink(Path.Combine(_root, "z.txt"), "out/../planted.txt");
            // d is the top of the disk: d/../etc/hostname is /etc/hostname.
            Directory.CreateSymbolicLink(Path.Combine(_root, "d"), "/");
            File.CreateSymbolicLink(Path.Combine(_root, "y.txt"), "d/../etc/hostname");
            File.CreateSymbolicLink(Path.Combine(_root, "loop.txt"), "loop.txt");
            // Out through a link and back in: inside, where it leads.
            File.CreateSymbolicLink(Path.Combine(_root, "back.txt"), $"out/../../{Path.GetFileName(_root)}/inside.txt");

            foreach (var path in new[] { "x.txt", "y.txt", "z.txt", "loop.txt", "out/secret.txt" })
            {
                var read = await Assert.ThrowsAsync<ToolError>(() => Run("read_file", Args(new { path }), Context()));
                Assert.Contains("outside the working directory", read.Message);
            }
            await Assert.ThrowsAsync<ToolError>(() => Run("write_file", Args(new { path = "x.txt", content = "owned" }), Context()));
            await Assert.ThrowsAsync<ToolError>(() => Run("edit_file", Args(new { path = "x.txt", old_string = "keep", new_string = "owned" }), Context()));
            await Assert.ThrowsAsync<ToolError>(() => Run("write_file", Args(new { path = "z.txt", content = "planted" }), Context()));
            Assert.Equal("keep out\n", File.ReadAllText(secret));
            Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));
            Assert.Equal("     1\tin\n", (await Run("read_file", Args(new { path = "back.txt" }), Context())).Text);

            // Searched and listed as read: what leads outside is not there.
            Assert.Equal("No matches.", (await Run("grep", Args(new { pattern = "keep out" }), Context())).Text);
            Assert.Equal(["back.txt", "inside.txt"], (await Run("glob", Args(new { pattern = "*.txt" }), Context())).Text.Split('\n').Order());
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task A_name_with_spaces_around_it_is_that_name_when_something_has_it()
    {
        Write("notes", "plain\n");
        Write("notes ", "spaced\n");
        Assert.Equal("     1\tspaced\n", (await Run("read_file", Args(new { path = "notes " }), Context())).Text);
        Assert.Equal("     1\tplain\n", (await Run("read_file", Args(new { path = " notes" }), Context())).Text);
    }

    [Fact]
    public async Task Grep_skips_what_it_cannot_read_and_nothing_waits_on_a_named_pipe()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Write("ok.txt", "needle\n");
        var locked = Special.Locked(Write("locked.txt", "needle\n"));
        Special.Pipe(Path.Combine(_root, "pipe"));

        // Each on a thread of its own: a read that waits on the pipe would wait for ever.
        var found = await Task.Run(() => Run("grep", Args(new { pattern = "needle" }), Context())).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(locked ? "ok.txt:1: needle" : "locked.txt:1: needle\nok.txt:1: needle", string.Join('\n', found.Text.Split('\n').Order()));
        var pipe = await Assert.ThrowsAsync<ToolError>(() => Task.Run(() => Run("read_file", Args(new { path = "pipe" }), Context())).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("not a regular file", pipe.Message);
        var write = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => Run("write_file", Args(new { path = "pipe", content = "x" }), Context())).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("not a regular file", write.Message);
        Assert.Equal("ok.txt", (await Task.Run(() => Run("glob", Args(new { pattern = "ok*" }), Context())).WaitAsync(TimeSpan.FromSeconds(20))).Text);
    }

    [Fact]
    public async Task A_glob_that_is_not_valid_is_said_back_not_thrown()
    {
        Write("src/a.txt", "x\n");
        foreach (var pattern in new[] { "src/{a,b", "[]", "[z-a].txt" })
        {
            var glob = await Assert.ThrowsAsync<ToolError>(() => Run("glob", Args(new { pattern }), Context()));
            Assert.Contains("is not a valid glob", glob.Message);
            await Assert.ThrowsAsync<ToolError>(() => Run("grep", Args(new { pattern = "x", glob = pattern }), Context()));
        }
    }

    [Fact]
    public async Task Read_file_numbers_the_lines_and_says_how_to_read_on()
    {
        Write("long.txt", string.Join('\n', Enumerable.Range(1, 2500).Select(i => $"line {i}")));
        var first = await Run("read_file", Args(new { path = "long.txt" }), Context());
        Assert.StartsWith("     1\tline 1\n", first.Text);
        Assert.Contains("(lines 1-2000 of 2500; read on with offset=2001)", first.Text);
        var rest = await Run("read_file", Args(new { path = "long.txt", offset = 2499, limit = 5 }), Context());
        Assert.Equal("  2499\tline 2499\n  2500\tline 2500\n", rest.Text);
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [1, 0, 2, 3]);
        Assert.Contains("binary file", (await Run("read_file", Args(new { path = "blob.bin" }), Context())).Text);
    }

    [Fact]
    public async Task Glob_and_grep_find_files_and_lines_and_skip_build_folders()
    {
        Write("src/Api/Program.cs", "var app = Build();\n// TODO: tidy\napp.Run();\n");
        Write("src/Api/Util.cs", "static class Util { } // todo later\n");
        Write("web/index.ts", "export const x = 1;\n");
        Write("node_modules/lib/index.ts", "TODO: not ours\n");
        Write("bin/Debug/out.cs", "// TODO: built\n");

        var cs = await Run("glob", Args(new { pattern = "**/*.cs" }), Context());
        Assert.Equal(["src/Api/Program.cs", "src/Api/Util.cs"], cs.Text.Split('\n').Order());
        var ts = await Run("glob", Args(new { pattern = "*.{ts,tsx}" }), Context());
        Assert.Equal("web/index.ts", ts.Text);

        var todo = await Run("grep", Args(new { pattern = "TODO", ignore_case = true }), Context());
        Assert.Contains("src/Api/Program.cs:2: // TODO: tidy", todo.Text);
        Assert.Contains("src/Api/Util.cs:1: static class Util { } // todo later", todo.Text);
        Assert.DoesNotContain("node_modules", todo.Text);
        Assert.DoesNotContain("built", todo.Text);
        Assert.Equal("2 matches in 2 files", todo.Display);

        var context = await Run("grep", Args(new { pattern = "TODO", glob = "*.cs", context = 1 }), Context());
        Assert.Equal("src/Api/Program.cs:1- var app = Build();\nsrc/Api/Program.cs:2: // TODO: tidy\nsrc/Api/Program.cs:3- app.Run();", context.Text);
        var files = await Run("grep", Args(new { pattern = "app", output = "files" }), Context());
        Assert.Equal("src/Api/Program.cs", files.Text);
        await Assert.ThrowsAsync<ToolError>(() => Run("grep", Args(new { pattern = "(unclosed" }), Context()));
    }

    [Fact]
    public async Task Run_shell_gives_output_and_exit_code_and_stops_a_command_at_its_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var ok = await Run("run_shell", Args(new { command = "echo out; echo err >&2; pwd" }), Context());
        Assert.False(ok.Error);
        Assert.Contains("out", ok.Text);
        Assert.Contains("err", ok.Text);
        Assert.Contains(_root, ok.Text);
        Assert.EndsWith("[exit code 0]", ok.Text);

        var failed = await Run("run_shell", Args(new { command = "exit 3" }), Context());
        Assert.True(failed.Error);
        Assert.Contains("[exit code 3]", failed.Text);

        var started = DateTime.UtcNow;
        var slow = await Run("run_shell", Args(new { command = "sleep 30", timeout_seconds = 1 }), Context());
        Assert.Contains("timed out after 1 s", slow.Text);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));

        // The key never reaches the commands the model runs.
        Environment.SetEnvironmentVariable("ARENA_API_KEY", "sk-should-not-leak");
        try
        {
            var env = await Run("run_shell", Args(new { command = "echo \"key=[$ARENA_API_KEY]\"" }), Context());
            Assert.Contains("key=[]", env.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARENA_API_KEY", null);
        }
    }

    [Fact]
    public async Task Git_reads_the_repository_but_leaves_changes_to_run_shell()
    {
        var refused = await Assert.ThrowsAsync<ToolError>(() => Run("git", Args(new { args = new[] { "commit", "-m", "x" } }), Context()));
        Assert.Contains("run_shell", refused.Message);
        await Assert.ThrowsAsync<ToolError>(() => Run("git", Args(new { args = new[] { "branch", "-D", "main" } }), Context()));
        await Assert.ThrowsAsync<ToolError>(() => Run("git", Args(new { args = new[] { "diff", "--output=/tmp/x" } }), Context()));
    }

    [Fact]
    public async Task Git_and_the_file_lists_run_no_program_the_repositorys_own_settings_name()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the traps are shell commands
        }
        using var traps = new GitTraps();
        var context = new ToolContext { Workspace = new Workspace(traps.Repo), Ui = new Ui(new StringReader(""), _out, _out, color: false, canAsk: false) };

        // grep, glob, the IDE's search and quick open list the files through git: its fsmonitor hook does not run.
        Assert.Equal([".gitattributes", "a.txt", "b.dat", "c.txt"], context.Workspace.FilesUnder(traps.Repo, CancellationToken.None).Select(Path.GetFileName).Order());
        Assert.Empty(traps.Ran);

        // Every read the git tool takes: no fsmonitor, hook, filter, text conversion, external diff or signature checker.
        string[][] reads =
        [
            ["status"], ["diff"], ["diff", "HEAD"], ["log", "-p", "-2"], ["log", "-1", "--show-signature"], ["show"], ["show", "HEAD~1"],
            ["blame", "a.txt"], ["blame", "c.txt"], ["describe", "--always"], ["ls-files", "-m"], ["branch", "-v"], ["rev-parse", "HEAD"], ["shortlog", "-1", "HEAD"],
        ];
        var said = new Dictionary<string, string>();
        foreach (var read in reads)
        {
            said[string.Join(' ', read)] = (await Run("git", Args(new { args = read }), context)).Text;
            Assert.True(traps.Ran.Length == 0, $"git {string.Join(' ', read)} ran {string.Join(", ", traps.Ran)}: {said[string.Join(' ', read)]}");
        }
        // And they still read: a.txt changed, as git sees it without the filters.
        Assert.Contains("modified:   a.txt", said["status"]);
        Assert.DoesNotContain("c.txt", said["status"]);
        Assert.Contains("+three", said["diff"]);
        Assert.Contains("signed", said["log -1 --show-signature"]);

        // Nor does it read outside the working directory, or look into what it cannot guard.
        foreach (var refused in new[]
        {
            new[] { "diff", "--no-index", "a.txt", "/etc/hostname" }, ["diff", "a.txt", "/etc/hostname"], ["diff", "../marks"],
            ["blame", "--contents=/etc/hostname", "a.txt"], ["blame", "--contents", "/etc/hostname", "a.txt"],
            ["diff", "--textconv"], ["log", "-p", "--ext-diff"], ["status", "--ignore-submodules=none"], ["log", "--submodule=diff"], ["describe", "--dirty"],
        })
        {
            await Assert.ThrowsAsync<ToolError>(() => Run("git", Args(new { args = refused }), context));
        }
        Assert.Empty(traps.Ran);
    }

    [Fact]
    public async Task The_todo_list_is_kept_and_shown()
    {
        var context = Context();
        List<TodoItem>? saved = null;
        context.TodosChanged = t => saved = t;
        var result = await Run("todo_write", """{"todos":[{"content":"Read the code","status":"completed"},{"content":"Fix the bug","status":"in_progress"},{"content":"Run the tests","status":"pending"}]}""", context);
        Assert.Equal("The to-do list has 3 items: 1 done, 1 in progress, 1 pending.", result.Text);
        Assert.Equal(3, saved!.Count);
        Assert.Equal("☒ Read the code\n◐ Fix the bug\n☐ Run the tests", result.Display);
    }

    [Fact]
    public void Diffs_show_removed_then_added_lines_with_context_and_numbers()
    {
        var ui = new Ui(new StringReader(""), _out, _out, false, false);
        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"l{i}")) + "\n";
        var after = before.Replace("l10\n", "ten\n").Replace("l11\n", "");
        var diff = Diff.Render(before, after, ui);
        Assert.Equal(
            """
              7   l7
              8   l8
              9   l9
             10 - l10
             11 - l11
             10 + ten
             11   l12
             12   l13
             13   l14
            """.Replace("\r", ""), diff);
        Assert.Equal((1, 2), Diff.Count(before, after));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
