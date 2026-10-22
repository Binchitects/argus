using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A skill made or changed. On a change, only what is sent changes.</summary>
public sealed record SkillRequest(string? Name = null, string? Title = null, string? Description = null, string? Instructions = null, SkillParameter[]? Parameters = null,
    bool? AutoUse = null, SkillSharing? Sharing = null, Guid[]? Groups = null);

/// <summary>
/// Skills (Workspace → Skills, and / in the composer): a person's own, those shared with their groups, the company's
/// (admins make them) and those of the plugins they may use. Only a skill's owner changes it; the company's, any admin;
/// a plugin's, nobody.
/// </summary>
public static class SkillEndpoints
{
    /// <summary>Skills one person may make (shared ones included).</summary>
    public const int PerPerson = 200;

    public static void MapSkills(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/skills").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync).RequirePermission(Llm.Core.Access.Permissions.SkillsCreate);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private sealed record Viewer(AppUser Me, Membership Member, IReadOnlyDictionary<string, ToolSetting> Tools)
    {
        /// <summary>Whether they may use it: their own, their groups', the company's, or a plugin's whose tool they may use.</summary>
        public bool Sees(Skill p) => p switch
        {
            { ServerId: { } server } => Tools.GetValueOrDefault(McpServerTool.Prefix + server) is not { } t || (t.Enabled && Member.MayUse(Core.Access.ResourceKinds.Tool, t.ToolId)),
            { UserId: { } owner } when owner == Me.Id => true,
            { Sharing: SkillSharing.Groups } => p.Groups.Any(Member.Groups.Contains),
            { Sharing: SkillSharing.Company, UserId: null } => true,
            _ => false,
        };

        public bool Edits(Skill p) => p.ServerId is null && (p.UserId == Me.Id || (p.UserId is null && Member.IsAdmin));
    }

    private static async Task<Viewer> ViewerAsync(ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var tools = await db.ToolSettings.AsNoTracking().Where(s => s.ToolId.StartsWith(McpServerTool.Prefix)).ToDictionaryAsync(s => s.ToolId, ct);
        return new Viewer(me, await access.MembershipAsync(me, ct), tools);
    }

    /// <summary>The skills a person may use, theirs first, then their groups', the company's and the plugins'; by slash name within each.</summary>
    internal static async Task<List<Skill>> UsableAsync(AppUser me, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var tools = await db.ToolSettings.AsNoTracking().Where(s => s.ToolId.StartsWith(McpServerTool.Prefix)).ToDictionaryAsync(s => s.ToolId, ct);
        var who = new Viewer(me, await access.MembershipAsync(me, ct), tools);
        return [.. (await db.Skills.AsNoTracking().Where(x => x.UserId == me.Id || x.UserId == null || x.Sharing == SkillSharing.Groups).ToListAsync(ct))
            .Where(who.Sees).OrderBy(x => Rank(x, me.Id)).ThenBy(x => x.Name, StringComparer.Ordinal)];
    }

    /// <summary>The skill a person means by its name: theirs before their groups', the company's and a plugin's of the same name.</summary>
    internal static async Task<Skill?> FindAsync(AppUser me, string name, AccessService access, AppDbContext db, CancellationToken ct) =>
        (await UsableAsync(me, access, db, ct)).FirstOrDefault(s => string.Equals(s.Name, name.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        var me = who.Me.Id;
        var seen = (await db.Skills.AsNoTracking().Where(x => x.UserId == me || x.UserId == null || x.Sharing == SkillSharing.Groups).ToListAsync(ct))
            .Where(who.Sees).ToList();
        var groupIds = seen.SelectMany(x => x.Groups).Concat(who.Member.Groups).ToHashSet();
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id) || who.Member.IsAdmin).OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        var serverIds = seen.Select(x => x.ServerId).OfType<Guid>().ToHashSet();
        var plugins = await db.McpServers.AsNoTracking().Where(s => serverIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var ownerIds = seen.Where(x => x.UserId is { } o && o != me).Select(x => x.UserId!.Value).ToHashSet();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName.Length > 0 ? u.DisplayName : u.UserName!, ct);
        return Results.Ok(new
        {
            isAdmin = who.Member.IsAdmin,
            // The groups they may share with: their own; an admin, any.
            groups = groups.Where(g => who.Member.IsAdmin || who.Member.Groups.Contains(g.Id)),
            skills = seen.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => Rank(x, me)).Select(x => new
            {
                x.Id, x.Name, x.Title, x.Description, x.Instructions, parameters = SkillLibrary.ParametersOf(x), x.AutoUse, x.Sharing,
                groups = groups.Where(g => x.Groups.Contains(g.Id)),
                source = Source(x, me),
                from = x.ServerId is { } s ? plugins.GetValueOrDefault(s) : x.UserId is { } o && o != me ? owners.GetValueOrDefault(o) : null,
                canEdit = who.Edits(x), x.UpdatedAt,
            }),
        });
    }

    private static string Source(Skill p, Guid me) => p.ServerId is not null ? "plugin" : p.UserId == me ? "mine" : p.UserId is null ? "company" : "group";

    /// <summary>With the same name, a person's own first, then their groups', the company's, a plugin's.</summary>
    private static int Rank(Skill p, Guid me) => Source(p, me) switch { "mine" => 0, "group" => 1, "company" => 2, _ => 3 };

    private static async Task<IResult> CreateAsync(SkillRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Instructions))
        {
            return AuthEndpoints.Problem(400, "skill", "A skill needs a slash name, a title and its instructions.");
        }
        if (await db.Skills.CountAsync(x => x.UserId == who.Me.Id, ct) >= PerPerson)
        {
            return AuthEndpoints.Problem(409, "full", $"You have {PerPerson} skills already: delete some first.");
        }
        var skill = new Skill { Name = "", Title = "", Instructions = "", UserId = who.Me.Id };
        if (await ApplyAsync(skill, body, who, db, ct) is { } problem)
        {
            return problem;
        }
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        if (skill.Sharing == SkillSharing.Company)
        {
            await audit.WriteAsync("skill.create", "/" + skill.Name);
        }
        return Results.Created($"/api/skills/{skill.Id}", new { skill.Id, skill.Name });
    }

    private static async Task<IResult> UpdateAsync(Guid id, SkillRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        if (await db.Skills.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } skill || !who.Sees(skill))
        {
            return Results.NotFound();
        }
        if (!who.Edits(skill))
        {
            return Refused(skill);
        }
        var wasCompany = skill.Sharing == SkillSharing.Company;
        if (await ApplyAsync(skill, body, who, db, ct) is { } problem)
        {
            return problem;
        }
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (wasCompany || skill.Sharing == SkillSharing.Company)
        {
            await audit.WriteAsync("skill.update", "/" + skill.Name);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        if (await db.Skills.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } skill || !who.Sees(skill))
        {
            return Results.NotFound();
        }
        if (!who.Edits(skill))
        {
            return Refused(skill);
        }
        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);
        if (skill.Sharing == SkillSharing.Company)
        {
            await audit.WriteAsync("skill.delete", "/" + skill.Name);
        }
        return Results.NoContent();
    }

    private static IResult Refused(Skill skill) => AuthEndpoints.Problem(403, "not_yours", skill.ServerId is not null
        ? "A plugin's skill comes and goes with the plugin."
        : "Only the person who made this skill changes it.");

    /// <summary>Puts what was sent into the skill, checked. The problem, or null.</summary>
    private static async Task<IResult?> ApplyAsync(Skill skill, SkillRequest body, Viewer who, AppDbContext db, CancellationToken ct)
    {
        var name = body.Name?.Trim().TrimStart('/').ToLowerInvariant() ?? skill.Name;
        var title = body.Title?.Trim() ?? skill.Title;
        var description = body.Description?.Trim() ?? skill.Description;
        var instructions = body.Instructions?.Trim() ?? skill.Instructions;
        var parameters = body.Parameters is { } given ? [.. given.Select(x => x with { Name = x.Name?.Trim() ?? "", Description = x.Description?.Trim(), Default = x.Default })]
            : SkillLibrary.Read(skill.Parameters);
        var sharing = body.Sharing ?? skill.Sharing;
        var groups = body.Groups?.Distinct().ToList() ?? (body.Sharing is null ? skill.Groups : []);
        if (SkillLibrary.NameProblem(name) is { } bad)
        {
            return AuthEndpoints.Problem(400, "name", bad);
        }
        if (title.Length is 0 or > SkillLibrary.MaxTitle)
        {
            return AuthEndpoints.Problem(400, "title", $"A title has 1 to {SkillLibrary.MaxTitle} characters.");
        }
        if (description.Length > SkillLibrary.MaxDescription)
        {
            return AuthEndpoints.Problem(400, "description", $"A description has up to {SkillLibrary.MaxDescription} characters.");
        }
        if (instructions.Length is 0 or > SkillLibrary.MaxText)
        {
            return AuthEndpoints.Problem(400, "instructions", $"A skill's instructions have 1 to {SkillLibrary.MaxText:N0} characters.");
        }
        if (SkillLibrary.ParametersProblem(parameters) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "parameters", wrong);
        }
        if (sharing == SkillSharing.Company && !who.Member.IsAdmin)
        {
            return AuthEndpoints.Problem(403, "sharing", "Only admins share skills with the whole company.");
        }
        if (sharing == SkillSharing.Groups)
        {
            if (groups.Count == 0)
            {
                return AuthEndpoints.Problem(400, "groups", "Choose the groups to share it with.");
            }
            var known = await db.Groups.Where(g => groups.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            if (known.Count != groups.Count || (!who.Member.IsAdmin && !groups.All(who.Member.Groups.Contains)))
            {
                return AuthEndpoints.Problem(400, "groups", "You can share it only with groups you are in.");
            }
        }
        else
        {
            groups = [];
        }
        // The company's is nobody's; made personal again, it is the admin's who did it.
        var owner = sharing == SkillSharing.Company ? (Guid?)null : skill.UserId ?? who.Me.Id;
        var clash = owner is { } o
            ? await db.Skills.AnyAsync(x => x.Id != skill.Id && x.UserId == o && x.Name == name, ct)
            : await db.Skills.AnyAsync(x => x.Id != skill.Id && x.UserId == null && x.ServerId == null && x.Name == name, ct);
        if (clash)
        {
            return AuthEndpoints.Problem(409, "name", owner is null ? $"The company has a skill /{name} already." : $"You have a skill /{name} already.");
        }
        (skill.Name, skill.Title, skill.Description, skill.Instructions, skill.Parameters, skill.Sharing, skill.Groups, skill.UserId) =
            (name, title, description, instructions, SkillLibrary.Write(parameters), sharing, groups, owner);
        skill.AutoUse = body.AutoUse ?? skill.AutoUse;
        return null;
    }
}
