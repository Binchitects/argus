using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Meetings;
using Llm.Core.Plans;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Meetings;

/// <summary>Meetings' limits: the most a recording may be (60 MB: three hours recorded in the browser), and the most one person keeps.</summary>
public sealed class MeetingOptions
{
    public long MaxUploadBytes { get; set; } = 60 * 1024 * 1024;
    public int MostPerPerson { get; set; } = 500;
}

public sealed record MeetingChange(string? Title);
public sealed record SpeakerChange(string? Name, Guid? PersonId);
public sealed record ActionChange(string? Text, int? Speaker, string? Owner, string? Due, bool? Done, bool ClearSpeaker = false, bool ClearDue = false);
public sealed record MeetingQuestion(string? Question);

/// <summary>
/// Meetings (/api/meetings), each its owner's: a recording uploaded (or recorded in the browser) and made in the
/// background (<see cref="MeetingEngine"/>), read with its transcript and minutes, its speakers named (and told which
/// person in the app they are: their action items go to them, with a reminder on the day due), its action items changed
/// or made a plan, its sound played, asked about in a chat, made again, or deleted with its recording.
/// </summary>
public static class MeetingEndpoints
{
    public static void MapMeetings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/meetings").PersonOnly().RequirePermission(Core.Access.Permissions.Meetings);
        g.MapGet("", ListAsync);
        g.MapPost("", UploadAsync).DisableAntiforgery();
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPut("/{id:guid}", ChangeAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/again", AgainAsync);
        g.MapGet("/{id:guid}/audio", AudioAsync);
        g.MapPut("/{id:guid}/speakers/{speaker:int}", SpeakerAsync);
        g.MapPost("/{id:guid}/actions", AddActionAsync);
        g.MapPut("/{id:guid}/actions/{actionId:guid}", ActionAsync);
        g.MapDelete("/{id:guid}/actions/{actionId:guid}", RemoveActionAsync);
        g.MapPost("/{id:guid}/plan", PlanAsync);
        g.MapPost("/{id:guid}/ask", AskAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static Task<Meeting?> MineAsync(AppDbContext db, Guid id, AppUser me, CancellationToken ct) =>
        db.Meetings.Include(m => m.Actions).SingleOrDefaultAsync(m => m.Id == id && m.UserId == me.Id, ct);

    public static List<TranscriptLine> Lines(Meeting m) => JsonSerializer.Deserialize<List<TranscriptLine>>(m.Transcript, MeetingEngine.Json) ?? [];
    public static List<MeetingSpeaker> Speakers(Meeting m) => JsonSerializer.Deserialize<List<MeetingSpeaker>>(m.Speakers, MeetingEngine.Json) ?? [];
    private static List<string> Decisions(Meeting m) => JsonSerializer.Deserialize<List<string>>(m.Decisions, MeetingEngine.Json) ?? [];

    private static object Brief(Meeting m) => new
    {
        m.Id, m.Title, m.Status, m.Step, m.Error, m.Seconds, m.CreatedAt,
        actions = m.Actions.Count, open = m.Actions.Count(a => !a.Done),
    };

    /// <summary>Whose an action item is, in words: its speaker's name, or the name given.</summary>
    private static string? OwnerName(MeetingAction a, IReadOnlyList<MeetingSpeaker> speakers) =>
        a.Speaker is { } s ? speakers.FirstOrDefault(x => x.Speaker == s)?.Name ?? $"Speaker {s + 1}" : a.Owner;

    private static async Task<object> FullAsync(Meeting m, AppDbContext db, CancellationToken ct)
    {
        var speakers = Speakers(m);
        var ids = speakers.Select(s => s.PersonId).Concat(m.Actions.Select(a => a.PersonId)).OfType<Guid>().ToHashSet();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName.Length > 0 ? u.DisplayName : u.UserName!, ct);
        return new
        {
            meeting = Brief(m), m.Language, m.Summary, m.Model, m.PlanId,
            transcript = Lines(m),
            speakers = speakers.Select(s => new { s.Speaker, s.Name, s.PersonId, person = s.PersonId is { } id ? people.GetValueOrDefault(id) : null }),
            decisions = Decisions(m),
            actions = m.Actions.OrderBy(a => a.Number).Select(a => new
            {
                a.Id, a.Number, a.Text, a.Speaker, given = a.Owner, owner = OwnerName(a, speakers), a.PersonId,
                person = a.PersonId is { } id ? people.GetValueOrDefault(id) : null, due = a.Due?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), a.Done,
            }),
        };
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var mine = await db.Meetings.AsNoTracking().Include(m => m.Actions).Where(m => m.UserId == me.Id).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(ct);
        return Results.Ok(mine.Select(Brief));
    }

