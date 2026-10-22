using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Media;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Vector;

/// <summary>Why a piece of vector work stopped, in words for the person.</summary>
public sealed class VectorRefusedException(string message) : Exception(message);

/// <summary>
/// The media studio's vector work, as jobs: a drawing drawn by the model from words; a picture traced into one (drawn
/// first by the picture model, flat, when it is words); a drawing changed or animated as asked; and a drawing made a
/// picture or a video (PNG, GIF, MP4, WebM), which goes into the gallery beside it.
/// </summary>
public static partial class VectorJobs
{
    /// <summary>What a picture is asked to look like when it is drawn to be traced: flat, so its shapes trace clean.</summary>
    public const string FlatStyle = "flat vector illustration, clean geometric shapes, bold solid colors, limited palette of 6 colors, no gradients, no texture, crisp edges, centered, plain white background";

    public static int Side(JsonNode? options, string key, int fallback) =>
        options?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : fallback;

    public static string? Text(JsonNode? options, string key) => options?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static async Task<IReadOnlyList<MediaItem>> RunAsync(IServiceProvider sp, AppDbContext db, MediaJob job, AppUser me, Func<string, Task> phase, CancellationToken ct)
    {
        var options = JsonNode.Parse(job.Options);
        var store = sp.GetRequiredService<VectorStore>();
        switch (job.Action)
        {
            case MediaJob.Draw:
            {
                var (w, h) = (Math.Clamp(Side(options, "width", 512), 16, 4096), Math.Clamp(Side(options, "height", 512), 16, 4096));
                var style = Text(options, "style") ?? "flat";
                var draw = sp.GetRequiredService<SvgDraw>();
                await phase("Planning the picture");
                var (scene, motion) = await draw.SplitAsync(me, job.Prompt ?? "", ct);
                var (drawn, problem) = await draw.DrawAsync(me, scene, style, w, h, phase, ct);
                if (drawn is null)
                {
                    throw new VectorRefusedException(problem ?? "Nothing was drawn.");
                }
                await phase("Keeping it");
                var made = await store.AddAsync(me, drawn.Svg, MediaJob.Draw, job.Prompt, "drawn", style, null, drawn.Model, ct);
                await AnimateAsync(sp, db, me, made, motion, Text(options, "animate"), phase, ct);
                return [made];
            }
            case MediaJob.Trace:
                return await TraceAsync(sp, db, job, me, options, phase, ct);
            case MediaJob.Reshape or MediaJob.Animate:
            {
                var (item, vector) = await VectorOfAsync(db, job.SourceId, me, ct);
                var draw = sp.GetRequiredService<SvgDraw>();
                if (job.Action == MediaJob.Reshape)
                {
                    var (drawn, problem) = await draw.ChangeAsync(me, vector.Still, job.Prompt ?? "", phase, ct);
                    if (drawn is null)
                    {
                        throw new VectorRefusedException(problem ?? "Nothing was changed.");
                    }
                    await phase("Keeping it");
                    // Its motions stay: those whose parts are gone say so, and are left out.
                    var motions = VectorStore.MotionsOf(vector.Motions);
                    var problems = await store.ChangeAsync(item, vector, drawn.Svg.Markup, motions, $"asked: {job.Prompt}", ct);
                    if (problems.Count > 0)
                    {
                        var kept = motions.Where((_, i) => !problems.Any(p => p.StartsWith($"motion {i + 1}:", StringComparison.Ordinal))).ToList();
                        await store.ChangeAsync(item, vector, vector.Still, kept, "motions of parts that are gone left out", ct);
                    }
                }
                else
                {
                    var (motions, _, problem) = await draw.AnimateAsync(me, vector.Still, VectorStore.MotionsOf(vector.Motions), job.Prompt ?? "", phase, ct);
                    if (motions is null)
                    {
                        throw new VectorRefusedException(problem ?? "No motions came.");
                    }
                    await phase("Keeping it");
                    await store.ChangeAsync(item, vector, vector.Still, motions, $"animated: {job.Prompt}", ct);
                }
                return [item];
            }
            case MediaJob.Export:
                return await ExportAsync(sp, db, job, me, options, phase, ct);
            default:
                throw new VectorRefusedException("Unknown work.");
        }
    }

