using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>What a route does to a chat: read it, write in it (ask, attach, stop their own), or manage it (settings, members, delete).</summary>
public enum Need
{
    Read,
    Write,
    Manage,
}

/// <summary>
/// The one check every chat route goes through: a chat is its owner's; a room (a chat with members) is also its
/// members' (people added, and whoever is in a group added), each with a role. A chat someone may not open is not
/// there for them (404).
/// </summary>
public static class RoomAccess
{
    /// <summary>The chat, when <paramref name="me"/> may do <paramref name="need"/> to it; else null.</summary>
    public static async Task<Conversation?> FindAsync(AppDbContext db, Guid id, AppUser me, Need need, CancellationToken ct = default)
    {
        var c = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, ct);
        return c is not null && Allows(await RoleAsync(db, c, me, ct), need) ? c : null;
    }

    public static bool Allows(string? role, Need need) => (role, need) switch
    {
        (null, _) => false,
        (RoomMember.Owner, _) => true,
        (RoomMember.Member, Need.Read or Need.Write) => true,
        (RoomMember.Viewer, Need.Read) => true,
        _ => false,
    };

    /// <summary>Their role in the chat: owner, member or viewer; null when they are none (the best of their own row and their groups').</summary>
    public static async Task<string?> RoleAsync(AppDbContext db, Conversation c, AppUser me, CancellationToken ct = default)
    {
        if (c.UserId == me.Id)
        {
            return RoomMember.Owner;
        }
        if (!c.IsRoom || me.IsDisabled)
        {
            return null;
        }
        var roles = await db.RoomMembers.AsNoTracking().Where(m => m.ConversationId == c.Id && m.UserId == me.Id).Select(m => m.Role).ToListAsync(ct);
        var groups = await db.RoomGroups.AsNoTracking().Where(g => g.ConversationId == c.Id).ToListAsync(ct);
        if (groups.Count > 0)
        {
            var mine = await GroupsOfAsync(db, me, ct);
            roles.AddRange(groups.Where(g => mine.Contains(g.GroupId)).Select(g => g.Role));
        }
        return roles.Contains(RoomMember.Member) ? RoomMember.Member : roles.Contains(RoomMember.Viewer) ? RoomMember.Viewer : null;
    }

    /// <summary>The groups a person is in: app groups they were added to, and directory groups that name them.</summary>
    public static async Task<HashSet<Guid>> GroupsOfAsync(AppDbContext db, AppUser me, CancellationToken ct = default)
    {
        var groups = (await db.GroupMembers.AsNoTracking().Where(m => m.UserId == me.Id).Select(m => m.GroupId).ToListAsync(ct)).ToHashSet();
        if (me.DirectoryGroups.Count > 0)
        {
            var directory = await db.Groups.AsNoTracking().Where(g => g.Directory != null).Select(g => new { g.Id, g.Directory }).ToListAsync(ct);
            groups.UnionWith(directory.Where(g => Access.AccessService.InDirectoryGroup(me.DirectoryGroups, g.Directory!)).Select(g => g.Id));
        }
        return groups;
    }

    /// <summary>The rooms a person is in but does not own (as a member or viewer, themselves or through a group).</summary>
    public static async Task<List<Guid>> RoomsOfAsync(AppDbContext db, AppUser me, CancellationToken ct = default)
    {
        var groups = await GroupsOfAsync(db, me, ct);
        var own = await db.RoomMembers.AsNoTracking().Where(m => m.UserId == me.Id).Select(m => m.ConversationId).ToListAsync(ct);
        var viaGroups = groups.Count == 0 ? [] : await db.RoomGroups.AsNoTracking().Where(g => groups.Contains(g.GroupId)).Select(g => g.ConversationId).ToListAsync(ct);
        var ids = own.Concat(viaGroups).Distinct().ToList();
        return await db.Conversations.AsNoTracking().Where(c => ids.Contains(c.Id) && c.Kind == RoomMember.Room && c.UserId != me.Id).Select(c => c.Id).ToListAsync(ct);
    }

    /// <summary>Everyone in the room now: its owner, its people, and the members of its groups (active people only).</summary>
    public static async Task<List<(AppUser Person, string Role)>> PeopleAsync(AppDbContext db, Conversation c, CancellationToken ct = default)
    {
        var roles = new Dictionary<Guid, string> { [c.UserId] = RoomMember.Owner };
        foreach (var m in await db.RoomMembers.AsNoTracking().Where(m => m.ConversationId == c.Id).ToListAsync(ct))
        {
            roles.TryAdd(m.UserId, m.Role);
        }
        var groups = await db.RoomGroups.AsNoTracking().Where(g => g.ConversationId == c.Id).ToListAsync(ct);
        if (groups.Count > 0)
        {
            var ids = groups.Select(g => g.GroupId).ToList();
            var app = await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.GroupId)).Select(m => new { m.UserId, m.GroupId }).ToListAsync(ct);
            var directory = await db.Groups.AsNoTracking().Where(g => ids.Contains(g.Id) && g.Directory != null).Select(g => new { g.Id, g.Directory }).ToListAsync(ct);
            var listed = directory.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => new { u.Id, u.DirectoryGroups }).ToListAsync(ct);
            foreach (var g in groups)
            {
                var people = app.Where(m => m.GroupId == g.GroupId).Select(m => m.UserId)
                    .Concat(directory.Where(d => d.Id == g.GroupId).SelectMany(d => listed.Where(u => Access.AccessService.InDirectoryGroup(u.DirectoryGroups, d.Directory!)).Select(u => u.Id)));
                foreach (var person in people)
                {
                    if (!roles.TryGetValue(person, out var had) || had == RoomMember.Viewer)
                    {
                        roles[person] = had == RoomMember.Owner ? had : g.Role == RoomMember.Member || had is null ? g.Role : had;
                    }
                }
            }
        }
        var ids2 = roles.Keys.ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids2.Contains(u.Id) && !u.IsDisabled).ToListAsync(ct);
        return [.. users.Select(u => (u, roles[u.Id])).OrderBy(x => x.Item2 == RoomMember.Owner ? 0 : 1).ThenBy(x => x.u.DisplayName)];
    }
}
