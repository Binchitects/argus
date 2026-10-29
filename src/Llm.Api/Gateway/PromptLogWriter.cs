using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>
/// Keeps each API request in the prompt log (Gateway:LogPrompts) from the moment it arrives: waiting for its turn, running,
/// then done, failed, stopped (the caller went away) or refused, with its answer or why it failed. The writes never hold a
/// request up: they run behind it in order, and one that fails is logged and dropped.
/// </summary>
public sealed partial class PromptLogWriter(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PromptLogWriter> logger)
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

    /// <summary>What a completion said: its words, else the tools it called.</summary>
    public static string? AnswerOf(JsonObject? completion)
    {
        var message = completion?["choices"]?[0]?["message"];
        return message?["content"]?.GetValue<string>() is { Length: > 0 } said ? said
            : message?["tool_calls"] is JsonArray calls && calls.Count > 0 ? "Called " + string.Join(", ", calls.Select(c => c?["function"]?["name"]?.GetValue<string>()).OfType<string>()) : null;
    }

    /// <summary>The message of an OpenAI-style error body (<c>{"error": {"message": ...}}</c>), else the body itself, short.</summary>
    public static string? ErrorOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(body)?["error"] is { } error)
            {
                return Short(error is JsonValue v && v.TryGetValue<string>(out var text) ? text : error["message"]?.GetValue<string>() ?? error.ToJsonString());
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: the text as it came.
        }
        return Short(body.Trim());
    }

    private static string Short(string s) => s.Length <= 2000 ? s : s[..1999] + "…";

    private static string Cut(string s) => s.Length <= PromptLogEntry.MaxText ? s : s[..(PromptLogEntry.MaxText - 1)] + "…";

    /// <summary>
    /// One request's line in the log, written as it goes: its writes run one after another, the first adding it. Null-safe
    /// to use: a request that is not logged has none.
    /// </summary>
    public sealed class Line
    {
        private readonly PromptLogWriter _writer;
        private readonly Lock _lock = new();
        private Task _last;
        private bool _ended;

        internal Line(PromptLogWriter writer, PromptLogEntry entry)
        {
            _writer = writer;
            Entry = entry;
            Started = writer.Clock.GetTimestamp();
            _last = writer.WriteAsync(db =>
            {
                db.PromptLog.Add(entry);
                return db.SaveChangesAsync();
            });
        }

        public PromptLogEntry Entry { get; }

        private long Started { get; }

        /// <summary>Its turn came: it is with the model now.</summary>
        public void Running() => Then(PromptLogEntry.Running, null, null, 0);

        /// <summary>Its end: done, failed, stopped or refused, with what was answered and why it failed. Only the first end counts.</summary>
        public void End(string state, int status, string? answer = null, string? error = null, JsonObject? completion = null)
        {
            lock (_lock)
            {
                if (_ended)
                {
                    return;
                }
                _ended = true;
            }
            Then(state, answer ?? AnswerOf(completion), error, status, completion);
        }

        private void Then(string state, string? answer, string? error, int status, JsonObject? completion = null)
        {
            var id = Entry.Id;
            var ms = (int)Math.Min(int.MaxValue, _writer.Clock.GetElapsedTime(Started).TotalMilliseconds);
            var done = state is not (PromptLogEntry.Waiting or PromptLogEntry.Running);
            int? promptTokens = completion?["usage"]?["prompt_tokens"]?.GetValue<int>();
            int? completionTokens = completion?["usage"]?["completion_tokens"]?.GetValue<int>();
            var said = answer is null ? null : Cut(answer);
            var why = error is null ? null : Short(error);
            lock (_lock)
            {
                _last = _last.ContinueWith(_ => _writer.WriteAsync(db => db.PromptLog.Where(p => p.Id == id).ExecuteUpdateAsync(u =>
                {
                    u.SetProperty(p => p.State, state).SetProperty(p => p.Status, status);
                    if (said is not null)
                    {
                        u.SetProperty(p => p.Answer, said);
                    }
                    if (why is not null)
                    {
                        u.SetProperty(p => p.Error, why);
                    }
                    if (promptTokens is not null)
                    {
                        u.SetProperty(p => p.PromptTokens, promptTokens);
                    }
                    if (completionTokens is not null)
                    {
                        u.SetProperty(p => p.CompletionTokens, completionTokens);
                    }
                    if (done)
                    {
                        u.SetProperty(p => p.DurationMs, ms);
                    }
                })), TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>
        /// Ends it, when nothing else did, as the request ends (dispose at the end of the request): stopped when the caller
        /// went away, else failed. A request ended on any path is never left running in the log.
        /// </summary>
        public IDisposable Closing(CancellationToken caller) => new Closer(this, caller);

        private sealed class Closer(Line line, CancellationToken caller) : IDisposable
        {
            public void Dispose() => line.End(caller.IsCancellationRequested ? PromptLogEntry.Stopped : PromptLogEntry.Failed, caller.IsCancellationRequested ? 499 : 500,
                error: caller.IsCancellationRequested ? "The caller went away before the answer." : "The request ended without an answer.");
        }

        /// <summary>For tests: every write so far has run.</summary>
        public Task Written
        {
            get
            {
                lock (_lock)
                {
                    return _last;
                }
            }
        }
    }

    private TimeProvider Clock => clock;

    /// <summary>A request arrived: in the log at once, waiting for its turn. Null when it asks no question (nothing to show).</summary>
    public Line? Begin(Guid person, string? keyToken, JsonObject request)
    {
        if (LastQuestion(request) is not { Length: > 0 } question)
        {
            return null;
        }
        return new Line(this, new PromptLogEntry
        {
            UserId = person, KeyToken = keyToken, Model = request["model"]?.GetValue<string>() ?? "", Prompt = Cut(question), Status = 0,
            State = PromptLogEntry.Waiting, At = clock.GetUtcNow(),
        });
    }

    /// <summary>A request answered at once (from the answer cache): in the log done, with its answer.</summary>
    public void Record(Guid person, string? keyToken, JsonObject request, JsonObject? completion, int status) =>
        Begin(person, keyToken, request)?.End(status is >= 200 and < 300 ? PromptLogEntry.Done : PromptLogEntry.Failed, status, completion: completion);

    private async Task WriteAsync(Func<AppDbContext, Task> write)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await write(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An API request's line in the prompt log was not written: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
