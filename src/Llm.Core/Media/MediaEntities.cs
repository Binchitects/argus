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
    /// <summary>An SVG drawing, still or animated (its <see cref="MediaVector"/>).</summary>
    public const string Vector = "vector";

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
    /// <summary>A picture with its background taken away (the studio server).</summary>
    public const string Cutout = "cutout";
    /// <summary>Words spoken in a voice from a short sample, or the model's own (the studio server).</summary>
    public const string Speak = "speak";
    /// <summary>Music from a description and lyrics (the studio server).</summary>
    public const string Music = "music";
    /// <summary>An SVG drawn by the model from words.</summary>
    public const string Draw = "draw";
    /// <summary>A picture traced into an SVG (drawn first by the picture model when it is words).</summary>
    public const string Trace = "trace";
    /// <summary>An SVG changed by the model as asked.</summary>
    public const string Reshape = "reshape";
    /// <summary>An SVG given motions by the model as asked.</summary>
    public const string Animate = "animate";
    /// <summary>An SVG made a picture or a video (PNG, GIF, MP4, WebM).</summary>
    public const string Export = "export";

    public static readonly IReadOnlyList<string> Actions = [Make, Edit, Vary, Enlarge, Move, Cutout, Speak, Music, Draw, Trace, Reshape, Animate, Export];

    /// <summary>Work on vector drawings.</summary>
    public static bool OnVector(string action) => action is Draw or Trace or Reshape or Animate or Export;

    /// <summary>Work the studio server does (not the picture or video server).</summary>
    public static bool OnStudio(string action) => action is Cutout or Speak or Music;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string Action { get; set; }
    public string? Prompt { get; set; }
    /// <summary>The item it works on: a picture (all but Make, Speak and Music).</summary>
    public Guid? SourceId { get; set; }
    /// <summary>How many it makes (Make and Vary: 1 to 4).</summary>
    public int Count { get; set; } = 1;
    /// <summary>Its settings, JSON: <c>{ width, height, strength, seconds, lyrics, voice, exaggeration }</c>.</summary>
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

/// <summary>
/// A short video made from a storyboard: shots the model wrote from the person's words (or they did), each a picture set
/// moving with what the narrator says over it; the narration spoken, captions from it, music under it, all cut into one
/// MP4 (a <see cref="MediaItem"/>). Any shot made again on its own, and the video cut again.
/// </summary>
public sealed class MediaStory
{
    public const string Draft = "draft";
    public const string Making = "making";
    public const string Done = "done";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string Title { get; set; }
    /// <summary>What the person asked for.</summary>
    public required string Brief { get; set; }
    /// <summary>
    /// Its shots, JSON: <c>[{ visual, narration, seconds, picture, clip, speech, speechSeconds }]</c>; picture and clip are
    /// files (attachments) made for the shot, speech its narration's sound: each made again when what it comes from changes.
    /// </summary>
    public string Shots { get; set; } = "[]";
    /// <summary>The narrator: null for the person's own speech settings, or a sound file of theirs whose voice it is (with their word).</summary>
    public Guid? VoiceId { get; set; }
    /// <summary>The music under it, in words (style, instruments, mood); null: none.</summary>
    public string? Music { get; set; }
    /// <summary>The music made for it (an attachment), while the words and length it was made for hold.</summary>
    public Guid? MusicFileId { get; set; }
    /// <summary>What the music file was made for: its words and seconds.</summary>
    public string? MusicMadeFor { get; set; }
    public string Status { get; set; } = Draft;
    public string? Phase { get; set; }
    public string? Error { get; set; }
    /// <summary>The video, once cut (a media item).</summary>
    public Guid? VideoItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A vector drawing of the media studio (a <see cref="MediaItem"/> of kind vector): the drawing as it stands still, the
/// motions compiled into it, and every version kept. Its item's file is the drawing as people take it: animated, made safe.
/// </summary>
public sealed class MediaVector
{
    public Guid ItemId { get; set; }
    /// <summary>The drawing without motions (SVG, made safe).</summary>
    public required string Still { get; set; }
    /// <summary>Its motions, JSON: <c>[{ target, kind, duration, delay, ... }]</c>.</summary>
    public string Motions { get; set; } = "[]";
    /// <summary>How long it plays once through, and whether it loops (what an export records).</summary>
    public double Seconds { get; set; }
    public bool Loops { get; set; }
    /// <summary>Where it came from: drawn (by the model from words), traced (from a picture), uploaded, or written by hand.</summary>
    public required string Source { get; set; }
    public string? Style { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A version of a vector drawing as it was: what changed it, its still drawing and its motions.</summary>
public sealed class MediaVectorVersion
{
    public Guid ItemId { get; set; }
    public int Version { get; set; }
    /// <summary>What made this version: "drawn", "traced", "asked: …", "animated: …", "by hand", "motions".</summary>
    public required string Change { get; set; }
    public required string Still { get; set; }
    public string Motions { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
