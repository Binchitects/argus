using System.Text;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;

namespace Llm.Api.Gateway;

/// <summary>
/// Keeps an API request's last question and its answer in the prompt log (Gateway:LogPrompts), after the answer went out: the
/// write never holds a request up, and one that fails is logged and dropped.
/// </summary>
public sealed partial class PromptLogWriter(IServiceScopeFactory scopes, ILogger<PromptLogWriter> logger)
{
    /// <summary>The question a request asks: its last user message's text (its text parts, for a message of parts).</summary>
    public static string? LastQuestion(JsonObject request)
    {
        if (request["messages"] is not JsonArray messages || messages.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user") is not JsonObject last)
        {
            return null;
        }
        return last["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var text) => text,
            JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "text").Select(p => p["text"]?.GetValue<string>()).OfType<string>()),
            _ => null,
        };
    }

    private static string Cut(string s) => s.Length <= PromptLogEntry.MaxText ? s : s[..(PromptLogEntry.MaxText - 1)] + "…";

    public void Record(Guid person, string? keyToken, JsonObject request, JsonObject? completion, int status)
    {
        if (LastQuestion(request) is not { Length: > 0 } question)
        {
            return;
        }
        var message = completion?["choices"]?[0]?["message"];
        var answer = message?["content"]?.GetValue<string>() is { Length: > 0 } said ? said
            : message?["tool_calls"] is JsonArray calls && calls.Count > 0 ? "Called " + string.Join(", ", calls.Select(c => c?["function"]?["name"]?.GetValue<string>()).OfType<string>()) : null;
        var entry = new PromptLogEntry
        {
            UserId = person, KeyToken = keyToken, Model = request["model"]?.GetValue<string>() ?? "", Prompt = Cut(question), Answer = answer is null ? null : Cut(answer),
            PromptTokens = completion?["usage"]?["prompt_tokens"]?.GetValue<int>(), CompletionTokens = completion?["usage"]?["completion_tokens"]?.GetValue<int>(), Status = status,
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.PromptLog.Add(entry);
                await db.SaveChangesAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex.Message);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An API request was not kept in the prompt log: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
