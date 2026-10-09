using Llm.Api.Ldap;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Access;

/// <summary>What someone belongs to: whether they are an admin, and their groups (app and directory).</summary>
public sealed record Membership(bool IsAdmin, IReadOnlySet<Guid> Groups)
{
    public bool May(Audience audience, IEnumerable<Guid>? groups) => IsAdmin || audience switch
    {
        Audience.Everyone => true,
        Audience.Groups => (groups ?? []).Any(Groups.Contains),
        _ => false,
    };
}

public sealed class AccessService(AppDbContext db, UserManager<AppUser> users)
{
    public async Task<Membership> MembershipAsync(AppUser user, CancellationToken ct = default)
    {
        var admin = await users.IsInRoleAsync(user, Roles.Admin);
        var groups = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == user.Id).Select(m => m.GroupId).ToListAsync(ct);
        if (user.DirectoryGroups.Count > 0)
        {
            var directory = await db.Groups.AsNoTracking().Where(g => g.Directory != null).Select(g => new { g.Id, g.Directory }).ToListAsync(ct);
            groups.AddRange(directory.Where(g => InDirectoryGroup(user.DirectoryGroups, g.Directory!)).Select(g => g.Id));
        }
        return new Membership(admin, groups.ToHashSet());
    }

    /// <summary>Their place in the answers' line: the highest priority of their groups; 0 when none sets one.</summary>
    public async Task<int> PriorityAsync(AppUser user, CancellationToken ct = default)
    {
        var groups = (await MembershipAsync(user, ct)).Groups;
        return groups.Count == 0 ? 0 : await db.Groups.AsNoTracking().Where(g => groups.Contains(g.Id)).MaxAsync(g => (int?)g.Priority, ct) ?? 0;
    }

    /// <summary>The directory's rule for group names: the full DN, or its common name, in any case.</summary>
    public static bool InDirectoryGroup(IEnumerable<string> memberOf, string group) => memberOf.Any(g => LdapDirectory.IsNamed(g, group));
}
