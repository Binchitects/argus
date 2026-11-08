using Llm.Core.Chat;

namespace Llm.Tests;

/// <summary>A model stuck repeating itself is found within a few hundred characters of its loop; what is not a loop never is.</summary>
public sealed class RepetitionGuardTests
{
    private static (bool Stuck, RepetitionGuard Guard, int At) Feed(string text, int piece = 7)
    {
        var guard = new RepetitionGuard();
        for (var i = 0; i < text.Length; i += piece)
        {
            if (guard.Add(text.Substring(i, Math.Min(piece, text.Length - i))))
            {
                return (true, guard, i + piece);
            }
        }
        return (false, guard, text.Length);
    }

    [Fact]
    public void A_word_repeated_until_the_window_fills_is_stopped_soon_and_what_came_before_is_kept()
    {
        var before = "Let me check the configuration file first. ";
        var (stuck, guard, at) = Feed(before + string.Concat(Enumerable.Repeat("wait, ", 2000)));
        Assert.True(stuck);
        Assert.Contains("wait, ", guard.Piece + guard.Piece);
        Assert.Equal(6, guard.Piece!.Length);
        // Within about two windows of checks past the least span, not after thousands of repeats.
        Assert.True(at < before.Length + RepetitionGuard.LeastSpan + 400, $"stopped at {at}");
        // Its first repeat (the space before it fits the pattern too).
        Assert.InRange(guard.LoopStart, before.Length - 1, before.Length);
        Assert.Contains("repeating itself in its thinking", guard.Notice("thinking"));
    }

    [Fact]
    public void A_line_repeated_and_a_long_phrase_repeated_are_loops_too()
    {
        Assert.True(Feed(string.Concat(Enumerable.Repeat("I need to read the file again before I answer.\n", 80))).Stuck);
        Assert.True(Feed("ok " + string.Concat(Enumerable.Repeat("123", 400))).Stuck);
    }

    [Fact]
    public void Prose_tables_code_and_short_repeats_are_not_loops()
    {
        var prose = string.Concat(Enumerable.Range(0, 200).Select(i => $"Step {i}: the parser reads token {i * 7 % 13} and moves on. "));
        Assert.False(Feed(prose).Stuck);
        var table = "| a | b |\n|---|---|\n" + string.Concat(Enumerable.Range(0, 60).Select(i => $"| {i} | {i * i} |\n"));
        Assert.False(Feed(table).Stuck);
        // A rule of dashes or a dozen equal lines is far below a loop.
        Assert.False(Feed(new string('-', 120) + "\n" + string.Concat(Enumerable.Repeat("    total += 1;\n", 12)) + "done").Stuck);
        // Whitespace alone is not a loop worth stopping.
        Assert.False(Feed(new string(' ', 5000)).Stuck);
    }
}
