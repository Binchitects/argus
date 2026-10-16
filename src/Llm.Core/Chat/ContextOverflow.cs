using System.Globalization;
using System.Text.RegularExpressions;

namespace Llm.Core.Chat;

/// <summary>
/// A refusal because the prompt is bigger than the model's context, read from the error's text or its whole body, as
/// llama.cpp ("request (N tokens) exceeds the available context size (M tokens)", exceed_context_size_error, n_prompt_tokens
/// and n_ctx), LiteLLM (ContextWindowExceededError), vLLM and OpenAI ("maximum context length") word it.
/// <see cref="SharedCache"/>: llama.cpp's "Context size has been exceeded.", which says the engine's cache, shared by the
/// requests it runs at once, filled up: not that this prompt alone is too big. The web chat and Code Arena both read
/// refusals with it (Code Arena links this file, as it builds with nothing but the SDK).
/// </summary>
/// <param name="Overflow">The refusal says the prompt did not fit.</param>
/// <param name="PromptTokens">The prompt's tokens as the engine counted them, when it said.</param>
/// <param name="ContextTokens">The context's tokens as the engine said, when it did.</param>
public readonly partial record struct ContextOverflow(bool Overflow, bool SharedCache, long? PromptTokens, long? ContextTokens)
{
    /// <summary>What a refusal's text says of the context; not an overflow when it says nothing of it.</summary>
    public static ContextOverflow Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Words().IsMatch(text))
        {
            return default;
        }
        long? prompt = null, context = null;
        if (LlamaRequest().Match(text) is { Success: true } request)
        {
            (prompt, context) = (Number(request.Groups[1]), Number(request.Groups[2]));
        }
        prompt ??= Number(PromptKey().Match(text).Groups[1]);
        context ??= Number(ContextKey().Match(text).Groups[1]);
        return new ContextOverflow(true, SharedWords().IsMatch(text), prompt, context);
    }

    private static long? Number(Group g) =>
        g.Success && long.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    [GeneratedRegex(@"ContextWindowExceeded|exceed_context|exceeds? (the )?(available |maximum )?context|context (size|length|window)|n_ctx|maximum context|prompt is too long|too many (input |prompt )?tokens", RegexOptions.IgnoreCase)]
    private static partial Regex Words();

    [GeneratedRegex(@"Context size has been exceeded", RegexOptions.IgnoreCase)]
    private static partial Regex SharedWords();

    [GeneratedRegex(@"request \((\d+) tokens\) exceeds the available context size \((\d+) tokens\)", RegexOptions.IgnoreCase)]
    private static partial Regex LlamaRequest();

    [GeneratedRegex(@"n_prompt_tokens\W{0,4}(\d+)")]
    private static partial Regex PromptKey();

    [GeneratedRegex(@"n_ctx\W{0,4}(\d+)")]
    private static partial Regex ContextKey();
}