    private static async Task<(MediaItem Item, MediaVector Vector)> VectorOfAsync(AppDbContext db, Guid? id, AppUser me, CancellationToken ct)
    {
        var item = id is { } i ? await db.MediaItems.SingleOrDefaultAsync(x => x.Id == i && x.OwnerId == me.Id && x.Kind == MediaItem.Vector, ct) : null;
        var vector = item is null ? null : await db.MediaVectors.SingleOrDefaultAsync(v => v.ItemId == item.Id, ct);
        return item is not null && vector is not null ? (item, vector) : throw new VectorRefusedException("The drawing it works on is gone.");
    }

    /// <summary>A picture traced: the person's (a picture item), or one the picture model draws flat from their words first (kept beside it).</summary>
    private static async Task<IReadOnlyList<MediaItem>> TraceAsync(IServiceProvider sp, AppDbContext db, MediaJob job, AppUser me, JsonNode? options, Func<string, Task> phase,
        CancellationToken ct)
    {
        var style = VectorSandbox.StyleOf(Text(options, "trace"));
        // A picture of one's own moves as its words say; a picture drawn from words, as they say (gently alive when they say not, as asked).
        string? scene = null;
        var motion = job.SourceId is not null ? job.Prompt : null;
        byte[] picture;
        string type;
        MediaItem? source;
        if (job.SourceId is { } sid)
        {
            source = await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == sid && i.OwnerId == me.Id && i.Kind == MediaItem.Picture, ct)
                ?? throw new VectorRefusedException("The picture to trace is gone.");
            var file = await db.ChatAttachments.AsNoTracking().SingleAsync(a => a.Id == source.AttachmentId, ct);
            (picture, type) = (file.Data ?? throw new VectorRefusedException("The picture to trace is gone."), file.ContentType);
        }
        else
        {
            // What is seen is drawn (the motion is not the picture model's to draw: it would draw a clock for "a file being sent").
            await phase("Planning the picture");
            (scene, motion) = await sp.GetRequiredService<SvgDraw>().SplitAsync(me, job.Prompt ?? "", ct);
            var log = sp.GetRequiredService<ILogger<SvgDraw>>();
            Planned(log, scene, motion);
            (source, picture) = await FlatPictureAsync(sp, db, job, me, scene, phase, ct);
            type = "image/png";
        }
        var (things, maps) = await ThingsAsync(sp, me, scene ?? source?.Prompt, picture, phase, ct);
        await phase("Tracing it into shapes");
        var svg = SvgSafe.Clean(await sp.GetRequiredService<VectorSandbox>().TraceAsync(picture, type, style, things, maps, ct));
        await phase("Keeping it");
        // Named by what it shows: a picture's own words (the words given with it say how it moves), else the words it was drawn from.
        var named = job.SourceId is not null ? source?.Prompt ?? job.Prompt : job.Prompt;
        var made = await sp.GetRequiredService<VectorStore>().AddAsync(me, svg, MediaJob.Trace, named, "traced", style.Name, source?.Id, null, ct);
        await AnimateAsync(sp, db, me, made, motion, Text(options, "animate"), phase, ct);
        return [made];
    }

    /// <summary>The words that ask for no motion, and only a drawing's own words then: gently alive, not a show.</summary>
    public const string Alive = "Make it gently alive: a few subtle loops that suit each thing (float, sway, drift, twinkle, glow, breathe, blink); no entrances.";

    /// <summary>
    /// A new drawing animated in the same work: by the motion its words asked for; with none, gently alive when
    /// <paramref name="mode"/> is "always" (the studio's own), still when "auto" (a chat's: it animates when asked) or
    /// "never". A plan that cannot be made leaves it still: the drawing is not lost for its motions.
    /// </summary>
    private static async Task AnimateAsync(IServiceProvider sp, AppDbContext db, AppUser me, MediaItem item, string? motion, string? mode, Func<string, Task> phase,
        CancellationToken ct)
    {
        var request = motion is { Length: > 0 } ? motion : (mode ?? "always") == "always" ? Alive : null;
        if (mode == "never" || request is null)
        {
            return;
        }
        var vector = await db.MediaVectors.SingleAsync(v => v.ItemId == item.Id, ct);
        var (motions, _, _) = await sp.GetRequiredService<SvgDraw>().AnimateAsync(me, vector.Still, [], request, phase, ct);
        if (motions is { Count: > 0 })
        {
            await phase("Keeping it");
            await sp.GetRequiredService<VectorStore>().ChangeAsync(item, vector, vector.Still, motions, $"animated: {request}", ct);
        }
    }

    /// <summary>
    /// The things in a picture that could move apart, and where each is in it: named by the model from the words it was drawn
    /// from, found by the studio's segmenter. None when there are no words, the segmenter is not there or a step fails (the
    /// drawing is then made of parts by its islands of shapes).
    /// </summary>
    private static async Task<(IReadOnlyList<Thing> Things, IReadOnlyList<byte[]> Maps)> ThingsAsync(IServiceProvider sp, AppUser me, string? words, byte[] picture,
        Func<string, Task> phase, CancellationToken ct)
    {
        var studio = sp.GetRequiredService<StudioServer>();
        var log = sp.GetRequiredService<ILogger<SvgDraw>>();
        if (string.IsNullOrWhiteSpace(words) || await studio.HealthAsync(ct) is not { Segment: true })
        {
            return ([], []);
        }
        await phase("Finding its parts");
        var things = await sp.GetRequiredService<SvgDraw>().ThingsAsync(me, words, ct);
        if (things.Count == 0)
        {
            return ([], []);
        }
        try
        {
            var maps = await studio.SegmentAsync(picture, [.. things.Select(t => t.Looks)], ct);
            Found(log, things);
            return maps.Count == things.Count ? (things, maps) : ([], []);
        }
        catch (Exception ex) when (ex is SdServer.SdException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            NotSegmented(log, ex);
            return ([], []);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A drawing planned: the picture {Scene}; the motion {Motion}")]
    private static partial void Planned(ILogger logger, string scene, string motion);

    [LoggerMessage(Level = LogLevel.Information, Message = "A traced picture's things: {Things}")]
    private static partial void Found(ILogger logger, IReadOnlyList<Thing> things);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The segmenter did not find the parts: traced by islands")]
    private static partial void NotSegmented(ILogger logger, Exception ex);

    /// <summary>A flat picture from the person's words by the picture model (their credit, today's pictures, its turn on the GPU), kept as their picture.</summary>
    /// <param name="scene">What the picture shows (the words' still picture, in English).</param>
    private static async Task<(MediaItem Item, byte[] Png)> FlatPictureAsync(IServiceProvider sp, AppDbContext db, MediaJob job, AppUser me, string scene, Func<string, Task> phase,
        CancellationToken ct)
    {
        var media = sp.GetRequiredService<MediaControl>();
        var ours = media.All.Where(m => m.Mode == Llm.Core.Models.MediaModes.Image).Select(m => m.Name).ToList();
        var model = media.Pick(Llm.Core.Models.MediaModes.Image, null, await sp.GetRequiredService<ModelPolicy>().AllowedAsync(me, ours, ct))
            ?? throw new VectorRefusedException("There is no picture model you may use (Admin → Models).");
        if (await sp.GetRequiredService<Gateway.Credit>().RefusalAsync(me, Llm.Core.Access.CreditKind.Pictures, ct) is { } spent)
        {
            throw new VectorRefusedException(spent);
        }
        if (await sp.GetRequiredService<Safeguards.Safeguards>().TakeImageAsync(me.Id, ct) is { } limit)
        {
            throw new VectorRefusedException(limit);
        }
        // The person's words, and the picture's words made of them (what the picture model is sent), each through the rules.
        var safeguards = sp.GetRequiredService<Safeguards.Safeguards>();
        var verdict = await safeguards.CheckMessageAsync(me, job.Prompt ?? "", 0, false, model.Name, ct);
        var seen = verdict.Allowed ? await safeguards.CheckMessageAsync(me, scene, 0, false, model.Name, ct) : verdict;
        if (!verdict.Allowed || !seen.Allowed)
        {
            throw new VectorRefusedException((verdict.Allowed ? seen : verdict).Reason ?? "A rule here stopped these words.");
        }
        await phase("Waiting for its turn on the GPU");
        await using var hold = await sp.GetRequiredService<GpuShare>().HoldAsync(model, ct);
        await phase($"Loading {model.Name}");
        if (await media.ReadyAsync(model.Name, ct) is { } notReady)
        {
            throw new VectorRefusedException(notReady);
        }
        await phase("Drawing it flat, to trace");
        var began = DateTimeOffset.UtcNow;
        var png = (await sp.GetRequiredService<SdServer>().PicturesAsync(model, $"{seen.Text ?? scene}, {FlatStyle}", 1024, 1024, 1, ct))[0];
        media.Touch(model.Name);
        var prices = sp.GetRequiredService<Gateway.PriceBook>();
        var key = Gateway.AnswerCache.HashOf(await sp.GetRequiredService<Chat.ChatKey>().GetAsync(ct));
        await sp.GetRequiredService<Gateway.SpendLog>().BookPicturesAsync(me.Email ?? "", key, model.Name, 1, model.Row.UnitPrice ?? prices.Defaults.PerImage, began, DateTimeOffset.UtcNow, ct);
        var file = new ChatAttachment
        {
            UserId = me.Id, FileName = Chat.Tools.ImageTool.FileName(job.Prompt ?? "picture"), ContentType = "image/png", Size = png.Length, Kind = "image", Data = png,
            Text = "", Origin = "picture",
        };
        var item = new MediaItem
        {
            OwnerId = me.Id, AttachmentId = file.Id, Kind = MediaItem.Picture, Action = MediaJob.Make, Prompt = job.Prompt, Width = 1024, Height = 1024,
            Model = model.Name, Thumb = MediaJobs.Thumb(png),
        };
        db.ChatAttachments.Add(file);
        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);
        return (item, png);
    }

    /// <summary>
    /// The formats a drawing is made: a picture (PNG), a looping GIF, an MP4 (on a background), a WebM (see-through kept), a
    /// sprite sheet (its frames in one PNG, for games and pages).
    /// </summary>
    public static readonly IReadOnlyList<string> Formats = ["png", "gif", "mp4", "webm", "sprite"];

    /// <summary>A sprite sheet's frames at most, and its side at most (pixels).</summary>
    public const int MostSprites = 144, MostSheet = 8192;

    private static async Task<IReadOnlyList<MediaItem>> ExportAsync(IServiceProvider sp, AppDbContext db, MediaJob job, AppUser me, JsonNode? options, Func<string, Task> phase,
        CancellationToken ct)
    {
        var (item, vector) = await VectorOfAsync(db, job.SourceId, me, ct);
        var markup = Encoding.UTF8.GetString((await db.ChatAttachments.AsNoTracking().SingleAsync(a => a.Id == item.AttachmentId, ct)).Data!);
        var format = Text(options, "format") is { } f && Formats.Contains(f) ? f : "png";
        var render = sp.GetRequiredService<SvgRender>();
        var stem = Path.GetFileNameWithoutExtension(VectorStore.FileName(item.Prompt));
        byte[] bytes;
        string contentType, extension, kind;
        int? width = null, height = null;
        double? seconds = null;
        byte[]? thumb;
        if (format == "png")
        {
            var side = Math.Clamp(Side(options, "side", 2048), 16, SvgRender.MostSide);
            await phase($"Drawing it {side} pixels wide");
            bytes = await render.PngAsync(markup, side, vector.Loops ? Math.Min(vector.Seconds, 3) : vector.Seconds, ct);
            (contentType, extension, kind) = ("image/png", ".png", MediaItem.Picture);
            (width, height) = Png.SizeOf(bytes) is { } s ? (s.Width, s.Height) : ((int?)null, (int?)null);
            thumb = MediaJobs.Thumb(bytes);
        }
        else
        {
            if (vector.Seconds <= 0)
            {
                throw new VectorRefusedException("This drawing does not move: give it motions first, or make it a PNG.");
            }
            var (gif, sprite) = (format == "gif", format == "sprite");
            var side = sprite ? Math.Clamp(Side(options, "side", 256), 32, 1024) : Math.Clamp(Side(options, "side", gif ? 480 : 1080), 64, gif ? 640 : 1920);
            var fps = sprite ? Math.Clamp(Side(options, "fps", 12), 2, 30) : Math.Clamp(Side(options, "fps", gif ? 20 : 30), 5, gif ? 25 : 60);
            seconds = Math.Clamp(options?["seconds"] is JsonValue sv && sv.TryGetValue<double>(out var sec) ? sec : vector.Seconds, 0.2, gif ? 12 : sprite ? 12 : 60);
            if (sprite)
            {
                // As many frames as fit: at most MostSprites, the sheet at most MostSheet a side.
                fps = Math.Max(1, Math.Min(fps, (int)Math.Floor(MostSprites / seconds.Value)));
                var columns = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, Math.Round(seconds.Value * fps))));
                side = Math.Min(side, MostSheet / columns);
            }
            await phase($"Recording {seconds:0.#} seconds, {fps} frames a second");
            var frames = await render.FramesAsync(markup, side, seconds.Value, fps, ct);
            await phase($"Making the {format.ToUpperInvariant()}");
            (bytes, contentType, extension) = await sp.GetRequiredService<VectorSandbox>().EncodeAsync(frames, fps, format, Text(options, "background") is { } bg && Color(bg) ? bg : "white", ct);
            kind = gif || sprite ? MediaItem.Picture : MediaItem.Video;
            (width, height) = Png.SizeOf(sprite ? bytes : frames[0]) is { } s ? (s.Width, s.Height) : ((int?)null, (int?)null);
            thumb = MediaJobs.Thumb(frames[Math.Min(frames.Count - 1, (int)(frames.Count * 0.6))]);
        }
        if (await sp.GetRequiredService<Storage.StorageQuotas>().RefusalAsync(me.Id, bytes.Length, ct) is { } full)
        {
            throw new VectorRefusedException(full);
        }
        await phase("Keeping it");
        var file = new ChatAttachment
        {
            UserId = me.Id, FileName = stem + extension, ContentType = contentType, Size = bytes.Length, Kind = kind == MediaItem.Video ? "video" : "image",
            Data = bytes, Text = "", Seconds = seconds, Origin = kind == MediaItem.Video ? "video" : "picture",
        };
        var made = new MediaItem
        {
            OwnerId = me.Id, AttachmentId = file.Id, Kind = kind, Action = MediaJob.Export, Prompt = item.Prompt, ParentId = item.Id, Width = width, Height = height,
            Seconds = seconds, Thumb = thumb,
        };
        db.ChatAttachments.Add(file);
        db.MediaItems.Add(made);
        await db.SaveChangesAsync(ct);
        return [made];
    }

    /// <summary>A background colour as ffmpeg and CSS read it: a name or #hex.</summary>
    public static bool Color(string c) => c.Length is > 0 and <= 30 && (c.StartsWith('#') ? c.Length is 4 or 7 && c[1..].All(Uri.IsHexDigit) : c.All(char.IsLetter));
}
