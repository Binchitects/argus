using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Tests;

/// <summary>
/// The tool results the model has used, made small (shared by Code Arena and the web chat): summarised with their key lines
/// and a handle, file reads as their outline and the lines cited, older reads of a file read again dropped; never the
/// results the model has not answered yet.
/// </summary>
public sealed class ResultCondenserTests
{
    private static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };

    private static JsonObject Say(string text) => new() { ["role"] = "assistant", ["content"] = text };

    private static JsonObject Calls(string said, params (string Id, string Name, JsonObject Args)[] calls) => new()
    {
        ["role"] = "assistant", ["content"] = said,
        ["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToJsonString() },
        })]),
    };

    private static JsonObject Result(string id, string text) => new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text };

    private static string Content(JsonObject m) => m["content"]!.GetValue<string>();

    /// <summary>A build's log: 300 lines of noise, one error in the middle with its place, the figures at the end.</summary>
    private static string BuildLog() => string.Join('\n',
        Enumerable.Range(1, 150).Select(i => $"  Compiling module {i} of 300 … done").Append("src/Shop/Cart.cs(42,17): error CS1002: ; expected")
            .Concat(Enumerable.Range(151, 150).Select(i => $"  Compiling module {i} of 300 … done")).Append("Build FAILED.").Append("    1 Error(s), 0 Warning(s)"));

    /// <summary>A file read as read_file numbers it: lines from <paramref name="from"/>.</summary>
    private static string Read(int from, IEnumerable<string> lines) => string.Concat(lines.Select((l, i) => $"{from + i,6}\t{l}\n"));

    private static List<string> CartLines() =>
    [
        "namespace Shop;", "", "public sealed class Cart", "{",
        .. Enumerable.Range(0, 40).SelectMany(i => new[] { $"    public decimal Price{i}(int count)", "    {", $"        return count * {i}.5m; // the price of item {i}", "    }", "" }),
        "    public decimal Total(IEnumerable<int> items) => items.Sum(i => Price0(i));", "}",
    ];

    private static ResultCondenser Condenser(List<(string Tool, string Text)>? kept = null) => new()
    {
        UseOf = name => name switch { "read_file" => ToolUse.Read, "edit_file" or "write_file" => ToolUse.Write, _ => ToolUse.Other },
        Keep = (tool, call, text) =>
        {
            kept?.Add((tool, text));
            return $"kept as {call}";
        },
    };

    [Fact]
    public void A_result_two_answers_behind_is_summarised_with_its_key_lines_and_a_handle()
    {
        var log = BuildLog();
        var messages = new List<JsonObject>
        {
            User("Build it and fix what fails."),
            Calls("", ("c1", "run_shell", new JsonObject { ["command"] = "dotnet build" })),
            Result("c1", log),
            Calls("It fails in Cart.cs at line 42.", ("c2", "run_shell", new JsonObject { ["command"] = "git status" })),
            Result("c2", "nothing to commit"),
            Calls("", ("c3", "run_shell", new JsonObject { ["command"] = "dotnet build -v q" })),
            Result("c3", log),
        };
        var kept = new List<(string, string)>();
        var done = Condenser(kept).Condense(messages);

        Assert.Equal(1, done.Summarised);
        var summary = Content(messages[2]);
        Assert.StartsWith(ResultCondenser.SummaryMark + " run_shell {\"command\":\"dotnet build\"} gave", summary, StringComparison.Ordinal);
        Assert.Contains("The whole result: kept as c1.", summary, StringComparison.Ordinal);
        // The error with its place, the figures at the end, and the first line, by their lines in the result.
        Assert.Contains("   151\tsrc/Shop/Cart.cs(42,17): error CS1002: ; expected", summary, StringComparison.Ordinal);
        Assert.Contains("   303\t    1 Error(s), 0 Warning(s)", summary, StringComparison.Ordinal);
        Assert.Contains("     1\t  Compiling module 1 of 300", summary, StringComparison.Ordinal);
        Assert.True(summary.Length < 1_500, summary);
        // Kept whole, as it was, for the handle.
        Assert.Equal(log, Assert.Single(kept).Item2);
        // The step just before keeps its results whole, and so does the one the model has not answered yet.
        Assert.Equal("nothing to commit", Content(messages[4]));
        Assert.Equal(log, Content(messages[6]));
        Assert.Equal(log.Length - summary.Length, done.Freed);

        // Again: nothing more to do.
        Assert.Equal(0, Condenser().Condense(messages).Count);
    }

    [Fact]
    public void A_pass_that_would_free_little_changes_nothing_so_the_cached_start_stays()
    {
        var small = string.Join('\n', Enumerable.Range(1, 60).Select(i => $"value {i}: some words that say nothing much"));
        var messages = new List<JsonObject>
        {
            User("Look."),
            Calls("", ("c1", "grep", new JsonObject { ["pattern"] = "x" })),
            Result("c1", small),
            Say("Found."),
            User("And?"),
            Say("Done."),
        };
        Assert.Equal(0, Condenser().Condense(messages).Count);
        Assert.Equal(small, Content(messages[2]));
        // With less asked of a pass, it goes.
        Assert.Equal(1, new ResultCondenser { MinFreed = 0, SummariseAbove = 100, Keep = (_, c, _) => "kept as " + c }.Condense(messages).Summarised);
    }

    [Fact]
    public void A_result_that_cannot_be_kept_stays_whole_but_a_file_read_need_not_be_kept()
    {
        var log = BuildLog();
        var lines = CartLines();
        var messages = new List<JsonObject>
        {
            User("Go."),
            Calls("", ("c1", "run_shell", new JsonObject { ["command"] = "make" }), ("c2", "read_file", new JsonObject { ["path"] = "src/Cart.cs" })),
            Result("c1", log),
            Result("c2", Read(1, lines)),
            Say("Read both."),
            User("Next."),
            Say("Ok."),
        };
        var done = new ResultCondenser
        {
            UseOf = name => name == "read_file" ? ToolUse.Read : ToolUse.Other,
            Keep = (_, _, _) => null,
        }.Condense(messages);
        Assert.Equal((0, 1), (done.Summarised, done.Outlined));
        Assert.Equal(log, Content(messages[2]));
        Assert.Contains("the whole read: read_file reads it again", Content(messages[3]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_read_once_the_model_moved_on_is_its_outline_and_the_lines_it_cited()
    {
        var lines = CartLines();
        var read = Read(1, lines);
        var messages = new List<JsonObject>
        {
            User("Make item 7 cost more."),
            Calls("", ("c1", "read_file", new JsonObject { ["path"] = "src/Cart.cs" })),
            Result("c1", read),
            // Cited by an edit's old_string, by a line number, and by a name written as code.
            Calls("`Total` sums them; Price7 is at src/Cart.cs:40.", ("c2", "edit_file", new JsonObject
            {
                ["path"] = "src/Cart.cs", ["old_string"] = "        return count * 7.5m; // the price of item 7", ["new_string"] = "        return count * 9.5m;",
            })),
            Result("c2", "Edited src/Cart.cs."),
            Calls("", ("c3", "run_shell", new JsonObject { ["command"] = "dotnet test" })),
            Result("c3", "Passed: 12"),
            Calls("", ("c4", "grep", new JsonObject { ["pattern"] = "Total" })),
            Result("c4", "src/Cart.cs:205: Total"),
        };
        var done = Condenser().Condense(messages);

        Assert.Equal(1, done.Outlined);
        var outlined = Content(messages[2]);
        Assert.StartsWith(ResultCondenser.SummaryMark + " read_file {\"path\":\"src/Cart.cs\"}: lines 1-206", outlined, StringComparison.Ordinal);
        Assert.Contains("the whole read: kept as c1", outlined, StringComparison.Ordinal);
        Assert.Contains("Outline:", outlined, StringComparison.Ordinal);
        Assert.Contains("public sealed class Cart  [3]", outlined, StringComparison.Ordinal);
        Assert.Contains("    40\t    public decimal Price7(int count)", outlined, StringComparison.Ordinal);
        Assert.Contains("    42\t        return count * 7.5m; // the price of item 7", outlined, StringComparison.Ordinal);
        Assert.Contains("   205\t    public decimal Total(IEnumerable<int> items) => items.Sum(i => Price0(i));", outlined, StringComparison.Ordinal);
        Assert.DoesNotContain("item 30", outlined, StringComparison.Ordinal);
        Assert.True(outlined.Length < read.Length / 2, outlined);
    }

    [Fact]
    public void A_file_the_latest_steps_still_touch_stays_whole()
    {
        var read = Read(1, CartLines());
        var messages = new List<JsonObject>
        {
            User("Fix it."),
            Calls("", ("c1", "read_file", new JsonObject { ["path"] = "src/Cart.cs" })),
            Result("c1", read),
            Calls("", ("c2", "run_shell", new JsonObject { ["command"] = "dotnet build" })),
            Result("c2", "ok"),
            Calls("", ("c3", "edit_file", new JsonObject { ["path"] = "./src/Cart.cs", ["old_string"] = "a", ["new_string"] = "b" })),
            Result("c3", "Edited src/Cart.cs."),
        };
        Assert.Equal(0, Condenser().Condense(messages).Count);
        Assert.Equal(read, Content(messages[2]));
    }

    [Fact]
    public void A_file_read_again_after_an_edit_drops_the_older_read_even_when_recent()
    {
        var before = Read(1, CartLines());
        var after = Read(1, CartLines().Select(l => l.Replace("7.5m", "9.5m", StringComparison.Ordinal)));
        var messages = new List<JsonObject>
        {
            User("Fix it."),
            Calls("", ("c1", "read_file", new JsonObject { ["path"] = "src/Cart.cs" })),
            Result("c1", before),
            Calls("", ("c2", "edit_file", new JsonObject { ["path"] = "src/Cart.cs", ["old_string"] = "7.5m", ["new_string"] = "9.5m" })),
            Result("c2", "Edited src/Cart.cs."),
            Calls("", ("c3", "read_file", new JsonObject { ["path"] = "src/Cart.cs" })),
            Result("c3", after),
        };
        var done = Condenser().Condense(messages);
        Assert.Equal(1, done.Dropped);
        Assert.Equal($"{ResultCondenser.DroppedMark} read_file of src/Cart.cs ({before.Length:N0} characters): it was read again after it was edited further on, and that read stands.]", Content(messages[2]));
        Assert.Equal(after, Content(messages[6]));
    }

    [Fact]
    public void A_file_read_again_the_same_way_drops_the_older_read_and_another_part_does_not()
    {
        var lines = CartLines();
        var messages = new List<JsonObject>
        {
            User("Look."),
            Calls("", ("c1", "read_file", new JsonObject { ["path"] = "src/Cart.cs", ["offset"] = 1, ["limit"] = 50 })),
            Result("c1", Read(1, lines.Take(50))),
            Calls("", ("c2", "read_file", new JsonObject { ["path"] = "src/Cart.cs", ["offset"] = 51, ["limit"] = 50 })),
            Result("c2", Read(51, lines.Skip(50).Take(50))),
            Calls("", ("c3", "read_file", new JsonObject { ["path"] = "src/Cart.cs", ["offset"] = 1, ["limit"] = 50 })),
            Result("c3", Read(1, lines.Take(50))),
        };
        var done = Condenser().Condense(messages);
        Assert.Equal(1, done.Dropped);
        Assert.StartsWith(ResultCondenser.DroppedMark, Content(messages[2]), StringComparison.Ordinal);
        Assert.Contains("read again the same way", Content(messages[2]), StringComparison.Ordinal);
        Assert.StartsWith("    51\t", Content(messages[4]), StringComparison.Ordinal);
    }

    [Fact]
    public void Results_the_model_has_not_answered_are_never_touched()
    {
        var log = BuildLog();
        var messages = new List<JsonObject>
        {
            User("Build."),
            Calls("", ("c1", "run_shell", new JsonObject { ["command"] = "make" })),
            Result("c1", log),
        };
        Assert.Equal(0, Condenser().Condense(messages).Count);
        Assert.Equal(log, Content(messages[2]));
        // Answered once: the next step may still need it whole.
        messages.Add(Calls("", ("c2", "run_shell", new JsonObject { ["command"] = "make again" })));
        messages.Add(Result("c2", "ok"));
        Assert.Equal(0, Condenser().Condense(messages).Count);
        // Answered twice: summarised.
        messages.Add(Say("Done."));
        Assert.Equal(1, Condenser().Condense(messages).Summarised);
    }

    [Fact]
    public void What_a_pass_did_is_said_in_a_line()
    {
        Assert.Equal("Summarised 2 earlier tool results, left 1 file read as its outline and the lines cited, dropped 1 older read of files read again (~1,000 tokens freed).",
            new Condensed(2, 1, 1, 3_000).Describe(3.0));
        Assert.Equal("", default(Condensed).Describe(3.0));
    }
}
