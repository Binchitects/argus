using System.Security.Claims;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>People and groups to add to a room, with the role they get (member or viewer).</summary>
public sealed record RoomPeople(Guid[]? UserIds = null, Guid[]? GroupIds = null, string? Role = null);

/// <summary>A new room: its title and first people; or, with <paramref name="From"/>, one of the person's chats made into a room.</summary>
public sealed record NewRoom(string? Title = null, Guid[]? UserIds = null, Guid[]? GroupIds = null, string? Role = null, Guid? From = null);

/// <summary>A room's settings: "every" or "mention" (answered only when @Arena is named), and whether its members may invite.</summary>
public sealed record RoomChange(string? AnswerMode = null, bool? MembersInvite = null);

public sealed record RoomRole(string Role);

public sealed record RoomOwner(Guid UserId);

/// <summary>
/// Rooms: chats several people share. Whoever asks pays for the answer and their rules apply to it; every message
/// says who wrote it; the room's people see one another write and the answers come, live (RoomHub). Every chat
/// route checks room access (RoomAccess); these are the room's own: making one, its people, its settings, its events.
/// </summary>
public static partial class RoomEndpoints
{
    public const string EveryMode = "every";
    public const string MentionMode = "mention";
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    public static void MapRooms(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/rooms").PersonOnly();
        g.MapPost("", CreateAsync).RequirePermission(Core.Access.Permissions.RoomsCreate);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapPost("/{id:guid}/members", AddAsync);
        g.MapPut("/{id:guid}/members/{userId:guid}", RoleAsync);
        g.MapDelete("/{id:guid}/members/{userId:guid}", RemoveAsync);
        g.MapDelete("/{id:guid}/groups/{groupId:guid}", RemoveGroupAsync);
        g.MapPut("/{id:guid}/owner", HandOverAsync);
        g.MapGet("/{id:guid}/events", EventsAsync);
        g.MapPost("/{id:guid}/typing", TypingAsync);
        g.MapPost("/{id:guid}/read", ReadAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static string NameOf(AppUser u) => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName! : u.DisplayName;

    [GeneratedRegex(@"(^|\s)@arena\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArenaNamed();

    /// <summary>Whether a message names the model (@Arena): a room on "mention" answers only those.</summary>
    public static bool NamesArena(string text) => ArenaNamed().IsMatch(text);

    /// <summary>Of the people a message names, those in the room.</summary>
    public static async Task<List<Guid>> MentionsAsync(AppDbContext db, Conversation c, IEnumerable<Guid>? named, CancellationToken ct)
    {
        var asked = (named ?? []).Distinct().ToHashSet();
        if (asked.Count == 0)
        {
            return [];
        }
        return [.. (await RoomAccess.PeopleAsync(db, c, ct)).Select(x => x.Person.Id).Where(asked.Contains)];
    }

    /// <summary>What the chat page shows of a room: the person's role, its people (who is here, who is typing), its groups and settings.</summary>
    public static async Task<object> BriefAsync(AppDbContext db, Conversation c, AppUser me, AnswerJobs jobs, CancellationToken ct)
    {
        var people = await RoomAccess.PeopleAsync(db, c, ct);
        var groups = await db.RoomGroups.AsNoTracking().Where(g => g.ConversationId == c.Id)
            .Join(db.Groups.AsNoTracking(), r => r.GroupId, g => g.Id, (r, g) => new { id = g.Id, g.Name, r.Role }).ToListAsync(ct);
        var role = await RoomAccess.RoleAsync(db, c, me, ct);
        return new
        {
            role,
            answerMode = c.AnswerMode ?? EveryMode,
            c.MembersInvite,
            answeringFor = jobs.Find(c.Id)?.Person,
            members = people.Select(x => new { id = x.Person.Id, name = NameOf(x.Person), role = x.Role }),
            groups,
        };
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs, RoomHub hub, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true } c)
        {
            return Results.NotFound();
        }
        return Results.Ok(new { brief = await BriefAsync(db, c, me, jobs, ct), here = hub.Here(id), typing = hub.TypingNow(id) });
    }

    private static string? RoleOf(string? role) => role switch
    {
        null or "" or RoomMember.Member => RoomMember.Member,
        RoomMember.Viewer => RoomMember.Viewer,
        _ => null,
    };

    /// <summary>The people and groups, checked: active people, groups that are there, and no more than the room may hold.</summary>
    private static async Task<IResult?> AddPeopleAsync(AppDbContext db, Conversation c, AppUser me, Guid[]? userIds, Guid[]? groupIds, string role, int most,
        Notifications.Notifier notifier, CancellationToken ct)
    {
        var ids = (userIds ?? []).Where(x => x != c.UserId).Distinct().ToList();
        var groups = (groupIds ?? []).Distinct().ToList();
        if (ids.Count > 0 && await db.Users.CountAsync(u => ids.Contains(u.Id) && !u.IsDisabled, ct) != ids.Count)
        {
            return AuthEndpoints.Problem(400, "people", "Someone of those is not here, or is disabled.");
        }
        if (groups.Count > 0 && await db.Groups.CountAsync(g => groups.Contains(g.Id), ct) != groups.Count)
        {
            return AuthEndpoints.Problem(400, "groups", "A group of those is not here.");
        }
        var had = await db.RoomMembers.Where(m => m.ConversationId == c.Id).ToListAsync(ct);
        var added = new List<Guid>();
        foreach (var user in ids)
        {
            if (had.FirstOrDefault(m => m.UserId == user) is { } row)
            {
                row.Role = role;
            }
            else
            {
                db.RoomMembers.Add(new RoomMember { ConversationId = c.Id, UserId = user, Role = role, AddedById = me.Id });
                added.Add(user);
            }
        }
        var hadGroups = await db.RoomGroups.Where(g => g.ConversationId == c.Id).ToListAsync(ct);
        foreach (var group in groups)
        {
            if (hadGroups.FirstOrDefault(g => g.GroupId == group) is { } row)
            {
                row.Role = role;
            }
            else
            {
                db.RoomGroups.Add(new RoomGroup { ConversationId = c.Id, GroupId = group, Role = role });
            }
        }
        await db.SaveChangesAsync(ct);
        if (most > 0 && (await RoomAccess.PeopleAsync(db, c, ct)).Count > most)
        {
            return AuthEndpoints.Problem(400, "too_many", $"A room holds at most {most} people.");
        }
        foreach (var user in added)
        {
            await notifier.SendAsync(user, new Notifications.News("room", $"{NameOf(me)} added you to {c.Title}", null, $"/chat/{c.Id}"), ct);
        }
        return null;
    }

    private static async Task<IResult> CreateAsync(NewRoom body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<ChatOptions> chat,
        Notifications.Notifier notifier, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (RoleOf(body.Role) is not { } role)
        {
            return AuthEndpoints.Problem(400, "role", "A role is member or viewer.");
        }
        Conversation c;
        if (body.From is { } from)
        {
            // One of their chats, as it is: what it said so far becomes the room's (the page says so first).
            if (await db.Conversations.SingleOrDefaultAsync(x => x.Id == from && x.UserId == me.Id, ct) is not { } own)
            {
                return Results.NotFound();
            }
            if (own.Origin is not null)
            {
                return AuthEndpoints.Problem(400, "origin", "A chat kept in step with Code Arena cannot become a room.");
            }
            c = own;
        }
        else
        {
            c = new Conversation { UserId = me.Id, Title = string.IsNullOrWhiteSpace(body.Title) ? "New room" : body.Title.Trim()[..Math.Min(200, body.Title.Trim().Length)] };
            db.Conversations.Add(c);
        }
        if (body.Title is { Length: > 0 } title && body.From is not null)
        {
            c.Title = title.Trim()[..Math.Min(200, title.Trim().Length)];
        }
        c.Kind = RoomMember.Room;
        c.AnswerMode ??= EveryMode;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (await AddPeopleAsync(db, c, me, body.UserIds, body.GroupIds, role, chat.CurrentValue.RoomMostPeople, notifier, ct) is { } refused)
        {
            return refused;
        }
        await audit.WriteAsync("room.create", c.Id.ToString(), detail: $"{(body.UserIds ?? []).Length} people, {(body.GroupIds ?? []).Length} groups");
        return Results.Created($"/api/chat/conversations/{c.Id}", new { c.Id, c.Title });
    }

    private static async Task<IResult> UpdateAsync(Guid id, RoomChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Manage, ct) is not { IsRoom: true } c)
        {
            return Results.NotFound();
        }
        if (body.AnswerMode is { } mode)
        {
            if (mode is not (EveryMode or MentionMode))
            {
                return AuthEndpoints.Problem(400, "answer_mode", "Answer every message (every), or only when @Arena is named (mention).");
            }
            c.AnswerMode = mode;
        }
        c.MembersInvite = body.MembersInvite ?? c.MembersInvite;
        await db.SaveChangesAsync(ct);
        hub.Changed(id, "settings");
        await audit.WriteAsync("room.settings", id.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> AddAsync(Guid id, RoomPeople body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, IOptionsMonitor<ChatOptions> chat,
        Notifications.Notifier notifier, RoomHub hub, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true } c)
        {
            return Results.NotFound();
        }
        var role = await RoomAccess.RoleAsync(db, c, me, ct);
        if (!(role == RoomMember.Owner || (role == RoomMember.Member && c.MembersInvite)))
        {
            return AuthEndpoints.Problem(403, "invite", "Only the room's owner invites people here.");
        }
        if (RoleOf(body.Role) is not { } given)
        {
            return AuthEndpoints.Problem(400, "role", "A role is member or viewer.");
        }
        if (await AddPeopleAsync(db, c, me, body.UserIds, body.GroupIds, given, chat.CurrentValue.RoomMostPeople, notifier, ct) is { } refused)
        {
            return refused;
        }
        hub.Changed(id, "members");
        await audit.WriteAsync("room.invite", id.ToString(), detail: $"{(body.UserIds ?? []).Length} people, {(body.GroupIds ?? []).Length} groups as {given}");
        return Results.NoContent();
    }

