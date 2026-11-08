namespace Llm.Core.Chat;

/// <summary>
/// A model stuck in a loop: its thinking or its answer repeating one short piece (a word, a phrase, a line) again and
/// again until the context is full. Fed the stream as it comes; <see cref="Stuck"/> says when the end of what came is one
/// piece of 1 to <see cref="MostPeriod"/> characters repeated over at least <see cref="LeastSpan"/> characters and at
/// least <see cref="LeastRepeats"/> times. A table's rule, a run of dashes or a short list is far below that; a loop is
/// not. Shared by the web chat (Llm.Api) and Code Arena (linked).
/// </summary>
public sealed class RepetitionGuard
{
    /// <summary>The longest piece a loop is looked for in.</summary>
    public const int MostPeriod = 200;

    /// <summary>The least run of repeated text that counts as a loop.</summary>
    public const int LeastSpan = 600;

    /// <summary>The least number of times the piece repeats.</summary>
    public const int LeastRepeats = 12;

    // Looked at after this many new characters: often enough to stop within a few hundred tokens of a loop's start.
    private const int Every = 120;

    private readonly System.Text.StringBuilder _tail = new();
    private int _since;

    /// <summary>The piece repeated, once a loop is found; null until then.</summary>
    public string? Piece { get; private set; }

    /// <summary>Where the loop starts in all that was fed (its first repeat kept: what came before it is the answer).</summary>
    public long LoopStart { get; private set; } = -1;

    private long _fed;

    public bool Stuck => Piece is not null;

    /// <summary>Takes the next piece of the stream; true when it ends in a loop now.</summary>
    public bool Add(string text)
    {
        if (Stuck || string.IsNullOrEmpty(text))
        {
            return Stuck;
        }
        _tail.Append(text);
        _fed += text.Length;
        _since += text.Length;
        // Only the end matters: enough for the longest piece's repeats and the least span.
        var keep = Math.Max(LeastSpan, MostPeriod * LeastRepeats) + Every;
        if (_tail.Length > keep * 2)
        {
            _tail.Remove(0, _tail.Length - keep);
        }
        if (_since < Every)
        {
            return false;
        }
        _since = 0;
        var s = _tail.ToString();
        for (var p = 1; p <= MostPeriod && p * LeastRepeats <= s.Length; p++)
        {
            // How far back from the end the text repeats with period p.
            var run = 0;
            for (var i = s.Length - 1; i - p >= 0 && s[i] == s[i - p]; i--)
            {
                run++;
            }
            var span = run + p;
            if (span >= LeastSpan && span / p >= LeastRepeats && s.Substring(s.Length - p).Trim().Length > 0)
            {
                // The run starts where the text first goes on as it does p characters later: its first repeat begins there.
                LoopStart = _fed - span;
                Piece = s.Substring(s.Length - span, p);
                return true;
            }
        }
        return false;
    }

    /// <summary>What a person reads when a loop was stopped.</summary>
    public string Notice(string where) =>
        $"The model was repeating itself in its {where} (\"{Shown(Piece ?? "")}\" over and over), so it was stopped there: what came before the loop is kept.";

    private static string Shown(string piece)
    {
        var one = string.Join(' ', piece.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 40 ? one : one[..40] + "…";
    }
}
