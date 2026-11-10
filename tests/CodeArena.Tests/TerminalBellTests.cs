namespace CodeArena.Tests;

/// <summary>The terminal's bell: rung when Code Arena waits for the person, only on a terminal, and not when turned off.</summary>
public sealed class TerminalBellTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Asking_to_allow_a_tool_rings_the_bell_on_a_terminal_when_it_is_on(bool terminal, bool rings, bool rang)
    {
        var output = new StringWriter();
        var ui = new Ui(new StringReader("y\n"), output, output, terminal, true) { Rings = rings };
        Assert.Equal(Approval.Yes, ui.Ask("Run npm test?", null));
        Assert.Equal(rang, output.ToString().Contains('\a'));
    }
}
