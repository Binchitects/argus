using Llm.Api.Identity;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Access;

/// <summary>The app groups new people join at their first sign-in; the rest are left as they are.</summary>
public sealed record DefaultGroupsRequest(Guid[] Groups);

/// <summary>
/// Admin → Access → Groups: each group with its members, the roles it holds and the rules on it; and the default groups,
/// which a new person joins as they first sign in (made by an admin, from the directory, company sign-in or SCIM).
/// </summary>
public static class AccessGroups
{
    /// <summary>The members a group's line lists by name; the count says how many there are in all.</summary>
    public const int MembersShown = 50;

    public static void MapAccessGroups(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/access/groups", OverviewAsync).RequirePermission(Permissions.AccessRead);
        app.MapPut("/api/admin/access/defaults", SetDefaultsAsync).RequirePermission(Permissions.AccessDefaults);
    }

    /// <summary>Whether a group can be a default: an app group, whose members are chosen here.</summary>
    public static bool CanBeDefault(Group g) => g.Directory is null && !g.Scim;

    /// <summary>
    /// Puts a new person in every default group, each written to the audit log. Called once, where each kind of
    /// account is first made; a group that is not an app group any more is skipped.
    /// </summary>
    public static async Task JoinAsync(AppDbContext db, Audit audit, Models.KeyAccessWatcher keys, AppUser user, CancellationToken ct = default)
    {
        var groups = await db.Groups.AsNoTracking().Where(g => g.JoinsNewPeople && g.Directory == null && !g.Scim).ToListAsync(ct);
        if (groups.Count == 0)
        {
            return;
        }
        var already = await db.GroupMembers.Where(m => m.UserId == user.Id).Select(m => m.GroupId).ToListAsync(ct);
        foreach (var g in groups.Where(g => !already.Contains(g.Id)))
        {
            db.GroupMembers.Add(new GroupMember { GroupId = g.Id, UserId = user.Id });
            await audit.WriteAsync("group.add_member", user.UserName, detail: $"{g.Name} (a default group for new people)");
        }
        await db.SaveChangesAsync(ct);
        // Their key carries the models their groups may use.
        keys.Wake();
    }

    private static async Task<IResult> OverviewAsync(AppDbContext db, AccessService access, CancellationToken ct)
    {
        var s = await access.SnapshotAsync(ct);
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var appMembers = (await db.GroupMembers.AsNoTracking().Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id,
                (m, u) => new { m.GroupId, u.Id, u.UserName, u.DisplayName, u.IsDisabled }).ToListAsync(ct))
            .ToLookup(m => m.GroupId);
        var directoryPeople = groups.Any(g => g.Directory is not null)
            ? await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => new { u.Id, u.UserName, u.DisplayName, u.IsDisabled, u.DirectoryGroups }).ToListAsync(ct)
            : [];
        return Results.Ok(groups.Select(g =>
        {
            var members = g.Directory is { } d
                ? directoryPeople.Where(u => AccessService.InDirectoryGroup(u.DirectoryGroups, d)).Select(u => new { u.Id, u.UserName, u.DisplayName, u.IsDisabled }).ToList()
                : appMembers[g.Id].Select(u => new { u.Id, u.UserName, u.DisplayName, u.IsDisabled }).ToList();
            return new
            {
                g.Id, g.Name, g.Description, g.Directory, g.Scim, g.JoinsNewPeople, canBeDefault = CanBeDefault(g),
                memberCount = members.Count,
                members = members.OrderBy(m => m.DisplayName.Length > 0 ? m.DisplayName : m.UserName, StringComparer.OrdinalIgnoreCase).Take(MembersShown)
                    .Select(m => new { m.Id, name = m.DisplayName.Length > 0 ? m.DisplayName : m.UserName, m.UserName, m.IsDisabled }),
                roles = s.Assignments.Where(a => a.SubjectKind == SubjectKind.Group && a.SubjectId == g.Id)
                    .Select(a => new { a.Id, roleId = a.RoleId, role = s.NameOf(a.RoleId), a.Scope, a.ExpiresAt }),
                rules = s.Rules.Where(r => r.SubjectKind == SubjectKind.Group && r.SubjectId == g.Id)
                    .Select(r => new { r.Id, r.Effect, r.ResourceKind, r.Resource, r.Action, r.ExpiresAt, r.Note }),
            };
        }));
    }

    /// <summary>Sets which app groups new people join, all at once; one change in the access history.</summary>
    private static async Task<IResult> SetDefaultsAsync(DefaultGroupsRequest body, AccessService access, HttpContext http, CancellationToken ct)
    {
        var wanted = (body.Groups ?? []).Distinct().ToHashSet();
        try
        {
            var me = await http.RequestServices.GetRequiredService<UserManager<AppUser>>().GetUserAsync(http.User);
            await access.ChangeAsync(me, async edit =>
            {
                var groups = await edit.Db.Groups.ToListAsync(ct);
                if (wanted.Any(id => groups.All(g => g.Id != id)))
                {
                    throw new AccessRefusedException("A group of those is not there.");
                }
                if (groups.FirstOrDefault(g => wanted.Contains(g.Id) && !CanBeDefault(g)) is { } owned)
                {
                    throw new AccessRefusedException($"{owned.Name}'s members come from {(owned.Scim ? "the identity provider (SCIM)" : "the directory")}: only an app group can be one new people join.");
                }
                var before = groups.Where(g => g.JoinsNewPeople).Select(g => new { g.Id, g.Name }).OrderBy(g => g.Name).ToList();
                foreach (var g in groups)
                {
                    g.JoinsNewPeople = wanted.Contains(g.Id);
                }
                var after = groups.Where(g => g.JoinsNewPeople).Select(g => new { g.Id, g.Name }).OrderBy(g => g.Name).ToList();
                if (!before.Select(g => g.Id).SequenceEqual(after.Select(g => g.Id)))
                {
                    edit.Changed("defaults", new { groups = before }, new { groups = after });
                }
            }, "access.defaults", string.Join(", ", wanted), ct);
            return Results.NoContent();
        }
        catch (AccessRefusedException ex)
        {
            return Endpoints.AuthEndpoints.Problem(400, "access", ex.Message);
        }
    }
}
