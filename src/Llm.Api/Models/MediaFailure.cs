namespace Llm.Api.Models;

/// <summary>
/// Why a picture, video or speech model failed to load: gpu-memory (the GPU had too little free), bad-options (its server
/// refused its flags: not tried again until the model is changed or loaded by hand), missing-file, crashed (with its exit
/// code), timeout (it did not answer in time) or refused (the speech server would not load it); with the line that says so,
/// how many times in a row, and since when.
/// </summary>
public sealed record MediaFailure(string Class, string Detail, int Count, DateTimeOffset Since, string Gen)
{
    /// <summary>When it is tried again by itself (1, 2, 4, 8, 16, then 30 minutes, as a chat model); null: not until it is changed or loaded.</summary>
    public DateTimeOffset? RetryAt => Class == "bad-options" ? null : Since + EngineState.Wait(Count);

    /// <summary>
    /// A server's exit, from the end of its log, its exit code and how long it ran: out of memory first (whatever the code),
    /// then flags it refused (or an exit within 5 seconds), a file it could not read, else a crash.
    /// </summary>
    public static (string Class, string Detail) Classify(string log, int? exit, double ranSeconds)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? Line(params string[] marks) =>
            lines.LastOrDefault(l => marks.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)));
        if (Line("out of memory", "cudaMalloc", "CUDA error 2", "failed to allocate", "not enough memory") is { } oom)
        {
            return ("gpu-memory", oom);
        }
        if (Line("unknown argument", "invalid argument", "unrecognized option", "usage:") is { } usage)
        {
            return ("bad-options", usage);
        }
        if (Line("No such file", "failed to open", "file not found", "cannot open") is { } file)
        {
            return ("missing-file", file);
        }
        if (ranSeconds < 5 && exit is not null and not 0)
        {
            return ("bad-options", lines.LastOrDefault() ?? $"exited with code {exit} at once");
        }
        return ("crashed", lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? $"exited with code {exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
    }
}
