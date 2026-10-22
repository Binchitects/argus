using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Llm.Api.Media;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Vector;

/// <summary>
/// Vector drawings kept: a new one (its file, its gallery item with a thumbnail, its still drawing and motions, its first
/// version), and a change to one (compiled again, its file and thumbnail made again, a version more). Versions past
/// <see cref="MostVersions"/> are let go, the oldest first.
/// </summary>
public sealed class VectorStore(AppDbContext db, SvgRender render, TimeProvider clock)
{
    public const int MostVersions = 50;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static List<Motion> MotionsOf(string json) => JsonSerializer.Deserialize<List<Motion>>(json, Json) ?? [];

    /// <summary>The drawing as people take it: its motions compiled in (or still), and how long it plays.</summary>
    public static (string Markup, Timeline Timeline, IReadOnlyList<string> Problems) Compile(string still, IReadOnlyList<Motion> motions)
    {
        if (motions.Count == 0)
        {
            var safe = SvgSafe.Clean(still);
            return (safe.Markup, safe.Animated ? SmilTimeline(safe.Markup) : new Timeline(0, false), []);
        }
        var a = SvgMotion.Apply(still, motions);
        return (a.Markup, a.Timeline, a.Problems);
    }

    /// <summary>A drawing that moves by its own SMIL or CSS (one written so): about how long, from its durations; it loops when any repeats.</summary>
    public static Timeline SmilTimeline(string markup)
    {
        var root = XElement.Parse(markup);
        var seconds = root.Descendants().SelectMany(e => new[] { (string?)e.Attribute("dur"), (string?)e.Attribute("begin") })
            .OfType<string>().Select(Seconds).Where(s => s > 0).DefaultIfEmpty(0).Sum();
        var css = string.Concat(root.Descendants(SvgSafe.Svg + "style").Select(s => s.Value)) + string.Concat(root.DescendantsAndSelf().Select(e => (string?)e.Attribute("style") ?? ""));
        seconds = Math.Max(seconds, System.Text.RegularExpressions.Regex.Matches(css, @"(\d*\.?\d+)(m?s)\b").Select(m => Seconds(m.Value)).DefaultIfEmpty(0).Max());
        var loops = root.Descendants().Any(e => (string?)e.Attribute("repeatCount") == "indefinite") || css.Contains("infinite", StringComparison.Ordinal);
        return new Timeline(Math.Clamp(seconds, 0, 60), loops);
    }

    private static double Seconds(string v)
    {
        var t = v.Trim();
        var ms = t.EndsWith("ms", StringComparison.Ordinal);
        var n = t.TrimEnd('s', 'm');
        return double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (ms ? d / 1000 : d) : 0;
    }

    /// <summary>A thumbnail: the drawing as it looks once its entrances have played, small, over light checks where it is see-through.</summary>
    private async Task<byte[]?> ThumbAsync(string markup, Timeline timeline, CancellationToken ct)
    {
        try
        {
            var png = await render.PngAsync(markup, MediaJobs.ThumbSide, timeline.Loops ? Math.Min(timeline.Seconds, 3) : timeline.Seconds, ct);
            return MediaJobs.Thumb(png);
        }
        catch (Exception ex) when (ex is SvgException or Chat.Tools.SandboxException or Chat.Tools.Browser.CdpException)
        {
            return null;
        }
    }

    public static string FileName(string? words) =>
        Path.GetFileNameWithoutExtension(Chat.Tools.ImageTool.FileName(words ?? "drawing")) + ".svg";

    /// <summary>A new drawing in the person's studio.</summary>
    public async Task<MediaItem> AddAsync(AppUser me, SafeSvg svg, string action, string? prompt, string source, string? style, Guid? parentId, string? model, CancellationToken ct)
    {
        var (markup, timeline, _) = Compile(svg.Markup, []);
        var file = new ChatAttachment
        {
            UserId = me.Id, FileName = FileName(prompt), ContentType = "image/svg+xml", Size = Encoding.UTF8.GetByteCount(markup), Kind = "file",
            Data = Encoding.UTF8.GetBytes(markup), Text = "", Origin = "picture",
        };
        var item = new MediaItem
        {
            OwnerId = me.Id, AttachmentId = file.Id, Kind = MediaItem.Vector, Action = action, Prompt = prompt, ParentId = parentId,
            Width = (int)Math.Round(svg.Width), Height = (int)Math.Round(svg.Height), Model = model, Seconds = timeline.Seconds > 0 ? timeline.Seconds : null,
            Thumb = await ThumbAsync(markup, timeline, ct),
        };
        var vector = new MediaVector
        {
            ItemId = item.Id, Still = svg.Markup, Source = source, Style = style, Seconds = timeline.Seconds, Loops = timeline.Loops, UpdatedAt = clock.GetUtcNow(),
        };
        db.ChatAttachments.Add(file);
        db.MediaItems.Add(item);
        db.MediaVectors.Add(vector);
        db.MediaVectorVersions.Add(new MediaVectorVersion { ItemId = item.Id, Version = 1, Change = source, Still = svg.Markup });
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>A drawing changed: its still drawing, its motions or both; compiled again and kept as a new version. What could not be compiled is said.</summary>
    public async Task<IReadOnlyList<string>> ChangeAsync(MediaItem item, MediaVector vector, string still, IReadOnlyList<Motion> motions, string change, CancellationToken ct)
    {
        var safe = SvgSafe.Clean(still);
        var (markup, timeline, problems) = Compile(safe.Markup, motions);
        var file = await db.ChatAttachments.SingleAsync(a => a.Id == item.AttachmentId, ct);
        file.Data = Encoding.UTF8.GetBytes(markup);
        file.Size = file.Data.Length;
        vector.Still = safe.Markup;
        vector.Motions = JsonSerializer.Serialize(motions, Json);
        (vector.Seconds, vector.Loops) = (timeline.Seconds, timeline.Loops);
        vector.Version++;
        vector.UpdatedAt = clock.GetUtcNow();
        (item.Width, item.Height) = ((int)Math.Round(safe.Width), (int)Math.Round(safe.Height));
        item.Seconds = timeline.Seconds > 0 ? timeline.Seconds : null;
        item.Thumb = await ThumbAsync(markup, timeline, ct);
        db.MediaVectorVersions.Add(new MediaVectorVersion
        {
            ItemId = item.Id, Version = vector.Version, Change = change.Length > 300 ? change[..300] : change, Still = vector.Still, Motions = vector.Motions,
        });
        await db.SaveChangesAsync(ct);
        await db.MediaVectorVersions.Where(v => v.ItemId == item.Id && v.Version <= vector.Version - MostVersions).ExecuteDeleteAsync(ct);
        return problems;
    }
}
