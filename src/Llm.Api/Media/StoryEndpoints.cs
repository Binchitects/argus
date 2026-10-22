using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Media;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Media;

public sealed record StoryRequest(string? Brief, int? Shots);

public sealed record StoryShotChange(string? Visual, string? Narration, int? Seconds);

/// <param name="VoiceId">The narrator: a sound file of the person's whose voice it is (with <paramref name="Consent"/>); null: their own speech settings.</param>
/// <param name="Music">The music under it, in words; empty: none.</param>
public sealed record StoryChange(string? Title, IReadOnlyList<StoryShotChange>? Shots, Guid? VoiceId, bool Consent, string? Music);

/// <summary>
/// The media studio's videos: a storyboard written by the model from the person's words (shots, each what is seen and what
/// the narrator says), changed by them, then made (pictures set moving, the narration, music, captions) and cut into one MP4.
/// </summary>
public static class StoryEndpoints
{
    public const int MostStories = 100;
    public const int MostBrief = 8000;

    public static void MapStories(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/media/stories").PersonOnly().RequirePermission(Core.Access.Permissions.Video);
        g.MapGet("", ListAsync);
        g.MapPost("", WriteAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPut("/{id:guid}", ChangeAsync);
        g.MapPost("/{id:guid}/make", MakeAsync);
        g.MapPost("/{id:guid}/shots/{index:int}/again", AgainAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static string? Url(Guid? file) => file is { } f ? $"/api/chat/attachments/{f}/content" : null;

    private static object View(MediaStory s, DateTimeOffset now, Guid? videoFile = null)
    {
        var lost = s.Status == MediaStory.Making && now - s.UpdatedAt > StoryRuns.Lost;
        return new
        {
            s.Id, s.Title, s.Brief, s.VoiceId, s.Music, s.VideoItemId, s.CreatedAt, s.UpdatedAt, video = Url(videoFile),
            status = lost ? MediaStory.Failed : s.Status,
            phase = lost ? null : s.Phase,
            error = lost ? "It stopped: the app was restarted meanwhile. Make it again: what was made is kept." : s.Error,
            shots = StoryModel.Read(s.Shots).Select(x => new
            {
                x.Visual, x.Narration, x.Seconds, picture = Url(x.Picture), clip = Url(x.Clip), speech = Url(x.Speech),
            }),
        };
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var stories = await db.MediaStories.AsNoTracking().Where(s => s.OwnerId == me.Id).OrderByDescending(s => s.UpdatedAt).Take(MostStories).ToListAsync(ct);
        var videos = await VideosAsync(db, stories, ct);
        var now = clock.GetUtcNow();
        return Results.Ok(stories.Select(s => View(s, now, videos.GetValueOrDefault(s.Id))));
    }

    /// <summary>Each story's video file, once cut.</summary>
    private static async Task<Dictionary<Guid, Guid>> VideosAsync(AppDbContext db, IReadOnlyList<MediaStory> stories, CancellationToken ct)
    {
        var items = stories.Select(s => s.VideoItemId).OfType<Guid>().ToList();
        var files = await db.MediaItems.AsNoTracking().Where(i => items.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.AttachmentId, ct);
        return stories.Where(s => s.VideoItemId is { } v && files.ContainsKey(v)).ToDictionary(s => s.Id, s => files[s.VideoItemId!.Value]);
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.MediaStories.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == me.Id, ct) is { } s
            ? Results.Ok(View(s, clock.GetUtcNow(), (await VideosAsync(db, [s], ct)).GetValueOrDefault(s.Id)))
            : Results.NotFound();
    }

    /// <summary>A storyboard from the person's words, by their model: a draft to read and change before it is made.</summary>
    private static async Task<IResult> WriteAsync(StoryRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, OneQuestion one, TimeProvider clock,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        var brief = body.Brief?.Trim() ?? "";
        if (brief.Length is 0 or > MostBrief)
        {
            return AuthEndpoints.Problem(400, "brief", $"Say what the video is about, in at most {MostBrief:N0} characters.");
        }
        if (await db.MediaStories.CountAsync(s => s.OwnerId == me.Id, ct) >= MostStories)
        {
            return AuthEndpoints.Problem(409, "brief", $"You have {MostStories} videos in the studio: delete some first.");
        }
        var (written, problem) = await StoryModel.WriteAsync(one, me, brief, Math.Clamp(body.Shots ?? 5, StoryModel.LeastShots, StoryModel.MostShots), ct);
        if (problem is not null)
        {
            return problem;
        }
        var story = new MediaStory { OwnerId = me.Id, Title = written!.Title, Brief = brief, Music = written.Music, Shots = StoryModel.Write(written.Shots) };
        db.MediaStories.Add(story);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/media/stories/{story.Id}", View(story, clock.GetUtcNow()));
    }

    /// <summary>The person's own changes: what was made for a shot stays while what it came from holds.</summary>
    private static async Task<IResult> ChangeAsync(Guid id, StoryChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.MediaStories.SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == me.Id, ct) is not { } story)
        {
            return Results.NotFound();
        }
        if (story.Status == MediaStory.Making && clock.GetUtcNow() - story.UpdatedAt < StoryRuns.Lost)
        {
            return AuthEndpoints.Problem(409, "shots", "It is being made: change it when it is done.");
        }
        var changes = body.Shots ?? [];
        if (changes.Count is < StoryModel.LeastShots or > StoryModel.MostShots)
        {
            return AuthEndpoints.Problem(400, "shots", $"A video has {StoryModel.LeastShots} to {StoryModel.MostShots} shots.");
        }
        if (changes.Any(c => string.IsNullOrWhiteSpace(c.Visual) || c.Visual.Length > StoryShot.MostVisual || (c.Narration?.Length ?? 0) > StoryShot.MostNarration))
        {
            return AuthEndpoints.Problem(400, "shots", $"Each shot says what is seen (at most {StoryShot.MostVisual:N0} characters), and what is said over it in at most {StoryShot.MostNarration}.");
        }
        if (body.VoiceId is { } voice)
        {
            if (!body.Consent)
            {
                return AuthEndpoints.Problem(400, "consent", "Say that the voice is yours, or that its owner lets you use it.");
            }
            if (!await db.ChatAttachments.AnyAsync(a => a.Id == voice && a.UserId == me.Id && (a.Kind == "audio" || a.Kind == "video"), ct))
            {
                return AuthEndpoints.Problem(400, "voiceId", "Choose a recording or sound file of yours for the narrator.");
            }
        }
        var old = StoryModel.Read(story.Shots);
        var shots = changes.Select((c, i) =>
        {
            var (visual, narration, seconds) = (c.Visual!.Trim(), c.Narration?.Trim() ?? "", Math.Clamp(c.Seconds ?? 4, StoryShot.Least, StoryShot.Most));
            return i < old.Count ? old[i].Changed(visual, narration, seconds) : new StoryShot { Visual = visual, Narration = narration, Seconds = seconds };
        }).ToList();
        // Another narrator: every shot's narration is spoken again.
        if (body.VoiceId != story.VoiceId)
        {
            shots = [.. shots.Select(s => s with { Speech = null })];
        }
        story.Title = body.Title?.Trim() is { Length: > 0 and <= 200 } title ? title : story.Title;
        story.Shots = StoryModel.Write(shots);
        story.VoiceId = body.VoiceId;
        story.Music = body.Music?.Trim() is { Length: > 0 } music ? music[..Math.Min(music.Length, 1000)] : null;
        story.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(story, clock.GetUtcNow(), (await VideosAsync(db, [story], ct)).GetValueOrDefault(story.Id)));
    }

    private static async Task<IResult> StartAsync(MediaStory story, AppDbContext db, StoryRuns runs, Operations.Modules modules, TimeProvider clock, CancellationToken ct)
    {
        if (story.Status == MediaStory.Making && clock.GetUtcNow() - story.UpdatedAt < StoryRuns.Lost)
        {
            return AuthEndpoints.Problem(409, "story", "It is being made already.");
        }
        // One video of a person's in the making at a time: each takes the GPU for minutes a shot.
        var since = clock.GetUtcNow() - StoryRuns.Lost;
        if (await db.MediaStories.AnyAsync(s => s.OwnerId == story.OwnerId && s.Id != story.Id && s.Status == MediaStory.Making && s.UpdatedAt > since, ct))
        {
            return AuthEndpoints.Problem(429, "story", "Another video of yours is being made: make this one when it is done.");
        }
        if (!await modules.HasAsync("videogen", ct) || !await modules.HasAsync("imagegen", ct))
        {
            return AuthEndpoints.Problem(503, "story", "A video needs the picture and video servers (the imagegen and videogen modules).");
        }
        (story.Status, story.Phase, story.Error, story.UpdatedAt) = (MediaStory.Making, "Starting", null, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        runs.Go(story.Id);
        return Results.Accepted($"/api/media/stories/{story.Id}", View(story, clock.GetUtcNow()));
    }

    private static async Task<IResult> MakeAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StoryRuns runs, Operations.Modules modules,
        TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.MediaStories.SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == me.Id, ct) is { } story
            ? await StartAsync(story, db, runs, modules, clock, ct)
            : Results.NotFound();
    }

    /// <summary>One shot drawn and set moving again (its narration kept), and the video cut again.</summary>
    private static async Task<IResult> AgainAsync(Guid id, int index, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StoryRuns runs, Operations.Modules modules,
        TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.MediaStories.SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == me.Id, ct) is not { } story)
        {
            return Results.NotFound();
        }
        var shots = StoryModel.Read(story.Shots);
        if (index < 0 || index >= shots.Count)
        {
            return Results.NotFound();
        }
        if (story.Status == MediaStory.Making && clock.GetUtcNow() - story.UpdatedAt < StoryRuns.Lost)
        {
            return AuthEndpoints.Problem(409, "story", "It is being made already.");
        }
        shots[index] = shots[index] with { Picture = null, Clip = null };
        story.Shots = StoryModel.Write(shots);
        return await StartAsync(story, db, runs, modules, clock, ct);
    }

    /// <summary>The story and what was made for it go; the video it was cut into stays in the gallery.</summary>
    private static async Task<IResult> RemoveAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.MediaStories.SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == me.Id, ct) is not { } story)
        {
            return Results.NotFound();
        }
        var files = StoryModel.Read(story.Shots).SelectMany(s => new[] { s.Picture, s.Clip, s.Speech }).Append(story.MusicFileId).OfType<Guid>().ToList();
        db.MediaStories.Remove(story);
        await db.SaveChangesAsync(ct);
        if (files.Count > 0)
        {
            await db.ChatAttachments.Where(a => files.Contains(a.Id) && a.UserId == me.Id).ExecuteDeleteAsync(ct);
        }
        return Results.NoContent();
    }
}
