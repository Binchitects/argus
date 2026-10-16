using Llm.Core.Access;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

/// <summary>Access control's tables: roles, who holds them, the rules on resources, their history, and the version every replica reads.</summary>
public static class AccessTables
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<AccessRole>(e =>
        {
            e.ToTable("access_roles");
            e.Property(r => r.Key).HasMaxLength(100);
            e.HasIndex(r => r.Key).IsUnique();
            e.Property(r => r.Name).HasMaxLength(100);
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Description).HasMaxLength(500);
            e.Property(r => r.Permissions).HasDefaultValueSql("'{}'::text[]");
        });
        builder.Entity<AccessAssignment>(e =>
        {
            e.ToTable("access_assignments");
            e.HasOne<AccessRole>().WithMany().HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(a => new { a.SubjectKind, a.SubjectId });
            e.OwnsOne(a => a.Scope, s =>
            {
                s.ToJson();
                s.Ignore(x => x.IsEmpty);
            });
        });
        builder.Entity<AccessRule>(e =>
        {
            e.ToTable("access_rules");
            e.Property(r => r.ResourceKind).HasMaxLength(40);
            e.Property(r => r.Resource).HasMaxLength(500);
            e.Property(r => r.Action).HasMaxLength(40);
            e.Property(r => r.Note).HasMaxLength(500);
            e.HasIndex(r => new { r.ResourceKind, r.Resource });
            e.HasIndex(r => new { r.SubjectKind, r.SubjectId });
        });
        builder.Entity<AccessChange>(e =>
        {
            e.ToTable("access_changes");
            e.Property(c => c.Kind).HasMaxLength(20);
            e.Property(c => c.Before).HasColumnType("jsonb");
            e.Property(c => c.After).HasColumnType("jsonb");
            e.HasIndex(c => c.At);
            e.HasIndex(c => c.BulkId);
        });
        builder.Entity<AccessMeta>(e =>
        {
            e.ToTable("access_meta");
            e.Property(m => m.Id).ValueGeneratedNever();
            e.HasData(new AccessMeta { Id = 1, Version = 1 });
        });
    }
}
