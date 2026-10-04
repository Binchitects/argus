using Llm.Core.Chat;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

/// <summary>The tables of chats' canvases and their versions (called from <see cref="AppDbContext"/>).</summary>
internal static class CanvasTables
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<Canvas>(e =>
        {
            e.ToTable("canvases");
            e.Property(c => c.Title).HasMaxLength(200);
            e.Property(c => c.Kind).HasMaxLength(20);
            e.Property(c => c.Language).HasMaxLength(40);
            e.Property(c => c.Version).IsConcurrencyToken();
            e.HasIndex(c => c.ConversationId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(c => c.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CanvasVersion>(e =>
        {
            e.ToTable("canvas_versions");
            e.HasKey(v => new { v.CanvasId, v.Number });
            e.Property(v => v.Title).HasMaxLength(200);
            e.Property(v => v.Author).HasMaxLength(20);
            e.Property(v => v.Summary).HasMaxLength(300);
            e.HasOne<Canvas>().WithMany().HasForeignKey(v => v.CanvasId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
