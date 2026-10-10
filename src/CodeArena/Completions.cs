using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Code completion in the IDE's editor: the code before the cursor and after it, sent as the model's fill-in-the-middle
/// prompt (its own tokens: <see cref="Templates"/>), and what it writes between them comes back as grey text to take with
/// Tab. A request of its own (/v1/completions), never part of the conversation; its tokens count to the session's.
/// </summary>
internal sealed class Completions(Runtime rt)
{
    /// <summary>The most of the code before the cursor and after it sent with each request.</summary>
    public const int MaxPrefix = 6_000;
    public const int MaxSuffix = 2_000;

    /// <summary>The longest a completion is, in tokens: a line or a few.</summary>
    public const int MaxTokens = 96;

    /// <summary>How long a completion may take: past it, the person has typed on.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    /// <summary>The fill-in-the-middle tokens of each family of models: before, after, middle; and what ends its answer.</summary>
    public static readonly Dictionary<string, (string Prefix, string Suffix, string Middle, string[] Stop)> Templates = new(StringComparer.Ordinal)
    {
        ["qwen"] = ("<|fim_prefix|>", "<|fim_suffix|>", "<|fim_middle|>", ["<|fim_pad|>", "<|endoftext|>", "<|fim_prefix|>", "<|file_sep|>"]),
        ["deepseek"] = ("<｜fim▁begin｜>", "<｜fim▁hole｜>", "<｜fim▁end｜>", ["<｜end▁of▁sentence｜>", "<｜fim▁begin｜>"]),
        ["codellama"] = ("<PRE> ", " <SUF>", " <MID>", ["<EOT>", "</s>"]),
        ["starcoder"] = ("<fim_prefix>", "<fim_suffix>", "<fim_middle>", ["<|endoftext|>", "<fim_prefix>", "<file_sep>"]),
    };

    // A client of its own: no tries again, and nothing said into the turn of the agent.
    private readonly GatewayClient _gateway = new(rt.Http, rt.Gateway.BaseUrl, rt.Config.ApiKey!) { RetryWaits = [] };

    /// <summary>The model completions come from: the config's, else the session's.</summary>
    public string Model => rt.Config.CompletionModel ?? rt.Model.Name;

    /// <summary>The prompt for the code around the cursor in a file: the file's path first, as a comment the model knows.</summary>
    public static JsonObject Request(string template, string model, string path, string prefix, string suffix)
    {
        var t = Templates[template];
        prefix = prefix.Length > MaxPrefix ? prefix[^MaxPrefix..] : prefix;
        suffix = suffix.Length > MaxSuffix ? suffix[..MaxSuffix] : suffix;
        return new JsonObject
        {
            ["model"] = model,
            ["prompt"] = $"{t.Prefix}// {path}\n{prefix}{t.Suffix}{suffix}{t.Middle}",
            ["max_tokens"] = MaxTokens,
            ["temperature"] = 0.1,
            ["stop"] = new JsonArray([.. t.Stop.Append("\n\n\n").Select(s => (JsonNode)s)]),
        };
    }

    /// <summary>
    /// What goes at the cursor: the model's text, without what the code after the cursor already has (a closing bracket it
    /// wrote again) and without blank lines at its end; empty: nothing worth showing.
    /// </summary>
    public async Task<string> CompleteAsync(string path, string prefix, string suffix, CancellationToken ct)
    {
        // One at a time: the next asked (the person typed on) ends the one before, here and at the gateway.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        var previous = Interlocked.Exchange(ref _current, limit);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Over already.
        }
        try
        {
            var (text, usage) = await _gateway.TextCompleteAsync(Request(rt.Config.CompletionTemplate, Model, path, prefix, suffix), limit.Token);
            if (usage is not null)
            {
                // At the completion model's prices, when the gateway lists them.
                rt.Total.Add(usage, rt.Models.FirstOrDefault(m => m.Id == Model) ?? rt.Model.Info);
            }
            return Tidy(text, suffix);
        }
        finally
        {
            Interlocked.CompareExchange(ref _current, null, limit);
        }
    }

    private CancellationTokenSource? _current;

    /// <summary>
    /// The text without blank lines at its end, nor the start of the code after the cursor written again at its end: only
    /// what the text closes more than it opens (a ")" the code after the cursor has already), never a bracket of its own.
    /// </summary>
    public static string Tidy(string text, string suffix)
    {
        text = text.TrimEnd();
        var next = suffix.TrimStart(' ', '\t');
        // The model often ends with what already follows (a ")" or "}" closing the line): not twice.
        for (var n = Math.Min(text.Length, Math.Min(next.Length, 40)); n > 0; n--)
        {
            var overlap = next[..n];
            if (text.EndsWith(overlap, StringComparison.Ordinal) && overlap.Trim().Length > 0 && Unopened(text, overlap))
            {
                return text[..^n].TrimEnd();
            }
        }
        return text;
    }

    /// <summary>Whether each bracket the overlap closes is one the text closes more often than it opens (written twice, not its own).</summary>
    private static bool Unopened(string text, string overlap)
    {
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
        {
            var closes = overlap.Count(c => c == close);
            if (closes > 0 && text.Count(c => c == close) - text.Count(c => c == open) < closes)
            {
                return false;
            }
        }
        return true;
    }
}