    private static async Task<IResult> RoleAsync(Guid id, Guid userId, RoomRole body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Manage, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        if (RoleOf(body.Role) is not { } role)
        {
            return AuthEndpoints.Problem(400, "role", "A role is member or viewer.");
        }
        if (await db.RoomMembers.SingleOrDefaultAsync(m => m.ConversationId == id && m.UserId == userId, ct) is not { } row)
        {
            return Results.NotFound();
        }
        row.Role = role;
        await db.SaveChangesAsync(ct);
        hub.Changed(id, "members");
        await audit.WriteAsync("room.role", id.ToString(), detail: role);
        return Results.NoContent();
    }

    /// <summary>The owner takes someone out; anyone leaves (the owner hands the room over first).</summary>
    private static async Task<IResult> RemoveAsync(Guid id, Guid userId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        var need = userId == me.Id ? Need.Read : Need.Manage;
        if (await RoomAccess.FindAsync(db, id, me, need, ct) is not { IsRoom: true } c)
        {
            return Results.NotFound();
        }
        if (userId == c.UserId)
        {
            return AuthEndpoints.Problem(400, "owner", "The owner hands the room to someone else before leaving it.");
        }
        var gone = await db.RoomMembers.Where(m => m.ConversationId == id && m.UserId == userId).ExecuteDeleteAsync(ct);
        if (gone == 0 && userId != me.Id)
        {
            return Results.NotFound();
        }
        hub.Close(id, userId);
        hub.Changed(id, "members");
        await audit.WriteAsync(userId == me.Id ? "room.leave" : "room.remove", id.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveGroupAsync(Guid id, Guid groupId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Manage, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        if (await db.RoomGroups.Where(g => g.ConversationId == id && g.GroupId == groupId).ExecuteDeleteAsync(ct) == 0)
        {
            return Results.NotFound();
        }
        hub.Changed(id, "members");
        await audit.WriteAsync("room.remove", id.ToString(), detail: "a group");
        return Results.NoContent();
    }

    /// <summary>The owner hands the room to one of its people, who becomes its owner; the old owner stays, as a member.</summary>
    private static async Task<IResult> HandOverAsync(Guid id, RoomOwner body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Manage, ct) is not { IsRoom: true } c)
        {
            return Results.NotFound();
        }
        if (await db.Users.SingleOrDefaultAsync(u => u.Id == body.UserId && !u.IsDisabled, ct) is not { } next
            || await RoomAccess.RoleAsync(db, c, next, ct) is not (RoomMember.Member or RoomMember.Viewer))
        {
            return AuthEndpoints.Problem(400, "owner", "Hand the room to one of its people.");
        }
        await db.RoomMembers.Where(m => m.ConversationId == id && m.UserId == next.Id).ExecuteDeleteAsync(ct);
        db.RoomMembers.Add(new RoomMember { ConversationId = id, UserId = c.UserId, Role = RoomMember.Member, AddedById = next.Id });
        c.UserId = next.Id;
        await db.SaveChangesAsync(ct);
        hub.Changed(id, "members");
        await audit.WriteAsync("room.transfer", id.ToString());
        return Results.NoContent();
    }

    /// <summary>The room's events as server-sent events, for as long as the page has it open (or the person is taken out).</summary>
    private static async Task EventsAsync(Guid id, HttpContext http, UserManager<AppUser> users, AppDbContext db, RoomHub hub)
    {
        var me = await Me(http.User, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, http.RequestAborted) is not { IsRoom: true })
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.Body.FlushAsync(http.RequestAborted);
        try
        {
            await foreach (var line in hub.WatchAsync(id, me.Id, Heartbeat, http.RequestAborted))
            {
                await http.Response.WriteAsync(line.Length == 0 ? ": still here\n\n" : $"data: {line}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The page closed.
        }
    }

    private static async Task<IResult> TypingAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, RoomHub hub, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Write, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        hub.Typing(id, me.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> ReadAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        await db.RoomMembers.Where(m => m.ConversationId == id && m.UserId == me.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadAt, clock.GetUtcNow()), ct);
        return Results.NoContent();
    }
}
