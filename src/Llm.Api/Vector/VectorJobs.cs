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
public static class VectorJobs
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
                var (drawn, problem) = await sp.GetRequiredService<SvgDraw>().DrawAsync(me, job.Prompt ?? "", style, w, h, phase, ct);
                if (drawn is null)
                {
                    throw new VectorRefusedException(problem ?? "Nothing was drawn.");
                }
                await phase("Keeping it");
                return [await store.AddAsync(me, drawn.Svg, MediaJob.Draw, job.Prompt, "drawn", style, null, drawn.Model, ct)];
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
            (source, picture) = await FlatPictureAsync(sp, db, job, me, phase, ct);
            type = "image/png";
        }
        await phase("Tracing it into shapes");
        var traced = await sp.GetRequiredService<VectorSandbox>().TraceAsync(picture, type, style, ct);
        var svg = SvgSafe.Clean(Named(traced));
        await phase("Keeping it");
        return [await sp.GetRequiredService<VectorStore>().AddAsync(me, svg, MediaJob.Trace, job.Prompt ?? source?.Prompt, "traced", style.Name, source?.Id, null, ct)];
    }

    /// <summary>
    /// A traced drawing made into parts: its first shape, when it covers the canvas, the background (id "background"); the rest in
    /// one group, the subject (id "subject"), in their order; each shape named shape-1, shape-2... So the subject moves as one,
    /// or its shapes one after another (a stagger), over a background that stays.
    /// </summary>
    public static string Named(string traced)
    {
        var root = System.Xml.Linq.XElement.Parse(SvgSafe.Clean(traced).Markup);
        var box = SvgMotion.ViewBox(root);
        var paths = root.Elements(SvgSafe.Svg + "path").ToList();
        var n = 0;
        foreach (var path in root.Descendants(SvgSafe.Svg + "path").Where(p => p.Attribute("id") is null))
        {
            path.SetAttributeValue("id", $"shape-{++n}");
        }
        if (paths.Count > 1 && Covers(paths[0], box))
        {
            paths[0].SetAttributeValue("id", "background");
            paths.RemoveAt(0);
        }
        if (paths.Count > 0)
        {
            var subject = new System.Xml.Linq.XElement(SvgSafe.Svg + "g", new System.Xml.Linq.XAttribute("id", "subject"));
            paths[0].AddBeforeSelf(subject);
            foreach (var p in paths)
            {
                p.Remove();
                subject.Add(p);
            }
        }
        return root.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    /// <summary>Whether a traced path is the whole canvas: vtracer starts with the background as a path from corner to corner.</summary>
    private static bool Covers(System.Xml.Linq.XElement path, (double X, double Y, double W, double H) box)
    {
        var numbers = System.Text.RegularExpressions.Regex.Matches((string?)path.Attribute("d") ?? "", @"-?\d+(\.\d+)?")
            .Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var (tx, ty) = Translate((string?)path.Attribute("transform"));
        var xs = numbers.Where((_, i) => i % 2 == 0).Select(x => x + tx).ToList();
        var ys = numbers.Where((_, i) => i % 2 == 1).Select(y => y + ty).ToList();
        return xs.Count > 1 && ys.Count > 1 && xs.Max() - xs.Min() >= box.W * 0.95 && ys.Max() - ys.Min() >= box.H * 0.95;
    }

    private static (double X, double Y) Translate(string? transform)
    {
        var m = System.Text.RegularExpressions.Regex.Match(transform ?? "", @"translate\(\s*(-?[\d.]+)[\s,]+(-?[\d.]+)");
        return m.Success
            ? (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            : (0, 0);
    }

    /// <summary>A flat picture from the person's words by the picture model (their credit, today's pictures, its turn on the GPU), kept as their picture.</summary>
    private static async Task<(MediaItem Item, byte[] Png)> FlatPictureAsync(IServiceProvider sp, AppDbContext db, MediaJob job, AppUser me, Func<string, Task> phase, CancellationToken ct)
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
        var verdict = await sp.GetRequiredService<Safeguards.Safeguards>().CheckMessageAsync(me, job.Prompt ?? "", 0, false, model.Name, ct);
        if (!verdict.Allowed)
        {
            throw new VectorRefusedException(verdict.Reason ?? "A rule here stopped these words.");
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
        var png = (await sp.GetRequiredService<SdServer>().PicturesAsync(model, $"{verdict.Text ?? job.Prompt}, {FlatStyle}", 1024, 1024, 1, ct))[0];
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

    /// <summary>The formats a drawing is made: a picture (PNG), a looping GIF, an MP4 (on a background), a WebM (see-through kept).</summary>
    public static readonly IReadOnlyList<string> Formats = ["png", "gif", "mp4", "webm"];

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
            var gif = format == "gif";
            var side = Math.Clamp(Side(options, "side", gif ? 480 : 1080), 64, gif ? 640 : 1920);
            var fps = Math.Clamp(Side(options, "fps", gif ? 20 : 30), 5, gif ? 25 : 60);
            seconds = Math.Clamp(options?["seconds"] is JsonValue sv && sv.TryGetValue<double>(out var sec) ? sec : vector.Seconds, 0.2, gif ? 12 : 60);
            await phase($"Recording {seconds:0.#} seconds, {fps} frames a second");
            var frames = await render.FramesAsync(markup, side, seconds.Value, fps, ct);
            await phase($"Making the {format.ToUpperInvariant()}");
            (bytes, contentType, extension) = await sp.GetRequiredService<VectorSandbox>().EncodeAsync(frames, fps, format, Text(options, "background") is { } bg && Color(bg) ? bg : "white", ct);
            kind = gif ? MediaItem.Picture : MediaItem.Video;
            (width, height) = Png.SizeOf(frames[0]) is { } s ? (s.Width, s.Height) : ((int?)null, (int?)null);
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
