using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>A room's calls: how many pages one holds (each hears every other: the more, the more each sends), and the ICE servers the browsers use to reach one another.</summary>
public sealed class RoomCallOptions
{
    public int MostInCall { get; set; } = 8;

    /// <summary>STUN or TURN servers ("stun:stun.example.com:3478"), when the browsers are not on one network. None: they reach one another directly.</summary>
    public List<string> IceServers { get; set; } = [];

    /// <summary>Captions a room keeps (in memory) for a page that joins the call late.</summary>
    public int CaptionsKept { get; set; } = 200;
}

public sealed record CallSeat(Guid Page, string? Language, bool? Muted);

public sealed record CallSignal(Guid Page, Guid To, JsonElement Signal);

public sealed record CaptionTranslation(string? Language);

/// <summary>What one person said in a call, written down: kept a while, each language it is translated into once.</summary>
public sealed class Caption(Guid id, Guid page, Guid person, string text, DateTimeOffset at)
{
    public Guid Id { get; } = id;
    public Guid Page { get; } = page;
    public Guid Person { get; } = person;
    public string Text { get; } = text;
    public DateTimeOffset At { get; } = at;

    /// <summary>Its translations, each asked once for everyone reading in that language: the text (null: it was in that language), or why not.</summary>
    public ConcurrentDictionary<string, Lazy<Task<(string? Text, string? Problem)>>> Translations { get; } = new();

    public object Json() => new { id = Id, page = Page, by = Person, text = Text, at = At };
}

/// <summary>The captions of each room's call, the last few hundred, in memory on this replica (as the call is).</summary>
public sealed class RoomCaptions(IOptionsMonitor<RoomCallOptions> options)
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Caption>> _rooms = new();

    public Caption Add(Guid room, Caption caption)
    {
        var kept = _rooms.GetOrAdd(room, _ => new());
        kept.Enqueue(caption);
        while (kept.Count > Math.Max(1, options.CurrentValue.CaptionsKept) && kept.TryDequeue(out _))
        {
        }
        return caption;
    }

    public Caption? Find(Guid room, Guid id) => _rooms.TryGetValue(room, out var kept) ? kept.FirstOrDefault(c => c.Id == id) : null;

    public IReadOnlyList<Caption> Recent(Guid room, int most) => _rooms.TryGetValue(room, out var kept) ? [.. kept.TakeLast(most)] : [];

    /// <summary>The call ended (nobody is in it): what was said goes too.</summary>
    public void Clear(Guid room) => _rooms.TryRemove(room, out _);
}

/// <summary>
/// Voice in rooms (/api/rooms/{id}/call): the room's people talk, each page to every other (WebRTC; the server passes only
/// their signals, never their sound), and what each says is written down as they speak (the speaker's page cuts it at a
/// pause and sends it to be heard, as Talk does) and shown to all as live captions, each reader's in their language.
/// </summary>
public static class RoomCalls
{
    /// <summary>Captions a page that joins late is given.</summary>
    public const int CaptionsOnJoin = 30;

    public static void MapRoomCalls(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/rooms/{id:guid}/call").PersonOnly();
        g.MapPost("", JoinAsync);
        g.MapDelete("", LeaveAsync);
        g.MapPost("/signal", SignalAsync);
        g.MapPost("/captions", CaptionAsync).DisableAntiforgery().RequirePermission(Llm.Core.Access.Permissions.Speech);
        g.MapPost("/captions/{captionId:guid}/translation", TranslateAsync);
    }

    private static object Seats(RoomHub hub, Guid room) => hub.InCall(room).Select(s => new { page = s.Page, person = s.Person, muted = s.Muted });

