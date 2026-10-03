namespace Llm.Core.Models;

/// <summary>
/// A model downloaded from Hugging Face into the model library: the repository at a
/// revision, its files, where they go (under the library), and how far it is.
/// </summary>
public sealed class ModelDownload
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Repo { get; set; }
    /// <summary>The commit the files are taken at, so every part comes from the same one.</summary>
    public required string Revision { get; set; }
    /// <summary>The folder under the library the files go to (the repository's id).</summary>
    public required string Dir { get; set; }
    public List<DownloadFile> Files { get; set; } = [];
    /// <summary>queued, running, paused, done, failed.</summary>
    public string State { get; set; } = "queued";
    public long Bytes { get; set; }
    public long Total { get; set; }
    public string? Error { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>One file of a download: its path in the repository, size and SHA-256 (checked when it is in).</summary>
public sealed class DownloadFile
{
    public required string Path { get; set; }
    /// <summary>Where it goes under the download's folder, when not at its path in the repository.</summary>
    public string? Target { get; set; }
    public long Size { get; set; }
    public string? Sha256 { get; set; }
}
