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
    bool? AutoUse = null, SkillSharing? Sharing = null, Guid[]? Groups = null, Dictionary<string, string>? Files = null);

/// <summary>
/// Skills (Workspace → Skills, and / in the composer): a person's own, those shared with their groups, the company's
/// (admins make them) and those of the plugins they may use. Only a skill's owner changes it; the company's, any admin;
/// a plugin's, nobody.
/// </summary>
public static partial class SkillEndpoints
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
        g.MapPut("/{id:guid}/off", OffAsync);
        // As files (Claude Code's SKILL.md folders): exported one or many, imported with a preview first.
        g.MapGet("/export", ExportAsync);
        g.MapPost("/import/preview", ImportPreviewAsync).RequirePermission(Llm.Core.Access.Permissions.SkillsCreate).DisableAntiforgery();
        g.MapPost("/import", ImportFilesAsync).RequirePermission(Llm.Core.Access.Permissions.SkillsCreate).DisableAntiforgery();
        // What was learned from the person's work: kept, set aside, or undone.
        g.MapPost("/changes/{id:guid}/apply", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
            await SkillLearning.ApplyAsync(db, (await users.GetUserAsync(p))!.Id, id, ct) ? Results.NoContent() : Results.NotFound());
        g.MapPost("/changes/{id:guid}/dismiss", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
            await SkillLearning.DismissAsync(db, (await users.GetUserAsync(p))!.Id, id, ct) ? Results.NoContent() : Results.NotFound());
        g.MapPost("/changes/{id:guid}/undo", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
            await SkillLearning.UndoAsync(db, (await users.GetUserAsync(p))!.Id, id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private sealed record Viewer(AppUser Me, Membership Member, IReadOnlyDictionary<string, ToolSetting> Tools)
    {
        /// <summary>Whether they may use it: their own, their groups', the company's, or a plugin's whose tool they may use.</summary>
        public bool Sees(Skill p) => p switch
        {
            // A built-in one turned off: only who looks after the company's skills sees it (to turn it on again).
            { BuiltIn: true, Off: true } => Member.Has(Core.Access.Permissions.SkillsCompany),
            { ServerId: { } server } => Tools.GetValueOrDefault(McpServerTool.Prefix + server) is not { } t || (t.Enabled && Member.MayUse(Core.Access.ResourceKinds.Tool, t.ToolId)),
            { UserId: { } owner } when owner == Me.Id => true,
            { Sharing: SkillSharing.Groups } => p.Groups.Any(Member.Groups.Contains) && Member.MayUse(Core.Access.ResourceKinds.Skill, p.Id.ToString()),
            { Sharing: SkillSharing.Company, UserId: null } => Member.MayUse(Core.Access.ResourceKinds.Skill, p.Id.ToString()),
            _ => false,
        };

        public bool Edits(Skill p) => p.ServerId is null && !p.BuiltIn && (p.UserId == Me.Id || (p.UserId is null && Member.Has(Core.Access.Permissions.SkillsCompany)));
    }

    private static async Task<Viewer> ViewerAsync(ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, CancellationToken ct) =>
        await ViewerAsync((await users.GetUserAsync(p))!, access, db, ct);

    private static async Task<Viewer> ViewerAsync(AppUser me, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var tools = await db.ToolSettings.AsNoTracking().Where(s => s.ToolId.StartsWith(McpServerTool.Prefix)).ToDictionaryAsync(s => s.ToolId, ct);
        return new Viewer(me, await access.MembershipAsync(me, ct), tools);
    }

    /// <summary>The skills a person may use, theirs first, then their groups', the company's and the plugins'; by slash name within each.</summary>
    internal static async Task<List<Skill>> UsableAsync(AppUser me, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var who = await ViewerAsync(me, access, db, ct);
        // One learned and not kept yet is not used (the person keeps it on the Skills page).
        return [.. (await db.Skills.AsNoTracking().Where(x => !x.Off && !x.Proposed && (x.UserId == me.Id || x.UserId == null || x.Sharing == SkillSharing.Groups)).ToListAsync(ct))
            .Where(who.Sees).OrderBy(x => Rank(x, me.Id)).ThenBy(x => x.Name, StringComparer.Ordinal)];
    }

    /// <summary>The skill a person means by its name: theirs before their groups', the company's and a plugin's of the same name.</summary>
    /// <summary>
    /// The skills a message uses, in the order chosen, each filled in with what it was given and the words typed: each one the
    /// person may use, none twice, at most <see cref="SkillLibrary.MostPerMessage"/>. The problem, else null.
    /// </summary>
    internal static async Task<(List<SkillCall> Calls, string? Problem)> UsedAsync(AppUser me, IReadOnlyList<SkillUse>? skills, string text, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var calls = new List<SkillCall>();
        var asked = (skills ?? []).Where(s => s.Name is { Length: > 0 }).ToList();
        if (asked.Count > SkillLibrary.MostPerMessage)
        {
            return (calls, $"Use at most {SkillLibrary.MostPerMessage} skills in one message.");
        }
        foreach (var use in asked)
        {
            var name = use.Name!.Trim().TrimStart('/');
            if (calls.Any(c => c.Name == name))
            {
                return (calls, $"/{name} is there twice.");
            }
            if (await FindAsync(me, name, access, db, ct) is not { } found)
            {
                return (calls, $"There is no skill /{name} you may use.");
            }
            var (call, missing) = SkillLibrary.Use(found, use.Args, text);
            if (missing is not null)
            {
                return (calls, missing);
            }
            calls.Add(call!);
        }
        return (calls, null);
    }

    internal static async Task<Skill?> FindAsync(AppUser me, string name, AccessService access, AppDbContext db, CancellationToken ct) =>
        (await UsableAsync(me, access, db, ct)).FirstOrDefault(s => string.Equals(s.Name, name.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        var me = who.Me.Id;
        var seen = (await db.Skills.AsNoTracking().Where(x => x.UserId == me || x.UserId == null || x.Sharing == SkillSharing.Groups).ToListAsync(ct))
            .Where(who.Sees).ToList();
        var groupIds = seen.SelectMany(x => x.Groups).Concat(who.Member.Groups).ToHashSet();
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id) || who.Member.Has(Core.Access.Permissions.SharingGroups)).OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        var serverIds = seen.Select(x => x.ServerId).OfType<Guid>().ToHashSet();
        var plugins = await db.McpServers.AsNoTracking().Where(s => serverIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var ownerIds = seen.Where(x => x.UserId is { } o && o != me).Select(x => x.UserId!.Value).ToHashSet();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName.Length > 0 ? u.DisplayName : u.UserName!, ct);
        // How the person's skills learned and got better: waiting first, then the newest.
        var changes = await db.SkillChanges.AsNoTracking().Where(c => c.UserId == me).OrderByDescending(c => c.Proposed).ThenByDescending(c => c.At).Take(200).ToListAsync(ct);
        return Results.Ok(new
        {
            // What they may do beyond their own: share with the company, with groups (and which), Arena's own skills.
            mayShareCompany = who.Member.Has(Core.Access.Permissions.SharingCompany),
            mayShareGroups = who.Member.Has(Core.Access.Permissions.SkillsShare),
            mayTurnBuiltIn = who.Member.Has(Core.Access.Permissions.SkillsCompany),
            changes = changes.Select(SkillLearning.View),
            // The groups they may share with: their own; with sharing.groups, any.
            groups = groups.Where(g => who.Member.Has(Core.Access.Permissions.SharingGroups) || who.Member.Groups.Contains(g.Id)),
            skills = seen.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => Rank(x, me)).Select(x => new
            {
                x.Id, x.Name, x.Title, x.Description, x.Instructions, parameters = SkillLibrary.ParametersOf(x), x.AutoUse, x.Sharing, x.Off,
                files = SkillTransfer.FilesOf(x).Keys.Order(StringComparer.Ordinal),
                groups = groups.Where(g => x.Groups.Contains(g.Id)),
                source = Source(x, me),
                from = x.ServerId is { } s ? plugins.GetValueOrDefault(s) : x.UserId is { } o && o != me ? owners.GetValueOrDefault(o) : null,
                canEdit = who.Edits(x), x.UpdatedAt, x.Proposed, x.Version, learned = x.Source == Skill.Learned,
            }),
        });
    }

    private static string Source(Skill p, Guid me) =>
        p.ServerId is not null ? "plugin" : p.BuiltIn ? "builtin" : p.UserId == me ? "mine" : p.UserId is null ? "company" : "group";

    /// <summary>With the same name, a person's own first, then their groups', the company's, Arena's own, a plugin's.</summary>
    private static int Rank(Skill p, Guid me) => Source(p, me) switch { "mine" => 0, "group" => 1, "company" => 2, "builtin" => 3, _ => 4 };

    private static async Task<IResult> CreateAsync(SkillRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct)
    {
        var (skill, problem) = await MakeAsync((await users.GetUserAsync(p))!, body, access, db, audit, ct);
        return problem ?? Results.Created($"/api/skills/{skill!.Id}", new { skill.Id, skill.Name });
    }

    /// <summary>A skill the person makes (on the Skills page, or from Code Arena), checked as one changed is: the skill, or why not.</summary>
    internal static async Task<(Skill? Made, IResult? Problem)> MakeAsync(AppUser me, SkillRequest body, AccessService access, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var who = await ViewerAsync(me, access, db, ct);
        if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Instructions))
        {
            return (null, AuthEndpoints.Problem(400, "skill", "A skill needs a slash name, a title and its instructions."));
        }
        if (await db.Skills.CountAsync(x => x.UserId == who.Me.Id, ct) >= PerPerson)
        {
            return (null, AuthEndpoints.Problem(409, "full", $"You have {PerPerson} skills already: delete some first."));
        }
        var skill = new Skill { Name = "", Title = "", Instructions = "", UserId = who.Me.Id };
        if (await ApplyAsync(skill, body, who, db, ct) is { } problem)
        {
            return (null, problem);
        }
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        if (skill.Sharing == SkillSharing.Company)
        {
            await audit.WriteAsync("skill.create", "/" + skill.Name);
        }
        return (skill, null);
    }

    private static async Task<IResult> UpdateAsync(Guid id, SkillRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct) =>
        (await ChangeAsync((await users.GetUserAsync(p))!, id, body, access, db, audit, ct)).Problem ?? Results.NoContent();

    /// <summary>A skill changed by whom may change it (its owner; the company's, an admin): what was sent, checked. The skill, or why not.</summary>
    internal static async Task<(Skill? Changed, IResult? Problem)> ChangeAsync(AppUser me, Guid id, SkillRequest body, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct)
    {
        var who = await ViewerAsync(me, access, db, ct);
        if (await db.Skills.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } skill || !who.Sees(skill))
        {
            return (null, Results.NotFound());
        }
        if (!who.Edits(skill))
        {
            return (null, Refused(skill));
        }
        var wasCompany = skill.Sharing == SkillSharing.Company;
        if (await ApplyAsync(skill, body, who, db, ct) is { } problem)
        {
            return (null, problem);
        }
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (wasCompany || skill.Sharing == SkillSharing.Company)
        {
            await audit.WriteAsync("skill.update", "/" + skill.Name);
        }
        return (skill, null);
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
        : skill.BuiltIn ? "Arena's own skills are written by Arena: turn one off, or make the company's skill of the same name, which wins."
        : "Only the person who made this skill changes it.");

    /// <summary>Whether a skill is off: one of Arena's own, which an admin turns off for everyone (and on again).</summary>
    public sealed record SkillOff(bool Off);

    private static async Task<IResult> OffAsync(Guid id, SkillOff body, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        if (await db.Skills.SingleOrDefaultAsync(x => x.Id == id && x.BuiltIn, ct) is not { } skill || !who.Sees(skill))
        {
            return Results.NotFound();
        }
        if (!who.Member.Has(Core.Access.Permissions.SkillsCompany))
        {
            return AuthEndpoints.Problem(403, "admin", "Turning Arena's own skills off or on needs sharing.skills (Admin → Access).");
        }
        (skill.Off, skill.UpdatedAt) = (body.Off, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(body.Off ? "skill.off" : "skill.on", "/" + skill.Name);
        return Results.NoContent();
    }

    /// <summary>Puts what was sent into the skill, checked. The problem, or null.</summary>
    private static async Task<IResult?> ApplyAsync(Skill skill, SkillRequest body, Viewer who, AppDbContext db, CancellationToken ct)
    {
        var name = body.Name?.Trim().TrimStart('/').ToLowerInvariant() ?? skill.Name;
        var title = body.Title?.Trim() ?? skill.Title;
        var description = body.Description?.Trim() ?? skill.Description;
        var instructions = body.Instructions?.Trim() ?? skill.Instructions;
        var parameters = body.Parameters is { } given ? [.. given.Select(x => x with { Name = x.Name?.Trim() ?? "", Description = x.Description?.Trim(), Default = x.Default })]
            : SkillLibrary.Read(skill.Parameters);
        var files = body.Files ?? SkillTransfer.FilesOf(skill);
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
        if (SkillTransfer.FilesProblem(files) is { } badFiles)
        {
            return AuthEndpoints.Problem(400, "files", badFiles);
        }
        if (sharing == SkillSharing.Company && skill.Sharing != SkillSharing.Company && !who.Member.Has(Core.Access.Permissions.SharingCompany))
        {
            return AuthEndpoints.Problem(403, "sharing", "Sharing a skill with the whole company needs sharing.company (Admin → Access).");
        }
        if (sharing == SkillSharing.Groups && skill.Sharing != SkillSharing.Groups && !who.Member.Has(Core.Access.Permissions.SkillsShare))
        {
            return AuthEndpoints.Problem(403, "sharing", "Sharing skills with groups needs feature.skills.share.");
        }
        if (sharing == SkillSharing.Groups)
        {
            if (groups.Count == 0)
            {
                return AuthEndpoints.Problem(400, "groups", "Choose the groups to share it with.");
            }
            var known = await db.Groups.Where(g => groups.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            if (known.Count != groups.Count || (!who.Member.Has(Core.Access.Permissions.SharingGroups) && !groups.All(who.Member.Groups.Contains)))
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
            : await db.Skills.AnyAsync(x => x.Id != skill.Id && x.UserId == null && x.ServerId == null && !x.BuiltIn && x.Name == name, ct);
        if (clash)
        {
            return AuthEndpoints.Problem(409, "name", owner is null ? $"The company has a skill /{name} already." : $"You have a skill /{name} already.");
        }
        (skill.Name, skill.Title, skill.Description, skill.Instructions, skill.Parameters, skill.Sharing, skill.Groups, skill.UserId) =
            (name, title, description, instructions, SkillLibrary.Write(parameters), sharing, groups, owner);
        skill.AutoUse = body.AutoUse ?? skill.AutoUse;
        skill.Files = SkillTransfer.WriteFiles(files);
        return null;
    }
}
