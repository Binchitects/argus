using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A prompt run every so often as a background agent, while the session is open: Code Arena's scheduled task.</summary>
internal sealed class LoopRun
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public required TimeSpan Every { get; init; }
    /// <summary>Its agent may change files (else it only reads).</summary>
    public bool Edits { get; init; }
    public DateTimeOffset NextAt { get; set; }
    public DateTimeOffset? LastAt { get; set; }
    public int Runs { get; set; }
    /// <summary>Runs skipped because the one before was still going.</summary>
    public int Skipped { get; set; }
    /// <summary>The background task of its last run.</summary>
    public AgentTask? Last { get; set; }
}

/// <summary>
/// Loops: a prompt run every interval as a background agent (/loop 30m PROMPT), each run a task under /tasks whose report
/// reaches the agent with the next message, as any background task's does. A run never overlaps the one before it (it is
/// skipped, and counted); loops end with the session.
/// </summary>
internal sealed partial class Runtime
{
    public const int MostLoops = 10;
    public static readonly TimeSpan LeastEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MostEvery = TimeSpan.FromDays(7);

    /// <summary>How often the loops are looked at (tests make it shorter).</summary>
    public static TimeSpan LoopTick { get; set; } = TimeSpan.FromSeconds(5);

    private readonly List<LoopRun> _loops = [];
    private int _loopIds;
    private Task? _looping;

    public IReadOnlyList<LoopRun> Loops
    {
        get
        {
            lock (_loops)
            {
                return [.. _loops];
            }
        }
    }

    [GeneratedRegex(@"^(\d+)\s*(s|m|h|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex Interval();

    /// <summary>"30m", "2h", "1d", "90s": an interval; null when it is not one.</summary>
    public static TimeSpan? ParseEvery(string text)
    {
        if (Interval().Match(text.Trim()) is not { Success: true } m || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return null;
        }
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => TimeSpan.FromDays(n),
        };
    }

    /// <summary>An interval as /loop takes it: "90s", "30m", "2h", "1d" (the largest whole unit).</summary>
    public static string EveryText(TimeSpan t) =>
        t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{(int)t.TotalDays}d"
        : t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? $"{(int)t.TotalHours}h"
        : t.TotalMinutes >= 1 && t.TotalMinutes % 1 == 0 ? $"{(int)t.TotalMinutes}m"
        : $"{(int)t.TotalSeconds}s";

    /// <summary>A loop started: its first run now. Null and why not.</summary>
    public (LoopRun? Loop, string? Wrong) StartLoop(TimeSpan every, string prompt, bool edits)
    {
        if (every < LeastEvery || every > MostEvery)
        {
            return (null, $"A loop runs every {EveryText(LeastEvery)} to {EveryText(MostEvery)}.");
        }
        if (prompt.Trim().Length is 0 or > 4000)
        {
            return (null, "Say what each run does, in 1 to 4,000 characters.");
        }
        if (edits && Permissions.Mode == Mode.Plan)
        {
            return (null, "Plan mode is read-only: a loop that changes files needs another mode.");
        }
        LoopRun loop;
        lock (_loops)
        {
            if (_loops.Count >= MostLoops)
            {
                return (null, $"At most {MostLoops} loops: stop one first (/loop stop N).");
            }
            loop = new LoopRun { Id = $"L{++_loopIds}", Prompt = prompt.Trim(), Every = every, Edits = edits, NextAt = DateTimeOffset.UtcNow };
            _loops.Add(loop);
            _looping ??= Task.Run(() => LoopAsync(_tasksStop.Token));
        }
        return (loop, null);
    }

    public bool StopLoop(string id)
    {
        lock (_loops)
        {
            return _loops.RemoveAll(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Id[1..], id, StringComparison.Ordinal)) > 0;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(LoopTick);
        try
        {
            do
            {
                await RunDueLoopsAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session ended: its loops with it.
        }
    }

    /// <summary>Each loop that is due starts its run (or is skipped while the one before still runs), then waits its interval.</summary>
    public async Task RunDueLoopsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var loop in Loops.Where(l => l.NextAt <= now))
        {
            loop.NextAt = now + loop.Every;
            if (loop.Last is { Over: false })
            {
                loop.Skipped++;
                continue;
            }
            if (loop.Edits && Permissions.Mode == Mode.Plan)
            {
                loop.Skipped++;
                continue;
            }
            try
            {
                loop.Last = await StartBackgroundAsync(loop.Prompt, $"Loop {loop.Id} · run {loop.Runs + 1}", null, null, loop.Edits, ct);
                loop.Runs++;
                loop.LastAt = now;
            }
            catch (ToolError)
            {
                // The tasks are full now: the next tick tries again.
                loop.NextAt = now;
                loop.Skipped++;
            }
        }
    }

    /// <summary>The loops in a few lines, for /loop.</summary>
    public string DescribeLoops()
    {
        var loops = Loops;
        if (loops.Count == 0)
        {
            return "No loops. /loop EVERY [--edits] PROMPT runs a prompt as a background agent every so often (30m, 2h, 1d), while the session is open.";
        }
        return string.Join("\n", loops.Select(l =>
            $"{l.Id} every {EveryText(l.Every)}{(l.Edits ? ", may change files" : "")}: {Fmt.OneLine(l.Prompt, 80)}\n" +
            $"   {l.Runs} {(l.Runs == 1 ? "run" : "runs")}{(l.Skipped > 0 ? $", {l.Skipped} skipped" : "")}" +
            $"{(l.Last is { } t ? $", last {t.Id} {t.Status}" : "")}, next in {Fmt.Duration(l.NextAt - DateTimeOffset.UtcNow < TimeSpan.Zero ? TimeSpan.Zero : l.NextAt - DateTimeOffset.UtcNow)}"));
    }
}
