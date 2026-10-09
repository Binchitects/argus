using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

/// <summary>How much one thing took on one day (a database, the chat's files of one kind, the model library, a disk): the storage page's trends.</summary>
public sealed class StorageSample
{
    public DateOnly Day { get; set; }

    /// <summary>What was measured: "db:llmapp", "files:picture", "models", "disk:/dev/nvme0n1p2"…</summary>
    public required string Key { get; set; }

    public long Bytes { get; set; }
}

/// <summary>A person's own room for files, instead of the company's (Settings → Storage).</summary>
public sealed class StorageQuota
{
    public Guid UserId { get; set; }

    public int Megabytes { get; set; }
}

public partial class AppDbContext
{
    /// <summary>The storage page's trends: one row a day for each thing measured (Llm.Api/Storage).</summary>
    public DbSet<StorageSample> StorageSamples => Set<StorageSample>();

    /// <summary>People given their own room for files.</summary>
    public DbSet<StorageQuota> StorageQuotas => Set<StorageQuota>();

    private static void StorageModel(ModelBuilder builder)
    {
        builder.Entity<StorageSample>(e =>
        {
            e.ToTable("storage_samples");
            e.HasKey(x => new { x.Day, x.Key });
            e.Property(x => x.Key).HasMaxLength(200);
        });
        builder.Entity<StorageQuota>(e =>
        {
            e.ToTable("storage_quotas");
            e.HasKey(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
