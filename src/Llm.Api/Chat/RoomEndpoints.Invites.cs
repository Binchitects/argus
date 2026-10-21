using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <param name="Role">member or viewer.</param>
/// <param name="Days">How long it works: 1 to 30 days.</param>
/// <param name="Uses">How many may join with it; null: any number until it expires.</param>
public sealed record NewInvite(string? Role = null, int Days = 7, int? Uses = null);

/// <summary>Rooms' invite links (made by whoever may invite, used by anyone signed in who may chat) and a room's export.</summary>
public static partial class RoomEndpoints
{
    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<bool> MayInviteAsync(AppDbContext db, Conversation c, AppUser me, CancellationToken ct)
    {
        var role = await RoomAccess.RoleAsync(db, c, me, ct);
        return role == RoomMember.Owner || (role == RoomMember.Member && c.MembersInvite);
    }

    /// <summary>A link to join the room: shown once (only its hash is kept), with its role, expiry and uses.</summary>
    private static async Task<IResult> InviteAsync(Guid id, NewInvite body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true } c)
        {
            return Results.NotFound();
        }
        if (!await MayInviteAsync(db, c, me, ct))
        {
            return AuthEndpoints.Problem(403, "invite", "Only the room's owner invites people here.");
        }
        if (RoleOf(body.Role) is not { } role)
        {
            return AuthEndpoints.Problem(400, "role", "A role is member or viewer.");
        }
        if (body.Days is < 1 or > 30 || body.Uses is < 1 or > 1000)
        {
            return AuthEndpoints.Problem(400, "invite", "A link works 1 to 30 days, for 1 to 1,000 people (or any number).");
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var invite = new RoomInvite
        {
            ConversationId = id, TokenHash = HashOf(token), Role = role, CreatedById = me.Id, ExpiresAt = clock.GetUtcNow().AddDays(body.Days), MaxUses = body.Uses,
        };
        db.RoomInvites.Add(invite);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("room.invite_link", id.ToString(), detail: $"{role}, {body.Days} days, {(body.Uses is { } n ? $"{n} uses" : "any number")}", actor: me);
        return Results.Created($"/api/rooms/{id}/invites/{invite.Id}", new { invite.Id, token, link = $"/rooms/join/{token}", invite.ExpiresAt, invite.Role, invite.MaxUses });
    }

    /// <summary>The room's links still working, for those who may invite.</summary>
    private static async Task<IResult> InvitesAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true } c || !await MayInviteAsync(db, c, me, ct))
        {
            return Results.NotFound();
        }
        var now = clock.GetUtcNow();
        var invites = (await db.RoomInvites.AsNoTracking().Where(i => i.ConversationId == id && i.RevokedAt == null && i.ExpiresAt > now).OrderByDescending(i => i.CreatedAt).ToListAsync(ct))
            .Where(i => i.Usable(now)).ToList();
        var ids = invites.Select(i => i.CreatedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, NameOf, ct);
        return Results.Ok(invites.Select(i => new
        {
            i.Id, i.Role, i.CreatedAt, i.ExpiresAt, i.Uses, i.MaxUses, by = i.CreatedById is { } by ? names.GetValueOrDefault(by) : null, mine = i.CreatedById == me.Id,
        }));
    }

    /// <summary>A link stops working: the owner's doing, or whoever made it.</summary>
    private static async Task<IResult> RevokeAsync(Guid id, Guid inviteId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true } c
            || await db.RoomInvites.SingleOrDefaultAsync(i => i.Id == inviteId && i.ConversationId == id, ct) is not { } invite)
        {
            return Results.NotFound();
        }
        if (c.UserId != me.Id && invite.CreatedById != me.Id)
        {
            return AuthEndpoints.Problem(403, "invite", "The owner, or whoever made the link, stops it.");
        }
        invite.RevokedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("room.invite_revoked", id.ToString(), actor: me);
        return Results.NoContent();
    }

    private static async Task<(RoomInvite? Invite, Conversation? Room)> InviteOfAsync(AppDbContext db, string token, TimeProvider clock, CancellationToken ct)
    {
        var hash = HashOf(token);
        var invite = await db.RoomInvites.SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null || !invite.Usable(clock.GetUtcNow()))
        {
            return (null, null);
        }
        var room = await db.Conversations.SingleOrDefaultAsync(c => c.Id == invite.ConversationId && c.Kind == RoomMember.Room, ct);
        return room is null ? (null, null) : (invite, room);
    }

    /// <summary>What a link leads to, before joining: the room's name, its owner, the role it gives, whether one is in it already.</summary>
    private static async Task<IResult> JoinPreviewAsync(string token, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await InviteOfAsync(db, token, clock, ct) is not ({ } invite, { } room))
        {
            return AuthEndpoints.Problem(404, "invite", "This link does not work: it expired, was used up, or was stopped. Ask for a new one.");
        }
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Id == room.UserId, ct);
        var people = await RoomAccess.PeopleAsync(db, room, ct);
        return Results.Ok(new
        {
            room = room.Id, title = room.Title, owner = NameOf(owner), invite.Role, people = people.Count, inside = people.Any(x => x.Person.Id == me.Id),
        });
    }

    /// <summary>Joining with a link: the person is a member (or viewer) as it says, the owner hears, and the link counts one use.</summary>
    private static async Task<IResult> JoinAsync(string token, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Access.AccessService access, TimeProvider clock,
        IOptionsMonitor<ChatOptions> chat, Notifications.Notifier notifier, RoomHub hub, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!(await access.MembershipAsync(me, ct)).Has(Core.Access.Permissions.Chat))
        {
            return AuthEndpoints.Problem(403, "chat", "The chat is not among what you may use here.");
        }
        if (await InviteOfAsync(db, token, clock, ct) is not ({ } invite, { } room))
        {
            return AuthEndpoints.Problem(404, "invite", "This link does not work: it expired, was used up, or was stopped. Ask for a new one.");
        }
        if ((await RoomAccess.PeopleAsync(db, room, ct)).Any(x => x.Person.Id == me.Id))
        {
            return Results.Ok(new { id = room.Id, joined = false });
        }
        if (await AddPeopleAsync(db, room, me, [me.Id], null, invite.Role, chat.CurrentValue.RoomMostPeople, notifier, ct) is { } refused)
        {
            return refused;
        }
        // One use, counted where two people joining at once cannot both take the last one.
        var used = await db.RoomInvites.Where(i => i.Id == invite.Id && (i.MaxUses == null || i.Uses < i.MaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Uses, i => i.Uses + 1), ct);
        if (used == 0)
        {
            await db.RoomMembers.Where(m => m.ConversationId == room.Id && m.UserId == me.Id).ExecuteDeleteAsync(ct);
            return AuthEndpoints.Problem(404, "invite", "This link was used up a moment ago. Ask for a new one.");
        }
        hub.Changed(room.Id, "members");
        await notifier.SendAsync(room.UserId, new Notifications.News("room", $"{NameOf(me)} joined {room.Title} with a link", null, $"/chat/{room.Id}"), ct);
        await audit.WriteAsync("room.join", room.Id.ToString(), detail: invite.Role, actor: me);
        return Results.Ok(new { id = room.Id, joined = true });
    }

    /// <summary>
    /// The room as a file: its title, its people, and every message in order with who wrote it and when (the model's answers
    /// by their model), Markdown or JSON. For anyone in the room who may export.
    /// </summary>
    private static async Task<IResult> ExportAsync(Guid id, string? format, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true } c)
        {
            return Results.NotFound();
        }
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id && (m.Role == "user" || m.Role == "assistant"))
            .OrderBy(m => m.Sequence).ToListAsync(ct);
        var authorIds = messages.Select(m => m.AuthorId).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, NameOf, ct);
        var people = await RoomAccess.PeopleAsync(db, c, ct);
        string Who(ChatMessage m) => m.Role == "assistant" ? $"Arena ({m.Model ?? "model"})" : m.AuthorId is { } a ? names.GetValueOrDefault(a) ?? "Former member" : "Former member";
        await audit.WriteAsync("room.export", id.ToString(), detail: format == "json" ? "json" : "markdown", actor: me);
        var file = string.Concat(c.Title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (format == "json")
        {
            var json = JsonSerializer.Serialize(new
            {
                title = c.Title, exportedAt = DateTimeOffset.UtcNow,
                people = people.Select(x => new { name = NameOf(x.Person), x.Role }),
                messages = messages.Select(m => new { author = Who(m), m.Role, m.Content, m.Quote, m.CreatedAt, forPeople = m.ForPeople, replyTo = m.ReplyToId }),
            }, JsonSerializerOptions.Web);
            return Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"{(file.Length > 0 ? file : "room")}.json");
        }
        var md = new StringBuilder($"# {c.Title}\n\nPeople: {string.Join(", ", people.Select(x => $"{NameOf(x.Person)} ({x.Role})"))}\n");
        foreach (var m in messages)
        {
            md.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n## {Who(m)}, {m.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC\n\n");
            if (m.Quote is { Length: > 0 } quote)
            {
                md.Append(string.Join('\n', quote.Split('\n').Select(l => "> " + l))).Append("\n\n");
            }
            md.Append(m.Content).Append('\n');
        }
        return Results.File(Encoding.UTF8.GetBytes(md.ToString()), "text/markdown; charset=utf-8", $"{(file.Length > 0 ? file : "room")}.md");
    }
}
