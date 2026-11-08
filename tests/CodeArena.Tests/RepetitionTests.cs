namespace CodeArena.Tests;

/// <summary>A model stuck repeating itself is stopped, its loop cut off, and the person told.</summary>
public sealed class RepetitionTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    [Fact]
    public async Task Thinking_that_loops_is_cut_off_and_said_and_what_came_before_is_kept()
    {
        _gateway.Answer = _ => Reply.Say("", reasoning: "Let me look at the build first. " + string.Concat(Enumerable.Repeat("wait, ", 4000)));
        using var h = new Harness(_gateway);
        Assert.Equal(0, await h.Run("", "-p", "why does the build fail?"));
        Assert.Contains("repeating itself in its thinking", h.Out + h.Err);
        Assert.DoesNotContain(string.Concat(Enumerable.Repeat("wait, ", 200)), h.Out);
    }

    [Fact]
    public async Task An_answer_that_loops_is_stopped_there()
    {
        _gateway.Answer = _ => Reply.Say("The fix is in Program.cs. " + string.Concat(Enumerable.Repeat("I will fix it now. ", 400)));
        using var h = new Harness(_gateway);
        Assert.Equal(0, await h.Run("", "-p", "fix it"));
        Assert.Contains("The fix is in Program.cs.", h.Out);
        Assert.Contains("repeating itself in its answer", h.Out + h.Err);
        Assert.DoesNotContain(string.Concat(Enumerable.Repeat("I will fix it now. ", 60)), h.Out);
    }
}
