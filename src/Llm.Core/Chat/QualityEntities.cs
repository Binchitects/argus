using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Chat;

/// <summary>
/// A person's thumbs up or down on an answer (its last message), with a reason when
/// down. One per person per answer; changed by rating again. It goes with the answer.
/// </summary>
public sealed class AnswerFeedback
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MessageId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public bool Up { get; set; }
    /// <summary>Down only: wrong, incomplete, too_long, unsafe, ignored_instructions or other.</summary>
    public string? Reason { get; set; }
    /// <summary>The person's own words with a down vote.</summary>
    public string? Comment { get; set; }
    /// <summary>Down only: the person shared the chat with the admins, who may then read it from the quality page.</summary>
    public bool Shared { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An API request in the prompt log (Admin → Prompt log): the question it asked last and the answer, cut at
/// <see cref="MaxText"/> characters each; whose key, which model, the tokens. The chat's questions and answers are read from
/// the chats themselves; these are the requests with a key, which nothing else keeps. Kept Retention:PromptLogDays.
/// </summary>
public sealed class PromptLogEntry
{
    public const int MaxText = 20_000;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>The key's hash at the gateway (never the key).</summary>
    public string? KeyToken { get; set; }
    public required string Model { get; set; }
    public required string Prompt { get; set; }
    public string? Answer { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    /// <summary>The gateway's status for it (200 for an answer).</summary>
    public int Status { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// What a person said about Arena itself (Send feedback): an idea, a problem, praise or anything else, with the page it was
/// sent from; read, marked and answered on Admin → Feedback (the answer reaches their bell).
/// </summary>
public sealed class ProductFeedback
{
    public const string New = "new";
    public const string Read = "read";
    public const string Done = "done";
    public static readonly string[] Kinds = ["idea", "problem", "praise", "other"];
    public static readonly string[] Statuses = [New, Read, Done];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>idea, problem, praise or other.</summary>
    public required string Kind { get; set; }
    public required string Text { get; set; }
    /// <summary>Where it was sent from: the page's path, or "Code Arena".</summary>
    public string? Page { get; set; }
    /// <summary>new, read or done.</summary>
    public string Status { get; set; } = New;
    /// <summary>An admin's answer, which the person gets in their bell.</summary>
    public string? Answer { get; set; }
    public Guid? AnsweredBy { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Arena: one question answered by two models, side by side and blind (Model A and
/// Model B), and the person's vote, after which the names are shown. Votes make the
/// leaderboard; they stay when the chat is deleted.
/// </summary>
public sealed class ArenaMatch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid QuestionId { get; set; }
    /// <summary>The first message of each answer (a child of the question), once it started.</summary>
    public Guid? AnswerA { get; set; }
    public Guid? AnswerB { get; set; }
    public required string ModelA { get; set; }
    public required string ModelB { get; set; }
    /// <summary>a, b, tie or bad (both bad); null until the person votes.</summary>
    public string? Vote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VotedAt { get; set; }
}

/// <summary>The tables of answer feedback and the arena.</summary>
public static class QualityModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<AnswerFeedback>(e =>
        {
            e.ToTable("answer_feedback");
            e.Property(f => f.Reason).HasMaxLength(30);
            e.Property(f => f.Comment).HasMaxLength(1000);
            e.HasIndex(f => new { f.MessageId, f.UserId }).IsUnique();
            e.HasIndex(f => f.UpdatedAt);
            e.HasIndex(f => f.ConversationId);
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(f => f.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ArenaMatch>(e =>
        {
            e.ToTable("arena_matches");
            e.Property(m => m.ModelA).HasMaxLength(200);
            e.Property(m => m.ModelB).HasMaxLength(200);
            e.Property(m => m.Vote).HasMaxLength(10);
            e.HasIndex(m => m.ConversationId);
            e.HasIndex(m => m.VotedAt);
            e.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        // The quality page counts answers by when they were written.
        builder.Entity<ChatMessage>().HasIndex(m => m.CreatedAt);
    }
}
