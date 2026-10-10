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

/// <summary>A gateway that restarts, a stream that stalls, a connection that drops mid-answer: the harness carries on.</summary>
public sealed class ReliabilityTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    private GatewayClient Client(List<string> said) => new(new HttpClient(), _gateway.Url, FakeGateway.Key)
    {
        RetryWaits = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
        FirstEventWait = TimeSpan.FromMilliseconds(500),
        EventWait = TimeSpan.FromMilliseconds(500),
        Retrying = said.Add,
    };

    private static JsonObject Ask(string text) => new()
    {
        ["model"] = "model-a",
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }),
    };

    [Fact]
    public async Task A_gateway_that_restarts_is_tried_again_and_the_person_is_told()
    {
        var said = new List<string>();
        var calls = 0;
        _gateway.Failure = _ => Interlocked.Increment(ref calls) <= 2 ? 503 : 0;
        _gateway.Answer = _ => Reply.Say("up again");
        var answer = await Client(said).CompleteAsync(Ask("hi"), null, CancellationToken.None);
        Assert.Equal("up again", answer.Text);
        Assert.Equal(2, said.Count);
        Assert.Contains("trying again in", said[0]);
        Assert.Contains("(2 of 4)", said[0]);
    }

    [Fact]
    public async Task A_stream_that_never_says_anything_is_given_up_and_tried_again()
    {
        var said = new List<string>();
        var calls = 0;
        _gateway.Silent = _ => Interlocked.Increment(ref calls) == 1;
        _gateway.Answer = _ => Reply.Say("second time lucky");
        var answer = await Client(said).CompleteAsync(Ask("hi"), null, CancellationToken.None);
        Assert.Equal("second time lucky", answer.Text);
        Assert.Contains("the answer did not start", Assert.Single(said));
    }

    [Fact]
    public async Task A_stream_that_stalls_after_part_of_the_answer_keeps_that_part()
    {
        _gateway.Hang = true;
        var answer = await Client([]).CompleteAsync(Ask("hi"), null, CancellationToken.None);
        Assert.Equal(GatewayClient.Interrupted, answer.FinishReason);
        Assert.Equal("Let me think", answer.Text);
    }

    [Fact]
    public async Task After_a_connection_drops_mid_answer_the_model_carries_on_from_where_it_stopped()
    {
        using var h = new Harness(_gateway);
        var calls = 0;
        _gateway.Drop = _ => Interlocked.Increment(ref calls) == 1;
        _gateway.Answer = req => FakeGateway.Last(req).StartsWith("(The connection dropped", StringComparison.Ordinal) ? Reply.Say("the rest.") : Reply.Say("The first half and");
        Assert.Equal(0, await h.Run("", "-p", "tell me"));
        var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["role"] + ": " + m["content"]).ToList();
        Assert.True(sent.Count >= 3, $"{_gateway.Requests.Count} requests; out: {h.Out}; err: {h.Err}; sent: {string.Join(" | ", sent)}");
        Assert.Equal("user: tell me", sent[0]);
        Assert.Equal("assistant: The first", sent[1]);
        Assert.StartsWith("user: (The connection dropped while you were answering", sent[2]);
        Assert.Contains("the rest.", h.Out);
    }
}
