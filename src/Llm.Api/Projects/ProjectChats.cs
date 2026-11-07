using Llm.Api.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Projects;

/// <summary>
/// Which project a Code Arena session goes in as it is made: the one it was started in (chosen in Code Arena: for its folder,
/// or with /project), else the project its repository is in, when the person may add to it.
/// </summary>
public static class ProjectChats
{
    /// <summary>
    /// Of these projects, the one with this repository (a session's key: host/group/name; or a repository's path). The first
    /// by name when several have it; null for a folder that is no clone ("local/...") or a repository in none.
    /// </summary>
    public static async Task<Guid?> OfRepositoryAsync(AppDbContext db, IReadOnlyCollection<Guid> projects, string key, CancellationToken ct)
    {
        var slash = key.IndexOf('/');
        if (key.StartsWith("local/", StringComparison.Ordinal) || slash < 0)
        {
            return null;
        }
        // host/group/name: the path is after the host (a path of a repository alone has no dot before its first slash).
        var host = key[..slash].Contains('.') || key[..slash].Contains(':') ? key[..slash].ToLowerInvariant() : null;
        var path = host is null ? key : key[(slash + 1)..];
        // Code hosts take paths in any case: the same path, as ILIKE matches it (its wildcards taken as letters).
        var like = path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var hits = await db.ProjectRepositories.AsNoTracking()
            .Where(r => projects.Contains(r.ProjectId) && EF.Functions.ILike(r.Path, like) && (r.Host == null || host == null || r.Host == host))
            .Join(db.Projects, r => r.ProjectId, p => p.Id, (r, p) => new { p.Id, p.Name }).OrderBy(p => p.Name).ToListAsync(ct);
        return hits.Count > 0 ? hits[0].Id : null;
    }

    /// <summary>The project a new session goes in: <paramref name="asked"/> when the person may add to it, else its repository's; null for none.</summary>
    public static async Task<Guid?> ForNewSessionAsync(AppDbContext db, AccessService access, AppUser me, Guid? asked, string? repositoryKey, CancellationToken ct)
    {
        var m = await access.MembershipAsync(me, ct);
        if (asked is { } id && await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.Status == Project.Active, ct) is { } chosen
            && ProjectAccess.Holds(await ProjectAccess.RoleAsync(db, chosen, me.Id, m, ct), ProjectMember.Contributor))
        {
            return id;
        }
        if (repositoryKey is not { Length: > 0 } key)
        {
            return null;
        }
        var candidates = await ProjectAccess.Visible(db, me.Id, m).AsNoTracking().Where(p => p.Status == Project.Active).ToListAsync(ct);
        var open = new List<Guid>();
        foreach (var p in candidates)
        {
            if (ProjectAccess.Holds(await ProjectAccess.RoleAsync(db, p, me.Id, m, ct), ProjectMember.Contributor))
            {
                open.Add(p.Id);
            }
        }
        return open.Count == 0 ? null : await OfRepositoryAsync(db, open, key, ct);
    }

    /// <summary>The chat's project as its page shows it: its id and name, or null.</summary>
    public static async Task<object?> BriefAsync(AppDbContext db, Conversation c, CancellationToken ct) =>
        c.WorkProjectId is { } id ? await db.Projects.AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Id, p.Name, p.Global }).SingleOrDefaultAsync(ct) : null;
}
