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

    [Fact]
    public async Task The_IDE_reads_what_fills_the_window_with_the_memory_apart_and_its_biggest_messages()
    {
        _mcp.Chats.Memories.Add(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["text"] = "Prefers answers in British English", ["scope"] = "person", ["kind"] = "preference",
            ["project"] = null, ["pinned"] = false, ["mine"] = true,
        });
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "big.txt"), Lines("the big file", 200));
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("read") : Reply.Call(("read_file", """{"path":"big.txt"}"""));
        await using var web = await WebRun.StartAsync(h);
        using (var stream = await web.StreamAsync(HttpMethod.Post, "/api/messages", new JsonObject { ["text"] = "read big.txt" }))
        {
            await stream.RestAsync();
        }
        var until = DateTime.UtcNow.AddSeconds(30);
        while ((await web.GetJsonAsync("/api/session"))["busy"]!.GetValue<bool>())
        {
            Assert.True(DateTime.UtcNow < until, "The turn never ended.");
            await Task.Delay(20);
        }

        var window = await web.GetJsonAsync("/api/window");
        var kinds = window["kinds"]!.AsArray().ToDictionary(k => k!["kind"]!.GetValue<string>(), k => k!["tokens"]!.GetValue<long>());
        Assert.True(kinds["instructions"] > 0);
        Assert.True(kinds["memory"] > 0);
        Assert.True(kinds["tools' results"] > 1_000);
        var biggest = window["biggest"]!.AsArray()[0]!;
        Assert.Equal("tool", biggest["role"]!.GetValue<string>());
        Assert.StartsWith("read_file's result", biggest["what"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_session_finds_and_reads_again_what_its_summary_covers_by_the_numbers_it_cites()
    {
        _gateway.Context = 20_000;
        using var h = new Harness(_gateway, _mcp, c => c["compactAt"] = 50);
        var asks = new List<string>();
        _gateway.Answer = req =>
        {
            var last = FakeGateway.Last(req);
            if (FakeGateway.Summarizing(req))
            {
                asks.Add(last);
                return Reply.Say("## Goal\n- pick a vendor (#1)");
            }
            return last switch
            {
                "find it" => Reply.Call(("session_search", """{"query":"Northwind vendor"}""")),
                "read it" => Reply.Call(("session_read", """{"from":1,"to":2}""")),
                _ => FakeGateway.HasToolResults(req) && req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "tool" ? Reply.Say("done") : Reply.Say("noted " + new string('n', 3000)),
            };
        };
        var input = "We chose Northwind Traders as the vendor.\n" + string.Concat(Enumerable.Range(1, 6).Select(i => $"step {i} " + new string('s', 3000) + "\n")) + "find it\nread it\n/exit\n";
        Assert.Equal(0, await h.Run(input, "chat", "--mode", "auto-edit"));

        // The summary is asked with the messages' numbers, and the person's first words.
        var ask = asks[0];
        Assert.Contains("are #1 to #", ask, StringComparison.Ordinal);
        Assert.Contains("#1 \"We chose Northwind Traders as the vendor.\"", ask, StringComparison.Ordinal);
        Assert.Contains("## Earlier parts", ask, StringComparison.Ordinal);
        var after = _gateway.Requests.Last(r => !FakeGateway.Summarizing(r))["messages"]!.AsArray();
        Assert.Contains("session_read reads them again", after[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        // Found, then read word for word, though the summary stands for it.
        var results = Results(_gateway.Requests[^1]);
        Assert.Contains("#1 the person: We chose Northwind Traders as the vendor.", results[^2], StringComparison.Ordinal);
        Assert.StartsWith("#1 the person: We chose Northwind Traders as the vendor.\n\n#2 you: noted nnn", results[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Past_chats_and_sessions_everywhere_are_found_and_read_through_Arena()
    {
        using var h = new Harness(_gateway, _mcp);
        var step = 0;
        _gateway.Answer = _ => ++step switch
        {
            1 => Reply.Call(("search_history", """{"query":"parts vendor"}""")),
            2 => Reply.Call(("open_chat", """{"chat":"c1","query":"Northwind"}""")),
            _ => Reply.Say("Northwind."),
        };
        Assert.Equal(0, await h.Run("", "-p", "which vendor did we pick?", "--mode", "auto-edit"));
        Assert.Equal([("search", "parts vendor"), ("open", "c1")], _mcp.Chats.HistoryAsked.Select(x => (x.What, x.Body["query"]?.GetValue<string>() is { } q && x.What == "search" ? q : x.Body["chat"]!.GetValue<string>())));
        var results = Results(_gateway.Requests[^1]);
        Assert.StartsWith("[c1] \"Parts vendor\"", results[0], StringComparison.Ordinal);
        Assert.Equal("#1 the person: We chose Northwind Traders.", results[1]);
    }
}
