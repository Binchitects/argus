using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>code_search, code_symbol and code_outline in a session: offered only when the index is on, read-only, for sub-agents too.</summary>
public sealed class CodeIndexToolTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static void Shop(Harness h)
    {
        h.Write("src/Shop/Payments.cs", "namespace Shop;\n\npublic sealed class Payments\n{\n    /// <summary>Gives the money back to the card.</summary>\n    public void Refund(decimal amount) => Record(-amount);\n\n    private void Record(decimal amount) { }\n}\n");
        h.Write("src/Shop/Cart.cs", "namespace Shop;\n\npublic sealed class Cart\n{\n    public void Back(Payments p) => p.Refund(1);\n}\n");
    }

    private static List<string> Results(JsonObject request) =>
        [.. request["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.ToString())];

    [Fact]
    public async Task The_tools_and_their_bullet_are_there_only_when_the_index_is_on()
    {
        _gateway.Answer = _ => Reply.Say("ok");
        using (var off = new Harness(_gateway, _mcp))
        {
            Assert.Equal(0, await off.Run("", "-p", "hi"));
        }
        var request = _gateway.Requests[^1];
        Assert.DoesNotContain(FakeGateway.ToolNames(request), n => n.StartsWith("code_", StringComparison.Ordinal));
        Assert.DoesNotContain("code_search", FakeGateway.SystemOnly(request), StringComparison.Ordinal);
        Assert.Contains("find code with grep and glob rather than guessing paths.", FakeGateway.SystemOnly(request), StringComparison.Ordinal);

        using (var on = new Harness(_gateway, _mcp, c => c["codeIndex"] = true))
        {
            Assert.Equal(0, await on.Run("", "-p", "hi"));
        }
        request = _gateway.Requests[^1];
        Assert.Equal(["code_outline", "code_search", "code_symbol"], FakeGateway.ToolNames(request).Where(n => n.StartsWith("code_", StringComparison.Ordinal)).Order());
        Assert.Contains("find code with code_search (where something is done, a name's definition and uses, what a change reaches): it cites path:lines; grep when you need every occurrence",
            FakeGateway.SystemOnly(request), StringComparison.Ordinal);

        using (var one = new Harness(_gateway, _mcp, c => { c["codeIndex"] = true; c["codeIndexTools"] = new JsonArray("code_search"); }))
        {
            Assert.Equal(0, await one.Run("", "-p", "hi"));
        }
        Assert.Equal(["code_search"], FakeGateway.ToolNames(_gateway.Requests[^1]).Where(n => n.StartsWith("code_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task In_plan_mode_the_tools_answer_with_cited_places_and_a_place_shown_is_not_shown_again()
    {
        using var h = new Harness(_gateway, _mcp, c => c["codeIndex"] = true);
        Shop(h);
        var step = 0;
        _gateway.Answer = _ => ++step switch
        {
            1 => Reply.Call(("code_search", """{"query":"money back to the card"}""")),
            2 => Reply.Call(("code_search", """{"query":"money back to the card"}""")),
            3 => Reply.Call(("code_symbol", """{"name":"Payments.Refund","show":"callers"}"""), ("code_outline", """{"path":"src/Shop/Payments.cs"}""")),
            _ => Reply.Say("found it"),
        };
        Assert.Equal(0, await h.Run("", "-p", "where is a refund made?", "--mode", "plan"));
        var results = Results(_gateway.Requests[^1]);
        Assert.Contains("[1] src/Shop/Payments.cs:6-6 · method Payments.Refund", results[0], StringComparison.Ordinal);
        Assert.Contains("     6\t    public void Refund(decimal amount) => Record(-amount);", results[0], StringComparison.Ordinal);
        // The same place again: cited in one line.
        Assert.Contains(results[1].Split('\n'), l => l.StartsWith("[1] src/Shop/Payments.cs:6-6 · method Payments.Refund · why: money, back, card", StringComparison.Ordinal) && l.EndsWith(" · shown above", StringComparison.Ordinal));
        Assert.DoesNotContain("public void Refund", results[1], StringComparison.Ordinal);
        Assert.Contains("- src/Shop/Cart.cs:5-5 · method Cart.Back · calls it at :5 (type)", results[2], StringComparison.Ordinal);
        Assert.Contains("    6 public void Refund(decimal amount)", results[3], StringComparison.Ordinal);
        // Nothing was kept in the repository.
        Assert.Empty(Directory.GetFiles(h.Work, "*.bin", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Path.Combine(h.Paths.DataDir, "index"), "*.bin"));
    }

    [Fact]
    public async Task A_sub_agent_has_the_tools_and_a_rule_that_denies_a_path_hides_it()
    {
        using var h = new Harness(_gateway, _mcp, c =>
        {
            c["codeIndex"] = true;
            c["permissions"] = new JsonObject { ["deny"] = new JsonArray("read_file(src/Shop/Cart.cs)") };
        });
        Shop(h);
        _gateway.Answer = req =>
        {
            if (FakeGateway.System(req).StartsWith("You are a sub-agent", StringComparison.Ordinal))
            {
                return FakeGateway.HasToolResults(req) ? Reply.Say("Report: " + FakeGateway.Last(req)) : Reply.Call(("code_search", """{"query":"Refund"}"""));
            }
            return FakeGateway.HasToolResults(req) ? Reply.Say(FakeGateway.Last(req)) : Reply.Call(("task", """{"description":"find refunds","prompt":"Where is Refund used?"}"""));
        };
        Assert.Equal(0, await h.Run("", "-p", "where are refunds made?"));
        var sub = _gateway.Requests.First(r => FakeGateway.System(r).StartsWith("You are a sub-agent", StringComparison.Ordinal));
        Assert.Contains("code_search", FakeGateway.ToolNames(sub));
        Assert.Contains("code_search \"Refund\" · name · 1 definition", h.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Shop/Cart.cs", h.Out, StringComparison.Ordinal);
        Assert.Contains("1 hidden by your rules", h.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Places_shown_before_earlier_results_were_cleared_are_shown_again_whole()
    {
        _gateway.Context = 40_000;
        using var h = new Harness(_gateway, _mcp, c => { c["compactAt"] = 30; c["codeIndex"] = true; });
        Shop(h);
        for (var i = 1; i <= 5; i++)
        {
            h.Write($"f{i}.txt", string.Concat(Enumerable.Range(1, 220).Select(n => $"line {n} of file {i}, some words to fill it\n")));
        }
        _gateway.Answer = req => FakeGateway.Last(req) is var last && last.StartsWith("read f", StringComparison.Ordinal)
            ? Reply.Call(("read_file", $$"""{"path":"{{last.Split(' ')[1]}}"}"""))
            : last == "search" ? Reply.Call(("code_search", """{"query":"money back to the card"}"""))
            : Reply.Say("done");
        Assert.Equal(0, await h.Run("search\nsearch\nread f1.txt\nread f2.txt\nread f3.txt\nread f4.txt\nread f5.txt\nsearch\n/exit\n", "chat", "--mode", "auto-edit"));
        var results = Results(_gateway.Requests[^1]);
        Assert.Contains(results, r => r.StartsWith("[Cleared to make room in the window: read_file's result", StringComparison.Ordinal));
        Assert.Contains("     6\t    public void Refund(decimal amount)", results[^1], StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task A_program_calls_code_search_as_a_function_of_its_own()
    {
        _gateway.Answer = req => FakeGateway.Last(req) == "find" && req["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>() == "user"
            ? Reply.Call(("run_code", """{"code":"import arena\nprint(arena.local.code_search(query='money back to the card').splitlines()[1])\n"}"""))
            : Reply.Say("ok");
        using var h = new Harness(_gateway, _mcp, c => c["codeIndex"] = true);
        Shop(h);
        Assert.Equal(0, await h.Run("find\n/exit\n", "chat", "--mode", "yolo"));
        var result = FakeGateway.History(_gateway.Requests[^1]).Last(m => m["role"]!.GetValue<string>() == "tool")["content"]!.ToString();
        Assert.Contains("[1] src/Shop/Payments.cs:6-6 · method Payments.Refund", result, StringComparison.Ordinal);
    }
}
