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
