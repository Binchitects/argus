using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// "Sometimes the thinking in code-arena stucks and takes too long to answer": Skip thinking (the IDE's button, Tab in the
/// terminal) stops a model that thinks on and on, and it answers without thinking; the rest of the turn does not think,
/// the next turn does. The model here thinks until it is asked not to.
/// </summary>
public sealed class SkipThinkingTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static bool ThinkingOff(JsonObject req) => req["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() == false;

    /// <summary>Thinks on and on unless thinking is off; then it answers (the first time after a tool call, with the tool).</summary>
    private void ThinksUntilSkipped(bool callFirst = false) =>
        _gateway.Answer = req => !ThinkingOff(req) ? new Reply("", [], "Let me weigh it once more. ") { ThinksOn = true }
            : callFirst && !FakeGateway.HasToolResults(req) ? Reply.Call(("list_dir", """{"path":"."}"""))
            : Reply.Say("The answer, at once.");

    private static async Task Within(Func<bool> condition)
    {
        var deadline = Task.Delay(Deadline);
        while (!condition())
        {
            Assert.True(await Task.WhenAny(deadline, Task.Delay(20)) != deadline, "not within the deadline");
        }
    }

    [Fact]
    public async Task Skipping_stops_the_thinking_the_model_answers_and_the_rest_of_the_turn_thinks_no_more()
    {
        ThinksUntilSkipped(callFirst: true);
        using var h = new Harness(_gateway, _mcp);
        var output = TextWriter.Synchronized(new StringWriter());
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        await using var rt = await Runtime.StartAsync(new Options(), env, new Ui(env.In, output, output, false, false), CancellationToken.None);
        Assert.False(rt.Agent.SkipThinking());

        var turn = rt.Agent.RunAsync("think it through", new Spend(), CancellationToken.None);
        await Within(() => _gateway.Requests.Count > 0);
        await Task.Delay(100);
        Assert.False(turn.IsCompleted);
        Assert.True(rt.Agent.SkipThinking());
        // Asked once: a second press says so.
        Assert.False(rt.Agent.SkipThinking());

        Assert.True(await Task.WhenAny(turn, Task.Delay(Deadline)) == turn, "the turn did not end once the thinking was skipped");
        Assert.Equal("The answer, at once.", await turn);
        var asked = _gateway.Requests;
        // The request that thought, the same one asked again without thinking (its call), and the step after the call.
        Assert.Equal(3, asked.Count);
        Assert.False(ThinkingOff(asked[0]));
        Assert.True(ThinkingOff(asked[1]));
        Assert.True(ThinkingOff(asked[2]));
        Assert.Equal(asked[0]["messages"]!.ToJsonString(), asked[1]["messages"]!.ToJsonString());
        // The thinking so far is kept with the answer it led to.
        Assert.StartsWith("Let me weigh it once more.", rt.Agent.Messages.First(m => m["tool_calls"] is not null)["reasoning_content"]!.GetValue<string>());

        // The next turn thinks again.
        _gateway.Answer = _ => Reply.Say("Thought it over.", "A short thought.");
        Assert.Equal("Thought it over.", await rt.Agent.RunAsync("and now?", new Spend(), CancellationToken.None));
        Assert.False(ThinkingOff(_gateway.Requests[^1]));
    }

    [Fact]
    public async Task The_IDE_skips_the_thinking_of_the_answer_running_and_its_stream_marks_it_cut_short()
    {
        ThinksUntilSkipped();
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/skip-thinking", new JsonObject())).StatusCode);

        using var stream = await web.SendAsync("think hard");
        await stream.UntilAsync("reasoning");
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/skip-thinking", new JsonObject())).StatusCode);
        Assert.True((await stream.UntilAsync("thought"))["cutShort"]!.GetValue<bool>());
        Assert.StartsWith("The answer", (await stream.UntilAsync("content"))["text"]!.GetValue<string>());
        Assert.Equal("done", (await stream.RestAsync())[^1]["type"]!.GetValue<string>());

        var messages = (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray();
        Assert.Equal("The answer, at once.", messages[1]!["content"]!.GetValue<string>());
        Assert.StartsWith("Let me weigh it once more.", messages[1]!["reasoning"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tab_in_the_terminal_skips_the_thinking_and_what_is_typed_meanwhile_is_kept()
    {
        ThinksUntilSkipped();
        using var h = new Harness(_gateway, _mcp);
        using var terminal = new SharedTerminal();
        var output = new LockedWriter();
        var env = new CliEnv
        {
            In = terminal, Out = output, Err = output, Env = k => h.Env.GetValueOrDefault(k), Cwd = h.Work, Paths = h.Paths, InTerminal = true, Keys = terminal,
        };
        var run = Task.Run(() => Cli.RunAsync(["chat"], env));
        try
        {
            terminal.Type("think hard\n");
            await Within(() => output.ToString().Contains("Let me weigh it once more."));
            terminal.Type("next one\n\t");
            await Within(() => output.ToString().Contains("The answer, at once."));
            Assert.Contains("Thinking skipped: the model answers now.", output.ToString());
            Assert.Contains("Read at the model's next step: next one", output.ToString());
            // The line typed while it thought is the next message, and that turn thinks again (and is skipped again).
            await Within(() => _gateway.Requests.Count(r => !ThinkingOff(r)) == 2);
            terminal.Type("\t");
            await Within(() => _gateway.Requests.Count == 4);
            Assert.Equal("next one", FakeGateway.Last(_gateway.Requests[^1]));
        }
        finally
        {
            terminal.Type("/exit\n");
            terminal.End();
            Assert.True(await Task.WhenAny(run, Task.Delay(Deadline)) == run, "code-arena chat did not end");
        }
    }
}
