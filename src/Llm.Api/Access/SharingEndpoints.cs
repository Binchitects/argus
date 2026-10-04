using System.Security.Claims;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Access;

/// <summary>
/// Whom a person can share with (an assistant, a chat's link): the groups they are in
/// (every group, for an admin), and people found by name.
/// </summary>
public static class SharingEndpoints
{
    public static void MapSharing(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sharing").RequireAuthorization();
        g.MapGet("/groups", GroupsAsync);
        g.MapGet("/people", PeopleAsync);
    }

    private static async Task<IResult> GroupsAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var m = await access.MembershipAsync(me, ct);
        var mine = m.Groups.ToList();
        var query = db.Groups.AsNoTracking();
        if (!m.IsAdmin)
        {
            query = query.Where(g => mine.Contains(g.Id));
        }
        return Results.Ok(await query.OrderBy(g => g.Name).Select(g => new { g.Id, g.Name, mine = mine.Contains(g.Id) }).ToListAsync(ct));
    }

    /// <summary>Up to 20 people whose name, sign-in name or email starts a word with what was typed (two characters at least).</summary>
    private static async Task<IResult> PeopleAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, string? q = null, CancellationToken ct = default)
    {
        var me = (await users.GetUserAsync(p))!;
        var text = (q ?? "").Trim();
        if (text.Length < 2)
        {
            return Results.Ok(Array.Empty<object>());
        }
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var start = escaped + "%";
        var word = "% " + escaped + "%";
        var found = await db.Users.AsNoTracking()
            .Where(u => u.Id != me.Id && !u.IsDisabled &&
                (EF.Functions.ILike(u.DisplayName, start) || EF.Functions.ILike(u.DisplayName, word) || EF.Functions.ILike(u.UserName!, start) || EF.Functions.ILike(u.Email!, start)))
            .OrderBy(u => u.DisplayName).Take(20)
            .Select(u => new { u.Id, name = u.DisplayName == "" ? u.UserName : u.DisplayName, u.UserName }).ToListAsync(ct);
        return Results.Ok(found);
    }
}
