using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Media;

/// <summary>A painted edit's two pictures from the page, the same size: the picture as it was drawn there, and where it was painted (white).</summary>
public sealed record Painted(byte[] Picture, byte[] Mask);

/// <summary>
/// The media studio's work, in the background: each job checks the person's credit, room, daily pictures and safeguards,
/// waits its turn on the GPU (the model loaded when it is not), has the picture or video server make it, keeps what was made
/// as the person's files and the studio's items, and books its cost in the gateway's log as the chat's. A job that takes a
/// while says so in the bell when done.
/// </summary>
public sealed partial class MediaJobs(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MediaJobs> logger)
{
    /// <summary>Jobs a person may have going at once.</summary>
    public const int MostGoing = 3;
    /// <summary>A job still working after this was lost (its app stopped): it is shown as failed.</summary>
    public static readonly TimeSpan Lost = TimeSpan.FromMinutes(45);
    /// <summary>The thumbnails' longer side, in pixels.</summary>
    public const int ThumbSide = 384;
    /// <summary>A picture enlarged at most to this, a side (the upscaler makes it four times as large).</summary>
    public const int MostEnlarged = 8192;

    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public Task Finished(Guid job) => _running.TryGetValue(job, out var t) ? t : Task.CompletedTask;

    public void Go(Guid job, Painted? painted = null)
    {
        _running[job] = Task.Run(async () =>
        {
            try
            {
                await RunAsync(job, painted, CancellationToken.None);
            }
            catch (Exception ex)
            {
                JobFailed(logger, job, ex);
                await FailAsync(job, "It stopped unexpectedly. Try again.");
            }
            finally
            {
                _running.TryRemove(job, out _);
            }
        });
    }

    private sealed class Refused(string message) : Exception(message);

    /// <summary>The options a job was given (its JSON), read leniently.</summary>
    private static double? Number(JsonNode? o, string key) => o?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private async Task RunAsync(Guid id, Painted? painted, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var job = await db.MediaJobs.SingleAsync(j => j.Id == id, ct);
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == job.OwnerId, ct);
        var started = clock.GetUtcNow();
        try
        {
            var made = await MakeAsync(sp, db, job, me, painted, ct);
            job.Items = JsonSerializer.Serialize(made.Select(i => i.Id));
            job.Status = MediaJob.Done;
            job.Phase = null;
        }
        catch (Exception ex) when (ex is Refused or SdServer.SdException or HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
        {
            job.Status = MediaJob.Failed;
            job.Phase = null;
            var why = ex is Refused ? ex.Message : $"The {(job.Action == MediaJob.Move ? "clip" : "picture")} could not be made: {ex.Message}";
            job.Error = why.Length > 2000 ? why[..2000] : why;
        }
        job.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
        // Work that took a while is said in the bell: the person may have left the page.
        if (job.FinishedAt - started > TimeSpan.FromSeconds(45))
        {
            var what = job.Action == MediaJob.Move ? "clip" : "pictures";
            await sp.GetRequiredService<Notifications.Notifier>().SendAsync(job.OwnerId, job.Status == MediaJob.Done
                ? new Notifications.News("media", $"Your {what} {(what == "clip" ? "is" : "are")} ready", job.Prompt, "/media")
                : new Notifications.News("media", $"Your {what} could not be made", job.Error, "/media"), CancellationToken.None);
        }
    }

