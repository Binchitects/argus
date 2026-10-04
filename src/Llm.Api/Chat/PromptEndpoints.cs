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

/// <summary>A prompt made or changed. On a change, only what is sent changes.</summary>
public sealed record PromptRequest(string? Name = null, string? Title = null, string? Text = null, PromptSharing? Sharing = null, Guid[]? Groups = null);

/// <summary>
/// The prompt library (Workspace → Prompts, and / in the composer): a person's own prompts,
/// those shared with their groups, the company's (admins make them) and those of the plugins
/// they may use. Only a prompt's owner changes it; the company's, any admin; a plugin's, nobody.
/// </summary>
public static class PromptEndpoints
{
    /// <summary>Prompts one person may make (shared ones included).</summary>
    public const int PerPerson = 200;

    public static void MapPrompts(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/prompts").RequireAuthorization();
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private sealed record Viewer(AppUser Me, Membership Member, IReadOnlyDictionary<string, ToolSetting> Tools)
    {
        /// <summary>Whether they may use it: their own, their groups', the company's, or a plugin's whose tool they may use.</summary>
        public bool Sees(SavedPrompt p) => p switch
        {
            { ServerId: { } server } => Tools.GetValueOrDefault(McpServerTool.Prefix + server) is not { } t || (t.Enabled && Member.May(t.Audience, t.Groups)),
            { UserId: { } owner } when owner == Me.Id => true,
            { Sharing: PromptSharing.Groups } => p.Groups.Any(Member.Groups.Contains),
            { Sharing: PromptSharing.Company, UserId: null } => true,
            _ => false,
        };

        public bool Edits(SavedPrompt p) => p.ServerId is null && (p.UserId == Me.Id || (p.UserId is null && Member.IsAdmin));
    }

    private static async Task<Viewer> ViewerAsync(ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var tools = await db.ToolSettings.AsNoTracking().Where(s => s.ToolId.StartsWith(McpServerTool.Prefix)).ToDictionaryAsync(s => s.ToolId, ct);
        return new Viewer(me, await access.MembershipAsync(me, ct), tools);
    }

    /// <summary>The prompts a person may use, theirs first, then their groups', the company's and the plugins'; by slash name within each.</summary>
    internal static async Task<List<SavedPrompt>> UsableAsync(AppUser me, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var tools = await db.ToolSettings.AsNoTracking().Where(s => s.ToolId.StartsWith(McpServerTool.Prefix)).ToDictionaryAsync(s => s.ToolId, ct);
        var who = new Viewer(me, await access.MembershipAsync(me, ct), tools);
        return [.. (await db.Prompts.AsNoTracking().Where(x => x.UserId == me.Id || x.UserId == null || x.Sharing == PromptSharing.Groups).ToListAsync(ct))
            .Where(who.Sees).OrderBy(x => Rank(x, me.Id)).ThenBy(x => x.Name, StringComparer.Ordinal)];
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        var me = who.Me.Id;
        var seen = (await db.Prompts.AsNoTracking().Where(x => x.UserId == me || x.UserId == null || x.Sharing == PromptSharing.Groups).ToListAsync(ct))
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
            prompts = seen.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => Rank(x, me)).Select(x => new
            {
                x.Id, x.Name, x.Title, x.Text, variables = PromptLibrary.Variables(x.Text), x.Sharing,
                groups = groups.Where(g => x.Groups.Contains(g.Id)),
                source = Source(x, me),
                from = x.ServerId is { } s ? plugins.GetValueOrDefault(s) : x.UserId is { } o && o != me ? owners.GetValueOrDefault(o) : null,
                canEdit = who.Edits(x), x.UpdatedAt,
            }),
        });
    }

    private static string Source(SavedPrompt p, Guid me) => p.ServerId is not null ? "plugin" : p.UserId == me ? "mine" : p.UserId is null ? "company" : "group";

    /// <summary>With the same name, a person's own first, then their groups', the company's, a plugin's.</summary>
    private static int Rank(SavedPrompt p, Guid me) => Source(p, me) switch { "mine" => 0, "group" => 1, "company" => 2, _ => 3 };

    private static async Task<IResult> CreateAsync(PromptRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Text))
        {
            return AuthEndpoints.Problem(400, "prompt", "A prompt needs a slash name, a title and its text.");
        }
        if (await db.Prompts.CountAsync(x => x.UserId == who.Me.Id, ct) >= PerPerson)
        {
            return AuthEndpoints.Problem(409, "full", $"You have {PerPerson} prompts already: delete some first.");
        }
        var prompt = new SavedPrompt { Name = "", Title = "", Text = "", UserId = who.Me.Id };
        if (await ApplyAsync(prompt, body, who, db, ct) is { } problem)
        {
            return problem;
        }
        db.Prompts.Add(prompt);
        await db.SaveChangesAsync(ct);
        if (prompt.Sharing == PromptSharing.Company)
        {
            await audit.WriteAsync("prompt.create", "/" + prompt.Name);
        }
        return Results.Created($"/api/prompts/{prompt.Id}", new { prompt.Id, prompt.Name });
    }

    private static async Task<IResult> UpdateAsync(Guid id, PromptRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit,
        CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        if (await db.Prompts.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } prompt || !who.Sees(prompt))
        {
            return Results.NotFound();
        }
        if (!who.Edits(prompt))
        {
            return Refused(prompt);
        }
        var wasCompany = prompt.Sharing == PromptSharing.Company;
        if (await ApplyAsync(prompt, body, who, db, ct) is { } problem)
        {
            return problem;
        }
        prompt.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (wasCompany || prompt.Sharing == PromptSharing.Company)
        {
            await audit.WriteAsync("prompt.update", "/" + prompt.Name);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var who = await ViewerAsync(p, users, access, db, ct);
        if (await db.Prompts.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } prompt || !who.Sees(prompt))
        {
            return Results.NotFound();
        }
        if (!who.Edits(prompt))
        {
            return Refused(prompt);
        }
        db.Prompts.Remove(prompt);
        await db.SaveChangesAsync(ct);
        if (prompt.Sharing == PromptSharing.Company)
        {
            await audit.WriteAsync("prompt.delete", "/" + prompt.Name);
        }
        return Results.NoContent();
    }

    private static IResult Refused(SavedPrompt prompt) => AuthEndpoints.Problem(403, "not_yours", prompt.ServerId is not null
        ? "A plugin's prompt comes and goes with the plugin."
        : "Only the person who made this prompt changes it.");

    /// <summary>Puts what was sent into the prompt, checked. The problem, or null.</summary>
    private static async Task<IResult?> ApplyAsync(SavedPrompt prompt, PromptRequest body, Viewer who, AppDbContext db, CancellationToken ct)
    {
        var name = body.Name?.Trim().TrimStart('/').ToLowerInvariant() ?? prompt.Name;
        var title = body.Title?.Trim() ?? prompt.Title;
        var text = body.Text?.Trim() ?? prompt.Text;
        var sharing = body.Sharing ?? prompt.Sharing;
        var groups = body.Groups?.Distinct().ToList() ?? (body.Sharing is null ? prompt.Groups : []);
        if (PromptLibrary.NameProblem(name) is { } bad)
        {
            return AuthEndpoints.Problem(400, "name", bad);
        }
        if (title.Length is 0 or > PromptLibrary.MaxTitle)
        {
            return AuthEndpoints.Problem(400, "title", $"A title has 1 to {PromptLibrary.MaxTitle} characters.");
        }
        if (text.Length is 0 or > PromptLibrary.MaxText)
        {
            return AuthEndpoints.Problem(400, "text", $"A prompt's text has 1 to {PromptLibrary.MaxText:N0} characters.");
        }
        if (sharing == PromptSharing.Company && !who.Member.IsAdmin)
        {
            return AuthEndpoints.Problem(403, "sharing", "Only admins share prompts with the whole company.");
        }
        if (sharing == PromptSharing.Groups)
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
        var owner = sharing == PromptSharing.Company ? (Guid?)null : prompt.UserId ?? who.Me.Id;
        var clash = owner is { } o
            ? await db.Prompts.AnyAsync(x => x.Id != prompt.Id && x.UserId == o && x.Name == name, ct)
            : await db.Prompts.AnyAsync(x => x.Id != prompt.Id && x.UserId == null && x.ServerId == null && x.Name == name, ct);
        if (clash)
        {
            return AuthEndpoints.Problem(409, "name", owner is null ? $"The company has a prompt /{name} already." : $"You have a prompt /{name} already.");
        }
        (prompt.Name, prompt.Title, prompt.Text, prompt.Sharing, prompt.Groups, prompt.UserId) = (name, title, text, sharing, groups, owner);
        return null;
    }
}
