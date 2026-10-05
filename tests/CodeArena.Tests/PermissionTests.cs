using System.Text.Json.Nodes;

namespace CodeArena.Tests;

public sealed class PermissionTests
{
    private static readonly ToolDef Read = LocalTools.All().Single(t => t.Name == "read_file");
    private static readonly ToolDef Edit = LocalTools.All().Single(t => t.Name == "edit_file");
    private static readonly ToolDef Shell = LocalTools.All().Single(t => t.Name == "run_shell");

    private static ToolDef RemoteTool(bool trusted, bool readOnly = false) => new()
    {
        Name = "mcp__tickets__close",
        Description = "",
        Parameters = [],
        Kind = ToolKind.Remote,
        Server = "tickets",
        Trusted = trusted,
        ChangesNothing = readOnly,
        Run = (_, _, _) => Task.FromResult(new ToolResult("ok")),
    };

    private static (Permissions, StringWriter) Make(Mode mode, string answers, bool canAsk = true)
    {
        var output = new StringWriter();
        return (new Permissions(new Ui(new StringReader(answers), output, output, false, canAsk), mode), output);
    }

    private static JsonObject Command(string command) => new() { ["command"] = command };

    [Fact]
    public async Task Ask_mode_asks_before_edits_and_commands_and_always_remembers_the_answer_for_the_session()
    {
        var (p, output) = Make(Mode.Ask, "n\ny\na\nn\n");
        Assert.Null(await p.CheckAsync(Read, [], default));
        Assert.Contains("declined", await p.CheckAsync(Edit, [], default));
        Assert.Null(await p.CheckAsync(Edit, [], default));
        Assert.Null(await p.CheckAsync(Edit, [], default)); // "always": file edits
        Assert.Null(await p.CheckAsync(Edit, [], default)); // ...asked no more
        Assert.Contains("declined", await p.CheckAsync(Shell, Command("npm test"), default)); // commands are asked about separately
        Assert.Equal(4, output.ToString().Split("Allow ").Length - 1);
        Assert.Contains("[a]lways for file edits", output.ToString());
    }

    [Fact]
    public async Task Always_for_a_command_covers_its_first_two_words_only()
    {
        var (p, output) = Make(Mode.Ask, "a\nn\n");
        Assert.Null(await p.CheckAsync(Shell, Command("npm test -- --watch"), default));
        Assert.Null(await p.CheckAsync(Shell, Command("npm test"), default));
        Assert.Contains("declined", await p.CheckAsync(Shell, Command("npm publish"), default));
        Assert.Contains("for `npm test …`", output.ToString());
        Assert.Equal("git", Permissions.CommandPrefix("git --no-pager log"));
        Assert.Equal("ls", Permissions.CommandPrefix("ls ./src"));
    }

    [Fact]
    public async Task Auto_edit_mode_edits_without_asking_but_asks_before_commands()
    {
        var (p, output) = Make(Mode.AutoEdit, "n\n");
        Assert.Null(await p.CheckAsync(Edit, [], default));
        Assert.Contains("declined", await p.CheckAsync(Shell, Command("rm -rf build"), default));
        Assert.Single(output.ToString().Split("Allow ").Skip(1));
    }

    [Fact]
    public async Task Plan_mode_is_read_only_and_offers_only_reading_tools()
    {
        var (p, output) = Make(Mode.Plan, "y\ny\ny\n");
        Assert.Null(await p.CheckAsync(Read, [], default));
        Assert.Contains("Plan mode is read-only", await p.CheckAsync(Edit, [], default));
        Assert.Contains("Plan mode is read-only", await p.CheckAsync(Shell, Command("ls"), default));
        Assert.Contains("Plan mode is read-only", await p.CheckAsync(RemoteTool(trusted: false), [], default));
        Assert.Null(await p.CheckAsync(RemoteTool(trusted: false, readOnly: true), [], default));
        Assert.Equal("", output.ToString());

        // A server the person trusts runs without asking, but is not taken to change nothing.
        Assert.Contains("Plan mode is read-only", await p.CheckAsync(RemoteTool(trusted: true), [], default));
        var box = new ToolBox([.. LocalTools.All(), LocalTools.SubAgentTool(), RemoteTool(trusted: true), RemoteTool(trusted: false, readOnly: true)]);
        var offered = box.Offered(Mode.Plan).Select(t => t.Name).ToList();
        Assert.DoesNotContain("edit_file", offered);
        Assert.DoesNotContain("write_file", offered);
        Assert.DoesNotContain("run_shell", offered);
        Assert.Contains("read_file", offered);
        Assert.Contains("task", offered);
        Assert.Single(offered, n => n == "mcp__tickets__close");
    }

    [Fact]
    public async Task Yolo_mode_asks_nothing_and_untrusted_MCP_tools_ask_in_the_other_modes()
    {
        var (yolo, quiet) = Make(Mode.Yolo, "");
        Assert.Null(await yolo.CheckAsync(Edit, [], default));
        Assert.Null(await yolo.CheckAsync(Shell, Command("make"), default));
        Assert.Null(await yolo.CheckAsync(RemoteTool(trusted: false), [], default));
        Assert.Equal("", quiet.ToString());

        var (ask, output) = Make(Mode.AutoEdit, "y\n");
        Assert.Null(await ask.CheckAsync(RemoteTool(trusted: true), [], default));
        Assert.Null(await ask.CheckAsync(RemoteTool(trusted: false), [], default));
        Assert.Contains("Allow mcp__tickets__close?", output.ToString());
    }

    [Fact]
    public async Task A_run_that_cannot_ask_refuses_and_says_which_mode_would_allow_it()
    {
        var (p, _) = Make(Mode.Ask, "y\n", canAsk: false);
        var refusal = await p.CheckAsync(Edit, [], default);
        Assert.Contains("cannot ask", refusal);
        Assert.Contains("--mode auto-edit", refusal);
    }
}
