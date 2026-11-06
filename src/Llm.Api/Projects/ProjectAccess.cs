using Llm.Api.Access;
using Llm.Core.Data;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Projects;

/// <summary>
/// Who may do what in a project. Its maker is an owner; the people and groups in it have their roles (the best of their own
/// row and their groups' counts); everyone is a contributor of a global project; projects.manage is an owner of every project.
/// A project someone may not see is not there for them (404).
/// </summary>
public static class ProjectAccess
{
    /// <summary>The projects a person sees: global ones, their own, and those they or their groups are in (all of them with projects.manage).</summary>
    public static IQueryable<Project> Visible(AppDbContext db, Guid me, Membership m)
    {
        if (m.Has(Core.Access.Permissions.ProjectsManage))
        {
            return db.Projects;
        }
        var groups = m.Groups.ToList();
        return db.Projects.Where(p => p.Global || p.OwnerId == me
            || db.ProjectMembers.Any(x => x.ProjectId == p.Id && (x.UserId == me || (x.GroupId != null && groups.Contains(x.GroupId.Value)))));
    }

    /// <summary>Their role in the project: owner, contributor or viewer; null when they are none of these.</summary>
    public static async Task<string?> RoleAsync(AppDbContext db, Project p, Guid me, Membership m, CancellationToken ct)
    {
        if (p.OwnerId == me || m.Has(Core.Access.Permissions.ProjectsManage))
        {
            return ProjectMember.Owner;
        }
        var groups = m.Groups.ToList();
        var roles = await db.ProjectMembers.AsNoTracking()
            .Where(x => x.ProjectId == p.Id && (x.UserId == me || (x.GroupId != null && groups.Contains(x.GroupId.Value))))
            .Select(x => x.Role).ToListAsync(ct);
        var best = roles.MaxBy(ProjectMember.Rank);
        return p.Global && ProjectMember.Rank(best) < ProjectMember.Rank(ProjectMember.Contributor) ? ProjectMember.Contributor : best;
    }

    /// <summary>The project with the person's role, when they hold at least <paramref name="need"/>; null when they may not see it at all.</summary>
    public static async Task<(Project? Project, string? Role)> FindAsync(AppDbContext db, Guid id, Guid me, Membership m, CancellationToken ct)
    {
        var p = await db.Projects.SingleOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? (null, null) : (await RoleAsync(db, p, me, m, ct)) is { } role ? (p, role) : (null, null);
    }

    public static bool Holds(string? role, string need) => ProjectMember.Rank(role) >= ProjectMember.Rank(need);
}
