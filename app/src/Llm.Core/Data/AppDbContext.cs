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
