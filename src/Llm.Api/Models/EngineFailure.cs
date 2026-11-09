using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Llm.Api.Models;

/// <summary>
/// Why a chat model on vLLM, SGLang, LMDeploy or Strata failed to start, read from what its supervisor wrote of it (its
/// status file: how it last exited, how many tries, the last lines of its log): the kind of failure, the line of the
/// engine's log that says so, what to do about it, and which engine (whose log holds the rest).
/// </summary>
/// <param name="Kind">gpu-memory, unsupported, missing-file, bad-options, download or crashed.</param>
/// <param name="Detail">The line of the engine's log that says why, as the engine said it.</param>
/// <param name="Hint">What to change, in a sentence for the admin.</param>
public sealed record EngineFailure(string Kind, string Detail, string Hint, string Engine, int Tries, DateTimeOffset? NextTry, int? Exit)
{
    /// <summary>Why, in a few words for a person in a chat (who cannot change it): "its GPUs had too little memory free".</summary>
    public string InBrief => Kind switch
    {
        "gpu-memory" => "its GPUs had too little memory free",
        "unsupported" => "its engine cannot run this model",
        "missing-file" => "a file of it is missing",
        "bad-options" => "its engine refused one of its settings",
        "download" => "its engine looked for files it does not have",
        _ => "its engine stopped while starting it",
    };

    /// <summary>
    /// A Python exception's own line ("ValueError: …", "torch.OutOfMemoryError: …"), the line engines end with when they
    /// stop; the traceback above it says only where.
    /// </summary>
    private static readonly Regex PythonError = new(@"^(?:[\w.]+\.)?(\w*(?:Error|Exception))\b:?\s*(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The failure of one process from its status file, or null while it has not failed (ready, loading on its first try).
    /// A process that exited and waits to be tried again counts as failed too: its last exit says why.
    /// </summary>
    public static EngineFailure? Of(JsonObject? status, string engine)
    {
        if (status is null)
        {
            return null;
        }
        var phase = status["phase"]?.GetValue<string>();
        var tries = status["tries"]?.GetValue<int>() ?? 0;
        if (phase is "ready" or "sleeping" or "draining" || (phase != "failed" && tries == 0))
        {
            return null;
        }
        var lines = status["log"] is JsonArray log ? log.Select(l => l?.GetValue<string>() ?? "").ToList() : [];
        int? exit = status["exit"] is JsonValue v && v.TryGetValue<int>(out var code) ? code : null;
        DateTimeOffset? next = status["next_try"] is JsonValue n && n.TryGetValue<long>(out var at) ? DateTimeOffset.FromUnixTimeSeconds(at) : null;
        var (kind, detail, hint) = Classify(lines, exit);
        return new EngineFailure(kind, detail, hint, engine, tries, next, exit);
    }

    /// <summary>
    /// The kind of a failure from the end of an engine's log: GPU memory first (whatever else it says), then a model the
    /// engine cannot run, a file it cannot find, flags it refused, files it could not fetch; else a crash, said by the
    /// engine's last error line.
    /// </summary>
    public static (string Kind, string Detail, string Hint) Classify(IReadOnlyList<string> log, int? exit)
    {
        var lines = log.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var error = lines.LastOrDefault(l => PythonError.IsMatch(l) && !l.StartsWith("Traceback", StringComparison.Ordinal));
        // The engine's last error line first (what it stopped on), else any line: an engine's startup lines speak of memory too.
        bool Has(string l, string[] marks) => marks.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase));
        string? Line(params string[] marks) => error is not null && Has(error, marks) ? error : lines.LastOrDefault(l => Has(l, marks));
        if (Line("larger than the maximum number of tokens that can be stored in KV cache", "larger than the available KV cache memory", "context length is larger") is { } window)
        {
            return ("gpu-memory", window,
                "Its context is longer than its share of the GPU's memory holds: give it a shorter context, or a larger share of the GPU's memory.");
        }
        if (Line("out of memory", "OutOfMemoryError", "no GPU memory", "is less than desired GPU memory", "not enough memory", "insufficient memory", "cudaMalloc failed",
                "No available memory for the cache blocks", "Not enough GPU memory") is { } memory)
        {
            return ("gpu-memory", memory,
                "Its GPUs had too little memory free for its weights and its token cache: give it a larger share of the GPU's memory, a shorter context or " +
                "fewer people at once, or unload the models beside it.");
        }
        if (Line("is not supported", "are not supported", "not supported for", "Unsupported architecture", "unsupported model", "does not support",
                "Model architectures", "no module named") is { } unsupported)
        {
            return ("unsupported", unsupported,
                "This engine cannot run this model (its architecture, its quantization or its file format): run it on llama.cpp or vLLM, or choose another file of it.");
        }
        if (Line("No such file", "file not found", "FileNotFoundError", "does not appear to have a file", "not a valid model", "cannot open", "failed to open") is { } file)
        {
            return ("missing-file", file, "A file of the model is missing or cannot be read: check that its download finished, or download it again.");
        }
        if (Line("unrecognized arguments", "error: argument", "invalid choice", "unknown argument", "usage:") is { } usage)
        {
            return ("bad-options", usage, "The engine refused a setting of this model: change its engine settings (or the extra arguments) and load it again.");
        }
        if (Line("HFValidationError", "LocalEntryNotFoundError", "couldn't connect to", "Connection refused", "offline mode", "HF_HUB_OFFLINE") is { } fetch)
        {
            return ("download", fetch, "The engine tried to fetch files from the internet: it reads only the library, so the model's own files (tokenizer, config) must be in its folder.");
        }
        var detail = error ?? lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault()
            ?? $"it exited with code {exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}";
        return ("crashed", detail, "The engine stopped while starting it: its log says the rest.");
    }
}
