using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>What Code Arena does as other coding agents do, through the program as the person runs it.</summary>
public sealed class ParityTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private string LastUser() => _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToString();

    private static string System(JsonObject request) => request["messages"]![0]!["content"]!.ToString();

    [Fact]
    public async Task Rewind_puts_back_the_files_a_turn_wrote_and_cuts_the_conversation()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = h.Write("a.txt", "old\n");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "change it" => Reply.Call(("edit_file", """{"path":"a.txt","old_string":"old","new_string":"new"}""")),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("changed"),
            var q => Reply.Say($"answer to {q}"),
        };
        Assert.Equal(0, await h.Run("first\nchange it\n/rewind\n/rewind 2\nafter\n/exit\n", "chat", "--mode", "auto-edit"));
        Assert.Equal("old\n", File.ReadAllText(file));
        Assert.Contains("1 file put back", h.Out);
        var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.ToString()).ToList();
        Assert.Equal(["first", "answer to first", "after"], sent);
    }

    [Fact]
    public async Task A_projects_own_command_is_its_prompt_with_the_arguments()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write(".arena/commands/greet.md", "---\ndescription: Greets someone\n---\nSay hello to $ARGUMENTS in one line.");
        Assert.Equal(0, await h.Run("/commands\n/greet Pat\n/exit\n", "chat"));
        Assert.Contains("/greet", h.Out);
        Assert.Contains("Greets someone", h.Out);
        Assert.Equal("Say hello to Pat in one line.", LastUser());
    }

    [Fact]
    public async Task A_deny_rule_refuses_a_command_whatever_the_mode_and_an_allow_rule_runs_one_unasked()
    {
        using var h = new Harness(_gateway, _mcp, c => c["permissions"] = new JsonObject
        {
            ["deny"] = new JsonArray("run_shell(rm *)"),
            ["allow"] = new JsonArray("run_shell(echo *)"),
        });
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("Result: " + FakeGateway.Last(req)) : Reply.Call(("run_shell", """{"command":"make && rm -rf build"}"""));
        Assert.Equal(0, await h.Run("", "-p", "clean", "--mode", "yolo"));
        Assert.Contains("A rule refuses this call (run_shell(rm *))", h.Out);

        // In ask mode a one-shot run cannot ask: the allowed command runs anyway.
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("Result: " + FakeGateway.Last(req)) : Reply.Call(("run_shell", """{"command":"echo allowed-by-rule"}"""));
        Assert.Equal(0, await h.Run("", "-p", "say it"));
        Assert.Contains("allowed-by-rule", h.Out);
    }

    [Fact]
    public async Task What_the_model_remembers_is_in_the_next_sessions_prompt_and_memory_lists_it()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("noted") : Reply.Call(("remember", """{"memory":"Run the tests with make check"}"""));
        Assert.Equal(0, await h.Run("", "-p", "remember how to test"));
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Contains("- Run the tests with make check", System(_gateway.Requests[^1]));
        Assert.Equal(0, await h.Run("/memory\n/exit\n", "chat"));
        Assert.Contains("1. Run the tests with make check", h.Out);
    }

    [Fact]
    public async Task A_command_run_with_a_bang_goes_with_the_next_message_and_an_at_file_with_its_message()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("notes.txt", "the notes' text");
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal(0, await h.Run("!echo from-the-shell\nwhat now? see @notes.txt\n/exit\n", "chat"));
        Assert.Contains("from-the-shell", h.Out);
        var asked = LastUser();
        Assert.StartsWith("I ran `echo from-the-shell` (exit 0):", asked);
        Assert.Contains("what now? see @notes.txt", asked);
        Assert.Contains("<file path=\"notes.txt\">\nthe notes' text\n</file>", asked);
    }

    [Fact]
    public async Task Other_agents_instructions_and_the_sandbox_say_what_applies()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("AGENTS.md", "Always answer in haiku.");
        _gateway.Answer = _ => Reply.Say("ok");
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Contains("Always answer in haiku.", System(_gateway.Requests[^1]));
        Assert.Equal(0, await h.Run("/sandbox\n/exit\n", "chat"));
        Assert.Matches("(On \\(bubblewrap\\)|Off)", h.Out);
    }
}