    private async Task FailAsync(Guid id, string why)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.MediaJobs.SingleOrDefaultAsync(j => j.Id == id && j.Status == MediaJob.Working) is { } job)
            {
                (job.Status, job.Phase, job.Error, job.FinishedAt) = (MediaJob.Failed, null, why, clock.GetUtcNow());
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            JobFailed(logger, id, ex);
        }
    }

    private static async Task PhaseAsync(AppDbContext db, MediaJob job, string phase)
    {
        job.Phase = phase;
        await db.SaveChangesAsync();
    }

    private static async Task<MediaControl.Model> ModelAsync(IServiceProvider sp, AppUser me, string mode, CancellationToken ct)
    {
        var media = sp.GetRequiredService<MediaControl>();
        var ours = media.All.Where(m => m.Mode == mode).Select(m => m.Name).ToList();
        var allowed = await sp.GetRequiredService<ModelPolicy>().AllowedAsync(me, ours, ct);
        return media.Pick(mode, null, allowed)
            ?? throw new Refused(mode == Llm.Core.Models.MediaModes.Video ? "There is no video model you may use (Admin → Models)." : "There is no picture model you may use (Admin → Models).");
    }

    private static async Task<IReadOnlyList<MediaItem>> MakeAsync(IServiceProvider sp, AppDbContext db, MediaJob job, AppUser me, Painted? painted, CancellationToken ct)
    {
        var video = job.Action == MediaJob.Move;
        var options = JsonNode.Parse(job.Options);
        var model = await ModelAsync(sp, me, video ? Llm.Core.Models.MediaModes.Video : Llm.Core.Models.MediaModes.Image, ct);
        var source = job.SourceId is { } sid ? await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == sid && i.OwnerId == me.Id, ct) : null;
        var file = source is null ? null : await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == source.AttachmentId, ct);
        if (job.Action != MediaJob.Make && file?.Data is null)
        {
            throw new Refused("The picture it works on is gone.");
        }
        var size = file?.Data is { } d ? Png.SizeOf(d) : null;
        if (job.Action == MediaJob.Enlarge && size is { } s && Math.Max(s.Width, s.Height) * 4 > MostEnlarged)
        {
            throw new Refused($"This picture is large already: enlarging makes it four times as large, at most {MostEnlarged:N0} pixels a side.");
        }

        // What the person may: credit, room, today's pictures, and the safeguards on their words.
        if (await sp.GetRequiredService<Gateway.Credit>().RefusalAsync(me, video ? Llm.Core.Access.CreditKind.Video : Llm.Core.Access.CreditKind.Pictures, ct) is { } spent)
        {
            throw new Refused(spent);
        }
        if (await sp.GetRequiredService<Storage.StorageQuotas>().RefusalAsync(me.Id, 0, ct) is { } full)
        {
            throw new Refused(full);
        }
        var safeguards = sp.GetRequiredService<Safeguards.Safeguards>();
        var count = job.Action is MediaJob.Make or MediaJob.Vary ? Math.Clamp(job.Count, 1, 4) : 1;
        for (var i = 0; i < count; i++)
        {
            if (await safeguards.TakeImageAsync(me.Id, ct) is { } limit)
            {
                count = i == 0 ? throw new Refused(limit) : i;
                break;
            }
        }
        var prompt = job.Prompt ?? source?.Prompt ?? "";
        if (job.Prompt is { Length: > 0 } words)
        {
            var verdict = await safeguards.CheckMessageAsync(me, words, 0, false, model.Name, ct);
            if (!verdict.Allowed)
            {
                throw new Refused(verdict.Reason ?? "A rule here stopped these words.");
            }
            prompt = verdict.Text ?? words;
        }

        // Its turn on the GPU (one picture or video at a time, room made for it when short), then the model loaded.
        await PhaseAsync(db, job, "Waiting for its turn on the GPU");
        var gpu = sp.GetRequiredService<GpuShare>();
        var media = sp.GetRequiredService<MediaControl>();
        var server = sp.GetRequiredService<SdServer>();
        await using var hold = await gpu.HoldAsync(model, ct);
        await PhaseAsync(db, job, $"Loading {model.Name}");
        if (await media.ReadyAsync(model.Name, ct) is { } notReady)
        {
            throw new Refused(notReady);
        }
        var began = DateTimeOffset.UtcNow;
        var (w, h) = Fit(Number(options, "width") ?? size?.Width ?? 1024, Number(options, "height") ?? size?.Height ?? 1024);
        var results = new List<(byte[] Data, string Type, int? Width, int? Height, double? Seconds)>();
        switch (job.Action)
        {
            case MediaJob.Make:
                await PhaseAsync(db, job, count == 1 ? "Drawing" : $"Drawing {count} pictures");
                results.AddRange((await server.PicturesAsync(model, prompt, w, h, count, ct)).Select(p => (p, "image/png", (int?)w, (int?)h, (double?)null)));
                break;
            case MediaJob.Vary:
                await PhaseAsync(db, job, count == 1 ? "Drawing a variation" : $"Drawing {count} variations");
                results.AddRange((await server.PicturesAsync(model, prompt, w, h, count, ct, start: SdServer.DataUrl(file!.Data!, file.ContentType),
                    strength: Math.Clamp(Number(options, "strength") ?? 0.55, 0.2, 0.9))).Select(p => (p, "image/png", (int?)w, (int?)h, (double?)null)));
                break;
            case MediaJob.Edit:
                await PhaseAsync(db, job, "Changing it");
                if (painted is not null)
                {
                    // Changed whole, by reference (what this model does well), then only the painted part kept, its edge soft.
                    var original = Png.Decode(painted.Picture);
                    var mask = Png.Decode(painted.Mask);
                    var changed = (await server.PicturesAsync(model, prompt, original.Width, original.Height, 1, ct, references: [SdServer.DataUrl(painted.Picture)]))[0];
                    var blended = Png.Blend(original, Png.Decode(changed), mask);
                    results.Add((Png.Encode(blended), "image/png", blended.Width, blended.Height, null));
                }
                else
                {
                    results.AddRange((await server.PicturesAsync(model, prompt, w, h, 1, ct, references: [SdServer.DataUrl(file!.Data!, file.ContentType)]))
                        .Select(p => (p, "image/png", (int?)w, (int?)h, (double?)null)));
                }
                break;
            case MediaJob.Enlarge:
                await PhaseAsync(db, job, "Enlarging");
                var (big, bw, bh) = await server.EnlargeAsync(await ModelAsync(sp, me, Llm.Core.Models.MediaModes.Image, ct), file!.Data!, ct);
                results.Add((big, "image/png", bw, bh, null));
                break;
            case MediaJob.Move:
                var seconds = (int)Math.Clamp(Number(options, "seconds") ?? 3, 1, Math.Max(1, model.Row.MaxSeconds ?? 5));
                await PhaseAsync(db, job, $"Making a {seconds}-second clip (a few minutes)");
                var (clip, type) = await server.VideoAsync(model, prompt.Length > 0 ? prompt : "gentle natural motion, the camera still", seconds, ct,
                    start: SdServer.DataUrl(file!.Data!, file.ContentType), size: SdServer.VideoSize(model, size?.Width ?? w, size?.Height ?? h));
                results.Add((clip, type, null, null, seconds));
                break;
            default:
                throw new Refused("Unknown work.");
        }
        media.Touch(model.Name);
        await PhaseAsync(db, job, "Keeping it");

        var bytes = results.Sum(r => (long)r.Data.Length);
        if (await sp.GetRequiredService<Storage.StorageQuotas>().RefusalAsync(me.Id, bytes, ct) is { } noRoom)
        {
            throw new Refused(noRoom);
        }
        var items = new List<MediaItem>();
        var stem = Path.GetFileNameWithoutExtension(Chat.Tools.ImageTool.FileName(prompt.Length > 0 ? prompt : source?.Prompt ?? job.Action));
        foreach (var (data, type, rw, rh, secs) in results)
        {
            var attachment = new ChatAttachment
            {
                UserId = me.Id, FileName = stem + (type == "image/png" ? ".png" : type == "video/webm" ? ".webm" : ".avi"), ContentType = type, Size = data.Length,
                Kind = video ? "video" : "image", Data = data, Text = "", Seconds = secs, Origin = video ? "video" : "picture",
            };
            db.ChatAttachments.Add(attachment);
            var item = new MediaItem
            {
                OwnerId = me.Id, AttachmentId = attachment.Id, Kind = video ? MediaItem.Video : MediaItem.Picture, Action = job.Action,
                Prompt = prompt.Length > 0 ? prompt : null, ParentId = source?.Id, Width = rw, Height = rh, Seconds = secs, Model = model.Name,
                Thumb = video ? null : Thumb(data),
            };
            db.MediaItems.Add(item);
            items.Add(item);
        }
        await db.SaveChangesAsync(ct);

        // Booked as the chat's request (its key, the person), at the model's price.
        var prices = sp.GetRequiredService<Gateway.PriceBook>();
        var key = Gateway.AnswerCache.HashOf(await sp.GetRequiredService<ChatKey>().GetAsync(ct));
        var spendLog = sp.GetRequiredService<Gateway.SpendLog>();
        if (video)
        {
            var secs = results[0].Seconds ?? 0;
            await spendLog.BookVideoAsync(me.Email ?? "", key, model.Name, secs, (model.Row.UnitPrice ?? prices.Defaults.PerVideoSecond) * (decimal)secs, began, DateTimeOffset.UtcNow, ct);
        }
        else
        {
            await spendLog.BookPicturesAsync(me.Email ?? "", key, model.Name, results.Count, (model.Row.UnitPrice ?? prices.Defaults.PerImage) * results.Count, began, DateTimeOffset.UtcNow, ct);
        }
        return items;
    }

    /// <summary>A size the picture model takes: within 64 to 1024 pixels a side (its shape kept), each a multiple of 16.</summary>
    public static (int Width, int Height) Fit(double width, double height)
    {
        var scale = Math.Min(1.0, 1024 / Math.Max(width, height));
        int Side(double v) => Math.Clamp((int)(v * scale) / 16 * 16, 64, 1024);
        return (Side(width), Side(height));
    }

    /// <summary>A PNG's thumbnail as a JPEG; null for a file that is no PNG this reads.</summary>
    public static byte[]? Thumb(byte[] picture)
    {
        try
        {
            return Jpeg.Encode(Jpeg.Smaller(Png.Decode(picture), ThumbSide), 82);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media job {Job} failed")]
    private static partial void JobFailed(ILogger logger, Guid job, Exception ex);
}
