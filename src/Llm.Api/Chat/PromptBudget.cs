using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Api.Chat;

/// <summary>
/// A request's size in tokens before it is sent, and whether it fits the model's context with room for the answer. Characters
/// become tokens at what this chat's last answer measured (what filled its request against the prompt tokens the model
/// counted), else at 3 characters a token: measured over real answers, a tenth of them were denser than 3.1, and tool
/// definitions, line-numbered files, JSON and Persian denser still. The tool definitions and pictures count too.
/// </summary>
/// <param name="Context">The model's context in tokens.</param>
/// <param name="AnswerRoom">What is kept free for the answer (<see cref="Room"/>).</param>
public sealed class PromptBudget(int context, int answerRoom, double charsPerToken = PromptBudget.DefaultCharsPerToken)
{
    public const double DefaultCharsPerToken = 3.0;
    public const double FewestCharsPerToken = 2.0;
    public const double MostCharsPerToken = 4.5;

    /// <summary>Tokens a message costs beyond its text (the chat template's marks around it).</summary>
    public const int PerMessage = 8;

    /// <summary>An answer whose prompt was smaller than this says too little of the chat's characters per token.</summary>
    private const int FewestTokensToLearn = 500;

    public int Context { get; } = context;
    public int AnswerRoom { get; } = answerRoom;
    public double CharsPerToken { get; private set; } = Math.Clamp(charsPerToken, FewestCharsPerToken, MostCharsPerToken);

    /// <summary>Kept free beside the answer's room, for what the estimate misses: 2% of the context, 512 tokens at least.</summary>
    public int Margin => Math.Max(Context / 50, 512);

    /// <summary>The most a request may hold, in tokens.</summary>
    public long Limit => Context - AnswerRoom - Margin;

    /// <summary>
    /// The room kept for the answer: the chat's own longest answer when it set one; else an eighth of the context, at most the
    /// model's longest answer, and at least 1,024 tokens (a quarter of the context for a tiny one). The gauge shows the same.
    /// </summary>
    public static int Room(int context, int? maxOutput, int? chatMaxTokens) =>
        chatMaxTokens is > 0 and var chosen ? Math.Min(chosen, context / 2)
            : Math.Max(Math.Min(context / 8, maxOutput is > 0 and var most ? most : int.MaxValue), Math.Min(1024, context / 4));

    /// <summary>The characters a token held in a request measured as <paramref name="contextJson"/> (ContextParts) that the model counted at <paramref name="promptTokens"/>.</summary>
    public static double? Measured(string? contextJson, int? promptTokens)
    {
        if (promptTokens is not { } tokens || tokens < FewestTokensToLearn || string.IsNullOrEmpty(contextJson)
            || JsonNode.Parse(contextJson) is not JsonObject parts)
        {
            return null;
        }
        var chars = parts.Sum(p => p.Value is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0);
        return chars > 0 ? Math.Clamp((double)chars / tokens, FewestCharsPerToken, MostCharsPerToken) : null;
    }

    /// <summary>The characters a token held in the newest answer on <paramref name="path"/> that says; null when none does.</summary>
    public static double? Measured(IEnumerable<ChatMessage> path) =>
        path.Reverse().Where(m => m.Role == "assistant").Select(m => Measured(m.ContextJson, m.PromptTokens)).FirstOrDefault(r => r is not null);

    /// <summary>Learns from a request of <paramref name="chars"/> that the model counted at <paramref name="tokens"/>.</summary>
    public void Learn(long chars, long tokens)
    {
        if (chars > 0 && tokens >= FewestTokensToLearn)
        {
            CharsPerToken = Math.Clamp((double)chars / tokens, FewestCharsPerToken, MostCharsPerToken);
        }
    }

    /// <summary>A request the model refused as too long, at <paramref name="tokens"/> by its count (else more than the context): never fewer than that again.</summary>
    public void Refused(long chars, long? tokens)
    {
        var counted = Math.Max(tokens ?? Context + 1, 1);
        CharsPerToken = Math.Clamp(Math.Min(CharsPerToken, (double)chars / counted), FewestCharsPerToken, MostCharsPerToken);
    }

    /// <summary>Tokens for <paramref name="chars"/> characters in <paramref name="messages"/> messages.</summary>
    public long Tokens(long chars, int messages) => (long)Math.Ceiling(chars / CharsPerToken) + (long)messages * PerMessage;

    /// <summary>Tokens of a request: its messages (pictures and sounds at <paramref name="mediaWeight"/> characters each), tool calls and tool definitions.</summary>
    public long Tokens(JsonObject request, int mediaWeight) => Tokens(Chars(request, mediaWeight), (request["messages"] as JsonArray)?.Count ?? 0);

    public bool Fits(long tokens) => tokens <= Limit;

    /// <summary>
    /// Sure not to fit: even at the most characters a token ever holds, the request alone is bigger than the context. Only
    /// then is a request not sent at all; short of it, the model's own count decides.
    /// </summary>
    public bool CannotFit(long chars, int messages) => (long)(chars / MostCharsPerToken) + (long)messages * PerMessage > Context;

    /// <summary>A request's characters as the gauge counts them (ContextParts): messages, pictures and sounds, tool calls and tool definitions.</summary>
    public static long Chars(JsonObject request, int mediaWeight)
    {
        long chars = request["tools"]?.ToJsonString().Length ?? 0;
        foreach (var m in (request["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            chars += m["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s.Length,
                JsonArray parts => parts.OfType<JsonObject>().Sum(p => p["type"]?.GetValue<string>() == "text" ? (long)(p["text"]?.GetValue<string>()?.Length ?? 0) : mediaWeight),
                _ => 0,
            };
            chars += m["tool_calls"]?.ToJsonString().Length ?? 0;
        }
        return chars;
    }
}
