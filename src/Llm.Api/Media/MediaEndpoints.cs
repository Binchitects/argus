using System.Security.Claims;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Media;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Media;

/// <param name="Picture">A painted edit: the picture as the page drew it, a PNG data URL (with <paramref name="Mask"/>, the same size: white where painted).</param>
/// <param name="VoiceId">Speak: a sound file of the person's (an upload or a recording) whose voice to speak in; with <paramref name="Consent"/>.</param>
/// <param name="Consent">Speak in a sample's voice: the person says the voice is theirs, or that they may use it.</param>
public sealed record MediaJobRequest(string? Action, string? Prompt, Guid? SourceId, int? Count, int? Width, int? Height, double? Strength, int? Seconds,
    string? Picture, string? Mask, string? Lyrics = null, Guid? VoiceId = null, bool Consent = false, double? Exaggeration = null);

public sealed record MediaUpload(Guid AttachmentId);

/// <summary>
/// The media studio: a person's pictures and clips, made from words, changed (all or the part painted over), made a little
/// different, enlarged or set moving; each made by a job in the background that the page follows.
/// </summary>
public static class MediaEndpoints
{
    public const int Page = 48;
    public const int MostPromptChars = 2000;
    public const int MostSpeechChars = 3000;
    public const int MostLyricsChars = 6000;
    /// <summary>A painted edit's picture or mask, at most.</summary>
    public const int MostPaintedBytes = 16 * 1024 * 1024;

