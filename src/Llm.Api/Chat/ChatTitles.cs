using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// A new chat's title, written by the model for small steps from its first question while the
/// answer is written (the question's first line stands until then). Only with a small model:
/// the big model is never kept from an answer for a title.
/// </summary>
public sealed partial class ChatTitles(IServiceScopeFactory scopes, ILogger<ChatTitles> logger)
{
    internal const string TitlePrompt =
        "You name conversations between a person and an AI assistant. Write a title for the conversation that starts with the person's " +
        "message below: at most six words, in the language they write in, saying what it is about. No quotes, no full stop. Write only the title.";

    /// <summary>
    /// Writes the title and tells the page (a "title" event), unless the person renamed the chat
    /// meanwhile. Runs beside the answer, in a scope of its own (the answer's database context is
    /// busy); a failure keeps the first line, and never touches the answer.
    /// </summary>
    public async Task WriteAsync(Guid userId, Guid conversationId, string question, Func<object, Task> emit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(userId.ToString());
            if (user is null || await services.GetRequiredService<SmallModel>().ForAsync(user, ct) is not { } small)
            {
                return;
            }
            var asked = (await services.GetRequiredService<Safeguards.Safeguards>().MaskerAsync(user.Email ?? "", ct))(question.Trim());
            var request = new JsonObject
            {
                ["model"] = small.Name,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = TitlePrompt },
                    new JsonObject { ["role"] = "user", ["content"] = "<message>\n" + (asked.Length > 2000 ? asked[..2000] : asked) + "\n</message>" }),
                ["stream"] = true, ["max_tokens"] = 32, ["temperature"] = 0.3, ["user"] = user.Email!.ToLowerInvariant(),
            };
            if (ThinkingPresets.TemplateKwargs(SmallModel.NoThinking(small)) is { } kwargs)
            {
                request["chat_template_kwargs"] = kwargs;
            }
            var said = new StringBuilder();
            await foreach (var e in services.GetRequiredService<GatewayChat>().StreamAsync(request, user.Email!, ct))
            {
                if (e is ContentDelta c)
                {
                    said.Append(c.Text);
                }
            }
            if (Clean(said.ToString()) is not { Length: > 0 } title)
            {
                return;
            }
            // Over the first line only: a title the person gave meanwhile stays.
            var first = ChatService.TitleFrom(question);
            var changed = await services.GetRequiredService<AppDbContext>().Conversations
                .Where(c => c.Id == conversationId && c.Title == first)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, title), ct);
            if (changed > 0)
            {
                await emit(new { type = "title", title, model = small.Name });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The answer was stopped: the first line stays.
        }
        catch (Exception ex)
        {
            // Never the answer's failure: the first line stays.
            LogTitleFailed(logger, conversationId, ex.Message);
        }
    }

    /// <summary>The model's title as a title: its first line, without quotes, a "Title:" label or a full stop, at most 60 characters.</summary>
    public static string Clean(string said)
    {
        var line = said.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
        {
            line = line[6..];
        }
        line = line.Trim().TrimEnd('.', '。').Trim().Trim('"', '\'', '*', '#', '`', '“', '”', '«', '»', ' ').TrimEnd('.', '。').Trim();
        return line.Length == 0 ? "" : ChatService.TitleFrom(line);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {Conversation}: the small model wrote no title, so the first line stays ({Reason})")]
    private static partial void LogTitleFailed(ILogger logger, Guid conversation, string reason);
}
