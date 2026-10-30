using System.Text;

namespace CodeArena.Tests;

/// <summary>Terminals that run a named program (a task, a run): its folder, variables, title and exit code, and the terminal used again by the next run.</summary>
public sealed class TerminalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("code-arena-terminals-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Terminals New() => new(new Workspace(_root), new Config(), _ => null);

    /// <summary>All a terminal showed, once its program ended.</summary>
    internal static string Output(Terminal terminal) => Encoding.UTF8.GetString(terminal.Attach().Backlog);

    [Fact]
    public async Task A_shell_opens_in_the_folder_it_is_given()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var sub = Directory.CreateDirectory(Path.Combine(_root, "src", "lib")).FullName;
        // The "shell" here prints where it is and ends: what a shell would start in.
        await using var terminals = new Terminals(new Workspace(_root), new Config { TerminalShell = "/bin/pwd" }, _ => null);
        var terminal = terminals.Open(80, 24, null, sub);
        Assert.Equal(0, await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains(sub + "\r\n", Output(terminal));
    }

    [Fact]
    public async Task A_terminal_runs_a_named_program_in_its_folder_with_its_variables_and_ends_with_its_exit_code()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        await using var terminals = New();
        var env = new Dictionary<string, string?> { ["GREETING"] = "hi", ["HOME"] = null, ["ARENA_API_KEY"] = "sk-not-passed-on" };
        var terminal = terminals.Open(80, 24, new TerminalStart("sh", ["-c", "pwd; echo \"[$GREETING] [${HOME-unset}] [${ARENA_API_KEY-none}]\"; exit 3"], sub, env, "Build", TerminalKind.Task, "task:Build"));

        Assert.Equal(3, await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(15)));
        var text = Output(terminal);
        Assert.Contains(sub + "\r\n", text);
        // Set, unset, and the key kept out whatever the task says.
        Assert.Contains("[hi] [unset] [none]", text);
        Assert.DoesNotContain("sk-not-passed-on", text);
        var json = terminal.Json();
        Assert.Equal("task", json["kind"]!.GetValue<string>());
        Assert.Equal("Build", json["title"]!.GetValue<string>());
        Assert.Equal(3, json["exitCode"]!.GetValue<int>());

        // The person's own shell is a shell, named after its program.
        var shell = terminals.Open(80, 24);
        Assert.Equal("shell", shell.Json()["kind"]!.GetValue<string>());
        terminals.Close(shell.Id);
    }

    [Fact]
    public async Task A_terminal_whose_program_ended_is_used_again_by_the_same_key_and_one_still_running_is_not()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        await using var terminals = New();
        var first = terminals.Open(80, 24, new TerminalStart("sh", ["-c", "echo first"], ReuseKey: "task:a"));
        Assert.Equal(0, await first.Exited.WaitAsync(TimeSpan.FromSeconds(15)));

        var second = terminals.Open(80, 24, new TerminalStart("sh", ["-c", "sleep 30"], ReuseKey: "task:a"));
        Assert.Equal(first.Id, second.Id);
        Assert.Same(second, Assert.Single(terminals.All));
        Assert.Same(second, terminals.Find(first.Id));

        // While it runs, the same key opens another; another key, or none, never takes it.
        var third = terminals.Open(80, 24, new TerminalStart("sh", ["-c", "sleep 30"], ReuseKey: "task:a"));
        Assert.NotEqual(second.Id, third.Id);
        var other = terminals.Open(80, 24, new TerminalStart("sh", ["-c", "exit 0"], ReuseKey: "task:b"));
        await other.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(other.Id, terminals.Open(80, 24, new TerminalStart("sh", ["-c", "exit 0"])).Id);
        Assert.Equal(4, terminals.All.Count);
    }

    [Fact]
    public async Task A_missing_program_or_folder_is_refused_and_sixteen_running_programs_are_the_most()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        await using var terminals = New();
        var missing = Assert.Throws<IdeError>(() => terminals.Open(80, 24, new TerminalStart("no-such-program-here", [])));
        Assert.Equal((400, "no_program"), (missing.Status, missing.Code));
        var nowhere = Assert.Throws<IdeError>(() => terminals.Open(80, 24, new TerminalStart("sh", [], Path.Combine(_root, "gone"))));
        Assert.Equal(400, nowhere.Status);

        for (var i = 0; i < Terminals.Max; i++)
        {
            terminals.Open(80, 24, new TerminalStart("sleep", ["30"]));
        }
        var tooMany = Assert.Throws<IdeError>(() => terminals.Open(80, 24, new TerminalStart("sleep", ["30"])));
        Assert.Equal((409, "too_many"), (tooMany.Status, tooMany.Code));
        Assert.Equal(16, Terminals.Max);
    }
}
