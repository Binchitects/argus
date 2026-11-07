using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// "Super optimization on context in code-arena, summarize tool results, read files, and make tool calling more reliable":
/// the results the model has used go as summaries (a file read as its outline and the lines cited) before each step,
/// kept whole for read_result, in the session as in the requests; calls a model got wrong are mended or refused with the
/// tool's parameters, and counted.
/// </summary>
public sealed class CondenseTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static async Task<Runtime> StartAsync(Harness h)
    {
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        return await Runtime.StartAsync(new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
    }

    private static int Results(JsonObject request) => request["messages"]!.AsArray().Count(m => m!["role"]?.GetValue<string>() == "tool");

    private static List<JsonObject> Tools(JsonObject request) =>
        [.. request["messages"]!.AsArray().OfType<JsonObject>().Where(m => m["role"]?.GetValue<string>() == "tool")];

    /// <summary>A C# file of 300 lines: 50 methods, each one's price on its third line.</summary>
    private static string Shop() => "namespace Shop;\n\npublic sealed class Cart\n{\n" +
        string.Concat(Enumerable.Range(0, 50).Select(i => $"    public decimal Price{i}(int count)\n    {{\n        return count * {i}.5m; // the price of item {i}, as the shop sets it\n    }}\n\n")) +
        "    public decimal Total(int[] items) => items.Sum(Price0);\n}\n";

    [Fact]
    public async Task A_file_read_the_model_has_moved_on_from_goes_as_its_outline_and_the_lines_it_cited_kept_whole_for_read_result()
    {
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "Cart.cs"), Shop());
        _gateway.Answer = req => Results(req) switch
        {
            0 => Reply.Call(("read_file", """{"path":"Cart.cs"}""")),
            1 => new Reply("It is `Price7`, then.", [("grep", """{"pattern":"Price7","path":"Cart.cs"}""")]),
            2 => Reply.Call(("list_dir", "{}")),
            3 => Reply.Say("Price7 is at Cart.cs:40, and `Total` sums them."),
            _ => Reply.Say("ok"),
        };
        await using var rt = await StartAsync(h);
        await rt.Agent.RunAsync("Where is item 7 priced?", new Spend(), CancellationToken.None);

        // The step after the model stepped on: the read is its outline and the lines it cited; the step's own results are whole.
        var last = _gateway.Requests.Last();
        var read = Tools(last)[0]["content"]!.GetValue<string>();
        Assert.StartsWith("[Summarised once used: read_file {\"path\":\"Cart.cs\"}: lines 1-256", read, StringComparison.Ordinal);
        Assert.Contains("is kept as r1, read_result {\"id\":\"r1\"} reads it", read, StringComparison.Ordinal);
        Assert.Contains("class Cart", read, StringComparison.Ordinal);
        Assert.Contains("    40\t    public decimal Price7(int count)", read, StringComparison.Ordinal);
        Assert.True(read.Length < Shop().Length / 2, $"{read.Length} of {Shop().Length}");
        Assert.Contains("Price7", Tools(last)[1]["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, rt.Agent.CondensedSoFar.Outlined);

        // read_result reads the whole read again.
        var whole = KeptResults.Read(rt.Agent.Context, "r1")!;
        Assert.Contains("return count * 30.5m;", whole, StringComparison.Ordinal);

        // The session keeps the history the model reads (its result in place, by a light line), Arena's copy keeps it whole.
        var file = Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Single();
        Assert.Contains("\"type\":\"results\"", File.ReadAllText(file), StringComparison.Ordinal);
        var data = SessionStore.Load(file);
        Assert.StartsWith("[Summarised once used:", data.Messages[2]["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("return count * 30.5m;", SessionStore.Load(file, whole: true).Stream[2].Message["content"]!.GetValue<string>(), StringComparison.Ordinal);

        // A session resumed reads it as it was left.
        var resumed = SessionStore.Load(file);
        Assert.Equal(data.Messages.Select(m => m.ToJsonString()), resumed.Messages.Select(m => m.ToJsonString()));
    }

    [Fact]
    public async Task With_condensing_off_every_result_stays_whole()
    {
        using var h = new Harness(_gateway, _mcp, c => c["condenseResults"] = false);
        File.WriteAllText(Path.Combine(h.Work, "Cart.cs"), Shop());
        _gateway.Answer = req => Results(req) switch
        {
            0 => Reply.Call(("read_file", """{"path":"Cart.cs"}""")),
            1 => Reply.Call(("list_dir", "{}")),
            2 => Reply.Call(("list_dir", """{"path":"."}""")),
            _ => Reply.Say("done"),
        };
        await using var rt = await StartAsync(h);
        await rt.Agent.RunAsync("Read it.", new Spend(), CancellationToken.None);
        Assert.StartsWith("     1\tnamespace Shop;", Tools(_gateway.Requests.Last())[0]["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, rt.Agent.CondensedSoFar.Count);
    }

    [Fact]
    public async Task A_tool_and_arguments_named_as_another_harness_names_them_are_mended_and_the_history_keeps_the_mended_call()
    {
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "a.txt"), "alpha\nbeta\n");
        _gateway.Answer = req => Results(req) == 0 ? Reply.Call(("Read", """{"file_path":"a.txt","limit":"1",}""")) : Reply.Say("done");
        await using var rt = await StartAsync(h);
        await rt.Agent.RunAsync("Read a.txt.", new Spend(), CancellationToken.None);

        var last = _gateway.Requests.Last();
        var result = Tools(last).Single()["content"]!.GetValue<string>();
        Assert.StartsWith("     1\talpha\n", result, StringComparison.Ordinal);
        Assert.Contains("(This call was mended before it ran: Read → read_file; mended: a trailing comma; file_path → path; limit \"1\" → 1. Call read_file so next time.)", result, StringComparison.Ordinal);
        var call = last["messages"]!.AsArray().OfType<JsonObject>().Single(m => m["tool_calls"] is JsonArray)["tool_calls"]![0]!["function"]!;
        Assert.Equal("read_file", call["name"]!.GetValue<string>());
        Assert.Equal("""{"path":"a.txt","limit":1}""", call["arguments"]!.GetValue<string>());
        Assert.Equal((1, 1, 0), (rt.Agent.CallStats.Calls, rt.Agent.CallStats.Mended, rt.Agent.CallStats.Failed));
    }

    [Fact]
    public async Task Without_mending_the_same_call_fails_and_is_counted_as_its_fault()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Results(req) == 0 ? Reply.Call(("Read", """{"file_path":"a.txt"}""")) : Reply.Say("done");
        await using var rt = await StartAsync(h);
        rt.Agent.RepairCalls = false;
        await rt.Agent.RunAsync("Read a.txt.", new Spend(), CancellationToken.None);
        Assert.StartsWith("There is no tool named Read.", Tools(_gateway.Requests.Last()).Single()["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal((1, 1, 1), (rt.Agent.CallStats.Calls, rt.Agent.CallStats.UnknownTool, rt.Agent.CallStats.Failed));
    }

    [Fact]
    public async Task A_call_missing_what_it_needs_is_refused_with_the_tools_parameters_and_the_same_call_again_is_a_retry()
    {
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "a.txt"), "alpha\n");
        _gateway.Answer = req => Results(req) < 2 ? Reply.Call(("edit_file", """{"path":"a.txt","new_string":"beta"}""")) : Reply.Say("done");
        await using var rt = await StartAsync(h);
        await rt.Agent.RunAsync("Change it.", new Spend(), CancellationToken.None);

        var refused = Tools(_gateway.Requests.Last())[0]["content"]!.GetValue<string>();
        Assert.StartsWith("The call to edit_file was not run: old_string is required. edit_file takes a JSON object:\n- path (string, required)", refused, StringComparison.Ordinal);
        Assert.Contains("- old_string (string, required): The exact text to replace.", refused, StringComparison.Ordinal);
        Assert.Contains("For example: {\"path\":\"…\",\"old_string\":\"…\",\"new_string\":\"…\"}", refused, StringComparison.Ordinal);
        Assert.Equal("alpha\n", File.ReadAllText(Path.Combine(h.Work, "a.txt")));
        Assert.Equal((2, 2, 1), (rt.Agent.CallStats.Calls, rt.Agent.CallStats.BadArguments, rt.Agent.CallStats.Retries));
    }
}