    public static void MapMedia(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/media").PersonOnly().RequirePermission(Core.Access.Permissions.Pictures);
        g.MapGet("", ConfigAsync);
        g.MapGet("/items", ItemsAsync);
        g.MapPost("/items", AddAsync);
        g.MapGet("/items/{id:guid}/thumb", ThumbAsync);
        g.MapDelete("/items/{id:guid}", RemoveAsync);
        g.MapGet("/jobs", JobsAsync);
        g.MapPost("/jobs", StartAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>What the studio can do here, for this person: the picture and video models they may use, and whether pictures can be enlarged.</summary>
    private static async Task<IResult> ConfigAsync(ClaimsPrincipal p, UserManager<AppUser> users, MediaControl media, ModelPolicy policy, Operations.Modules modules,
        AppDbContext db, Access.AccessService access, StudioServer studio, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        async Task<string?> PickAsync(string mode, string module)
        {
            if (!await modules.HasAsync(module, ct))
            {
                return null;
            }
            var ours = media.All.Where(m => m.Mode == mode).Select(m => m.Name).ToList();
            return media.Pick(mode, null, await policy.AllowedAsync(me, ours, ct))?.Name;
        }
        var picture = await PickAsync(Core.Models.MediaModes.Image, "imagegen");
        var since = clock.GetUtcNow() - MediaJobs.Lost;
        var has = await modules.HasAsync("studio", ct) ? await studio.HealthAsync(ct) : null;
        var sound = await access.MayAsync(me, Core.Access.Permissions.Speech, ct);
        return Results.Ok(new
        {
            picture,
            cutout = has?.Cutout == true,
            voice = sound && has?.Voice == true,
            music = sound && has?.Music == true,
            video = await access.MayAsync(me, Core.Access.Permissions.Video, ct) ? await PickAsync(Core.Models.MediaModes.Video, "videogen") : null,
            enlarge = picture is not null && media.CanEnlarge,
            going = await db.MediaJobs.CountAsync(j => j.OwnerId == me.Id && j.Status == MediaJob.Working && j.CreatedAt > since, ct),
            mostGoing = MediaJobs.MostGoing,
        });
    }

    private static async Task<IResult> ItemsAsync(string? kind, DateTimeOffset? before, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var q = db.MediaItems.AsNoTracking().Where(i => i.OwnerId == me.Id);
        if (kind is MediaItem.Picture or MediaItem.Video or MediaItem.Sound)
        {
            q = q.Where(i => i.Kind == kind);
        }
        if (before is { } b)
        {
            q = q.Where(i => i.CreatedAt < b);
        }
        var rows = await q.OrderByDescending(i => i.CreatedAt).Take(Page + 1)
            .Join(db.ChatAttachments, i => i.AttachmentId, a => a.Id, (i, a) => new
            {
                i.Id, i.Kind, i.Action, i.Prompt, i.ParentId, i.Width, i.Height, i.Seconds, i.Model, i.CreatedAt, i.AttachmentId,
                a.FileName, a.ContentType, a.Size, Thumb = i.Thumb != null,
            })
            .OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        return Results.Ok(new { items = rows.Take(Page), more = rows.Count > Page });
    }

    /// <summary>A picture of the person's own (an upload) into the studio, to change it.</summary>
    private static async Task<IResult> AddAsync(MediaUpload body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var a = await db.ChatAttachments.AsNoTracking().Where(x => x.Id == body.AttachmentId && x.UserId == me.Id)
            .Select(x => new { x.Id, x.Kind, x.Data, x.FileName }).SingleOrDefaultAsync(ct);
        if (a is null)
        {
            return Results.NotFound();
        }
        if (a.Kind != "image" || a.Data is null)
        {
            return AuthEndpoints.Problem(400, "attachmentId", "Only a picture can go into the studio.");
        }
        if (await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.AttachmentId == a.Id && i.OwnerId == me.Id, ct) is { } there)
        {
            return Results.Ok(new { there.Id });
        }
        var size = Png.SizeOf(a.Data);
        var item = new MediaItem
        {
            OwnerId = me.Id, AttachmentId = a.Id, Kind = MediaItem.Picture, Action = "upload", Prompt = null, Width = size?.Width, Height = size?.Height,
            Thumb = MediaJobs.Thumb(a.Data),
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { item.Id });
    }

    private static async Task<IResult> ThumbAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var me = await Me(p, users);
        var row = await db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.OwnerId == me.Id).Select(i => new { i.Thumb, i.AttachmentId }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return Results.NotFound();
        }
        if (row.Thumb is null)
        {
            return Results.Redirect($"/api/chat/attachments/{row.AttachmentId}/content");
        }
        // Never changes: kept by the browser.
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.File(row.Thumb, "image/jpeg");
    }

    /// <summary>Takes an item out of the studio; a file the studio made goes with it (an upload stays the person's).</summary>
    private static async Task<IResult> RemoveAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.MediaItems.SingleOrDefaultAsync(i => i.Id == id && i.OwnerId == me.Id, ct) is not { } item)
        {
            return Results.NotFound();
        }
        if (item.Action == "upload")
        {
            db.MediaItems.Remove(item);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // The item goes with its file (the database's cascade); those made from it keep going, without where they came from.
            await db.ChatAttachments.Where(a => a.Id == item.AttachmentId && a.UserId == me.Id).ExecuteDeleteAsync(ct);
        }
        return Results.NoContent();
    }

    private static object View(MediaJob j, DateTimeOffset now)
    {
        var lost = j.Status == MediaJob.Working && now - j.CreatedAt > MediaJobs.Lost;
        return new
        {
            j.Id, j.Action, j.Prompt, j.SourceId, j.Count, j.CreatedAt, j.FinishedAt,
            status = lost ? MediaJob.Failed : j.Status,
            phase = lost ? null : j.Phase,
            error = lost ? "It stopped: the app was restarted meanwhile. Try again." : j.Error,
            items = JsonSerializer.Deserialize<List<Guid>>(j.Items) ?? [],
        };
    }

    /// <summary>The person's work going now, and what finished in the last quarter of an hour (the page shows how it went).</summary>
    private static async Task<IResult> JobsAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var now = clock.GetUtcNow();
        var recent = now.AddMinutes(-15);
        var jobs = await db.MediaJobs.AsNoTracking().Where(j => j.OwnerId == me.Id && (j.Status == MediaJob.Working || j.FinishedAt > recent))
            .OrderByDescending(j => j.CreatedAt).Take(20).ToListAsync(ct);
        return Results.Ok(jobs.Select(j => View(j, now)));
    }

    private static byte[]? FromDataUrl(string? url)
    {
        if (url is null)
        {
            return null;
        }
        var comma = url.IndexOf(',');
        if (!url.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || comma < 0 || url.Length - comma > MostPaintedBytes / 3 * 4)
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(url[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task<IResult> StartAsync(MediaJobRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, MediaJobs jobs,
        Operations.Modules modules, Access.AccessService access, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (body.Action is not { } action || !MediaJob.Actions.Contains(action))
        {
            return AuthEndpoints.Problem(400, "action", $"Choose what to do: {string.Join(", ", MediaJob.Actions)}.");
        }
        if (action == MediaJob.Move && !await access.MayAsync(me, Core.Access.Permissions.Video, ct))
        {
            return AuthEndpoints.Problem(403, "action", "You may not make videos here (an admin gives it under Access).");
        }
        var prompt = body.Prompt?.Trim();
        if (prompt is { Length: > MostPromptChars })
        {
            return AuthEndpoints.Problem(400, "prompt", $"Say it in at most {MostPromptChars:N0} characters.");
        }
        if (action is MediaJob.Make or MediaJob.Edit or MediaJob.Speak or MediaJob.Music && string.IsNullOrEmpty(prompt))
        {
            return AuthEndpoints.Problem(400, "prompt", action switch
            {
                MediaJob.Make => "Describe the picture.", MediaJob.Edit => "Say what to change.", MediaJob.Speak => "Write what to say.", _ => "Describe the music.",
            });
        }
        if (action == MediaJob.Speak && prompt!.Length > MostSpeechChars)
        {
            return AuthEndpoints.Problem(400, "prompt", $"Say at most {MostSpeechChars:N0} characters at once.");
        }
        if (body.Lyrics is { Length: > MostLyricsChars })
        {
            return AuthEndpoints.Problem(400, "lyrics", $"Lyrics of at most {MostLyricsChars:N0} characters.");
        }
        if (action is MediaJob.Speak or MediaJob.Music && !await access.MayAsync(me, Core.Access.Permissions.Speech, ct))
        {
            return AuthEndpoints.Problem(403, "action", "You may not make speech or music here (an admin gives it under Access).");
        }
        if (action == MediaJob.Speak && body.VoiceId is { } voice)
        {
            if (!body.Consent)
            {
                return AuthEndpoints.Problem(400, "consent", "Say that the voice is yours, or that its owner lets you use it.");
            }
            if (!await db.ChatAttachments.AnyAsync(a => a.Id == voice && a.UserId == me.Id && (a.Kind == "audio" || a.Kind == "video"), ct))
            {
                return AuthEndpoints.Problem(400, "voiceId", "Choose a recording or sound file of yours for the voice.");
            }
        }
        var module = MediaJob.OnStudio(action) ? "studio" : action == MediaJob.Move ? "videogen" : "imagegen";
        if (!await modules.HasAsync(module, ct))
        {
            return AuthEndpoints.Problem(503, "action", module switch
            {
                "studio" => "The studio server does not run here (the studio module).",
                "videogen" => "The video server does not run here (the videogen module).",
                _ => "The picture server does not run here (the imagegen module).",
            });
        }
        MediaItem? source = null;
        if (action is not (MediaJob.Make or MediaJob.Speak or MediaJob.Music))
        {
            source = body.SourceId is { } sid ? await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == sid && i.OwnerId == me.Id, ct) : null;
            if (source is null)
            {
                return AuthEndpoints.Problem(400, "sourceId", "Choose the picture to work on.");
            }
            if (source.Kind != MediaItem.Picture)
            {
                return AuthEndpoints.Problem(400, "sourceId", "Only a picture can be worked on this way.");
            }
        }
        Painted? painted = null;
        if (body.Picture is not null || body.Mask is not null)
        {
            if (action != MediaJob.Edit || FromDataUrl(body.Picture) is not { } picture || FromDataUrl(body.Mask) is not { } mask)
            {
                return AuthEndpoints.Problem(400, "mask", "A painted change sends the picture and where it was painted, both as PNG.");
            }
            try
            {
                var (a, b) = (Png.Decode(picture), Png.Decode(mask));
                if (a.Width != b.Width || a.Height != b.Height || a.Width % 16 != 0 || a.Height % 16 != 0 || Math.Max(a.Width, a.Height) > 1024)
                {
                    return AuthEndpoints.Problem(400, "mask", "The picture and where it was painted must be the same size, at most 1024 pixels a side, each a multiple of 16.");
                }
            }
            catch (InvalidDataException ex)
            {
                return AuthEndpoints.Problem(400, "mask", $"The painted picture could not be read: {ex.Message}.");
            }
            painted = new Painted(picture, mask);
        }
        var since = clock.GetUtcNow() - MediaJobs.Lost;
        if (await db.MediaJobs.CountAsync(j => j.OwnerId == me.Id && j.Status == MediaJob.Working && j.CreatedAt > since, ct) >= MediaJobs.MostGoing)
        {
            return AuthEndpoints.Problem(429, "action", $"You have {MediaJobs.MostGoing} pieces of work going: wait for one to finish.");
        }
        var options = new Dictionary<string, object?>();
        if (body.Width is { } w && body.Height is { } h)
        {
            options["width"] = Math.Clamp(w, 64, 4096);
            options["height"] = Math.Clamp(h, 64, 4096);
        }
        if (body.Strength is { } s)
        {
            options["strength"] = Math.Clamp(s, 0.2, 0.9);
        }
        if (body.Seconds is { } secs)
        {
            options["seconds"] = action == MediaJob.Music ? Math.Clamp(secs, 5, 240) : Math.Clamp(secs, 1, 10);
        }
        if (action == MediaJob.Music && body.Lyrics?.Trim() is { Length: > 0 } lyrics)
        {
            options["lyrics"] = lyrics;
        }
        if (action == MediaJob.Speak)
        {
            options["exaggeration"] = Math.Clamp(body.Exaggeration ?? 0.5, 0.25, 1.5);
            if (body.VoiceId is { } v)
            {
                options["voice"] = v.ToString();
            }
        }
        var job = new MediaJob
        {
            OwnerId = me.Id, Action = action, Prompt = string.IsNullOrEmpty(prompt) ? null : prompt, SourceId = source?.Id, Count = Math.Clamp(body.Count ?? 1, 1, 4),
            Options = JsonSerializer.Serialize(options), Phase = "Starting",
        };
        db.MediaJobs.Add(job);
        await db.SaveChangesAsync(ct);
        jobs.Go(job.Id, painted);
        return Results.Accepted($"/api/media/jobs", View(job, clock.GetUtcNow()));
    }
}