    /// <summary>A recording (a sound or a video's sound) and its title, if any: kept as its owner's file, and made into a meeting in the background.</summary>
    private static async Task<IResult> UploadAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<MeetingOptions> options,
        Storage.StorageQuotas quotas, Safeguards.Safeguards safeguards, MeetingEngine engine, AccessService access, Audit audit)
    {
        var ct = request.HttpContext.RequestAborted;
        var me = await Me(p, users);
        var o = options.CurrentValue;
        if (!await access.MayAsync(me, Core.Access.Permissions.Speech, ct))
        {
            return AuthEndpoints.Problem(403, "feature", "Meetings are heard by speech to text, which is not among what you may use: ask an admin.");
        }
        if (await db.Meetings.CountAsync(m => m.UserId == me.Id, ct) >= o.MostPerPerson)
        {
            return AuthEndpoints.Problem(409, "full", $"You keep {o.MostPerPerson} meetings: delete some first.");
        }
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = o.MaxUploadBytes + 1024 * 1024;
        }
        if (!request.HasFormContentType || await request.ReadFormAsync(ct) is not { Files.Count: 1 } form)
        {
            return AuthEndpoints.Problem(400, "file", "Send the recording: one sound or video file.");
        }
        var file = form.Files[0];
        if (file.Length > o.MaxUploadBytes)
        {
            return AuthEndpoints.Problem(413, "too_large", $"A recording is at most {o.MaxUploadBytes / 1024 / 1024} MB.");
        }
        if (await quotas.RefusalAsync(me.Id, file.Length, ct) is { } full)
        {
            return AuthEndpoints.Problem(413, "quota", full);
        }
        if (await safeguards.CheckFileAsync(me, Path.GetFileName(file.FileName), "", ct, file.Length) is { Allowed: false } refused)
        {
            return Safeguards.Safeguards.Problem(refused);
        }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        if (Chat.Media.Detect(file.FileName, file.ContentType, ms.ToArray()) is not { } kind)
        {
            return AuthEndpoints.Problem(400, "unreadable", $"{file.FileName} is not a sound or a video.");
        }
        // Its sound alone is kept (as an MP3, once made): a video's pictures are not the meeting's.
        var recording = new ChatAttachment
        {
            UserId = me.Id, FileName = Path.GetFileName(file.FileName), ContentType = kind.ContentType, Size = file.Length, Text = "", Kind = "audio", Data = ms.ToArray(),
        };
        db.ChatAttachments.Add(recording);
        var title = form["title"].ToString().Trim();
        var meeting = new Meeting { UserId = me.Id, Title = title.Length > 300 ? title[..300] : title, RecordingId = recording.Id, Step = "Waiting its turn" };
        db.Meetings.Add(meeting);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("meeting.add", meeting.Title.Length > 0 ? meeting.Title : recording.FileName, actor: me);
        engine.Start(meeting.Id);
        return Results.Created($"/api/meetings/{meeting.Id}", Brief(meeting));
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await MineAsync(db, id, me, ct) is { } m ? Results.Ok(await FullAsync(m, db, ct)) : Results.NotFound();
    }

    private static async Task<IResult> ChangeAsync(Guid id, MeetingChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m)
        {
            return Results.NotFound();
        }
        if (body.Title?.Trim() is { } title)
        {
            if (title.Length is 0 or > 300)
            {
                return AuthEndpoints.Problem(400, "title", "A title has 1 to 300 characters.");
            }
            m.Title = title;
        }
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await FullAsync(m, db, ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m)
        {
            return Results.NotFound();
        }
        await db.ChatAttachments.Where(a => a.Id == m.RecordingId && a.UserId == me.Id).ExecuteDeleteAsync(ct);
        db.Meetings.Remove(m);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("meeting.delete", m.Title, actor: me);
        return Results.NoContent();
    }

    private static async Task<IResult> AgainAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, MeetingEngine engine, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m)
        {
            return Results.NotFound();
        }
        if (m.Status == Meeting.Processing)
        {
            return AuthEndpoints.Problem(409, "processing", "It is being made now.");
        }
        (m.Status, m.Step, m.Error, m.PromptTokens, m.CompletionTokens) = (Meeting.Processing, "Waiting its turn", null, 0, 0);
        await db.SaveChangesAsync(ct);
        engine.Start(m.Id);
        return Results.Ok(Brief(m));
    }

    /// <summary>The recording's sound (an MP3), its own range at a time: the page plays it from any line of the transcript.</summary>
    private static async Task<IResult> AudioAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Meetings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id && m.UserId == me.Id, ct) is not { RecordingId: { } recording }
            || await db.ChatAttachments.AsNoTracking().Where(a => a.Id == recording && a.UserId == me.Id).Select(a => new { a.Data, a.ContentType }).SingleOrDefaultAsync(ct) is not { Data: { } data } a)
        {
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.File(data, a.ContentType == "audio/mpeg" ? "audio/mpeg" : "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>
    /// A speaker named, and told which person in the app they are (or none): their action items become that person's, who
    /// is told of them now, and reminded on the day each is due.
    /// </summary>
    private static async Task<IResult> SpeakerAsync(Guid id, int speaker, SpeakerChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        Notifications.Notifier notifier, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m)
        {
            return Results.NotFound();
        }
        var speakers = Speakers(m);
        var at = speakers.FindIndex(s => s.Speaker == speaker);
        if (at < 0)
        {
            return Results.NotFound();
        }
        var name = body.Name?.Trim() is { Length: > 0 } n ? n[..Math.Min(100, n.Length)] : speakers[at].Name;
        AppUser? person = null;
        if (body.PersonId is { } pid && (person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == pid && !u.IsDisabled, ct)) is null)
        {
            return AuthEndpoints.Problem(400, "person", "There is no such person.");
        }
        var before = speakers[at].PersonId;
        speakers[at] = new MeetingSpeaker(speaker, name, person?.Id);
        m.Speakers = JsonSerializer.Serialize(speakers, MeetingEngine.Json);
        var theirs = m.Actions.Where(a => a.Speaker == speaker).ToList();
        foreach (var a in theirs)
        {
            a.PersonId = person?.Id;
        }
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (person is not null && person.Id != before && theirs.Count(a => !a.Done) is > 0 and var open)
        {
            await notifier.SendAsync(person.Id, new Notifications.News(MeetingEngine.Tag, $"{open} action item{(open > 1 ? "s" : "")} from {m.Title}",
                string.Join("; ", theirs.Where(a => !a.Done).Take(5).Select(a => a.Due is { } d ? $"{a.Text} (by {d:yyyy-MM-dd})" : a.Text)),
                person.Id == me.Id ? $"/meetings/{m.Id}" : null), ct);
        }
        return Results.Ok(await FullAsync(m, db, ct));
    }

    private static async Task<IResult> AddActionAsync(Guid id, ActionChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m)
        {
            return Results.NotFound();
        }
        var action = new MeetingAction { MeetingId = m.Id, Number = m.Actions.Select(a => a.Number).DefaultIfEmpty().Max() + 1 };
        if (Change(action, body, Speakers(m)) is { } problem)
        {
            return AuthEndpoints.Problem(400, "action", problem);
        }
        if (action.Text.Length == 0)
        {
            return AuthEndpoints.Problem(400, "action", "Say what is to be done.");
        }
        m.Actions.Add(action);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await FullAsync(m, db, ct));
    }

    private static async Task<IResult> ActionAsync(Guid id, Guid actionId, ActionChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m || m.Actions.FirstOrDefault(a => a.Id == actionId) is not { } action)
        {
            return Results.NotFound();
        }
        if (Change(action, body, Speakers(m)) is { } problem)
        {
            return AuthEndpoints.Problem(400, "action", problem);
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(await FullAsync(m, db, ct));
    }

    /// <summary>An action item changed as asked (what is unsaid stays); its person follows its speaker; a new due date is reminded anew.</summary>
    private static string? Change(MeetingAction a, ActionChange body, IReadOnlyList<MeetingSpeaker> speakers)
    {
        if (body.Text?.Trim() is { } text)
        {
            if (text.Length is 0 or > 1000)
            {
                return "An action item says what is to be done, in up to 1,000 characters.";
            }
            a.Text = text;
        }
        if (body.ClearSpeaker)
        {
            (a.Speaker, a.PersonId) = (null, null);
        }
        if (body.Speaker is { } s)
        {
            if (speakers.FirstOrDefault(x => x.Speaker == s) is not { } speaker)
            {
                return "There is no such speaker.";
            }
            (a.Speaker, a.Owner, a.PersonId) = (s, null, speaker.PersonId);
        }
        if (body.Owner?.Trim() is { } owner)
        {
            (a.Owner, a.Speaker, a.PersonId) = (owner.Length > 0 ? owner[..Math.Min(200, owner.Length)] : null, null, null);
        }
        if (body.ClearDue)
        {
            (a.Due, a.RemindedAt) = (null, null);
        }
        if (body.Due?.Trim() is { Length: > 0 } due)
        {
            if (!DateOnly.TryParseExact(due, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
            {
                return "A due date is a day: YYYY-MM-DD.";
            }
            (a.Due, a.RemindedAt) = (day, a.Due == day ? a.RemindedAt : null);
        }
        if (body.Done is { } done)
        {
            a.Done = done;
        }
        return null;
    }

    private static async Task<IResult> RemoveActionAsync(Guid id, Guid actionId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m || m.Actions.FirstOrDefault(a => a.Id == actionId) is not { } action)
        {
            return Results.NotFound();
        }
        db.MeetingActions.Remove(action);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await FullAsync(m, db, ct));
    }

    /// <summary>The action items made a plan (Plans): a step each, with whose it is and when it is due; again, the plan made anew.</summary>
    private static async Task<IResult> PlanAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } m)
        {
            return Results.NotFound();
        }
        if (m.Actions.Count == 0)
        {
            return AuthEndpoints.Problem(400, "actions", "This meeting has no action items to plan.");
        }
        if (m.PlanId is { } old)
        {
            await db.Plans.Where(x => x.Id == old && x.UserId == me.Id).ExecuteDeleteAsync(ct);
        }
        var speakers = Speakers(m);
        var plan = new Plan
        {
            UserId = me.Id, Title = $"Actions: {m.Title}"[..Math.Min(200, $"Actions: {m.Title}".Length)],
            Goal = $"What was agreed at {m.Title} ({m.CreatedAt:yyyy-MM-dd}).",
        };
        plan.Steps.AddRange(m.Actions.OrderBy(a => a.Number).Select((a, i) => new PlanStep
        {
            PlanId = plan.Id, Number = i + 1, Title = a.Text.Length > 200 ? a.Text[..200] : a.Text, Notes = a.Text.Length > 200 ? a.Text : null,
            Owner = OwnerName(a, speakers), Due = a.Due, Status = a.Done ? PlanStep.Done : PlanStep.Todo,
        }));
        db.Plans.Add(plan);
        m.PlanId = plan.Id;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = plan.Id });
    }

    /// <summary>
    /// A chat about the meeting: its minutes and transcript (with who said what, and when) as a file of its first question,
    /// read as any document is; the page sends the question in it.
    /// </summary>
    private static async Task<IResult> AskAsync(Guid id, MeetingQuestion body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<ChatOptions> chat,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { Status: Meeting.Ready } m)
        {
            return Results.NotFound();
        }
        var speakers = Speakers(m);
        var names = speakers.ToDictionary(s => s.Speaker, s => s.Name);
        var text = new StringBuilder($"# {m.Title}\n\nRecorded {m.CreatedAt:yyyy-MM-dd}, {MeetingEngine.Clock(m.Seconds ?? 0)} long.\n\n## Summary\n\n{m.Summary}\n\n## Decisions\n\n");
        text.AppendJoin('\n', Decisions(m).Select(d => $"- {d}")).Append("\n\n## Action items\n\n");
        text.AppendJoin('\n', m.Actions.OrderBy(a => a.Number).Select(a => $"- {a.Text}{(OwnerName(a, speakers) is { } o ? $" — {o}" : "")}{(a.Due is { } d ? $", by {d:yyyy-MM-dd}" : "")}{(a.Done ? " (done)" : "")}"));
        text.Append("\n\n## Transcript\n\n").Append(MeetingEngine.Readable(Lines(m), names));
        var all = text.ToString();
        var most = chat.CurrentValue.MaxAttachmentChars;
        var attachment = new ChatAttachment
        {
            UserId = me.Id, FileName = $"Meeting — {string.Concat(m.Title.Split(Path.GetInvalidFileNameChars()))}.md", ContentType = "text/markdown",
            Size = Encoding.UTF8.GetByteCount(all), Text = all.Length > most ? all[..most] : all, Truncated = all.Length > most,
        };
        db.ChatAttachments.Add(attachment);
        var c = new Conversation { UserId = me.Id, Title = ChatService.TitleFrom($"About: {m.Title}") };
        db.Conversations.Add(c);
        await db.SaveChangesAsync(ct);
        var question = body.Question?.Trim() is { Length: > 0 } q ? q : "What did we decide, and who does what next?";
        return Results.Ok(new
        {
            id = c.Id, message = $"About the meeting “{m.Title}” (its minutes and transcript are attached): {question}",
            attachment = new { attachment.Id, attachment.FileName, attachment.Size, attachment.Kind, attachment.ContentType, chars = attachment.Text.Length, attachment.Truncated },
        });
    }
}
