namespace Llm.Core.Media;

/// <summary>
/// A picture, clip or sound in a person's media studio: its file (a <see cref="Chat.ChatAttachment"/>), what made it (a
/// description, an edit of another, an enlargement...), and the one it was made from, so each shows where it came from.
/// </summary>
public sealed class MediaItem
{
    public const string Picture = "picture";
    public const string Video = "video";
    public const string Sound = "sound";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public Guid AttachmentId { get; set; }
    /// <summary>picture, video or sound.</summary>
    public required string Kind { get; set; }
    /// <summary>What made it: <see cref="MediaJob"/>'s actions, or "upload" (the person's own).</summary>
    public required string Action { get; set; }
    /// <summary>The words it was made or changed with, when there were any.</summary>
    public string? Prompt { get; set; }
    /// <summary>The item it was made from (an edit's original), while that one is kept.</summary>
    public Guid? ParentId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? Seconds { get; set; }
    /// <summary>The model that made it.</summary>
    public string? Model { get; set; }
    /// <summary>A small JPEG of a picture (its longer side 384 pixels), for the gallery; null when the picture is no PNG.</summary>
    public byte[]? Thumb { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One piece of work for the media studio, in the background: drawn, changed, enlarged or set moving, on the GPU in its
/// turn. Kept in the database so any of the app's replicas can say how it goes; the one that took it does it.
/// </summary>
public sealed class MediaJob
{
    public const string Working = "working";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>Pictures from words.</summary>
    public const string Make = "make";
    /// <summary>A picture changed by words, all of it or the part painted over.</summary>
    public const string Edit = "edit";
    /// <summary>Pictures like one, a little different.</summary>
    public const string Vary = "vary";
    /// <summary>A picture four times as large, its details drawn in.</summary>
    public const string Enlarge = "enlarge";
    /// <summary>A short clip that starts from a picture.</summary>
    public const string Move = "move";

    public static readonly IReadOnlyList<string> Actions = [Make, Edit, Vary, Enlarge, Move];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string Action { get; set; }
    public string? Prompt { get; set; }
    /// <summary>The item it works on, for all but Make.</summary>
    public Guid? SourceId { get; set; }
    /// <summary>How many it makes (Make and Vary: 1 to 4).</summary>
    public int Count { get; set; } = 1;
    /// <summary>Its settings, JSON: <c>{ width, height, strength, seconds }</c>.</summary>
    public string Options { get; set; } = "{}";
    public string Status { get; set; } = Working;
    /// <summary>Where it is: waiting for the GPU, loading the model, drawing...</summary>
    public string? Phase { get; set; }
    public string? Error { get; set; }
    /// <summary>What it made, JSON: the items' ids.</summary>
    public string Items { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
