using Llm.Core.Chat;
using Llm.Core.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    /// <summary>Cookie, 2FA and OIDC key material survives restarts and is shared by every replica.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict<Guid>();

        builder.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasColumnName("key").HasMaxLength(200);
            e.Property(s => s.Value).HasColumnName("value");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        });

        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.LdapDn).HasMaxLength(1000);
            e.HasIndex(u => u.NormalizedEmail).IsUnique();
        });

        builder.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.Property(c => c.Title).HasMaxLength(200);
            e.Property(c => c.Thinking).HasMaxLength(20);
            e.HasIndex(c => new { c.UserId, c.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.Property(m => m.Role).HasMaxLength(20);
            e.Property(m => m.ToolCallId).HasMaxLength(200);
            e.Property(m => m.ToolName).HasMaxLength(200);
            e.Property(m => m.Model).HasMaxLength(200);
            e.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
        });
        builder.Entity<ChatAttachment>(e =>
        {
            e.ToTable("chat_attachments");
            e.Property(a => a.FileName).HasMaxLength(260);
            e.Property(a => a.ContentType).HasMaxLength(200);
            e.HasIndex(a => a.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.Actor).HasMaxLength(256);
            e.Property(a => a.Target).HasMaxLength(256);
            e.Property(a => a.Ip).HasMaxLength(64);
            e.HasIndex(a => a.At);
        });
    }
}

/// <summary>A key/value the app owns at runtime (prices, feature switches, ...).</summary>
public class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