    /// <summary>A page takes its seat (or says it is muted, or reads in another language): the others' pages call it.</summary>
    private static async Task<IResult> JoinAsync(Guid id, CallSeat body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub, RoomCaptions captions,
        IOptionsMonitor<RoomCallOptions> options, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (await RoomAccess.FindAsync(db, id, me, Need.Write, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        if (!hub.Watching(id, me.Id, body.Page))
        {
            return AuthEndpoints.Problem(409, "not_open", "Open the room first: the call reaches the page through its events.");
        }
        var language = RoomTranslations.Languages.Any(l => l.Code == body.Language) ? body.Language : null;
        var o = options.CurrentValue;
        if (hub.InCall(id).Count == 0)
        {
            // A new call (the last one ended, perhaps by its pages closing): what was said in that one is not this one's.
            captions.Clear(id);
        }
        if (!hub.Sit(id, new RoomHub.Seat(body.Page, me.Id, language, body.Muted ?? false), o.MostInCall))
        {
            return AuthEndpoints.Problem(409, "full", $"The call is full: {o.MostInCall} are in it.");
        }
        return Results.Ok(new
        {
            seats = Seats(hub, id),
            iceServers = o.IceServers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => new { urls = s.Trim() }),
            captions = captions.Recent(id, CaptionsOnJoin).Select(c => c.Json()),
        });
    }

    private static async Task<IResult> LeaveAsync(Guid id, Guid page, ClaimsPrincipal p, UserManager<AppUser> users, RoomHub hub, RoomCaptions captions)
    {
        var me = (await users.GetUserAsync(p))!;
        if (hub.InCall(id).Any(s => s.Page == page && s.Person == me.Id))
        {
            hub.Stand(id, page);
        }
        if (hub.InCall(id).Count == 0)
        {
            captions.Clear(id);
        }
        return Results.NoContent();
    }

    /// <summary>A call's signal (an offer, an answer, a way to reach it) from one page in the call to another, passed on as it is.</summary>
    private static async Task<IResult> SignalAsync(Guid id, CallSignal body, ClaimsPrincipal p, UserManager<AppUser> users, RoomHub hub)
    {
        var me = (await users.GetUserAsync(p))!;
        var seats = hub.InCall(id);
        if (!seats.Any(s => s.Page == body.Page && s.Person == me.Id))
        {
            return AuthEndpoints.Problem(409, "not_in_call", "Join the call first.");
        }
        if (!seats.Any(s => s.Page == body.To))
        {
            return AuthEndpoints.Problem(404, "not_found", "They are not in the call.");
        }
        if (body.Signal.ValueKind != JsonValueKind.Object || body.Signal.GetRawText().Length > 64 * 1024)
        {
            return AuthEndpoints.Problem(400, "signal", "A signal is an object, at most 64 KB.");
        }
        hub.PublishTo(id, body.To, new { type = "signal", from = body.Page, by = me.Id, signal = body.Signal });
        return Results.NoContent();
    }

    /// <summary>What the person said, cut at a pause by their page: written down and shown to the call as a caption.</summary>
    private static async Task<IResult> CaptionAsync(Guid id, HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub,
        RoomCaptions captions, GatewayChat gateway, ChatModels models, VoiceCatalog voices, MediaControl media, TimeProvider clock)
    {
        var ct = request.HttpContext.RequestAborted;
        var me = (await users.GetUserAsync(p))!;
        if (await RoomAccess.FindAsync(db, id, me, Need.Write, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = Talk.MaxBytes + 64 * 1024;
        }
        if (!request.HasFormContentType || await request.ReadFormAsync(ct) is not { Files.Count: 1 } form || !Guid.TryParse(form["page"], out var page))
        {
            return AuthEndpoints.Problem(400, "file", "Send one recording and the page it is from.");
        }
        if (!hub.InCall(id).Any(s => s.Page == page && s.Person == me.Id && !s.Muted))
        {
            return AuthEndpoints.Problem(409, "not_in_call", "Join the call (unmuted) first.");
        }
        var (text, refused) = await Talk.WriteDownAsync(request.HttpContext, form.Files[0], me, gateway, models, voices, media, ct);
        if (refused is not null)
        {
            return refused;
        }
        if (text.Length == 0)
        {
            return Results.NoContent();
        }
        var caption = captions.Add(id, new Caption(Guid.NewGuid(), page, me.Id, text.Length > RoomTranslations.MostChars ? text[..RoomTranslations.MostChars] : text, clock.GetUtcNow()));
        hub.Publish(id, new { type = "caption", caption = caption.Json() });
        return Results.Ok(caption.Json());
    }

    /// <summary>
    /// A caption in the reader's language: translated once for everyone reading in it (by the translation model, as the
    /// first reader who asks), null when it was in that language already.
    /// </summary>
    private static async Task<IResult> TranslateAsync(Guid id, Guid captionId, CaptionTranslation body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        RoomCaptions captions, IServiceScopeFactory scopes, IOptionsMonitor<ChatOptions> chat, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        if (RoomTranslations.Languages.FirstOrDefault(l => l.Code == body.Language) is not { Code: { } code } language)
        {
            return AuthEndpoints.Problem(400, "language", "Choose a language the room can be read in.");
        }
        if (captions.Find(id, captionId) is not { } caption)
        {
            return AuthEndpoints.Problem(404, "not_found", "That caption is gone.");
        }
        var model = chat.CurrentValue.TranslationModel is { Length: > 0 } named ? named : "small";
        // Asked once for everyone reading in this language, not stopped when the first reader leaves.
        var asked = caption.Translations.GetOrAdd(code, _ => new(() => Task.Run(async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var one = scope.ServiceProvider.GetRequiredService<OneQuestion>();
            try
            {
                var (made, why) = await RoomTranslations.AskAsync(one, me, model, language.Name, [(caption.Id, caption.Text)], timeout.Token);
                return why is null && made.TryGetValue(caption.Id, out var text) ? (text, (string?)null) : ((string?)null, why ?? "The model did not translate it this time.");
            }
            catch (OperationCanceledException)
            {
                return ((string?)null, "The translation took too long.");
            }
        })));
        var (text, problem) = await asked.Value.WaitAsync(ct);
        if (problem is not null)
        {
            // Asked again by the next reader.
            caption.Translations.TryRemove(new KeyValuePair<string, Lazy<Task<(string? Text, string? Problem)>>>(code, asked));
        }
        return Results.Ok(new { id = caption.Id, language = code, text, same = problem is null && text is null, error = problem });
    }
}
