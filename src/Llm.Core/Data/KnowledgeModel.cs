using Llm.Core.Chat;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

public partial class AppDbContext
{
    /// <summary>Company knowledge and the chat's files read by their passages (Llm.Api/Knowledge).</summary>
    private static void KnowledgeModel(ModelBuilder builder)
    {
        builder.Entity<KnowledgeSource>(e =>
        {
            e.ToTable("knowledge_sources");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Location).HasMaxLength(2000);
            e.Property(x => x.Hosts).HasMaxLength(1000);
            e.Property(x => x.Groups).HasDefaultValueSql("'{}'::uuid[]");
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => x.Name).IsUnique();
        });
        builder.Entity<KnowledgeDocument>(e =>
        {
            e.ToTable("knowledge_documents");
            e.Property(x => x.Key).HasMaxLength(1000);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Version).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Readers).HasDefaultValueSql("'{}'::text[]");
            e.HasIndex(x => new { x.SourceId, x.Key }).IsUnique();
            e.HasIndex(x => x.AttachmentId);
            e.HasIndex(x => x.Readers).HasMethod("gin");
            e.HasOne<KnowledgeSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ChatAttachment>().WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<KnowledgeChunk>(e =>
        {
            e.ToTable("knowledge_chunks");
            e.Property(x => x.Heading).HasMaxLength(500);
            e.HasIndex(x => x.DocumentId);
            e.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<KnowledgeReaders>(e =>
        {
            e.ToTable("knowledge_readers");
            e.HasKey(x => new { x.SourceId, x.Key });
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(500);
            e.Property(x => x.People).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.Activity).HasMaxLength(100);
            e.HasIndex(x => x.People).HasMethod("gin");
            e.HasOne<KnowledgeSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
