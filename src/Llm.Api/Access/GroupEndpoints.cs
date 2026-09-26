using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Ldap;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Access;

public sealed record GroupRequest(string? Name = null, string? Description = null, string? Directory = null);

public sealed record MembersRequest(Guid[] UserIds);

/// <summary>Groups for access rules: app groups whose members are chosen here, and directory groups.</summary>
public static class GroupEndpoints
{
    public static void MapGroups(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/groups").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("/directory", DirectoryAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/members", AddMembersAsync);
        g.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db)
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var counts = await db.GroupMembers.AsNoTracking().GroupBy(m => m.GroupId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        var directoryPeople = groups.Any(x => x.Directory is not null)
            ? await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => u.DirectoryGroups).ToListAsync()
            : [];
        return Results.Ok(groups.Select(x => new
        {
            x.Id, x.Name, x.Description, x.Directory, x.CreatedAt,
            members = x.Directory is { } d ? directoryPeople.Count(m => AccessService.InDirectoryGroup(m, d)) : counts.GetValueOrDefault(x.Id),
        }));
    }

    /// <summary>The groups the directory has put people in, to choose a directory group from.</summary>
    private static async Task<IResult> DirectoryAsync(AppDbContext db)
    {
        var all = await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).Select(u => u.DirectoryGroups).ToListAsync();
        return Results.Ok(all.SelectMany(x => x).GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { dn = x.Key, name = LdapDirectory.CommonName(x.Key), people = x.Count() })
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db)
    {
        if (await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        List<AppUser> people;
        if (group.Directory is { } d)
        {
            var candidates = await db.Users.AsNoTracking().Where(u => u.DirectoryGroups.Count > 0).ToListAsync();
            people = [.. candidates.Where(u => AccessService.InDirectoryGroup(u.DirectoryGroups, d))];
        }
        else
        {
            people = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == id)
                .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (_, u) => u).ToListAsync();
        }
        return Results.Ok(new
        {
            group.Id, group.Name, group.Description, group.Directory, group.CreatedAt,
            members = people.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(u => new { u.Id, u.UserName, u.DisplayName, u.Email, u.IsDisabled }),
        });
    }

    private static async Task<IResult> CreateAsync(GroupRequest body, AppDbContext db, Audit audit)
    {
        var name = (body.Name ?? "").Trim();
        if (await CheckAsync(db, null, name, body.Directory) is { } problem)
        {
            return problem;
        }
        var group = new Group { Name = name, Description = Clean(body.Description), Directory = Clean(body.Directory) };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.create", group.Name, detail: group.Directory is { } d ? $"directory group {d}" : "app group");
        return Results.Created($"/api/admin/groups/{group.Id}", new { group.Id, group.Name });
    }

    private static async Task<IResult> UpdateAsync(Guid id, GroupRequest body, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.Groups.SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        var name = body.Name is null ? group.Name : body.Name.Trim();
        var directory = body.Directory is null ? group.Directory : Clean(body.Directory);
        if ((group.Directory is null) != (directory is null))
        {
            return AuthEndpoints.Problem(400, "kind", "An app group cannot become a directory group, or the other way round. Make a new group.");
        }
        if (await CheckAsync(db, id, name, directory) is { } problem)
        {
            return problem;
        }
        group.Name = name;
        group.Directory = directory;
        if (body.Description is not null)
        {
            group.Description = Clean(body.Description);
        }
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.update", group.Name);
        keys.Wake();
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.Groups.SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        db.Groups.Remove(group);
        await db.SaveChangesAsync();
        await audit.WriteAsync("group.delete", group.Name);
        keys.Wake();
        return Results.NoContent();
    }

    private static async Task<IResult> AddMembersAsync(Guid id, MembersRequest body, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) is not { } group)
        {
            return Results.NotFound();
        }
        if (group.Directory is not null)
        {
            return AuthEndpoints.Problem(400, "directory", "The directory decides who is in a directory group.");
        }
        var ids = (body.UserIds ?? []).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.UserName }).ToListAsync();
        if (people.Count != ids.Count)
        {
            return AuthEndpoints.Problem(400, "person", "Someone in the list does not exist.");
        }
        var already = await db.GroupMembers.Where(m => m.GroupId == id && ids.Contains(m.UserId)).Select(m => m.UserId).ToListAsync();
        foreach (var p in people.Where(p => !already.Contains(p.Id)))
        {
            db.GroupMembers.Add(new GroupMember { GroupId = id, UserId = p.Id });
            await audit.WriteAsync("group.add_member", p.UserName, detail: group.Name);
        }
        await db.SaveChangesAsync();
        keys.Wake();
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(Guid id, Guid userId, AppDbContext db, Audit audit, Models.KeyAccessWatcher keys)
    {
        if (await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == id && m.UserId == userId) is not { } member)
        {
            return Results.NotFound();
        }
        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync();
        var name = await db.Groups.Where(x => x.Id == id).Select(x => x.Name).SingleAsync();
        var person = await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).SingleAsync();
        await audit.WriteAsync("group.remove_member", person, detail: name);
        keys.Wake();
        return Results.NoContent();
    }

    private static async Task<IResult?> CheckAsync(AppDbContext db, Guid? id, string name, string? directory)
    {
        if (name.Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "A group needs a name of up to 100 characters.");
        }
        if (directory is { Length: > 1000 })
        {
            return AuthEndpoints.Problem(400, "directory", "The directory group's name is too long.");
        }
        if (await db.Groups.AnyAsync(x => x.Id != id && EF.Functions.ILike(x.Name, name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal))))
        {
            return AuthEndpoints.Problem(409, "exists", $"There is already a group named {name}.");
        }
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
