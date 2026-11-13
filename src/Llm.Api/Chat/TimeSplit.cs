using Llm.Core.Chat;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>Where a chat's time went: the model's (its answers' rounds), the tools' (their runs, sub-agents included), the person's.</summary>
public sealed record ChatTime(long ModelMs, long ToolMs, long HumanMs);

/// <summary>
/// Time accounting, as the view gateway.chat_times counts it (migration ChatTimes) for Accounting and the dashboards. A
/// person's time is a question's wait since the message it follows ended (reading the answer, writing the question), at
/// most <see cref="HumanCapMs"/>: longer, they were away.
/// </summary>
public static class TimeSplit
{
    /// <summary>The most of a person's time one question counts (30 minutes).</summary>
    public const int HumanCapMs = 30 * 60 * 1000;

    /// <summary>When a message ended: one written here starts at its time and runs its duration; one from Code Arena is put down as it ends.</summary>
    public static DateTimeOffset End(ChatMessage m) => m.SyncRef is null ? m.CreatedAt.AddMilliseconds(m.DurationMs ?? 0) : m.CreatedAt;

    /// <summary>A person's question: their time before it, since the answer (or tool result) it follows ended. Null: none counted (the first question, a task's, a note).</summary>
    public static int? HumanMs(ChatMessage question, ChatMessage? parent) =>
        question is { Role: "user", AuthorId: not null } && parent is { Role: "assistant" or "tool" }
            ? (int)Math.Clamp((question.CreatedAt - End(parent)).TotalMilliseconds, 0, HumanCapMs)
            : null;

    /// <summary>These messages' times together (each question's parent among them).</summary>
    public static ChatTime Of(IReadOnlyCollection<ChatMessage> messages)
    {
        var byId = messages.ToDictionary(m => m.Id);
        long model = 0, tools = 0, human = 0;
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "assistant":
                    model += m.DurationMs ?? 0;
                    break;
                case "tool":
                    tools += m.DurationMs ?? 0;
                    break;
                case "user":
                    human += HumanMs(m, m.ParentId is { } p ? byId.GetValueOrDefault(p) : null) ?? 0;
                    break;
            }
        }
        return new ChatTime(model, tools, human);
    }

    /// <summary>A chat's whole time, every branch of it.</summary>
    public static async Task<ChatTime> OfChatAsync(Llm.Core.Data.AppDbContext db, Guid conversation, CancellationToken ct)
    {
        var rows = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation)
            .Select(m => new ChatMessage
            {
                Id = m.Id, ConversationId = m.ConversationId, ParentId = m.ParentId, Role = m.Role, CreatedAt = m.CreatedAt, DurationMs = m.DurationMs, SyncRef = m.SyncRef,
                AuthorId = m.AuthorId,
            })
            .ToListAsync(ct);
        return Of(rows);
    }
}
