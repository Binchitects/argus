using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace CodeArena.Tests;

/// <summary>
/// What fills the window in Code Arena (/context, the IDE's Context): the next request in its pieces, counted by Arena with
/// the engine's tokenizer and adding up to its count of the request, or estimated when Arena does not count, the same way
/// the web chat builds its own (ContextWindow).
/// </summary>
public sealed class ContextViewTests : IDisposable
{
    private readonly FakeGateway _gateway = new() { Context = 20_000 };
    private readonly FakeMcp _mcp = new();

    private static async Task<Runtime> StartAsync(Harness h)
    {
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        return await Runtime.StartAsync(new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
    }

    private static long Sum(JsonObject window) => (window["parts"] as JsonArray)!.Sum(p => p!["tokens"]!.GetValue<long>());

    private static JsonObject? Part(JsonObject window, string key) => (window["parts"] as JsonArray)!.OfType<JsonObject>().FirstOrDefault(p => p["key"]!.GetValue<string>() == key);

    [Fact]
    public async Task Counted_by_Arena_the_pieces_add_up_to_the_requests_tokens_with_each_tool_and_the_instructions()
    {
        using var h = new Harness(_gateway, _mcp);
        File.WriteAllText(Path.Combine(h.Work, "AGENTS.md"), "Run make test before every commit.");
        _mcp.Chats.Tokenizer = true;
        await using var rt = await StartAsync(h);
        rt.Agent.Messages.AddRange(
        [
            new JsonObject { ["role"] = "user", ["content"] = "Read the notes" + Mentions.Marker + "\n<file path=\"notes.txt\">\nThe release waits for the docs.\n</file>" + Mentions.End },
            new JsonObject
            {
                ["role"] = "assistant", ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "c1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = """{"path":"notes.txt"}""" } }),
            },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c1", ["content"] = "1: The release waits for the docs." },
            new JsonObject { ["role"] = "assistant", ["content"] = "The release waits for the docs." },
        ]);

        var window = await ContextView.WindowAsync(rt, CancellationToken.None);
        Assert.Equal("tokenizer", window["counted"]!.GetValue<string>());
        // Each piece as the tokenizer counted it, the chat format what the whole holds beyond them: Arena's count of the request.
        var request = rt.Agent.NextRequest();
        var pieces = rt.Agent.Pieces(request);
        var expected = pieces.Sum(p => FakeChats.CountTokens(p.Text)) + FakeChats.FormatPerMessage * request["messages"]!.AsArray().Count;
        Assert.Equal(expected, window["total"]!.GetValue<long>());
        Assert.Equal(expected, Sum(window));
        Assert.Equal(FakeChats.FormatPerMessage * request["messages"]!.AsArray().Count, Part(window, "format")!["tokens"]!.GetValue<long>());
        // Every tool offered, by name, the biggest first; the project's AGENTS.md as the person's instructions; the file mentioned.
        var tools = Part(window, "tools")!["items"]!.AsArray().Select(t => t!["tokens"]!.GetValue<long>()).ToList();
        Assert.Equal(rt.Tools.Offered().Count, Part(window, "tools")!["count"]!.GetValue<int>());
        Assert.Equal(tools.OrderDescending(), tools);
        Assert.Contains(Part(window, "instructions")!["items"]!.AsArray(), i => i!["name"]!.GetValue<string>().Contains("AGENTS.md", StringComparison.Ordinal));
        Assert.Contains(Part(window, "files")!["items"]!.AsArray(), i => i!["name"]!.GetValue<string>() == "Files mentioned");
        Assert.Contains(Part(window, "toolResults")!["items"]!.AsArray(), i => i!["name"]!.GetValue<string>() == "read_file");
        // Code Arena's note goes with the system prompt; the room kept for the answer is its own.
        Assert.Contains(Part(window, "system")!["items"]!.AsArray(), i => i!["name"]!.GetValue<string>() == "Code Arena's note");
        Assert.Equal(rt.Agent.Reserve, window["reserve"]!.GetValue<long>());
        Assert.Equal(20_000 - rt.Agent.Reserve - expected, window["free"]!.GetValue<long>());
    }

    [Fact]
    public async Task Without_Arenas_count_it_is_estimated_as_the_status_bar_says()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var rt = await StartAsync(h);
        rt.Agent.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = "hello" });
        var window = await ContextView.WindowAsync(rt, CancellationToken.None);
        Assert.Equal("estimate", window["counted"]!.GetValue<string>());
        Assert.Equal(rt.Agent.Estimate(), Sum(window));
        Assert.Null(Part(window, "format"));
        Assert.Contains(ContextWindow.Lines(window), l => l.StartsWith("Estimated from characters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task First_opened_while_a_long_turn_runs_it_shows_the_window_as_the_turn_found_it_not_nothing()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        _gateway.Answer = _ => Reply.Say("the first answer");
        await (await web.SendAsync("a first question")).RestAsync();
        // A turn that goes on (the model thinks for long): the view was never opened before it.
        _gateway.Hang = true;
        var asked = _gateway.Requests.Count;
        _ = await web.SendAsync("a second question");
        for (var waited = 0; waited < 10_000 && _gateway.Requests.Count == asked; waited += 50)
        {
            await Task.Delay(50);
        }
        var said = (await web.GetJsonAsync("/api/context")).AsObject();
        Assert.True(said!["running"]!.GetValue<bool>());
        var window = said["window"]!.AsObject();
        // The pieces as they were: the first question and its answer, each listed in its part.
        Assert.Contains(Part(window, "you")!["items"]!.AsArray(), i => i!["preview"]?.GetValue<string>() == "a first question");
        Assert.Contains(Part(window, "answers")!["items"]!.AsArray(), i => i!["preview"]?.GetValue<string>() == "the first answer");
        Assert.NotEmpty(Part(window, "tools")!["items"]!.AsArray());
        _gateway.Hang = false;
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
