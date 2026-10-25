using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The window, managed: a big result kept aside and read again, earlier results cleared before compacting, and /context.</summary>
public sealed class WindowTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static List<string> Results(JsonObject request) =>
        [.. request["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.ToString())];

    private static string Lines(string name, int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"line {i} of {name}, some words to fill it\n"));

    [Fact]
    public async Task A_result_too_big_to_go_whole_is_kept_aside_and_read_again_in_parts()
    {
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "big.txt"), Lines("the big file", 1_500));
        var step = 0;
        _gateway.Answer = _ => ++step switch
        {
            1 => Reply.Call(("read_file", """{"path":"big.txt"}""")),
            2 => Reply.Call(("read_result", """{"id":"r1","find":"line 1200 of"}""")),
            3 => Reply.Call(("read_result", """{"id":"r1","from":700,"lines":2}""")),
            _ => Reply.Say("read it"),
        };
        Assert.Equal(0, await h.Run("", "-p", "read big.txt", "--mode", "auto-edit"));
        var results = Results(_gateway.Requests[^1]);
        // The start and the end, and where the rest is.
        Assert.Contains("line 1 of the big file", results[0], StringComparison.Ordinal);
        Assert.Contains("is kept as r1", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("line 700 of the big file", results[0], StringComparison.Ordinal);
        Assert.Contains("line 1500 of the big file", results[0], StringComparison.Ordinal);
        // Read again: the lines that hold some text, and a range.
        Assert.Contains("line 1200 of the big file", results[1], StringComparison.Ordinal);
        Assert.Contains("line 700 of the big file", results[2], StringComparison.Ordinal);
        Assert.Contains("line 701 of the big file", results[2], StringComparison.Ordinal);
        Assert.DoesNotContain("line 702 of the big file", results[2], StringComparison.Ordinal);
        Assert.Single(Directory.GetDirectories(h.Paths.SessionsDir, "*.results"));
    }

    [Fact]
    public async Task Before_compacting_the_earlier_results_are_cleared_and_kept_and_often_that_is_enough()
    {
        _gateway.Context = 40_000;
        using var h = new Harness(_gateway, _mcp, c => c["compactAt"] = 30);
        for (var i = 1; i <= 5; i++)
        {
            File.WriteAllText(Path.Combine(h.Work, $"f{i}.txt"), Lines($"file {i}", 220));
        }
        // Each question reads its file, then the answer says so.
        _gateway.Answer = req => FakeGateway.Last(req) is var last && last.StartsWith("read f", StringComparison.Ordinal)
            ? Reply.Call(("read_file", $$"""{"path":"{{last.Split(' ')[1]}}"}"""))
            : Reply.Say("read");
        Assert.Equal(0, await h.Run("read f1.txt\nread f2.txt\nread f3.txt\nread f4.txt\nread f5.txt\n/context\n/exit\n", "chat", "--mode", "auto-edit"));

        Assert.DoesNotContain(_gateway.Requests, FakeGateway.Summarizing);
        var results = Results(_gateway.Requests[^1]);
        Assert.StartsWith("[Cleared to make room in the window: read_file's result", results[0], StringComparison.Ordinal);
        Assert.Contains("line 1 of file 5", results[^1], StringComparison.Ordinal);
        Assert.Contains("Cleared ", h.Out + h.Err);
        // /context: what fills the window, and its biggest.
        Assert.Contains("tools' results", h.Out, StringComparison.Ordinal);
        Assert.Contains("read_file's result", h.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_summary_is_asked_in_sections_and_the_to_do_list_is_said_after_it_as_it_stands()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => FakeGateway.Summarizing(req) ? Reply.Say("## Goal\n- fix the bug")
            : FakeGateway.Last(req) == "plan it" ? Reply.Call(("todo_write", """{"todos":[{"content":"Read the code","status":"completed"},{"content":"Fix the bug","status":"in_progress"}]}"""))
            : Reply.Say("ok");
        Assert.Equal(0, await h.Run("plan it\n/compact\nwhere are we?\n/exit\n", "chat", "--mode", "auto-edit"));

        var asked = FakeGateway.Last(_gateway.Requests.Single(FakeGateway.Summarizing));
        Assert.Contains("## Decisions (and why)", asked, StringComparison.Ordinal);
        Assert.Contains("## Next steps", asked, StringComparison.Ordinal);
        var summary = _gateway.Requests[^1]["messages"]!.AsArray()[1]!["content"]!.GetValue<string>();
        Assert.Contains("## Goal\n- fix the bug", summary, StringComparison.Ordinal);
        Assert.Contains("Your to-do list as it stands (todo_write changes it):\n- [completed] Read the code\n- [in_progress] Fix the bug", summary, StringComparison.Ordinal);
    }
}
