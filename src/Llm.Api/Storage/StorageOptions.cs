namespace Llm.Api.Storage;

/// <summary>Configuration section "Storage": when a disk is too full, each person's room for files, and the clean-ups' defaults.</summary>
public sealed class StorageOptions
{
    /// <summary>A disk fuller than this share (percent) raises an alert.</summary>
    public int AlertPercent { get; set; } = 80;

    /// <summary>Each person's room for files, in megabytes, unless an admin gave them their own; null or 0: no limit.</summary>
    public int? PersonMegabytes { get; set; }

    /// <summary>Pictures, videos and speech the tools made are old after this many days (the clean-up's default).</summary>
    public int MediaDays { get; set; } = 90;

    /// <summary>How many backups scripts/backup.sh keeps (BACKUP_KEEP, which compose gives the app): what the preview of old ones keeps unless asked otherwise.</summary>
    public int BackupsKept { get; set; } = 14;

    /// <summary>The backups folder (BACKUP_DIR), as the app mounts it.</summary>
    public string BackupDir { get; set; } = "/backups";

    /// <summary>Where the app sees Docker's own disk (its images and named volumes): its root.</summary>
    public string DockerDir { get; set; } = "/";
}

/// <summary>Bytes, with how many things they are.</summary>
public sealed record Amount(long Count, long Bytes)
{
    public static readonly Amount None = new(0, 0);

    public Amount Plus(Amount other) => new(Count + other.Count, Bytes + other.Bytes);
}
