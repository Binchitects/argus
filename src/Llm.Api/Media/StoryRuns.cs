using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Media;

/// <summary>
/// Stories made, in the background: each shot without its picture drawn (all the pictures first, then all the clips: each
/// model loaded once), each set moving from its picture, the narration spoken (the person's voice settings, or a voice from
/// their sample), the music made, then all cut into one MP4 in the sandbox (captions from the narration). What was made is
/// kept as it comes, so a story stopped half way goes on from there; a shot changed is made again alone.
/// </summary>
public sealed partial class StoryRuns(IServiceScopeFactory scopes, TimeProvider clock, ILogger<StoryRuns> logger)
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public Task Finished(Guid story) => _running.TryGetValue(story, out var t) ? t : Task.CompletedTask;

    /// <summary>A story still making after this was lost (its app stopped): shown as failed.</summary>
    public static readonly TimeSpan Lost = TimeSpan.FromHours(2);

    public void Go(Guid story)
    {
        _running[story] = Task.Run(async () =>
        {
            try
            {
                await RunAsync(story, CancellationToken.None);
            }
            catch (Exception ex)
            {
                StoryFailed(logger, story, ex);
                await FailAsync(story, "It stopped unexpectedly. Make it again: what was made is kept.");
            }
            finally
            {
                _running.TryRemove(story, out _);
            }
        });
    }

    private sealed class Refused(string message) : Exception(message);

    private async Task FailAsync(Guid id, string why)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.MediaStories.SingleOrDefaultAsync(s => s.Id == id && s.Status == MediaStory.Making) is { } story)
        {
            (story.Status, story.Phase, story.Error, story.UpdatedAt) = (MediaStory.Failed, null, why, clock.GetUtcNow());
            await db.SaveChangesAsync();
        }
    }

    private async Task RunAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var story = await db.MediaStories.SingleAsync(s => s.Id == id, ct);
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == story.OwnerId, ct);
        var started = clock.GetUtcNow();
        try
        {
            await MakeAsync(sp, db, story, me, ct);
            (story.Status, story.Phase, story.Error) = (MediaStory.Done, null, null);
        }
        catch (Exception ex) when (ex is Refused or SdServer.SdException or HttpRequestException or JsonException or InvalidDataException or SandboxException
            or ChatGatewayException or TaskCanceledException)
        {
            (story.Status, story.Phase) = (MediaStory.Failed, null);
            var why = ex is Refused ? ex.Message : $"The video could not be made: {ex.Message}";
            story.Error = why.Length > 2000 ? why[..2000] : why;
        }
        story.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
        if (clock.GetUtcNow() - started > TimeSpan.FromSeconds(45))
        {
            await sp.GetRequiredService<Notifications.Notifier>().SendAsync(story.OwnerId, story.Status == MediaStory.Done
                ? new Notifications.News("media", $"Your video is ready: {story.Title}", null, "/media")
                : new Notifications.News("media", $"Your video could not be made: {story.Title}", story.Error, "/media"), CancellationToken.None);
        }
    }

    private static async Task PhaseAsync(AppDbContext db, MediaStory story, string phase, List<StoryShot>? shots = null)
    {
        story.Phase = phase;
        if (shots is not null)
        {
            story.Shots = StoryModel.Write(shots);
        }
        await db.SaveChangesAsync();
    }

    private static async Task<MediaControl.Model> ModelAsync(IServiceProvider sp, AppUser me, string mode, CancellationToken ct)
    {
        var media = sp.GetRequiredService<MediaControl>();
        var ours = media.All.Where(m => m.Mode == mode).Select(m => m.Name).ToList();
        return media.Pick(mode, null, await sp.GetRequiredService<ModelPolicy>().AllowedAsync(me, ours, ct))
            ?? throw new Refused(mode == Llm.Core.Models.MediaModes.Video ? "There is no video model you may use (Admin → Models)." : "There is no picture model you may use (Admin → Models).");
    }

    private static async Task<Guid> KeepAsync(AppDbContext db, AppUser me, string name, string type, string kind, string origin, byte[] data, double? seconds, CancellationToken ct)
    {
        var a = new ChatAttachment { UserId = me.Id, FileName = name, ContentType = type, Size = data.Length, Kind = kind, Data = data, Text = "", Seconds = seconds, Origin = origin };
        db.ChatAttachments.Add(a);
        await db.SaveChangesAsync(ct);
        return a.Id;
    }

    private static async Task<byte[]> FileAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.ChatAttachments.AsNoTracking().Where(a => a.Id == id).Select(a => a.Data).SingleOrDefaultAsync(ct) ?? throw new Refused("A file made for the video is gone: make the video again.");

    private static async Task MakeAsync(IServiceProvider sp, AppDbContext db, MediaStory story, AppUser me, CancellationToken ct)
    {
        var shots = StoryModel.Read(story.Shots);
        if (shots.Count == 0)
        {
            throw new Refused("The story has no shots.");
        }
        var credit = sp.GetRequiredService<Gateway.Credit>();
        var quotas = sp.GetRequiredService<Storage.StorageQuotas>();
        var safeguards = sp.GetRequiredService<Safeguards.Safeguards>();
        var server = sp.GetRequiredService<SdServer>();
        var media = sp.GetRequiredService<MediaControl>();
        var gpu = sp.GetRequiredService<GpuShare>();
        var prices = sp.GetRequiredService<Gateway.PriceBook>();
        var spendLog = sp.GetRequiredService<Gateway.SpendLog>();
        var key = Gateway.AnswerCache.HashOf(await sp.GetRequiredService<ChatKey>().GetAsync(ct));
        if (await quotas.RefusalAsync(me.Id, 0, ct) is { } full)
        {
            throw new Refused(full);
        }
        // The person's words go through the safeguards' rules first (as the shots read now: they may have changed them).
        var words = string.Join("\n", shots.Select(s => $"{s.Visual}\n{s.Narration}"));
        var verdict = await safeguards.CheckMessageAsync(me, words, 0, false, null, ct);
        if (!verdict.Allowed)
        {
            throw new Refused(verdict.Reason ?? "A rule here stopped these words.");
        }

        // The pictures each shot starts from: the picture model loaded once for all of them.
        var wanting = shots.Select((s, i) => (s, i)).Where(x => x.s.Clip is null && x.s.Picture is null).ToList();
        if (wanting.Count > 0)
        {
            if (await credit.RefusalAsync(me, Llm.Core.Access.CreditKind.Pictures, ct) is { } spent)
            {
                throw new Refused(spent);
            }
            var picture = await ModelAsync(sp, me, Llm.Core.Models.MediaModes.Image, ct);
            await PhaseAsync(db, story, "Waiting for its turn on the GPU");
            await using var hold = await gpu.HoldAsync(picture, ct);
            await PhaseAsync(db, story, $"Loading {picture.Name}");
            if (await media.ReadyAsync(picture.Name, ct) is { } notReady)
            {
                throw new Refused(notReady);
            }
            foreach (var (shot, i) in wanting)
            {
                if (await safeguards.TakeImageAsync(me.Id, ct) is { } limit)
                {
                    throw new Refused(limit);
                }
                await PhaseAsync(db, story, $"Drawing shot {i + 1} of {shots.Count}");
                var began = DateTimeOffset.UtcNow;
                var png = (await server.PicturesAsync(picture, shot.Visual, 1024, 576, 1, ct))[0];
                media.Touch(picture.Name);
                shots[i] = shot with { Picture = await KeepAsync(db, me, $"shot-{i + 1}.png", "image/png", "image", "picture", png, null, ct) };
                await PhaseAsync(db, story, $"Drew shot {i + 1}", shots);
                await spendLog.BookPicturesAsync(me.Email ?? "", key, picture.Name, 1, picture.Row.UnitPrice ?? prices.Defaults.PerImage, began, DateTimeOffset.UtcNow, ct);
            }
        }

        // Each set moving from its picture: the video model loaded once for all of them.
        var still = shots.Select((s, i) => (s, i)).Where(x => x.s.Clip is null).ToList();
        if (still.Count > 0)
        {
            if (await credit.RefusalAsync(me, Llm.Core.Access.CreditKind.Video, ct) is { } spent)
            {
                throw new Refused(spent);
            }
            var video = await ModelAsync(sp, me, Llm.Core.Models.MediaModes.Video, ct);
            await PhaseAsync(db, story, "Waiting for its turn on the GPU");
            await using var hold = await gpu.HoldAsync(video, ct);
            await PhaseAsync(db, story, $"Loading {video.Name}");
            if (await media.ReadyAsync(video.Name, ct) is { } notReady)
            {
                throw new Refused(notReady);
            }
            var size = SdServer.VideoSize(video, 1024, 576);
            foreach (var (shot, i) in still)
            {
                var seconds = Math.Clamp(shot.Seconds, 1, Math.Max(1, video.Row.MaxSeconds ?? 5));
                await PhaseAsync(db, story, $"Setting shot {i + 1} of {shots.Count} moving (a few minutes)");
                var began = DateTimeOffset.UtcNow;
                var start = SdServer.DataUrl(await FileAsync(db, shot.Picture!.Value, ct));
                var (clip, type) = await server.VideoAsync(video, shot.Visual, seconds, ct, start: start, size: size);
                media.Touch(video.Name);
                shots[i] = shots[i] with { Clip = await KeepAsync(db, me, $"shot-{i + 1}{(type == "video/webm" ? ".webm" : ".avi")}", type, "video", "video", clip, seconds, ct) };
                await PhaseAsync(db, story, $"Shot {i + 1} moves", shots);
                await spendLog.BookVideoAsync(me.Email ?? "", key, video.Name, seconds, (video.Row.UnitPrice ?? prices.Defaults.PerVideoSecond) * seconds, began, DateTimeOffset.UtcNow, ct);
            }
        }

        // The narration: in a voice from the person's sample (the studio server), else their speech settings (the gateway).
        var unsaid = shots.Select((s, i) => (s, i)).Where(x => x.s.Narration.Length > 0 && x.s.Speech is null).ToList();
        if (unsaid.Count > 0)
        {
            if (await credit.RefusalAsync(me, Llm.Core.Access.CreditKind.Speech, ct) is { } spent)
            {
                throw new Refused(spent);
            }
            if (story.VoiceId is { } voiceId)
            {
                var sample = await db.ChatAttachments.AsNoTracking().Where(a => a.Id == voiceId && a.UserId == me.Id).Select(a => a.Kind == "video" ? a.Sound : a.Data)
                    .SingleOrDefaultAsync(ct) ?? throw new Refused("The voice sample is gone: choose the narrator again.");
                var studio = sp.GetRequiredService<StudioServer>();
                var health = await studio.HealthAsync(ct) ?? throw new Refused("The studio server does not answer: choose your own voice settings for the narrator.");
                await PhaseAsync(db, story, "Waiting for its turn on the GPU");
                await using var hold = await gpu.HoldStudioAsync(StudioServer.VoiceModel, health.Loaded.Contains("voice") ? 0 : StudioServer.NeedMb("voice"),
                    async c => (await studio.HealthAsync(c))?.FreeMb, studio.UnloadAsync, ct);
                foreach (var (shot, i) in unsaid)
                {
                    await PhaseAsync(db, story, $"Narrating shot {i + 1} of {shots.Count}");
                    var began = DateTimeOffset.UtcNow;
                    var (mp3, said) = await studio.SpeakAsync(shot.Narration, sample, 0.5, ct);
                    shots[i] = shots[i] with { Speech = await KeepAsync(db, me, $"shot-{i + 1}.mp3", "audio/mpeg", "audio", "speech", mp3, said, ct) };
                    await PhaseAsync(db, story, $"Narrated shot {i + 1}", shots);
                    await spendLog.BookSoundAsync(Gateway.SpendLog.SpeechCall, me.Email ?? "", key, StudioServer.VoiceModel, shot.Narration.Length, prices.Speech(shot.Narration.Length),
                        began, DateTimeOffset.UtcNow, ct);
                }
            }
            else
            {
                var speech = await sp.GetRequiredService<VoiceCatalog>().ForAsync(me, ct);
                var gateway = sp.GetRequiredService<GatewayChat>();
                foreach (var (shot, i) in unsaid)
                {
                    if (speech.For(shot.Narration) is not { } voice)
                    {
                        throw new Refused("There is no text to speech model for the narration (the audio module).");
                    }
                    await PhaseAsync(db, story, $"Narrating shot {i + 1} of {shots.Count}");
                    var mp3 = await gateway.SpeakAsync(voice.Model, shot.Narration, voice.Name, me.Email ?? "", ct, speech.Speed);
                    shots[i] = shots[i] with { Speech = await KeepAsync(db, me, $"shot-{i + 1}.mp3", "audio/mpeg", "audio", "speech", mp3, null, ct) };
                    await PhaseAsync(db, story, $"Narrated shot {i + 1}", shots);
                }
            }
        }

        // The music under it, made for about the video's length (each shot as long as its clip or its narration).
        byte[]? music = null;
        if (story.Music is { Length: > 0 } tune)
        {
            var estimate = (int)Math.Ceiling(shots.Sum(s => Math.Max(s.Seconds, s.Narration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.6 + 0.5))) + 2;
            var madeFor = $"{tune}|{estimate}";
            if (story.MusicFileId is { } kept && story.MusicMadeFor == madeFor)
            {
                music = await FileAsync(db, kept, ct);
            }
            else
            {
                var studio = sp.GetRequiredService<StudioServer>();
                if (await studio.HealthAsync(ct) is not { Music: true } health)
                {
                    throw new Refused("The music model is not there (the studio module): take the music away, or ask an admin.");
                }
                if (await credit.RefusalAsync(me, Llm.Core.Access.CreditKind.Speech, ct) is { } spent)
                {
                    throw new Refused(spent);
                }
                await PhaseAsync(db, story, "Waiting for its turn on the GPU");
                await using var hold = await gpu.HoldStudioAsync(StudioServer.MusicModel, health.Loaded.Contains("music") ? 0 : StudioServer.NeedMb("music"),
                    async c => (await studio.HealthAsync(c))?.FreeMb, studio.UnloadAsync, ct);
                await PhaseAsync(db, story, $"Making {estimate} seconds of music");
                var began = DateTimeOffset.UtcNow;
                (music, var seconds) = await studio.MusicAsync(tune, "[instrumental]", estimate, ct);
                story.MusicFileId = await KeepAsync(db, me, "music.mp3", "audio/mpeg", "audio", "music", music, seconds, ct);
                story.MusicMadeFor = madeFor;
                await db.SaveChangesAsync(ct);
                await spendLog.BookSoundAsync(Gateway.SpendLog.MusicCall, me.Email ?? "", key, StudioServer.MusicModel, seconds, prices.Defaults.PerMusicMinute * (decimal)seconds / 60m,
                    began, DateTimeOffset.UtcNow, ct);
            }
        }

        // All cut into one video in the sandbox.
        await PhaseAsync(db, story, "Cutting the video");
        var sandbox = sp.GetRequiredService<SandboxClient>();
        if (sandbox.Unavailable() is { } why)
        {
            throw new Refused(why.Replace("run code", "cut the video", StringComparison.Ordinal));
        }
        var files = new List<SandboxFile>();
        var plan = new JsonArray();
        for (var i = 0; i < shots.Count; i++)
        {
            var s = shots[i];
            files.Add(new SandboxFile($"clip-{i + 1}.webm", await FileAsync(db, s.Clip!.Value, ct)));
            if (s.Speech is { } said)
            {
                files.Add(new SandboxFile($"speech-{i + 1}.mp3", await FileAsync(db, said, ct)));
            }
            plan.Add(new JsonObject
            {
                ["clip"] = $"clip-{i + 1}.webm", ["speech"] = s.Speech is null ? null : $"speech-{i + 1}.mp3", ["text"] = s.Narration, ["seconds"] = s.Seconds,
            });
        }
        if (music is not null)
        {
            files.Add(new SandboxFile("music.mp3", music));
        }
        var rtl = StoryModel.RightToLeft(string.Concat(shots.Select(s => s.Narration)));
        files.Add(new SandboxFile("story.json", Encoding.UTF8.GetBytes(new JsonObject
        {
            ["shots"] = plan, ["music"] = music is null ? null : "music.mp3", ["rtl"] = rtl,
        }.ToJsonString())));
        var run = await sandbox.RunAsync(CutScript, files, 280, ct);
        var report = run.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        var mp4 = run.Files.FirstOrDefault(f => f.Name == "story.mp4")?.Bytes;
        var info = report is null ? null : JsonNode.Parse(report);
        if (mp4 is null || info?["error"]?.GetValue<string>() is { } error)
        {
            throw new Refused($"The video could not be cut: {info?["error"]?.GetValue<string>() ?? run.Killed ?? run.Error ?? "the sandbox made no file"}");
        }
        var length = info?["seconds"]?.GetValue<double>();
        if (await quotas.RefusalAsync(me.Id, mp4.Length, ct) is { } noRoom)
        {
            throw new Refused(noRoom);
        }

        // The video in the gallery; the one cut before goes (the story keeps what it was cut from).
        var stem = Path.GetFileNameWithoutExtension(ImageTool.FileName(story.Title));
        var file = await KeepAsync(db, me, stem + ".mp4", "video/mp4", "video", "video", mp4, length, ct);
        var item = new MediaItem
        {
            OwnerId = me.Id, AttachmentId = file, Kind = MediaItem.Video, Action = "story", Prompt = story.Title, Width = 1280, Height = 720, Seconds = length,
            Model = (await ModelAsync(sp, me, Llm.Core.Models.MediaModes.Video, ct)).Name,
        };
        db.MediaItems.Add(item);
        var before = story.VideoItemId;
        story.VideoItemId = item.Id;
        story.Shots = StoryModel.Write(shots);
        await db.SaveChangesAsync(ct);
        if (before is { } old)
        {
            var oldFile = await db.MediaItems.Where(i => i.Id == old && i.OwnerId == me.Id).Select(i => (Guid?)i.AttachmentId).SingleOrDefaultAsync(ct);
            if (oldFile is { } f)
            {
                await db.ChatAttachments.Where(a => a.Id == f && a.UserId == me.Id).ExecuteDeleteAsync(ct);
            }
        }
    }

    /// <summary>The sandbox's script that cuts the video (Media/Scripts/cut-story.py).</summary>
    private static readonly string CutScript = Read("cut-story.py");

    private static string Read(string name)
    {
        using var stream = typeof(StoryRuns).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not in the app");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Story {Story} failed")]
    private static partial void StoryFailed(ILogger logger, Guid story, Exception ex);
}
