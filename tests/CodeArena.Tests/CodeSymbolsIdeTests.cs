using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The IDE's Go to Symbol from the code index: by name across the folder (best matches first), or one file's.</summary>
public sealed class CodeSymbolsIdeTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public void A_name_matches_whole_then_at_its_start_then_by_its_parts_then_inside()
    {
        Assert.Equal(100, CodeSearch.NameScore("RunToolsAsync", "runtoolsasync"));
        Assert.Equal(80, CodeSearch.NameScore("RunToolsAsync", "RunTo"));
        Assert.Equal(60, CodeSearch.NameScore("RunToolsAsync", "rta"));
        Assert.Equal(60, CodeSearch.NameScore("RunToolsAsync", "RunTA"));
        Assert.Equal(60, CodeSearch.NameScore("RunToolsAsync", "rasync"));
        Assert.Equal(60, CodeSearch.NameScore("make_report_now", "mrn"));
        Assert.Equal(40, CodeSearch.NameScore("RunToolsAsync", "tools"));
        Assert.Equal(0, CodeSearch.NameScore("RunToolsAsync", "tra"));
        Assert.Equal(0, CodeSearch.NameScore("RunToolsAsync", "xyz"));
    }

    [Fact]
    public async Task Symbols_are_found_by_name_across_the_folder_and_listed_for_one_file()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("src/Agent.cs", "namespace Shop;\n\npublic sealed class Agent\n{\n    public void RunToolsAsync() { }\n\n    public void Run() { }\n}\n");
        h.Write("src/cart.ts", "export function runTotals() {\n  return 1\n}\n");
        h.Write("tests/AgentTests.cs", "namespace Shop.Tests;\n\npublic sealed class AgentTests\n{\n    public void RunToolsAsync() { }\n}\n");
        await using var web = await WebRun.StartAsync(h);

        var found = (await web.GetJsonAsync("/api/symbols?q=rta"))["symbols"]!.AsArray();
        Assert.Equal([("RunToolsAsync", "src/Agent.cs", 5, "Agent"), ("RunToolsAsync", "tests/AgentTests.cs", 5, "AgentTests")],
            found.Select(s => (s!["name"]!.GetValue<string>(), s["path"]!.GetValue<string>(), s["line"]!.GetValue<int>(), s["container"]!.GetValue<string>())));
        // The name whole before the names it starts.
        var run = (await web.GetJsonAsync("/api/symbols?q=run"))["symbols"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToList();
        Assert.Equal("Run", run[0]);
        Assert.Contains("runTotals", run);

        var file = (await web.GetJsonAsync("/api/symbols?path=src/Agent.cs"))["symbols"]!.AsArray();
        Assert.Equal(["Agent", "RunToolsAsync", "Run"], file.Select(s => s!["name"]!.GetValue<string>()));
        Assert.False((await web.GetJsonAsync("/api/symbols?q=nothing_named_so"))["building"]!.GetValue<bool>());

        // The Outline: the text the editor holds, saved or not; a Markdown file's headings.
        var outline = JsonNode.Parse(await (await web.PostAsync("/api/outline", new JsonObject { ["path"] = "src/cart.ts", ["text"] = "export class Cart {\n  add() {}\n}\n" })).Content.ReadAsStringAsync())!;
        Assert.Equal([("Cart", "class", (string?)null, 1), ("add", "method", "Cart", 2)],
            outline["symbols"]!.AsArray().Select(s => (s!["name"]!.GetValue<string>(), s["kind"]!.GetValue<string>(), s["parent"]?.GetValue<string>(), s["start"]!.GetValue<int>())));
        var headings = JsonNode.Parse(await (await web.PostAsync("/api/outline", new JsonObject { ["path"] = "README.md", ["text"] = "# Shop\n\n## Paying\n" })).Content.ReadAsStringAsync())!;
        Assert.Equal(["Shop", "Paying"], headings["symbols"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
        // No text: the file as it is on disk.
        var disk = JsonNode.Parse(await (await web.PostAsync("/api/outline", new JsonObject { ["path"] = "src/Agent.cs" })).Content.ReadAsStringAsync())!;
        Assert.Contains(disk["symbols"]!.AsArray(), s => s!["name"]!.GetValue<string>() == "RunToolsAsync");
    }
}
