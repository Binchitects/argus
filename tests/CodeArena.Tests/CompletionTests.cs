using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Code completion in the IDE's editor: the code around the cursor asked of the model, its answer as grey text.</summary>
public sealed class CompletionTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public void The_prompt_is_the_models_fill_in_the_middle_with_the_files_path_and_the_code_around_the_cursor()
    {
        var request = Completions.Request("qwen", "model-a", "src/sum.py", "def add(a, b):\n    return ", "\n\nprint(add(1, 2))\n");
        Assert.Equal("model-a", request["model"]!.GetValue<string>());
        Assert.Equal("<|fim_prefix|>// src/sum.py\ndef add(a, b):\n    return <|fim_suffix|>\n\nprint(add(1, 2))\n<|fim_middle|>", request["prompt"]!.GetValue<string>());
        Assert.Equal(Completions.MaxTokens, request["max_tokens"]!.GetValue<int>());
        Assert.Contains("<|fim_pad|>", request["stop"]!.AsArray().Select(s => s!.GetValue<string>()));

        // Long files: the code nearest the cursor only.
        var big = Completions.Request("deepseek", "m", "a.cs", new string('x', 10_000) + "END", "START" + new string('y', 10_000));
        var prompt = big["prompt"]!.GetValue<string>();
        Assert.StartsWith("<｜fim▁begin｜>", prompt);
        Assert.Contains("END<｜fim▁hole｜>START", prompt);
        Assert.True(prompt.Length < Completions.MaxPrefix + Completions.MaxSuffix + 100);

        // What already follows the cursor is not written twice; blank lines at the end go.
        Assert.Equal("a + b", Completions.Tidy("a + b)\n\n", ")\n"));
        Assert.Equal("total += x", Completions.Tidy("total += x\n", "\n    return total"));
    }

    [Fact]
    public async Task The_IDE_completes_at_the_cursor_unless_it_is_off_or_the_agent_is_working()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        Assert.Equal("model-a", (await web.GetJsonAsync("/api/state"))["completion"]!["model"]!.GetValue<string>());

        var asked = new JsonObject { ["path"] = "src/sum.py", ["prefix"] = "def add(a, b):\n    return ", ["suffix"] = "\n" };
        var done = await web.PostAsync("/api/complete", asked);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal("a + b;", JsonNode.Parse(await done.Content.ReadAsStringAsync())!["text"]!.GetValue<string>());
        var request = Assert.Single(_gateway.TextRequests);
        Assert.StartsWith("<|fim_prefix|>// src/sum.py\ndef add(a, b):", request["prompt"]!.GetValue<string>());
        // Not part of the conversation.
        Assert.Empty(_gateway.Requests);

        // The key's limit: said, not tried again.
        _gateway.TextStatus = 429;
        Assert.Equal((HttpStatusCode)429, (await web.PostAsync("/api/complete", asked)).StatusCode);
        Assert.Equal(2, _gateway.TextRequests.Count);
    }

    [Fact]
    public async Task Code_completion_can_be_turned_off_in_the_config()
    {
        using var h = new Harness(_gateway, _mcp, c => c["completion"] = false);
        await using var web = await WebRun.StartAsync(h);
        Assert.Null((await web.GetJsonAsync("/api/state"))["completion"]);
        var refused = await web.PostAsync("/api/complete", new JsonObject { ["path"] = "a.py", ["prefix"] = "x", ["suffix"] = "" });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains("\"off\"", await refused.Content.ReadAsStringAsync());
        Assert.Empty(_gateway.TextRequests);
    }
}
